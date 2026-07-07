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
            |> Array.map _.Trim().ToLowerInvariant()
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
/// ProviderAuthUI ("clerk", box { PublishableKey = clerkPublishableKey })`
/// (via the ClerkUI companion's `ClerkRegister.authUI` smart
/// constructor) and supplies them via the overrides record. They are
/// accepted here so a composition root that wants to surface them in
/// a single block can do so without re-`[<Emit>]`-declaring.
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

/// Phase 71.A.10 — fold the brand-string Vite-define values into the
/// overrides record with Vite-define > override > default precedence
/// (the bundle constant, when present, wins over the library-default
/// override-record value, mirroring the server-side `fromEnv` brand
/// lifts). Pure / explicit-values form so it's unit-testable without
/// the `jsNative` `BundleConstants` reads. A `None` constant leaves the
/// override-record value untouched, so the eventual
/// `fromBundleConstantValues` resolution still applies override > default.
let foldBrandConstants
    (appName: string option)
    (appLogo: string option)
    (activeModule: string option)
    (devDefaultUserId: string option)
    (enableElmishConsoleTrace: bool option)
    (showDebugOnlyModules: bool option)
    (overrides: ClientConfigOverrides)
    : ClientConfigOverrides =
    {
        overrides with
            AppName = appName |> Option.orElse overrides.AppName
            AppLogo = appLogo |> Option.orElse overrides.AppLogo
            ActiveModule = activeModule |> Option.orElse overrides.ActiveModule
            DevDefaultUserId = devDefaultUserId |> Option.orElse overrides.DevDefaultUserId
            EnableElmishConsoleTrace = enableElmishConsoleTrace |> Option.orElse overrides.EnableElmishConsoleTrace
            ShowDebugOnlyModules = showDebugOnlyModules |> Option.orElse overrides.ShowDebugOnlyModules
    }

/// Phase 71.A.9 — parse a `No*` / `Default*` admin-module case-flip.
/// `no`/`off`/`disabled`/`none` → the disabled case; `default`/`on`/
/// `enabled` → the default case; anything else (unset / unrecognised /
/// a `Configured`/`External`-shaped token) → `None`, so the caller keeps
/// the config's existing value.
let parseNoDefault (raw: string) (noVal: 'T) (defaultVal: 'T) : 'T option =
    match (if isNull raw then "" else raw.ToLowerInvariant()) with
    | "no"
    | "off"
    | "disabled"
    | "none" -> Some noVal
    | "default"
    | "on"
    | "enabled" -> Some defaultVal
    | _ -> None

