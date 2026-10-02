module ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

/// Bind the `IDataObjectStore` contract test pack to the blob-backed
/// `DataObjectStore` over a `LocalFileStorage` substrate. Each factory
/// call creates a fresh temp directory so tests don't share filesystem
/// state. The `(scopeA, scopeB)` pair shares the same physical
/// directory but uses GUID-suffixed container names, exercising the
/// cross-scope-isolation contract end-to-end through the underlying
/// blob storage.
let private contractTests =
    let factory () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-do-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let store = DataObjectStore(blob) :> IDataObjectStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    IDataObjectStoreContract.tests "DataObjectStore (blob-backed)" factory

// ─── Phase 966 — a delete that could not delete says so ───────────────
//
// `IDataObjectStore.Delete`, `Evict` and `IConditionalDataObjectStore.
// DeleteIfVersion` removed an object's version blobs through
// `let! _ = … |> Async.Parallel`. `IBlobStorage.Delete` is idempotent on
// a missing blob, so an `Error` from it is a refusal, and it was dropped:
// the operation answered `Ok` over a version blob still in the container.

/// `Delete` returns `Error` for every blob name `refused` selects; every
/// other operation passes through to `inner`. Carries no conditional-write
/// capability, so `DeleteIfVersion` over it takes the compare-only path.
type DeleteRefusingBlobStorage(inner: IBlobStorage, refused: string -> bool) =
    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)

        member _.Delete(container, blobName) =
            if refused blobName then
                async { return Error "simulated storage delete refusal" }
            else
                inner.Delete(container, blobName)

        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// The same double with the ETag seam forwarded, so `DeleteIfVersion`
/// takes the claim-then-remove path.
type ConditionalDeleteRefusingBlobStorage(inner: InMemoryBlobStorage, refused: string -> bool) =
    inherit DeleteRefusingBlobStorage(inner, refused)

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) =
            (inner :> IConditionalBlobStorage).DownloadWithETag(container, blobName)

        member _.UploadWithETag(container, blobName, content, condition) =
            (inner :> IConditionalBlobStorage).UploadWithETag(container, blobName, content, condition)

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

let private scope = "team-966"
let private objectId = "o1"
let private v1 = "objects/o1/v1.json"
let private v2 = "objects/o1/v2.json"
let private v3 = "objects/o1/v3.json"

let private versionBlobsOf (inner: IBlobStorage) = async {
    let! names = inner.List(scope, "objects/o1/")
    return names |> List.sort
}

let private contentCount (inner: IBlobStorage) = async {
    let! names = inner.List(scope, "objects/_content/")
    return names |> List.filter _.EndsWith(".data") |> List.length
}

/// An object with three versions, each with distinct content, over a
/// healthy store; returns the backing blob store and that store.
let private seeded () = async {
    let inner = InMemoryBlobStorage()
    let healthy = DataObjectStore(inner) :> IDataObjectStore

    for content in [ "content-1"; "content-2"; "content-3" ] do
        match! healthy.Save(scope, objectId, utf8 content, "dt", "u1", Map.empty, Versioned) with
        | Ok _ -> ()
        | Error e -> failtestf "seeding failed: %A" e

    return inner, healthy
}

let private storageFailureNaming (what: string) (expectedBlob: string) (otherBlob: string) (msg: string) =
    Expect.stringContains msg expectedBlob $"{what}: the failure names the version blob that was NOT removed"
    Expect.isFalse (msg.Contains otherBlob) $"{what}: a blob that was removed is not named as a failure"

/// What every one of the three operations owes after a refused delete of
/// `objects/o1/v2.json`: the refused blob and v1 (the blob a re-run finds
/// the object by) are still there, v3 went, the object still reads (as its
/// surviving head), and its surviving content was not reclaimed.
let private expectLeftForARerun (what: string) (inner: IBlobStorage) (healthy: IDataObjectStore) = async {
    let! remaining = versionBlobsOf inner
    Expect.equal remaining [ v1; v2 ] $"{what}: the refused version and v1 stay, the version that went stays gone"

    match! healthy.Get(scope, objectId) with
    | Ok(obj, content) ->
        Expect.equal obj.Version 2 $"{what}: the surviving head reads"

        Expect.equal
            (Encoding.UTF8.GetString content)
            "content-2"
            $"{what}: its content was not reclaimed from under it"
    | Error e -> failtestf "%s: the object must still be readable: %A" what e

    let! contents = contentCount inner

    // v3's content went with its metadata; v1's and v2's metadata survive,
    // so their bytes do too.
    Expect.equal contents 2 $"{what}: only the removed version's content was reclaimed"
}

