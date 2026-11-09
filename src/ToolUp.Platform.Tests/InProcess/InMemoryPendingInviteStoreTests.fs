module ToolUp.Platform.Tests.InProcess.InMemoryPendingInviteStoreTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Scope.Teams
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── InMemoryPendingInviteStore contract binding ─────────────────────
//
// Runs the `IPendingInviteStoreContract` pack — the interface's behaviour and
// the document's (bytes at rest, read failure, quarantine) — against the
// single-instance store, each case over a fresh `InMemoryBlobStorage`. The
// store holds no process-wide state, so the cases run in parallel.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let tests =
    let factory () =
        let storage = InMemoryBlobStorage() :> IBlobStorage
        InMemoryPendingInviteStore(storage, silentLogger) :> IPendingInviteStore

    testList "InMemoryPendingInviteStore" [
        IPendingInviteStoreContract.tests "InMemoryPendingInviteStore" factory
        IPendingInviteStoreContract.documentTests
            "InMemoryPendingInviteStore"
            (fun () -> InMemoryBlobStorage() :> IBlobStorage)
            (fun storage -> InMemoryPendingInviteStore(storage, silentLogger) :> IPendingInviteStore)
    ]