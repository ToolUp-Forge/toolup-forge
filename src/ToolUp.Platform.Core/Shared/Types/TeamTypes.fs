// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Team types (shared, Fable-compatible) ────────────────────────

/// Metadata describing a team.
type TeamInfo = {
    TeamId: string
    Name: string
    CreatedAt: DateTime
    /// Whether the team is archived. Archived teams are filtered out
    /// of members' team lists (`GetMyTeams`) and cannot be selected as
    /// the active team; a member whose active team is archived is
    /// bumped to the no-active-team state. Nothing is deleted —
    /// archive is fully reversible via `TeamApi.RestoreTeam`. Persisted
    /// team blobs written before this field existed deserialize as
    /// `false` (the `archived` JSON property is treated as optional).
    Archived: bool
}

/// Richer per-team view for the Platform-Management admin table. Pairs
/// each team's metadata with a membership summary (count + the user ids
/// holding Owner / Admin roles) so a Platform Admin can see, at a
/// glance, who runs each team. Computed server-side by `ListAllTeams`
/// from `ITeamStore.ListTeams` + `GetTeamMembers`; the user ids are the
/// admin-asserted membership principal ids. They are carried as raw ids
/// on the wire — a deployment that wires an `IUserDirectory` companion
/// resolves them to emails client-side via `IUserDirectoryApi.ResolveUsers`
/// (the Platform-Management table renders the email with the id as a
/// fallback); deployments without a directory keep showing the ids.
type TeamSummary = {
    TeamId: string
    Name: string
    CreatedAt: DateTime
    Archived: bool
    MemberCount: int
    Owners: string list
    Admins: string list
}

/// A user's membership in a team.
type TeamMembership = {
    TeamId: string
    UserId: string
    Role: TeamRole
    JoinedAt: DateTime
}

// ─── Platform info ────────────────────────────────────────────────

/// Platform info returned to the client.
type PlatformInfo = { RequiresAuth: bool }

/// Phase 5f — who may call `TeamApi.CreateTeam` on a `Team` /
/// `MultiTeam` deployment. Secure-by-default flip: closed-roster
/// deployments (internal tools, single-company SaaS, regulated
/// environments where team membership is provisioned, not self-
/// service) want team creation gated on Platform Admin so that an
/// authenticated user can't conjure a new team and auto-Owner it.
/// Open-membership deployments (community tools, signup-forward SaaS)
/// flip back to the pre-5f shape with
/// `{ ServerConfig.defaults with TeamCreationPolicy = AnyAuthenticatedUser }`.
///
/// The substrate gate is the `IPlatformAdminStore` check inside
/// `teamApiHandler.CreateTeam`; the `TeamCreationPolicyValidator` /
/// `BootstrapTeamValidator` preflight refuses to start a wedged
/// configuration (policy = `PlatformAdminOnly` with no bootstrap
/// admin set; team-name set with no admin to own it).
///
/// Independent of `IPlatformAdminStore` itself — admin assignment
/// remains gated on `canModifyPlatformConfig` regardless of this
/// setting. The policy gates one specific action (team creation)
/// against the same admin-role read.
///
/// Lives in `TeamTypes.fs` (not next to the other `ServerConfig`
/// modes in `SDK.Shared.fs`) because `TeamApi.GetTeamCreationPolicy`
/// returns it and `TeamApi` is declared further down in this same
/// file — `TeamTypes.fs` compiles before `SDK.Shared.fs`, so siting
/// the DU here is what makes it reachable from both the client
/// Fable.Remoting surface and the `ServerConfig` record without
/// introducing a reverse cross-reference.
type TeamCreationPolicy =
    /// Default. Only callers holding `PlatformRole.PlatformAdmin`
    /// may create teams. Non-admin `CreateTeam` calls return
    /// `Error "Team creation requires Platform Admin"`; the
    /// `TeamManagerUI` hides the Create form for non-admins so
    /// the affordance only renders for callers it would succeed
    /// for.
    | PlatformAdminOnly
    /// Pre-5f shape: any authenticated user in `Team` / `MultiTeam`
    /// mode may call `CreateTeam` and is auto-promoted to Owner of
    /// the resulting team. Appropriate for open-membership
    /// deployments where self-service team creation is part of the
    /// product surface.
    | AnyAuthenticatedUser

