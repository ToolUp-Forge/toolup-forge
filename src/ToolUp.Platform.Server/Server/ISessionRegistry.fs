// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── ISessionRegistry — Phase 528 ────────────────────────────────────
//
// The seam between "a credential was presented" and "that credential is
// still allowed to act". Six methods, deliberately: recording, touching,
// listing, revoking one, revoking all for a user, and asking whether one
// is revoked. Nothing else — a registry that also decided WHO may revoke
// would put an authorisation policy inside a storage interface, and the
// second implementation would have to reproduce it.
//
// The wire types (`SessionRecord`, `SessionStatus`, `SessionError`) live
// in `Platform.Core` because the client lists them; the interface lives
// here because a registry is server-side substrate and has no Fable
// consumer (the store-substrate authoring convention: types in Core,
// interface + default impl in Server).
//
// ─── Phase 9c portability rules (all six honoured) ───────────────────
//
//   1. **Identity by value.** Every parameter and return is a string, an
//      `int`, a `DateTimeOffset`, or `SessionRecord` / `SessionError` —
//      records over exactly those. No live handle, no store cursor, no
//      `IAsyncEnumerable`. A Redis-backed companion stores the same
//      record shape and returns the same values.
//   2. **Async at every boundary.** All six return `Async<_>`. Notably
//      `IsRevoked` too, even though the shipped middleware wants a
//      synchronous answer on the hot path — the middleware caches the
//      result rather than the interface promising a cheap synchronous
//      read that a network-backed implementation could not honour.
//      Making the interface sync to suit one caller's hot path is
//      precisely the shape rule 2 exists to refuse.
//   3. **Retry / supervision as data.** Failures come back as
//      `Result<_, SessionError>`; nothing throws for an expected path
//      and there is no `OnFailure` callback anywhere in the shape.
//   4. **Stateless between invocations.** No method assumes the
//      implementation carries state across calls. `Touch` re-reads the
//      record it advances rather than mutating a cached one, and
//      `IsRevoked` is answerable from the store alone — the shipped
//      middleware's cache is the CALLER's, not the store's, which is
//      what keeps a multi-instance deployment correct (see the
//      multi-instance caveat below).
//   5. **No cross-shard ordering promises.** Sessions are independent;
//      the only ordering that exists is within one session id, and even
//      there `Touch` is explicitly allowed to coarsen (see its doc).
//      `ListForUser` returns a set the caller sorts, not a stream with
//      an ordering guarantee.
//   6. **Precision at the lower bound.** `LastSeenAt` is advanced at
//      **minute** granularity or coarser — stated on `Touch` rather than
//      implied — so an implementation that batches writes is conformant
//      rather than broken. Revocation itself is not a timing primitive;
//      its bound is the caller-side cache window
//      (`SessionRegistryOptions.RevocationCacheSeconds`), documented on
//      that field.
//
// A Redis-backed companion is implementable without touching this
// interface: `Record` / `Touch` become a hash write, `IsRevoked` a key
// lookup, `ListForUser` a set scan, `RevokeAllForUser` a pipeline. That
// was the design constraint, not a hoped-for property.
//
// ─── Multi-instance caveat (the Phase 9c distributed-companion family)
//
// The blob-backed default is correct across instances — every instance
// reads the same blobs — but `SessionRevocationMiddleware`'s in-process
// negative cache is per-instance, so a revocation propagates to instance
// B within B's own `RevocationCacheSeconds` window rather than instantly.
// This is the same shape `PerScopeKeyResolver`'s cache has, and the same
// remedy applies where the window is too wide: either set
// `RevocationCacheSeconds = 0` (a store read per authenticated request)
// or compose a `CustomSessionRegistry` whose implementation invalidates
// peers over `INotificationChannel`. The window is stated rather than
// hidden precisely so an operator can make that trade knowingly.

open System

/// Server-side session registry. One stateless component per
/// deployment; composed only when `ServerConfig.SessionRegistry` selects
/// a backend, so a deployment that does not opt in never resolves it and
/// pays nothing (GP 13).
type ISessionRegistry =
    /// Record a session, or return the existing record unchanged if one
    /// is already stored for `SessionId`.
    ///
    /// **Idempotent and non-clobbering.** A returning credential must NOT
    /// reset `CreatedAt` (that would erase "since when has this device
    /// been signed in?", the one thing a session list is read for) and
    /// must NOT resurrect a revoked record — a revoked session that keeps
    /// presenting its credential stays revoked, which is the entire point.
    /// The returned record is what is now stored, so a caller can trust
    /// it rather than re-reading.
    abstract Record: record: SessionRecord -> Async<Result<SessionRecord, SessionError>>

    /// Advance `LastSeenAt` on an existing session. A no-op returning
    /// `Ok ()` when the session is unknown or already revoked — a touch
    /// is a liveness signal, not an assertion that the session exists,
    /// and failing the request that carried it would be absurd.
    ///
    /// **Precision: minute-grain or coarser (GP 12 rule 6).** An
    /// implementation may skip a write whose delta is below its own
    /// resolution; callers must not read `LastSeenAt` as a
    /// request-accurate clock.
    abstract Touch: scopeId: string * sessionId: string * seenAt: DateTimeOffset -> Async<Result<unit, SessionError>>

    /// Every session recorded for `userId` within `scopeId`, active and
    /// revoked alike, in unspecified order. Scoped rather than global:
    /// the same user id in two tenants has two independent session sets,
    /// and no caller can enumerate across the boundary (GP 4).
    abstract ListForUser: scopeId: string * userId: string -> Async<Result<SessionRecord list, SessionError>>

    /// Revoke one session. Idempotent — revoking an already-revoked
    /// session returns `Ok ()` and leaves the original revocation's
    /// timestamp and actor intact. `Error (NotFound _)` when no such
    /// session exists in `scopeId`.
    ///
    /// The registry does NOT decide whether `actorUserId` is entitled to
    /// revoke this session; that is the caller's authorisation decision
    /// (`SessionApiHandler` makes it). The parameter exists so the
    /// decision is *attributable* in the stored record and the audit row.
    abstract Revoke: scopeId: string * sessionId: string * actorUserId: string -> Async<Result<unit, SessionError>>

    /// Revoke every active session belonging to `userId` within
    /// `scopeId`. Returns how many records moved from active to revoked
    /// — a repeat call returns `0`, which is what lets a caller report
    /// something truthful rather than "done" twice.
    abstract RevokeAllForUser:
        scopeId: string * userId: string * actorUserId: string -> Async<Result<int, SessionError>>

    /// Is this session revoked? The hot-path question, asked once per
    /// authenticated request by `SessionRevocationMiddleware` (through
    /// its cache).
    ///
    /// **Fails OPEN, and that is deliberate.** An unknown session id
    /// answers `false`, and so does a store the implementation cannot
    /// reach — a session is refused only when the store affirmatively
    /// says it was revoked. The alternative fails closed and turns a blob
    /// -store outage into a total sign-out of every user, which is a
    /// larger incident than the one this substrate exists to contain.
    /// The registry is a revocation list, not an authentication gate:
    /// the credential has already been validated by `IAuthProvider`
    /// before this is consulted, so failing open returns to the
    /// pre-Phase-528 posture rather than admitting anyone new.
    /// An implementation that cannot reach its store SHOULD surface that
    /// through its logger; it must not answer `true`.
    abstract IsRevoked: scopeId: string * sessionId: string -> Async<bool>