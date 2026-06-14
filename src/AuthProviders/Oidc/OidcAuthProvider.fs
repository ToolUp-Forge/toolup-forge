module ToolUp.AuthProviders.OidcAuthProvider

open System
open System.Net.Http
open System.Security.Cryptography
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Metrics
open ToolUp.AuthProviders.OidcAuthProviderJwt
open ToolUp.AuthProviders.OidcAuthProviderJwks

// ─── Shared default HttpClient ───────────────────────────────────────
//
// `fromConfig` callers don't supply an HttpClient; this lazy single
// instance is shared across every production provider built that way.
// `fromConfigWith` (tests + advanced callers) injects a different
// client backed by a stub HttpMessageHandler — the JWKS / discovery
// fetches go through the injected client instead, removing the need
// for a real OIDC issuer during contract tests.
let private defaultHttpClient = lazy (new HttpClient())

// ─── Signature verification ──────────────────────────────────────────

/// SDK default trust set when `AuthConfig.AcceptedAlgorithms = None`.
/// Single-algorithm RS256-only — every consumer in the workspace today
/// inherits this, and every deployment whose IdP signs RS256 is
/// byte-for-byte unchanged by Phase 3.A. Operators opting in to a
/// wider set (ES256 / RS384 / RS512 / PS256) populate
/// `AcceptedAlgorithms = Some [...]` explicitly.
let private defaultAcceptedAlgorithms = [ RS256 ]

let private resolveAcceptedAlgorithms (config: AuthConfig) : JwsAlgorithm list =
    config.AcceptedAlgorithms |> Option.defaultValue defaultAcceptedAlgorithms

/// Dispatch on the typed JWS algorithm + the resolved JWK key. RSA
/// (RS256/RS384/RS512/PS256) and EC (ES256) verification paths are
/// independent; the key-type / algorithm mismatch arms (e.g. an RS256
/// header against an EC JWK) return false rather than throw, so the
/// caller surfaces them through the same `InvalidSignature` metric as
/// any other verify failure. ES256's JWS signature transport is the
/// IEEE-P1363 fixed-field concatenation (r || s, 64 bytes for P-256),
/// distinct from the DER-encoded form `ECDsa` defaults to.
let private verifyJws (alg: JwsAlgorithm) (key: JwkKey) (signedBytes: byte[]) (signature: byte[]) : bool =
    match alg, key with
    | RS256, RsaKey p ->
        use rsa = RSA.Create()
        rsa.ImportParameters p
        rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    | RS384, RsaKey p ->
        use rsa = RSA.Create()
        rsa.ImportParameters p
        rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1)
    | RS512, RsaKey p ->
        use rsa = RSA.Create()
        rsa.ImportParameters p
        rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1)
    | PS256, RsaKey p ->
        use rsa = RSA.Create()
        rsa.ImportParameters p
        rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
    | ES256, EcKey p ->
        use ec = ECDsa.Create()
        ec.ImportParameters p

        ec.VerifyData(
            signedBytes,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        )
    | _ -> false

// ─── Claim validation ────────────────────────────────────────────────

/// SDK default clock-skew tolerance applied to `exp` and `nbf` when
/// the consumer's `AuthConfig.ClockSkewSeconds` is `None`. 60 seconds
/// is standard practice — covers small NTP drift between the
/// deployment host and the IdP. Per-deployment overrides via
/// `AuthConfig.ClockSkewSeconds` are honoured.
let private defaultClockSkewSeconds = 60L

let private unixNow () =
    DateTimeOffset.UtcNow.ToUnixTimeSeconds()

// ─── Unmapped role/group claim discoverability ───────────────────────
//
// The OIDC provider deliberately maps only `sub` / `name` / `email` —
// `AuthenticatedUser.Roles` stays empty because the SDK permission
// model is team-membership driven, and a configurable claim-mapper is
// a tracked roadmap item. A brownfield IdP (Auth0 / Keycloak) that
// already organises users into roles/groups will have those claims
// silently dropped. This module-level flag drives a single Warn the
// first time a token carrying such a claim is seen, so the limitation
// is discoverable in the log rather than surfacing as "everyone is a
// plain member after migration" with no explanation. A benign race on
// first concurrent calls may log it 2–3×; not worth a lock for a
// process-lifetime advisory.
//
// (a) — process-lifetime warn-once flag, no Expecto reset hazard
// (test surface doesn't observe the warn-once side effect). See
// docs/platform/testing-conventions.md.
let mutable private unmappedRolesWarned = false

