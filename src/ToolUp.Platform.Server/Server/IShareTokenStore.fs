namespace ToolUp.Platform.Scope

open ToolUp.Platform

// ─── IShareTokenStore ────────────────────────────────────────────────
//
// SDK-level interface for the share-token substrate (Phase 21b). The
// default impl is `BlobShareTokenStore`; distributed companions
// (Redis-backed cache, signed-only-no-store edge variants) bind to
// this interface.
//
// **Phase 9c portability rules** (all six honoured):
//
//   1. Identity by value. Every method takes / returns string ids —
//      no `IActorRef`, `IGrainReference`, or other live handles.
//   2. Async at every boundary. Every method returns `Async<_>`.
//   3. Retry / supervision as data. Failures returned as
//      `ShareTokenError`; no `OnFailure` callbacks.
//   4. Stateless handlers between invocations. `Validate` re-reads
//      the persisted claim every time so a distributed implementation
//      can run on any node.
//   5. No cross-shard ordering promises. Each token is independent.
//   6. Precision N/A — token validity is timestamp-bounded, not
//      tick-driven.

type IShareTokenStore =
    /// Issue a new token. Server fills in `TokenId`, `IssuedAt`,
    /// `UsedCount = 0`, and `Revoked = false`. Defaults applied when
    /// `request.ExpiresAt` / `request.UseLimit` are `None` — see
    /// `ShareTokenTypes.DefaultLifetime` / `DefaultUseLimit`.
    abstract Issue: request: ShareTokenIssueRequest -> Async<Result<ShareToken, ShareTokenError>>

    /// Validate a token string. Re-reads the persisted claim and
    /// applies every gate: signature, existence, expiry, revocation.
    /// Does NOT increment `UsedCount` — call `MarkUsed` separately. A
    /// passed `Validate` is no bound on how many concurrent callers pass
    /// it: a caller for whom the use limit is a bound marks FIRST and acts
    /// only on `Ok` (a team invite link admits that way). Acting before
    /// marking spends no use on a request that fails downstream, at the
    /// cost of letting concurrent callers exceed the limit.
    abstract Validate: token: string -> Async<Result<ShareTokenClaim, ShareTokenError>>

    /// Atomically increment `UsedCount`. Returns `Error UseLimitExceeded`
    /// when the increment would push `UsedCount` past `UseLimit`.
    /// Callers must call exactly once per consumed use — there is no
    /// rollback.
    ///
    /// Atomic is the contract, not a best effort: of N concurrent calls on
    /// a token with `UseLimit = K`, exactly K return `Ok ()`. An
    /// implementation that cannot make the increment atomic returns an
    /// `Error` rather than a check-then-write, and a caller that admits
    /// something on a use (a team invite link) consumes BEFORE it admits
    /// and admits nothing on an `Error`. `BlobShareTokenStore` serialises
    /// the increment in-process and makes it conditional on the claim's
    /// ETag across replicas on a blob backend implementing
    /// `IConditionalBlobStorage` (every shipped backend does). Over a
    /// third-party backend without conditional writes it is atomic within
    /// one process only and logs that it fell back, so such a backend is
    /// single-replica. The shipped decorators forward `MarkUsed` unchanged.
    abstract MarkUsed: scopeId: string * tokenId: string -> Async<Result<unit, ShareTokenError>>

    /// Permanently revoke a token. Subsequent `Validate` returns
    /// `Error RevokedToken`. Idempotent — revoking an already-revoked
    /// token returns `Ok ()`. `actorUserId` identifies who revoked
    /// for audit purposes.
    abstract Revoke: scopeId: string * tokenId: string * actorUserId: UserId -> Async<Result<unit, ShareTokenError>>

    /// Enumerate every claim for a `(resourceKind, resourceId)` pair
    /// within `scopeId`. Returns both active and revoked tokens —
    /// callers filter as needed. Used by admin UIs ("which tokens
    /// have I issued for this form?") and aggregation views.
    abstract ListByResource: scopeId: string * resourceKind: string * resourceId: string -> Async<ShareTokenClaim list>

    /// Enumerate every claim within `scopeId` that was issued by
    /// `issuerUserId` (matched against `ShareTokenClaim.IssuedBy`).
    /// Returns both active and revoked tokens — callers filter as
    /// needed. Drives the `RevokeOnIssuerRemoved` companion (Phase 66
    /// §3.11): when a team member is removed, the companion enumerates
    /// the leaver's outstanding claims and revokes each, so a departed
    /// issuer's share-links stop minting access. There is no
    /// per-issuer index blob — the default impl scans the scope's
    /// token blobs and filters in memory, which is acceptable for the
    /// membership-change cadence this method serves.
    abstract ListByIssuer: scopeId: string * issuerUserId: UserId -> Async<ShareTokenClaim list>