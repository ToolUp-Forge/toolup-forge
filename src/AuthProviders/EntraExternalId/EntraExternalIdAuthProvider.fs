// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.EntraExternalIdAuthProvider

open System
open System.Net.Http
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Metrics
open ToolUp.AuthProviders.EntraExternalIdConfig

// ─── DEPRECATED in 0.4.0 ────────────────────────────────────────────
//
// New deployments use the generic `ToolUp.AuthProviders.Oidc` server
// provider paired with an `OidcAppConfig` produced by
// `OidcPresets.entraExternalId` on the client side. See
// `docs/migrations/0.4.0-entra-external-id-deprecation.md` for the
// migration walk-through.
//
// This module stays compiling for one minor cycle (consumer migration
// window) and is scheduled for removal at 0.Y.0. The use case where
// it remains the right answer is the `oid` -> `UserId` /
// `tid` -> `TenantId` claim-mapping decorator below — the generic
// `OidcAuthProvider` does not yet expose claim remapping as a
// first-class option, so deployments whose downstream code depends
// on the stable `oid` user-id stay on this wrapper until the
// substrate gains a claim-mapping seam (tracked separately).

// ─── Entra External ID auth-provider companion ──────────────────────
//
// Thin wrapper that delegates JWT signature + claim validation to the
// generic `OidcAuthProvider`, then post-processes the returned
// `AuthenticatedUser` to apply Entra-specific claim mappings:
//
//   - `oid` -> `UserId` (more stable than `sub` in External ID; `sub`
//     varies per app registration, `oid` is constant per user per tenant)
//   - `tid` -> `TenantId`
//   - `idp` is read here for audit pass-through but not exposed on the
//     `AuthenticatedUser` (audit-event metadata is provider-agnostic);
//     consumers wanting per-IdP audit details should attach an audit
//     decorator that reads the request's bearer token.
//
// The post-processing is signature-safe because it runs after the
// inner provider has already verified the JWT signature, issuer,
// audience, and expiry. We re-parse the payload segment to read the
// External-ID-specific claims; the bytes are already trusted by that
// point.

// ─── Claim extraction (unsigned re-read of an already-validated JWT) ─
//
// Base64url decode goes through the shared `ToolUp.Platform.Base64Url`
// codec (this file already `open`s `ToolUp.Platform`).

let private tryGetString (name: string) (el: JsonElement) : string option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

type private EntraClaims = {
    Oid: string option
    Tid: string option
    Idp: string option
    Sub: string option
    Email: string option
    Name: string option
}

let private emptyClaims = {
    Oid = None
    Tid = None
    Idp = None
    Sub = None
    Email = None
    Name = None
}

// ─── Auth-pipeline metrics (Cluster D1 / Phase 9e.A) ─────────────────
//
// Optional `IMetricsSink` threaded through `createWithMetrics` /
// `createMetered`. Phase 9e.A retired the prior `setMetricsSink`
// setter + `mutable private metricsSink` cell in favour of per-instance
// binding — each constructed provider holds its sink in its closure,
// no module-level state. Only one Entra-specific counter today
// (best-effort claim-parse failure post-validation); the inner OIDC
// provider emits the validate.* counters through its own sink.

let private entraTags = Map.ofList [ AuthMetrics.ProviderTag, "entra-external-id" ]

let private tryReadClaims (metrics: IMetricsSink) (rawToken: string) : EntraClaims =
    let incr (counter: string) : unit = metrics.Increment(counter, entraTags)

    try
        let parts = rawToken.Split('.')

        if parts.Length <> 3 then
            incr AuthMetrics.EntraClaimParseFailed
            emptyClaims
        else
            let payloadJson = Encoding.UTF8.GetString(Base64Url.decode parts[1])
            use doc = JsonDocument.Parse payloadJson
            let root = doc.RootElement

            {
                Oid = tryGetString "oid" root
                Tid = tryGetString "tid" root
                Idp = tryGetString "idp" root
                Sub = tryGetString "sub" root
                Email = tryGetString "email" root
                Name = tryGetString "name" root
            }
    with _ ->
        // The inner provider has already accepted this token; a parse
        // failure here is implausible. We never throw — Entra-specific
        // overrides are best-effort enrichment on top of an already-
        // validated identity. Counter increments per the audit lens
        // captured in the Investigate-Gaps pass: silent post-validation
        // failures should be ambient observability, not Warn spam.
        incr AuthMetrics.EntraClaimParseFailed
        emptyClaims

let private extractFromBearerHeader (ctx: HttpContext) : string option =
    match ctx.Request.Headers.TryGetValue "Authorization" with
    | true, values when values.Count > 0 ->
        let value = string values[0]

        if value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            Some(value.Substring 7)
        else
            None
    | _ -> None

