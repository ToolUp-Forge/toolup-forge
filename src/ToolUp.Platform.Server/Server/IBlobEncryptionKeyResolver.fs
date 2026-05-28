module ToolUp.Platform.BlobEncryption

open System
open ToolUp.Platform
open ToolUp.Platform.EncryptionTypes

// ─── Phase 22 — IBlobEncryptionKeyResolver ──────────────────────────
//
// Server-side abstraction for resolving the encryption key used by
// `EncryptedBlobStorage` against a given storage scope. The decorator
// stays policy-free; the resolver is the single policy point — letting
// deployments choose between platform-wide single-key, per-team keys,
// BYOK, or external KMS without touching the decorator code.
//
// Two methods cover the read and write paths:
//
// - `ResolveKey scope` returns the *current* key for the active scope.
//   Called on every Upload to encrypt the new blob. Implementations
//   cache aggressively (in-memory map keyed by `scope.ScopeId`) — most
//   real deployments resolve the same key thousands of times per
//   second per scope.
//
// - `ResolveKeyById keyId` returns a specific historical key by its
//   stamped identifier. Called on every Download — the decorator reads
//   `KeyId` out of the envelope header and looks it up here so a key
//   rotation doesn't strand previously-uploaded data.
//
// The two-method shape (rather than a single "give me everything")
// supports both forward (rotate-without-re-encrypting-history) and
// reverse (decrypt-old-blob-with-old-key) flows symmetrically.
//
// Six-rule portability audit (Phase 9c, Guiding Principle 12):
//   1. Identity by value      — `KeyId: string`, `StorageScope` is a
//                               value record. No live handles.
//   2. Async at every boundary — both methods return `Async<_>`.
//   3. Retry/supervision as data — no callbacks; errors flow through
//                                  `KeyResolutionError` DU.
//   4. Stateless between calls — implementations may cache, but the
//                                interface contract assumes nothing.
//                                A grain that deactivates between
//                                calls re-resolves correctly.
//   5. No cross-shard ordering — keys are independent per `KeyId`;
//                                there is no ordering relationship
//                                between resolver calls.
//   6. Precision at lower bound — n/a (no time semantics).

/// Resolves AES-256 keys for the `EncryptedBlobStorage` decorator.
///
/// Implementations are policy points — choose one based on deployment
/// shape:
///
/// - `SingleKeyResolver` (default) — one platform-wide key under
///   `_platform/encryption/master.key`. Simplest; one cryptographic
///   boundary across all tenants.
/// - `PerScopeKeyResolver` (opt-in) — one key per `StorageScope.ScopeId`.
///   Multi-tenant deployments hosting independent practices, agencies,
///   or businesses on one instance use this. Crypto-shredding via
///   `PerScopeKeyResolver.DestroyKey` is the canonical tenant-offboarding
///   path.
/// - KMS-backed (Phase 22a sub-companions, deferred) — proxies to
///   AWS KMS / Azure Key Vault / GCP KMS for regulated deployments
///   with formal key-custody requirements.
///
/// Custom implementations (`per-(scopeId, userId)` for stronger
/// user-private guarantees within a team scope, BYOK against a
/// customer-supplied endpoint, etc.) plug in via `ServerApp.withEncryptedBlobStorage`.
type IBlobEncryptionKeyResolver =
    /// Resolve the *current* encryption key for the given scope.
    /// Called on every blob upload. `KeyId` on the returned key is
    /// stamped into the envelope so subsequent reads can recover the
    /// same key. Implementations cache as appropriate; the contract
    /// only requires correctness, not freshness.
    ///
    /// Implementations that auto-create keys on first resolution
    /// (e.g. `PerScopeKeyResolver`) emit `EncryptionKeyCreated` audit
    /// events through `IAuditLog` — keep that side-effect inside the
    /// implementation, not in the contract.
    abstract ResolveKey: scope: StorageScope -> Async<EncryptionKey>

    /// Look up a specific historical key by its stamped identifier.
    /// Called on every blob download — the decorator reads `KeyId` out
    /// of the envelope header and calls this method.
    ///
    /// Returns `Error KeyDestroyed` when the key was crypto-shredded
    /// (intentional tenant offboarding); the decorator surfaces this
    /// to the API boundary as HTTP 410 Gone.
    ///
    /// Returns `Error KeyNotFound` when the key was never registered
    /// or has been removed for non-destruction reasons; usually
    /// indicates a configuration error.
    ///
    /// Returns `Error (StorageFailure msg)` for transient
    /// infrastructure failures.
    abstract ResolveKeyById: keyId: string -> Async<Result<EncryptionKey, KeyResolutionError>>