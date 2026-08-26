module internal ToolUp.AuthProviders.OidcAuthProviderJwks

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AuthProviders.OidcJwksCacheTypes
open ToolUp.AuthProviders.OidcAuthProviderJwt

// ─── JWKS discovery and cache ────────────────────────────────────────
//
// HttpClient is passed in by the caller (typically `fromConfig` →
// `validate` → here). Production code in `OidcAuthProvider.fromConfig`
// supplies a process-wide lazy default; tests inject an HttpClient
// backed by a stub HttpMessageHandler so contract assertions don't
// need a real OIDC issuer.
//
// ─── THE REVOCATION WINDOW — read this before changing the cache ────
//
// This module is the whole of the OIDC provider's key-revocation
// posture, and the honest statement of it is:
//
//   **A signing key the issuer has revoked keeps validating tokens on
//   this instance until a JWKS fetch succeeds and returns a key set
//   without it. By default that is up to `defaultJwksTtl` (10 minutes)
//   while the issuer is reachable, and UNBOUNDED while JWKS fetches are
//   failing** — because the stale-fallback below prefers serving the
//   last-known-good key set over failing every login during a provider
//   outage.
//
// That default matches how mainstream OIDC libraries behave and is the
// right trade for most deployments: an issuer blip should not take the
// application down. It is the wrong trade for a deployment whose threat
// model includes signing-key compromise, so all three levers are now
// explicit rather than baked in (Phase 463):
//
//   * `JwksCachePolicy.JwksTtl` — shorten the ordinary window. `10:00`
//     is the default; `TimeSpan.Zero` disables the JWKS cache entirely
//     (every validation re-fetches, and NOTHING is ever served from
//     cache — including the stale fallback), which is the tightest
//     window this provider can offer and costs one round-trip per
//     validated request.
//   * `JwksCachePolicy.FailClosedOnStale` (Phase 341) — bound the
//     OUTAGE window: a failing refresh surfaces the error instead of
//     serving keys that may have been revoked since the last success.
//   * `JwksCachePolicy.EvictionSignal` — bound the window ACROSS
//     instances. A fetch failure on one instance is published as a
//     `CustomNotification`; subscribed siblings evict their own entry
//     for that URL, so a fleet converges in one channel round-trip
//     instead of each silo waiting out its own TTL independently.
//
// **What this provider still does NOT do: token introspection
// (RFC 7662).** Revocation is observed only through the key set. A
// token whose *individual* grant was revoked while its signing key
// remains published keeps validating until `exp`, regardless of every
// knob above. A deployment that needs per-token revocation needs an
// introspection call on the validation path, which this provider does
// not make (it would put a synchronous IdP round-trip on every request
// and is not implemented). Keep access-token lifetimes short.

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

let private jwksRefreshCooldown = TimeSpan.FromMinutes 1.0

// ─── Phase 463 — cross-instance JWKS eviction signal ─────────────────
//
// The caches above are PROCESS-LOCAL. A JWKS fetch failure on instance A
// evicts A's discovery entry (see `evictDiscoveryForUrl`) and, in strict
// mode, fails A's validations closed — but instance B knows nothing and
// keeps serving its own cached key set for the rest of ITS TTL. So the
// fleet-wide revocation window is not `JwksTtl`; it is `JwksTtl`
// measured independently per silo, with no instance able to observe
// another's trouble.
//
// This closes that by mirroring the Phase 22b key-destruction broadcast
// exactly: publish a `CustomNotification` on
// `NotificationKind.PlatformReservedScope`, and let every subscribed
// sibling evict its own entry for the named URL. The window across the
// fleet then collapses to the channel companion's fanout latency plus
// one JWKS round-trip.
//
// **Portability rule 5 (no cross-shard ordering) is satisfied** — cache
// eviction is idempotent and order-insensitive. Two envelopes for the
// same URL, or envelopes for different URLs arriving in any order,
// converge on the same state: every instance has evicted.
//
// **Opt-in, and a no-op when unwired (GP 11 / GP 13).** An
// `OidcHardening` that leaves `JwksEvictionSignal = None` publishes
// nothing and allocates nothing. The SDK's default in-process channel
// reaches only the publishing process, which is exactly right for a
// single-instance deployment and is why wiring it there is harmless.

