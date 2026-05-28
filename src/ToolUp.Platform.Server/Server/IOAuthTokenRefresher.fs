// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 10h — IOAuthTokenRefresher ───────────────────────────────
//
// Server-side surface for the generic OAuth 2.0 refresh-token
// lifecycle substrate. Connectors register an `OAuthRefreshDescriptor`
// at `Connect` time and unregister on `Disconnect`; the substrate
// schedules background refreshes ahead of expiry, persists rotated
// tokens through `ISecretStore`, and emits the
// `_platform.oauth.refresh` audit family + `toolup.oauth.refresh.*`
// metrics.
//
// **Six-rule portability audit (GP 12).**
//   1. Identity by value      — every method takes
//                                `(provider, configId)` strings.
//                                No live handles.
//   2. Async at boundary      — every method returns `Async<_>`.
//                                `RefreshNow` returns
//                                `Async<OAuthRefreshResult>`;
//                                lifecycle methods return `Async<unit>`.
//   3. Retry as data          — `OAuthRefreshResult` discriminates
//                                Transient vs Permanent vs Invalidated
//                                vs Refreshed. No callbacks.
//   4. Stateless handler      — `OAuthRefreshJobHandler` resolves
//                                the descriptor + secrets on every
//                                dispatch from DI / `ISecretStore`.
//                                Implementations may cache descriptors
//                                in-memory for read efficiency, but
//                                the persisted source of truth is the
//                                `JobDefinition.Payload` keyed by
//                                `(provider, configId)`.
//   5. No cross-shard ordering — refresh ordering is per-descriptor.
//                                Two `RefreshNow` calls on different
//                                `(provider, configId)` pairs may
//                                resume in any order.
//   6. Precision at lower bound — refresh dispatches schedule under
//                                `JobPrecision.Minute` (the
//                                in-process scheduler's floor).

/// Substrate for OAuth 2.0 refresh-token lifecycle management.
/// Implementations:
///
/// - Hold a per-`(Provider, ConfigId)` set of registered descriptors,
///   each pinned to its `ScopeId`. The set survives process restart by
///   persisting through the underlying `IJobScheduler` (every active
///   descriptor has at least one scheduled `JobDefinition` under
///   `_platform.oauth.refresh`).
/// - Read the current refresh token from
///   `ISecretStore.GetSecret(descriptor.ScopeId, descriptor.RefreshTokenKey)`
///   on every dispatch attempt.
/// - On successful refresh, write the new access token + expiry under
///   the derived keys (`OAuthRefreshDescriptor.accessTokenKey` /
///   `accessExpiryKey`) and, if the upstream rotated the refresh
///   token, write the new refresh token back under
///   `descriptor.RefreshTokenKey`.
/// - Emit the corresponding `_platform.oauth.refresh` audit row on
///   every terminal outcome (success, failure, invalidation,
///   dead-letter).
///
/// Implementations must be thread-safe — callers may invoke
/// `RefreshNow` interactively (from a connector's data-fetch path
/// retrying after a 401) at the same time the scheduler dispatches
/// the background refresh job.
type IOAuthTokenRefresher =
    /// Run a refresh attempt for the descriptor identified by
    /// `(provider, configId)` synchronously and return the outcome.
    /// Used by the `OAuthRefreshJobHandler` (which invokes this from
    /// `IJobHandler.Execute`) and by connectors that observe a 401
    /// from a cached access token and want to force-refresh before
    /// the next scheduled dispatch.
    ///
    /// **Side effects on success.** The substrate persists the new
    /// access token + expiry (and rotated refresh token, when the
    /// upstream rotated it) before returning `Refreshed`. The caller
    /// can therefore read the fresh access token from `ISecretStore`
    /// immediately after this method returns.
    ///
    /// Returns `OAuthRefreshResult.PermanentError "unknown descriptor"`
    /// when no descriptor is registered for `(provider, configId)` —
    /// the caller likely missed a `RegisterDescriptor` call.
    abstract RefreshNow: provider: string * configId: string -> Async<OAuthRefreshResult>

    /// Register or replace a refresh descriptor. Idempotent —
    /// re-registering the same `(Provider, ConfigId)` with new fields
    /// (e.g. updated `TokenEndpoint` after the connector's config
    /// changed) overwrites the previous binding atomically; the next
    /// scheduled dispatch picks up the new descriptor without losing
    /// the in-flight retry counter.
    ///
    /// On first registration the implementation MAY schedule the
    /// initial background refresh job — typically at
    /// `now + ScheduleAheadOfExpiry` if no expiry is known yet, or
    /// at `cachedExpiry - ScheduleAheadOfExpiry` if a cached access
    /// token already exists under the derived expiry key.
    abstract RegisterDescriptor: descriptor: OAuthRefreshDescriptor -> Async<unit>

    /// Remove a refresh descriptor. Idempotent — unregistering an
    /// unknown `(provider, configId)` succeeds silently. On removal
    /// the implementation:
    ///   1. Cancels the pending refresh job (`IJobScheduler.Cancel`).
    ///   2. Deletes the cached access-token + expiry secrets (the
    ///      derived keys `OAuthRefreshDescriptor.accessTokenKey` /
    ///      `accessExpiryKey`).
    ///   3. Leaves the refresh-token secret alone — that is the
    ///      connector's data; the connector's own `Disconnect` path
    ///      removes it via the existing Phase 10e revoke flow.
    abstract UnregisterDescriptor: provider: string * configId: string -> Async<unit>

    /// Read the descriptor registered for `(provider, configId)`.
    /// Returns `None` when no descriptor is registered. Used by the
    /// admin-UI token-status column to render `Provider` +
    /// `TokenEndpoint` alongside the next scheduled refresh time.
    abstract GetDescriptor: provider: string * configId: string -> Async<OAuthRefreshDescriptor option>

    /// Enumerate every registered descriptor across every scope.
    /// Used by the admin-UI token-status column to populate the row
    /// for each data source without one call per descriptor. Ordering
    /// is implementation-defined; callers sort client-side if a
    /// stable presentation is required.
    abstract ListDescriptors: unit -> Async<OAuthRefreshDescriptor list>