// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BundleConstants

open Fable.Core

// Phase 11.G — typed accessors for the three Vite-injected `define`
// constants the reference deployment relies on. Lifting them into the
// SDK saves every consumer composition root from re-declaring three
// `[<Emit>]` `let` bindings. The `typeof X === 'string' ? X : ''`
// guard means consumers whose Vite config doesn't define the constant
// see an empty string instead of a runtime `ReferenceError` — falls
// back gracefully for any deployment that hasn't (yet) wired the
// Vite `define`.
//
// **Open question (Phase 11.G acceptance — verify in MinimalApp):**
// Fable's `[<Emit>]` propagation across `<ProjectReference>` is
// load-bearing here. If a Fable consumer that takes
// `ToolUp.Platform.Client` via `<ProjectReference>` does NOT see these
// Vite-defined constants substituted (the emit-output uses the literal
// `__TOOLUP_MODULE__` token at runtime), consumers will need to keep
// their own three `[<Emit>]` declarations and pass the resolved values
// to `ClientConfig.fromBundleConstantValues`. The `MinimalApp` sample
// (`toolup-forge/samples/MinimalApp/`) verifies this empirically at
// the end of Phase 11.G.

/// Read from the `__TOOLUP_MODULE__` Vite define (case-insensitive
/// substring filter over `ErasedModule.Definition.Name`). Empty
/// string when the define isn't wired — equivalent to "no filter".
[<Emit("(typeof __TOOLUP_MODULE__ === 'string' ? __TOOLUP_MODULE__ : '')")>]
let moduleFilter: string = jsNative

/// Read from the `__AG_GRID_LICENSE__` Vite define. Empty string
/// when unset — AG Grid Enterprise components then render "License
/// Required" overlays but the app still boots in Community-tier.
[<Emit("(typeof __AG_GRID_LICENSE__ === 'string' ? __AG_GRID_LICENSE__ : '')")>]
let agGridLicense: string = jsNative

/// Read from the `__CLERK_PUBLISHABLE_KEY__` Vite define. Empty
/// string when unset — Release builds wiring `ClerkAuthUI` should
/// fail loud rather than silently fall through to anonymous mode.
[<Emit("(typeof __CLERK_PUBLISHABLE_KEY__ === 'string' ? __CLERK_PUBLISHABLE_KEY__ : '')")>]
let clerkPublishableKey: string = jsNative

/// Phase 66 Stream A.8 client-side counterpart — read from the
/// `__TOOLUP_PLATFORM_SURFACES__` Vite define. Comma- / semicolon- /
/// space-separated token list matching the server-side
/// `TOOLUP_PLATFORM_SURFACES` env var (valid tokens: `anonymous`,
/// `anonymous_persistent`, `trial`, `individual`, `team`, `multi_team`,
/// `claim_bearer`). Empty string when unset — `ClientConfigOverrides`
/// then falls back to `Surfaces.anonymous` (the SDK default). Replaces
/// the retired `__TOOLUP_PLATFORM_MODE__` define (clean cutover; no
/// aliasing).
[<Emit("(typeof __TOOLUP_PLATFORM_SURFACES__ === 'string' ? __TOOLUP_PLATFORM_SURFACES__ : '')")>]
let platformSurfaces: string = jsNative

/// Phase 58 — paired with `ServerConfig.Notifications =
/// NoNotificationsExplicit`. When the consumer's Vite config defines
/// `__TOOLUP_NOTIFICATIONS_DISABLED__ = true`, the client's
/// `NotificationClient` skips EventSource instantiation entirely
/// — no `/api/notifications` request, no 404 retry loop, no console
/// warning. Absent / `false` keeps today's behaviour (EventSource
/// is created on first `subscribe`; if the server has no route mounted
/// the defensive 404-fallback in `NotificationClient.onError` is what
/// catches the silent-default).
[<Emit("(typeof __TOOLUP_NOTIFICATIONS_DISABLED__ === 'boolean' ? __TOOLUP_NOTIFICATIONS_DISABLED__ : false)")>]
let notificationsDisabledExplicitly: bool = jsNative

