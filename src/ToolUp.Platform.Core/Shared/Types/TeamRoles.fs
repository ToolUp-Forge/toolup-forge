// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Permission predicates over `TeamRole`. Consolidates the
/// role-to-capability mapping so call sites use named helpers rather
/// than ad-hoc `match role with Owner | Admin -> ...` expressions
/// scattered across the codebase. Adding new permissions
/// (fine-grained RBAC, the job-scheduler admin surface, …) is a
/// one-place change here.
///
/// Lives in Shared so server-side handlers and Fable-compiled client
/// views can both use the same predicates — the client can grey out a
/// button via `TeamRoles.canWriteTeamConfig role` the same way the
/// server rejects a request.
module TeamRoles =

    /// Whether the role can write scope-level configuration — AI
    /// provider keys, module settings, future team-profile fields.
    /// Owner and Admin can; Member is read-only. Used by
    /// `AISettingsHandler` to gate team-scope mutations.
    let canWriteTeamConfig (role: TeamRole) =
        match role with
        | Owner
        | Admin -> true
        | Member -> false

    /// Whether the role can add/remove team members or change member
    /// roles. Owner and Admin. Matches the existing gate in
    /// `SDK.Server.compose` around `AddTeamMember` / `RemoveTeamMember`.
    let canManageMembers (role: TeamRole) =
        match role with
        | Owner
        | Admin -> true
        | Member -> false

    /// Fine-grained predicate: can `callerRole` add / remove / change-role
    /// targeting `targetRole`?
    ///
    /// Rules:
    ///   * Owner can manage Members and Admins (assign or unassign
    ///     either; the Owner role itself is set once at team-creation
    ///     time via `CreateTeamWithOwner` and is not transferable via
    ///     the team-admin surface — that's a separate ownership-transfer
    ///     concern).
    ///   * Admin can manage Members only — promoting a Member to Admin
    ///     or demoting an Admin to Member requires Owner.
    ///   * Member cannot manage anyone.
    ///
    /// Server-side, both `Add` and `Remove` gate on
    /// `canManageRole callerRole targetRole`. `ChangeMemberRole` gates
    /// on `canManageRole` against BOTH the old role (you must be
    /// authorised to demote that role) AND the new role (you must be
    /// authorised to assign that role). Platform Admins bypass these
    /// gates entirely server-side via the `IPlatformAdminStore`
    /// short-circuit.
    let canManageRole (callerRole: TeamRole) (targetRole: TeamRole) =
        match callerRole, targetRole with
        | Owner, _ -> true
        | Admin, Member -> true
        | Admin, _
        | Member, _ -> false

    /// Whether the role is specifically Owner — for operations that
    /// are irreversible from the team's perspective (deleting the
    /// team, transferring ownership). Admins cannot perform these.
    let isOwner (role: TeamRole) =
        match role with
        | Owner -> true
        | Admin
        | Member -> false

    /// Human-readable role name for UI labels and log messages.
    let displayName (role: TeamRole) =
        match role with
        | Owner -> "Owner"
        | Admin -> "Admin"
        | Member -> "Member"