module ToolUp.Platform.BootstrapTeam

open System
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── Phase 5f — bootstrap team for closed-roster deployments ──────────
//
// Companion to `PlatformAdminStore.bootstrap` (same shape, same
// idempotency rules, same `"_bootstrap"` actor constant). Resolves the
// chicken-and-egg the secure-by-default `TeamCreationPolicy =
// PlatformAdminOnly` introduces: a fresh deployment with no admins
// can't sign anyone in to create a team, and a fresh deployment with
// an admin but no team leaves Team / MultiTeam users on an empty
// shell. This module creates the first team and adds the bootstrap
// admin as its Owner, off the same env-var pair the operator already
// sets to seed the initial Platform Admin.
//
// **Idempotency.** Runs iff `TOOLUP_INITIAL_TEAM_NAME` is set AND the
// team store is empty. Once any team exists, subsequent restarts no-op
// even if the env vars are still set. Wipe + re-bootstrap is the
// documented disaster-recovery path (matches `PlatformAdminStore`).
//
// **Ordering.** Must be wired in `compose` *after*
// `PlatformAdminStore.bootstrap` — the team needs an Owner, so the
// admin must exist first. The compose call site in `SDK.Server.fs`
// holds that order.
//
// **Atomicity.** `ITeamStore.CreateTeam` → `AddMember(Owner)` →
// `SetActiveTeam` is a three-step sequence; the bootstrap log records
// success / failure for each step. A partial failure (e.g. AddMember
// errors after CreateTeam succeeded) leaves a team with no Owner; the
// operator wipes blob storage and re-runs. This matches the
// PlatformAdminStore bootstrap's posture: best-effort with loud
// logging, never an aborted startup. The substrate guarantee is the
// once-per-deployment shape, not transactional rollback (which would
// need a cross-store coordinator we don't have).

let private bootstrapActor = "_bootstrap"

[<Literal>]
let initialTeamNameEnvVar = ConfigKeys.Names.initialTeamName

[<Literal>]
let initialTeamIdEnvVar = ConfigKeys.Names.initialTeamId

[<Literal>]
let initialAdminEnvVar = ConfigKeys.Names.initialPlatformAdmin

// Phase 698 — the three bootstrap keys resolve through the Phase-696
// `ConfigResolution` seam, so a manifest can declare the initial team the
// same way it declares every other deployment-shape key.
let private trimOrNone (value: string) =
    if String.IsNullOrWhiteSpace value then
        None
    else
        Some(value.Trim())

