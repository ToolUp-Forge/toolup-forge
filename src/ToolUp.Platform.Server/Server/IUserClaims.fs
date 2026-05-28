// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 62 — IUserClaims substrate ─────────────────────────────
//
// `IUserClaims` is the server-side seam for reading and writing
// per-user metadata claims that live in the active auth provider
// (Clerk's `publicMetadata`, OIDC custom claims, future providers'
// equivalents). Today the SDK reads/writes one claim — `premium` —
// but the interface is general enough to extend to other operator-
// granted user-metadata flags without re-shaping the surface.
//
// Default `NoOpUserClaims` implementation returns `NotPremium` for
// every user; the grant / revoke methods record audit events but
// don't write through to any provider (the audit trail captures the
// operator's intent even when no concrete provider is wired).
// Production deployments swap in a provider-specific implementation
// (`ClerkUserClaims` etc.) via DI.
//
// **Six-rule portability audit** (see
// [`docs/platform/portability-rules.md`](../../../docs/platform/portability-rules.md)):
//
//   1. Identity by value. `userId: string`, `grantor: string`,
//      `reason: string option`. `PremiumStatus` is a DU of plain
//      values — `DateTimeOffset` + strings — with no embedded live
//      handles. `ListPremiumUsers` returns `(string * PremiumStatus)
//      list`, again all values. No `IActorRef` / `IGrainReference`
//      crosses the boundary; a distributed `IUserClaims` impl (Clerk
//      gateway shard, OIDC fan-out) can ship every method's argument
//      and return value over the wire unchanged.
//   2. Async at every boundary. All four methods return `Async<_>`.
//      No fire-and-forget signatures even on `GrantPremium` /
//      `RevokePremium`, where the audit-trail invariant requires
//      the caller to observe success or failure before emitting the
//      `PremiumGranted` / `PremiumRevoked` event.
//   3. Retry / supervision as data. `Grant` / `Revoke` /
//      `ListPremiumUsers` surface failures as
//      `Result<_, string>` so transient provider errors (Clerk admin
//      API throttling, OIDC issuer down) flow back to the caller
//      without throwing. A future retrying impl expresses its retry
//      policy as a record on its `create` function (e.g.
//      `UserClaimsRetryPolicy { MaxAttempts; Backoff }`), not as an
//      `OnFailure` callback on this interface. `GetPremiumStatus`
//      currently returns `Async<PremiumStatus>` without `Result`
//      because reads are designed to fail safely — a transient
//      read error degrades to `NotPremium` (the safe default for a
//      gating check) rather than surfacing an exception that would
//      tear down the request pipeline.
//   4. Stateless handlers between invocations. Implementations
//      cache nothing between calls — `GetPremiumStatus` re-reads
//      the provider's metadata every time. The default no-op holds
//      no state. A distributed provider-backed impl can hash-
//      partition by `userId` and route any call to any node without
//      coordinating local state. Consumers wanting a request-scoped
//      cache wrap an adapter (typical: cache the result for the
//      lifetime of one `AccessContext` resolution).
//   5. No cross-user / cross-shard ordering promises. Claim writes
//      apply per user; two `GrantPremium` calls against different
//      users have no implied ordering even when issued from the
//      same operator session. `ListPremiumUsers` returns a snapshot
//      with no defined order. Consumers requiring an audit ordering
//      reconstruct it from `Premium.grantedAt` timestamps.
//   6. Precision documented. Provider write-through propagates
//      eventually — Clerk's admin-API write returns before the
//      change is visible across every Clerk region, and a
//      `GetPremiumStatus` call moments after `GrantPremium` may
//      still return the prior value. This is the auth-context-
//      refresh ceiling, documented per provider in
//      `docs/platform/auth-providers.md`. Consumers gating on
//      premium must tolerate the staleness window (typically
//      ≤ one auth refresh cycle) or check explicitly via
//      `IUserClaims.GetPremiumStatus` rather than reading from a
//      stale `AccessContext`.
//
// Contract pack:
// [`IUserClaimsContract`](../../ToolUp.Platform.Tests/Contracts/IUserClaimsContract.fs)
// exercises the audit against `NoOpUserClaims` (non-persisting) and
// an `InMemoryUserClaims` test impl (persisting). Any external impl
// (Clerk-backed, OIDC-backed) binds the same pack from its own test
// project.

type IUserClaims =
    /// Read the premium status for a user. Anonymous users always
    /// return `NotPremium`. Logged-in users return whatever the
    /// active auth provider's metadata reports.
    abstract GetPremiumStatus: userId: string -> Async<PremiumStatus>

    /// List every user currently holding `Premium` status. Used by
    /// Phase 61's PremiumUserList admin widget. The Result wraps
    /// transient auth-provider failures so the admin UI can render
    /// a clean error rather than hanging on a network failure.
    abstract ListPremiumUsers: unit -> Async<Result<(string * PremiumStatus) list, string>>

    /// Grant premium status to a user. Writes through to the active
    /// auth provider's user-metadata API. The grantor identity is
    /// captured for audit; reason is operator-supplied free text.
    /// Returns `Error` on provider failure (the caller emits the
    /// audit event for both success and failure paths).
    abstract GrantPremium:
        userId: string * grantor: string * reason: string option -> Async<Result<PremiumStatus, string>>

    /// Revoke premium status. Same shape as `GrantPremium` — auth-
    /// provider write + operator-driven reason.
    abstract RevokePremium: userId: string * grantor: string * reason: string option -> Async<Result<unit, string>>

/// Default no-op `IUserClaims` implementation. Returns `NotPremium`
/// for every user; grant / revoke succeed without writing to any
/// provider (the audit trail captures the operator's intent
/// regardless). Wired by `compose` until the deployment registers a
/// provider-specific implementation.
type NoOpUserClaims() =
    interface IUserClaims with
        member _.GetPremiumStatus(_userId: string) = async { return NotPremium }

        member _.ListPremiumUsers() = async { return Ok [] }

        member _.GrantPremium(_userId, grantor, reason) = async {
            return Ok(Premium(System.DateTimeOffset.UtcNow, grantor, reason))
        }

        member _.RevokePremium(_userId, _grantor, _reason) = async { return Ok() }