// ─── Auth-pipeline metrics (Cluster D1 / Phase 9e.A) ─────────────────
//
// Optional `IMetricsSink` threaded through the constructor — each
// provider instance binds its own sink at build time, with no
// process-wide mutable singleton. Phase 9e.A retired the prior
// `setMetricsSink` setter (a `mutable private metricsSink` populated
// once at compose time) in favour of this per-instance shape so each
// provider's emission state is reachable only via its closure, not via
// a module-level cell. Removes the test-isolation hazard the setter
// pattern carried (see Phase 11a.A) and matches the broader SDK rule
// that hot-path infrastructure resolves through DI / construction, not
// through process-wide singletons.

let private oidcTags = Map.ofList [ AuthMetrics.ProviderTag, "oidc" ]

let private validateExpiry (clockSkewSeconds: int64) (payload: JwtPayload) : Result<unit, JwtValidationError> =
    match payload.ExpiresAt with
    | None -> Error MissingExpiry
    | Some exp when exp + clockSkewSeconds < unixNow () -> Error TokenExpired
    | Some _ ->
        match payload.NotBefore with
        | Some nbf when nbf - clockSkewSeconds > unixNow () -> Error TokenNotYetValid
        | _ -> Ok()

let private validateIssuer (expected: string option) (payload: JwtPayload) : Result<unit, JwtValidationError> =
    match expected, payload.Issuer with
    | None, _ -> Ok()
    | Some e, Some a when e = a -> Ok()
    | Some e, Some a -> Error(InvalidIssuer(e, a))
    | Some e, None -> Error(InvalidIssuer(e, "(no iss claim)"))

let private validateAudience (expected: string option) (payload: JwtPayload) : Result<unit, JwtValidationError> =
    match expected with
    | None -> Ok()
    | Some e ->
        if payload.Audience |> List.contains e then
            Ok()
        else
            let rendered =
                match payload.Audience with
                | [] -> "(no aud claim)"
                | [ a ] -> a
                | many -> String.concat "," many

            Error(InvalidAudience(e, rendered))

// ─── Token extraction ────────────────────────────────────────────────

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
        // 0.5.3 — try Bearer header first (XHR/fetch path), fall back to
        // cookie (EventSource path). Both transports converge on the
        // same OIDC validator + subject resolution downstream — the
        // cookie path just lights up the SSE channels under the same
        // identity the REST proxies are already authenticated as.
        match extractFromBearerHeader ctx with
        | Some _ as token -> token
        | None -> extractFromCookie cookieName ctx

// ─── User mapping ────────────────────────────────────────────────────

/// Map a validated payload to `AuthenticatedUser`. `TenantId` and
/// `Roles` remain empty — provider-specific claim shapes (Clerk's
/// `org_id`, Auth0's custom roles claim, etc.) are a future concern
/// handled by a claim-mapper hook or a provider-specific sub-companion.
///
/// 0.5.4 — `preferOid` chooses between Entra's tenant-stable `oid`
/// claim and the OIDC `sub` claim for the resolved `UserId`. When
/// `true` and `payload.Oid` is `Some _`, `oid` wins; otherwise the
/// historical `sub`-only behaviour applies (so non-Entra IdPs and
/// pre-0.5.4 deployments preserve identity continuity verbatim).
/// Both claims go through `IdentitySanitiser.sanitiseScopeId` —
/// defence-in-depth against a self-issued / misconfigured IdP
/// delivering a malicious id (`../target`, `\0`, control chars,
/// Windows reserved names).
let private userFromPayload (preferOid: bool) (payload: JwtPayload) : AuthenticatedUser =
    let sanitise raw =
        match IdentitySanitiser.sanitiseScopeId raw with
        | Result.Ok value -> value
        | Result.Error _ -> "anonymous"

    let resolvedId =
        match preferOid, payload.Oid with
        | true, Some rawOid -> sanitise rawOid
        | _ -> payload.Subject |> Option.defaultValue "anonymous" |> sanitise

    // Microsoft Entra Workforce ID v2 omits `email` by default and
    // surfaces the user's address on `preferred_username` instead.
    // Without the fallback, the SDK's pending-invite-by-email path
    // (`tryConsumePendingForUser`) short-circuits on `Email = None`
    // for every Entra-signed-in invitee — they authenticate but the
    // team-membership add never fires, leaving them on their
    // per-user scope with no team data, no module permissions, and
    // only the platform-admin surfaces that don't require a team.
    // Restrict to `@`-bearing values so non-Entra IdPs that use
    // `preferred_username` for a non-email handle don't get a bogus
    // `Email = Some "alice"`. Mirrors client-side `UserSession.fs`.
    let resolvedEmail =
        match payload.Email with
        | Some _ as e -> e
        | None ->
            match payload.PreferredUsername with
            | Some pu when pu.Contains '@' -> Some pu
            | _ -> None

    {
        UserId = resolvedId
        DisplayName = payload.Name |> Option.defaultValue resolvedId
        Email = resolvedEmail
        TenantId = None
        Roles = []
    }