/// Bootstrap the initial team. Wired into `compose` after
/// `PlatformAdminStore.bootstrap`. Fires once at startup.
///
/// **Inputs (env-derived):**
///   1. `TOOLUP_INITIAL_TEAM_NAME` — required. Empty / unset =>
///      no-op (the operator hasn't asked for a bootstrap team).
///   2. `TOOLUP_INITIAL_TEAM_ID` — optional. Auto-generated 8-char
///      hex when unset, matching `TeamApi.CreateTeam`'s
///      id-minting rule.
///   3. `TOOLUP_INITIAL_PLATFORM_ADMIN` — required when team-name
///      is set. The user who becomes Owner. Preflight
///      `BootstrapTeamValidator` refuses startup when name is set
///      and admin is unset; this function logs `Error` and no-ops
///      as a failsafe in case the validator was bypassed.
///
/// **Side effects (success path):**
///   * `ITeamStore.CreateTeam(teamId, teamName)`
///   * Audit `TeamCreated { Actor = "_bootstrap" }` under `teamId`
///     scope (matches handler-emitted audit shape).
///   * `ITeamStore.AddMember(teamId, adminUserId, Owner)`
///   * Audit `MemberAdded { Actor = "_bootstrap" }` under `teamId`.
///   * `ITeamStore.SetActiveTeam(adminUserId, teamId)`.
///
/// **No-op conditions:** env var unset; team store non-empty;
/// admin env var unset (logged at `Error`).
let bootstrap (logger: ILogger) (auditLog: IAuditLog) (teamStore: ITeamStore) : Async<unit> = async {
    let nameOpt =
        ConfigResolution.tryValue initialTeamNameEnvVar |> Option.bind trimOrNone

    match nameOpt with
    | None ->
        // Operator hasn't opted into the bootstrap. Silent no-op —
        // matches `PlatformAdminStore.bootstrap`'s "no source set"
        // branch.
        return ()
    | Some teamName ->
        let! existing = teamStore.ListTeams()

        if not (List.isEmpty existing) then
            // Already-populated — bootstrap is one-shot.
            return ()
        else
            let adminOpt =
                ConfigResolution.tryValue initialAdminEnvVar |> Option.bind trimOrNone

            match adminOpt with
            | None ->
                logger.Error(
                    sprintf
                        "[BootstrapTeam] %s is set (%s) but %s is unset; cannot create a team without an Owner. Set %s before restart, or unset %s to skip the bootstrap."
                        initialTeamNameEnvVar
                        teamName
                        initialAdminEnvVar
                        initialAdminEnvVar
                        initialTeamNameEnvVar,
                    None
                )

                return ()
            | Some adminUserId ->
                let teamId =
                    ConfigResolution.tryValue initialTeamIdEnvVar
                    |> Option.bind trimOrNone
                    |> Option.defaultWith (fun () -> Guid.NewGuid().ToString("N")[..7])

                let! createResult = teamStore.CreateTeam(teamId, teamName)

                match createResult with
                | Error msg ->
                    logger.Error(
                        sprintf "[BootstrapTeam] CreateTeam failed for team '%s' (id %s): %s" teamName teamId msg,
                        None
                    )

                    return ()
                | Ok team ->
                    do!
                        auditLog.Record(
                            teamId,
                            TeamCreated {
                                UserId = bootstrapActor
                                TeamId = teamId
                                TeamName = teamName
                            }
                        )

                    let! addResult = teamStore.AddMember(teamId, adminUserId, Owner)

                    match addResult with
                    | Error msg ->
                        // Team exists but has no Owner — loud log, the
                        // operator will wipe + re-bootstrap. We don't
                        // try to roll back the team create because
                        // `ITeamStore` has no delete primitive (Phase 5
                        // deliberately omits team deletion).
                        logger.Error(
                            sprintf
                                "[BootstrapTeam] Created team '%s' (id %s) but AddMember(Owner = %s) failed: %s. The team has no Owner and is unmanageable; wipe blob storage and restart with %s + %s set."
                                teamName
                                teamId
                                adminUserId
                                msg
                                initialTeamNameEnvVar
                                initialAdminEnvVar,
                            None
                        )

                        return ()
                    | Ok() ->
                        do!
                            auditLog.Record(
                                teamId,
                                MemberAdded {
                                    UserId = bootstrapActor
                                    TeamId = teamId
                                    AffectedUserId = adminUserId
                                    Role = TeamRoles.displayName Owner
                                }
                            )

                        let! activeResult = teamStore.SetActiveTeam(adminUserId, teamId)

                        match activeResult with
                        | Ok() ->
                            logger.Info(
                                sprintf
                                    "[BootstrapTeam] Bootstrapped team '%s' (id %s) with Owner %s from %s / %s."
                                    team.Name
                                    teamId
                                    adminUserId
                                    initialTeamNameEnvVar
                                    initialAdminEnvVar
                            )
                        | Error msg ->
                            // Team + Owner are persisted; the active-
                            // team pointer is best-effort UX. Warn
                            // rather than Error because the admin can
                            // still select the team manually.
                            logger.Warn(
                                sprintf
                                    "[BootstrapTeam] Bootstrapped team '%s' (id %s) with Owner %s but SetActiveTeam failed: %s. The admin can pick the team from the team switcher."
                                    team.Name
                                    teamId
                                    adminUserId
                                    msg
                            )
}