/// Phase 463 — JSON options for the eviction envelope. The F# converter
/// set is mandatory (records / `DateTimeOffset` all break on a bare
/// `JsonSerializerOptions`); constructed once at module level per the
/// SDK's SSE / non-Remoting JSON convention.
let private envelopeJson = FableConverters.create ()

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
///
/// Deliberately swept against the DEFAULT TTLs rather than a caller's
/// configured ones: this is a memory bound, not a trust boundary. What
/// decides whether a cached key set may be SERVED is the per-call
/// `JwksCachePolicy.JwksTtl` on the read path below, so a deployment
/// that shortens its TTL is never served a key set older than it asked
/// for — an entry merely lingers in memory a little longer before the
/// sweep collects it. (The caches are process-wide and shared across
/// provider instances, so a sweep keyed to one instance's tightened TTL
/// would evict entries another instance is entitled to serve.)
let evictExpired (now: DateTime) : int =
    let mutable removed = 0

    for kvp in jwksCache do
        if now - kvp.Value.FetchedAt >= defaultJwksTtl then
            if jwksCache.TryRemove(kvp.Key) |> fst then
                removed <- removed + 1

    for kvp in discoveryCache do
        if now - kvp.Value.FetchedAt >= defaultDiscoveryTtl then
            if discoveryCache.TryRemove(kvp.Key) |> fst then
                removed <- removed + 1

    removed

/// Phase 463 — evict the cached JWKS key set for exactly one URL.
/// `true` when an entry was present and removed. The receiving half of
/// the cross-instance eviction signal, and a supported manual lever for
/// an operator who has just revoked a signing key and does not want to
/// wait out the TTL. Idempotent: a URL that was never cached is a
/// `false`-returning no-op, never an error.
let evictJwksUrl (url: string) : bool = jwksCache.TryRemove url |> fst

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

/// Phase 463 — `resolveJwksUrl` with an explicit discovery-cache
/// lifetime. `TimeSpan.Zero` disables the discovery cache: every call
/// re-runs OIDC metadata discovery, so a rotated `jwks_uri` is observed
/// immediately at the cost of one extra fetch per validation. The
/// zero-TTL path neither reads nor writes the cache, so a strict
/// provider sharing the process with a default one never disturbs the
/// other's entry.
let resolveJwksUrlWithTtl
    (discoveryTtl: TimeSpan)
    (httpClient: HttpClient)
    (source: KeySource)
    : Async<Result<string, JwtValidationError>> =
    async {
        let cachingDisabled = discoveryTtl <= TimeSpan.Zero

        match source with
        | JwksExplicit url -> return Ok url
        | StaticSecret _ ->
            // fromConfig rejects StaticSecret; unreachable in practice.
            return Error(MalformedToken "StaticSecret is not supported by OidcAuthProvider")
        | JwksDiscovery issuer ->
            let now = DateTime.UtcNow

            match discoveryCache.TryGetValue issuer with
            | true, cached when not cachingDisabled && now - cached.FetchedAt < discoveryTtl -> return Ok cached.JwksUrl
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
                        if not cachingDisabled then
                            discoveryCache[issuer] <- { JwksUrl = jwksUrl; FetchedAt = now }

                        return Ok jwksUrl
                    | None -> return Error(JwksUnavailable $"OIDC metadata at {metadataUrl} has no jwks_uri")
                with ex ->
                    return Error(JwksUnavailable $"discovery fetch failed: {ex.Message}")
    }

/// Resolve `KeySource` to a JWKS URL against the default 24-hour
/// discovery-cache lifetime. Retained unchanged as the zero-argument-
/// growth entry point; `resolveJwksUrlWithTtl` is the configurable one.
let resolveJwksUrl (httpClient: HttpClient) (source: KeySource) : Async<Result<string, JwtValidationError>> =
    resolveJwksUrlWithTtl defaultDiscoveryTtl httpClient source

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

