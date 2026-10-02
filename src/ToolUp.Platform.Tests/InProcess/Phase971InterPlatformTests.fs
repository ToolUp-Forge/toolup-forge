module ToolUp.Platform.Tests.InProcess.Phase971InterPlatformTests

open System
open Expecto
open ToolUp.InterPlatform
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — InterPlatform: a refused delete that is the operation ─
//
// `IRoundStateStore.Clear` is `Async<unit>`, and the delete IS the clear:
// a refusal returning normally would claim a run's state is gone while it
// is still there for the next `RunRounds` to resume from. The store
// raises instead, mirroring its own `Save`.

let private scope = "team-971"
let private runId = "run-971"

let private stateFor (id: string) = {
    RunId = id
    RoundNumber = 1
    Participants = [ { PeerId = "a"; DisplayName = "a" } ]
    Statuses = Map.empty
    Accumulated = "1"
    UpdatedAt = DateTimeOffset.UnixEpoch
}

let tests =
    testList "Phase 971 - InterPlatform" [
        testCaseAsync "a refused delete fails BlobRoundStateStore.Clear and the state stays readable"
        <| async {
            let blobs =
                DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n.EndsWith(sprintf "/%s.json" runId))
                :> IBlobStorage

            let store = BlobRoundStateStore(blobs) :> IRoundStateStore
            do! store.Save(scope, stateFor runId)

            let! cleared = store.Clear(scope, runId) |> Async.Catch

            match cleared with
            | Choice1Of2() -> failtest "a refused delete must not read as a cleared run"
            | Choice2Of2 ex ->
                Expect.stringContains ex.Message runId "the failure names the run whose state is still at rest"

            let! reloaded = store.TryLoad(scope, runId)
            Expect.isSome reloaded "the state is still there for a resume or a re-run of Clear"
        }

        testCaseAsync "control: Clear over a healthy store reclaims the state"
        <| async {
            let blobs =
                DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun _ -> false) :> IBlobStorage

            let store = BlobRoundStateStore(blobs) :> IRoundStateStore
            do! store.Save(scope, stateFor runId)
            do! store.Clear(scope, runId)
            let! reloaded = store.TryLoad(scope, runId)
            Expect.isNone reloaded "Clear reclaims the run's state"
        }
    ]