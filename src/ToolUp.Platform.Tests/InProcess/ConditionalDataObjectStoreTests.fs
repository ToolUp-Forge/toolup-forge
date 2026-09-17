module ToolUp.Platform.Tests.InProcess.ConditionalDataObjectStoreTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 753 — IConditionalDataObjectStore ─────────────────────────
//
// The compare-and-set save on the data-object layer, and the one claim
// that makes the entity-level racing-writers contract hold: two racers
// that BOTH read head = N — provably, because a gate holds each of them
// after its `List` until the other has listed too — see exactly one
// `Ok` and one `VersionConflict`, and the version list afterwards is
// `[1; 2]`, no gap, no duplicate. The interleaving is forced, not left
// to the scheduler, because a race test that only sometimes races
// only sometimes tests anything (the campaign's "measure has a floor"
// lesson: state the falsifier — here, remove the `IfAbsent` claim in
// `DataObjectStore.SaveIfVersion` and this test goes red).

let private bytes (s: string) = Encoding.UTF8.GetBytes s

/// Forwarding decorator that HOLDS every `List` result until the test
/// releases the gate, counting arrivals so the test can wait for both
/// racers to be parked holding the same head read. Forwards the ETag
/// seam untouched, so the claim under test is the real one.
type private GatedBlobStorage(inner: InMemoryBlobStorage) =
    let gate = TaskCompletionSource<unit>()
    let mutable armed = false
    let mutable arrivals = 0
    let blob = inner :> IBlobStorage
    let cas = inner :> IConditionalBlobStorage

    /// `List` calls parked at the gate so far (only counted once armed).
    member _.Arrivals = arrivals
    /// Start holding `List` calls. Seeding happens before this.
    member _.Arm() = armed <- true
    /// Let every parked (and every later) `List` through.
    member _.Release() = gate.TrySetResult() |> ignore

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) =
            cas.DownloadWithETag(container, blobName)

        member _.UploadWithETag(container, blobName, content, condition) =
            cas.UploadWithETag(container, blobName, content, condition)

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            blob.Upload(container, blobName, content)

        member _.Download(container, blobName) = blob.Download(container, blobName)
        member _.Delete(container, blobName) = blob.Delete(container, blobName)

        member _.List(container, prefix) = async {
            // List FIRST, then park: what each racer holds while parked
            // is the head as it was before either of them wrote. Parking
            // before the read would let the first racer released run its
            // whole in-memory tail before the second's read even ran —
            // a sequential test wearing a race's name (found by the
            // falsifier: with the `IfAbsent` claim disabled it stayed
            // green).
            let! names = blob.List(container, prefix)

            if armed then
                Interlocked.Increment &arrivals |> ignore
                do! gate.Task |> Async.AwaitTask

            return names
        }

        member _.Exists(container, blobName) = blob.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = blob.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            blob.DownloadRange(container, blobName, offset, length)

        member _.CanComposeFrom = blob.CanComposeFrom

        member _.ComposeFrom(container, target, sources) =
            blob.ComposeFrom(container, target, sources)

        member _.Erase(container, prefix, policy, dryRun) =
            blob.Erase(container, prefix, policy, dryRun)

/// Forwarding decorator that HIDES the ETag seam — an `IBlobStorage`
/// that predates Phase 600 — so the residual-window path in
/// `DataObjectStore.SaveIfVersion` is exercised.
type private PlainBlobStorage(inner: InMemoryBlobStorage) =
    let blob = inner :> IBlobStorage

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            blob.Upload(container, blobName, content)

        member _.Download(container, blobName) = blob.Download(container, blobName)
        member _.Delete(container, blobName) = blob.Delete(container, blobName)
        member _.List(container, prefix) = blob.List(container, prefix)
        member _.Exists(container, blobName) = blob.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = blob.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            blob.DownloadRange(container, blobName, offset, length)

        member _.CanComposeFrom = blob.CanComposeFrom

        member _.ComposeFrom(container, target, sources) =
            blob.ComposeFrom(container, target, sources)

        member _.Erase(container, prefix, policy, dryRun) =
            blob.Erase(container, prefix, policy, dryRun)