// ─── Validation pipeline ─────────────────────────────────────────────

/// Full validation: extract → parse → resolve JWKS → verify signature
/// → check claims → map to user. Shared by `GetUser` (lenient) and
/// `ValidateRequest` (strict). `httpClient` carries discovery / JWKS
/// fetches; production callers get the shared lazy default via
/// `fromConfig`, tests inject a stub handler via `fromConfigWith`.
let private validate
    (httpClient: HttpClient)
    (metrics: IMetricsSink)
    (logger: ILogger)
    (config: AuthConfig)
    (ctx: HttpContext)
    : Async<Result<AuthenticatedUser, JwtValidationError>> =
    let incr (counter: string) : unit = metrics.Increment(counter, oidcTags)

    async {
        match extractToken config.TokenLocation ctx with
        | None ->
            incr AuthMetrics.ValidateNoToken
            return Error NoToken
        | Some raw ->
            match parseJwt raw with
            | Error(UnsupportedAlgorithm _ as e) ->
                // `parseJwt` short-circuits on an unrecognised `alg`
                // string (anything outside RS256/RS384/RS512/ES256/PS256
                // — most notably HS256, which OIDC rejects by design).
                // Counted under the invalid-signature bucket alongside
                // the whitelist-gate rejections below.
                incr AuthMetrics.ValidateInvalidSignature
                return Error e
            | Error e ->
                incr AuthMetrics.ValidateMalformed
                return Error e
            | Ok jwt ->
                let acceptedAlgs = resolveAcceptedAlgorithms config

                if not (acceptedAlgs |> List.contains jwt.Header.Algorithm) then
                    // Whitelist gate. The signature would verify if we
                    // tried it (the algorithm is recognised), but the
                    // operator's `AcceptedAlgorithms` does not include
                    // it. Reject with the standard error path; the
                    // operator can widen the trust set if intentional.
                    incr AuthMetrics.ValidateInvalidSignature
                    return Error(UnsupportedAlgorithm(JwsAlgorithm.toString jwt.Header.Algorithm))
                else
                    match jwt.Header.KeyId with
                    | None ->
                        incr AuthMetrics.ValidateMalformed
                        return Error(MalformedToken "header has no kid")
                    | Some kid ->
                        let! urlResult = resolveJwksUrl httpClient config.KeySource

                        match urlResult with
                        | Error e -> return Error e
                        | Ok jwksUrl ->
                            // First look: current cache / fetch if cold.
                            let! keysResult = getJwks httpClient logger jwksUrl false

                            match keysResult with
                            | Error e -> return Error e
                            | Ok keys ->
                                let! resolvedKey = async {
                                    match keys.TryFind kid with
                                    | Some k -> return Ok k
                                    | None ->
                                        // kid miss: refresh once in case the provider rotated keys.
                                        let! refreshed = getJwks httpClient logger jwksUrl true

                                        match refreshed with
                                        | Ok refreshedKeys ->
                                            match refreshedKeys.TryFind kid with
                                            | Some k -> return Ok k
                                            | None -> return Error(UnknownKid kid)
                                        | Error e -> return Error e
                                }

                                match resolvedKey with
                                | Error(UnknownKid _ as e) ->
                                    incr AuthMetrics.ValidateUnknownKid
                                    return Error e
                                | Error e -> return Error e
                                | Ok key ->
                                    if not (verifyJws jwt.Header.Algorithm key jwt.SignedBytes jwt.Signature) then
                                        incr AuthMetrics.ValidateInvalidSignature
                                        return Error InvalidSignature
                                    else
                                        let skew =
                                            config.ClockSkewSeconds |> Option.defaultValue defaultClockSkewSeconds

                                        let chain =
                                            validateExpiry skew jwt.Payload
                                            |> Result.bind (fun () -> validateIssuer config.Issuer jwt.Payload)
                                            |> Result.bind (fun () -> validateAudience config.Audience jwt.Payload)

                                        match chain with
                                        | Error TokenExpired ->
                                            incr AuthMetrics.ValidateExpired
                                            return Error TokenExpired
                                        | Error(InvalidIssuer _ as e) ->
                                            incr AuthMetrics.ValidateInvalidIssuer
                                            return Error e
                                        | Error(InvalidAudience _ as e) ->
                                            incr AuthMetrics.ValidateInvalidAudience
                                            return Error e
                                        | Error e -> return Error e
                                        | Ok() ->
                                            if
                                                not unmappedRolesWarned && not jwt.Payload.UnmappedRoleClaims.IsEmpty
                                            then
                                                unmappedRolesWarned <- true

                                                logger.Warn(
                                                    sprintf
                                                        "OIDC token carries role/group claim(s) [%s] that are NOT mapped into AuthenticatedUser.Roles — the SDK permission model is team-membership driven. Users authenticate but land with no roles; assign roles via team membership, or track the claim-mapper roadmap item. This is logged once per process."
                                                        (String.concat ", " jwt.Payload.UnmappedRoleClaims)
                                                )

                                            incr AuthMetrics.ValidateSuccess
                                            let preferOid = config.PreferOidWhenPresent |> Option.defaultValue false

                                            return Ok(userFromPayload preferOid jwt.Payload)
    }

