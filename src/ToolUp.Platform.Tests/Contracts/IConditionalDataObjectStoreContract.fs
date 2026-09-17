module ToolUp.Platform.Tests.Contracts.IConditionalDataObjectStoreContract

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 753 — IConditionalDataObjectStore contract ────────────────
//
// Parametrised conformance pack for the compare-and-set save on the
// data-object layer. The factory builds the implementation under test
// OVER a blob storage the pack supplies, because the one claim that
// makes the entity-level racing-writers contract hold is only provable
// with a storage the pack controls: two racers that BOTH read head = N —
// provably, because a gate holds each of them after its `List` until the
// other has listed too — see exactly one `Ok` and one `VersionConflict`,
// and the version list afterwards is `[1; 2]`, no gap, no duplicate.
//
// The interleaving is forced, not left to the scheduler, because a race
// test that only sometimes races only sometimes tests anything. State
// the falsifier: disable the `IfAbsent` claim in
// `DataObjectStore.SaveIfVersion` (write the metadata blob
// unconditionally) and the race case goes red with two winners — which
// is exactly how the first draft of this pack was caught parking the
// racers BEFORE their read instead of after it.

let private bytes (s: string) = Encoding.UTF8.GetBytes s

/// Forwarding decorator that HOLDS every `List` result until the test
/// releases the gate, counting arrivals so the test can wait for both
/// racers to be parked holding the same head read. Forwards the ETag
/// seam untouched, so the claim under test is the real one.
type GatedBlobStorage(inner: InMemoryBlobStorage) =
    let gate = TaskCompletionSource<unit>()
    let mutable armed = false
    let mutable arrivals = 0
    let blob = inner :> IBlobStorage
    let cas = inner :> IConditionalBlobStorage

    /// `List` calls parked at the gate so far (only counted once armed).
    member _.Arrivals = arrivals
    /// Start holding `List` results. Seeding happens before this.
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
            // a sequential test wearing a race's name.
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
/// that predates Phase 600 — so an implementation's residual-window
/// path is exercised: the compare must still refuse a stale expectation
/// even where the claim cannot be atomic.
type PlainBlobStorage(inner: InMemoryBlobStorage) =
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

/// Bind an implementation. `factory` builds the store under test over
/// the blob storage the pack hands it; the store MUST claim
/// `IConditionalDataObjectStore` (the first case checks).
let tests (name: string) (factory: IBlobStorage -> IDataObjectStore) =
    testList $"{name} — IConditionalDataObjectStore contract" [

        testCaseAsync
            "claims the capability; a stale expectation is refused with nothing written; a current one assigns expected + 1"
        <| async {
            let store = factory (InMemoryBlobStorage())
            Expect.isTrue (store :? IConditionalDataObjectStore) "the implementation claims the capability"
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
            let store = factory gated
            let s = scope ()

            let! seeded = saveIf store s "o1" "v1" 0
            Expect.isOk seeded "seed v1"

            // Park both racers holding their head read, so each has
            // provably observed head = 1 before either writes.
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
            let store = factory (PlainBlobStorage(InMemoryBlobStorage()))
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

        testCaseAsync "an Unversioned object has no head to compare and is refused, never silently overwritten"
        <| async {
            let store = factory (InMemoryBlobStorage())
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