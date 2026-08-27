module ToolUp.Platform.PerScopeKeyResolver

open System
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Microsoft.Extensions.Caching.Memory
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

// ─── Phase 22 — PerScopeKeyResolver ─────────────────────────────────
//
// `IBlobEncryptionKeyResolver` implementation: one AES-256 key per
// `StorageScope.ScopeId`. Use case: multi-tenant deployments hosting
// independent practices, agencies, or businesses on a single instance,
// where cryptographic separation between tenants is a meaningful
// security and privacy boundary.
//
// What this enables:
//
// - **Defence in depth.** A misrouted blob read from team A under team
//   B's resolver fails to decrypt — application-layer scope isolation
//   bugs no longer leak plaintext.
// - **Crypto-shredding for tenant offboarding.** `DestroyKey scopeId`
//   removes the key permanently; all blobs encrypted under it become
//   undecryptable in one operation. Clean answer to GDPR
//   right-to-be-forgotten and contract-termination workflows that
//   don't trust soft-delete + scheduled scrub jobs.
// - **BYOK groundwork.** Per-scope keys are the substrate every
//   customer-managed-key story builds on; the same resolver shape can
//   resolve from a customer-supplied KMS endpoint instead of
//   `ISecretStore`.
//
// Implementation:
//
// - Persistence at `_platform/encryption/scopes/{scopeId}.key` via
//   `ISecretStore`. Same backing store as `SingleKeyResolver`'s master
//   key; one slot per scope.
// - `IMemoryCache` with 5-minute sliding expiration mirrors the
//   pattern in `TeamScopeResolver` (`StorageScopeResolver.fs`).
//   Cache eviction on `DestroyKey` via `cache.Remove`.
// - Per-`scopeId` `SemaphoreSlim` is overkill — `IMemoryCache` plus
//   "auto-create if absent" is correct under concurrent first-call
//   resolution because two concurrent `SetSecret` calls produce the
//   same final state (last-write-wins on identical bytes is
//   idempotent for our purposes; the blob-encryption decorator
//   tolerates one extra `EncryptionKeyCreated` audit event in the
//   rare race).
// - `ResolveKeyById` parses the scopeId out of the keyId
//   (`_platform/scopes/{scopeId}/v1`) and resolves through the same
//   cache+ISecretStore path.
//
// Audit emission:
//
// - `EncryptionKeyCreated` on first auto-creation per scope. UserId
//   is `"system"` because the SDK does the creation, not a user
//   action.
// - `EncryptionKeyDestroyed` on `DestroyKey`. UserId is the actor who
//   invoked the admin endpoint (passed in by the caller).
// - `EncryptionKeyDestroyAcknowledged` (Phase 22b) on each OTHER
//   replica, when its `KeyDestroyed` subscription handler evicts the
//   cache entry. Forensic completeness: the trail shows the shred
//   reached the whole fleet, not just the replica that served the
//   admin request. The originating replica does not self-acknowledge.
//
// Both audit emissions go to the `_platform.audit` source module via
// `IAuditLog.Record`. If `IAuditLog` is not provided (constructor
// `auditLog: IAuditLog option = None`), audit emission is silently
// skipped — useful for tests that don't wire `IEventStore`.
//
// ─── Channel wiring: optional on one replica, REQUIRED on more ───────
//
// Phase 458. `WireToChannel` is what turns the destruction broadcast on,
// and `channel` starts as `None`, so the rule is worth stating in one
// place rather than inferring from the publish site:
//
//   * **Single replica — optional.** There is no sibling cache to evict,
//     so the broadcast is a no-op and `DestroyKey` really is complete
//     when it returns. A deployment that cannot use the fanout pays
//     nothing for it (GP 13).
//   * **More than one replica — REQUIRED, and enforced at startup.**
//     Without wiring, `DestroyKey` on replica A evicts only A's cache:
//     every sibling keeps decrypting the just-offboarded tenant's blobs
//     from its own warm cache for up to the 5-minute sliding TTL, and
//     the audit trail records no `EncryptionKeyDestroyAcknowledged`
//     rows — which reads identically to "there are no siblings", so the
//     hole does not announce itself.
//     `PerScopeKeyResolverDistributedValidator` refuses startup with a
//     security-class `Error` for that shape (`ServerConfig.ReplicaCount
//     > 1` — or the `TOOLUP_REPLICA_COUNT` env declaration — with this
//     resolver active and either no wiring at all or a channel that
//     cannot leave the process).
//
// Compose wires this for you: `ComposeEncryption`
// .wirePerScopeResolverToNotificationChannel` calls `WireToChannel` for
// every composed `PerScopeKeyResolver`. So an UNWIRED resolver in
// practice means one built and driven outside `compose` — direct admin
// tooling, a bespoke composition root, a test. That path used to publish
// nothing and say nothing; since Phase 458 the first unwired `DestroyKey`
// per process emits a security-class `ILogger.Warn` naming the staleness
// window (pass a logger via the four-argument constructor /
// `createWithLogger`), and every unwired destroy is counted and surfaced
// on the `/dev/inspect` "Crypto-shred fanout" panel. Counting as well as
// logging is deliberate: a resolver built without a logger would
// otherwise still be silent, and the count needs no logger to be true.

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKeyPrefix = "encryption/scopes/"