// ─── No-op logger fallback ───────────────────────────────────────────

let private noOpLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// ─── Public entry point ──────────────────────────────────────────────

/// Build an OIDC `IAuthProvider` over an explicit HttpClient + optional
/// metrics sink. Single private implementation; the four public entry
/// points delegate here with their own argument defaults.
let private buildProvider
    (httpClient: HttpClient)
    (metrics: IMetricsSink option)
    (logger: ILogger option)
    (config: AuthConfig)
    : IAuthProvider =
    // Gap audit 2026-06-12 Auth G3 — refuse cleartext key-fetch URLs at
    // construction. An `http://` issuer / JWKS URL sends the discovery +
    // JWKS fetches over plaintext, where a MITM can substitute the key
    // set and have forged tokens validate. Loopback hosts are the dev
    // escape hatch (a local mock IdP without TLS); everything else must
    // be https. Construction-time, so the misconfiguration surfaces at
    // startup rather than as silent trust in a poisoned JWKS.
    // Phase 134 — delegate the scheme decision to the shared
    // `isAcceptableKeyFetchUrl` predicate (Jwks module) so the accept
    // rule (https, or http for loopback only) lives in exactly one place
    // and the construction-time guard here can never drift from the
    // request-time discovered-`jwks_uri` / fetch guards.
    let requireHttps (label: string) (url: string) =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | false, _ -> invalidArg (nameof config) $"OidcAuthProvider {label} '{url}' is not an absolute URL."
        | true, _ ->
            if not (isAcceptableKeyFetchUrl url) then
                invalidArg
                    (nameof config)
                    $"OidcAuthProvider refuses non-https {label} '{url}': discovery/JWKS fetched over cleartext can be MITM-substituted, letting forged tokens validate. Use https (http is permitted for loopback hosts only)."

    match config.KeySource with
    | StaticSecret _ ->
        invalidArg
            (nameof config)
            "OidcAuthProvider requires KeySource = JwksDiscovery _ or JwksExplicit _. Use StaticJwtAuthProvider for StaticSecret."
    | JwksDiscovery issuer -> requireHttps "issuer" issuer
    | JwksExplicit url -> requireHttps "JWKS URL" url

    let log = logger |> Option.defaultValue noOpLogger

    let sink =
        metrics |> Option.defaultWith (fun () -> NoOpMetricsSink() :> IMetricsSink)

    { new IAuthProvider with
        member _.GetUser ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! result = validate httpClient sink log config httpCtx

            match result with
            | Ok user ->
                log.Debug $"OIDC auth ok: user={user.UserId}"
                return user
            | Error e ->
                // Lenient path: log for audit, then fall back to anonymous.
                // Missing-token is the expected case for unauthenticated
                // endpoints and would be noisy at Warn.
                match e with
                | NoToken ->
                    // No credential on the request. This is the expected,
                    // high-volume case for genuinely unauthenticated
                    // endpoints, so it stays off the Warn channel. Logged at
                    // Debug so a 401 on an endpoint that *should* have carried
                    // a Bearer can be traced to "no token arrived" by raising
                    // the log level — rather than requiring client-bundle
                    // archaeology to prove no Authorization header was sent.
                    log.Debug "OIDC auth (lenient): no token in request → anonymous"
                | _ -> log.Warn $"OIDC auth failed (lenient): {JwtValidationError.toMessage e}"

                return AuthenticatedUser.anonymous
        }

        member _.ValidateRequest ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! result = validate httpClient sink log config httpCtx

            match result with
            | Ok user ->
                log.Debug $"OIDC validate ok: user={user.UserId}"
                return Ok user
            | Error e ->
                let message = JwtValidationError.toMessage e
                log.Warn $"OIDC validate failed: {message}"
                return Error message
        }

        // RS256 JWT signature validation against OIDC-discovered (or
        // explicitly configured) JWKS — identity is cryptographically
        // proven, not header-trusted.
        member _.IsCryptographicallyVerified = true
    }

