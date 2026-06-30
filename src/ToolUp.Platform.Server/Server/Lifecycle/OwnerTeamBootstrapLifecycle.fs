module ToolUp.Platform.OwnerTeamBootstrapLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── Phase 54g — first-party owner-team bootstrap lifecycle hook ─────
//
// On `OnProvisioned` for a team scope, ensures the owner team row + its
// owner membership exist, so standing up a tenant no longer needs a
// separate "create the team" choreography. Resolves the active
// `ITeamStore` from DI per call (stateless between invocations,
// GP 12 rule 4):
//   * `ITeamStore` present + a `team-{id}` scope — create the team if
//     absent, then attach the owner membership if absent. Both steps are
//     guarded by a read first, so re-provisioning an existing scope is a
//     no-op (idempotent — no duplicate team / membership).
//   * any non-team scope (`user-{id}`, session, …) — `Skipped`: a user /
//     ephemeral scope has no owner team to bootstrap.
//   * no store registered — `Skipped` (teams substrate not composed).
//
// **Owner-identity note.** The task envisaged seeding the owner from
// `ProvisioningRequest.OwnerUserId`, but the `OnProvisioned` hook surface
// carries only `scopeId` + `actorUserId` (the deploy-plane request is not
// threaded through `ITenantLifecycle`). The provisioning actor IS the
// owner for the self-provision case, so the hook attaches `actorUserId`
// as the team `Owner`. Threading the explicit `OwnerUserId` through the
// hook surface is a follow-on (an `ITenantLifecycle` contract change, out
// of this phase's scope).
//
// `OnDeprovisioned` is a no-op `Skipped`: the offboard substrate evicts
// membership caches + erases subject data; deleting the team row on
// offboard is not this provision-only hook's job.

/// Strip the `team-` container prefix if present so the hook accepts
/// either the bare team id or the `team-{id}` container form (mirrors
/// `MembershipCacheLifecycle.teamIdOf`). Returns `None` for a non-team
/// scope, which the hook treats as "nothing to bootstrap".
let private teamIdOf (scopeId: string) : string option =
    if scopeId.StartsWith("team-", StringComparison.Ordinal) then
        Some(scopeId.Substring 5)
    else
        None

/// Attach `actorUserId` as the team `Owner` if they are not already a
/// member (idempotent). A blank actor is left unattached — the team row
/// still exists, but there is no owner id to bind.
let private ensureOwner (store: ITeamStore) (teamId: string) (actorUserId: string) : Async<Result<unit, string>> = async {
    if String.IsNullOrWhiteSpace actorUserId then
        return Ok()
    else
        match! store.GetMemberRole(teamId, actorUserId) with
        | Some _ -> return Ok()
        | None -> return! store.AddMember(teamId, actorUserId, Owner)
}

type OwnerTeamBootstrapLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "owner-team-bootstrap"

        member _.OnProvisioned(scopeId, actorUserId) = async {
            match services.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as store ->
                match teamIdOf scopeId with
                | None -> return LifecycleHookResult.Skipped "scope is not a team scope — no owner team to bootstrap"
                | Some teamId ->
                    let! existing = store.GetTeam teamId

                    match existing with
                    | Some _ ->
                        // Team already exists — re-provision. Ensure the
                        // owner membership is present, then done.
                        match! ensureOwner store teamId actorUserId with
                        | Ok() -> return LifecycleHookResult.Completed
                        | Error e -> return LifecycleHookResult.Failed(sprintf "attach owner to %s: %s" teamId e)
                    | None ->
                        match! store.CreateTeam(teamId, teamId) with
                        | Error e -> return LifecycleHookResult.Failed(sprintf "create team %s: %s" teamId e)
                        | Ok _ ->
                            match! ensureOwner store teamId actorUserId with
                            | Ok() -> return LifecycleHookResult.Completed
                            | Error e -> return LifecycleHookResult.Failed(sprintf "attach owner to %s: %s" teamId e)
            | _ -> return LifecycleHookResult.Skipped "no ITeamStore registered (teams substrate not composed)"
        }

        member _.OnDeprovisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped
                    "no offboard action — owner-team teardown is the offboard substrate's job, not this provision hook"
        }

/// Construct the first-party owner-team bootstrap lifecycle hook.
/// Resolves the active `ITeamStore` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    OwnerTeamBootstrapLifecycle(services) :> ITenantLifecycle