[<Literal>]
let private KeyByteLength = 32 // AES-256

[<Literal>]
let private CacheKeyPrefix = "blob-encryption-key:"

[<Literal>]
let private ResolverName = "PerScopeKeyResolver"

/// Gap audit #1 / Phase 22b — `CustomNotification` key used to broadcast
/// key destruction across silos. Subscribers in other instances evict
/// their local cache for the named scope so the 5-minute sliding-TTL
/// "decrypt-after-destroy" window collapses to one notification
/// round-trip. Published on `PlatformReservedScope = "_platform"`
/// (cross-scope reserved bus, same convention as `MembershipChanged`).
///
/// The constant itself now lives in `EncryptionTypes` so a distributed
/// channel companion can recognise the topic without re-deriving the
/// string; this alias keeps the call sites below unchanged.
let private KeyDestroyedNotificationKey = KeyDestroyedNotification.NotificationKey

let private cacheSlidingTtl = TimeSpan.FromMinutes 5.0

/// Phase 22b — JSON options for the destruction envelope. The F#
/// converter set is mandatory (records / `DateTimeOffset` / options all
/// break on a bare `JsonSerializerOptions`); constructed once at module
/// level per the SDK's SSE / non-Remoting JSON convention.
let private envelopeJson = FableConverters.create ()

/// Phase 22b — this process's replica identity, stamped onto every
/// published envelope and every acknowledgement so a shared audit trail
/// can tell N replicas apart.
///
/// Deliberately derived, not configured: `MachineName` is the container /
/// pod name under every container orchestrator the SDK targets, so a
/// replica is already uniquely named without introducing an env var the
/// operator must remember to set (and whose absence would silently
/// collapse every replica onto one identity). The process id
/// disambiguates several replicas colocated on one host.
let internal defaultReplicaId () =
    sprintf "%s/%d" Environment.MachineName Environment.ProcessId

/// Phase 22b — decode a destruction-broadcast payload.
///
/// Legacy-tolerant by construction (GP 11): before Phase 22b the payload
/// was the bare `scopeId` string, which is not valid JSON for this record
/// and would throw. A rolling upgrade therefore has replicas of both
/// vintages on the bus, and the SECURITY-CRITICAL half of the handler —
/// evict the cache — must still run when an old-format message arrives.
/// So a payload that does not parse is treated as a bare scopeId: the
/// eviction proceeds, and the acknowledgement is skipped because the
/// fields it needs were never sent. Failing closed on eviction here would
/// mean a destroyed key keeps decrypting, which is the whole defect.
let internal decodeKeyDestroyedPayload (payload: string) : KeyDestroyedEnvelope option * string =
    let legacy = (None, payload)

    if String.IsNullOrWhiteSpace payload then
        (None, "")
    else
        try
            let env = JsonSerializer.Deserialize<KeyDestroyedEnvelope>(payload, envelopeJson)

            if obj.ReferenceEquals(env, null) || String.IsNullOrWhiteSpace env.ScopeId then
                legacy
            else
                (Some env, env.ScopeId)
        with _ ->
            legacy

