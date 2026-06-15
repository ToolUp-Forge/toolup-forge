module internal ToolUp.AuthProviders.OidcAuthProviderJwks

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open ToolUp.Platform
open ToolUp.AuthProviders.OidcAuthProviderJwt

// ─── JWKS discovery and cache ────────────────────────────────────────
//
// HttpClient is passed in by the caller (typically `fromConfig` →
// `validate` → here). Production code in `OidcAuthProvider.fromConfig`
// supplies a process-wide lazy default; tests inject an HttpClient
// backed by a stub HttpMessageHandler so contract assertions don't
// need a real OIDC issuer.

/// JWKS keys come in two crypto shapes since Phase 3.A. RSA keys
/// (`kty=RSA`) carry modulus + exponent and back RS256 / RS384 / RS512 /
/// PS256; EC keys (`kty=EC`) carry curve + (x, y) coords and today back
/// ES256 (P-256). Both are `ImportParameters`-shaped (`RSAParameters`,
/// `ECParameters`) — pure value records, safe to cache, no `IDisposable`
/// surface.
type JwkKey =
    | RsaKey of RSAParameters
    | EcKey of ECParameters

type private CachedJwks = {
    Keys: Map<string, JwkKey>
    FetchedAt: DateTime
    LastRefreshAttemptAt: DateTime
}

type private CachedDiscovery = { JwksUrl: string; FetchedAt: DateTime }

/// Process-wide JWKS cache keyed by JWKS URL. Shared across provider
/// instances so repeated `fromConfig` calls with the same issuer reuse
/// the same cache. Entries live for `jwksTtl`; a `kid` miss triggers
/// one forced refresh, rate-limited to `jwksRefreshCooldown` between
/// attempts per URL to prevent refresh storms under bursty traffic or
/// replay of a forged token with an unknown kid.
let private jwksCache = ConcurrentDictionary<string, CachedJwks>()

/// Process-wide discovery cache keyed by issuer URL → resolved
/// `jwks_uri` + fetch timestamp. OIDC providers rarely rotate the JWKS
/// endpoint, so a long (`discoveryTtl`) lifetime is safe — but it is
/// bounded, not process-lifetime, so a provider that *does* rotate
/// `jwks_uri` is eventually picked up and a stale issuer no longer
/// pins memory forever.
let private discoveryCache = ConcurrentDictionary<string, CachedDiscovery>()

let private jwksTtl = TimeSpan.FromMinutes 10.0
let private jwksRefreshCooldown = TimeSpan.FromMinutes 1.0

/// Discovery entries live far longer than JWKS keys (the `jwks_uri`
/// itself rarely changes) but are still bounded so a no-longer-used
/// issuer is swept and provider-side endpoint rotation is eventually
/// observed.
let private discoveryTtl = TimeSpan.FromHours 24.0

/// Phase 134 — a key-fetch URL is acceptable only over https, with http
/// permitted for loopback hosts (the local mock-IdP dev escape hatch).
/// This is the single home of the accept rule: the construction-time
/// `requireHttps` guard in `OidcAuthProvider` delegates here, and the
/// request-time guards below (`resolveJwksUrl`'s discovered-`jwks_uri`
/// check + `fetchJwks`) call it too, so a discovered `jwks_uri` cannot
/// downgrade key-fetch to cleartext. `requireHttps` only validates the
/// *configured* issuer / explicit JWKS URL at startup; a hostile or
/// compromised OIDC metadata document can still return an `http://`
/// `jwks_uri`, which without this guard would be fetched over a
/// MITM-substitutable channel.
let isAcceptableKeyFetchUrl (url: string) : bool =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, uri when uri.Scheme = Uri.UriSchemeHttps -> true
    | true, uri when uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback -> true
    | _ -> false

/// Evict cache entries past their TTL. Self-contained sweep (no hosted
/// service — the OIDC provider is a props-injected companion the SDK
/// composition root cannot reference). Driven opportunistically from
/// the `getJwks` fetch path, which only runs on TTL expiry / kid-miss,
/// so the sweep is naturally throttled to fetch frequency and bounds
/// both caches to the set of issuers actively in use. Returns the
/// number of entries removed (diagnostics / tests).
let evictExpired (now: DateTime) : int =
    let mutable removed = 0

    for kvp in jwksCache do
        if now - kvp.Value.FetchedAt >= jwksTtl then
            if jwksCache.TryRemove(kvp.Key) |> fst then
                removed <- removed + 1

    for kvp in discoveryCache do
        if now - kvp.Value.FetchedAt >= discoveryTtl then
            if discoveryCache.TryRemove(kvp.Key) |> fst then
                removed <- removed + 1

    removed

