module ToolUp.Platform.Tests.InProcess.ConditionalDataObjectStoreTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 753 — IConditionalDataObjectStore bindings ────────────────
//
// The contract pack bound to both production implementations: the
// default `DataObjectStore`, and the fact tier's `ReactiveDataObjectStore`
// decorator over it (which must forward the capability rather than hide
// it — a deployment that composes facts must not silently drop to the
// compare-then-save fallback). Plus the one case that is about the PROBE
// rather than an implementation: `ConditionalDataObjectStore.saveIfVersion`
// over a store that never heard of the capability.

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

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

let private saveIf (store: IDataObjectStore) scopeId objectId (content: string) expected =
    ConditionalDataObjectStore.saveIfVersion
        store
        scopeId
        objectId
        (System.Text.Encoding.UTF8.GetBytes(content: string))
        "doc"
        "tester"
        Map.empty
        Versioned
        expected

let tests =
    testList "Phase 753 — IConditionalDataObjectStore" [

        IConditionalDataObjectStoreContract.tests "DataObjectStore (blob-backed)" (fun blob ->
            DataObjectStore(blob) :> IDataObjectStore)

        IConditionalDataObjectStoreContract.tests "ReactiveDataObjectStore over DataObjectStore" (fun blob ->
            ReactiveDataObjectStore(DataObjectStore(blob), (fun () -> false), (fun _ _ -> async.Return()), noopLogger)
            :> IDataObjectStore)

        testCaseAsync "the probe falls back to compare-then-save on a store without the capability"
        <| async {
            let inner = DataObjectStore(InMemoryBlobStorage()) :> IDataObjectStore
            let store = PlainDataObjectStore(inner) :> IDataObjectStore
            Expect.isFalse (store :? IConditionalDataObjectStore) "the decorator hides the capability"
            let s = "team-fallback"

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
    ]