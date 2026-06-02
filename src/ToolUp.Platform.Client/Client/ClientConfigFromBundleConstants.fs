// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ClientConfigDefaults

open Fable.Core
open ToolUp.Platform

/// Curated client-side overrides that compose on top of
/// `fromBundleConstants` / `fromBundleConstantValues`. Each `Some`
/// wins over the helper's default; each `None` keeps the
/// reference-app posture. Consumers reach for `ClientConfigOverrides.empty`
/// and set just the fields they care about.
type ClientConfigOverrides = {
    AppName: string option
    AppLogo: string option
    /// Phase 66 Stream B.8 — declared subject shapes this deployment
    /// supports. `Some surfaces` wins over the
    /// `__TOOLUP_PLATFORM_SURFACES__` Vite define; `None` lets the
    /// bundle constant drive (falling back to `Surfaces.anonymous` —
    /// the SDK default — when the constant is empty). Replaces the
    /// retired `Mode: PlatformMode option` field; clean cutover.
    Surfaces: SurfaceProfile list option
    AuthUI: AuthUIMode option
    WebhookAdmin: WebhookAdminMode option
    /// AG Grid module configuration. Consumer typically builds via
    /// `AgGridEnterprise.gridModuleConfig agGridLicense` (consuming
    /// the `BundleConstants.agGridLicense` value).
    GridModules: ToolUp.Platform.AgGrid.AgGridModuleConfig option
    PublicEntryDispatchers: (ClientConfig -> bool) list option
    Handlers: ClientHandlerRegistry option
    /// Reference-app posture: `true` in DEBUG builds, `false` in Release.
    /// Consumer gates via `#if DEBUG` themselves.
    EnableElmishConsoleTrace: bool option
    /// Reference-app posture: `true` in DEBUG builds, `false` in Release.
    ShowDebugOnlyModules: bool option
    /// Reference-app posture: `Some "dev-admin"` in DEBUG builds, `None`
    /// in Release.
    DevDefaultUserId: string option
    ActiveModule: string option
    DataManager: DataManagerMode option
    UsageDashboard: UsageDashboardMode option
    DataIngestionAdmin: DataIngestionAdminMode option
}

module ClientConfigOverrides =
    let empty: ClientConfigOverrides = {
        AppName = None
        AppLogo = None
        Surfaces = None
        AuthUI = None
        WebhookAdmin = None
        GridModules = None
        PublicEntryDispatchers = None
        Handlers = None
        EnableElmishConsoleTrace = None
        ShowDebugOnlyModules = None
        DevDefaultUserId = None
        ActiveModule = None
        DataManager = None
        UsageDashboard = None
        DataIngestionAdmin = None
    }

    /// Reference-app posture knobs that are the same across DEBUG /
    /// Release: webhook admin enabled (pair with
    /// `ServerConfigOverrides.referenceApp`).
    let referenceApp: ClientConfigOverrides = {
        empty with
            WebhookAdmin = Some DefaultWebhookAdmin
    }

let private applyOption defaultValue =
    function
    | Some v -> v
    | None -> defaultValue

/// Phase 66 Stream A.8 client-side counterpart — parse a single token
/// from `__TOOLUP_PLATFORM_SURFACES__` into a `SurfaceProfile`. Mirrors
/// the server-side `parseSurfaceProfile` token vocabulary
/// (`anonymous`, `anonymous_persistent`, `trial`, `individual`, `team`,
/// `multi_team`, `claim_bearer`, plus separator-tolerant aliases).
/// Returns `Error <raw>` for unrecognised tokens so the caller can
/// surface a clear list of bad entries.
let private parseSurfaceProfile (raw: string) : Result<SurfaceProfile, string> =
    match raw with
    | "anonymous" -> Ok SurfaceProfile.anonymous
    | "anonymous_persistent"
    | "anonymous-persistent"
    | "anonymouspersistent" -> Ok SurfaceProfile.anonymousPersistent
    | "trial"
    | "authephemeral"
    | "auth-ephemeral"
    | "auth_ephemeral" -> Ok SurfaceProfile.trial
    | "individual" -> Ok SurfaceProfile.individual
    | "team" -> Ok SurfaceProfile.team
    | "multiteam"
    | "multi-team"
    | "multi_team" -> Ok SurfaceProfile.multiTeam
    | "claimbearer"
    | "claim-bearer"
    | "claim_bearer" -> Ok SurfaceProfile.claimBearer
    | other -> Error other

/// Phase 66 Stream A.8 client-side counterpart — parse the
/// `__TOOLUP_PLATFORM_SURFACES__` raw string into a `SurfaceProfile
/// list`. Empty / whitespace input or any unrecognised token falls
/// back to `fallback` and surfaces a `console.warn`. Mirrors the
/// server-side `fromEnv` resolution semantics.
///
/// Phase 71.A — caller passes the override-record fallback so the
/// env-var-set-but-invalid path lands consistently with the
/// env-var-unset path. Without this parameter, a typo'd
/// `__TOOLUP_PLATFORM_SURFACES__` value would land on the SDK default
/// regardless of override-record posture, contradicting the
/// "consumer overrides beat the bare default" expectation.
let private parseSurfacesString (raw: string) (fallback: SurfaceProfile list) : SurfaceProfile list =
    match raw with
    | null
    | "" -> fallback
    | s ->
        let tokens =
            s.Split([| ','; ';'; ' ' |])
            |> Array.map (fun t -> t.Trim().ToLowerInvariant())
            |> Array.filter (fun t -> t <> "")
            |> Array.toList

        let parsed = tokens |> List.map parseSurfaceProfile

        let errors =
            parsed
            |> List.choose (function
                | Error e -> Some e
                | _ -> None)

        match errors with
        | [] ->
            let resolved =
                parsed
                |> List.choose (function
                    | Ok p -> Some p
                    | _ -> None)

            if List.isEmpty resolved then
                JS.console.warn
                    $"__TOOLUP_PLATFORM_SURFACES__={raw} resolved to an empty surface list. Valid tokens: anonymous, anonymous_persistent, trial, individual, team, multi_team, claim_bearer. Falling back to the library-default override value (or defaults)."

                fallback
            else
                resolved
        | bad ->
            let badList = String.concat ", " bad

            JS.console.warn
                $"__TOOLUP_PLATFORM_SURFACES__={raw} contains unrecognised token(s): {badList}. Valid tokens: anonymous, anonymous_persistent, trial, individual, team, multi_team, claim_bearer. Falling back to the library-default override value (or defaults)."

            fallback

