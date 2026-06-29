module ToolUp.Platform.Tests.InProcess.LifecycleSummaryStoreTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 54e — BlobBackedLifecycleSummaryStore tests ───────────────
//
// The durable last-`LifecycleSummary` backing for `GetLifecycleSummary`.
// Exercises round-trip fidelity (the `TenantLifecyclePhase` DU + nested
// outcome list survive serialisation), restart-survival (a fresh store
// instance over the same blob backend reads what the prior one wrote —
// the cross-replica / post-restart property the phase exists for), and
// corrupt-blob tolerance (a bad sidecar reads as `None`, never throws
// into the admin read path).

let private sampleSummary scopeId = {
    ScopeId = scopeId
    Phase = Deprovisioning
    Outcomes = [
        {
            HookName = "encryption-key"
            Result = LifecycleHookResult.Completed
            ElapsedMs = 12L
        }
        {
            HookName = "membership-cache"
            Result = LifecycleHookResult.Skipped "no cache configured"
            ElapsedMs = 3L
        }
        {
            HookName = "scheduled-jobs"
            Result = LifecycleHookResult.Failed "scheduler unreachable"
            ElapsedMs = 5000L
        }
    ]
    TotalElapsedMs = 5012L
}

let tests =
    testList "Phase 54e — BlobBackedLifecycleSummaryStore" [

        testCaseAsync "round-trips a summary with full DU + outcome-list fidelity"
        <| async {
            let blobs = InMemoryBlobStorage() :> BlobStorage.IBlobStorage
            let store = BlobBackedLifecycleSummaryStore.create blobs
            let original = sampleSummary "team-acme"

            do! store.SetLast("team-acme", original)
            let! readBack = store.GetLast "team-acme"

            Expect.equal readBack (Some original) "the persisted summary round-trips structurally"
        }

        testCaseAsync "GetLast returns None for a scope with no persisted run"
        <| async {
            let blobs = InMemoryBlobStorage() :> BlobStorage.IBlobStorage
            let store = BlobBackedLifecycleSummaryStore.create blobs

            let! readBack = store.GetLast "team-never-run"
            Expect.equal readBack None "an un-persisted scope reads as None"
        }

        testCaseAsync "survives a process restart — a fresh store over the same backend reads the last summary"
        <| async {
            // One shared blob backend; two store instances stand in for
            // two processes / replicas. The second never wrote, yet reads
            // what the first persisted — the restart/cluster fix.
            let blobs = InMemoryBlobStorage() :> BlobStorage.IBlobStorage
            let original = sampleSummary "team-acme"

            let writer = BlobBackedLifecycleSummaryStore.create blobs
            do! writer.SetLast("team-acme", original)

            let restarted = BlobBackedLifecycleSummaryStore.create blobs
            let! readBack = restarted.GetLast "team-acme"

            Expect.equal readBack (Some original) "a fresh instance reads the durable last summary"
        }

        testCaseAsync "SetLast overwrites the prior summary (last run wins)"
        <| async {
            let blobs = InMemoryBlobStorage() :> BlobStorage.IBlobStorage
            let store = BlobBackedLifecycleSummaryStore.create blobs

            do! store.SetLast("team-acme", sampleSummary "team-acme")

            let newer = {
                sampleSummary "team-acme" with
                    TotalElapsedMs = 99L
                    Outcomes = []
            }

            do! store.SetLast("team-acme", newer)
            let! readBack = store.GetLast "team-acme"

            Expect.equal readBack (Some newer) "the most recent persisted summary wins"
        }

        testCaseAsync "scope ids are isolated — one scope's summary never bleeds into another"
        <| async {
            let blobs = InMemoryBlobStorage() :> BlobStorage.IBlobStorage
            let store = BlobBackedLifecycleSummaryStore.create blobs

            do! store.SetLast("team-a", sampleSummary "team-a")
            let! readB = store.GetLast "team-b"

            Expect.equal readB None "a different scope reads None"
        }

        testCaseAsync "corrupt sidecar reads as None rather than throwing"
        <| async {
            let blobs = InMemoryBlobStorage() :> BlobStorage.IBlobStorage
            // Plant a non-JSON blob at the path the store reads for team-x.
            let! _ =
                blobs.Upload(
                    "_platform",
                    "_tenant-lifecycle/team-x.json",
                    System.Text.Encoding.UTF8.GetBytes "not json {{{"
                )

            let store = BlobBackedLifecycleSummaryStore.create blobs
            let! readBack = store.GetLast "team-x"

            Expect.equal readBack None "a corrupt sidecar degrades to None, never an exception"
        }
    ]