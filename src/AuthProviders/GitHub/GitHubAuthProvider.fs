// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GitHubAuthProvider

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Metrics
open ToolUp.AuthProviders.GitHubApi
open ToolUp.AuthProviders.GitHubAuthConfig

// ─── GitHub auth-provider companion ──────────────────────────────────
//
// Server-side `IAuthProvider` that establishes identity from a GitHub
// OAuth access token. Because the token is opaque (no local signature to
// verify), validation is a call to GitHub's authoritative `GET /user` —
// a token GitHub did not mint returns 401 there, so identity is proven
// remotely. Optional org allow-listing gates admission to members of
// named organisations.
//
// A short-TTL per-instance cache keyed by a SHA-256 of the token keeps
// the `GET /user` round-trip off the per-request hot path (GitHub's
// authenticated rate limit is 5000/hr/token). The cache lives in the
// provider's closure — no process-wide mutable singleton, matching the
// SDK's per-instance-not-static discipline (see the OIDC provider's
// metrics-sink note).

// ─── Shared default HttpClient ───────────────────────────────────────
//
// `fromConfig` callers don't supply an HttpClient; this lazy single
// instance is shared across every production provider built that way.
// `fromConfigWith` (tests + advanced callers) injects a client backed by
// a stub `HttpMessageHandler`, so the /user + membership calls go through
// the injected client and no real GitHub credentials are needed in CI.
let private defaultHttpClient = lazy (new HttpClient())

// ─── GitHub-specific metric names ────────────────────────────────────
//
// Reuses the shared `provider` tag + success / no-token counters so a
// dashboard aggregates GitHub alongside OIDC / Entra; GitHub-specific
// outcomes (opaque-token rejection, forbidden/rate-limited, org-denied,
// upstream error, cache hit) get companion-local names on the same
// `toolup.auth.validate.*` prefix.
let private githubTags = Map.ofList [ AuthMetrics.ProviderTag, "github" ]

[<Literal>]
let private ValidateInvalidToken = "toolup.auth.validate.invalid_token_total"

[<Literal>]
let private ValidateForbidden = "toolup.auth.validate.forbidden_total"

[<Literal>]
let private ValidateOrgDenied = "toolup.auth.validate.org_denied_total"

[<Literal>]
let private ValidateUpstreamError = "toolup.auth.validate.upstream_error_total"

[<Literal>]
let private ValidateCacheHit = "toolup.auth.validate.cache_hit_total"

// ─── Token extraction (mirrors OidcAuthProvider.extractToken) ────────

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

let private extractToken (location: TokenLocation) (ctx: HttpContext) : string option =
    match location with
    | BearerHeader -> extractFromBearerHeader ctx
    | Cookie name -> extractFromCookie name ctx
    | CustomHeader name ->
        match ctx.Request.Headers.TryGetValue name with
        | true, values when values.Count > 0 -> Some(string values[0])
        | _ -> None
    | BearerOrCookie cookieName ->
        match extractFromBearerHeader ctx with
        | Some _ as token -> token
        | None -> extractFromCookie cookieName ctx

// ─── Per-token identity cache ────────────────────────────────────────
//
// Keyed by a SHA-256 hex of the token — the raw bearer is never used as
// a dictionary key (it is secret material; the digest is a stable,
// non-reversible handle). Value carries the validated user + the UTC
// instant it expires. Cache is populated only on full success (identity
// + any org gate passed), so a cached hit is safe to admit outright.

let private hashToken (token: string) : string =
    SHA256.HashData(Encoding.UTF8.GetBytes token) |> Convert.ToHexString

// ─── User mapping ────────────────────────────────────────────────────