/// Phase 463 — publish a JWKS fetch failure so sibling instances evict
/// their own cached key set for the same URL. Best-effort by
/// construction: a channel that throws or blocks must never turn a
/// recoverable auth outage into a worse one, so every failure here is
/// swallowed after a Warn. The caller's own eviction / fail-closed
/// decision has already been taken and does not depend on this.
let private publishFetchFailure
    (logger: ILogger)
    (signal: JwksEvictionSignal option)
    (url: string)
    (reason: string)
    : Async<unit> =
    async {
        match signal with
        | None -> return ()
        | Some s ->
            try
                let envelope: JwksFetchFailedEnvelope = {
                    JwksUrl = url
                    Reason = reason
                    FailedAt = DateTimeOffset.UtcNow
                    OriginReplicaId = s.OriginReplicaId
                }

                do!
                    s.Channel.Publish(
                        NotificationKind.PlatformReservedScope,
                        CustomNotification(
                            JwksFetchFailedNotification.NotificationKey,
                            JsonSerializer.Serialize(envelope, envelopeJson)
                        )
                    )

                logger.Debug
                    $"OIDC JWKS eviction signal published: url={url} origin={s.OriginReplicaId} (sibling instances will evict their cached key set)"
            with ex ->
                logger.Warn
                    $"OIDC JWKS eviction signal could not be published for {url}: {ex.Message}. Sibling instances will fall back to waiting out their own cache TTL."
    }

/// Phase 463 — subscribe this instance to the cross-instance JWKS
/// eviction signal. Call once at compose time on the same
/// `INotificationChannel` the provider's `JwksEvictionSignal` publishes
/// to, passing the SAME `originReplicaId` this instance publishes under
/// so its own echo is discarded rather than causing a pointless
/// evict-and-refetch.
///
/// Returns the subscription id; retain it if the deployment ever needs
/// to `Unsubscribe`. Eviction is idempotent and in-memory, so the
/// handler stays synchronous (the documented portability-rule-2
/// exemption `INotificationChannel` already carries) and a malformed or
/// unknown-URL payload is a no-op rather than an error.
let subscribeToJwksEvictions
    (channel: INotificationChannel)
    (originReplicaId: string)
    (logger: ILogger option)
    : Async<NotificationSubscriptionId> =
    async {
        let log =
            logger
            |> Option.defaultValue (
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = ()
                    member _.Warn _ = ()
                    member _.Error(_, _) = ()
                }
            )

        let handler (env: NotificationEnvelope) =
            match env.Notification with
            | CustomNotification(key, payloadJson) when key = JwksFetchFailedNotification.NotificationKey ->
                try
                    let decoded =
                        JsonSerializer.Deserialize<JwksFetchFailedEnvelope>(payloadJson, envelopeJson)

                    // A payload that deserialises to null, or carries no
                    // URL, names nothing to evict. Ignore rather than
                    // clear the whole cache: a broadcast storm that
                    // emptied every instance's key set on a malformed
                    // message would be a denial-of-service lever.
                    if
                        not (obj.ReferenceEquals(decoded, null))
                        && not (String.IsNullOrWhiteSpace decoded.JwksUrl)
                    then
                        if decoded.OriginReplicaId = originReplicaId then
                            // Own echo — the in-process channel always
                            // delivers a publish back to its publisher.
                            // Evicting here would discard the entry this
                            // instance is about to re-fetch anyway.
                            ()
                        elif evictJwksUrl decoded.JwksUrl then
                            log.Warn
                                $"OIDC JWKS cache evicted for {decoded.JwksUrl} on a fetch-failure signal from instance {decoded.OriginReplicaId} ({decoded.Reason}); the next validation re-fetches instead of waiting out this instance's cache TTL"
                        else
                            log.Debug
                                $"OIDC JWKS eviction signal for {decoded.JwksUrl} from {decoded.OriginReplicaId}: nothing cached here, no-op"
                with ex ->
                    log.Warn $"OIDC JWKS eviction signal could not be decoded: {ex.Message}"
            | _ -> ()

        return! channel.Subscribe(NotificationKind.PlatformReservedScope, handler)
    }

