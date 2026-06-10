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

        testCaseAsync "every first-party hook is a no-op Skipped on OnProvisioned"
        <| async {
            let hooks = [
                EncryptionKeyLifecycle.create emptyProvider
                MembershipCacheLifecycle.create emptyProvider
                JobSchedulerLifecycle.create emptyProvider
                DataSubjectRequestLifecycle.create emptyProvider
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
                ]
                |> List.map (fun h -> h.Name)

            Expect.equal (List.distinct names |> List.length) names.Length "all four hook names are unique"
        }
    ]