/// Map a validated GitHub identity to `AuthenticatedUser`. `UserId` is
/// the numeric account id (stable + never reused), run through
/// `IdentitySanitiser` as defence-in-depth before it can flow into a
/// storage-scope path — the numeric id is always clean, but the sanitiser
/// keeps the identity boundary uniform across providers. `TenantId` /
/// `Roles` stay empty — GitHub org membership gates *admission*, and the
/// SDK's permission model is team-membership driven, not claim-driven.
let private userFromGitHub (ghUser: GitHubUser) (resolvedEmail: string option) : AuthenticatedUser =
    let userId =
        match IdentitySanitiser.sanitiseScopeId (string ghUser.Id) with
        | Result.Ok value -> value
        | Result.Error _ -> "anonymous"

    let displayName =
        match ghUser.Name with
        | Some n when n.Trim() <> "" -> n
        | _ when ghUser.Login <> "" -> ghUser.Login
        | _ -> userId

    {
        UserId = userId
        DisplayName = displayName
        Email = resolvedEmail
        TenantId = None
        Roles = []
    }

// ─── Validation pipeline ─────────────────────────────────────────────

/// Full validation: extract → cache lookup → `GET /user` → optional
/// email resolution → optional org gate → map → cache. Shared by
/// `GetUser` (lenient) and `ValidateRequest` (strict). Returns a
/// caller-facing message on failure (suitable for a 401 body — carries
/// no token or secret material).
let private validate
    (httpClient: HttpClient)
    (cache: ConcurrentDictionary<string, DateTimeOffset * AuthenticatedUser>)
    (metrics: IMetricsSink)
    (config: GitHubAuthConfig)
    (ctx: HttpContext)
    : Async<Result<AuthenticatedUser, string>> =
    let incr (counter: string) : unit = metrics.Increment(counter, githubTags)

    // Any active membership across the allow-list admits the user. A
    // network error mid-check fails closed (returns the upstream error),
    // never silently admits.
    let rec anyActiveOrg (token: string) (orgs: string list) : Async<Result<bool, GitHubApiError>> = async {
        match orgs with
        | [] -> return Ok false
        | org :: rest ->
            match! checkOrgMembership httpClient config.UserAgent config.ApiBaseUrl token org with
            | Ok true -> return Ok true
            | Ok false -> return! anyActiveOrg token rest
            | Error e -> return Error e
    }

    let resolveEmail (token: string) (ghUser: GitHubUser) : Async<string option> = async {
        match ghUser.Email with
        | Some _ as e -> return e
        | None when config.FetchPrimaryEmail ->
            match! getPrimaryEmail httpClient config.UserAgent config.ApiBaseUrl token with
            | Ok email -> return email
            | Error _ -> return None // best-effort enrichment; never fail validation over it
        | None -> return None
    }

    async {
        match extractToken config.TokenLocation ctx with
        | None ->
            incr AuthMetrics.ValidateNoToken
            return Error "no GitHub token in request"
        | Some token ->
            let cacheKey = hashToken token

            let cached =
                if config.CacheTtlSeconds > 0 then
                    match cache.TryGetValue cacheKey with
                    | true, (expiry, user) when expiry > DateTimeOffset.UtcNow -> Some user
                    | _ -> None
                else
                    None

            match cached with
            | Some user ->
                incr ValidateCacheHit
                return Ok user
            | None ->
                match! getUser httpClient config.UserAgent config.ApiBaseUrl token with
                | Error Unauthorized ->
                    incr ValidateInvalidToken
                    return Error "GitHub rejected the access token"
                | Error(Forbidden msg) ->
                    incr ValidateForbidden
                    return Error(sprintf "GitHub API forbade the request: %s" msg)
                | Error(NetworkError msg) ->
                    incr ValidateUpstreamError
                    return Error(sprintf "could not reach the GitHub API: %s" msg)
                | Error(MalformedResponse msg) ->
                    incr AuthMetrics.ValidateMalformed
                    return Error(sprintf "unexpected GitHub API response: %s" msg)
                | Ok ghUser ->
                    // Identity is proven. Apply the org gate (if any)
                    // before admitting, then resolve the email.
                    let! orgResult =
                        if config.AllowedOrgs.IsEmpty then
                            async { return Ok true }
                        else
                            anyActiveOrg token config.AllowedOrgs

                    match orgResult with
                    | Error Unauthorized ->
                        incr ValidateInvalidToken
                        return Error "GitHub rejected the access token"
                    | Error(Forbidden msg) ->
                        incr ValidateForbidden
                        return Error(sprintf "GitHub API forbade the org-membership check: %s" msg)
                    | Error(NetworkError msg) ->
                        incr ValidateUpstreamError
                        return Error(sprintf "could not verify GitHub org membership: %s" msg)
                    | Error(MalformedResponse msg) ->
                        incr AuthMetrics.ValidateMalformed
                        return Error(sprintf "unexpected GitHub membership response: %s" msg)
                    | Ok false ->
                        incr ValidateOrgDenied
                        return Error "GitHub user is not an active member of an allowed organisation"
                    | Ok true ->
                        let! resolvedEmail = resolveEmail token ghUser
                        let user = userFromGitHub ghUser resolvedEmail

                        if config.CacheTtlSeconds > 0 then
                            let expiry = DateTimeOffset.UtcNow.AddSeconds(float config.CacheTtlSeconds)
                            cache[cacheKey] <- (expiry, user)

                        incr AuthMetrics.ValidateSuccess
                        return Ok user
    }

