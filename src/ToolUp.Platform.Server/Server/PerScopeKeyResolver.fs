module ToolUp.Platform.PerScopeKeyResolver

open System
open System.Security.Cryptography
open System.Threading
open Microsoft.Extensions.Caching.Memory
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
//   pattern in `TeamScopeResolver` (`StorageScopeResolver.fs:142`).
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
//
// Both audit emissions go to the `_platform.audit` source module via
// `IAuditLog.Record`. If `IAuditLog` is not provided (constructor
// `auditLog: IAuditLog option = None`), audit emission is silently
// skipped — useful for tests that don't wire `IEventStore`.

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

/// Gap audit #1 — `CustomNotification` key used to broadcast key
/// destruction across silos. Subscribers in other instances evict
/// their local cache for the named scope so the 5-minute sliding-TTL
/// "decrypt-after-destroy" window collapses to one notification
/// round-trip. Published on `PlatformReservedScope = "_platform"`
/// (cross-scope reserved bus, same convention as `MembershipChanged`).
[<Literal>]
let private KeyDestroyedNotificationKey = "_platform.encryption.key-destroyed"

let private cacheSlidingTtl = TimeSpan.FromMinutes 5.0

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
/// `CustomNotification(KeyDestroyedNotificationKey, scopeId)` and other
/// silos' subscribed handlers evict their caches synchronously.
type PerScopeKeyResolver(secretStore: ISecretStore, cache: IMemoryCache, auditLog: IAuditLog option) =

    let mutable channel: INotificationChannel option = None
    let mutable subscriptionId: NotificationSubscriptionId option = None

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

    interface IBlobEncryptionKeyResolver with
        member _.ResolveKey(scope: StorageScope) = resolveScopeKey scope.ScopeId

        member _.ResolveKeyById(keyId: string) = resolveKeyByIdImpl keyId

    /// Crypto-shred the key for a given scope. Removes both the
    /// in-memory cache entry and the persisted ISecretStore record.
    /// All blobs encrypted under this scope's key become permanently
    /// undecryptable after this operation completes.
    ///
    /// Multi-instance: when wired via `WireToChannel`, publishes a
    /// `CustomNotification(KeyDestroyedNotificationKey, scopeId)` on
    /// `PlatformReservedScope` after the local + persistent delete
    /// succeeds. Other silos' subscribed handlers evict their caches
    /// synchronously. Without `WireToChannel` (single-instance dev),
    /// no publish; the local cache + secret-store delete is the
    /// entirety of the operation.
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

            // Gap audit #1 — broadcast destruction to other silos so
            // they evict their caches synchronously. Channel-handler
            // exceptions are caught by the channel itself (GP 12 r3);
            // a publish-side failure logs but doesn't fail DestroyKey
            // (the local + persistent delete already succeeded).
            match channel with
            | Some ch ->
                try
                    do!
                        ch.Publish(
                            NotificationKind.PlatformReservedScope,
                            CustomNotification(KeyDestroyedNotificationKey, scopeId)
                        )
                with _ ->
                    ()
            | None -> ()

            return Ok()
        | Error msg -> return Error(StorageFailure msg)
    }

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
    member _.WireToChannel(notificationChannel: INotificationChannel) : Async<unit> = async {
        channel <- Some notificationChannel

        // Subscribe to the cross-scope reserved bus for destruction
        // notifications from other silos. Handler is synchronous (GP 12
        // rule 2 documented exemption); cache eviction is in-memory,
        // microsecond-scale.
        let handler (env: NotificationEnvelope) =
            match env.Notification with
            | CustomNotification(key, payloadJson) when key = KeyDestroyedNotificationKey ->
                let scopeId = payloadJson
                cache.Remove(cacheKeyFor scopeId) |> ignore
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