/// Evict any discovery-cache entries that resolved to `url`. Driven from
/// the JWKS fetch-failure path: when a fetch against a discovered
/// `jwks_uri` fails, the most likely non-transient cause is the issuer
/// having rotated its `jwks_uri` — but the 24h discovery TTL would
/// otherwise keep returning the stale URL, pinning every login to the
/// dead endpoint until the TTL expires (a silent login-outage window).
/// Evicting forces the next call to re-run OIDC metadata discovery and
/// pick up the rotated endpoint. A merely transient fetch blip costs one
/// extra metadata fetch on the next call (discovery re-resolves to the
/// same URL), which is cheap and self-correcting. Returns the number of
/// entries removed.
let private evictDiscoveryForUrl (url: string) : int =
    let mutable removed = 0

    for kvp in discoveryCache do
        if kvp.Value.JwksUrl = url then
            if discoveryCache.TryRemove(kvp.Key) |> fst then
                removed <- removed + 1

    removed

let private rsaParamsFromJwk (n: string) (e: string) : RSAParameters =
    RSAParameters(Modulus = base64UrlDecode n, Exponent = base64UrlDecode e)

/// Map a JWK `crv` string to a named ECCurve. Only the curves we
/// announce support for are returned; unknown values yield `None` and
/// the JWK entry is silently dropped from the cache (the provider's
/// algorithm whitelist + signature verify will then reject any token
/// trying to use the dropped key, surfacing as `UnknownKid` upstream).
let private ecCurveFromJwk (crv: string) : ECCurve option =
    match crv with
    | "P-256" -> Some ECCurve.NamedCurves.nistP256
    | _ -> None

let private ecParamsFromJwk (crv: ECCurve) (x: string) (y: string) : ECParameters =
    ECParameters(Curve = crv, Q = ECPoint(X = base64UrlDecode x, Y = base64UrlDecode y))

let private parseJwks (json: string) : Map<string, JwkKey> =
    use doc = JsonDocument.Parse json

    match doc.RootElement.TryGetProperty "keys" with
    | true, keys when keys.ValueKind = JsonValueKind.Array ->
        keys.EnumerateArray()
        |> Seq.choose (fun key ->
            let kty = tryGetString "kty" key
            let kid = tryGetString "kid" key

            // Dispatch on `kty` — RSA keys (RS*/PS256) and EC keys
            // (ES256) both flow through the same JWKS document.
            // Anything else (`oct`, future curve families) is silently
            // dropped; the algorithm whitelist + verify path are the
            // operator-visible trust boundary, not the parse step.
            match kty, kid with
            | Some "RSA", Some kidValue ->
                match tryGetString "n" key, tryGetString "e" key with
                | Some n, Some e -> Some(kidValue, RsaKey(rsaParamsFromJwk n e))
                | _ -> None
            | Some "EC", Some kidValue ->
                match tryGetString "crv" key, tryGetString "x" key, tryGetString "y" key with
                | Some crv, Some x, Some y ->
                    match ecCurveFromJwk crv with
                    | Some curve -> Some(kidValue, EcKey(ecParamsFromJwk curve x y))
                    | None -> None
                | _ -> None
            | _ -> None)
        |> Map.ofSeq
    | _ -> Map.empty

/// Resolve `KeySource` to a JWKS URL. `JwksExplicit` returns the URL
/// verbatim; `JwksDiscovery` fetches OIDC metadata (using the supplied
/// `httpClient`) and extracts `jwks_uri`. Discovery results are cached
/// indefinitely (providers rarely rotate `jwks_uri`).
let resolveJwksUrl (httpClient: HttpClient) (source: KeySource) : Async<Result<string, JwtValidationError>> = async {
    match source with
    | JwksExplicit url -> return Ok url
    | StaticSecret _ ->
        // fromConfig rejects StaticSecret; unreachable in practice.
        return Error(MalformedToken "StaticSecret is not supported by OidcAuthProvider")
    | JwksDiscovery issuer ->
        let now = DateTime.UtcNow

        match discoveryCache.TryGetValue issuer with
        | true, cached when now - cached.FetchedAt < discoveryTtl -> return Ok cached.JwksUrl
        | _ ->
            try
                let metadataUrl = issuer.TrimEnd('/') + "/.well-known/openid-configuration"
                let! body = httpClient.GetStringAsync(metadataUrl) |> Async.AwaitTask
                use doc = JsonDocument.Parse body

                match tryGetString "jwks_uri" doc.RootElement with
                | Some jwksUrl when not (isAcceptableKeyFetchUrl jwksUrl) ->
                    // Phase 134 — the discovered endpoint is cleartext.
                    // Refuse before caching or fetching: a MITM (or a
                    // compromised IdP) that returns an `http://` jwks_uri
                    // could otherwise substitute the signing key set and
                    // have forged tokens validate. Not cached, so a fixed
                    // metadata document is re-evaluated on the next call.
                    return
                        Error(
                            JwksUnavailable
                                $"OIDC metadata at {metadataUrl} returned a cleartext jwks_uri '{jwksUrl}'; refusing to fetch signing keys over a MITM-substitutable channel (https required; http permitted for loopback hosts only)"
                        )
                | Some jwksUrl ->
                    discoveryCache[issuer] <- { JwksUrl = jwksUrl; FetchedAt = now }
                    return Ok jwksUrl
                | None -> return Error(JwksUnavailable $"OIDC metadata at {metadataUrl} has no jwks_uri")
            with ex ->
                return Error(JwksUnavailable $"discovery fetch failed: {ex.Message}")
}

