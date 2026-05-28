// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// RBAC permission on a single module. Hierarchy is encoded in
/// `hasPermission` — `Admin` implies `Write` and `Read`; `Write`
/// implies `Read`; `Read` stands alone. Users may be granted any
/// combination; the helpers normalise the hierarchy when checking.
///
/// `RequireQualifiedAccess` is mandatory because `Admin` collides with
/// `TeamRole.Admin` (different concept: module-level perm vs team
/// membership role). Forcing `ModulePermission.Admin` at call sites
/// keeps the two kinds of admin distinct.
[<RequireQualifiedAccess>]
type ModulePermission =
    /// View module data and call read-only methods. The minimum grant.
    | Read
    /// Read + mutate module data (invoke analysis, upload files,
    /// delete records).
    | Write
    /// Read + Write + module-scoped administrative actions (configure
    /// module defaults, manage per-module resources). Does not imply
    /// team-scope admin — that's `TeamRole.Admin`.
    | Admin

module ModulePermission =
    /// Does holding `granted` satisfy a requirement of `required`?
    /// Encodes the Read / Write / Admin hierarchy.
    let implies (granted: ModulePermission) (required: ModulePermission) =
        match granted, required with
        | ModulePermission.Admin, _ -> true
        | ModulePermission.Write, ModulePermission.Write
        | ModulePermission.Write, ModulePermission.Read -> true
        | ModulePermission.Read, ModulePermission.Read -> true
        | _ -> false

/// Persisted per-team permission document. One per team, stored under
/// `_platform/permissions/{teamId}.json` by the blob-backed
/// `PermissionStore`.
///
/// `Defaults` are applied when a member has no explicit per-module
/// entry. `Members` maps userId → moduleName → permissions.
/// Effective permissions for a user on a module: `Members[userId][module]`
/// if present, else `Defaults[module]`, else no access.
///
/// Lives in the shared compilation layer because the client-facing
/// `PlatformApi` exposes it — team admins read and edit it from the
/// team-management UI.
type TeamPermissions = {
    Defaults: Map<string, ModulePermission list>
    Members: Map<string, Map<string, ModulePermission list>>
}

module TeamPermissions =
    let empty = {
        Defaults = Map.empty
        Members = Map.empty
    }