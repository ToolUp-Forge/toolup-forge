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

let private buildAuthConfig (issuer: string) (audience: string option) : AuthConfig = {
    Issuer = Some issuer
    Audience = audience
    KeySource = JwksDiscovery issuer
    TokenLocation = tokenLocationFromEnv ()
    ClockSkewSeconds = None
    AcceptedAlgorithms = None
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
///     `TOOLUP_OIDC_AUDIENCE`. Missing issuer falls back to
///     `HeaderAuthProvider` with a Warn.
///   - unrecognised value — falls back to `HeaderAuthProvider` with a
///     Warn naming the recognised values.
///
/// Warning text + behaviour is byte-for-byte identical to the
/// hand-written dispatch in the reference composition root.
///
/// Metrics: the constructed provider does not emit auth-pipeline
/// metrics. Use `fromEnvMetered` to thread an `IMetricsSink` resolved
/// from the compose-time service collection into the OIDC builder.
let fromEnv (logger: ILogger) (oidcBuilder: OidcAuthBuilder) : IAuthProvider =
    let headerAuth = HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider

    match envVar "TOOLUP_AUTH_MODE" |> Option.map _.ToLowerInvariant() with
    | Some "oidc" ->
        match envVar "TOOLUP_OIDC_ISSUER" with
        | None ->
            logger.Warn
                "TOOLUP_AUTH_MODE=oidc but TOOLUP_OIDC_ISSUER not set. Falling back to HeaderAuthProvider (dev default). Authenticated modes will reject all requests until an issuer is configured."

            headerAuth
        | Some issuer ->
            let audience = envVar "TOOLUP_OIDC_AUDIENCE"
            let authConfig = buildAuthConfig issuer audience
            let audienceLabel = audience |> Option.defaultValue "(any)"
            logger.Info $"Auth provider: OIDC (issuer={issuer}, audience={audienceLabel})"

            oidcBuilder (Some logger) authConfig
    | Some other ->
        logger.Warn
            $"TOOLUP_AUTH_MODE={other} not recognised. Valid values: oidc (or unset for dev-only HeaderAuthProvider)."

        headerAuth
    | None -> headerAuth

/// Metrics-aware counterpart to `fromEnv`. Threads the resolved
/// `IMetricsSink option` into `oidcBuilder` so the env-driven OIDC
/// provider emits `toolup.auth.validate.*` counters tagged
/// `provider=oidc`. Composition roots resolve the sink from the
/// compose-time service collection (after `compose` registers
/// `IMetricsSink` per `MetricsEndpoint`) and pass it here.
///
/// Behaviour is identical to `fromEnv` for `HeaderAuthProvider` /
/// unrecognised-mode dispatch — metrics only flow when the env vars
/// resolve to the OIDC branch.
let fromEnvMetered
    (logger: ILogger)
    (metrics: IMetricsSink option)
    (oidcBuilder: OidcAuthBuilderMetered)
    : IAuthProvider =
    let headerAuth = HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider

    match envVar "TOOLUP_AUTH_MODE" |> Option.map _.ToLowerInvariant() with
    | Some "oidc" ->
        match envVar "TOOLUP_OIDC_ISSUER" with
        | None ->
            logger.Warn
                "TOOLUP_AUTH_MODE=oidc but TOOLUP_OIDC_ISSUER not set. Falling back to HeaderAuthProvider (dev default). Authenticated modes will reject all requests until an issuer is configured."

            headerAuth
        | Some issuer ->
            let audience = envVar "TOOLUP_OIDC_AUDIENCE"
            let authConfig = buildAuthConfig issuer audience
            let audienceLabel = audience |> Option.defaultValue "(any)"
            logger.Info $"Auth provider: OIDC (issuer={issuer}, audience={audienceLabel})"

            oidcBuilder (Some logger) metrics authConfig
    | Some other ->
        logger.Warn
            $"TOOLUP_AUTH_MODE={other} not recognised. Valid values: oidc (or unset for dev-only HeaderAuthProvider)."

        headerAuth
    | None -> headerAuth