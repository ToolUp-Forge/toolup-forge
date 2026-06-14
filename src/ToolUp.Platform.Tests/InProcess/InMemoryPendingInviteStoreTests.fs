module ToolUp.Platform.Tests.InProcess.InMemoryPendingInviteStoreTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Teams
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.Support

// ─── Phase 5h — InMemoryPendingInviteStore contract binding ──────────
//
// Runs the `IPendingInviteStoreContract` pack against the in-process
// single-instance default. The InMemory impl holds module-level
// `writeLock` + `cache` state (the 30-second TTL read cache from Phase
// 3d / Cluster A1); wrap the binding in `testSequenced` so sibling tests
// don't interleave on the shared cache. Each test's `factory ()` call
// resets the cache via `CacheReset.invalidateAll` and pairs the impl
// with a fresh `InMemoryBlobStorage`, so on-disk state is per-test even
// though the in-memory cache machinery is process-wide.
//
// The future `BlobPendingInviteStore` companion binds to the same pack
// from its own binding file (against a unique blob path per test) once
// Phase 9c half-2's `IBlobStorage.UploadWithETag` substrate ships.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let tests =
    let factory () =
        // Drop the module-level cache so the previous test's reads
        // can't bleed into this test's view of a fresh blob.
        CacheReset.invalidateAll () |> Async.RunSynchronously

        let storage = InMemoryBlobStorage() :> IBlobStorage
        InMemoryPendingInviteStore(storage, silentLogger) :> IPendingInviteStore

    testSequenced (IPendingInviteStoreContract.tests "InMemoryPendingInviteStore" factory)