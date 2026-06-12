// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AssetStore.Tests.DerivativePipelineTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AssetStore
open ToolUp.AssetStore.Tests.Doubles
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private container = "user-test"

let private uploadRequest (profile: DerivativeProfileId) =
    match
        UploadRequest.create
            AssetStoreOptions.defaults
            (System.Text.Encoding.UTF8.GetBytes "original-bytes")
            "fixture.png"
            "image/png"
            "A test image"
            None
            "tester"
            profile
    with
    | Ok request -> request
    | Error e -> failtestf "fixture upload request invalid: %A" e

let private generalSpec mode = {
    Name = "poster"
    AcceptedInputMimes = [ "image/*" ]
    OutputMime = "image/jpeg"
    FileExtension = "jpg"
    RendererKey = "stub-poster"
    Mode = mode
    Parameters = Map.empty
}

let private profileId = DerivativeProfileId "test-profile"

/// One fully-wired store + scheduler + channel fixture.
type private Fixture(entries: ProfileEntry list, ?withAsync: bool, ?mimeRenderer: CountingMimeRenderer) =
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let imageRenderer = CountingImageRenderer()

    let mimeRenderer =
        mimeRenderer
        |> Option.defaultWith (fun () -> CountingMimeRenderer(System.Text.Encoding.UTF8.GetBytes "poster-bytes"))

    let scheduler = ManualJobScheduler()
    let channel = RecordingNotificationChannel()

    let profiles =
        DerivativeProfileRegistry.empty
        |> DerivativeProfileRegistry.registerEntries profileId entries

    let mimeRenderers =
        MimeRendererRegistry.empty
        |> MimeRendererRegistry.register "stub-poster" mimeRenderer

    let retry: JobRetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromMilliseconds 1.0
        MaxBackoff = TimeSpan.FromMilliseconds 1.0
        DeadLetterDestination = None
    }

    let coordinator =
        if defaultArg withAsync false then
            (scheduler :> IJobScheduler)
                .RegisterHandler(
                    DerivativeJobs.HandlerName,
                    DerivativeJobHandler(
                        blob,
                        profiles,
                        mimeRenderers,
                        Some(channel :> INotificationChannel),
                        nullLogger,
                        retry.MaxAttempts
                    )
                )

            Some(DerivativeJobCoordinator(blob, scheduler, retry, nullLogger))
        else
            None

    let store =
        DefaultAssetStore(
            blob,
            imageRenderer,
            profiles,
            nullAuditLog,
            nullLogger,
            AssetStoreOptions.defaults,
            mimeRenderers,
            coordinator
        )
        :> IAssetStore

    member _.Store = store
    member _.Blob = blob
    member _.ImageRenderer = imageRenderer
    member _.MimeRenderer = mimeRenderer
    member _.Scheduler = scheduler
    member _.Channel = channel

    member _.Upload() =
        store.Upload(container, uploadRequest profileId)
        |> Async.RunSynchronously
        |> function
            | Ok record -> record
            | Error e -> failtestf "upload failed: %A" e

let private getDerivative (fixture: Fixture) (record: AssetRecord) (name: string) =
    fixture.Store.GetDerivative(container, record.Id, name)
    |> Async.RunSynchronously

