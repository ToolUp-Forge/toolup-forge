module ToolUp.Platform.Tests.Contracts.ITenantLifecycleContract

open System
open Expecto
open ToolUp.Platform

// ─── Phase 54 — ITenantLifecycle contract pack ───────────────────────
//
// The conformance bar for the four first-party hooks: each must
// `Skipped` (never `Failed`, never throw) when its substrate is absent
// from the resolving `IServiceProvider`. This is the graceful-degrade
// guarantee that lets an offboard on a minimal deployment run clean —
// `EncryptionKeyLifecycle` under no resolver, `JobSchedulerLifecycle`
// under no scheduler, etc. all report `Skipped`, so a `DeprovisionTenant`
// on such a deployment returns a summary of four `Skipped` outcomes
// rather than four failures.

/// `IServiceProvider` that resolves nothing — models a minimal
/// deployment where none of the hooks' substrates are registered.
let private emptyProvider: IServiceProvider =
    { new IServiceProvider with
        member _.GetService(_serviceType) = null
    }

let private isSkipped (result: LifecycleHookResult) =
    match result with
    | LifecycleHookResult.Skipped _ -> true
    | _ -> false

let tests =
    testList "ITenantLifecycle — first-party hook contract" [

        testCaseAsync "EncryptionKeyLifecycle skips when no IBlobEncryptionKeyResolver is registered"
        <| async {
            let hook = EncryptionKeyLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without an encryption resolver"
        }

        testCaseAsync "MembershipCacheLifecycle skips when no TeamScopeResolver is registered"
        <| async {
            let hook = MembershipCacheLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a team scope resolver"
        }

        testCaseAsync "JobSchedulerLifecycle skips when no IJobScheduler is registered"
        <| async {
            let hook = JobSchedulerLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a scheduler"
        }

        testCaseAsync "DataSubjectRequestLifecycle skips when no IErasureHandler is registered"
        <| async {
            let hook = DataSubjectRequestLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("user-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without erasure handlers"
        }

        // ─── Phase 54d — domain / companion offboard hooks ───────────

        testCaseAsync "ConversationStoreLifecycle skips when no IConversationStore is registered"
        <| async {
            let hook = ConversationStoreLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a conversation store"
        }

        testCaseAsync "KnowledgeBaseLifecycle skips when no IBlobStorage is registered"
        <| async {
            let hook = ToolUp.KnowledgeBase.Server.KnowledgeBaseLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a blob store (KB substrate uncomposed)"
        }

        testCaseAsync "RagVectorStoreLifecycle skips when no IVectorStore is registered"
        <| async {
            let hook = ToolUp.RAG.RagVectorStoreLifecycle.create emptyProvider
            let! result = hook.OnDeprovisioned("team-x", "admin")
            Expect.isTrue (isSkipped result) "skipped without a vector store"
        }

        testCaseAsync "every first-party hook is a no-op Skipped on OnProvisioned"
        <| async {
            let hooks = [
                EncryptionKeyLifecycle.create emptyProvider
                MembershipCacheLifecycle.create emptyProvider
                JobSchedulerLifecycle.create emptyProvider
                DataSubjectRequestLifecycle.create emptyProvider
                ConversationStoreLifecycle.create emptyProvider
                ToolUp.KnowledgeBase.Server.KnowledgeBaseLifecycle.create emptyProvider
                ToolUp.RAG.RagVectorStoreLifecycle.create emptyProvider
            ]

            for hook in hooks do
                let! result = hook.OnProvisioned("team-x", "admin")
                Expect.isTrue (isSkipped result) (sprintf "%s provisioning is a no-op skip" hook.Name)
        }

        testCaseAsync "first-party hook names are distinct (no aggregation collision)"
        <| async {
            let names =
                [
                    EncryptionKeyLifecycle.create emptyProvider
                    MembershipCacheLifecycle.create emptyProvider
                    JobSchedulerLifecycle.create emptyProvider
                    DataSubjectRequestLifecycle.create emptyProvider
                    ConversationStoreLifecycle.create emptyProvider
                    ToolUp.KnowledgeBase.Server.KnowledgeBaseLifecycle.create emptyProvider
                    ToolUp.RAG.RagVectorStoreLifecycle.create emptyProvider
                ]
                |> List.map (fun h -> h.Name)

            Expect.equal (List.distinct names |> List.length) names.Length "all hook names are unique"
        }

        // ─── Phase 54b — resumable offboard ledger contract ──────────
        //
        // ILifecycleLedger conformance (against the blob-backed default
        // over the in-memory blob double). Resumability + retry *through
        // the aggregator sweep* are exercised in
        // TenantLifecycleAggregatorTests (`runResumable`); these cases pin
        // the ledger seam those callbacks ride on.

        testCaseAsync "ledger records a hook then GetCompleted reads it back"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
                )

            let! before = ledger.GetCompleted("team-x", Deprovisioning)
            Expect.isTrue (Set.isEmpty before) "a fresh ledger is empty"

            do! ledger.Record("team-x", Deprovisioning, "encryption-key", LedgerDisposition.Completed)
            do! ledger.Record("team-x", Deprovisioning, "data-erasure", LedgerDisposition.Skipped)

            let! after = ledger.GetCompleted("team-x", Deprovisioning)
            Expect.equal after (Set.ofList [ "encryption-key"; "data-erasure" ]) "both dispositions recorded as done"
        }

        testCaseAsync "ledger Record is idempotent — recording the same hook twice is one entry"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
                )

            do! ledger.Record("team-x", Deprovisioning, "job-scheduler", LedgerDisposition.Completed)
            do! ledger.Record("team-x", Deprovisioning, "job-scheduler", LedgerDisposition.Completed)

            let! completed = ledger.GetCompleted("team-x", Deprovisioning)
            Expect.equal completed (Set.ofList [ "job-scheduler" ]) "duplicate record collapses to one entry"
        }

        testCaseAsync "ledger keys are isolated per (scope, phase)"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
                )

            do! ledger.Record("team-a", Deprovisioning, "h", LedgerDisposition.Completed)

            let! otherScope = ledger.GetCompleted("team-b", Deprovisioning)
            let! otherPhase = ledger.GetCompleted("team-a", Provisioning)
            Expect.isTrue (Set.isEmpty otherScope) "a different scope sees nothing"
            Expect.isTrue (Set.isEmpty otherPhase) "a different phase sees nothing"
        }

        testCaseAsync "ledger Clear resets the run so a re-offboard starts fresh"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
                )

            do! ledger.Record("team-x", Deprovisioning, "h1", LedgerDisposition.Completed)
            do! ledger.Record("team-x", Deprovisioning, "h2", LedgerDisposition.Completed)
            do! ledger.Clear("team-x", Deprovisioning)

            let! afterClear = ledger.GetCompleted("team-x", Deprovisioning)
            Expect.isTrue (Set.isEmpty afterClear) "Clear removes every recorded hook for the run"
        }
    ]