let private phase966Tests =
    testList "Phase 966 — a refused version-blob delete is reported" [

        testAsync "Delete: one refused version blob of three fails the delete, naming the blob" {
            let! inner, healthy = seeded ()

            let failing =
                DataObjectStore(DeleteRefusingBlobStorage(inner, (=) v2)) :> IDataObjectStore

            match! failing.Delete(scope, objectId) with
            | Ok() -> failtest "a refused version-blob delete must not read as Ok"
            | Error(StorageFailure msg) -> storageFailureNaming "Delete" v2 v3 msg
            | Error other -> failtestf "expected StorageFailure, got %A" other

            do! expectLeftForARerun "Delete" inner healthy

            // The blobs that went stay gone and a re-run over a healthy
            // store finishes the job — content included.
            Expect.isOk (healthy.Delete(scope, objectId) |> Async.RunSynchronously) "the re-run completes"
            let! remaining = versionBlobsOf inner
            Expect.isEmpty remaining "the re-run removed every version"
            let! contents = contentCount inner
            Expect.equal contents 0 "the re-run reclaimed the rest of the content"
        }

        testAsync "Delete: a refused v1 (the last blob) still fails the delete and a re-run finishes it" {
            let! inner, healthy = seeded ()

            let failing =
                DataObjectStore(DeleteRefusingBlobStorage(inner, (=) v1)) :> IDataObjectStore

            match! failing.Delete(scope, objectId) with
            | Ok() -> failtest "a refused version-blob delete must not read as Ok"
            | Error(StorageFailure msg) -> storageFailureNaming "Delete (v1)" v1 v3 msg
            | Error other -> failtestf "expected StorageFailure, got %A" other

            let! remaining = versionBlobsOf inner
            Expect.equal remaining [ v1 ] "v1 is all that is left"

            Expect.isOk (healthy.Delete(scope, objectId) |> Async.RunSynchronously) "the re-run completes"
            let! after = versionBlobsOf inner
            Expect.isEmpty after "the re-run removed it"
        }

        testAsync "Evict: one refused version blob of three fails the eviction, naming the blob" {
            let! inner, healthy = seeded ()

            let failing =
                DataObjectStore(DeleteRefusingBlobStorage(inner, (=) v2)) :> IDataObjectStore

            match! failing.Evict(scope, objectId) with
            | Ok() -> failtest "a refused version-blob delete must not read as Ok"
            | Error(StorageFailure msg) -> storageFailureNaming "Evict" v2 v3 msg
            | Error other -> failtestf "expected StorageFailure, got %A" other

            do! expectLeftForARerun "Evict" inner healthy

            Expect.isOk (healthy.Evict(scope, objectId) |> Async.RunSynchronously) "the re-run completes"
            let! remaining = versionBlobsOf inner
            Expect.isEmpty remaining "the re-run removed every version"
        }

        testAsync "DeleteIfVersion (compare-only path): a refused version blob fails the delete, naming the blob" {
            let! inner, healthy = seeded ()
            let failing = DataObjectStore(DeleteRefusingBlobStorage(inner, (=) v2))

            match! (failing :> IConditionalDataObjectStore).DeleteIfVersion(scope, objectId, 3) with
            | Ok() -> failtest "a refused version-blob delete must not read as Ok"
            | Error(DeleteFailed(StorageFailure msg)) -> storageFailureNaming "DeleteIfVersion" v2 v3 msg
            | Error other -> failtestf "expected DeleteFailed(StorageFailure), got %A" other

            do! expectLeftForARerun "DeleteIfVersion" inner healthy

            // A partial delete moved the head back to the surviving v2, so
            // the re-run states what the store now reports.
            match! (healthy :?> IConditionalDataObjectStore).DeleteIfVersion(scope, objectId, 2) with
            | Ok() -> ()
            | Error e -> failtestf "the re-run over a healthy store must complete: %A" e

            let! remaining = versionBlobsOf inner
            Expect.isEmpty remaining "the re-run removed every version"
        }

        testAsync "DeleteIfVersion (claim path): a refused version blob fails the delete and releases the claim" {
            let! inner, healthy = seeded ()

            let failing = DataObjectStore(ConditionalDeleteRefusingBlobStorage(inner, (=) v2))

            match! (failing :> IConditionalDataObjectStore).DeleteIfVersion(scope, objectId, 3) with
            | Ok() -> failtest "a refused version-blob delete must not read as Ok"
            | Error(DeleteFailed(StorageFailure msg)) -> storageFailureNaming "DeleteIfVersion" v2 v3 msg
            | Error other -> failtestf "expected DeleteFailed(StorageFailure), got %A" other

            // The claim slot (v4) was released on the failure path too.
            do! expectLeftForARerun "DeleteIfVersion (claim)" inner healthy

            match! (healthy :?> IConditionalDataObjectStore).DeleteIfVersion(scope, objectId, 2) with
            | Ok() -> ()
            | Error e -> failtestf "the re-run over a healthy store must complete: %A" e

            let! remaining = versionBlobsOf inner
            Expect.isEmpty remaining "the re-run removed every version"
        }

        testAsync "a delete over a store that refuses nothing is unchanged" {
            let! inner, healthy = seeded ()

            Expect.isOk (healthy.Delete(scope, objectId) |> Async.RunSynchronously) "Delete is Ok"
            let! remaining = versionBlobsOf inner
            Expect.isEmpty remaining "every version is gone"
            let! contents = contentCount inner
            Expect.equal contents 0 "every content blob is reclaimed"
        }
    ]