let private extractFromCookie (name: string) (ctx: HttpContext) : string option =
    match ctx.Request.Cookies.TryGetValue name with
    | true, value when not (String.IsNullOrEmpty value) -> Some value
    | _ -> None

let private extractRawToken (location: TokenLocation) (ctx: HttpContext) : string option =
    match location with
    | BearerHeader -> extractFromBearerHeader ctx
    | Cookie name -> extractFromCookie name ctx
    | CustomHeader name ->
        match ctx.Request.Headers.TryGetValue name with
        | true, values when values.Count > 0 -> Some(string values[0])
        | _ -> None
    | BearerOrCookie cookieName ->
        // 0.5.3 — mirrors `OidcAuthProvider.extractToken` BearerOrCookie
        // handling. Try Bearer header first, fall back to cookie.
        match extractFromBearerHeader ctx with
        | Some _ as token -> token
        | None -> extractFromCookie cookieName ctx

// ─── Entra-flavoured user projection ─────────────────────────────────

let private applyEntraMapping (claims: EntraClaims) (user: AuthenticatedUser) : AuthenticatedUser =
    // `oid` is preferred. Fall back to `sub` (the inner provider's
    // default) only when `oid` is absent — rare but possible for some
    // federated IdP flows. Display name and email fall back to the
    // inner provider's values when the Entra-specific claims are
    // absent.
    let userId =
        claims.Oid
        |> Option.defaultWith (fun () -> claims.Sub |> Option.defaultValue user.UserId)

    {
        user with
            UserId = userId
            TenantId = claims.Tid |> Option.orElse user.TenantId
            DisplayName = claims.Name |> Option.defaultValue user.DisplayName
            Email = claims.Email |> Option.orElse user.Email
    }

/// Federated IdP claim, surfaced for audit consumers that decorate the
/// authentication pipeline. Returns `None` when the token is absent or
/// the `idp` claim is missing (local Entra accounts typically omit it).
/// Pure function over the request — does not validate the token (the
/// caller must do so first). Internal claim-parse failures are ignored
/// here (audit-lens fallback to `None`); the metric is only emitted
/// from the auth-validation path, where a sink is bound via
/// `createWithMetrics` / `createMetered`.
let readIdpClaim (location: TokenLocation) (ctx: HttpContext) : string option =
    let silent = NoOpMetricsSink() :> IMetricsSink

    extractRawToken location ctx
    |> Option.bind (fun raw -> (tryReadClaims silent raw).Idp)

// ─── HttpClient sharing ──────────────────────────────────────────────

let private defaultHttpClient = lazy (new HttpClient())

// ─── Construction ────────────────────────────────────────────────────

let private toAuthConfig (config: EntraExternalIdConfig) : AuthConfig =
    let issuer = EntraExternalIdConfig.issuerUrl config.Tenant config.CustomDomain

    {
        Issuer = Some issuer
        Audience = Some config.Audience
        KeySource = JwksDiscovery issuer
        TokenLocation = BearerHeader
        // Phase 3d / Cluster B4 — propagate clock-skew override to
        // the inner OIDC provider. EntraExternalIdConfig.ClockSkewSeconds
        // is int option (operator-facing seconds); AuthConfig.ClockSkewSeconds
        // is int64 option. None at the consumer flows through as None
        // here so the inner provider applies its 60s default.
        ClockSkewSeconds = config.ClockSkewSeconds |> Option.map int64
        // Entra External ID issues RS256-signed tokens; inherit the
        // SDK default whitelist (`[ RS256 ]`) so the inner provider's
        // posture is byte-for-byte unchanged by Phase 3.A.
        AcceptedAlgorithms = None
        // 0.5.4 — the EntraExternalId companion does its own `oid` →
        // UserId remapping via `applyEntraMapping` post-validation,
        // so the inner OIDC provider keeps `sub`-only behaviour here.
        PreferOidWhenPresent = None
    }

