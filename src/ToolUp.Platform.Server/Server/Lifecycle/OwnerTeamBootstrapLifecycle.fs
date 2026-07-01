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
// **Owner-identity note.** Phase 305 threads the deploy-plane
// `ProvisioningRequest` through the optional
// `ITenantLifecycleProvisionContext` surface, so `OnProvisionedWith`
// attaches `ProvisioningRequest.OwnerUserId` as the team `Owner` when the
// request carries one. The request's owner is often a *different* user
// from the acting admin (an operator provisions a tenant on a customer's
// behalf), which the actor-only base surface could not express. When no
// request rides (the base `OnProvisioned` path) or its `OwnerUserId` is
// blank, the hook falls back to `actorUserId` — byte-identical to the
// pre-305 behaviour (the provisioning actor IS the owner for the
// self-provision case).
//
// **Phase 306 — offboard teardown (provision/offboard symmetry).**
// `OnDeprovisioned` is now the symmetric counterpart of the bootstrap: it
// deletes the owner-team row this hook created, so what provisioning
// creates, offboarding removes. It deletes only the team metadata row (via
// `ITeamStore.DeleteTeam`); membership blobs live under a separate prefix
// and stay the offboard erasure path's job — so this teardown running in
// the same parallel offboard sweep as `DataSubjectRequestLifecycle` (which
// enumerates members to erase their data) cannot race it away.
// `DeleteTeam` is read-guarded on `GetTeam`, so a re-run over an
// already-deleted team — the Phase 54b resumable re-dispatch, or a double
// offboard — is a clean no-op.

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

/// Bootstrap the owner team for `scopeId`, attaching `ownerUserId` as the
/// team `Owner`. Shared by the base `OnProvisioned` (owner == acting
/// admin) and the Phase 305 `OnProvisionedWith` (owner ==
/// `ProvisioningRequest.OwnerUserId`, falling back to the actor). Both
/// steps are read-guarded so re-provision is idempotent.
let private bootstrap (store: ITeamStore) (scopeId: string) (ownerUserId: string) : Async<LifecycleHookResult> = async {
    match teamIdOf scopeId with
    | None -> return LifecycleHookResult.Skipped "scope is not a team scope — no owner team to bootstrap"
    | Some teamId ->
        let! existing = store.GetTeam teamId

        match existing with
        | Some _ ->
            // Team already exists — re-provision. Ensure the owner
            // membership is present, then done.
            match! ensureOwner store teamId ownerUserId with
            | Ok() -> return LifecycleHookResult.Completed
            | Error e -> return LifecycleHookResult.Failed(sprintf "attach owner to %s: %s" teamId e)
        | None ->
            match! store.CreateTeam(teamId, teamId) with
            | Error e -> return LifecycleHookResult.Failed(sprintf "create team %s: %s" teamId e)
            | Ok _ ->
                match! ensureOwner store teamId ownerUserId with
                | Ok() -> return LifecycleHookResult.Completed
                | Error e -> return LifecycleHookResult.Failed(sprintf "attach owner to %s: %s" teamId e)
}

/// Phase 306 — tear down the bootstrapped owner team for `scopeId` on
/// offboard: delete the team row (the symmetric counterpart of
/// `bootstrap`). Read-guarded on `GetTeam` so a re-run over an
/// already-deleted team — the Phase 54b resumable re-dispatch, or a double
/// offboard — is a no-op that still reports Completed. A non-team scope has
/// no owner team to tear down (`Skipped`), mirroring `bootstrap`.
let private teardown (store: ITeamStore) (scopeId: string) : Async<LifecycleHookResult> = async {
    match teamIdOf scopeId with
    | None -> return LifecycleHookResult.Skipped "scope is not a team scope — no owner team to tear down"
    | Some teamId ->
        match! store.GetTeam teamId with
        | None -> return LifecycleHookResult.Completed
        | Some _ ->
            match! store.DeleteTeam teamId with
            | Ok() -> return LifecycleHookResult.Completed
            | Error e -> return LifecycleHookResult.Failed(sprintf "delete owner team %s: %s" teamId e)
}

/// The effective owner for a provisioning run: the request's explicit
/// `OwnerUserId` when it carries a non-blank one, else the acting admin.
/// This is the one behavioural lever Phase 305 adds — an operator
/// provisioning on a customer's behalf names the customer as owner rather
/// than themselves.
let private effectiveOwner (context: TenantProvisioningContext) : string =
    match context.Request with
    | Some req when not (String.IsNullOrWhiteSpace req.OwnerUserId) -> req.OwnerUserId
    | _ -> context.ActorUserId

type OwnerTeamBootstrapLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "owner-team-bootstrap"

        member _.OnProvisioned(scopeId, actorUserId) = async {
            match services.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as store -> return! bootstrap store scopeId actorUserId
            | _ -> return LifecycleHookResult.Skipped "no ITeamStore registered (teams substrate not composed)"
        }

        // Phase 306 — offboard teardown: delete the owner-team row this
        // hook created at provisioning (symmetry with the bootstrap).
        member _.OnDeprovisioned(scopeId, _actorUserId) = async {
            match services.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as store -> return! teardown store scopeId
            | _ -> return LifecycleHookResult.Skipped "no ITeamStore registered (teams substrate not composed)"
        }

    // Phase 305 — request-aware provisioning: attach the request's explicit
    // OwnerUserId (falling back to the acting admin) rather than always the
    // actor. Falls back byte-identically to OnProvisioned when no request
    // rides or its owner is blank.
    interface ITenantLifecycleProvisionContext with
        member _.OnProvisionedWith(context) = async {
            match services.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as store -> return! bootstrap store context.ScopeId (effectiveOwner context)
            | _ -> return LifecycleHookResult.Skipped "no ITeamStore registered (teams substrate not composed)"
        }

/// Construct the first-party owner-team bootstrap lifecycle hook.
/// Resolves the active `ITeamStore` from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    OwnerTeamBootstrapLifecycle(services) :> ITenantLifecycle