// ─── No-op logger fallback (mirrors OidcAuthProvider.noOpLogger) ─────

let private noOpLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// ─── Construction ────────────────────────────────────────────────────

/// Build a GitHub `IAuthProvider` over an explicit HttpClient + optional
/// metrics sink. Single private implementation behind the public entry
/// points. Each provider instance owns its identity cache + sink via the
/// closure — no shared mutable state.
let private buildProvider
    (httpClient: HttpClient)
    (metrics: IMetricsSink option)
    (logger: ILogger option)
    (config: GitHubAuthConfig)
    : IAuthProvider =
    // Refuse a cleartext API base at construction — the bearer rides
    // every call, and http:// would leak it / admit a MITM substituting
    // the identity response. Surfaces at startup, not as silent trust.
    match Uri.TryCreate(config.ApiBaseUrl, UriKind.Absolute) with
    | false, _ ->
        invalidArg (nameof config) $"GitHubAuthConfig.ApiBaseUrl '{config.ApiBaseUrl}' is not an absolute URL."
    | true, uri when uri.Scheme <> Uri.UriSchemeHttps ->
        invalidArg
            (nameof config)
            $"GitHubAuthConfig.ApiBaseUrl '{config.ApiBaseUrl}' must be https — the access token rides every GitHub API call and cleartext would expose it."
    | true, _ -> ()

    if String.IsNullOrWhiteSpace config.UserAgent then
        invalidArg
            (nameof config)
            "GitHubAuthConfig.UserAgent must not be empty — GitHub rejects API requests with no User-Agent."

    let log = logger |> Option.defaultValue noOpLogger

    let sink =
        metrics |> Option.defaultWith (fun () -> NoOpMetricsSink() :> IMetricsSink)

    let cache = ConcurrentDictionary<string, DateTimeOffset * AuthenticatedUser>()

    { new IAuthProvider with
        member _.GetUser ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! result = validate httpClient cache sink config httpCtx

            match result with
            | Ok user ->
                log.Debug $"GitHub auth ok: user={user.UserId}"
                return user
            | Error msg ->
                // Lenient path: the missing-token case is the expected,
                // high-volume shape for unauthenticated endpoints and
                // stays at Debug; genuine validation failures log at Warn.
                if msg = "no GitHub token in request" then
                    log.Debug "GitHub auth (lenient): no token in request → anonymous"
                else
                    log.Warn $"GitHub auth failed (lenient): {msg}"

                return AuthenticatedUser.anonymous
        }

        member _.ValidateRequest ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! result = validate httpClient cache sink config httpCtx

            match result with
            | Ok user ->
                log.Debug $"GitHub validate ok: user={user.UserId}"
                return Ok user
            | Error msg ->
                log.Warn $"GitHub validate failed: {msg}"
                return Error msg
        }

        // Identity is proven by GitHub's authoritative `GET /user` — a
        // token GitHub did not mint fails that call — not by trusting a
        // spoofable request header. That is remote-token validation of
        // the same class the interface's `IsCryptographicallyVerified`
        // gate is written to admit (a validated OIDC token), so `true`
        // is the honest answer: the bearer cannot be forged, and the
        // unverified-provider startup gate must not reject this provider.
        member _.IsCryptographicallyVerified = true
    }

