module ToolUp.AuthProviders.OidcAuthProvider

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Metrics
open ToolUp.AuthProviders.OidcJwksCacheTypes
open ToolUp.AuthProviders.OidcAuthProviderJwt
open ToolUp.AuthProviders.OidcAuthProviderJwks

// ─── Phase 341 — opt-in token-validation hardening knobs ─────────────
//
// A small OIDC-provider-owned options record carrying the RFC-8725-
// adjacent hardening switches that are NOT expressible as a claim-
// validation input on `AuthConfig` (which stays the declarative
// issuer/audience/keysource contract shared with every auth provider).
// Every field defaults to "prior behaviour", so a deployment that keeps
// `OidcHardening.defaults` is byte-for-byte unchanged (GP 11); the
// existing `fromConfig*` entry points delegate with the defaults, and
// the `*Hardened` variants below let a caller opt in.
//
// The `azp` multi-audience binding (RFC 8725 §3.9) is NOT a knob here —
// it is an always-on correctness fix that only changes behaviour for the
// genuinely-dangerous multi-audience-without-`azp` case; a single-
// audience token is unaffected.
type OidcHardening = {
    /// Maximum absolute token age in seconds, enforced as
    /// `iat + MaxTokenAgeSeconds > now` (with the same clock-skew
    /// tolerance as `exp`). `None` (default) applies no age bound beyond
    /// `exp`, preserving prior behaviour. When `Some`, a token with no
    /// `iat` claim is rejected (an age bound cannot be honoured without
    /// it, and an attacker must not bypass the bound by omitting `iat`).
    MaxTokenAgeSeconds: int64 option
    /// When `true`, a JWKS refresh that fails (or is within the refresh
    /// cooldown) fails validation closed rather than serving the cached
    /// (possibly-revoked) keys. `false` (default) keeps the
    /// availability-first stale-fallback. High-security deployments that
    /// prefer revocation-safety over availability opt in.
    FailClosedOnStaleJwks: bool
    /// Phase 463 — JWKS cache lifetime, and therefore the ordinary
    /// upper bound on this provider's key-revocation window. `None`
    /// (default) keeps the shipped `defaultJwksTtl` of 10 minutes.
    /// `Some ts` shortens (or lengthens) it; `Some TimeSpan.Zero`
    /// disables the JWKS cache entirely — every validation re-fetches
    /// and nothing is ever served from cache, including the stale
    /// fallback. Negative values are refused at construction.
    ///
    /// Read the revocation-window note at the head of
    /// `OidcAuthProvider.Jwks.fs` before tightening this: a zero TTL
    /// puts one JWKS round-trip on every validated request, and it does
    /// NOT bound per-token revocation (this provider does not do
    /// introspection).
    JwksCacheTtl: TimeSpan option
    /// Phase 463 — OIDC discovery (`jwks_uri`) cache lifetime. `None`
    /// (default) keeps the shipped `defaultDiscoveryTtl` of 24 hours,
    /// which is safe because providers rarely rotate the endpoint;
    /// `Some TimeSpan.Zero` re-runs metadata discovery per validation.
    /// Rotation is already recovered from without this knob — a failing
    /// fetch evicts the discovery entry — so tightening it is for
    /// deployments that want the endpoint re-read on a schedule rather
    /// than on failure.
    DiscoveryCacheTtl: TimeSpan option
    /// Phase 463 — publish a JWKS fetch failure to sibling instances so
    /// they evict their own cached key set for the same URL, collapsing
    /// the fleet-wide revocation window from "each silo's TTL,
    /// independently" to one channel round-trip. `None` (default)
    /// publishes nothing.
    ///
    /// Pair with `OidcAuthProviderJwks.subscribeToJwksEvictions` on the
    /// same channel and the same `OriginReplicaId` — publishing without
    /// a subscriber signals into the void, and the SDK cannot subscribe
    /// on the provider's behalf because the provider is a
    /// props-injected companion the composition root does not reference.
    JwksEvictionSignal: JwksEvictionSignal option
}

