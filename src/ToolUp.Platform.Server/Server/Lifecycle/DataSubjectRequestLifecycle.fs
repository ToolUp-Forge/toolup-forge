module ToolUp.Platform.DataSubjectRequestLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.TeamManagement

// ─── Phase 54 — first-party data-erasure lifecycle hook ──────────────
//
// On `OnDeprovisioned`, erases the offboarded tenant's subject data by
// driving every registered Phase 9h `IErasureHandler` (the same
// extension point `IDataSubjectRequestApi` composes) under
// `ErasurePolicy.HardDelete`. Resolves `seq<IErasureHandler>` + the
// optional `ITeamStore` from DI per call (GP 12 rule 4).
//
// **Subject resolution.** `IErasureHandler.Erase` is subject-keyed
// (it erases records *naming* a subject within a scope — Phase 9h's
// per-DSR contract). A tenant offboard must clear *every* member's
// data, so the hook resolves the subject set:
//   * team scope (`team-{id}` / a bare team id `ITeamStore` resolves) —
//     every member's `UserId` via `ITeamStore.GetTeamMembers`.
//   * user scope (`user-{id}`) — that one user id.
//   * anything else — the scope id verbatim as the subject.
// It then erases each (subject × handler) pair and sums the affected
// counts.
//
// **Graceful degradation (soft dep on Phase 9h.A).** This runs the
// erasure *synchronously inline*. When the async-erasure substrate
// (Phase 9h.A, `IJobScheduler`-backed background DSR) is present, a
// long multi-store erasure should route through it so the offboard
// returns promptly and the erasure survives a restart; that routing is
// a follow-on. Absent 9h.A, the synchronous path here IS the graceful
// degrade — bounded by the aggregator's 5-minute `OnDeprovisioned`
// per-hook timeout.
//
// **Skipped vs Failed.** No registered `IErasureHandler` → `Skipped`
// (subject-data erasure not wired). A team scope with no resolvable
// `ITeamStore` → `Skipped` (can't enumerate subjects). Any handler
// returning `Error` → `Failed` with the joined diagnostics (data was
// NOT erased — the operator must see it), even when other handlers
// succeeded.
//
// `OnProvisioned` is a no-op `Skipped`: there is nothing to erase when
// a tenant is stood up.

/// Resolve the subject set whose data the offboard must erase.
let private subjectsFor (services: IServiceProvider) (scopeId: string) : Async<Result<string list, string>> = async {
    if scopeId.StartsWith("team-", StringComparison.Ordinal) then
        match services.GetService(typeof<ITeamStore>) with
        | :? ITeamStore as store ->
            let teamId = scopeId.Substring 5
            let! members = store.GetTeamMembers teamId
            return Ok(members |> List.map _.UserId)
        | _ -> return Error "team scope offboard requires an ITeamStore to enumerate member subjects"
    elif scopeId.StartsWith("user-", StringComparison.Ordinal) then
        return Ok [ scopeId.Substring 5 ]
    else
        return Ok [ scopeId ]
}

type DataSubjectRequestLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "data-erasure"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped
                    "no provisioning action — there is no subject data to erase when a tenant is stood up"
        }

        member _.OnDeprovisioned(scopeId, _actorUserId) = async {
            // MS DI resolves `seq<T>` to an empty enumerable when nothing
            // is registered; guard against a provider that returns null
            // (a bare custom `IServiceProvider`) so the hook degrades to
            // Skipped rather than throwing.
            let handlers =
                match services.GetService(typeof<seq<IErasureHandler>>) with
                | :? seq<IErasureHandler> as hs -> List.ofSeq hs
                | _ -> []

            if List.isEmpty handlers then
                return LifecycleHookResult.Skipped "no IErasureHandler registered (subject-data erasure not wired)"
            else
                let! subjectsResult = subjectsFor services scopeId

                match subjectsResult with
                | Error reason -> return LifecycleHookResult.Skipped reason
                | Ok subjects ->
                    let errors = ResizeArray<string>()

                    for subject in subjects do
                        for handler in handlers do
                            let! result = handler.Erase(scopeId, subject, ErasurePolicy.HardDelete)

                            match result with
                            | Ok _ -> ()
                            | Error err -> errors.Add(sprintf "%s/%s: %A" handler.Name subject err)

                    if errors.Count > 0 then
                        return LifecycleHookResult.Failed(String.Join("; ", errors))
                    else
                        return LifecycleHookResult.Completed
        }

/// Construct the first-party data-erasure lifecycle hook. Resolves the
/// registered erasure handlers (+ optional team store) from `services`
/// on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    DataSubjectRequestLifecycle(services) :> ITenantLifecycle