// ─── Platform API contracts ───────────────────────────────────────
//
// Originally a single `PlatformApi` record carrying 14 methods across
// five concerns (info, team CRUD, permissions, accessibility, data
// catalog). Split per Tidy-Up "Split PlatformApi as it grows" into
// five sibling records so each concern has its own Fable.Remoting
// proxy route prefix, its own composeable handler, and a clean
// per-concern test surface. The compose root auto-injects all five
// via `SDK.Server.compose` — consumers see no change to the
// fluent-config API.

type AccessibleModulesResponse = {
    Managed: string list
    Accessible: string list
}

/// Platform-level info: mode, auth posture. Always-on, no auth
/// gating — the client shell needs this before it can decide whether
/// to render a login affordance. Auto-injected by `SDK.Server.compose`.
type PlatformInfoApi = {
    /// Get current platform configuration (mode, auth requirements).
    /// Ungated by design — the client shell calls this before any
    /// sign-in to decide whether to render a login affordance.
    [<AllowAnonymous>]
    GetPlatformInfo: unit -> Async<PlatformInfo>
}

/// Input to `TeamApi.CreateTeamWithOwner`. The Platform-Management
/// "create team" flow names an initial Owner explicitly (rather than
/// the caller defaulting to Owner the way the legacy `CreateTeam`
/// path does). Self-as-owner is the typical case for a Platform
/// Admin spinning up a team they intend to run; specifying another
/// user is the typical case for a Platform Admin provisioning a
/// team on behalf of someone else, e.g. before the named owner has
/// even signed in.
type CreateTeamRequest = {
    /// Display name for the new team. Free text — NOT marked
    /// `PiiSafe`, so it is redacted from dispatcher-emitted audit
    /// payloads (the in-handler `TeamCreated` event carries it).
    Name: string
    /// Initial Owner's user id. The named user is added as the
    /// team's `Owner` immediately after the team row is minted; if
    /// they haven't signed in yet, the membership is still attached
    /// to the user id (server-side substrate has no precondition on
    /// the IdP having seen them — the pending-invite substrate is
    /// the recommended path when the recipient is identified by
    /// email only).
    [<PiiSafe>]
    InitialOwnerUserId: string
}

