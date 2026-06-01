// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Fable.Remoting API surface ─────────────────────────────────

/// Client-facing Fable.Remoting API for reading resolved feature
/// flags. Resolution happens server-side — the client receives the
/// final `FlagValue` per declared key after the scope walk (User →
/// Team → Platform → declared default) and after schema-drift
/// correction. A key is present in the map iff a module (or the
/// platform) declared it at compose time.
///
/// The shell prefetches this once per session start (`FlagsLoaded`
/// message, analogous to `ConfigsLoaded`) and re-inits the active
/// module with the resolved snapshot. Feature-flag writes happen via
/// the admin UI, which invalidates the session's prefetched map on
/// save.
type IFeatureFlagApi = {
    /// Resolved flag map for the caller's `AccessContext`. Keys are
    /// every declared flag for the deployment; values are the result
    /// of `FlagEvaluator.Resolve` per key — already coerced to match
    /// the declared shape (`Bool` / `Variant`).
    [<AllowAnonymous>]
    GetResolvedFlags: unit -> Async<Map<string, FlagValue>>
    /// Flags declared at `register()` / `ServerConfig.FeatureFlags`,
    /// in registration order. Drives the admin UI's flag list and lets
    /// the client surface owner / description / declared default
    /// without re-evaluating.
    [<AllowAnonymous>]
    ListDeclared: unit -> Async<FeatureFlag list>
    /// Overrides currently set at the caller's admin scope (Team for
    /// Team mode, User otherwise). Keyed by flag key. Missing keys mean
    /// the scope has no override — reads fall through to the next
    /// layer. `Error` when the caller has no admin scope (Anonymous).
    [<RequiresClaim "scope">]
    GetOverrides: unit -> Async<Result<Map<string, FlagValue>, string>>
    /// Upsert one override at the caller's admin scope. Validates:
    /// - key is declared (unknown-key writes rejected);
    /// - value shape matches the declared shape (`Bool` vs `Variant`);
    /// - for `Variant`, value is in the declared option list.
    /// In Team mode additionally requires Owner/Admin
    /// (`TeamRoles.canWriteTeamConfig`).
    [<RequiresClaim "scope">]
    SetOverride: string * FlagValue -> Async<Result<unit, string>>
    /// Remove one override at the caller's admin scope — subsequent
    /// reads fall through. Idempotent. In Team mode requires Owner/Admin.
    [<RequiresClaim "scope">]
    ClearOverride: string -> Async<Result<unit, string>>
}

module FeatureFlagApi =
    /// Fable.Remoting endpoint prefix. Matches the pattern used by
    /// `PlatformApi`, `IConfigApi`, etc.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"