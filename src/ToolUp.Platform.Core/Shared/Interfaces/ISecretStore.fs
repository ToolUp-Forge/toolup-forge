// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Secrets

/// Abstraction for secret management.
/// Implementations may target Azure Key Vault, AWS Secrets Manager,
/// GCP Secret Manager, environment variables, or a file hierarchy.
///
/// Secret lookup is scoped to prevent cross-tenant leakage (team isolation).
/// Scope prefixes match `StorageScope.Container` conventions:
///   - `_platform` — SDK-level secrets shared across all tenants
///                   (e.g. ANTHROPIC_API_KEY, database connection strings)
///   - `user-{userId}`  — per-user secrets
///   - `team-{teamId}`  — per-team secrets
///
/// Implementations MUST ensure that secrets registered under one scope are
/// never returned for a different scope's lookup. Callers MUST pass a scope
/// derived from a resolved `StorageScope` or the reserved `_platform` value —
/// never an arbitrary string.
type ISecretStore =
    /// Retrieve a secret by scope and key. Returns None if not found.
    /// `scopeId` must be a scope-derived container name or the reserved
    /// `_platform` scope. Implementations are free to reject unrecognised
    /// scope shapes.
    abstract GetSecret: scopeId: string * key: string -> Async<string option>

    /// Store or replace a secret. Used by the BYOK settings API when a
    /// user or team admin enters an API key through the UI.
    /// Returns `Error "read-only"` (or similar) for implementations
    /// that do not support writes (env-var-only stores, for example).
    /// Writes SHOULD persist immediately — the UI round-trips the key
    /// into storage before acknowledging "saved" to the user.
    abstract SetSecret: scopeId: string * key: string * value: string -> Async<Result<unit, string>>

    /// Remove a secret. Idempotent by contract — deleting a non-existent
    /// key succeeds. Returns `Error` only for genuine write failures or
    /// unsupported implementations. Used by the settings API when a
    /// user removes a configured provider instance.
    abstract DeleteSecret: scopeId: string * key: string -> Async<Result<unit, string>>

    /// Enumerate the key names stored under a scope. Used by the
    /// encrypted-store rotation helper to iterate all secrets when
    /// rotating the master key. Implementations that cannot enumerate
    /// (env-var-only stores) return `[]`; callers treat empty as "no
    /// known keys" — the rotation then has nothing to re-encrypt for
    /// that scope. Callers MUST NOT infer "scope is empty"
    /// operationally from this method; it is advisory.
    abstract ListKeys: scopeId: string -> Async<string list>

/// Phase 464 — the optional cache-invalidation seam a **caching**
/// `ISecretStore` implements.
///
/// Deliberately a SEPARATE interface rather than a fifth member on
/// `ISecretStore`. Two reasons, and the second is the load-bearing one:
///
///  * Additive by construction (GP 11). Every shipped store, every
///    companion store, and every consumer's own implementation keeps
///    compiling — adding a member to `ISecretStore` would break all of
///    them at once for a concern most of them do not have.
///  * It is only meaningful for an implementation that memoises. A
///    cloud store that round-trips the vault per call, or an env-var
///    store, has nothing to invalidate; making them all implement a
///    no-op would erase exactly the distinction a caller needs to
///    make. A type test for this interface is therefore a truthful
///    question — "does this store hold a copy that could go stale?" —
///    and `false` is a real answer, not an unimplemented one.
///
/// `ISecretStore.SetSecret` / `DeleteSecret` already invalidate their
/// own cache on the writing instance; this interface exists for the
/// case where the write happened SOMEWHERE ELSE and arrived as a
/// notification (the Phase 464 webhook signing-secret rotation
/// broadcast is the first caller).
type ISecretCacheInvalidation =
    /// Drop any memoised secret material for `scopeId` so the next read
    /// goes to the durable store.
    ///
    /// **Idempotent and order-insensitive** — invalidating a scope that
    /// holds nothing is a no-op, and two invalidations converge on the
    /// same state (portability rule 5: no cross-shard ordering promise
    /// is needed to use this correctly).
    ///
    /// Synchronous by design: it is an in-memory eviction, and callers
    /// are notification handlers that must evict BEFORE returning so no
    /// concurrent read on the same instance can still hit the stale
    /// entry. This is the same documented exemption to portability
    /// rule 2 that `INotificationChannel.Subscribe`'s handler carries.
    abstract InvalidateScope: scopeId: string -> unit