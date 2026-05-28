// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Providers

// ─── IProviderProfile ────────────────────────────────────────────
//
// Canonical platform-wide store for per-scope multi-provider BYOK
// configuration. Keyed by StorageScope (user scope in Individual /
// AuthenticatedEphemeral, team scope in Team / MultiTeam — the caller
// resolves the scope from the request's AccessContext). Phase 42.B
// introduced this behind a behaviour-preserving IUserAIConfigStore
// shim; Phase 43.A removed the shim and cut the AI assistant
// (factory / settings handler / usage-metering wrapper) over to read
// this store directly. It is now the sole canonical BYOK store.
//
// The store persists and retrieves the ProviderProfile blob and
// resolves routing; it does NOT hold secret material (only
// SecretKeyName references) and does NOT validate ProviderId against
// any provider catalogue — that is the consuming factory's job at
// resolve time and the settings UI's at write time.
//
// Six-rule portability audit (the portability contract):
//   1. Identity by value      — every method keys by StorageScope
//                               (value record) + string label; no live
//                               handle on the surface.
//   2. Async at every boundary — every method returns Async<_>.
//   3. Retry / supervision as data — none on this surface; it is a
//                               persistence store, not a dispatcher.
//                               Callers retry by re-issuing the Async.
//   4. Stateless between calls — no in-memory continuity contract; a
//                               distributed implementation may serve
//                               successive calls from different nodes
//                               (each call reads/writes fresh state).
//   5. No cross-shard ordering — each StorageScope is an independent
//                               shard; no ordering claim across scopes.
//   6. Precision at the lower bound — no timing primitive on this
//                               surface; N/A by construction.

/// Persistent store for per-scope ProviderProfile configuration.
/// Get / Set / Clear are the persistence surface (Phase 42.B kept
/// them shaped like the now-removed IUserAIConfigStore so the
/// pre-cutover blob round-trips byte-identically). ResolveEntry and
/// SetEntryHealth add routing resolution + advisory-health writes
/// from the verification call and the background probe.
type IProviderProfile =
    /// Read the profile for a scope. Returns None when no profile has
    /// ever been saved or the blob was deleted. A malformed blob is
    /// treated as None (the consumer then falls back per its own
    /// policy) — the default BlobProviderProfile's tolerant read.
    abstract Get: scope: StorageScope -> Async<ProviderProfile option>

    /// Save or replace the profile for a scope. Overwrites any
    /// existing entry. Callers set profile.UpdatedAt to DateTime.UtcNow
    /// immediately before calling.
    abstract Set: scope: StorageScope * profile: ProviderProfile -> Async<Result<unit, string>>

    /// Remove the profile for a scope. Idempotent — succeeds even when
    /// no profile exists.
    abstract Clear: scope: StorageScope -> Async<unit>

    /// Resolve the entry a (surface, context) pair routes to for a
    /// scope. Convenience over Get + ProviderProfile.resolveEntry: a
    /// context-specific rule wins over the surface default; a stale
    /// EntryLabel or no matching rule yields None (identical
    /// None-on-stale semantics to AIUserConfig.activeInstance). Returns
    /// None when the scope has no saved profile.
    abstract ResolveEntry: scope: StorageScope * surface: string * context: string option -> Async<ProviderEntry option>

    /// Replace only the advisory ProviderHealth of the entry with the
    /// given label. No-op (Ok) when the scope has no profile or no
    /// entry carries that label. Kept separate from Set so the
    /// background probe / verification call can write health without
    /// racing a user editing routing or entries through Set.
    abstract SetEntryHealth: scope: StorageScope * label: string * health: ProviderHealth -> Async<Result<unit, string>>