/// Forwarding decorator that HIDES the Phase 753 capability — an
/// `IDataObjectStore` implementation that never heard of it — so the
/// `ConditionalDataObjectStore.saveIfVersion` fallback is exercised.
type private PlainDataObjectStore(inner: IDataObjectStore) =
    interface IDataObjectStore with
        member _.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy) =
            inner.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy)

        member _.Get(scopeId, objectId) = inner.Get(scopeId, objectId)

        member _.GetVersion(scopeId, objectId, version) =
            inner.GetVersion(scopeId, objectId, version)

        member _.GetContent(scopeId, contentHash) = inner.GetContent(scopeId, contentHash)
        member _.ListVersions(scopeId, objectId) = inner.ListVersions(scopeId, objectId)
        member _.ListObjects scopeId = inner.ListObjects scopeId

        member _.Recover(scopeId, objectId, version, createdBy) =
            inner.Recover(scopeId, objectId, version, createdBy)

        member _.Delete(scopeId, objectId) = inner.Delete(scopeId, objectId)
        member _.Evict(scopeId, objectId) = inner.Evict(scopeId, objectId)
        member _.Purge scopeId = inner.Purge scopeId

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

let private scope () =
    "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

let private saveIf (store: IDataObjectStore) scopeId objectId (content: string) expected =
    ConditionalDataObjectStore.saveIfVersion
        store
        scopeId
        objectId
        (bytes content)
        "doc"
        "tester"
        Map.empty
        Versioned
        expected

let private versionsOf (store: IDataObjectStore) scopeId objectId = async {
    let! vs = store.ListVersions(scopeId, objectId)
    return vs |> List.map _.Version
}

