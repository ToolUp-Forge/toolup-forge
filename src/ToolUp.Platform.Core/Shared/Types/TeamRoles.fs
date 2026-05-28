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