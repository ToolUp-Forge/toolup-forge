module ToolUp.AuthProviders.OidcJwksCacheTypes

open System
open ToolUp.Platform

// ─── Phase 463 — the JWKS cache's PUBLIC vocabulary ──────────────────
//
// `OidcAuthProviderJwks` is an `internal` module: it is implementation,
// and everything in it — `JwkKey`, `getJwks`, the caches themselves —
// is deliberately unreachable from a consumer. The cache POLICY is the
// opposite: an operator has to be able to name a TTL, construct an
// eviction signal, and recognise the notification key their own channel
// companion is carrying. Those types therefore live here, in a public
// module compiled ahead of the internal one, rather than forcing the
// whole implementation module public to expose four records.
//
// Read the revocation-window note at the head of
// `OidcAuthProvider.Jwks.fs` before changing any default here — these
// values ARE the provider's key-revocation window.

/// Default JWKS cache lifetime, and therefore the default upper bound on
/// the revocation window while the issuer is reachable. Named rather
/// than restated so a doc, a preflight, or an operator tuning
/// `OidcHardening.JwksCacheTtl` reads the number from its definition.
let defaultJwksTtl = TimeSpan.FromMinutes 10.0

/// Default OIDC discovery (`jwks_uri`) cache lifetime. Far longer than
/// the JWKS TTL because providers rarely rotate the endpoint, but still
/// bounded so a no-longer-used issuer is swept and a rotation is
/// eventually observed even absent a fetch failure.
let defaultDiscoveryTtl = TimeSpan.FromHours 24.0

/// Phase 463 — the cross-instance JWKS-eviction envelope. Published on
/// `NotificationKind.PlatformReservedScope` under
/// `JwksFetchFailedNotification.NotificationKey` as a
/// `CustomNotification` whose payload is this record's JSON.
///
/// **Identity-by-value (portability rule 1).** Every field is a string
/// or an instant — no live handles — so a subscriber can be a separate
/// process, container, grain, or actor without a signature change.
type JwksFetchFailedEnvelope = {
    /// The JWKS URL whose fetch failed. Receiving instances evict their
    /// cache entry for exactly this URL; a URL they never cached is a
    /// no-op.
    JwksUrl: string
    /// Classified failure reason (`JwtValidationError.toMessage` of the
    /// fetch error). Diagnostic only — never parsed by the receiver, and
    /// never carrying token or key material.
    Reason: string
    /// When the fetch failed on the originating instance. Subtracting
    /// this from the receipt time is the measured fanout window.
    FailedAt: DateTimeOffset
    /// Instance the failure originated on. Load-bearing rather than
    /// decorative: the in-process channel delivers a publish back to the
    /// publisher, and an instance that evicted on its OWN signal would
    /// discard the entry it is about to re-fetch — churn that would test
    /// as "the fanout works" on a fleet of one.
    OriginReplicaId: string
}

/// Phase 463 — wire constants for the cross-instance JWKS-eviction
/// broadcast. Public so a distributed `INotificationChannel` companion,
/// or a deployment auditing its own fanout, can recognise the topic
/// without re-deriving the string.
module JwksFetchFailedNotification =
    /// `CustomNotification` key the eviction envelope travels under.
    /// Published on the cross-scope reserved bus
    /// (`NotificationKind.PlatformReservedScope`), the same convention
    /// `MembershipChanged` and `_platform.encryption.key-destroyed` use.
    [<Literal>]
    let NotificationKey = "_platform.oidc.jwks-fetch-failed"

/// Phase 463 — opt-in wiring for the cross-instance eviction signal.
/// Supplied through `OidcHardening.JwksEvictionSignal`; absent by
/// default, in which case nothing is published (GP 11).
type JwksEvictionSignal = {
    /// Channel the eviction envelope is published on. In a multi-silo
    /// deployment this must be a distributed `INotificationChannel`
    /// companion — the in-process default reaches only the publishing
    /// process, so it makes the signal a well-formed no-op rather than
    /// a fleet-wide one.
    Channel: INotificationChannel
    /// Stable identity for THIS instance, stamped on every published
    /// envelope so a receiver can discard its own echo. Must be
    /// non-blank; `OidcAuthProvider` refuses a blank one at construction
    /// rather than letting every instance share the empty identity and
    /// silently discard each other's signals.
    OriginReplicaId: string
}

/// Phase 463 — the levers that together define this provider's
/// revocation window, in one record so they are set as a unit.
/// `JwksCachePolicy.defaults` reproduces the pre-Phase-463 behaviour
/// exactly (GP 11); `OidcHardening` is the public way to supply a
/// different one.
type JwksCachePolicy = {
    /// JWKS cache lifetime. `defaultJwksTtl` (10 minutes) by default.
    /// `TimeSpan.Zero` disables the JWKS cache outright — no read, no
    /// write, and no stale fallback on a failed fetch.
    JwksTtl: TimeSpan
    /// Discovery (`jwks_uri`) cache lifetime. `defaultDiscoveryTtl`
    /// (24 hours) by default; `TimeSpan.Zero` re-runs OIDC metadata
    /// discovery on every validation.
    DiscoveryTtl: TimeSpan
    /// Phase 341 — fail validation closed rather than serve a cached
    /// key set once a refresh has failed or is inside the cooldown.
    FailClosedOnStale: bool
    /// Phase 463 — publish a fetch failure so sibling instances evict.
    EvictionSignal: JwksEvictionSignal option
}

module JwksCachePolicy =
    /// Behaviour-preserving defaults: the shipped 10-minute JWKS TTL,
    /// 24-hour discovery TTL, availability-first stale fallback, no
    /// cross-instance signal. Byte-for-byte the pre-Phase-463 provider.
    let defaults = {
        JwksTtl = defaultJwksTtl
        DiscoveryTtl = defaultDiscoveryTtl
        FailClosedOnStale = false
        EvictionSignal = None
    }