let private fetchJwks (httpClient: HttpClient) (url: string) : Async<Result<Map<string, JwkKey>, JwtValidationError>> = async {
    // Phase 134 — belt-and-braces scheme guard at the fetch site, so the
    // `JwksExplicit` path enforces https-or-loopback at fetch time too
    // (construction-time `requireHttps` already checks the configured
    // value; this also catches any future caller that reaches `fetchJwks`
    // with an unchecked URL).
    if not (isAcceptableKeyFetchUrl url) then
        return
            Error(
                JwksUnavailable
                    $"refusing to fetch JWKS over cleartext URL '{url}' (https required; http permitted for loopback hosts only)"
            )
    else
        try
            let! body = httpClient.GetStringAsync(url) |> Async.AwaitTask
            return Ok(parseJwks body)
        with ex ->
            return Error(JwksUnavailable ex.Message)
}

/// Get JWKS keys for `url`, using the cache when fresh. `forceRefresh`
/// bypasses the TTL check and is used on `kid` miss. A failed refresh
/// falls back to the stale entry if one exists — prefer stale over
/// nothing during brief provider outages.
///
/// Bounded-staleness security note (Auth-core audit, by-design): the
/// stale-fallback below means a key the issuer *revoked* (e.g. after a
/// signing-key compromise) keeps validating tokens here until a
/// refresh fetch succeeds. The staleness window is bounded only by
/// provider availability, not by `jwksTtl` — once a fetch is failing,
/// the cooldown branch above also keeps serving the cached keys. This
/// is a deliberate availability-over-strict-revocation trade-off
/// (matching how mainstream OIDC libraries behave); a deployment that
/// needs hard revocation must front this with a provider that supports
/// it (token introspection / short-lived access tokens). No behaviour
/// change is implied by this note.
let getJwks
    (httpClient: HttpClient)
    (logger: ILogger)
    (url: string)
    (forceRefresh: bool)
    : Async<Result<Map<string, JwkKey>, JwtValidationError>> =
    async {
        let now = DateTime.UtcNow

        match jwksCache.TryGetValue url with
        | true, cached when not forceRefresh && now - cached.FetchedAt < jwksTtl -> return Ok cached.Keys
        | true, cached when forceRefresh && now - cached.LastRefreshAttemptAt < jwksRefreshCooldown ->
            // Rate-limited: a very recent refresh attempt is already
            // in our cache or just failed. Don't thrash on forged kids.
            return Ok cached.Keys
        | _ ->
            // Opportunistic sweep: this branch only runs on TTL expiry,
            // a kid-miss, or a cold cache, so the eviction is naturally
            // throttled to fetch frequency and keeps both caches bounded
            // to the issuers actively in use (no hosted-service needed —
            // see `evictExpired`).
            evictExpired now |> ignore

            let! result = fetchJwks httpClient url

            match result with
            | Ok keys ->
                let entry = {
                    Keys = keys
                    FetchedAt = now
                    LastRefreshAttemptAt = now
                }

                jwksCache[url] <- entry

                let reason = if forceRefresh then "refresh" else "initial"
                logger.Debug $"OIDC JWKS fetched: url={url} keys={keys.Count} ({reason})"

                return Ok keys
            | Error e ->
                logger.Warn $"OIDC JWKS fetch failed: url={url} reason={JwtValidationError.toMessage e}"
                // Endpoint-rotation recovery: drop any discovery-cache
                // entry that resolved to this (now-failing) URL so the
                // next request re-resolves `jwks_uri` from OIDC metadata
                // instead of waiting out the 24h discovery TTL. Self-
                // correcting on a transient blip (re-resolves to the same
                // URL). See `evictDiscoveryForUrl`.
                let rotatedIssuers = evictDiscoveryForUrl url

                if rotatedIssuers > 0 then
                    logger.Warn
                        $"OIDC discovery cache evicted for {rotatedIssuers} issuer(s) pointing at the failing jwks_uri {url}; the next request will re-resolve jwks_uri from metadata (picks up an endpoint rotation without waiting out the discovery TTL)"
                // Record attempt so cooldown applies next time.
                match jwksCache.TryGetValue url with
                | true, cached ->
                    jwksCache[url] <- {
                        cached with
                            LastRefreshAttemptAt = now
                    }
                    // Prefer stale cache over failing during a provider
                    // outage. See the function docstring's bounded-
                    // staleness note: a revoked key keeps validating
                    // until a fetch succeeds — deliberate availability
                    // trade-off, not a gap to "fix" by failing closed here.
                    return Ok cached.Keys
                | _ -> return Error e
    }