// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 637 — module-visibility admin surface ─────────────────────

/// What an admin caller submits when saving a profile. Deliberately NOT
/// `ModuleVisibilityProfile`: that record carries its own `Scope`, and a
/// client-supplied scope is a caller-controlled pointer at another
/// team's document. The handler derives the scope from the authenticated
/// `AccessContext` and never reads one off the wire — the same posture
/// `IFeatureFlagApi`'s override methods take, and the reason they carry
/// no scope parameter either (GP 4).
type ModuleVisibilityProfileInput = {
    /// The selection over registered module ids.
    Rule: ModuleVisibilityRule
    /// Optional per-page narrowing — composite `{moduleId}{pageRoute}`
    /// ids or bare module ids, per `ModuleVisibilityProfile`.
    ExcludedEntryIds: string list
    /// Free-text operator note recorded with the profile.
    Note: string option
}

/// Client-facing admin API for module-visibility profiles.
///
/// Resolution happens server-side and rides the existing
/// `AccessibleModulesResponse` (see its `VisibilityProfile` field), so
/// the shell needs no extra boot fetch to apply a profile — this record
/// is the ADMIN surface: read the caller's own profile, list what there
/// is to curate, save, clear.
///
/// Mounted only when the deployment opts in via
/// `ServerConfig.ModuleVisibility`; the default `NoModuleVisibility`
/// mounts nothing and the routes 404 (GP 13).
type IModuleVisibilityApi = {
    /// The resolution the server would apply to this caller right now —
    /// `None` on a deployment where no layer declares a profile. Ungated
    /// for the same reason the resolved-flag map is: it describes the
    /// caller's own surface, which they can already observe by looking
    /// at their sidebar.
    [<AllowAnonymous>]
    GetResolvedVisibility: unit -> Async<ModuleVisibilityResolution option>
    /// The profile stored at the caller's admin scope (Team in team
    /// mode, User otherwise), or `None` when that scope declares none.
    /// `Error` when the caller has no admin scope (anonymous).
    [<RequiresClaim "scope">]
    GetProfile: unit -> Async<Result<ModuleVisibilityProfile option, string>>
    /// Every module id this deployment registers, in composition order —
    /// the editor's candidate list. **Derived from the composed module
    /// surface (`ServerConfig.ModuleNames`), never a hand-maintained
    /// list**, so a module added to the composition appears in the
    /// editor without anyone remembering to add it anywhere.
    [<RequiresClaim "scope">]
    ListRegisteredModules: unit -> Async<string list>
    /// Save the profile at the caller's admin scope. In team mode
    /// additionally requires Owner/Admin (`TeamRoles.canWriteTeamConfig`),
    /// mirroring `IFeatureFlagApi.SetOverride`. Emits a
    /// `ModuleVisibilityProfileChanged` audit event.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    SetProfile: ModuleVisibilityProfileInput -> Async<Result<unit, string>>
    /// Remove the profile at the caller's admin scope — the layer stops
    /// contributing and resolution falls back to whatever the outer
    /// scopes declare. Idempotent.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    ClearProfile: unit -> Async<Result<unit, string>>
}

module ModuleVisibilityApi =
    /// Fable.Remoting endpoint prefix. Matches the pattern used by
    /// `PlatformApi`, `IConfigApi`, `IFeatureFlagApi`.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"