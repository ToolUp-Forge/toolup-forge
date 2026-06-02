// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// RBAC permission on a single module. Hierarchy is encoded in
/// `hasPermission` — `Admin` implies `Write` and `Read`; `Write`
/// implies `Read`; `Read` stands alone. Users may be granted any
/// combination; the helpers normalise the hierarchy when checking.
///
/// `SchemaOnly` is the Phase 30d substrate role: the holder may call
/// `IDataCatalog.GetSchema` / `GetSyntheticSample` to see what data
/// exists and iterate against synthetic samples, but every real-row
/// read path is structurally refused. Outside the read-side hierarchy
/// (`SchemaOnly` does NOT imply `Read`, and `Read` does NOT imply
/// `SchemaOnly`) — the two grants describe different access intents
/// and a partner who is given `SchemaOnly` must not silently inherit
/// real-data access by being later granted `Read`.
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
    /// Phase 30d — partner-sandbox grant. The holder can call
    /// `IDataCatalog.GetSchema` + `GetSyntheticSample` to discover data
    /// shapes and iterate against deterministically-generated synthetic
    /// rows, but every path that would return a real-row blob is
    /// refused with a `SchemaOnlyAccessAttempted` audit event. Does
    /// NOT imply `Read` — a partner who acquires real-data access must
    /// be granted `Read` explicitly. Intended for federated cross-
    /// instance partner tenants and any deployment that wants to expose
    /// "what data exists" without exposing "what's in it".
    | SchemaOnly

module ModulePermission =
    /// Does holding `granted` satisfy a requirement of `required`?
    /// Encodes the Read / Write / Admin hierarchy plus the
    /// Phase 30d `SchemaOnly` carve-out. `Admin` / `Write` / `Read` all
    /// satisfy a `SchemaOnly` requirement (more authority covers less —
    /// any real-data reader can trivially see schemas + synthetic
    /// samples). The reverse is structurally blocked: `SchemaOnly` does
    /// NOT satisfy `Read` / `Write` / `Admin`, so a partner whose only
    /// grant is `SchemaOnly` cannot inherit real-data access.
    let implies (granted: ModulePermission) (required: ModulePermission) =
        match granted, required with
        | ModulePermission.Admin, _ -> true
        | ModulePermission.Write, ModulePermission.Write
        | ModulePermission.Write, ModulePermission.Read
        | ModulePermission.Write, ModulePermission.SchemaOnly -> true
        | ModulePermission.Read, ModulePermission.Read
        | ModulePermission.Read, ModulePermission.SchemaOnly -> true
        | ModulePermission.SchemaOnly, ModulePermission.SchemaOnly -> true
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