/// Phase 341 — `getJwksCore` factors the cache-freshness / cooldown /
/// stale-fallback logic behind explicit `ttl` + `cooldown` parameters so
/// the time-gated stale-key window is deterministically exercisable in
/// tests (the public `getJwks` always passes the module constants, so a
/// deployment is byte-for-byte unchanged — GP 11). Internal test seam,
/// never a shipped surface: `getJwks` is the only production entry point.
///
/// `failClosedOnStale` closes the availability-over-revocation trade-off
/// described below for high-security deployments (opt-in via
/// `OidcHardening.FailClosedOnStaleJwks`): instead of serving cached
/// (possibly-revoked) keys when a refresh fetch fails / is within the
/// cooldown, it returns the fetch `Error` so validation fails closed.
///
/// Phase 463 — `ttl` is now operator-configurable, and `TimeSpan.Zero`
/// means the JWKS cache is DISABLED rather than merely always-expired.
/// That distinction is the whole of task D's second assertion: an
/// always-expired cache still serves its stale entry when the refetch
/// fails, so "TTL 0" would otherwise leave exactly the unbounded
/// revocation window an operator set it to zero to close. Disabled
/// therefore means no read, no write, and no stale fallback — the
/// entry is never consulted for any purpose, and another provider
/// instance's entry for the same URL is left untouched.
///
/// `signal`, when present, publishes a fetch failure to sibling
/// instances (see the eviction-signal note above). Best-effort and
/// never load-bearing for this instance's own outcome.
let internal getJwksCoreWith
    (signal: JwksEvictionSignal option)
    (httpClient: HttpClient)
    (logger: ILogger)
    (url: string)
    (forceRefresh: bool)
    (failClosedOnStale: bool)
    (ttl: TimeSpan)
    (cooldown: TimeSpan)
    : Async<Result<Map<string, JwkKey>, JwtValidationError>> =
    async {
        let now = DateTime.UtcNow

        // Zero TTL is "no cache", not "cache that has always expired".
        let cachingDisabled = ttl <= TimeSpan.Zero

        // Both routes that would hand back a cached key set without a
        // successful fetch behind it — the cooldown serve and the
        // fetch-failure fallback — are refused under strict mode OR a
        // disabled cache. Same remedy, same reasoning: neither caller
        // has consented to being served keys that may have been revoked.
        let refuseCachedKeys = failClosedOnStale || cachingDisabled

        match jwksCache.TryGetValue url with
        | true, cached when not cachingDisabled && not forceRefresh && now - cached.FetchedAt < ttl ->
            return Ok cached.Keys
        | true, cached when
            not cachingDisabled
            && forceRefresh
            && now - cached.LastRefreshAttemptAt < cooldown
            ->
            // Rate-limited: a very recent refresh attempt is already
            // in our cache or just failed. Don't thrash on forged kids.
            // Strict mode treats this cooldown-served cache as a stale
            // window and fails closed rather than serving keys that may
            // have been revoked since the last successful fetch.
            if refuseCachedKeys then
                logger.Warn
                    $"OIDC JWKS strict mode: refusing to serve cached keys for {url} within the refresh cooldown (fail-closed-on-stale is enabled)"

                return
                    Error(
                        JwksUnavailable
                            $"strict JWKS mode: refresh cooldown active for {url}, refusing to serve cached keys"
                    )
            else
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
                if not cachingDisabled then
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
                let failureReason = JwtValidationError.toMessage e
                logger.Warn $"OIDC JWKS fetch failed: url={url} reason={failureReason}"

                // Phase 463 — tell the rest of the fleet. Awaited rather
                // than fire-and-forget so a channel that accepts the
                // publish has done so before this validation's outcome is
                // returned; the publish itself swallows its own failures,
                // so this can neither throw nor change the outcome below.
                do! publishFetchFailure logger signal url failureReason

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
                // Record attempt so cooldown applies next time. Skipped
                // entirely when caching is disabled: this provider is not
                // participating in the cache, so it must not mutate an
                // entry a differently-configured provider in the same
                // process owns and is entitled to serve.
                match jwksCache.TryGetValue url with
                | true, cached when not cachingDisabled ->
                    jwksCache[url] <- {
                        cached with
                            LastRefreshAttemptAt = now
                    }

                    if failClosedOnStale then
                        // Strict mode: a revoked key must NOT keep
                        // validating. Fail closed with the fetch error
                        // rather than serving the (possibly-revoked)
                        // stale cache. The cooldown/LastRefreshAttempt
                        // above is still recorded so healthy retries stay
                        // rate-limited.
                        logger.Warn
                            $"OIDC JWKS strict mode: fetch failed for {url} and fail-closed-on-stale is enabled — refusing to serve the stale cache"

                        return Error e
                    else
                        // Prefer stale cache over failing during a provider
                        // outage. See the module note: a revoked key keeps
                        // validating until a fetch succeeds — deliberate
                        // availability trade-off, opt out via
                        // `OidcHardening.FailClosedOnStaleJwks`.
                        return Ok cached.Keys
                | _ -> return Error e
    }

