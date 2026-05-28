// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Admin-UI payloads ───────────────────────────────────────────

/// A module entry returned by `ListModules` — paired with its schema
/// so the admin UI can render a per-module form without a second
/// round-trip per module. Order matches the deployment's registration
/// order (whatever the app passed in `ServerConfig.ModuleConfigs`);
/// the UI is free to sort by `DisplayName` at render time.
///
/// The reserved `ModuleKey = "_platform"` entry, when present, carries
/// the platform-level schema (locale, timezone, date format). The UI
/// treats it like any other module — a team admin just sees
/// "Platform" as another tab.
type ModuleConfigEntry = {
    /// Stable identifier used as the store's `moduleKey`. Matches
    /// either a module's `Definition.Id` or the reserved
    /// `ConfigKeys.PlatformModuleKey`.
    ModuleKey: string
    /// Human-readable label shown as the tab / section header in the
    /// admin UI. The SDK defaults to the module's `DisplayName`; the
    /// app may override when it registers the schema.
    DisplayName: string
    /// Field definitions driving form rendering.
    Schema: ModuleConfigSchema
}

/// A full view of one module's config — both the persisted per-field
/// map and the schema — so the admin UI can render without computing
/// effective values server-side. Missing fields fall back to
/// `Schema.Fields[k].DefaultJson` in the form.
type ModuleConfigView = {
    ModuleKey: string
    Schema: ModuleConfigSchema
    /// Persisted JSON-per-field map. Empty when no document exists.
    Values: Map<string, string>
}

// ─── Fable.Remoting API surface ─────────────────────────────────

/// Admin-facing Fable.Remoting API for inspecting and editing
/// per-scope module configuration. Auto-injected by `compose` in all
/// modes — the handler gates writes behind `TeamRoles.canWriteTeamConfig`
/// in Team mode (Owner/Admin only) and short-circuits Anonymous mode
/// (no persistent config, same as the canonical `IProviderProfile`).
///
/// Reads are ungated within a scope: a team member can see the team
/// config; a user can see their own. Cross-scope reads are impossible
/// — the handler resolves the caller's scope from `AccessContext`
/// and never reads another scope.
type IConfigApi = {
    /// Enumerate configurable modules for the deployment. Every entry
    /// carries its schema so the UI can render forms inline.
    ListModules: unit -> Async<ModuleConfigEntry list>
    /// Full view of one module — schema + persisted map. Returns
    /// `Error` when the caller has no config scope (Anonymous mode),
    /// or when the key is not registered.
    GetModuleConfig: string -> Async<Result<ModuleConfigView, string>>
    /// Replace the persisted map for one module. Validated against
    /// the schema server-side. Team mode: Owner/Admin only.
    SaveModuleConfig: string * Map<string, string> -> Async<Result<unit, string>>
    /// Clear the persisted map for one module (all fields fall back
    /// to schema defaults on next read). Team mode: Owner/Admin only.
    ClearModuleConfig: string -> Async<Result<unit, string>>
}

module ConfigApi =
    /// Fable.Remoting endpoint prefix. Matches the pattern used by
    /// `PlatformApi`, `AISettingsApi`, etc.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"