/// Construct a GitHub `IAuthProvider` from a declarative
/// `GitHubAuthConfig`, using a custom `HttpClient` (tests inject a stub
/// `HttpMessageHandler`; advanced callers a pre-configured client with
/// proxy / timeout policy). The returned provider validates each inbound
/// bearer against GitHub's `GET /user`, applies any org allow-list, and
/// caches the validated identity for `CacheTtlSeconds`.
///
/// `logger` receives observational events (validation success / failure
/// reasons). Messages never include the token or any secret material.
/// `GetUser` is lenient (failure → `anonymous`); `ValidateRequest` is
/// strict (failure → `Error <reason>`).
let fromConfigWith (httpClient: HttpClient) (logger: ILogger option) (config: GitHubAuthConfig) : IAuthProvider =
    buildProvider httpClient None logger config

/// Production shorthand — uses a process-wide lazy `HttpClient`.
let fromConfig (logger: ILogger option) (config: GitHubAuthConfig) : IAuthProvider =
    buildProvider defaultHttpClient.Value None logger config

/// Metrics-enabled production shorthand — pair with a resolved
/// `IMetricsSink` (e.g. from the SDK's DI container at compose time) so
/// the provider emits `toolup.auth.validate.*` counters tagged
/// `provider=github`. Passing `None` is equivalent to `fromConfig`.
let fromConfigMetered
    (logger: ILogger option)
    (metrics: IMetricsSink option)
    (config: GitHubAuthConfig)
    : IAuthProvider =
    buildProvider defaultHttpClient.Value metrics logger config

/// Metrics-enabled variant of `fromConfigWith` — same role as
/// `fromConfigMetered` but accepts a custom `HttpClient`. Tests typically
/// use this with a stub `HttpMessageHandler` + a recording metrics sink
/// to assert per-outcome counter emission.
let fromConfigWithMetrics
    (httpClient: HttpClient)
    (logger: ILogger option)
    (metrics: IMetricsSink option)
    (config: GitHubAuthConfig)
    : IAuthProvider =
    buildProvider httpClient metrics logger config

/// Construct directly from environment variables. Gated on an explicit
/// `TOOLUP_GITHUB_AUTH` opt-in (`1` / `true` / `yes` / `on` / `enabled`)
/// so a deployment that doesn't use GitHub — and hasn't set the flag —
/// gets `None` and can fall back to another provider without a
/// `try`/`with`. When enabled, config layers `GitHubAuthConfig.fromEnv`
/// over the defaults (`TOOLUP_GITHUB_ALLOWED_ORGS`, `_API_BASE_URL`, …).
let fromEnv (logger: ILogger option) : IAuthProvider option =
    match Environment.GetEnvironmentVariable ConfigKeys.Names.githubAuth with
    | null
    | "" -> None
    | raw ->
        match raw.Trim().ToLowerInvariant() with
        | "1"
        | "true"
        | "yes"
        | "on"
        | "enabled" -> Some(fromConfig logger (GitHubAuthConfig.fromEnv ()))
        | _ -> None

/// Metrics-enabled variant of `fromEnv` — wires an optional
/// `IMetricsSink` into the constructed provider when `TOOLUP_GITHUB_AUTH`
/// is enabled.
let fromEnvMetered (logger: ILogger option) (metrics: IMetricsSink option) : IAuthProvider option =
    match Environment.GetEnvironmentVariable ConfigKeys.Names.githubAuth with
    | null
    | "" -> None
    | raw ->
        match raw.Trim().ToLowerInvariant() with
        | "1"
        | "true"
        | "yes"
        | "on"
        | "enabled" -> Some(fromConfigMetered logger metrics (GitHubAuthConfig.fromEnv ()))
        | _ -> None