// Phase 16e — typed accessors for the Docker-era build-time-injected
// set. As container-mode deploys roll across siblings (Phase 16b's
// `ToolUp.Hosts.Docker` companion), build-time injection of Entra
// tenant / client IDs + per-deployment OIDC overrides becomes the
// universal pattern. The four accessors below replace per-consumer
// `[<Emit>]` boilerplate and centralise the Vite-define ↔ F#-accessor
// mapping. Shape divergence from the Phase 11.G set above is
// intentional: each returns `string option`, with `None` covering
// three failure modes — the define is the empty string, the literal
// JS `'undefined'` string (what Vite emits when a `process.env.X`
// substitution finds nothing), or the literal placeholder
// `__NAME__` (what survives in the bundle when Vite didn't substitute
// at all). Consumers gate fail-loud on `None` rather than on
// `String.IsNullOrEmpty` against a raw `string`.

[<Emit("(typeof __ENTRA_TENANT_ID__ === 'string' && __ENTRA_TENANT_ID__ !== '' && __ENTRA_TENANT_ID__ !== 'undefined' && __ENTRA_TENANT_ID__ !== '__ENTRA_TENANT_ID__' ? __ENTRA_TENANT_ID__ : '')")>]
let private entraTenantIdRaw: string = jsNative

[<Emit("(typeof __ENTRA_CLIENT_ID__ === 'string' && __ENTRA_CLIENT_ID__ !== '' && __ENTRA_CLIENT_ID__ !== 'undefined' && __ENTRA_CLIENT_ID__ !== '__ENTRA_CLIENT_ID__' ? __ENTRA_CLIENT_ID__ : '')")>]
let private entraClientIdRaw: string = jsNative

[<Emit("(typeof __OIDC_ISSUER_OVERRIDE__ === 'string' && __OIDC_ISSUER_OVERRIDE__ !== '' && __OIDC_ISSUER_OVERRIDE__ !== 'undefined' && __OIDC_ISSUER_OVERRIDE__ !== '__OIDC_ISSUER_OVERRIDE__' ? __OIDC_ISSUER_OVERRIDE__ : '')")>]
let private oidcIssuerOverrideRaw: string = jsNative

[<Emit("(typeof __OIDC_AUDIENCE_OVERRIDE__ === 'string' && __OIDC_AUDIENCE_OVERRIDE__ !== '' && __OIDC_AUDIENCE_OVERRIDE__ !== 'undefined' && __OIDC_AUDIENCE_OVERRIDE__ !== '__OIDC_AUDIENCE_OVERRIDE__' ? __OIDC_AUDIENCE_OVERRIDE__ : '')")>]
let private oidcAudienceOverrideRaw: string = jsNative

let private toOption (raw: string) : string option =
    if System.String.IsNullOrEmpty raw then None else Some raw

/// Read from the `__ENTRA_TENANT_ID__` Vite define. `None` when the
/// define is unset, empty, the literal `'undefined'` string, or the
/// literal placeholder `__ENTRA_TENANT_ID__` (i.e. Vite didn't
/// substitute). Consumers wire Microsoft Entra Workforce ID via
/// `OidcAuthUI` and pattern-match `Some tenantId` for fail-loud
/// behaviour in Release builds.
let entraTenantId: string option = toOption entraTenantIdRaw

/// Read from the `__ENTRA_CLIENT_ID__` Vite define. Same `None`
/// semantics as `entraTenantId`. Paired with it in
/// `OidcUIConfig.defaults` + the `api://{clientId}/access_as_user`
/// scope.
let entraClientId: string option = toOption entraClientIdRaw

/// Read from the `__OIDC_ISSUER_OVERRIDE__` Vite define. `None` when
/// the consumer accepts the SDK / IdP-default issuer URL. Some IdPs
/// (multi-region Auth0 / Okta, sovereign-cloud Entra) require a
/// non-default issuer baked at build time; this accessor lets
/// `OidcUIConfig.defaults` consume it without a consumer-local
/// `[<Emit>]`.
let oidcIssuerOverride: string option = toOption oidcIssuerOverrideRaw

/// Read from the `__OIDC_AUDIENCE_OVERRIDE__` Vite define. `None`
/// when the consumer accepts the default audience derived from
/// `entraClientId` / `OidcUIConfig.ClientId`. Override when the
/// access-token audience needs to differ from the OIDC client id
/// (multi-audience deployments, federated APIs).
let oidcAudienceOverride: string option = toOption oidcAudienceOverrideRaw