/// Construct an OIDC/JWT `IAuthProvider` from a declarative
/// `AuthConfig`. Supports RS256-signed JWTs (by default) with keys
/// fetched via OIDC discovery (`JwksDiscovery`) or from an explicit
/// JWKS URL (`JwksExplicit`). Rejects `StaticSecret` at construction —
/// use `StaticJwtAuthProvider` for HS256 flows.
///
/// `logger` receives observational events (JWKS fetch outcomes,
/// validation success / failure reasons). When omitted, events are
/// silently discarded. Messages never include the token, its signature,
/// or any secret material — only identifiers and failure classifications.
///
/// `GetUser` is lenient: any validation failure maps to
/// `AuthenticatedUser.anonymous`, matching the `IAuthProvider`
/// contract.
/// `ValidateRequest` is strict: failures surface as `Error <reason>`
/// suitable for inclusion in a 401 response body.
///
/// Variants: `fromConfigWith` accepts a custom `HttpClient` for tests
/// and advanced callers. `fromConfigMetered` / `fromConfigWithMetrics`
/// thread an `IMetricsSink` so the provider emits the standard
/// `toolup.auth.*` counters; the no-metrics variants emit nothing.
let fromConfigWith (httpClient: HttpClient) (logger: ILogger option) (config: AuthConfig) : IAuthProvider =
    buildProvider httpClient None logger config

/// Production shorthand — uses a process-wide lazy `HttpClient`.
/// Equivalent to `fromConfigWith` with the default client supplied.
let fromConfig (logger: ILogger option) (config: AuthConfig) : IAuthProvider =
    buildProvider defaultHttpClient.Value None logger config

/// Metrics-enabled production shorthand — pair with a resolved
/// `IMetricsSink` (e.g. from the SDK's DI container at compose time)
/// so the provider emits `toolup.auth.validate.*` counters tagged
/// `provider=oidc`. Passing `None` is equivalent to `fromConfig` —
/// useful for call sites that want a single conditional construction
/// path keyed off whether metrics are configured.
let fromConfigMetered (logger: ILogger option) (metrics: IMetricsSink option) (config: AuthConfig) : IAuthProvider =
    buildProvider defaultHttpClient.Value metrics logger config

/// Metrics-enabled variant of `fromConfigWith` — same role as
/// `fromConfigMetered` but accepts a custom `HttpClient`. Tests typically
/// use this with a stub `HttpMessageHandler` + a recording metrics sink
/// to assert per-outcome counter emission.
let fromConfigWithMetrics
    (httpClient: HttpClient)
    (logger: ILogger option)
    (metrics: IMetricsSink option)
    (config: AuthConfig)
    : IAuthProvider =
    buildProvider httpClient metrics logger config