/// Build the `KeyId` string for a given scope. Format is
/// `_platform/scopes/{scopeId}/v1` so future v2 rotation can simply
/// add a new file at `.../v2.key` and start writing v2 KeyIds for
/// new uploads while v1 keys remain available for historical reads.
let private keyIdFor (scopeId: string) =
    sprintf "_platform/scopes/%s/v1" scopeId

/// Reverse `keyIdFor`. Returns `Some scopeId` when the format
/// matches; `None` otherwise (used by `ResolveKeyById` to filter
/// keyIds the resolver is responsible for).
let private scopeIdFromKeyId (keyId: string) : string option =
    let prefix = "_platform/scopes/"
    let suffix = "/v1"

    if keyId.StartsWith prefix && keyId.EndsWith suffix then
        let inner =
            keyId.Substring(prefix.Length, keyId.Length - prefix.Length - suffix.Length)

        if String.IsNullOrEmpty inner then None else Some inner
    else
        None

let private secretKeyFor (scopeId: string) = SecretKeyPrefix + scopeId + ".key"

let private cacheKeyFor (scopeId: string) = CacheKeyPrefix + scopeId

let private generateKey () : byte[] =
    RandomNumberGenerator.GetBytes KeyByteLength

/// One key per `StorageScope.ScopeId`, persisted via `ISecretStore`,
/// cached in-memory with sliding-TTL eviction. Crypto-shredding via
/// `DestroyKey` is the canonical tenant-offboarding path.
///
/// Multi-instance cache coherence (gap audit #1): the in-memory cache
/// has a 5-minute sliding TTL. Without distributed coordination, a
/// `DestroyKey` call on silo A only evicts A's cache; silos B, C…
/// continue to decrypt the offboarded scope's blobs until their TTL
/// elapses. Compose calls `WireToChannel` after `INotificationChannel`
/// resolves; on `DestroyKey`, the resolver publishes a
/// `CustomNotification(KeyDestroyedNotificationKey, <envelope JSON>)` and
/// other silos' subscribed handlers evict their caches synchronously.
///
/// Phase 22b sharpened that broadcast: the payload is now a typed
/// `KeyDestroyedEnvelope` carrying `(ScopeId, KeyId, RequestedBy,
/// RequestedAt)` plus the originating replica id, and each receiving
/// replica records an `EncryptionKeyDestroyAcknowledged` audit event so
/// the trail proves the shred reached the whole fleet. The propagation
/// window is the active channel companion's fanout latency — minute-grain
/// per the `INotificationChannel` precision contract, not instant.
///
/// Phase 458 — `logger` is optional and additive (GP 11): the
/// three-argument constructor still exists and behaves exactly as before
/// apart from the unwired-destroy counter, which costs one `int`. A logger
/// is only ever used for the unwired-`DestroyKey` security warning; every
/// other diagnostic on this path goes through `IAuditLog`.
type PerScopeKeyResolver
    (secretStore: ISecretStore, cache: IMemoryCache, auditLog: IAuditLog option, logger: ILogger option) =

    let mutable channel: INotificationChannel option = None
    let mutable subscriptionId: NotificationSubscriptionId option = None

    /// Phase 458 — how many `DestroyKey` calls were served with no
    /// channel wired, i.e. how many crypto-shreds published no
    /// destruction broadcast. `Interlocked` because `DestroyKey` is
    /// reachable from any request thread.
    let mutable unwiredDestroyCount = 0

    /// Phase 22b — this replica's identity, stamped on published
    /// envelopes and recorded on acknowledgements. Defaults to the
    /// derived `{machine}/{pid}`; the two-argument `WireToChannel`
    /// overload overrides it, which is what lets a test stand two
    /// "replicas" up in one process (they would otherwise share one
    /// identity and each suppress the other's acknowledgement as a
    /// self-echo).
    let mutable replicaId = defaultReplicaId ()

    /// Auto-create / load / cache. Returns the `EncryptionKey` for the
    /// requested scope. Emits `EncryptionKeyCreated` audit event on
    /// first auto-create.
    let resolveScopeKey (scopeId: string) : Async<EncryptionKey> = async {
        let cacheKey = cacheKeyFor scopeId

        match cache.TryGetValue<EncryptionKey> cacheKey with
        | true, cached -> return cached
        | false, _ ->
            // Try to load from persistence first.
            let! stored = secretStore.GetSecret(SecretScope, secretKeyFor scopeId)

            let! resolvedKey =
                match stored with
                | Some base64 -> async {
                    try
                        let material = Convert.FromBase64String base64

                        if material.Length = KeyByteLength then
                            return {
                                KeyId = keyIdFor scopeId
                                Material = material
                            }
                        else
                            // Stored key wrong length — fall through to
                            // regenerate. Logging the corruption is the
                            // caller's responsibility (typically through
                            // ILogger); we don't accept silent bad keys.
                            let material' = generateKey ()
                            let base64' = Convert.ToBase64String material'
                            let! _ = secretStore.SetSecret(SecretScope, secretKeyFor scopeId, base64')

                            match auditLog with
                            | Some log ->
                                do!
                                    log.Record(
                                        scopeId,
                                        EncryptionKeyCreated {
                                            UserId = "system"
                                            ScopeId = scopeId
                                            KeyId = keyIdFor scopeId
                                            Resolver = ResolverName
                                        }
                                    )
                            | None -> ()

                            return {
                                KeyId = keyIdFor scopeId
                                Material = material'
                            }
                    with _ ->
                        let material' = generateKey ()
                        let base64' = Convert.ToBase64String material'
                        let! _ = secretStore.SetSecret(SecretScope, secretKeyFor scopeId, base64')

                        return {
                            KeyId = keyIdFor scopeId
                            Material = material'
                        }
                  }
                | None -> async {
                    let material = generateKey ()
                    let base64 = Convert.ToBase64String material
                    let! result = secretStore.SetSecret(SecretScope, secretKeyFor scopeId, base64)

                    match result with
                    | Ok() ->
                        match auditLog with
                        | Some log ->
                            do!
                                log.Record(
                                    scopeId,
                                    EncryptionKeyCreated {
                                        UserId = "system"
                                        ScopeId = scopeId
                                        KeyId = keyIdFor scopeId
                                        Resolver = ResolverName
                                    }
                                )
                        | None -> ()

                        return {
                            KeyId = keyIdFor scopeId
                            Material = material
                        }
                    | Error msg ->
                        return failwithf "PerScopeKeyResolver: failed to persist key for scope %s: %s" scopeId msg
                  }

            // Cache with sliding expiration.
            let entry = cache.CreateEntry cacheKey
            entry.SlidingExpiration <- cacheSlidingTtl
            entry.Value <- resolvedKey
            entry.Dispose()
            return resolvedKey
    }

    /// Resolve a key by its stamped `KeyId`. Parses the scopeId out
    /// of the keyId and resolves via `resolveScopeKey`. Returns
    /// `KeyNotFound` when the keyId doesn't match this resolver's
    /// format (different resolver class, future v2, etc.).
    /// Returns `KeyDestroyed` when the keyId matches the format but
    /// the underlying secret has been removed (post-`DestroyKey`).
    let resolveKeyByIdImpl (keyId: string) : Async<Result<EncryptionKey, KeyResolutionError>> = async {
        match scopeIdFromKeyId keyId with
        | None -> return Error(KeyNotFound keyId)
        | Some scopeId ->
            // Cache fast-path.
            let cacheKey = cacheKeyFor scopeId

            match cache.TryGetValue<EncryptionKey> cacheKey with
            | true, cached -> return Ok cached
            | false, _ ->
                let! stored = secretStore.GetSecret(SecretScope, secretKeyFor scopeId)

                match stored with
                | None ->
                    // Not in cache and not persisted — was either never
                    // created (KeyNotFound) or destroyed
                    // (KeyDestroyed). The two are indistinguishable
                    // from this side; we conservatively report
                    // KeyDestroyed because the typical path here is
                    // "blob with stamped keyId was uploaded, key was
                    // later destroyed, blob is now being read." Pure
                    // never-existed cases come through the resolver's
                    // ResolveKey path which auto-creates.
                    return Error(KeyDestroyed keyId)
                | Some base64 ->
                    try
                        let material = Convert.FromBase64String base64

                        if material.Length <> KeyByteLength then
                            return Error(StorageFailure "Stored key has invalid length")
                        else
                            let key = { KeyId = keyId; Material = material }

                            // Cache for subsequent reads.
                            let entry = cache.CreateEntry cacheKey
                            entry.SlidingExpiration <- cacheSlidingTtl
                            entry.Value <- key
                            entry.Dispose()
                            return Ok key
                    with ex ->
                        return Error(StorageFailure(sprintf "Stored key parse failure: %s" ex.Message))
    }

    /// Backwards-compatible three-argument form — the shape every
    /// pre-Phase-458 call site (and `create`) uses, preserved byte-for-byte
    /// so no consumer had to change (GP 11). No logger means the
    /// unwired-`DestroyKey` warning has nowhere to go; the counter behind
    /// `UnwiredDestroyKeyCount` still records it and the `/dev/inspect`
    /// "Crypto-shred fanout" panel still surfaces it.
    new(secretStore: ISecretStore, cache: IMemoryCache, auditLog: IAuditLog option) =
        PerScopeKeyResolver(secretStore, cache, auditLog, None)

    interface IBlobEncryptionKeyResolver with
        member _.ResolveKey(scope: StorageScope) = resolveScopeKey scope.ScopeId

        member _.ResolveKeyById(keyId: string) = resolveKeyByIdImpl keyId

    /// Crypto-shred the key for a given scope. Removes both the
    /// in-memory cache entry and the persisted ISecretStore record.
    /// All blobs encrypted under this scope's key become permanently
    /// undecryptable after this operation completes.
    ///
    /// Multi-instance (Phase 22b): when wired via `WireToChannel`,
    /// publishes a `CustomNotification(KeyDestroyedNotificationKey,
    /// <KeyDestroyedEnvelope JSON>)` on `PlatformReservedScope` after the
    /// local + persistent delete succeeds. Other silos' subscribed
    /// handlers evict their caches and each record an
    /// `EncryptionKeyDestroyAcknowledged` audit event. Without
    /// `WireToChannel` (single-instance dev), no publish; the local cache
    /// + secret-store delete is the entirety of the operation — which is
    /// why the fanout costs a single-replica deployment nothing (GP 13).
    ///
    /// The publish happens AFTER the persistent delete, never before: a
    /// replica that evicted on the broadcast must not be able to
    /// re-populate its cache from a secret that is still present.
    ///
    /// `actorUserId` is the authenticated user invoking the admin
    /// endpoint; used as the `UserId` on the emitted audit event.
    /// Tests that exercise destruction directly may pass `"system"`.
    member _.DestroyKey(scopeId: string, actorUserId: string) : Async<Result<unit, KeyResolutionError>> = async {
        let cacheKey = cacheKeyFor scopeId
        cache.Remove cacheKey |> ignore

        let! result = secretStore.DeleteSecret(SecretScope, secretKeyFor scopeId)

        match result with
        | Ok() ->
            match auditLog with
            | Some log ->
                do!
                    log.Record(
                        scopeId,
                        EncryptionKeyDestroyed {
                            UserId = actorUserId
                            ScopeId = scopeId
                            KeyId = keyIdFor scopeId
                            Resolver = ResolverName
                        }
                    )
            | None -> ()

            // Gap audit #1 / Phase 22b — broadcast destruction to other
            // silos so they evict their caches. Channel-handler
            // exceptions are caught by the channel itself (GP 12 r3);
            // a publish-side failure logs but doesn't fail DestroyKey
            // (the local + persistent delete already succeeded).
            match channel with
            | Some ch ->
                try
                    let envelope: KeyDestroyedEnvelope = {
                        ScopeId = scopeId
                        KeyId = keyIdFor scopeId
                        RequestedBy = actorUserId
                        RequestedAt = DateTimeOffset.UtcNow
                        OriginReplicaId = replicaId
                    }

                    do!
                        ch.Publish(
                            NotificationKind.PlatformReservedScope,
                            CustomNotification(
                                KeyDestroyedNotificationKey,
                                JsonSerializer.Serialize(envelope, envelopeJson)
                            )
                        )
                with _ ->
                    ()
            // Phase 458 — an unwired destroy published nothing. On one
            // replica that is correct; on more than one it is a
            // GDPR-erasure hole that lasts the full sliding TTL and
            // leaves no acknowledgement rows behind to reveal itself. So
            // it is no longer a silent `()`: counted always, and logged
            // once per process at security class.
            | None ->
                let count = Interlocked.Increment &unwiredDestroyCount

                if count = 1 then
                    match logger with
                    | Some log ->
                        log.Warn(
                            sprintf
                                "[PerScopeKeyResolver] event=crypto_shred_unwired_destroy class=security scope=%s — DestroyKey crypto-shredded this scope's key, but the resolver is not wired to an INotificationChannel (WireToChannel was never called), so NO destruction broadcast was published. On a single-replica deployment this is correct and complete. On more than one replica, every SIBLING replica keeps decrypting this tenant's blobs from its own warm cache for up to the resolver's %g-minute sliding TTL, and the audit trail records no EncryptionKeyDestroyAcknowledged rows — indistinguishable from having no siblings, so the gap does not announce itself. Fix: compose the resolver through ServerApp.withEncryptedBlobStorage (compose calls WireToChannel for you) and configure a distributed channel companion (ServerConfig.Notifications = RedisNotifications \"<connection-string>\"). Logged once per process; every unwired destroy is counted and surfaced on the /dev/inspect \"Crypto-shred fanout\" panel."
                                scopeId
                                cacheSlidingTtl.TotalMinutes
                        )
                    | None -> ()

            return Ok()
        | Error msg -> return Error(StorageFailure msg)
    }

    /// Phase 458 — `true` once `WireToChannel` has been called, i.e. once
    /// `DestroyKey` publishes a destruction broadcast. Read by
    /// `PerScopeKeyResolverDistributedValidator` (which refuses startup
    /// when a multi-instance deployment leaves this `false`) and by the
    /// `/dev/inspect` "Crypto-shred fanout" panel, so an operator can
    /// confirm cache-coherence wiring without reading compose code.
    member _.IsWiredToChannel: bool = channel.IsSome

    /// Phase 458 — how many `DestroyKey` calls have published no
    /// destruction broadcast because no channel was wired. `0` on a
    /// correctly-wired deployment and on one that has never shredded a
    /// key; any non-zero value on a multi-replica deployment means that
    /// many tenants stayed decryptable on sibling replicas for up to the
    /// sliding TTL. Surfaced on `/dev/inspect` because the log line fires
    /// once per process and a logger is optional.
    member _.UnwiredDestroyKeyCount: int = Volatile.Read &unwiredDestroyCount

    /// Gap audit #1 — wire this resolver to a cross-process notification
    /// channel for multi-instance cache coherence. After this call, every
    /// `DestroyKey` publishes to the channel and every incoming
    /// destruction notification evicts the local cache for the named
    /// scope. Single-instance deployments don't need to call this — the
    /// 5-minute sliding TTL on the local cache is the only window of
    /// post-destroy decryptability and only matters when other silos
    /// exist. Multi-instance deployments wire this from `compose` once
    /// the `INotificationChannel` is resolved.
    ///
    /// Idempotent: a second call replaces the prior subscription. The
    /// resolver doesn't expose unsubscribe — the resolver's lifetime is
    /// the process lifetime.
    member this.WireToChannel(notificationChannel: INotificationChannel) : Async<unit> =
        this.WireToChannel(notificationChannel, defaultReplicaId ())

    /// Phase 22b — `WireToChannel` with an explicit replica identity.
    ///
    /// Production wiring uses the one-argument overload, which derives the
    /// identity from `{machine-name}/{process-id}` — already unique per
    /// container. This overload exists for the case that derivation cannot
    /// serve: two resolver instances in ONE process standing in for two
    /// replicas (the in-process fanout test, and any deployment that hosts
    /// several logical replicas per process). They would otherwise share
    /// one identity, and each would discard the other's broadcast as its
    /// own echo — the fanout would test as working while doing nothing.
    member _.WireToChannel(notificationChannel: INotificationChannel, replicaIdentity: string) : Async<unit> = async {
        // Re-wiring drops the previous subscription first (tidy-drain
        // 2026-08-26; origin: Phase 22b ship report, which recorded
        // `subscriptionId` as assigned and never read — dead state). It is
        // no longer dead, and the reason it should not be is that the
        // handler closes over `replicaId`: a second `WireToChannel` with a
        // different identity used to leave the FIRST handler subscribed
        // alongside the second, so one broadcast was handled twice and the
        // stale handler's echo suppression compared against the old
        // identity. Both duplicate acknowledgement events name the same
        // scope, which is exactly the forensic record this fanout exists to
        // make trustworthy. Idempotent by construction rather than by
        // callers remembering to wire once.
        match subscriptionId with
        | Some previous ->
            match channel with
            | Some existing ->
                try
                    do! existing.Unsubscribe previous
                with _ ->
                    // A channel that cannot cancel a subscription is not a
                    // reason to refuse the re-wire; the new subscription is
                    // what the caller asked for.
                    ()
            | None -> ()

            subscriptionId <- None
        | None -> ()

        channel <- Some notificationChannel
        replicaId <- replicaIdentity

        // Subscribe to the cross-scope reserved bus for destruction
        // notifications from other silos. Handler is synchronous (GP 12
        // rule 2 documented exemption); cache eviction is in-memory,
        // microsecond-scale.
        let handler (env: NotificationEnvelope) =
            match env.Notification with
            | CustomNotification(key, payloadJson) when key = KeyDestroyedNotificationKey ->
                let decoded, scopeId = decodeKeyDestroyedPayload payloadJson

                // Self-echo suppression. The in-process channel
                // delivers a publish back to the publisher, so without
                // this a single-replica deployment would record a
                // spurious "another replica acknowledged" event about
                // itself — and the forensic question this event exists
                // to answer ("did the shred reach the fleet?") would
                // read as answered on a fleet of one. The eviction is
                // skipped too: `DestroyKey` already did it locally
                // before publishing.
                let isSelfEcho =
                    match decoded with
                    | Some e -> e.OriginReplicaId = replicaId
                    | None -> false

                if not isSelfEcho && not (String.IsNullOrWhiteSpace scopeId) then
                    // Eviction first, and synchronously — it is the
                    // security-critical half, and it must have happened
                    // before any later read on this replica can hit the
                    // cache.
                    cache.Remove(cacheKeyFor scopeId) |> ignore

                    // Then the forensic record, off the channel's
                    // delivery thread. `INotificationChannel` documents
                    // handlers as synchronous with long-running work
                    // dispatched by the handler itself, and an audit
                    // write goes to blob storage — blocking the
                    // publisher on it would make one slow replica's
                    // storage the publisher's latency. `IAuditLog.Record`
                    // swallows its own failures; the try/with guards the
                    // dispatch itself so a throw can never escape into
                    // the channel.
                    match decoded, auditLog with
                    | Some e, Some log ->
                        try
                            Async.Start(
                                log.Record(
                                    scopeId,
                                    EncryptionKeyDestroyAcknowledged {
                                        UserId = e.RequestedBy
                                        ScopeId = e.ScopeId
                                        KeyId = e.KeyId
                                        Resolver = ResolverName
                                        AcknowledgedBy = replicaId
                                        OriginReplicaId = e.OriginReplicaId
                                        RequestedAt = e.RequestedAt
                                        AcknowledgedAt = DateTimeOffset.UtcNow
                                    }
                                )
                            )
                        with _ ->
                            ()
                    // A pre-Phase-22b payload (bare scopeId) carries
                    // none of the acknowledgement's fields. Evict —
                    // done above — and skip the record rather than
                    // fabricate an actor and an instant.
                    | _ -> ()
            | _ -> ()

        let! subId = notificationChannel.Subscribe(NotificationKind.PlatformReservedScope, handler)
        subscriptionId <- Some subId
        return ()
    }

/// Build a `PerScopeKeyResolver` from an `ISecretStore` + `IMemoryCache`
/// + optional `IAuditLog`. Audit emission is opt-in — pass `None` to
/// disable (useful in tests that don't wire `IEventStore`).
///
/// The resolver auto-creates a fresh AES-256 key per scope on first
/// `ResolveKey` call when none is stored.
let create (secretStore: ISecretStore) (cache: IMemoryCache) (auditLog: IAuditLog option) : PerScopeKeyResolver =
    PerScopeKeyResolver(secretStore, cache, auditLog)

/// Phase 458 — `create` plus a logger for the unwired-`DestroyKey`
/// security warning. Worth reaching for when the resolver is driven
/// OUTSIDE `compose` (bespoke admin tooling, a custom composition root),
/// because that is the only way it can still be unwired: compose calls
/// `WireToChannel` for every resolver it composes. A resolver built with
/// `create` still counts unwired destroys — it just cannot narrate them.
let createWithLogger
    (secretStore: ISecretStore)
    (cache: IMemoryCache)
    (auditLog: IAuditLog option)
    (logger: ILogger)
    : PerScopeKeyResolver =
    PerScopeKeyResolver(secretStore, cache, auditLog, Some logger)

/// Phase 458 — the wired/unwired state of the crypto-shred fanout, as one
/// `/dev/inspect` panel row. Answers "is cross-replica cache eviction
/// actually on in this deployment?" without reading compose code, which
/// was previously only inferable from a preflight validator's silence.
type CryptoShredFanoutStatus = {
    /// The resolver class whose fanout this describes. Constant today —
    /// `PerScopeKeyResolver` is the only resolver with a `DestroyKey` — but
    /// named so the panel stays readable if a KMS-backed resolver grows one.
    Resolver: string
    /// `true` once `WireToChannel` has been called. Compose calls it for
    /// every composed `PerScopeKeyResolver`, so `false` here means the
    /// resolver is being driven outside compose.
    WiredToChannel: bool
    /// The window a sibling replica keeps decrypting a destroyed scope for
    /// when no broadcast reaches it. The resolver's cache TTL, stated in
    /// the panel so the operator does not have to know it.
    UnwiredStalenessWindowMinutes: float
    /// Crypto-shreds that published no broadcast. Non-zero on a
    /// multi-replica deployment is a GDPR-erasure gap that already happened.
    UnwiredDestroyKeyCalls: int
}

/// `IDevDiagnosticsContributor` surfacing the crypto-shred fanout wiring
/// as a `"Crypto-shred fanout"` panel. Registered by `ComposeEncryption`
/// only when the composed resolver is a `PerScopeKeyResolver`, so a
/// deployment with no crypto-shred surface gains no panel and no cost
/// (GP 13). Cheap: two field reads, well inside the contributor budget.
type CryptoShredFanoutContributor(resolver: PerScopeKeyResolver) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            return
                "Crypto-shred fanout",
                box {
                    Resolver = ResolverName
                    WiredToChannel = resolver.IsWiredToChannel
                    UnwiredStalenessWindowMinutes = cacheSlidingTtl.TotalMinutes
                    UnwiredDestroyKeyCalls = resolver.UnwiredDestroyKeyCount
                }
        }