/// Phase 341 — the seven-argument core, retained verbatim so the
/// existing contract tests and any partial application keep compiling.
/// Delegates with no cross-instance eviction signal (the pre-Phase-463
/// behaviour).
let internal getJwksCore
    (httpClient: HttpClient)
    (logger: ILogger)
    (url: string)
    (forceRefresh: bool)
    (failClosedOnStale: bool)
    (ttl: TimeSpan)
    (cooldown: TimeSpan)
    : Async<Result<Map<string, JwkKey>, JwtValidationError>> =
    getJwksCoreWith None httpClient logger url forceRefresh failClosedOnStale ttl cooldown

/// Phase 463 — the policy-driven read path the provider actually calls.
/// `JwksCachePolicy.defaults` is byte-for-byte the pre-Phase-463
/// behaviour (GP 11).
let getJwksWithPolicy
    (policy: JwksCachePolicy)
    (httpClient: HttpClient)
    (logger: ILogger)
    (url: string)
    (forceRefresh: bool)
    : Async<Result<Map<string, JwkKey>, JwtValidationError>> =
    getJwksCoreWith
        policy.EvictionSignal
        httpClient
        logger
        url
        forceRefresh
        policy.FailClosedOnStale
        policy.JwksTtl
        jwksRefreshCooldown

/// Get JWKS keys for `url`, using the cache when fresh. `forceRefresh`
/// bypasses the TTL check and is used on `kid` miss. A failed refresh
/// falls back to the stale entry if one exists — prefer stale over
/// nothing during brief provider outages — UNLESS `failClosedOnStale`
/// is set, in which case the fetch error is surfaced instead.
///
/// Bounded-staleness security note (Auth-core audit, by-design): the
/// stale-fallback below means a key the issuer *revoked* (e.g. after a
/// signing-key compromise) keeps validating tokens here until a
/// refresh fetch succeeds. The staleness window is bounded only by
/// provider availability, not by the TTL — once a fetch is failing,
/// the cooldown branch also keeps serving the cached keys. This is a
/// deliberate availability-over-strict-revocation default (matching how
/// mainstream OIDC libraries behave). Phase 341 adds `failClosedOnStale`
/// (`OidcHardening.FailClosedOnStaleJwks`) so a deployment that prefers
/// revocation-safety over availability opts into failing closed; the
/// default (`false`) preserves prior behaviour byte-for-byte (GP 11).
/// Phase 463 adds the TTL and cross-instance levers — see
/// `getJwksWithPolicy` and the revocation-window note at the head of
/// this file. This entry point keeps the default TTL and publishes no
/// signal.
let getJwks
    (httpClient: HttpClient)
    (logger: ILogger)
    (url: string)
    (forceRefresh: bool)
    (failClosedOnStale: bool)
    : Async<Result<Map<string, JwkKey>, JwtValidationError>> =
    getJwksCore httpClient logger url forceRefresh failClosedOnStale defaultJwksTtl jwksRefreshCooldown