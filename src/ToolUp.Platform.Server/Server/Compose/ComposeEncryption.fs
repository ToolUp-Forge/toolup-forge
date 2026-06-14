module ToolUp.Platform.ComposeEncryption

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.DegradedCapabilities

// ─── compose phase: encryption decorator + DI ────────────────────────
//
// Phase 22 — envelope encryption substrate. Wraps the configured
// `IBlobStorage` with `EncryptedBlobStorage` when a resolver is
// registered; every downstream consumer (DataObjectStore, EventStore,
// JobStore, WebhookRegistry, TeamStore, PermissionStore,
// NotificationAddressBook, etc.) receives the wrapped instance so
// encryption is transparent. `None` (the default) leaves the storage
// layer un-decorated.
//
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up). Takes the exact substrate values the inline definition
// captured and returns the same shape. Zero behaviour change.

/// Apply the `EncryptedBlobStorage` decorator when an encryption-key
/// resolver was supplied. Returns the (possibly wrapped) blob storage
/// that every downstream consumer (DataObjectStore, EventStore, ...)
/// receives via DI.
let applyEncryptionDecorator
    (innerBlobStorage: IBlobStorage)
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    : IBlobStorage =
    match encryptionKeyResolver with
    | Some resolver -> EncryptedBlobStorage.EncryptedBlobStorage(innerBlobStorage, resolver) :> IBlobStorage
    | None -> innerBlobStorage

/// Register the encryption resolver as a DI singleton when the
/// deployment opted into envelope encryption. `EncryptionAdminHandler`
/// resolves it per-request to dispatch the destroy-scope-key endpoint
/// on `PerScopeKeyResolver`. Apps that didn't call
/// `ServerApp.withEncryptedBlobStorage` skip this registration entirely.
let registerEncryptionResolver
    (services: IServiceCollection)
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    : unit =
    match encryptionKeyResolver with
    | Some r -> services.AddSingleton<IBlobEncryptionKeyResolver>(r) |> ignore
    | None -> ()

/// Stable id for the crypto-shred cross-silo cache-eviction capability
/// in the degraded-capability registry (Phase 118). Referenced by both
/// the `Register` (failure) and `Clear` (success) paths below.
[<Literal>]
let CryptoShredCacheEvictionCapability = "crypto-shred-cache-eviction"

/// Gap audit #1 — wire `PerScopeKeyResolver` to the cross-process
/// notification channel for multi-instance cache coherence. After
/// wiring, `DestroyKey` on any silo publishes a
/// `KeyDestroyedNotificationKey` `CustomNotification` on the platform-
/// reserved scope; other silos' subscribed handlers evict their local
/// caches synchronously. Distributed channels (Redis pub/sub) bridge the
/// publish across silos; the in-process default doesn't
/// (`PerScopeKeyResolverDistributedValidator` warns about the
/// combination at preflight). Best-effort: `WireToChannel` runs
/// synchronously here, but a network glitch on subscribe shouldn't
/// crash compose — the resolver still functions single-instance.
///
/// Phase 118 — best-effort is no longer SILENT. A subscribe failure
/// logs `Error` and registers a `crypto-shred-cache-eviction` degraded
/// entry naming the single-silo-only consequence (a destroyed key keeps
/// decrypting on every OTHER silo until restart — a GDPR-erasure hole).
/// A successful wire clears any stale entry (idempotent no-op on a clean
/// boot). Operators alert on a non-empty `/health` degraded set.
let wirePerScopeResolverToNotificationChannel
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    (resolvedNotificationChannel: INotificationChannel)
    (degradedCapabilities: DegradedCapabilityRegistry)
    (logger: ILogger)
    : unit =
    match encryptionKeyResolver with
    | Some(:? PerScopeKeyResolver.PerScopeKeyResolver as perScope) ->
        try
            perScope.WireToChannel resolvedNotificationChannel |> Async.RunSynchronously
            // Cross-silo eviction is live — clear any degraded entry a
            // prior failed attempt left behind. No-op on a clean boot.
            degradedCapabilities.Clear CryptoShredCacheEvictionCapability
        with ex ->
            // Best-effort by design (compose must not crash on a subscribe
            // blip), but the failure is recorded loudly now: Error log +
            // a degraded-capability entry that survives until a successful
            // re-subscribe or restart (GP 9).
            logger.Error(
                "[ComposeEncryption] event=crypto_shred_subscribe_failed "
                + "impact=destroyed-key-still-decrypts-on-other-silos-until-restart",
                Some ex
            )

            degradedCapabilities.Register {
                Capability = CryptoShredCacheEvictionCapability
                DegradedSince = DateTimeOffset.UtcNow
                Reason =
                    sprintf
                        "PerScopeKeyResolver failed to subscribe to the notification channel at compose: %s"
                        ex.Message
                Impact =
                    "A crypto-shredded (destroyed) encryption key keeps decrypting on every OTHER silo "
                    + "until that silo's process restarts — cross-silo cache eviction is not happening, "
                    + "defeating the GDPR-erasure guarantee. Single-silo deployments are unaffected."
                Remediation =
                    "Check the distributed notification channel (e.g. Redis) connectivity/auth, then "
                    + "restart this instance to re-subscribe. Verify with a key-destroy on one silo + a "
                    + "read on another. Single-silo deployments can ignore this entry."
            }
    | _ -> ()