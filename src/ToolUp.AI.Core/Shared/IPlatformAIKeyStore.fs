// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open ToolUp.Platform

// ─── Phase 70 — Platform-Admin-managed AI key store ──────────────
//
// The substrate behind the Platform Admin keys module. Two scopes of
// keys live here:
//
//   * Platform-scope keys — one per provider id (e.g. "anthropic",
//     "openai", "google-gemini"). Default key for every request that
//     does not match a team-scope override.
//   * Team-scope keys — one per (teamId, providerId) pair. Used by
//     the factory in preference to platform-scope when the request's
//     `AccessContext.Subject` carries a team id.
//
// The interface is deliberately small and value-typed. Per GP 12 (six
// portability rules):
//   1. Identity by value — string ids only, no live handles.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry as data — callers supply RetryPolicy where it matters;
//      this interface emits no callbacks.
//   4. Stateless between invocations — implementations may cache, but
//      a fresh instance per call must give the same answer.
//   5. Per-scope sharding — per-team container is the natural shard;
//      platform-scope is a singleton container.
//   6. No implicit sub-second timing primitive — all timing is on the
//      caller (request lifetime).
//
// **The interface does not expose a "read the current key value"
// path.** Callers of the factory's `Resolve` chain read the value
// internally to build the provider; UI surfaces never receive it.
// `HasPlatformKey` / `HasTeamKey` exist for the admin UI to render
// "key configured" badges without exposing the value.

/// Platform-Admin-managed AI key store. Backs the Platform AI Keys
/// admin module and the factory's request-time key resolution. The
/// default implementation `BlobPlatformAIKeyStore` is `ISecretStore`
/// -backed; alternative implementations (Azure Key Vault, AWS Secrets
/// Manager via the existing secrets companions) plug in via DI.
type IPlatformAIKeyStore =
    /// Read the platform-scope (deployment-default) key for a given
    /// provider id. Returns `None` when no key is recorded. The
    /// factory's resolution chain treats `None` as "fall through to
    /// `BootstrapKeyFromEnv`".
    abstract GetPlatformKey: providerId: string -> Async<string option>

    /// Write (or overwrite) the platform-scope key for a provider.
    /// Idempotent — a `Set` with the same value is byte-identical to
    /// the prior state. Returns `Error` only on storage-layer
    /// failure, never on validation (callers validate the provider id
    /// against the factory's `PlatformDescriptors`).
    abstract SetPlatformKey: providerId: string * apiKey: string -> Async<Result<unit, string>>

    /// Remove the platform-scope key for a provider. Idempotent —
    /// deleting a non-existent key succeeds silently. Subsequent
    /// `Resolve` calls for this provider fall through to
    /// `BootstrapKeyFromEnv`.
    abstract DeletePlatformKey: providerId: string -> Async<Result<unit, string>>

    /// Test whether a platform-scope key is recorded for a provider.
    /// Equivalent to `GetPlatformKey >> Option.isSome` but lets
    /// implementations short-circuit the value read for the
    /// admin-UI's status-badge use case.
    abstract HasPlatformKey: providerId: string -> Async<bool>

    /// Read the team-scope key for a given (teamId, providerId)
    /// pair. Returns `None` when no team-scope key is recorded;
    /// callers fall through to the platform-scope read.
    abstract GetTeamKey: teamId: string * providerId: string -> Async<string option>

    /// Write (or overwrite) a team-scope key. Same idempotency
    /// contract as `SetPlatformKey`.
    abstract SetTeamKey: teamId: string * providerId: string * apiKey: string -> Async<Result<unit, string>>

    /// Remove the team-scope key for a (teamId, providerId) pair.
    /// Idempotent. Subsequent `Resolve` calls for this team fall
    /// through to the platform-scope read.
    abstract DeleteTeamKey: teamId: string * providerId: string -> Async<Result<unit, string>>

    /// Test whether a team-scope key is recorded for a
    /// (teamId, providerId) pair. Lets the admin UI render
    /// "team override active" badges without reading the value.
    abstract HasTeamKey: teamId: string * providerId: string -> Async<bool>