module OidcHardening =
    /// Behaviour-preserving defaults: no max-age bound, availability-
    /// first stale-JWKS fallback, shipped cache TTLs, no cross-instance
    /// signal. Equivalent to the pre-Phase-341 provider (GP 11).
    let defaults = {
        MaxTokenAgeSeconds = None
        FailClosedOnStaleJwks = false
        JwksCacheTtl = None
        DiscoveryCacheTtl = None
        JwksEvictionSignal = None
    }

    /// Phase 463 — project the cache-facing knobs into the policy the
    /// JWKS module reads. `OidcHardening.defaults` maps to
    /// `JwksCachePolicy.defaults` exactly.
    let toCachePolicy (hardening: OidcHardening) : JwksCachePolicy = {
        JwksTtl = hardening.JwksCacheTtl |> Option.defaultValue defaultJwksTtl
        DiscoveryTtl = hardening.DiscoveryCacheTtl |> Option.defaultValue defaultDiscoveryTtl
        FailClosedOnStale = hardening.FailClosedOnStaleJwks
        EvictionSignal = hardening.JwksEvictionSignal
    }

/// Phase 463 — the receiving half of the cross-instance eviction
/// signal, and the manual lever beside it. The cache itself lives in an
/// `internal` implementation module, so these two thin wrappers are how
/// a deployment reaches it.
///
/// Wiring, in full, for a multi-instance deployment:
///
/// 1. give each instance a stable identity — `sprintf "%s/%d"
///    Environment.MachineName Environment.ProcessId` is what the SDK's
///    own cross-replica broadcast uses, and needs no new env var;
/// 2. build the provider with `OidcHardening.JwksEvictionSignal =
///    Some { Channel = channel; OriginReplicaId = id }`;
/// 3. call `OidcJwksCache.subscribeToEvictions channel id (Some logger)`
///    once at compose time, on the SAME channel and id.
///
/// Steps 2 and 3 are separate because the provider is a props-injected
/// companion the SDK composition root does not reference — nothing in
/// the SDK can subscribe on its behalf. Publishing without subscribing
/// signals into the void; subscribing without publishing is harmless.
///
/// **The channel must be a distributed `INotificationChannel` companion
/// for this to cross instances at all.** The SDK's in-process default
/// reaches only the publishing process, which makes the whole mechanism
/// a well-formed no-op — correct for a single instance, and silently
/// useless for a fleet.
module OidcJwksCache =
    /// Subscribe this instance to sibling instances' JWKS fetch-failure
    /// signals, evicting its own cached key set for each named URL.
    /// Returns the subscription id; retain it if the deployment ever
    /// needs to `Unsubscribe`. Idempotent and self-echo-suppressing.
    let subscribeToEvictions
        (channel: INotificationChannel)
        (originReplicaId: string)
        (logger: ILogger option)
        : Async<NotificationSubscriptionId> =
        subscribeToJwksEvictions channel originReplicaId logger

    /// Evict this instance's cached JWKS key set for exactly one URL.
    /// `true` when an entry was present and removed. The supported
    /// manual lever for an operator who has just revoked a signing key
    /// and does not want to wait out the TTL. A URL that was never
    /// cached is a `false`-returning no-op, never an error.
    let evictUrl (jwksUrl: string) : bool = evictJwksUrl jwksUrl

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

let private renderAudience (audience: string list) : string =
    match audience with
    | [] -> "(no aud claim)"
    | [ a ] -> a
    | many -> String.concat "," many