/// Build a `ClientConfig` from the Vite-injected bundle constants
/// (passed as explicit string values) + the overrides record. Use
/// this overload when Fable's `[<Emit>]` propagation across project
/// references is in doubt — pass the values you already read in the
/// consumer's own `[<Emit>]` declarations.
///
/// `ModuleFilter` is `None` when `moduleFilter` is empty (the
/// "no filter" semantics); `Some filter` otherwise.
///
/// Phase 66 Stream B.8 — `platformSurfaces` is the new
/// `__TOOLUP_PLATFORM_SURFACES__` Vite define (comma- / semicolon- /
/// space-separated token list); resolved per
/// `parseSurfacesString` unless `overrides.Surfaces` wins. Mirrors
/// the server-side `TOOLUP_PLATFORM_SURFACES` env var resolution.
///
/// `agGridLicense` and `clerkPublishableKey` are not directly used by
/// this helper — the consumer typically constructs `GridModules =
/// AgGridEnterprise.gridModuleConfig agGridLicense` and `AuthUI =
/// ClerkAuthUI { PublishableKey = clerkPublishableKey }` and supplies
/// them via the overrides record. They are accepted here so a
/// composition root that wants to surface them in a single block
/// can do so without re-`[<Emit>]`-declaring.
let fromBundleConstantValues
    (moduleFilter: string)
    (_agGridLicense: string)
    (_clerkPublishableKey: string)
    (platformSurfaces: string)
    (overrides: ClientConfigOverrides)
    : ClientConfig =
    let normalisedFilter =
        match moduleFilter with
        | null
        | "" -> None
        | s -> Some s

    // Phase 71.A — env-var (Vite define) wins over library-default
    // override-record value. Consumer-authored literals via
    // `{ ClientConfig.defaults with Surfaces = ... }` bypass this
    // helper and still take precedence over both. Mirrors the server
    // side semantics in `ServerConfigOverrides.fromEnv` so a deployment
    // with `TOOLUP_PLATFORM_SURFACES=team` plus
    // `__TOOLUP_PLATFORM_SURFACES__=team` lands consistently on both
    // sides regardless of override-record posture.
    let overridesFallback =
        match overrides.Surfaces with
        | Some s when not (List.isEmpty s) -> s
        | _ -> [ SurfaceProfile.anonymous ]

    let resolvedSurfaces = parseSurfacesString platformSurfaces overridesFallback

    {
        ClientConfig.defaults with
            AppName = overrides.AppName |> applyOption ClientConfig.defaults.AppName
            AppLogo = overrides.AppLogo |> applyOption ClientConfig.defaults.AppLogo
            ActiveModule = overrides.ActiveModule
            Surfaces = resolvedSurfaces
            AuthUI = overrides.AuthUI |> applyOption ClientConfig.defaults.AuthUI
            WebhookAdmin = overrides.WebhookAdmin |> applyOption ClientConfig.defaults.WebhookAdmin
            GridModules = overrides.GridModules |> applyOption ClientConfig.defaults.GridModules
            ModuleFilter = normalisedFilter
            Handlers = overrides.Handlers |> applyOption ClientConfig.defaults.Handlers
            PublicEntryDispatchers =
                overrides.PublicEntryDispatchers
                |> applyOption ClientConfig.defaults.PublicEntryDispatchers
            EnableElmishConsoleTrace =
                overrides.EnableElmishConsoleTrace
                |> applyOption ClientConfig.defaults.EnableElmishConsoleTrace
            ShowDebugOnlyModules =
                overrides.ShowDebugOnlyModules
                |> applyOption ClientConfig.defaults.ShowDebugOnlyModules
            DevDefaultUserId = overrides.DevDefaultUserId
            DataManager = overrides.DataManager |> applyOption ClientConfig.defaults.DataManager
            UsageDashboard = overrides.UsageDashboard |> applyOption ClientConfig.defaults.UsageDashboard
            DataIngestionAdmin =
                overrides.DataIngestionAdmin
                |> applyOption ClientConfig.defaults.DataIngestionAdmin
    }

/// Read the Vite-injected bundle constants via `BundleConstants`
/// and call `fromBundleConstantValues`. Preferred form when Fable's
/// `[<Emit>]` propagation across project references works (the default
/// expectation — verified by `MinimalApp` in this phase's acceptance
/// pass). Consumers unsure should reach for `fromBundleConstantValues` and
/// pass the Vite values explicitly from their own `[<Emit>]` reads.
let fromBundleConstants (overrides: ClientConfigOverrides) : ClientConfig =
    fromBundleConstantValues
        BundleConstants.moduleFilter
        BundleConstants.agGridLicense
        BundleConstants.clerkPublishableKey
        BundleConstants.platformSurfaces
        overrides