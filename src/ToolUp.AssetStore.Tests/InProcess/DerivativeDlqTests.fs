// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 207 — the dead-letter + retry-observability surface over the
/// Phase 127 async derivative pipeline.
///
/// The pack is written around one question the acceptance criteria ask
/// twice: does the opt-in change anything for a deployment that did
/// not take it? Every assertion about the new surface therefore has a
/// negative twin on the un-opted-in handler — same poison payload,
/// same exhausted budget, and nothing written, published, or counted
/// beyond what Phase 127 already did.
module ToolUp.AssetStore.Tests.DerivativeDlqTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AssetStore
open ToolUp.AssetStore.AssetCompose
open ToolUp.AssetStore.Tests.Doubles
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private container = "user-dlq"

let private profileId = DerivativeProfileId "dlq-profile"

let private derivativeName = "poster"

let private deadLetterDestination = "assetstore-derivative-dlq"

let private asyncSpec = {
    Name = derivativeName
    AcceptedInputMimes = [ "image/*" ]
    OutputMime = "image/jpeg"
    FileExtension = "jpg"
    RendererKey = "stub-poster"
    Mode = AsyncJob
    Parameters = Map.empty
}

let private uploadRequest () =
    match
        UploadRequest.create
            AssetStoreOptions.defaults
            (System.Text.Encoding.UTF8.GetBytes "original-bytes")
            "fixture.png"
            "image/png"
            "A test image"
            None
            "tester"
            profileId
    with
    | Ok request -> request
    | Error e -> failtestf "fixture upload request invalid: %A" e

/// Async-derivation fixture parameterised on the Phase 207 posture.
/// `observability = None` builds the handler through the Phase 127
/// six-argument constructor — the un-opted-in path, byte-identical to
/// what shipped before this phase.
type private Fixture(mimeRenderer: CountingMimeRenderer, observability: DerivativeObservability option) =
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let scheduler = ManualJobScheduler()
    let channel = RecordingNotificationChannel()

    let profiles =
        DerivativeProfileRegistry.empty
        |> DerivativeProfileRegistry.registerEntries profileId [ GeneralDerivative asyncSpec ]

    let mimeRenderers =
        MimeRendererRegistry.empty
        |> MimeRendererRegistry.register "stub-poster" mimeRenderer

    let retry: JobRetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromMilliseconds 1.0
        MaxBackoff = TimeSpan.FromMilliseconds 1.0
        DeadLetterDestination = Some deadLetterDestination
    }

    let handler =
        match observability with
        | None ->
            DerivativeJobHandler(
                blob,
                profiles,
                mimeRenderers,
                Some(channel :> INotificationChannel),
                nullLogger,
                retry.MaxAttempts
            )
        | Some posture ->
            DerivativeJobHandler(
                blob,
                profiles,
                mimeRenderers,
                Some(channel :> INotificationChannel),
                nullLogger,
                retry.MaxAttempts,
                posture
            )

    do (scheduler :> IJobScheduler).RegisterHandler(DerivativeJobs.HandlerName, handler)

    let store =
        DefaultAssetStore(
            blob,
            CountingImageRenderer(),
            profiles,
            nullAuditLog,
            nullLogger,
            AssetStoreOptions.defaults,
            mimeRenderers,
            Some(DerivativeJobCoordinator(blob, scheduler, retry, nullLogger))
        )
        :> IAssetStore

    member _.Blob = blob
    member _.Scheduler = scheduler
    member _.Channel = channel
    member _.Store = store

    member _.Upload() =
        store.Upload(container, uploadRequest ())
        |> Async.RunSynchronously
        |> function
            | Ok record -> record
            | Error e -> failtestf "upload failed: %A" e

    /// Enqueue the derivation (first request answers Pending) and
    /// return the asset record.
    member this.Enqueue() =
        let record = this.Upload()

        match
            store.GetDerivative(container, record.Id, derivativeName)
            |> Async.RunSynchronously
        with
        | Error(AssetDerivativeError.DerivationPending _) -> ()
        | other -> failtestf "expected DerivationPending on first request, got %A" other

        record

    /// Re-trigger the single scheduled job and run it at `attempt`.
    member _.RunAttempt(attempt: int) : JobResult =
        let jobId = scheduler.ScheduledJobIds |> List.exactlyOne

        (scheduler :> IJobScheduler).TriggerOnce(container, jobId, "test")
        |> Async.RunSynchronously
        |> ignore

        scheduler.RunTriggered attempt |> List.exactlyOne

    member _.DeadLetter(hash: string) =
        DerivativeJobs.readDeadLetter blob container hash derivativeName
        |> Async.RunSynchronously

    /// The raw persisted status blob, so the pack can assert the
    /// terminal `StatusFailed` without the store's request path in
    /// between.
    member _.RawStatus(hash: string) =
        match
            blob.Download(container, DerivativeJobs.statusKey hash derivativeName)
            |> Async.RunSynchronously
        with
        | Ok bytes -> Some(System.Text.Encoding.UTF8.GetString bytes)
        | Error _ -> None

    member _.PayloadsUnder(key: string) =
        channel.Published
        |> List.choose (fun (_, notification) ->
            match notification with
            | CustomNotification(publishedKey, payload) when publishedKey = key -> Some payload
            | _ -> None)