/// Team CRUD + membership management. Owner/Admin gating enforced
/// server-side inside the handler. Auto-injected by
/// `SDK.Server.compose`.
type TeamApi = {
    /// Create a new team (caller becomes Owner). Returns the new team.
    /// Legacy path retained so callers can self-Owner without a
    /// directory lookup; the Platform-Management UI uses
    /// `CreateTeamWithOwner` so a Platform Admin can spin up a team
    /// for someone else. Authenticated callers only; the handler's
    /// `TeamCreationPolicy` gate (default `PlatformAdminOnly`) is the
    /// real enforcement.
    [<RequiresClaim "scope">]
    [<Audit "Custom:TeamCreated">]
    CreateTeam: string -> Async<Result<TeamInfo, string>>
    /// Phase 0.5.x — create a team naming an initial Owner explicitly.
    /// Same `TeamCreationPolicy` gate as `CreateTeam` (default
    /// `PlatformAdminOnly`). The caller is NOT auto-promoted to
    /// Owner; only `request.InitialOwnerUserId` is — pass the
    /// caller's own user id when "self" is the intended owner.
    [<RequiresClaim "scope">]
    [<Audit "Custom:TeamCreated">]
    CreateTeamWithOwner: CreateTeamRequest -> Async<Result<TeamInfo, string>>
    /// Get all teams the current user belongs to. Ungated — callers
    /// without a team store (Anonymous / Individual modes) get `[]`.
    [<AllowAnonymous>]
    GetMyTeams: unit -> Async<TeamInfo list>
    /// Add a member to a team. Requires Owner or Admin role, plus
    /// `TeamRoles.canManageRole callerRole targetRole` — Admins
    /// cannot add other Admins (only Owners can).
    [<RequiresClaim "scope">]
    AddTeamMember: string * string * TeamRole -> Async<Result<unit, string>>
    /// Remove a member from a team. Requires Owner or Admin role,
    /// plus `TeamRoles.canManageRole callerRole targetRole` —
    /// Admins cannot remove other Admins (only Owners can).
    [<RequiresClaim "scope">]
    [<Audit "PermissionRevoked">]
    RemoveTeamMember: string * string -> Async<Result<unit, string>>
    /// Change an existing member's role on a team. Requires Owner or
    /// Admin. Idempotent — no-op when the member already holds the
    /// requested role. Cannot demote the last remaining Owner.
    /// `TeamRoles.canManageRole` gates the caller against BOTH the
    /// old and new roles — Admins cannot promote a Member to Admin
    /// or demote an Admin to Member.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    ChangeMemberRole: string * string * TeamRole -> Async<Result<unit, string>>
    /// List all members of a team. Ungated at the dispatcher — the
    /// handler's leak guard returns `[]` for non-members (same shape
    /// as "no such team", so team existence doesn't leak).
    [<AllowAnonymous>]
    GetTeamMembers: string -> Async<TeamMembership list>
    /// Set the active team for the current user. Ungated at the
    /// dispatcher — the handler verifies target-team membership
    /// before persisting.
    [<AllowAnonymous>]
    SetActiveTeam: string -> Async<Result<unit, string>>
    /// Get the current user's active team.
    [<AllowAnonymous>]
    GetActiveTeam: unit -> Async<string option>
    /// Phase 5f — read the deployment's `TeamCreationPolicy` so the
    /// `TeamManagerUI` can decide whether to render the Create form
    /// for the current caller. Pure read of `ServerConfig.TeamCreationPolicy`
    /// — no auth check. The client pairs the result with
    /// `PlatformAdminApi.IsPlatformAdmin` to decide whether the caller
    /// would clear the server-side gate; the server-side gate inside
    /// `CreateTeam` is the real enforcement.
    [<AllowAnonymous>]
    GetTeamCreationPolicy: unit -> Async<TeamCreationPolicy>
    /// Platform-Admin only — enumerate every team on the deployment
    /// with a membership summary (count + Owner / Admin user ids) for
    /// the Platform-Management admin table. Gated server-side on
    /// `IPlatformAdminStore.IsPlatformAdmin`; non-admin callers receive
    /// `Error "Platform Admin required"` (the client also hides the
    /// surface, but the server gate is the real enforcement).
    [<RequiresClaim "scope">]
    ListAllTeams: unit -> Async<Result<TeamSummary list, string>>
    /// Platform-Admin only — archive a team. Reversible: the team's
    /// data and membership rows are retained, but the team is filtered
    /// out of members' `GetMyTeams` and can no longer be selected as
    /// active; members whose active team this is are bumped to the
    /// no-active-team state. Restore via `RestoreTeam`.
    [<RequiresClaim "scope">]
    [<Audit "Custom:TeamArchived">]
    ArchiveTeam: string -> Async<Result<unit, string>>
    /// Platform-Admin only — restore a previously-archived team,
    /// clearing the archived flag so members can see and select it
    /// again. Idempotent on an already-active team.
    [<RequiresClaim "scope">]
    [<Audit "Custom:TeamRestored">]
    RestoreTeam: string -> Async<Result<unit, string>>
    /// Platform-Admin only — **irreversibly** delete a team: purges the
    /// team record AND strips the team from every member's membership
    /// rows + active-team pointers. Named `DeleteTeamHard` to keep it
    /// distinct from the store's blob-only `DeleteTeam` create-rollback
    /// primitive. The client only offers this on an already-archived
    /// team behind a name-echoing confirm modal; the server applies no
    /// archive precondition (a direct API caller may delete any team),
    /// the admin gate is the enforcement.
    [<RequiresClaim "scope">]
    [<Audit "Custom:TeamDeleted">]
    DeleteTeamHard: string -> Async<Result<unit, string>>
}