// Phase 71.A.10 — client brand-string lifts. Same `string option`
// shape as the Phase 16e set: `None` covers unset / empty / the literal
// `'undefined'` / the un-substituted placeholder. A container-deploy can
// re-brand (`AppName` / `AppLogo`), re-home the landing module
// (`ActiveModule`), or flip the dev-only knobs without a Fable recompile.
// These fold into `ClientConfigDefaults.fromBundleConstants` with
// Vite-define > override-record > default precedence.

[<Emit("(typeof __TOOLUP_APP_NAME__ === 'string' && __TOOLUP_APP_NAME__ !== '' && __TOOLUP_APP_NAME__ !== 'undefined' && __TOOLUP_APP_NAME__ !== '__TOOLUP_APP_NAME__' ? __TOOLUP_APP_NAME__ : '')")>]
let private appNameRaw: string = jsNative

[<Emit("(typeof __TOOLUP_APP_LOGO__ === 'string' && __TOOLUP_APP_LOGO__ !== '' && __TOOLUP_APP_LOGO__ !== 'undefined' && __TOOLUP_APP_LOGO__ !== '__TOOLUP_APP_LOGO__' ? __TOOLUP_APP_LOGO__ : '')")>]
let private appLogoRaw: string = jsNative

[<Emit("(typeof __TOOLUP_ACTIVE_MODULE__ === 'string' && __TOOLUP_ACTIVE_MODULE__ !== '' && __TOOLUP_ACTIVE_MODULE__ !== 'undefined' && __TOOLUP_ACTIVE_MODULE__ !== '__TOOLUP_ACTIVE_MODULE__' ? __TOOLUP_ACTIVE_MODULE__ : '')")>]
let private activeModuleRaw: string = jsNative

[<Emit("(typeof __TOOLUP_DEV_DEFAULT_USER_ID__ === 'string' && __TOOLUP_DEV_DEFAULT_USER_ID__ !== '' && __TOOLUP_DEV_DEFAULT_USER_ID__ !== 'undefined' && __TOOLUP_DEV_DEFAULT_USER_ID__ !== '__TOOLUP_DEV_DEFAULT_USER_ID__' ? __TOOLUP_DEV_DEFAULT_USER_ID__ : '')")>]
let private devDefaultUserIdRaw: string = jsNative

// Boolean defines: Vite may inject a real JS boolean or a string; both
// are normalised to a token string here, then to `bool option` below.
[<Emit("(typeof __TOOLUP_ENABLE_ELMISH_TRACE__ === 'boolean' ? (__TOOLUP_ENABLE_ELMISH_TRACE__ ? 'true' : 'false') : (typeof __TOOLUP_ENABLE_ELMISH_TRACE__ === 'string' ? __TOOLUP_ENABLE_ELMISH_TRACE__ : ''))")>]
let private enableElmishTraceRaw: string = jsNative

[<Emit("(typeof __TOOLUP_SHOW_DEBUG_MODULES__ === 'boolean' ? (__TOOLUP_SHOW_DEBUG_MODULES__ ? 'true' : 'false') : (typeof __TOOLUP_SHOW_DEBUG_MODULES__ === 'string' ? __TOOLUP_SHOW_DEBUG_MODULES__ : ''))")>]
let private showDebugModulesRaw: string = jsNative

let private toBoolOption (raw: string) : bool option =
    match (if isNull raw then "" else raw.ToLowerInvariant()) with
    | "true"
    | "1"
    | "yes"
    | "on" -> Some true
    | "false"
    | "0"
    | "no"
    | "off" -> Some false
    | _ -> None

/// `__TOOLUP_APP_NAME__` Vite define — `None` when unset.
let appName: string option = toOption appNameRaw
/// `__TOOLUP_APP_LOGO__` Vite define — `None` when unset.
let appLogo: string option = toOption appLogoRaw
/// `__TOOLUP_ACTIVE_MODULE__` Vite define — `None` when unset.
let activeModule: string option = toOption activeModuleRaw
/// `__TOOLUP_DEV_DEFAULT_USER_ID__` Vite define — `None` when unset.
let devDefaultUserId: string option = toOption devDefaultUserIdRaw
/// `__TOOLUP_ENABLE_ELMISH_TRACE__` Vite define — `None` when unset.
let enableElmishConsoleTrace: bool option = toBoolOption enableElmishTraceRaw
/// `__TOOLUP_SHOW_DEBUG_MODULES__` Vite define — `None` when unset.
let showDebugOnlyModules: bool option = toBoolOption showDebugModulesRaw