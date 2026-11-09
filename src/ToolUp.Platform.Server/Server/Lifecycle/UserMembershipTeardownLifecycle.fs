module ToolUp.Platform.Compose.UserMembershipTeardownLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.Config.TeamManagement

// ─── Phase 545 — first-party user-membership-teardown lifecycle hook ─
//
// `DataSubjectRequestLifecycle` erases a `user-{id}` scope's *data*;
// this hook tears down the identity's platform rows — the membership
// blob, the active-team pointer, and any pending email invite naming
// the user — via `ITeamStore.PurgeUser`, so a "deleted" user cannot
// resurrect with full team access on their next sign-in (GP 4). The
// user-scope twin of the Phase 306 team provision/offboard symmetry.
//
// Resolves `ITeamStore` (+ the optional `IAuditLog`, `IUserDirectory`,
// `IPendingInviteStore`) from DI per call (GP 12 rule 4).
//
// **Skipped vs Failed.** Non-user scopes → `Skipped` (this hook only
// acts on `user-{id}` offboards; team scopes are handled by the team
// hooks). No resolvable `ITeamStore` → `Skipped` (membership teardown
// not wired). `PurgeUser` refusing (the user is the last Owner of a
// team) or an invite-sweep storage failure → `Failed` with the
// diagnostic — a surviving row is an orphaned access path the operator
// must see. The offboard's retry ledger can re-run the hook and the
// purge is idempotent, so a partial failure is recoverable.
//
// **Pending-invite sweep.** `IPendingInviteStore` is keyed by email;
// the user id is resolved to an email via the registered
// `IUserDirectory` when one is composed. No directory / no resolvable
// email → the sweep is not applicable and the purge outcome stands
// (a caller holding the email directly can call
// `IPendingInviteStore.Remove` itself).
//
// **Audit.** One `MemberRemoved` per stripped team (the existing
// audit case the team API emits), recorded against the team's scope —
// same shape as `TeamApi.RemoveTeamMember`, with the
// offboard's actor as `UserId`. Emitted here (not in the store)
// because the actor's identity lives in the lifecycle call, mirroring
// the handler-emits-audit convention (Phase 9).
//
// `OnProvisioned` is a no-op `Skipped`: membership rows are created by
// invitation / join flows, not by standing a user scope up.

/// What the teardown acts with: the team store it purges, and the
/// collaborators it uses when they are composed — the audit log (one
/// `MemberRemoved` per stripped team), the user directory and the
/// pending-invite store (the stale-invite sweep).
type TeardownSubstrate = {
    Teams: ITeamStore
    Audit: IAuditLog option
    Directory: IUserDirectory option
    Invites: IPendingInviteStore option
}

/// The user's email via the composed `IUserDirectory`, best-effort. `None`
/// when no directory is composed, the lookup errors, or the directory holds
/// no email for the id.
let private resolveEmail (substrate: TeardownSubstrate) (userId: string) : Async<string option> = async {
    match substrate.Directory with
    | Some directory ->
        let! resolved = directory.ResolveUsers [ userId ]

        match resolved with
        | Ok summaries -> return summaries |> List.tryPick (fun s -> if s.UserId = userId then s.Email else None)
        | Error _ -> return None
    | None -> return None
}

let private optional<'T when 'T: not struct> (services: IServiceProvider) : 'T option =
    match services.GetService(typeof<'T>) with
    | :? 'T as service -> Some service
    | _ -> None

/// The first-party user-membership teardown hook, as a value. It acts on
/// `user-{id}` offboards only; team scopes are handled by the team hooks.
let hook: TenantHook<TeardownSubstrate> = {
    Name = "user-membership-teardown"
    Requires = "an ITeamStore (membership teardown)"
    Resolve =
        fun services ->
            optional<ITeamStore> services
            |> Option.map (fun teams -> {
                Teams = teams
                Audit = optional<IAuditLog> services
                Directory = optional<IUserDirectory> services
                Invites = optional<IPendingInviteStore> services
            })
    // Membership rows are created by invitation / join flows.
    OnProvisioned = None
    OnDeprovisioned =
        Some(fun substrate scopeId actorUserId -> async {
            match ScopeId.parse scopeId with
            | ScopeKind.User userId ->
                let store = substrate.Teams

                // Snapshot the affected teams BEFORE the purge — the audit
                // trail records one MemberRemoved per stripped team, and the
                // membership rows are gone afterwards.
                let! teams = store.GetTeamsForUser(userId)

                match! store.PurgeUser(userId) with
                | Error err -> return LifecycleHookResult.Failed err
                | Ok() ->
                    match substrate.Audit with
                    | Some auditLog ->
                        for team in teams do
                            do!
                                auditLog.Record(
                                    AuditScope.team (TeamId team.TeamId),
                                    AuditSubject.UserAudit(UserId actorUserId),
                                    MemberRemoved {
                                        UserId = UserId actorUserId
                                        TeamId = TeamId team.TeamId
                                        AffectedUserId = userId
                                    }
                                )
                                |> AuditRow.orRefuse
                    | None -> ()

                    // Pending-invite sweep — a purged user must not
                    // auto-rejoin from a stale email invite on their next
                    // sign-in.
                    match! resolveEmail substrate userId.Value with
                    | None -> return LifecycleHookResult.Completed
                    | Some address ->
                        match substrate.Invites with
                        | Some invites ->
                            match! invites.Remove address with
                            | Ok()
                            | Error PendingInviteStoreError.NotFound -> return LifecycleHookResult.Completed
                            | Error(PendingInviteStoreError.StorageFailed msg) ->
                                return
                                    LifecycleHookResult.Failed(
                                        sprintf "membership purged, but the pending-invite sweep failed: %s" msg
                                    )
                            | Error PendingInviteStoreError.Conflict ->
                                return
                                    LifecycleHookResult.Failed
                                        "membership purged, but the pending-invite sweep hit a concurrency conflict"
                        | None -> return LifecycleHookResult.Completed
            | ScopeKind.Team _
            | ScopeKind.Other _ ->
                return
                    LifecycleHookResult.Skipped
                        "not a user scope — membership teardown applies to user-{id} offboards only"
        })
    // Mutation-free preview: the membership rows a user-scope offboard
    // WOULD strip, via the read-only GetTeamsForUser.
    Preview =
        Some(fun substrate scopeId _ -> async {
            match ScopeId.parse scopeId with
            | ScopeKind.User userId ->
                let! teams = substrate.Teams.GetTeamsForUser(userId)
                return teams.Length, sprintf "%d team membership row(s) would be stripped" teams.Length
            | ScopeKind.Team _
            | ScopeKind.Other _ -> return 0, "not a user scope — nothing to tear down"
        })
}

/// Construct the first-party user-membership-teardown lifecycle hook.
/// Resolves the team store (+ optional audit log / user directory /
/// pending-invite store) from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle = TenantHook.asLifecycle hook services