/// Team permission management. Owner/Admin only — members receive
/// `Error`. Teams with no permission document configured behave
/// unrestricted (every member can access every module). Admins use
/// these to opt into RBAC for their team. Auto-injected by
/// `SDK.Server.compose`.
type PermissionApi = {
    /// Fetch the full per-team permission document. Owner/Admin only —
    /// members receive `Error`. Returns the empty document when no
    /// permissions are configured (the default on a fresh team).
    [<RequiresClaim "scope">]
    GetTeamPermissions: string -> Async<Result<TeamPermissions, string>>
    /// Set one member's permissions on one module. Pass an empty list
    /// to remove that member's override (falls back to team defaults).
    /// Owner/Admin only.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    SetMemberPermissions: string * string * string * ModulePermission list -> Async<Result<unit, string>>
    /// Replace the team's default per-module permissions — applied to
    /// members who lack an explicit override. Owner/Admin only.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    SetTeamDefaults: string * Map<string, ModulePermission list> -> Async<Result<unit, string>>
    /// Set whether a module is **exposed** in the team's sidebar
    /// (`teamId * moduleId * exposed`). `exposed = false` hides the
    /// module from every member of the team (the "Expose in team"
    /// toggle); `exposed = true` removes it from the hidden set. A
    /// visibility concern, orthogonal to the permission maps. Owner/
    /// Admin only.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    SetModuleExposure: string * string * bool -> Async<Result<unit, string>>
}

/// Sidebar filter helper: the set of modules the platform manages and
/// the subset the caller can access on their active team. Read-only
/// derived view; the canonical permission state lives behind
/// `PermissionApi`. Auto-injected by `SDK.Server.compose`.
type AccessibilityApi = {
    /// Return both the full set of modules the server exposes (and
    /// therefore RBAC-manages) and the subset the current caller can
    /// access on their active team. Used by the client shell to filter
    /// its sidebar:
    ///   - `Managed` is the Id list declared in `ServerConfig.ModuleNames`.
    ///   - `Accessible` is the subset the caller has permission for.
    ///   - Modules whose Id is NOT in `Managed` are SDK-built-ins or
    ///     debug-only modules the server doesn't track; the client
    ///     leaves them visible unconditionally.
    ///   - Modules whose Id IS in `Managed` but not in `Accessible` are
    ///     hidden from the sidebar (the per-module permission guard is
    ///     still the actual enforcement; this is a UX-layer filter).
    ///
    /// Teams without configured permissions (empty
    /// `ModulePermissions`) have every managed module in `Accessible` —
    /// opt-in RBAC preserves today's "everyone sees everything"
    /// behaviour until an admin configures perms.
    /// Ungated by design — anonymous subjects get the full Managed
    /// list (the handler's Anonymous branch).
    [<AllowAnonymous>]
    GetAccessibleModules: unit -> Async<AccessibleModulesResponse>
}

/// Data-catalog query surface. Read-only enumeration of the data
/// types the running platform supports + the modules that produce
/// them. Surfaces in admin UIs and AI tool discovery. Auto-injected
/// by `SDK.Server.compose`.
type DataCatalogApi = {
    /// Return every data type the running platform supports, paired
    /// with the module(s) that produce it and the optional schema.
    /// Surfaces in admin UIs ("what data shapes can this deployment
    /// ingest?") and AI tool discovery. The schema for a given type
    /// rides on `entry.Info.Schema`; modules that haven't documented
    /// their columns leave it `None`.
    [<AllowAnonymous>]
    GetDataCatalog: unit -> Async<DataManagementTypes.DataCatalogResponse>
}