/// Phase 71.A.9 — parse a flat nilary DU from an explicit token map.
let parseCaseToken (raw: string) (cases: (string * 'T) list) : 'T option =
    let r = if isNull raw then "" else raw.ToLowerInvariant()
    cases |> List.tryPick (fun (tok, v) -> if tok = r then Some v else None)

/// Phase 71.A.9 — fold the client admin-module / profile Vite-define
/// values onto an already-built `ClientConfig` (Vite > default; these
/// fields carry no override-record member). Only the payload-free
/// case-flip lifts — `Configured*` / `External*` / `Custom*` cases carry
/// function values and stay compile-time, so an empty / unrecognised
/// define leaves the config's existing value untouched. Pure /
/// explicit-values form so it's unit-testable without the `jsNative`
/// `BundleConstants` reads.
let applyAdminModuleConstants
    (teamManager: string)
    (teamConfig: string)
    (platformAdmin: string)
    (permissionsAdmin: string)
    (healthMonitor: string)
    (serviceStatusBoard: string)
    (dataSubjectRequestAdmin: string)
    (toastCentre: string)
    (platformAdminProfile: string)
    (inputsPaneWidth: string)
    (config: ClientConfig)
    : ClientConfig =
    {
        config with
            TeamManager =
                parseNoDefault teamManager NoTeamManager DefaultTeamManager
                |> Option.defaultValue config.TeamManager
            TeamConfig =
                parseNoDefault teamConfig NoTeamConfig DefaultTeamConfig
                |> Option.defaultValue config.TeamConfig
            PlatformAdmin =
                parseNoDefault platformAdmin NoPlatformAdmin DefaultPlatformAdmin
                |> Option.defaultValue config.PlatformAdmin
            PermissionsAdmin =
                parseNoDefault permissionsAdmin NoPermissionsAdmin DefaultPermissionsAdmin
                |> Option.defaultValue config.PermissionsAdmin
            HealthMonitor =
                parseNoDefault healthMonitor NoHealthMonitor DefaultHealthMonitor
                |> Option.defaultValue config.HealthMonitor
            ServiceStatusBoard =
                parseNoDefault serviceStatusBoard NoServiceStatusBoard DefaultServiceStatusBoard
                |> Option.defaultValue config.ServiceStatusBoard
            DataSubjectRequestAdmin =
                parseNoDefault dataSubjectRequestAdmin NoDataSubjectRequestAdmin DefaultDataSubjectRequestAdmin
                |> Option.defaultValue config.DataSubjectRequestAdmin
            ToastCentre =
                parseNoDefault toastCentre NoToastCentre DefaultToastCentre
                |> Option.defaultValue config.ToastCentre
            PlatformAdminProfile =
                parseCaseToken platformAdminProfile [
                    "standard", StandardPlatformAdminProfile
                    "publicutility", PublicUtilityPlatformAdminProfile
                    "public-utility", PublicUtilityPlatformAdminProfile
                ]
                |> Option.defaultValue config.PlatformAdminProfile
            InputsPaneWidth =
                parseCaseToken inputsPaneWidth [ "narrow", Narrow; "wide", Wide; "auto", Auto ]
                |> Option.defaultValue config.InputsPaneWidth
    }

/// Phase 71.A.11 — fold the hybrid client toggles onto an already-built
/// `ClientConfig`. Only the nilary `No*` case is env-selectable
/// (`no`/`off`/`disabled`); the `EnabledAdPanel` / `FundingChoicesConsent`
/// / `CustomConsentProvider` cases carry a structured config / id string
/// that must be supplied in code, so any non-off token leaves the
/// config's existing value untouched. Pure / explicit-values form.
let applyHybridClientConstants (adPanel: string) (consentProvider: string) (config: ClientConfig) : ClientConfig =
    let offTokensAdPanel = [ "no", NoAdPanel; "off", NoAdPanel; "disabled", NoAdPanel ]

    let offTokensConsent = [
        "no", NoConsentProvider
        "off", NoConsentProvider
        "disabled", NoConsentProvider
    ]

    {
        config with
            AdPanel = parseCaseToken adPanel offTokensAdPanel |> Option.defaultValue config.AdPanel
            ConsentProvider =
                parseCaseToken consentProvider offTokensConsent
                |> Option.defaultValue config.ConsentProvider
    }

/// Read the Vite-injected bundle constants via `BundleConstants`
/// and call `fromBundleConstantValues`. Preferred form when Fable's
/// `[<Emit>]` propagation across project references works (the default
/// expectation — verified by `MinimalApp` in this phase's acceptance
/// pass). Consumers unsure should reach for `fromBundleConstantValues` and
/// pass the Vite values explicitly from their own `[<Emit>]` reads.
///
/// Phase 71.A.10 — the brand-string Vite defines (`__TOOLUP_APP_NAME__`,
/// `__TOOLUP_APP_LOGO__`, `__TOOLUP_ACTIVE_MODULE__`,
/// `__TOOLUP_DEV_DEFAULT_USER_ID__`, `__TOOLUP_ENABLE_ELMISH_TRACE__`,
/// `__TOOLUP_SHOW_DEBUG_MODULES__`) are folded in here with
/// Vite > override > default precedence, so a container-deploy re-brands
/// without a Fable recompile. The `fromBundleConstantValues` signature is
/// deliberately left unchanged (emit-in-doubt consumers keep passing
/// brand values via their own overrides).
let fromBundleConstants (overrides: ClientConfigOverrides) : ClientConfig =
    let withBrandLifts =
        foldBrandConstants
            BundleConstants.appName
            BundleConstants.appLogo
            BundleConstants.activeModule
            BundleConstants.devDefaultUserId
            BundleConstants.enableElmishConsoleTrace
            BundleConstants.showDebugOnlyModules
            overrides

    fromBundleConstantValues
        BundleConstants.moduleFilter
        BundleConstants.agGridLicense
        BundleConstants.clerkPublishableKey
        BundleConstants.platformSurfaces
        withBrandLifts
    // Phase 71.A.9 — fold the admin-module / profile flat-case toggles
    // on top (Vite > default; no override-record member for these).
    |> applyAdminModuleConstants
        BundleConstants.teamManager
        BundleConstants.teamConfig
        BundleConstants.platformAdmin
        BundleConstants.permissionsAdmin
        BundleConstants.healthMonitor
        BundleConstants.serviceStatusBoard
        BundleConstants.dataSubjectRequestAdmin
        BundleConstants.toastCentre
        BundleConstants.platformAdminProfile
        BundleConstants.inputsPaneWidth
    // Phase 71.A.11 — hybrid client toggles (off-direction only).
    |> applyHybridClientConstants BundleConstants.adPanel BundleConstants.consentProvider