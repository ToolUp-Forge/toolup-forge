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

/// Per-team, per-module **exposure** state — the tri-state behind the
/// team-management "module exposure" control. Orthogonal to the RBAC
/// permission maps: exposure governs *whether the module is offered to
/// the team at all*; permission governs *what a member may do once it
/// is*. Absence from `TeamPermissions.Exposure` ⇒ `Available` (the
/// default, so a brand-new team and every pre-exposure persisted
/// document show every module).
///
/// `Available` and `Hidden` remain pure visibility/navigation states
/// (NOT an authorization boundary — the per-route permission guard
/// `canAccessModule` / `hasPermission` is the enforcement). `Hidden`
/// preserves the legacy "Expose in team" off behaviour: the module
/// leaves the sidebar + Home but its data types stay mappable.
/// `Unavailable` is the stronger *clearance* state: the module leaves
/// the sidebar + Home AND its data types are no longer offered for
/// mapping ("this team isn't cleared to use it").
///
/// Extensible — a future "visible-but-locked upsell" state is simply a
/// 4th case rather than a second orthogonal flag.
[<RequireQualifiedAccess>]
type ModuleExposure =
    /// Default. In the sidebar + Home; data types mappable.
    | Available
    /// Cosmetically hidden — off the sidebar + Home, but data types
    /// stay mappable. The legacy "Expose in team" off state.
    | Hidden
    /// Not cleared for this team — off the sidebar + Home, AND its data
    /// types are refused for mapping/detection. Upload still succeeds.
    | Unavailable

module ModuleExposure =
    /// Stable wire token for persistence + audit. `Available` is never
    /// serialised (absence ⇒ Available), so it has no token of its own
    /// on the write path; included here for completeness.
    let toToken =
        function
        | ModuleExposure.Available -> "available"
        | ModuleExposure.Hidden -> "hidden"
        | ModuleExposure.Unavailable -> "unavailable"

    /// Parse a persisted/legacy token. Unknown tokens fall back to the
    /// safe-but-visible `Available` default rather than throwing — a
    /// malformed document must not strand a team with no modules.
    let ofToken (token: string) =
        match token.Trim().ToLowerInvariant() with
        | "hidden" -> ModuleExposure.Hidden
        | "unavailable" -> ModuleExposure.Unavailable
        | _ -> ModuleExposure.Available

    /// Whether the module is shown in the sidebar + Home. Only
    /// `Available` is exposed; `Hidden` and `Unavailable` are not.
    let isExposed =
        function
        | ModuleExposure.Available -> true
        | ModuleExposure.Hidden
        | ModuleExposure.Unavailable -> false

    /// Whether the module's data types may be mapped/detected. Both
    /// `Available` and `Hidden` are mappable; only `Unavailable` blocks.
    let isMappable =
        function
        | ModuleExposure.Available
        | ModuleExposure.Hidden -> true
        | ModuleExposure.Unavailable -> false

/// Persisted per-team permission document. One per team, stored under
/// `_platform/permissions/{teamId}.json` by the blob-backed
/// `PermissionStore`.
///
/// `Defaults` are applied when a member has no explicit per-module
/// entry. `Members` maps userId → moduleName → permissions.
/// Effective permissions for a user on a module: `Members[userId][module]`
/// if present, else `Defaults[module]`, else no access.
///
/// `Exposure` is the **per-team module-exposure** axis (see
/// `ModuleExposure`), orthogonal to the permission maps above. A module
/// absent from the map is `Available`; entries record the non-default
/// `Hidden` / `Unavailable` states. A `Hidden` / `Unavailable` module
/// is removed from the team's sidebar + Home for every member (and for
/// a platform admin acting on the team), regardless of permission
/// level; `Unavailable` additionally blocks data mapping. Exposure is a
/// navigation/availability concern, NOT the per-route authorization
/// boundary.
///
/// Lives in the shared compilation layer because the client-facing
/// `PlatformApi` exposes it — team admins read and edit it from the
/// team-management UI.
type TeamPermissions = {
    Defaults: Map<string, ModulePermission list>
    Members: Map<string, Map<string, ModulePermission list>>
    /// Per-module exposure state. A module absent from the map is
    /// `Available` (default); entries hold the non-default `Hidden` /
    /// `Unavailable` states. See the `ModuleExposure` doc for the
    /// exposure-vs-permission distinction.
    Exposure: Map<string, ModuleExposure>
}

module TeamPermissions =
    let empty = {
        Defaults = Map.empty
        Members = Map.empty
        Exposure = Map.empty
    }