let tests =
    testList "DerivativePipeline" [
        testCase "GP 11 — image profiles behave exactly as before through the old constructor shape"
        <| fun () ->
            // The pre-Phase-127 constructor + DerivativeSpec-shaped
            // registration: render on miss, cache at the same path,
            // serve from cache thereafter.
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let imageRenderer = CountingImageRenderer()

            let profiles =
                DerivativeProfileRegistry.empty
                |> DerivativeProfileRegistry.register profileId [
                    {
                        Name = "thumbnail"
                        MaxWidth = Some 240
                        MaxHeight = Some 240
                        Format = Jpeg
                        Quality = 78
                    }
                ]

            let store =
                DefaultAssetStore(blob, imageRenderer, profiles, nullAuditLog, nullLogger, AssetStoreOptions.defaults)
                :> IAssetStore

            let record =
                store.Upload(container, uploadRequest profileId)
                |> Async.RunSynchronously
                |> function
                    | Ok r -> r
                    | Error e -> failtestf "upload failed: %A" e

            match store.GetDerivative(container, record.Id, "thumbnail") |> Async.RunSynchronously with
            | Ok(bytes, mime) ->
                Expect.equal (System.Text.Encoding.UTF8.GetString bytes) "image:thumbnail" "rendered bytes"
                Expect.equal mime "image/jpeg" "image mime"
            | Error e -> failtestf "expected Ok, got %A" e

            // Cache slot at the unchanged path.
            let cacheKey = sprintf "assets/derivatives/%s/thumbnail.jpg" record.ContentHash

            match blob.Download(container, cacheKey) |> Async.RunSynchronously with
            | Ok _ -> ()
            | Error e -> failtestf "expected cached derivative at the pre-127 path, got %s" e

            // Second request serves the cache — renderer not re-invoked.
            store.GetDerivative(container, record.Id, "thumbnail")
            |> Async.RunSynchronously
            |> ignore

            Expect.equal imageRenderer.RenderCount 1 "single render across two requests"

        testCase "general sync entries dispatch by MIME through the renderer registry"
        <| fun () ->
            let fixture = Fixture [ GeneralDerivative(generalSpec Sync) ]
            let record = fixture.Upload()

            match getDerivative fixture record "poster" with
            | Ok(bytes, mime) ->
                Expect.equal (System.Text.Encoding.UTF8.GetString bytes) "poster-bytes" "mime renderer output"
                Expect.equal mime "image/jpeg" "spec output mime"
            | Error e -> failtestf "expected Ok, got %A" e

            // Cached under the spec's extension; second call is a hit.
            getDerivative fixture record "poster" |> ignore
            Expect.equal fixture.MimeRenderer.RenderCount 1 "single render across two requests"
            Expect.equal fixture.ImageRenderer.RenderCount 0 "image renderer untouched"

        testCase "input-MIME mismatch fails typed without reaching the renderer"
        <| fun () ->
            let spec = {
                generalSpec Sync with
                    AcceptedInputMimes = [ "video/mp4" ]
            }

            let fixture = Fixture [ GeneralDerivative spec ]
            let record = fixture.Upload()

            match getDerivative fixture record "poster" with
            | Error(AssetDerivativeError.RenderFailed message) ->
                Expect.stringContains message "does not accept input MIME" "typed mismatch"
            | other -> failtestf "expected RenderFailed, got %A" other

            Expect.equal fixture.MimeRenderer.RenderCount 0 "renderer never invoked"

        testCase "GP 13 — async entries without the opt-in fail typed and never touch the scheduler"
        <| fun () ->
            let fixture = Fixture [ GeneralDerivative(generalSpec AsyncJob) ]
            let record = fixture.Upload()

            match getDerivative fixture record "poster" with
            | Error(AssetDerivativeError.RenderFailed message) ->
                Expect.stringContains message "withAsyncDerivation" "typed opt-in pointer"
            | other -> failtestf "expected RenderFailed, got %A" other

            Expect.equal fixture.Scheduler.ScheduleCalls 0 "no scheduler traffic"
            Expect.isEmpty fixture.Channel.Published "no channel traffic"

        testCase "async happy path: pending → job run → notification → instant cache hit"
        <| fun () ->
            let fixture = Fixture([ GeneralDerivative(generalSpec AsyncJob) ], withAsync = true)
            let record = fixture.Upload()

            // First request: enqueued, typed pending.
            let correlationId =
                match getDerivative fixture record "poster" with
                | Error(AssetDerivativeError.DerivationPending id) -> id
                | other -> failtestf "expected DerivationPending, got %A" other

            Expect.isNotEmpty correlationId "correlation id carried"

            // Run the background job.
            let results = fixture.Scheduler.RunTriggered 1
            Expect.equal results [ Success ] "job succeeded"

            // Completion published on the scope.
            match fixture.Channel.Published with
            | [ (scope, CustomNotification(key, payload)) ] ->
                Expect.equal scope container "scope-keyed"
                Expect.equal key DerivativeJobs.DerivativeReadyNotificationKey "notification key"
                Expect.stringContains payload "Ready" "ready outcome"
                Expect.stringContains payload record.ContentHash "content hash in payload"
            | other -> failtestf "expected one ready notification, got %A" other

            // Thereafter: the unchanged instant hit path.
            match getDerivative fixture record "poster" with
            | Ok(bytes, _) ->
                Expect.equal (System.Text.Encoding.UTF8.GetString bytes) "poster-bytes" "served from cache"
            | other -> failtestf "expected Ok after completion, got %A" other

            Expect.equal fixture.MimeRenderer.RenderCount 1 "derived exactly once"

        testCase "concurrent requests coalesce into one derivation"
        <| fun () ->
            let fixture = Fixture([ GeneralDerivative(generalSpec AsyncJob) ], withAsync = true)
            let record = fixture.Upload()

            // Two callers race the same uncached derivative.
            let outcomes =
                [ 1..2 ]
                |> List.map (fun _ -> async { return! fixture.Store.GetDerivative(container, record.Id, "poster") })
                |> Async.Parallel
                |> Async.RunSynchronously

            for outcome in outcomes do
                match outcome with
                | Error(AssetDerivativeError.DerivationPending _) -> ()
                | other -> failtestf "expected DerivationPending, got %A" other

            Expect.equal fixture.Scheduler.ScheduledJobs.Length 1 "one job scheduled"

            fixture.Scheduler.RunTriggered 1 |> ignore
            Expect.equal fixture.MimeRenderer.RenderCount 1 "derived exactly once"

        testCase "a re-run job is an idempotent no-op (stateless-handler rule)"
        <| fun () ->
            let fixture = Fixture([ GeneralDerivative(generalSpec AsyncJob) ], withAsync = true)
            let record = fixture.Upload()

            getDerivative fixture record "poster" |> ignore
            Expect.equal (fixture.Scheduler.RunTriggered 1) [ Success ] "first run renders"

            // Simulate a killed/restarted scheduler replaying the
            // run: trigger the same persisted job again.
            let jobId = fixture.Scheduler.ScheduledJobIds |> List.exactlyOne

            (fixture.Scheduler :> IJobScheduler).TriggerOnce(container, jobId, "test")
            |> Async.RunSynchronously
            |> ignore

            Expect.equal (fixture.Scheduler.RunTriggered 2) [ Success ] "replay still succeeds"
            Expect.equal fixture.MimeRenderer.RenderCount 1 "cache re-check makes the replay a no-op"

        testCase "failure surfaces typed and does not re-enqueue forever"
        <| fun () ->
            let failingRenderer =
                CountingMimeRenderer(Array.empty, FailWith = Some(DecodeFailed "corrupt original"))

            let fixture =
                Fixture([ GeneralDerivative(generalSpec AsyncJob) ], withAsync = true, mimeRenderer = failingRenderer)

            let record = fixture.Upload()

            // Enqueue + run: decode failures are terminal, so the
            // handler records the failure and dead-letters.
            getDerivative fixture record "poster" |> ignore

            match fixture.Scheduler.RunTriggered 1 with
            | [ PermanentFailure message ] -> Expect.stringContains message "corrupt original" "typed failure"
            | other -> failtestf "expected PermanentFailure, got %A" other

            // Failure notification published.
            Expect.isTrue
                (fixture.Channel.Published
                 |> List.exists (fun (_, n) ->
                     match n with
                     | CustomNotification(_, payload) -> payload.Contains "Failed"
                     | _ -> false))
                "failure notification published"

            // Subsequent requests see the recorded failure — typed
            // RenderFailed, no fresh enqueue.
            let schedulesBefore = fixture.Scheduler.ScheduleCalls

            match getDerivative fixture record "poster" with
            | Error(AssetDerivativeError.RenderFailed message) ->
                Expect.stringContains message "corrupt original" "recorded failure surfaces"
            | other -> failtestf "expected RenderFailed, got %A" other

            Expect.equal fixture.Scheduler.ScheduleCalls schedulesBefore "no re-enqueue after recorded failure"

        testCase "transient failures keep answering pending until attempts are exhausted"
        <| fun () ->
            let failingRenderer =
                CountingMimeRenderer(Array.empty, FailWith = Some(DerivativeRenderError.RenderFailed "ffmpeg busy"))

            let fixture =
                Fixture([ GeneralDerivative(generalSpec AsyncJob) ], withAsync = true, mimeRenderer = failingRenderer)

            let record = fixture.Upload()
            getDerivative fixture record "poster" |> ignore

            // Attempt 1 of 3: transient — failure NOT yet recorded,
            // requests still see Pending.
            match fixture.Scheduler.RunTriggered 1 with
            | [ TransientFailure _ ] -> ()
            | other -> failtestf "expected TransientFailure, got %A" other

            match getDerivative fixture record "poster" with
            | Error(AssetDerivativeError.DerivationPending _) -> ()
            | other -> failtestf "expected still-pending after a retryable attempt, got %A" other

            // Final attempt: failure recorded; requests see it typed.
            let jobId = fixture.Scheduler.ScheduledJobIds |> List.exactlyOne

            (fixture.Scheduler :> IJobScheduler).TriggerOnce(container, jobId, "test")
            |> Async.RunSynchronously
            |> ignore

            fixture.Scheduler.RunTriggered 3 |> ignore

            match getDerivative fixture record "poster" with
            | Error(AssetDerivativeError.RenderFailed message) ->
                Expect.stringContains message "ffmpeg busy" "exhausted failure surfaces"
            | other -> failtestf "expected RenderFailed after exhausted retries, got %A" other
    ]