/// A renderer whose failures are retryable, so the bounded budget is
/// what ends the derivation rather than a permanent classification.
/// Output bytes are real, so a test that clears `FailWith` mid-run
/// asserts against a genuinely-servable derivative rather than an
/// empty blob that would satisfy a weaker check.
let private poisonRenderer () =
    CountingMimeRenderer(
        System.Text.Encoding.UTF8.GetBytes "poster-bytes",
        FailWith = Some(DerivativeRenderError.RenderFailed "poster encoder wedged")
    )

let private opted: DerivativeObservability = {
    RecordDeadLetters = true
    NotifyOnFailure = true
    Metrics = None
    DeadLetterDestination = Some deadLetterDestination
}

let tests =
    testList "DerivativeDlq" [
        testCase
            "a poison payload exhausts the budget → StatusFailed + dead-letter record + failure notification + counter"
        <| fun () ->
            let metrics = RecordingMetricsSink()

            let fixture =
                Fixture(
                    poisonRenderer (),
                    Some {
                        opted with
                            Metrics = Some(metrics :> ToolUp.Platform.Metrics.IMetricsSink)
                    }
                )

            let record = fixture.Enqueue()

            // Attempts 1 and 2 are retries: the budget still has room,
            // so nothing terminal is recorded and the leading-indicator
            // counter is what moves.
            for attempt in 1..2 do
                match fixture.Scheduler.RunTriggered attempt |> List.tryExactlyOne with
                | Some(TransientFailure _) -> ()
                | other -> failtestf "attempt %d: expected TransientFailure, got %A" attempt other

                Expect.isNone (fixture.DeadLetter record.ContentHash) "no dead-letter record while retries remain"

                if attempt = 1 then
                    // The first request already dequeued its trigger;
                    // re-trigger for the next attempt.
                    let jobId = fixture.Scheduler.ScheduledJobIds |> List.exactlyOne

                    (fixture.Scheduler :> IJobScheduler).TriggerOnce(container, jobId, "test")
                    |> Async.RunSynchronously
                    |> ignore

            Expect.equal (metrics.CountOf DerivativeJobs.RetryMetric) 2 "one retry counter per non-exhausting attempt"
            Expect.equal (metrics.CountOf DerivativeJobs.FailedMetric) 0 "nothing terminal yet"

            // Attempt 3 exhausts the budget.
            match fixture.RunAttempt 3 with
            | TransientFailure message -> Expect.stringContains message "wedged" "final attempt carries the error"
            | other -> failtestf "expected TransientFailure on the exhausting attempt, got %A" other

            // 1. The terminal status.
            match fixture.RawStatus record.ContentHash with
            | Some json ->
                Expect.stringContains json "StatusFailed" "terminal status persisted"
                Expect.stringContains json "wedged" "error text persisted"
            | None -> failtest "expected a persisted terminal status blob"

            // 2. The dead-letter record, keyed outside the status
            //    prefix so a later status clear cannot erase it.
            match fixture.DeadLetter record.ContentHash with
            | Some dead ->
                Expect.equal dead.ContentHash record.ContentHash "content hash"
                Expect.equal dead.AssetId (AssetId.value record.Id) "asset id"
                Expect.equal dead.DerivativeName derivativeName "derivative name"
                Expect.equal dead.ProfileId (DerivativeProfileId.value profileId) "profile id"
                Expect.equal dead.Attempts 3 "exhausting attempt number"
                Expect.equal dead.Destination (Some deadLetterDestination) "operator destination carried through"
                Expect.stringContains dead.Error "wedged" "final error"
            | None -> failtest "expected a dead-letter record after the budget was exhausted"

            // 3. The failure notification, on its own key.
            match fixture.PayloadsUnder DerivativeJobs.DerivativeFailedNotificationKey with
            | [ payload ] ->
                Expect.stringContains payload record.ContentHash "content hash in the failure payload"
                Expect.stringContains payload "wedged" "error in the failure payload"
                Expect.stringContains payload "DeadLettered" "dead-lettered flag carried"
            | other -> failtestf "expected exactly one failure notification, got %A" other

            // 4. The counter.
            Expect.equal (metrics.CountOf DerivativeJobs.FailedMetric) 1 "one terminal-failure counter"

            Expect.equal
                (metrics.Increments |> List.map fst |> List.distinct |> List.sort)
                (List.sort [ DerivativeJobs.RetryMetric; DerivativeJobs.FailedMetric ])
                "only the two declared counters are emitted"

        testCase "a permanent failure dead-letters on the first attempt without spending the budget"
        <| fun () ->
            let metrics = RecordingMetricsSink()

            let renderer =
                CountingMimeRenderer(Array.empty, FailWith = Some(DecodeFailed "corrupt original"))

            let fixture =
                Fixture(
                    renderer,
                    Some {
                        opted with
                            Metrics = Some(metrics :> ToolUp.Platform.Metrics.IMetricsSink)
                    }
                )

            let record = fixture.Enqueue()

            match fixture.Scheduler.RunTriggered 1 |> List.tryExactlyOne with
            | Some(PermanentFailure message) -> Expect.stringContains message "corrupt original" "typed failure"
            | other -> failtestf "expected PermanentFailure, got %A" other

            match fixture.DeadLetter record.ContentHash with
            | Some dead -> Expect.equal dead.Attempts 1 "dead-lettered on the first attempt"
            | None -> failtest "expected a dead-letter record for a permanent failure"

            Expect.equal (metrics.CountOf DerivativeJobs.RetryMetric) 0 "a permanent failure is not a retry"
            Expect.equal (metrics.CountOf DerivativeJobs.FailedMetric) 1 "one terminal-failure counter"

        testCase "a transient failure that later succeeds resolves to ready with no dead-letter record"
        <| fun () ->
            let metrics = RecordingMetricsSink()
            let renderer = poisonRenderer ()

            let fixture =
                Fixture(
                    renderer,
                    Some {
                        opted with
                            Metrics = Some(metrics :> ToolUp.Platform.Metrics.IMetricsSink)
                    }
                )

            let record = fixture.Enqueue()

            match fixture.Scheduler.RunTriggered 1 |> List.tryExactlyOne with
            | Some(TransientFailure _) -> ()
            | other -> failtestf "expected TransientFailure on the first attempt, got %A" other

            // The upstream recovers before the budget runs out.
            renderer.FailWith <- None

            match fixture.RunAttempt 2 with
            | Success -> ()
            | other -> failtestf "expected Success once the transient fault cleared, got %A" other

            Expect.isNone (fixture.DeadLetter record.ContentHash) "no spurious dead-letter record"
            Expect.isNone (fixture.RawStatus record.ContentHash) "status cleared on completion"

            Expect.isEmpty
                (fixture.PayloadsUnder DerivativeJobs.DerivativeFailedNotificationKey)
                "no failure notification"

            Expect.equal (metrics.CountOf DerivativeJobs.FailedMetric) 0 "no terminal-failure counter"
            Expect.equal (metrics.CountOf DerivativeJobs.RetryMetric) 1 "the one retry that happened is counted"

            // And the derivative is genuinely servable afterwards.
            match
                fixture.Store.GetDerivative(container, record.Id, derivativeName)
                |> Async.RunSynchronously
            with
            | Ok(bytes, _) -> Expect.isNonEmpty bytes "derivative served from the cache"
            | other -> failtestf "expected Ok after recovery, got %A" other

        testCase "GP 11 / GP 13 — without the opt-in an exhausted budget behaves exactly as Phase 127"
        <| fun () ->
            let fixture = Fixture(poisonRenderer (), None)
            let record = fixture.Enqueue()

            fixture.Scheduler.RunTriggered 1 |> ignore
            fixture.RunAttempt 2 |> ignore
            fixture.RunAttempt 3 |> ignore

            // Unchanged Phase 127 behaviour: the terminal status is
            // recorded and the ready channel carries the Failed
            // outcome, so the request path answers typed.
            match fixture.RawStatus record.ContentHash with
            | Some json -> Expect.stringContains json "StatusFailed" "Phase 127 terminal status still recorded"
            | None -> failtest "expected the Phase 127 terminal status blob"

            Expect.isTrue
                (fixture.PayloadsUnder DerivativeJobs.DerivativeReadyNotificationKey
                 |> List.exists (fun payload -> payload.Contains "Failed"))
                "Phase 127 ready-channel failure outcome still published"

            match
                fixture.Store.GetDerivative(container, record.Id, derivativeName)
                |> Async.RunSynchronously
            with
            | Error(AssetDerivativeError.RenderFailed message) ->
                Expect.stringContains message "wedged" "recorded failure surfaces typed"
            | other -> failtestf "expected RenderFailed, got %A" other

            // And none of the Phase 207 surfaces exist.
            Expect.isNone (fixture.DeadLetter record.ContentHash) "no dead-letter record without the opt-in"

            Expect.isEmpty
                (fixture.PayloadsUnder DerivativeJobs.DerivativeFailedNotificationKey)
                "no failure notification without the opt-in"

        testCase "the three surfaces gate independently"
        <| fun () ->
            // A deployment that wants the notification and the counters
            // but not a persisted record gets exactly that — the flags
            // are not a single switch wearing three names.
            let metrics = RecordingMetricsSink()

            let fixture =
                Fixture(
                    poisonRenderer (),
                    Some {
                        opted with
                            RecordDeadLetters = false
                            Metrics = Some(metrics :> ToolUp.Platform.Metrics.IMetricsSink)
                    }
                )

            let record = fixture.Enqueue()
            fixture.Scheduler.RunTriggered 1 |> ignore
            fixture.RunAttempt 2 |> ignore
            fixture.RunAttempt 3 |> ignore

            Expect.isNone (fixture.DeadLetter record.ContentHash) "record suppressed"

            match fixture.PayloadsUnder DerivativeJobs.DerivativeFailedNotificationKey with
            | [ payload ] ->
                Expect.stringContains payload "DeadLettered" "the flag is present"
                Expect.stringContains payload "false" "and reports that nothing was persisted"
            | other -> failtestf "expected the failure notification to survive, got %A" other

            Expect.equal (metrics.CountOf DerivativeJobs.FailedMetric) 1 "counters survive"

            Expect.isNone DerivativeObservability.disabled.Metrics "the shipped disabled posture holds no sink"

        testCase "the compose surface defaults to off and turns everything on when taken"
        <| fun () ->
            let app = AssetStoreServerApp.create ()
            Expect.isNone app.DerivativeDlq "default-off at compose"

            let composed =
                AssetStoreServerApp.withDerivativeDlq DerivativeDlqOptions.defaults app

            match composed.DerivativeDlq with
            | Some options ->
                Expect.isTrue options.RecordDeadLetters "dead-letter records on"
                Expect.isTrue options.NotifyOnFailure "failure notification on"
                Expect.isTrue options.EmitMetrics "counters on"
                Expect.isNone options.RetryPolicy "retry budget inherited from withAsyncDerivation by default"
            | None -> failtest "expected the opt-in to be recorded"

            // Taking the opt-in changes nothing else about the app —
            // in particular it does not imply the async pipeline.
            Expect.isNone composed.AsyncDerivation "withDerivativeDlq does not turn on async derivation"
            Expect.isNone composed.RendererOverride "no other field touched"
            Expect.isNone composed.StoreOverride "no other field touched"

        testCase "the dead-letter key sits outside the status prefix"
        <| fun () ->
            // The two live in sibling prefixes on purpose: a completed
            // derivation clears its status blob, and a record an
            // operator has not swept yet must survive that.
            let statusKey = DerivativeJobs.statusKey "abc123" derivativeName
            let dlqKey = DerivativeJobs.deadLetterKey "abc123" derivativeName

            Expect.notEqual statusKey dlqKey "distinct keys"
            Expect.stringStarts dlqKey "assets/derivative-dlq/" "own prefix"
            Expect.isFalse (dlqKey.StartsWith "assets/derivative-status/") "not under the status prefix"
    ]