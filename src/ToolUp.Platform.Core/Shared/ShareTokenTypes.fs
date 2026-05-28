// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Share token substrate (IShareTokenStore) ─────────────────────────
//
// Generic, opt-in token primitive that lets any module issue signed,
// scoped, expiring, use-limited tokens for delegated access. Forms is
// the first consumer (publishable forms / opinion surveys); the
// substrate is module-agnostic — future shareable-dashboard, magic-
// login-link, or public read-only-view surfaces bind to the same
// interface.
//
// Lives in the Fable-compatible `Shared/` layer so admin UIs can
// render the same `ShareTokenClaim` shape the server persists.
//
// **Portability** — the six rules are honoured below:
//
//   1. Identity by value. `TokenId` / `ScopeId` / `ResourceId` are all
//      strings. No live handles cross the interface.
//   2. Async at every boundary. Enforced on `IShareTokenStore` (see
//      that interface).
//   3. Retry / supervision as data. Failures returned as `ShareTokenError`;
//      no callbacks.
//   4. Stateless handlers between invocations. Validation re-reads the
//      persisted claim on every call — no in-memory state assumed.
//   5. No cross-shard ordering promises. Each token is independent.
//   6. Precision N/A — token validity is timestamp-bounded, not
//      tick-driven.

/// Phase 21e — per-token rate-limit window. Bounded use rate so a
/// leaked token cannot be exercised at full request rate until its
/// `UseLimit` is exhausted. `MaxUses` requests are admitted per
/// rolling `Window`; the (MaxUses + 1)th request inside the window
/// returns `ShareTokenError.RateLimited` (or the consumer's
/// equivalent — Forms maps to `FormError.RateLimited`). `None` on
/// `ShareTokenClaim.RateLimit` means no per-token rate limit (the
/// existing `UseLimit` cap is the only throttle).
type ShareTokenRateLimit = {
    /// Requests admitted per rolling `Window`. Must be ≥ 1 at
    /// issue-time (validated by `ShareTokenTypes.validateRateLimit`).
    MaxUses: int
    /// Window over which `MaxUses` applies. Must be ≥ 1 second.
    Window: TimeSpan
}

/// Immutable claim describing what a share token is good for. Stored
/// server-side under `IShareTokenStore`'s blob layout; the public
/// token string carries `TokenId` plus a signature so the validator
/// can fast-fail before the storage hit, but the persisted record is
/// the source of truth — a valid signature alone is not sufficient
/// to use the token.
type ShareTokenClaim = {
    /// Public id, unique per token. Embedded in the token string and
    /// in the storage path; effectively immutable once issued.
    TokenId: string
    /// Storage scope at issue time. Tokens never widen scope on
    /// validation — a token issued under team A cannot reach team B's
    /// data even if the consuming handler doesn't re-check.
    ScopeId: string
    /// Free-form resource discriminator. Conventionally
    /// `"<companion>.<surface>"` — e.g. `"forms.publishable"` for the
    /// Forms public-survey flow. The consuming handler is responsible
    /// for matching this string against its own surface.
    ResourceKind: string
    /// Resource id within `ResourceKind`'s namespace (e.g. a
    /// `FormSchemaId` for `forms.publishable`).
    ResourceId: string
    /// Optional opaque recipient handle. The issuer chooses the
    /// format — could be an email, a hashed panel id, or any string
    /// the issuer's own systems use to attribute this token. Echoed
    /// back to the consuming handler on every successful `Validate`.
    /// `None` for unattributed share-link tokens.
    AttributedHandle: string option
    /// User who issued the token. Authenticated identity from the
    /// caller's `AccessContext` at issue time.
    IssuedBy: string
    IssuedAt: DateTimeOffset
    ExpiresAt: DateTimeOffset
    /// `Some n` = token is good for at most `n` uses (most surveys
    /// want 1). `None` = unlimited; useful for shared-link surfaces
    /// where the token is the read-secret for a public dashboard.
    UseLimit: int option
    /// Number of times `MarkUsed` has succeeded for this token.
    /// Persisted; bumped atomically on each successful use.
    UsedCount: int
    /// `true` once `Revoke` has been called. `Validate` rejects
    /// revoked tokens regardless of expiry / use count.
    Revoked: bool
    /// Phase 21e — optional per-token rate-limit window. `Some` =
    /// `IShareTokenRateLimiter` consults this on every use; the
    /// (MaxUses + 1)th admission inside the window is rejected.
    /// `None` = no per-token rate limit; only the static `UseLimit`
    /// cap applies. Defaulted to `None` by `BlobShareTokenStore.Issue`
    /// when `ShareTokenIssueRequest.RateLimit = None`.
    RateLimit: ShareTokenRateLimit option
}

