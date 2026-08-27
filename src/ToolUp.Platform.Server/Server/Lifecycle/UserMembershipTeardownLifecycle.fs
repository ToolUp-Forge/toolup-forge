module ToolUp.Platform.UserMembershipTeardownLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

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

type UserMembershipTeardownLifecycle(services: IServiceProvider) =

    /// Resolve the user's email via the registered `IUserDirectory`,
    /// best-effort. `None` when no directory is composed, the lookup
    /// errors, or the directory holds no email for the id.
    let resolveEmail (userId: string) : Async<string option> = async {
        match services.GetService(typeof<IUserDirectory>) with
        | :? IUserDirectory as directory ->
            let! resolved = directory.ResolveUsers [ userId ]

            match resolved with
            | Ok summaries -> return summaries |> List.tryPick (fun s -> if s.UserId = userId then s.Email else None)
            | Error _ -> return None
        | _ -> return None
    }

    interface ITenantLifecycle with
        member _.Name = "user-membership-teardown"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped
                    "no provisioning action — membership rows are created by invitation / join flows"
        }

        member _.OnDeprovisioned(scopeId, actorUserId) = async {
            if not (scopeId.StartsWith("user-", StringComparison.Ordinal)) then
                return
                    LifecycleHookResult.Skipped
                        "not a user scope — membership teardown applies to user-{id} offboards only"
            else
                match services.GetService(typeof<ITeamStore>) with
                | :? ITeamStore as store ->
                    let userId = scopeId.Substring 5

                    // Snapshot the affected teams BEFORE the purge — the
                    // audit trail records one MemberRemoved per stripped
                    // team, and the membership rows are gone afterwards.
                    let! teams = store.GetTeamsForUser userId

                    match! store.PurgeUser userId with
                    | Error err -> return LifecycleHookResult.Failed err
                    | Ok() ->
                        match services.GetService(typeof<IAuditLog>) with
                        | :? IAuditLog as auditLog ->
                            for team in teams do
                                do!
                                    auditLog.Record(
                                        team.TeamId,
                                        MemberRemoved {
                                            UserId = actorUserId
                                            TeamId = team.TeamId
                                            AffectedUserId = userId
                                        }
                                    )
                        | _ -> ()

                        // Pending-invite sweep — a purged user must not
                        // auto-rejoin from a stale email invite on their
                        // next sign-in.
                        match! resolveEmail userId with
                        | None -> return LifecycleHookResult.Completed
                        | Some address ->
                            match services.GetService(typeof<IPendingInviteStore>) with
                            | :? IPendingInviteStore as invites ->
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
                            | _ -> return LifecycleHookResult.Completed
                | _ -> return LifecycleHookResult.Skipped "no ITeamStore resolvable — membership teardown not wired"
        }

    // Phase 54c — mutation-free preview: count the membership rows a
    // user-scope offboard WOULD strip, via the read-only GetTeamsForUser.
    interface ITenantLifecyclePreview with
        member _.OnDeprovisionPreview(scopeId, _actorUserId) = async {
            if not (scopeId.StartsWith("user-", StringComparison.Ordinal)) then
                return
                    LifecyclePreviewItem.affecting
                        "user-membership-teardown"
                        0
                        "not a user scope — nothing to tear down"
            else
                match services.GetService(typeof<ITeamStore>) with
                | :? ITeamStore as store ->
                    let userId = scopeId.Substring 5
                    let! teams = store.GetTeamsForUser userId

                    return
                        LifecyclePreviewItem.affecting
                            "user-membership-teardown"
                            teams.Length
                            (sprintf "%d team membership row(s) would be stripped" teams.Length)
                | _ ->
                    return
                        LifecyclePreviewItem.affecting
                            "user-membership-teardown"
                            0
                            "no ITeamStore resolvable — nothing to tear down"
        }

/// Construct the first-party user-membership-teardown lifecycle hook.
/// Resolves the team store (+ optional audit log / user directory /
/// pending-invite store) from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    UserMembershipTeardownLifecycle(services) :> ITenantLifecycle