// ─── Phase 967 — a single-call delete that could not delete says so ───
//
// Two single `Delete` calls in `DataObjectStore` discarded their result
// (`let! _ = blobStorage.Delete(…)`): `SaveIfVersion`'s undo of its own
// just-written `v{N+1}` blob, and `DeleteIfVersion`'s release of the claim
// slot. A refused undo left the write in place while the caller was told
// `VersionConflict`; a refused release left a standing claim while the
// delete answered `Ok`.

/// Answers `Exists` false for every blob `hidden` selects, as the store
/// would after a concurrent `DeleteIfVersion` removed it between the
/// head read and the existence check; everything else, the conditional
/// seam included, passes through to `inner`.
type ExistsHidingBlobStorage(inner: IBlobStorage, hidden: string -> bool) =
    interface IBlobStorage with
        member _.CanComposeFrom = inner.CanComposeFrom
        member _.ComposeFrom(a, b, c) = inner.ComposeFrom(a, b, c)

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)

        member _.Exists(container, blobName) =
            if hidden blobName then
                async { return false }
            else
                inner.Exists(container, blobName)

        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) =
            (inner :?> IConditionalBlobStorage).DownloadWithETag(container, blobName)

        member _.UploadWithETag(container, blobName, content, condition) =
            (inner :?> IConditionalBlobStorage).UploadWithETag(container, blobName, content, condition)

type private WarnRecordingLogger() =
    let warnings = ResizeArray<string>()
    member _.Warnings = lock warnings (fun () -> List.ofSeq warnings)

    interface ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()

        member _.Warn(message: string) =
            lock warnings (fun () -> warnings.Add message)

        member _.Error(_: string, _: exn option) = ()

let private v4 = "objects/o1/v4.json"