let private validateAudience (expected: string option) (payload: JwtPayload) : Result<unit, JwtValidationError> =
    match expected with
    | None -> Ok()
    | Some e ->
        if not (payload.Audience |> List.contains e) then
            Error(InvalidAudience(e, renderAudience payload.Audience))
        else
            // RFC 8725 §3.9 — a MULTI-audience token (`aud` carries more
            // than one entry) additionally requires the authorized-party
            // claim (`azp`, or `client_id`) to name THIS application.
            // Without it a token minted for `[thisApp, attackerApp]` by
            // the shared issuer would validate here even though it was
            // authorized for a different party. Single-audience tokens
            // are unaffected — the `azp` binding does not apply and the
            // membership check above is the whole test (GP 11).
            match payload.Audience with
            | _ :: _ :: _ ->
                match payload.AuthorizedParty with
                | Some azp when azp = e -> Ok()
                | Some azp ->
                    Error(
                        InvalidAudience(
                            e,
                            sprintf
                                "multi-audience token [%s] with azp/client_id=%s that does not match the expected audience (RFC 8725 §3.9)"
                                (renderAudience payload.Audience)
                                azp
                        )
                    )
                | None ->
                    Error(
                        InvalidAudience(
                            e,
                            sprintf
                                "multi-audience token [%s] carries no azp/client_id to disambiguate the authorized party (RFC 8725 §3.9)"
                                (renderAudience payload.Audience)
                        )
                    )
            | _ -> Ok()

/// Phase 341 — bound the absolute age of an accepted token via `iat`,
/// independent of `exp`. `None` max-age applies no bound (prior
/// behaviour). When configured, a token with no `iat` is rejected (the
/// bound cannot be honoured, and must not be bypassable by omitting the
/// claim). The same clock-skew tolerance as `exp` is applied so a small
/// NTP drift between IdP and host doesn't spuriously age a fresh token.
let private validateMaxAge
    (clockSkewSeconds: int64)
    (maxAgeSeconds: int64 option)
    (payload: JwtPayload)
    : Result<unit, JwtValidationError> =
    match maxAgeSeconds with
    | None -> Ok()
    | Some maxAge ->
        match payload.IssuedAt with
        | None ->
            Error(MalformedToken "OidcHardening.MaxTokenAgeSeconds is configured but the token carries no iat claim")
        | Some iat when iat + maxAge + clockSkewSeconds < unixNow () -> Error(TokenTooOld maxAge)
        | Some _ -> Ok()

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

// ─── Claim mapping (post-validation projection) ──────────────────────
//
// `AuthConfig.ClaimMapping` names the claims a deployment wants
// projected onto `UserId` / `TenantId` in place of the built-in `sub`.
// The claims an operator may name are open-ended, so they cannot be
// pre-parsed into the typed `JwtPayload` — the payload segment is
// re-read here for the named names only.
//
// **The re-read is signature-safe** and this is the whole reason it is
// placed where it is: it runs only on the `Ok()` branch of the
// validation chain, i.e. after the signature has verified against the
// resolved JWKS key and after `iss` / `aud` / `exp` / `nbf` / max-age
// have all passed. The bytes are already trusted at that point, so
// decoding them without re-verifying adds no trust; it only reads more
// of what the validator already accepted. It can never ADMIT a token —
// every path below either returns the token's user (possibly remapped)
// or an error.
//
// This mirrors the pattern the deprecated Entra companion decorator
// uses for its `oid` -> UserId / `tid` -> TenantId remapping, and
// generalises it: the claim NAMES become configuration instead of being
// hard-coded per IdP. It uses the same shared `ToolUp.Platform.Base64Url`
// codec (re-exported as `base64UrlDecode` by the `.Jwt` partial), so
// there is one base64url implementation on this path, not two.
//
// Semantics are FAIL-CLOSED, and deliberately stricter than the
// companion's fallback chain — see `ClaimMapping` in `AuthConfig.fs` for
// the rationale. A claim that is absent, non-string, empty, or refused
// by `IdentitySanitiser` rejects the request naming the claim, rather
// than silently reverting to `sub`.

/// Read a single mapped claim from an already-validated payload element,
/// returning the sanitised value or the operator-facing reason it could
/// not be used. `IdentitySanitiser.sanitiseScopeId` is the same guard
/// `userFromPayload` applies to `sub` / `oid` — a mapped claim becomes a
/// storage-scope container name exactly as they do, so it is held to the
/// identical rule rather than a per-seam dialect.
let private readMappedClaim (root: JsonElement) (claim: string) : Result<string, string> =
    match tryGetString claim root with
    | None ->
        // Distinguish "absent" from "present but not a string" — an
        // operator debugging a mapping needs to know whether to change
        // the IdP's claim set or the claim's type.
        match root.TryGetProperty claim with
        | true, v -> Error $"the claim is present but its JSON value is {v.ValueKind}, not a string"
        | _ -> Error "the claim is absent from the token payload"
    | Some raw when String.IsNullOrWhiteSpace raw -> Error "the claim is present but empty"
    | Some raw ->
        match IdentitySanitiser.sanitiseScopeId raw with
        | Result.Ok clean -> Result.Ok clean
        | Result.Error reason ->
            // Never echo the raw claim value back into a log line — it is
            // attacker-controlled at this boundary by construction.
            Error $"the claim's value is not a usable scope identifier ({reason})"

