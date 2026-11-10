module ToolUp.Platform.Scope.TeamAuthority

open ToolUp.Platform
open ToolUp.Platform.Config.TeamManagement

// ─── One "may this caller act on this team" gate ─────────────────────
//
// `require` is the one place a handler asks whether the caller may act on a
// team: read the caller's role, treat a platform admin as the team's Owner
// where the gate says so, apply one of the pure `TeamRoles` predicates. It
// takes the request's principal (the `AccessContext` the scope middleware
// stamps once), so the
// platform role is read from the principal rather than re-queried from
// `IPlatformAdminStore`, and the caller id is the principal's own. The
// predicates stay in `TeamRoles`; the refusal is data each site renders in
// its own words.

/// Whether a holder of `PlatformRole.PlatformAdmin` is the team's Owner at a
/// gate. A gate states it; it is not inferred.
[<RequireQualifiedAccess>]
type AdminBypass =
    /// A platform admin acts as the team's Owner, member or not — the
    /// "complete rights across all teams" contract of team membership
    /// management.
    | PlatformAdminIsOwner
    /// The caller's own membership decides; the platform role confers
    /// nothing at this gate.
    | MembershipOnly

/// Why `require` refused.
[<RequireQualifiedAccess>]
type AuthorityRefusal =
    /// The caller holds no role on the team.
    | NotAMember of TeamId
    /// The caller's role on the team does not satisfy the gate's predicate.
    | InsufficientRole of TeamId * TeamRole

/// The caller's effective role on `teamId`: `Owner` for a platform admin
/// under `PlatformAdminIsOwner`, otherwise the team store's answer for the
/// principal's id.
let effectiveRole
    (teams: ITeamStore)
    (bypass: AdminBypass)
    (principal: AccessContext)
    (teamId: TeamId)
    : Async<TeamRole option> =
    match bypass with
    | AdminBypass.PlatformAdminIsOwner when AccessContext.canModifyPlatformConfig principal -> async.Return(Some Owner)
    | _ -> teams.GetMemberRole(teamId, UserId(AccessContext.callerId principal))

/// The one team-authority decision: the caller's effective role on `teamId`
/// when `permits` admits it, else the refusal.
let require
    (teams: ITeamStore)
    (bypass: AdminBypass)
    (principal: AccessContext)
    (teamId: TeamId)
    (permits: TeamRole -> bool)
    : Async<Result<TeamRole, AuthorityRefusal>> =
    async {
        match! effectiveRole teams bypass principal teamId with
        | None -> return Error(AuthorityRefusal.NotAMember teamId)
        | Some role when permits role -> return Ok role
        | Some role -> return Error(AuthorityRefusal.InsufficientRole(teamId, role))
    }

/// The refusal in the words the team-management handlers have always used:
/// "<who> can <act>. Your role: <role>." or "You are not a member of this team."
let describe (who: string) (act: string) (refusal: AuthorityRefusal) : string =
    match refusal with
    | AuthorityRefusal.NotAMember _ -> "You are not a member of this team."
    | AuthorityRefusal.InsufficientRole(_, role) ->
        $"Only team {who} can {act}. Your role: {TeamRoles.displayName role}."