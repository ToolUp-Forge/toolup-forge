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
    /// Display name for the new team.
    Name: string
    /// Initial Owner's user id. The named user is added as the
    /// team's `Owner` immediately after the team row is minted; if
    /// they haven't signed in yet, the membership is still attached
    /// to the user id (server-side substrate has no precondition on
    /// the IdP having seen them — the pending-invite substrate is
    /// the recommended path when the recipient is identified by
    /// email only).
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
    /// for someone else.
    CreateTeam: string -> Async<Result<TeamInfo, string>>
    /// Phase 0.5.x — create a team naming an initial Owner explicitly.
    /// Same `TeamCreationPolicy` gate as `CreateTeam` (default
    /// `PlatformAdminOnly`). The caller is NOT auto-promoted to
    /// Owner; only `request.InitialOwnerUserId` is — pass the
    /// caller's own user id when "self" is the intended owner.
    CreateTeamWithOwner: CreateTeamRequest -> Async<Result<TeamInfo, string>>
    /// Get all teams the current user belongs to.
    GetMyTeams: unit -> Async<TeamInfo list>
    /// Add a member to a team. Requires Owner or Admin role, plus
    /// `TeamRoles.canManageRole callerRole targetRole` — Admins
    /// cannot add other Admins (only Owners can).
    AddTeamMember: string * string * TeamRole -> Async<Result<unit, string>>
    /// Remove a member from a team. Requires Owner or Admin role,
    /// plus `TeamRoles.canManageRole callerRole targetRole` —
    /// Admins cannot remove other Admins (only Owners can).
    RemoveTeamMember: string * string -> Async<Result<unit, string>>
    /// Change an existing member's role on a team. Requires Owner or
    /// Admin. Idempotent — no-op when the member already holds the
    /// requested role. Cannot demote the last remaining Owner.
    /// `TeamRoles.canManageRole` gates the caller against BOTH the
    /// old and new roles — Admins cannot promote a Member to Admin
    /// or demote an Admin to Member.
    ChangeMemberRole: string * string * TeamRole -> Async<Result<unit, string>>
    /// List all members of a team.
    GetTeamMembers: string -> Async<TeamMembership list>
    /// Set the active team for the current user.
    SetActiveTeam: string -> Async<Result<unit, string>>
    /// Get the current user's active team.
    GetActiveTeam: unit -> Async<string option>
    /// Phase 5f — read the deployment's `TeamCreationPolicy` so the
    /// `TeamManagerUI` can decide whether to render the Create form
    /// for the current caller. Pure read of `ServerConfig.TeamCreationPolicy`
    /// — no auth check. The client pairs the result with
    /// `PlatformAdminApi.IsPlatformAdmin` to decide whether the caller
    /// would clear the server-side gate; the server-side gate inside
    /// `CreateTeam` is the real enforcement.
    GetTeamCreationPolicy: unit -> Async<TeamCreationPolicy>
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
    GetTeamPermissions: string -> Async<Result<TeamPermissions, string>>
    /// Set one member's permissions on one module. Pass an empty list
    /// to remove that member's override (falls back to team defaults).
    /// Owner/Admin only.
    SetMemberPermissions: string * string * string * ModulePermission list -> Async<Result<unit, string>>
    /// Replace the team's default per-module permissions — applied to
    /// members who lack an explicit override. Owner/Admin only.
    SetTeamDefaults: string * Map<string, ModulePermission list> -> Async<Result<unit, string>>
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
    GetDataCatalog: unit -> Async<DataManagementTypes.DataCatalogResponse>
}