let tests =
    testList "Phase 753 — IConditionalDataObjectStore" [

        testCaseAsync
            "DataObjectStore implements the capability, and a stale expectation is refused with nothing written"
        <| async {
            let store = DataObjectStore(InMemoryBlobStorage()) :> IDataObjectStore
            Expect.isTrue (store :? IConditionalDataObjectStore) "the default store claims the capability"
            let s = scope ()

            let! first = saveIf store s "o1" "v1" 0

            match first with
            | Ok d -> Expect.equal d.Version 1 "create-only save assigns version 1"
            | Error e -> failtestf "create-only save should succeed, got %A" e

            let! stale = saveIf store s "o1" "v2-stale" 0

            match stale with
            | Error(ConditionalSaveError.VersionConflict(expected, actual)) ->
                Expect.equal expected 0 "names the stated expectation"
                Expect.equal actual 1 "names the head it observed"
            | other -> failtestf "expected VersionConflict, got %A" other

            let! versions = versionsOf store s "o1"
            Expect.equal versions [ 1 ] "the refused save consumed no version"

            let! current = saveIf store s "o1" "v2" 1

            match current with
            | Ok d -> Expect.equal d.Version 2 "a current expectation assigns expected + 1"
            | Error e -> failtestf "current expectation should succeed, got %A" e
        }

        testCaseAsync
            "two racers that both read head = 1 — exactly one wins the v2 slot, the loser is told, and versions are [1; 2]"
        <| async {
            let gated = GatedBlobStorage(InMemoryBlobStorage())
            let store = DataObjectStore(gated) :> IDataObjectStore
            let s = scope ()

            let! seeded = saveIf store s "o1" "v1" 0
            Expect.isOk seeded "seed v1"

            // Park both racers at their head read, so each has provably
            // observed head = 1 before either writes.
            gated.Arm()
            let racerA = saveIf store s "o1" "from-A" 1 |> Async.StartAsTask
            let racerB = saveIf store s "o1" "from-B" 1 |> Async.StartAsTask

            let deadline = DateTime.UtcNow.AddSeconds 10.0

            while gated.Arrivals < 2 && DateTime.UtcNow < deadline do
                do! Async.Sleep 5

            Expect.equal gated.Arrivals 2 "both racers reached the head read before the gate opened"
            gated.Release()

            let! a = racerA |> Async.AwaitTask
            let! b = racerB |> Async.AwaitTask

            let outcomes = [ a; b ]

            let wins =
                outcomes
                |> List.choose (function
                    | Ok d -> Some d
                    | Error _ -> None)

            let conflicts =
                outcomes
                |> List.choose (function
                    | Error(ConditionalSaveError.VersionConflict(e, act)) -> Some(e, act)
                    | _ -> None)

            Expect.hasLength wins 1 "exactly one racer wins"
            Expect.equal wins.Head.Version 2 "the winner took v2"

            Expect.equal
                conflicts
                [ (1, 2) ]
                "the loser is told expected 1, actual 2 — a typed refusal, never a silent overwrite"

            let! versions = versionsOf store s "o1"
            Expect.equal versions [ 1; 2 ] "no version skipped, none duplicated"

            // And the winner's bytes are what v2 holds — the loser's
            // metadata did not overwrite them.
            let! head = store.Get(s, "o1")

            match head, wins.Head.ContentHash with
            | Ok(d, _), winnerHash -> Expect.equal d.ContentHash winnerHash "v2 is the winner's write"
            | Error e, _ -> failtestf "head read failed: %A" e
        }

        testCaseAsync "over a blob store without the ETag seam the compare still refuses a stale expectation"
        <| async {
            let store =
                DataObjectStore(PlainBlobStorage(InMemoryBlobStorage())) :> IDataObjectStore

            let s = scope ()
            let! _ = saveIf store s "o1" "v1" 0
            let! _ = saveIf store s "o1" "v2" 1

            let! stale = saveIf store s "o1" "stale" 1

            match stale with
            | Error(ConditionalSaveError.VersionConflict(1, 2)) -> ()
            | other -> failtestf "expected VersionConflict(1, 2), got %A" other

            let! versions = versionsOf store s "o1"
            Expect.equal versions [ 1; 2 ] "nothing written on the refused path"
        }

        testCaseAsync "the probe falls back to compare-then-save on a store without the capability"
        <| async {
            let inner = DataObjectStore(InMemoryBlobStorage()) :> IDataObjectStore
            let store = PlainDataObjectStore(inner) :> IDataObjectStore
            Expect.isFalse (store :? IConditionalDataObjectStore) "the decorator hides the capability"
            let s = scope ()

            let! first = saveIf store s "o1" "v1" 0
            Expect.isOk first "fallback create succeeds"

            let! stale = saveIf store s "o1" "stale" 0

            match stale with
            | Error(ConditionalSaveError.VersionConflict(0, 1)) -> ()
            | other -> failtestf "expected VersionConflict(0, 1), got %A" other

            let! current = saveIf store s "o1" "v2" 1

            match current with
            | Ok d -> Expect.equal d.Version 2 "fallback assigns expected + 1"
            | Error e -> failtestf "fallback current save should succeed, got %A" e
        }

        testCaseAsync "an Unversioned object has no head to compare and is refused, never silently overwritten"
        <| async {
            let store = DataObjectStore(InMemoryBlobStorage()) :> IDataObjectStore
            let s = scope ()

            let! result =
                ConditionalDataObjectStore.saveIfVersion store s "o1" (bytes "x") "doc" "tester" Map.empty Unversioned 0

            match result with
            | Error(SaveFailed(StorageFailure msg)) ->
                Expect.stringContains msg "Unversioned" "the refusal names the policy"
            | other -> failtestf "expected SaveFailed, got %A" other

            let! versions = versionsOf store s "o1"
            Expect.isEmpty versions "nothing written"
        }
    ]