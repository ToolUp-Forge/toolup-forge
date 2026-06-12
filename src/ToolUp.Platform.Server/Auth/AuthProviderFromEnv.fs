// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AuthProvider

open System
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Metrics

/// Builder function for OIDC `IAuthProvider`. The consumer passes
/// `ToolUp.AuthProviders.OidcAuthProvider.fromConfig` (or any
/// signature-compatible function). Keeps `ToolUp.Platform.Server`
/// free of any direct dependency on the OIDC companion package.
type OidcAuthBuilder = ILogger option -> AuthConfig -> IAuthProvider

/// Metrics-aware builder counterpart to `OidcAuthBuilder`. Consumers
/// pass `ToolUp.AuthProviders.OidcAuthProvider.fromConfigMetered`
/// (or any signature-compatible function). Threads the resolved
/// `IMetricsSink option` from the compose-time service collection
/// into the constructed provider so the env-driven OIDC path
/// participates in the standard `toolup.auth.*` observability
/// pipeline.
type OidcAuthBuilderMetered = ILogger option -> IMetricsSink option -> AuthConfig -> IAuthProvider

let private envVar (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

// Gap audit 2026-06-12 Auth G6 — explicit misconfiguration refuses at
// startup instead of silently degrading to the spoofable
// HeaderAuthProvider. A deployment that declared `TOOLUP_AUTH_MODE=oidc`
// (and may legitimately run `AcceptHeaderAuthWhenAuthRequired` for its
// mTLS proxy) must not boot in header-trust because of an env-var typo.
// The dev-default fallback is reserved for genuinely-unset mode (GP 11
// — refusals fire on explicit misconfiguration, never on unset config).

let private missingIssuerMessage =
    "TOOLUP_AUTH_MODE=oidc but TOOLUP_OIDC_ISSUER is not set. Refusing to start: falling back to the header-trust dev provider under a declared OIDC intent would silently accept spoofable X-User-Id identities. Set TOOLUP_OIDC_ISSUER to your OIDC provider's discovery URL, or unset TOOLUP_AUTH_MODE entirely to use the dev-only HeaderAuthProvider."

let private unrecognisedModeMessage (other: string) =
    $"TOOLUP_AUTH_MODE={other} is not recognised. Refusing to start rather than falling back to the dev-only HeaderAuthProvider under a declared auth intent. Valid values: oidc (or unset for the dev-only HeaderAuthProvider)."

/// 0.5.3 — derive `TokenLocation` from the SSE auth mode env var.
/// `TOOLUP_SSE_AUTH=cookie` (or `cookies` / `cookieonly`) signals that
/// the deployment serves SSE via EventSource, which the browser can only
/// auth via cookie — so the OIDC provider must accept the JWT from
/// EITHER the Authorization header (REST path) OR the
/// `toolup-auth-token` cookie (SSE path). Without this, every
/// authenticated SSE handshake 401s while REST keeps working, breaking
/// real-time notifications + AI chat streams in any consumer that runs
/// SSE behind OIDC.
///
/// Any other value (including unset / empty) keeps the historical
/// `BearerHeader`-only behaviour — deployments that don't use SSE pay
/// nothing for the fallback (GP 11 + GP 13).
let private tokenLocationFromEnv () : TokenLocation =
    match envVar "TOOLUP_SSE_AUTH" |> Option.map _.ToLowerInvariant() with
    | Some "cookie"
    | Some "cookies"
    | Some "cookieonly" ->
        // The cookie name is fixed in `UserSession.fs` client-side
        // (`toolup-auth-token`). Hardcoded server-side to avoid two
        // independent string literals drifting; both must agree.
        BearerOrCookie "toolup-auth-token"
    | _ -> BearerHeader

/// 0.5.4 — derive `PreferOidWhenPresent` from the issuer. Microsoft
/// Entra v2 endpoints (`login.microsoftonline.com/<tenant>/v2.0` and
/// the External-ID `<tenant>.ciamlogin.com` shape) mint tokens
/// carrying the tenant-stable `oid` claim alongside the pairwise-
/// pseudonymous `sub`. `oid` is the right operational identifier for
/// `TOOLUP_INITIAL_PLATFORM_ADMIN`, RBAC entries, and audit identity
/// — `sub` rotates per app registration and breaks identity continuity
/// when an operator looks up a user in the Entra portal. The match
/// is permissive: any host suffix of `microsoftonline.com` /
/// `ciamlogin.com` qualifies; non-Microsoft issuers (Auth0, Clerk,
/// AWS Cognito, custom IdPs) leave the flag `None` and the provider
/// preserves the pre-0.5.4 `sub`-only behaviour (GP 11).
let private preferOidFromIssuer (issuer: string) : bool option =
    let host =
        try
            (System.Uri issuer).Host.ToLowerInvariant()
        with _ ->
            ""

    let isEntra =
        host.EndsWith "login.microsoftonline.com" || host.EndsWith "ciamlogin.com"

    if isEntra then Some true else None

let private buildAuthConfig (issuer: string) (audience: string option) : AuthConfig = {
    Issuer = Some issuer
    Audience = audience
    KeySource = JwksDiscovery issuer
    TokenLocation = tokenLocationFromEnv ()
    ClockSkewSeconds = None
    AcceptedAlgorithms = None
    PreferOidWhenPresent = preferOidFromIssuer issuer
}

/// Build the deployment's `IAuthProvider` from `TOOLUP_AUTH_MODE`.
/// Recognised values:
///
///   - unset (default) — `HeaderAuthProvider`. The dev-mode auth
///     provider that trusts `X-User-Id` at face value. Production
///     deployments running in any authenticated `PlatformMode` MUST
///     pair this with `AcceptHeaderAuthWhenAuthRequired = true` AND
///     stand behind a mTLS proxy that strips/re-injects the header
///     (per Phase 6l.A `HeaderAuthProviderModeValidator`).
///   - `oidc` — calls the supplied `oidcBuilder` with an `AuthConfig`
///     populated from `TOOLUP_OIDC_ISSUER` + optional
///     `TOOLUP_OIDC_AUDIENCE`. Missing issuer refuses startup
///     (`invalidOp`) — explicit OIDC intent must never degrade to
///     header trust (Gap audit 2026-06-12 Auth G6).
///   - unrecognised value — refuses startup, naming the recognised
///     values. Only genuinely-unset mode gets the dev fallback (GP 11).
///
/// Metrics: the constructed provider does not emit auth-pipeline
/// metrics. Use `fromEnvMetered` to thread an `IMetricsSink` resolved
/// from the compose-time service collection into the OIDC builder.
let fromEnv (logger: ILogger) (oidcBuilder: OidcAuthBuilder) : IAuthProvider =
    match envVar "TOOLUP_AUTH_MODE" |> Option.map _.ToLowerInvariant() with
    | Some "oidc" ->
        match envVar "TOOLUP_OIDC_ISSUER" with
        | None ->
            logger.Error(missingIssuerMessage, None)
            invalidOp missingIssuerMessage
        | Some issuer ->
            let audience = envVar "TOOLUP_OIDC_AUDIENCE"
            let authConfig = buildAuthConfig issuer audience
            let audienceLabel = audience |> Option.defaultValue "(any)"
            logger.Info $"Auth provider: OIDC (issuer={issuer}, audience={audienceLabel})"

            oidcBuilder (Some logger) authConfig
    | Some other ->
        let message = unrecognisedModeMessage other
        logger.Error(message, None)
        invalidOp message
    | None -> HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider

/// Metrics-aware counterpart to `fromEnv`. Threads the resolved
/// `IMetricsSink option` into `oidcBuilder` so the env-driven OIDC
/// provider emits `toolup.auth.validate.*` counters tagged
/// `provider=oidc`. Composition roots resolve the sink from the
/// compose-time service collection (after `compose` registers
/// `IMetricsSink` per `MetricsEndpoint`) and pass it here.
///
/// Behaviour is identical to `fromEnv` for unset-mode fallback and
/// explicit-misconfiguration refusal — metrics only flow when the env
/// vars resolve to the OIDC branch.
let fromEnvMetered
    (logger: ILogger)
    (metrics: IMetricsSink option)
    (oidcBuilder: OidcAuthBuilderMetered)
    : IAuthProvider =
    match envVar "TOOLUP_AUTH_MODE" |> Option.map _.ToLowerInvariant() with
    | Some "oidc" ->
        match envVar "TOOLUP_OIDC_ISSUER" with
        | None ->
            logger.Error(missingIssuerMessage, None)
            invalidOp missingIssuerMessage
        | Some issuer ->
            let audience = envVar "TOOLUP_OIDC_AUDIENCE"
            let authConfig = buildAuthConfig issuer audience
            let audienceLabel = audience |> Option.defaultValue "(any)"
            logger.Info $"Auth provider: OIDC (issuer={issuer}, audience={audienceLabel})"

            oidcBuilder (Some logger) metrics authConfig
    | Some other ->
        let message = unrecognisedModeMessage other
        logger.Error(message, None)
        invalidOp message
    | None -> HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider