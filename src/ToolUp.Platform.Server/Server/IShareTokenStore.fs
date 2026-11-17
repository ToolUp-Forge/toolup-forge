namespace ToolUp.Platform.Scope

open ToolUp.Platform

// ─── IShareTokenStore ────────────────────────────────────────────────
//
// SDK-level interface for the share-token substrate. The
// default impl is `BlobShareTokenStore`; distributed companions
// (Redis-backed cache, signed-only-no-store edge variants) bind to
// this interface.
//
// **Portability rules** (all six honoured):
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
    /// marking would let concurrent callers exceed the limit; a caller that
    /// must not spend a use on work that fails marks first and returns the
    /// use with `ReleaseUse` (a public form submission does).
    abstract Validate: token: string -> Async<Result<ShareTokenClaim, ShareTokenError>>

    /// Atomically increment `UsedCount`. Returns `Error UseLimitExceeded`
    /// when the increment would push `UsedCount` past `UseLimit`.
    /// Callers must call exactly once per consumed use; a use whose work
    /// then fails is returned with `ReleaseUse`.
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

    /// Return one use taken by `MarkUsed`: decrement `UsedCount`, never
    /// below zero. For a caller that reserves a use BEFORE the work it
    /// admits (a public form submission) and must give the use back when
    /// that work fails. Revocation and expiry are untouched.
    ///
    /// The decrement is a conditional write, exactly as `MarkUsed`'s
    /// increment is: it cannot overwrite a concurrent reservation, so of
    /// a release racing K reservations on a `UseLimit = K` token no
    /// reservation is lost and none is admitted twice. An implementation
    /// that cannot make it conditional returns an `Error` rather than a
    /// check-then-write. On `Error` the use stays spent: the caller treats
    /// the link as consumed (fails closed) and never retries the work.
    /// `BlobShareTokenStore` makes it conditional on the claim's ETag on
    /// every `IConditionalBlobStorage` backend (every shipped backend) and
    /// serialises it in-process otherwise. The shipped decorators forward
    /// it unchanged.
    abstract ReleaseUse: scopeId: string * tokenId: string -> Async<Result<unit, ShareTokenError>>

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
    /// needed. Drives the `RevokeOnIssuerRemoved` companion: when a team member is removed, the companion enumerates
    /// the leaver's outstanding claims and revokes each, so a departed
    /// issuer's share-links stop minting access. There is no
    /// per-issuer index blob — the default impl scans the scope's
    /// token blobs and filters in memory, which is acceptable for the
    /// membership-change cadence this method serves.
    abstract ListByIssuer: scopeId: string * issuerUserId: UserId -> Async<ShareTokenClaim list>