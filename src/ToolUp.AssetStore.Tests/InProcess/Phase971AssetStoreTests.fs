// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 971 — a refused derivative-status clear fails the derivation job.
///
/// The worker clears the (hash, name) status blob once the derivative is
/// cached. `IBlobStorage.Delete` answers `Ok` on a missing blob, so an
/// `Error` is a refusal — and a kept status is not harmless:
/// `DerivativeJobCoordinator.EnsureQueued` answers any existing status
/// without queueing, so once the cache entry is gone a stale Pending masks
/// the derivative. The job therefore reports the refusal as a retryable
/// failure, publishes no ready notification, and the retry's cache-hit
/// branch re-clears.
module ToolUp.AssetStore.Tests.Phase971AssetStoreTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AssetStore
open ToolUp.AssetStore.Tests.Doubles
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private container = "user-971"

let private profileId = DerivativeProfileId "phase-971-profile"

let private derivativeName = "poster"

/// `Delete` answers `Error` for every blob name `refused` selects while
/// `refusing` is set; every other operation passes through to `inner`.
type private DeleteRefusingBlobStorage(inner: IBlobStorage, refused: string -> bool, refusing: bool ref) =
    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            BlobStorage.composeNotSupported "test double"

        member _.Upload(c, n, content) = inner.Upload(c, n, content)
        member _.Download(c, n) = inner.Download(c, n)

        member _.Delete(c, n) =
            if refusing.Value && refused n then
                async { return Error "simulated storage delete refusal" }
            else
                inner.Delete(c, n)

        member _.List(c, prefix) = inner.List(c, prefix)
        member _.Exists(c, n) = inner.Exists(c, n)
        member _.GetMetadata(c, n) = inner.GetMetadata(c, n)

        member _.DownloadRange(c, n, offset, length) =
            inner.DownloadRange(c, n, offset, length)

        member _.Erase(c, prefix, policy, dryRun) = inner.Erase(c, prefix, policy, dryRun)

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

type private Fixture(refusing: bool ref) =
    let inner = InMemoryBlobStorage() :> IBlobStorage

    let blob =
        DeleteRefusingBlobStorage(inner, (fun n -> n.StartsWith "assets/derivative-status/"), refusing) :> IBlobStorage

    let scheduler = ManualJobScheduler()
    let channel = RecordingNotificationChannel()

    let spec = {
        Name = derivativeName
        AcceptedInputMimes = [ "image/*" ]
        OutputMime = "image/jpeg"
        FileExtension = "jpg"
        RendererKey = "stub-poster"
        Mode = AsyncJob
        Parameters = Map.empty
    }

    let profiles =
        DerivativeProfileRegistry.empty
        |> DerivativeProfileRegistry.registerEntries profileId [ GeneralDerivative spec ]

    let mimeRenderers =
        MimeRendererRegistry.empty
        |> MimeRendererRegistry.register
            "stub-poster"
            (CountingMimeRenderer(System.Text.Encoding.UTF8.GetBytes "poster-bytes"))

    let retry: JobRetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromMilliseconds 1.0
        MaxBackoff = TimeSpan.FromMilliseconds 1.0
        DeadLetterDestination = None
    }

    do
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

    member _.Scheduler = scheduler

    /// Upload, then request the derivative once so the job is queued and
    /// its Pending status is written.
    member _.Enqueue() =
        let record =
            match store.Upload(container, uploadRequest ()) |> Async.RunSynchronously with
            | Ok record -> record
            | Error e -> failtestf "upload failed: %A" e

        match
            store.GetDerivative(container, record.Id, derivativeName)
            |> Async.RunSynchronously
        with
        | Error(AssetDerivativeError.DerivationPending _) -> ()
        | other -> failtestf "expected DerivationPending on first request, got %A" other

        record

    member _.Retrigger() =
        let jobId = scheduler.ScheduledJobIds |> List.exactlyOne

        (scheduler :> IJobScheduler).TriggerOnce(container, jobId, "test")
        |> Async.RunSynchronously
        |> ignore

    member _.StatusPresent(hash: string) =
        match
            inner.Download(container, DerivativeJobs.statusKey hash derivativeName)
            |> Async.RunSynchronously
        with
        | Ok _ -> true
        | Error _ -> false

    member _.ReadyNotifications =
        channel.Published
        |> List.filter (fun (_, notification) ->
            match notification with
            | CustomNotification(key, _) -> key = DerivativeJobs.DerivativeReadyNotificationKey
            | _ -> false)

let tests =
    testList "Phase 971 - AssetStore" [
        testCase
            "a derivation whose status clear is refused does not report Success and publishes no ready notification"
        <| fun () ->
            let fixture = Fixture(ref true)
            let record = fixture.Enqueue()

            match fixture.Scheduler.RunTriggered 1 with
            | [ TransientFailure message ] ->
                Expect.stringContains message "status could not be cleared" "the failure names the kept status"
            | other -> failtestf "expected one TransientFailure, got %A" other

            Expect.isTrue (fixture.StatusPresent record.ContentHash) "the status blob is still at rest"
            Expect.isEmpty fixture.ReadyNotifications "no ready notification while the status stands"

        testCase "the retry re-clears the status through the cache-hit branch, then succeeds and notifies once"
        <| fun () ->
            let refusing = ref true
            let fixture = Fixture refusing
            let record = fixture.Enqueue()

            fixture.Scheduler.RunTriggered 1 |> ignore
            refusing.Value <- false
            fixture.Retrigger()

            Expect.equal (fixture.Scheduler.RunTriggered 2) [ Success ] "the retry succeeds"
            Expect.isFalse (fixture.StatusPresent record.ContentHash) "the retry cleared the status"
            Expect.equal fixture.ReadyNotifications.Length 1 "ready is published once, by the run whose clear landed"
    ]