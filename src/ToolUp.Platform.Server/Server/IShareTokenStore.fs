namespace ToolUp.Platform

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
    /// Does NOT increment `UsedCount` — call `MarkUsed` separately
    /// after the consuming operation succeeds. This split lets the
    /// caller validate-then-act-then-bump without burning a use-slot
    /// on a request that fails downstream.
    abstract Validate: token: string -> Async<Result<ShareTokenClaim, ShareTokenError>>

    /// Atomically increment `UsedCount`. Returns `Error UseLimitExceeded`
    /// when the increment would push `UsedCount` past `UseLimit`.
    /// Callers must call exactly once per consumed use — there is no
    /// rollback.
    abstract MarkUsed: scopeId: string * tokenId: string -> Async<Result<unit, ShareTokenError>>

    /// Permanently revoke a token. Subsequent `Validate` returns
    /// `Error RevokedToken`. Idempotent — revoking an already-revoked
    /// token returns `Ok ()`. `actorUserId` identifies who revoked
    /// for audit purposes.
    abstract Revoke: scopeId: string * tokenId: string * actorUserId: string -> Async<Result<unit, ShareTokenError>>

    /// Enumerate every claim for a `(resourceKind, resourceId)` pair
    /// within `scopeId`. Returns both active and revoked tokens —
    /// callers filter as needed. Used by admin UIs ("which tokens
    /// have I issued for this form?") and aggregation views.
    abstract ListByResource: scopeId: string * resourceKind: string * resourceId: string -> Async<ShareTokenClaim list>