let private wrapWithEntraMapping
    (inner: IAuthProvider)
    (metrics: IMetricsSink)
    (location: TokenLocation)
    : IAuthProvider =
    { new IAuthProvider with
        member _.GetUser ctx = async {
            let! user = inner.GetUser ctx

            if AuthenticatedUser.isAnonymous user then
                return user
            else
                let httpCtx = RequestContext.value ctx :?> HttpContext

                match extractRawToken location httpCtx with
                | Some raw ->
                    let claims = tryReadClaims metrics raw
                    return applyEntraMapping claims user
                | None -> return user
        }

        member _.ValidateRequest ctx = async {
            let! result = inner.ValidateRequest ctx

            match result with
            | Error e -> return Error e
            | Ok user ->
                let httpCtx = RequestContext.value ctx :?> HttpContext

                match extractRawToken location httpCtx with
                | Some raw ->
                    let claims = tryReadClaims metrics raw
                    return Ok(applyEntraMapping claims user)
                | None -> return Ok user
        }

        // Decorator: identity verification is the inner OIDC provider's
        // responsibility (this wrapper only remaps `oid`/`tid` claims
        // post-validation). Delegate the capability so the wrapper can't
        // mask the inner provider's guarantee.
        member _.IsCryptographicallyVerified = inner.IsCryptographicallyVerified
    }

/// Build an Entra External ID `IAuthProvider` over an explicit
/// HttpClient + optional metrics sink. Single private implementation
/// behind the four public entry points.
let private buildProvider
    (httpClient: HttpClient)
    (metrics: IMetricsSink option)
    (logger: ILogger option)
    (config: EntraExternalIdConfig)
    : IAuthProvider =
    if String.IsNullOrWhiteSpace config.Tenant then
        invalidArg (nameof config) "EntraExternalIdConfig.Tenant must not be empty"

    if String.IsNullOrWhiteSpace config.Audience then
        invalidArg (nameof config) "EntraExternalIdConfig.Audience must not be empty"

    let authConfig = toAuthConfig config

    let inner =
        OidcAuthProvider.fromConfigWithMetrics httpClient logger metrics authConfig

    let sink =
        metrics |> Option.defaultWith (fun () -> NoOpMetricsSink() :> IMetricsSink)

    wrapWithEntraMapping inner sink authConfig.TokenLocation

/// Construct an Entra External ID `IAuthProvider` from a declarative
/// `EntraExternalIdConfig`. The returned provider:
///   1. Delegates JWT signature + claim validation to the generic
///      `OidcAuthProvider`, using a constructed issuer URL keyed off
///      the tenant id (and optional custom domain).
///   2. Post-processes the validated `AuthenticatedUser` to apply
///      Entra's `oid` (more stable than `sub`) and `tid` (tenant id)
///      claims as `UserId` / `TenantId`.
///
/// `logger` is forwarded to the inner provider. Variant `createWith`
/// accepts a custom `HttpClient` for tests; `create` uses a
/// process-wide lazy default. Metrics-enabled variants
/// (`createMetered` / `createWithMetrics`) thread an `IMetricsSink` so
/// the inner provider emits `toolup.auth.validate.*` counters and this
/// wrapper emits `toolup.auth.entra.claim_parse_failed_total` — all
/// tagged `provider=entra-external-id`.
let createWith (httpClient: HttpClient) (logger: ILogger option) (config: EntraExternalIdConfig) : IAuthProvider =
    buildProvider httpClient None logger config

/// Production shorthand — uses a process-wide lazy `HttpClient`.
let create (logger: ILogger option) (config: EntraExternalIdConfig) : IAuthProvider =
    buildProvider defaultHttpClient.Value None logger config

/// Metrics-enabled production shorthand — pair with a resolved
/// `IMetricsSink`. Passing `None` is equivalent to `create`.
let createMetered
    (logger: ILogger option)
    (metrics: IMetricsSink option)
    (config: EntraExternalIdConfig)
    : IAuthProvider =
    buildProvider defaultHttpClient.Value metrics logger config

/// Metrics-enabled variant of `createWith` — same role as
/// `createMetered` but accepts a custom `HttpClient`.
let createWithMetrics
    (httpClient: HttpClient)
    (logger: ILogger option)
    (metrics: IMetricsSink option)
    (config: EntraExternalIdConfig)
    : IAuthProvider =
    buildProvider httpClient metrics logger config

/// Construct directly from environment variables. Returns `None` when
/// `TOOLUP_ENTRA_EXTERNAL_ID_TENANT` or `_AUDIENCE` is unset, so
/// composition roots can decide to fall back to a different provider
/// without wrapping in `try`/`with`.
let fromEnv (logger: ILogger option) : IAuthProvider option =
    EntraExternalIdConfig.fromEnv () |> Option.map (create logger)

/// Metrics-enabled variant of `fromEnv` — wires an optional
/// `IMetricsSink` into the constructed provider when the env vars are
/// set. Composition roots that resolve `IMetricsSink` from DI pass it
/// in here so the env-driven Entra provider participates in the
/// standard `toolup.auth.*` observability pipeline.
let fromEnvMetered (logger: ILogger option) (metrics: IMetricsSink option) : IAuthProvider option =
    EntraExternalIdConfig.fromEnv () |> Option.map (createMetered logger metrics)