let private phase967Tests =
    testList "Phase 967 — a refused single-call delete is reported" [

        testAsync "SaveIfVersion: a refused undo of the claimed slot is not reported as a clean conflict" {
            let inner = InMemoryBlobStorage()
            let healthy = DataObjectStore(inner) :> IDataObjectStore

            match! healthy.Save(scope, objectId, utf8 "content-1", "dt", "u1", Map.empty, Versioned) with
            | Ok _ -> ()
            | Error e -> failtestf "seeding failed: %A" e

            // v1 reads as gone after the slot is claimed, so the save must
            // undo its own v2 — and the store refuses that undo.
            let racing =
                ExistsHidingBlobStorage(ConditionalDeleteRefusingBlobStorage(inner, (=) v2), (=) v1)

            let store = DataObjectStore(racing) :> IConditionalDataObjectStore

            let! result = store.SaveIfVersion(scope, objectId, utf8 "content-2", "dt", "u1", Map.empty, Versioned, 1)

            match result with
            | Error(ConditionalSaveError.VersionConflict _) ->
                failtest "a conflict over a write that is still in the container must not read as a clean conflict"
            | Error(SaveFailed(StorageFailure msg)) ->
                Expect.stringContains msg v2 "the failure names the version blob the undo could not remove"
            | other -> failtestf "expected SaveFailed(StorageFailure), got %A" other

            let! remaining = versionBlobsOf inner
            Expect.equal remaining [ v1; v2 ] "the write really is still in the container"
        }

        testAsync "SaveIfVersion: an undo that succeeds is still a clean VersionConflict (control)" {
            let inner = InMemoryBlobStorage()
            let healthy = DataObjectStore(inner) :> IDataObjectStore

            match! healthy.Save(scope, objectId, utf8 "content-1", "dt", "u1", Map.empty, Versioned) with
            | Ok _ -> ()
            | Error e -> failtestf "seeding failed: %A" e

            let store =
                DataObjectStore(ExistsHidingBlobStorage(inner, (=) v1)) :> IConditionalDataObjectStore

            match! store.SaveIfVersion(scope, objectId, utf8 "content-2", "dt", "u1", Map.empty, Versioned, 1) with
            | Error(ConditionalSaveError.VersionConflict(1, 0)) -> ()
            | other -> failtestf "expected VersionConflict(1, 0), got %A" other

            let! remaining = versionBlobsOf inner
            Expect.equal remaining [ v1 ] "the claimed slot was released"
        }

        testAsync "DeleteIfVersion: a delete whose claim could not be released does not answer Ok" {
            let! inner, _ = seeded ()

            // The claim slot for a delete at head 3 is v4.
            let store = DataObjectStore(ConditionalDeleteRefusingBlobStorage(inner, (=) v4))

            match! (store :> IConditionalDataObjectStore).DeleteIfVersion(scope, objectId, 3) with
            | Ok() -> failtest "a delete that left its claim standing must not read as Ok"
            | Error(DeleteFailed(StorageFailure msg)) ->
                Expect.stringContains msg v4 "the failure names the claim blob that was not released"
            | other -> failtestf "expected DeleteFailed(StorageFailure), got %A" other

            let! remaining = versionBlobsOf inner
            Expect.equal remaining [ v4 ] "the object is gone and the claim is the debris"
        }

        testAsync "DeleteIfVersion: a delete that already failed keeps its own error and logs the stuck claim" {
            let! inner, _ = seeded ()
            let logger = WarnRecordingLogger()

            let store =
                DataObjectStore(
                    ConditionalDeleteRefusingBlobStorage(inner, (fun n -> n = v2 || n = v4)),
                    logger :> ILogger
                )

            match! (store :> IConditionalDataObjectStore).DeleteIfVersion(scope, objectId, 3) with
            | Ok() -> failtest "a refused version-blob delete must not read as Ok"
            | Error(DeleteFailed(StorageFailure msg)) ->
                Expect.stringContains msg v2 "the delete's own failure is the one returned"
                Expect.isFalse (msg.Contains v4) "the claim is not folded into the delete's own error"
            | other -> failtestf "expected DeleteFailed(StorageFailure), got %A" other

            let stuck = logger.Warnings |> List.filter (fun w -> w.Contains v4)
            Expect.equal stuck.Length 1 "the stuck claim is logged once, at Warn, naming the blob"
        }

        testAsync "DeleteIfVersion: a claim that is released leaves nothing behind (control)" {
            let! inner, _ = seeded ()

            let store =
                DataObjectStore(ConditionalDeleteRefusingBlobStorage(inner, (fun _ -> false)))

            match! (store :> IConditionalDataObjectStore).DeleteIfVersion(scope, objectId, 3) with
            | Ok() -> ()
            | other -> failtestf "expected Ok, got %A" other

            let! remaining = versionBlobsOf inner
            Expect.isEmpty remaining "every version and the claim are gone"
        }
    ]

let tests =
    testList "DataObjectStore" [ contractTests; phase966Tests; phase967Tests ]