/// Input to `IShareTokenStore.Issue`. The store fills in `TokenId`,
/// `IssuedAt`, `UsedCount`, and `Revoked` server-side so the caller
/// cannot forge them. `ExpiresAt` and `UseLimit` are option-of-option
/// to distinguish "use the default" from "explicitly unlimited".
type ShareTokenIssueRequest = {
    ScopeId: string
    ResourceKind: string
    ResourceId: string
    AttributedHandle: string option
    IssuedBy: string
    /// `None` = use the store's default (typically issue-time + 30
    /// days). `Some at` = explicit expiry.
    ExpiresAt: DateTimeOffset option
    /// `None` = use the store's default (typically `Some 1` for one-
    /// use tokens). `Some None` = explicitly unlimited. `Some (Some n)`
    /// = at most `n` uses.
    UseLimit: int option option
    /// Phase 21e — optional per-token rate-limit window. Validated at
    /// issue-time (see `ShareTokenTypes.validateRateLimit`); persisted
    /// verbatim on the resulting `ShareTokenClaim`.
    RateLimit: ShareTokenRateLimit option
}

/// Successfully issued token. The wire-format `Token` string is what
/// the issuer hands to the consumer (URL query param, header, etc.);
/// the embedded `Claim` is exposed for convenience so callers don't
/// have to round-trip through `Validate` immediately after issue.
type ShareToken = {
    /// Wire format: `{tokenId}.{base64url(payload)}.{base64url(hmac)}`.
    /// Opaque to consumers — never construct manually.
    Token: string
    Claim: ShareTokenClaim
}

/// Failure shape for `IShareTokenStore`. Distinct cases let consumers
/// decide which to surface to the end user (`Expired` → "this link
/// has expired") versus internal log-and-fail (`StorageFailed` → 500).
///
/// `[<RequireQualifiedAccess>]` because the case names (`NotFound`,
/// `Malformed`, `StorageFailed`) collide with sibling DUs in the SDK
/// (`DataObjectError`, etc.). Callers say `ShareTokenError.NotFound`
/// to disambiguate.
[<RequireQualifiedAccess>]
type ShareTokenError =
    /// Token string couldn't be parsed into the expected shape.
    | Malformed
    /// Signature check failed — token was tampered with or signed
    /// against a different key. Indistinguishable from `NotFound` to
    /// consumers to avoid leaking which tokens exist.
    | InvalidSignature
    /// Token's `TokenId` does not exist in the store. Same observable
    /// as `InvalidSignature` for security; distinct so server logs can
    /// tell the cases apart.
    | NotFound
    /// `ExpiresAt` is in the past at validation time.
    | Expired
    /// `Revoke` was called on this token earlier.
    | RevokedToken
    /// `UseLimit` reached. `MarkUsed` returns this when the increment
    /// would push `UsedCount` past `UseLimit`.
    | UseLimitExceeded
    /// Phase 21e — per-token rate-limit window admission denied.
    /// `IShareTokenRateLimiter.Admit` returns this when the caller
    /// has consumed its `RateLimit.MaxUses` budget inside the rolling
    /// `RateLimit.Window`. Distinct from `UseLimitExceeded` (the
    /// static total cap) — a rate-limited token still has uses
    /// remaining; it has just exceeded its per-window rate.
    | RateLimited
    /// Underlying storage / signer / secret-store raised. The string
    /// is operator-visible (logs) but should not be surfaced raw to
    /// end users.
    | StorageFailed of string

module ShareTokenTypes =
    /// Default lifetime for newly-issued tokens when the caller passes
    /// `ExpiresAt = None`. 30 days — long enough for "click the link
    /// in your inbox next week" without being indefinite.
    let DefaultLifetime: TimeSpan = TimeSpan.FromDays 30.0

    /// Default `UseLimit` when the caller passes `UseLimit = None`.
    /// Surveys want one-and-done semantics; explicit `Some None` opts
    /// out for unlimited surfaces.
    let DefaultUseLimit: int option = Some 1

    /// Reserved `SourceModule` for share-token audit events. Filtering
    /// `IEventStore` reads on this constant returns the share-token
    /// audit trail only.
    [<Literal>]
    let AuditSourceModule = "_platform.share_tokens"

    /// Phase 21e — issue-time validation for the optional rate-limit
    /// field on `ShareTokenIssueRequest`. Rejects malformed values at
    /// issue-time rather than first-use so operators see the failure
    /// in the issue flow rather than the first respondent's request.
    /// `None` (no rate limit) is always valid.
    let validateRateLimit (rate: ShareTokenRateLimit option) : Result<unit, string> =
        match rate with
        | None -> Ok()
        | Some r when r.MaxUses < 1 -> Error(sprintf "ShareTokenRateLimit.MaxUses must be >= 1 (got %d)." r.MaxUses)
        | Some r when r.Window < TimeSpan.FromSeconds 1.0 ->
            Error(sprintf "ShareTokenRateLimit.Window must be >= 1 second (got %O)." r.Window)
        | Some _ -> Ok()