/// Apply a `ClaimMapping` to an already-validated `AuthenticatedUser`,
/// given the raw token the validator accepted.
///
/// **Performs no validation of its own.** The caller MUST have verified
/// the token's signature, issuer, audience and expiry first — the
/// validation pipeline has, by the time it calls this. Exposed publicly
/// (rather than kept private to `validate`) so conformance packs can
/// drive the shipped mapping rather than a re-implementation of it: a
/// re-implementation is exactly what would let the two drift, and the
/// mapping's whole job is identity continuity.
///
/// Returns `Ok user` unchanged when the mapping names no claim, and
/// `Error (claim, reason)` when a named claim cannot be honoured
/// (fail-closed). The claim name is returned separately from the reason
/// so callers can classify without parsing a message: the validation
/// pipeline puts it in `MappedClaimUnusable`, and a conformance pack can
/// assert WHICH claim was refused rather than only that one was.
let applyValidatedClaimMapping
    (mapping: ClaimMapping)
    (rawToken: string)
    (user: AuthenticatedUser)
    : Result<AuthenticatedUser, string * string> =
    if ClaimMapping.isEmpty mapping then
        Result.Ok user
    else
        // The claim name the failure is attributed to when the payload
        // itself is unreadable: whichever claim the mapping names first.
        let firstNamed =
            mapping.UserIdClaim
            |> Option.orElse mapping.TenantIdClaim
            |> Option.defaultValue ""

        let parsed =
            try
                let parts = rawToken.Split('.')

                if parts.Length <> 3 then
                    Result.Error(firstNamed, "the token does not have three base64url segments")
                else
                    Result.Ok(JsonDocument.Parse(Encoding.UTF8.GetString(base64UrlDecode parts[1])))
            with ex ->
                // The validator already accepted this token, so a parse
                // failure here is implausible. It is still an error and
                // not a silent pass: with a mapping configured there is
                // no safe identity to fall through to.
                Result.Error(firstNamed, $"the token payload could not be re-read after validation ({ex.Message})")

        match parsed with
        | Result.Error failure -> Result.Error failure
        | Result.Ok document ->
            use doc = document
            let root = doc.RootElement

            let applyOne
                (claim: string option)
                (label: string)
                (set: string -> AuthenticatedUser -> AuthenticatedUser)
                (current: AuthenticatedUser)
                =
                match claim with
                | None -> Result.Ok current
                | Some name ->
                    readMappedClaim root name
                    |> Result.mapError (fun reason -> name, $"{label} mapping: {reason}")
                    |> Result.map (fun value -> set value current)

            applyOne mapping.UserIdClaim "UserId" (fun value u -> { u with UserId = value }) user
            |> Result.bind (applyOne mapping.TenantIdClaim "TenantId" (fun value u -> { u with TenantId = Some value }))

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
    (hardening: OidcHardening)
    (cachePolicy: JwksCachePolicy)
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
                        let! urlResult = resolveJwksUrlWithTtl cachePolicy.DiscoveryTtl httpClient config.KeySource

                        match urlResult with
                        | Error e -> return Error e
                        | Ok jwksUrl ->
                            // First look: current cache / fetch if cold.
                            let! keysResult = getJwksWithPolicy cachePolicy httpClient logger jwksUrl false

                            match keysResult with
                            | Error e -> return Error e
                            | Ok keys ->
                                let! resolvedKey = async {
                                    match keys.TryFind kid with
                                    | Some k -> return Ok k
                                    | None ->
                                        // kid miss: refresh once in case the provider rotated keys.
                                        let! refreshed = getJwksWithPolicy cachePolicy httpClient logger jwksUrl true

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
                                            |> Result.bind (fun () ->
                                                validateMaxAge skew hardening.MaxTokenAgeSeconds jwt.Payload)

                                        match chain with
                                        | Error TokenExpired ->
                                            incr AuthMetrics.ValidateExpired
                                            return Error TokenExpired
                                        | Error(TokenTooOld _ as e) ->
                                            // Age-bound rejection is an expiry-class outcome —
                                            // count it under the same `validate.expired` meter so
                                            // dashboards see "token too old" alongside "token
                                            // expired" without a new counter.
                                            incr AuthMetrics.ValidateExpired
                                            return Error e
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

                                            let preferOid = config.PreferOidWhenPresent |> Option.defaultValue false
                                            let baseUser = userFromPayload preferOid jwt.Payload

                                            // Claim mapping runs LAST, on the fully-
                                            // validated token, and only when a deployment
                                            // configured one. `None` short-circuits to the
                                            // historical result byte-for-byte (GP 11 / GP 13
                                            // — an unmapped deployment does not even parse
                                            // the payload a second time). A configured
                                            // `UserIdClaim` overrides `PreferOidWhenPresent`
                                            // because it is the explicit operator
                                            // instruction and the stricter of the two.
                                            match config.ClaimMapping with
                                            | None ->
                                                incr AuthMetrics.ValidateSuccess
                                                return Ok baseUser
                                            | Some mapping ->
                                                match applyValidatedClaimMapping mapping raw baseUser with
                                                | Result.Ok mapped ->
                                                    incr AuthMetrics.ValidateSuccess
                                                    return Ok mapped
                                                | Result.Error(claim, reason) ->
                                                    // Counted as a malformed-token outcome:
                                                    // from the deployment's point of view the
                                                    // token did not carry what this deployment
                                                    // requires of it. A dedicated counter would
                                                    // be a new metric name on a hot path for a
                                                    // condition that is a misconfiguration, not
                                                    // a traffic pattern — the Warn line below
                                                    // is the diagnosable signal.
                                                    incr AuthMetrics.ValidateMalformed

                                                    logger.Warn(
                                                        sprintf
                                                            "OIDC claim mapping could not be honoured for an otherwise-valid token — claim '%s': %s. Rejecting fail-closed rather than falling back to `sub` (AuthConfig.ClaimMapping)."
                                                            claim
                                                            reason
                                                    )

                                                    return Error(MappedClaimUnusable(claim, reason))
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
/// metrics sink. Single private implementation; the public entry points
/// delegate here with their own argument defaults. `hardening` carries
/// the Phase 341 opt-in switches; the non-`*Hardened` entry points pass
/// `OidcHardening.defaults` (byte-for-byte prior behaviour, GP 11).
let private buildProvider
    (httpClient: HttpClient)
    (metrics: IMetricsSink option)
    (logger: ILogger option)
    (hardening: OidcHardening)
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

    // Phase 463 — the cache knobs are validated at construction, the
    // same place and in the same shape as the cleartext-URL guard above.
    // A negative TTL has no coherent reading (`ttl <= 0` already means
    // "no cache", so a negative would silently alias to it), and a blank
    // replica id would have every instance in a fleet publish under the
    // empty identity — at which point each discards every sibling's
    // signal as its own echo, and the fanout tests as wired while doing
    // nothing. Both surface at startup rather than as a subtly-wrong
    // revocation window nobody can see from the outside.
    let requireNonNegativeTtl (label: string) (ttl: TimeSpan option) =
        match ttl with
        | Some t when t < TimeSpan.Zero ->
            invalidArg
                (nameof hardening)
                $"OidcAuthProvider {label} '{t}' is negative. Use TimeSpan.Zero to disable the cache (every validation re-fetches and nothing is served from cache), or a positive lifetime."
        | _ -> ()

    requireNonNegativeTtl (nameof hardening.JwksCacheTtl) hardening.JwksCacheTtl
    requireNonNegativeTtl (nameof hardening.DiscoveryCacheTtl) hardening.DiscoveryCacheTtl

    match hardening.JwksEvictionSignal with
    | Some s when String.IsNullOrWhiteSpace s.OriginReplicaId ->
        invalidArg
            (nameof hardening)
            "OidcAuthProvider JwksEvictionSignal.OriginReplicaId is blank. It identifies THIS instance so a receiver can discard its own echo; with every instance sharing the empty identity, each would discard the others' eviction signals and the cross-instance window would silently stay at the full per-silo TTL. Use a stable per-instance value (e.g. sprintf \"%s/%d\" Environment.MachineName Environment.ProcessId)."
    | _ -> ()

    let cachePolicy = OidcHardening.toCachePolicy hardening

    let log = logger |> Option.defaultValue noOpLogger

    let sink =
        metrics |> Option.defaultWith (fun () -> NoOpMetricsSink() :> IMetricsSink)

    { new IAuthProvider with
        member _.GetUser ctx = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let! result = validate httpClient sink log config hardening cachePolicy httpCtx

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
            let! result = validate httpClient sink log config hardening cachePolicy httpCtx

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
/// `toolup.auth.*` counters; the no-metrics variants emit nothing. The
/// `*Hardened` variants (Phase 341) additionally take an `OidcHardening`
/// record to opt into the max-token-age / fail-closed-on-stale-JWKS
/// switches; every non-`*Hardened` entry point uses
/// `OidcHardening.defaults` (prior behaviour, GP 11).
let fromConfigWith (httpClient: HttpClient) (logger: ILogger option) (config: AuthConfig) : IAuthProvider =
    buildProvider httpClient None logger OidcHardening.defaults config

/// Production shorthand — uses a process-wide lazy `HttpClient`.
/// Equivalent to `fromConfigWith` with the default client supplied.
let fromConfig (logger: ILogger option) (config: AuthConfig) : IAuthProvider =
    buildProvider defaultHttpClient.Value None logger OidcHardening.defaults config

/// Metrics-enabled production shorthand — pair with a resolved
/// `IMetricsSink` (e.g. from the SDK's DI container at compose time)
/// so the provider emits `toolup.auth.validate.*` counters tagged
/// `provider=oidc`. Passing `None` is equivalent to `fromConfig` —
/// useful for call sites that want a single conditional construction
/// path keyed off whether metrics are configured.
let fromConfigMetered (logger: ILogger option) (metrics: IMetricsSink option) (config: AuthConfig) : IAuthProvider =
    buildProvider defaultHttpClient.Value metrics logger OidcHardening.defaults config

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
    buildProvider httpClient metrics logger OidcHardening.defaults config

/// Phase 341 — production shorthand with the token-validation hardening
/// switches. Equivalent to `fromConfig` but with an explicit
/// `OidcHardening` (max-token-age bound and/or fail-closed-on-stale
/// JWKS). `OidcHardening.defaults` reproduces `fromConfig` exactly.
let fromConfigHardened (logger: ILogger option) (hardening: OidcHardening) (config: AuthConfig) : IAuthProvider =
    buildProvider defaultHttpClient.Value None logger hardening config

/// Phase 341 — `fromConfigWith` with the hardening switches. Accepts a
/// custom `HttpClient` (tests / advanced callers) alongside the
/// `OidcHardening` record.
let fromConfigWithHardened
    (httpClient: HttpClient)
    (logger: ILogger option)
    (hardening: OidcHardening)
    (config: AuthConfig)
    : IAuthProvider =
    buildProvider httpClient None logger hardening config

/// Phase 341 — metrics-enabled variant with the hardening switches:
/// `fromConfigWithMetrics` plus an `OidcHardening` record, so a metered
/// deployment can also opt into max-token-age / fail-closed-on-stale.
let fromConfigWithMetricsHardened
    (httpClient: HttpClient)
    (logger: ILogger option)
    (metrics: IMetricsSink option)
    (hardening: OidcHardening)
    (config: AuthConfig)
    : IAuthProvider =
    buildProvider httpClient metrics logger hardening config