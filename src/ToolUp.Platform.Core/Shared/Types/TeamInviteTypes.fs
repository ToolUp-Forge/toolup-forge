// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Team-invitation substrate ───────────────────────────────────────
//
// Additive surface on top of the existing `IShareTokenStore` (Phase
// 21b). A team-invite token is a share-token with
// `ResourceKind = "platform.team_invite"` and a `ResourceId` of the
// team id. The full invitation-specific claim shape (role assigned on
// acceptance, inviter user id, optional email hint, max uses) lives in
// `TeamInviteClaim` below and is carried alongside the share-token
// substrate's generic claim — the invitation handler is responsible
// for serialising / deserialising it into the share-token's
// `AttributedHandle` field, which the substrate already round-trips
// verbatim.
//
// **Portability** — six rules audit:
//
//   1. Identity by value. `TeamId`, `Token`, `TokenId`, `InviterUserId`
//      all strings; `Role : TeamRole` is a small value-typed DU.
//   2. Async at every boundary. `ITeamInviteApi` returns `Async<_>`
//      from every method; the underlying `IShareTokenStore` already
//      enforces this rule.
//   3. Retry / supervision as data. Failures returned as `Result<_,
//      string>` strings the UI can render; no callbacks.
//   4. Stateless handlers between invocations. The invitation handler
//      reads / writes the `IShareTokenStore` blob path on every call;
//      no in-memory continuation contract between Accept/Revoke calls.
//   5. No cross-shard ordering promises. Each invite is independent.
//   6. Precision N/A — invite validity is timestamp-bounded, not
//      tick-driven.

/// Server-side claim attached to a team-invite share-token. The
/// share-token substrate persists the generic `ShareTokenClaim` plus
/// this invite-specific payload (serialised into the share-token's
/// `AttributedHandle` field as JSON). Reads on `IShareTokenStore.Validate`
/// return the generic claim; the invitation handler deserialises the
/// payload here and applies the role on acceptance.
type TeamInviteClaim = {
    /// Team the invitee will join on successful acceptance. Echoed as
    /// the share-token's `ResourceId`.
    TeamId: string
    /// Role assigned to the invitee at acceptance time. Owner cannot
    /// be granted via an invitation — the handler refuses
    /// `Owner` at issue time. Member and Admin are both valid.
    Role: TeamRole
    /// `UserId` of the inviter (the caller's authenticated user at
    /// issue time). Used for audit attribution and for permission
    /// checks when listing or revoking pending invites.
    InviterUserId: string
    /// Absolute expiry timestamp. Mirrors the share-token's
    /// `ExpiresAt` (the store enforces it independently); echoed here
    /// for UI rendering convenience without a second store round-trip.
    ExpiresAt: DateTime
    /// Optional email hint. When present, the issuance flow can also
    /// dispatch the invitation link via the configured
    /// `INotificationSink` of `Email` kind (Phase 3d optional
    /// wiring). Does not gate acceptance — the recipient need not
    /// match this email to redeem the link.
    EmailHint: string option
    /// Maximum number of acceptances. Default `1` for the typical
    /// one-and-done invitation; admins issuing a "any new joiner can
    /// click this link once" set it higher. Mirrors the share-token's
    /// `UseLimit` (the store enforces it independently).
    MaxUses: int
}

/// Lightweight summary surfaced to `ITeamInviteApi.ListPendingInvites`.
/// Carries only what the admin UI needs to render the Pending Invites
/// tab — the full claim is reachable via `IShareTokenStore.Validate`
/// when a specific invite needs deep inspection.
type TeamInviteSummary = {
    /// Share-token id, used to identify a specific invite when
    /// revoking. Stable across the invite's lifetime.
    TokenId: string
    /// Team id the invite would attach to (always matches the listing
    /// query's `teamId`; echoed for symmetry with the share-token
    /// substrate's listing shape).
    TeamId: string
    /// Role the invitee will be granted on acceptance.
    Role: TeamRole
    /// User id of the inviter — surfaced for audit transparency.
    InviterUserId: string
    /// Email hint provided at issue time, if any.
    EmailHint: string option
    /// Absolute expiry.
    ExpiresAt: DateTime
    /// Remaining uses — `MaxUses - UsedCount`. Reaches `0` after the
    /// final acceptance; the substrate keeps the row for audit
    /// continuity but `Validate` returns `UseLimitExceeded`.
    RemainingUses: int
    /// `true` once the invite has been revoked. Revoked invites
    /// remain visible to the admin UI so the operator can see the
    /// trail; the substrate rejects redemption attempts.
    Revoked: bool
}

/// Acceptance result returned by `ITeamInviteApi.AcceptInvite` on
/// success. The minimum surface a calling client needs to render
/// "you've joined team X as role Y" without a second round-trip to
/// resolve the team name.
type TeamInviteAcceptResult = {
    TeamId: string
    /// Display name of the team at the moment of acceptance.
    TeamName: string
    Role: TeamRole
}

/// Issuance result returned by `ITeamInviteApi.IssueInvite` on
/// success. `InviteUrl` is the consumer-facing link (typically
/// `https://<app>/invite/<token>`); `TokenId` is the substrate-level
/// id used to revoke later.
type TeamInviteIssueResult = { InviteUrl: string; TokenId: string }

/// Email-keyed pre-invite row, persisted in
/// `_platform/pending-invites.json` and consumed by
/// `ScopeResolutionMiddleware` on first sign-in matching the email
/// claim. Distinct from a link-based invite: no token is minted, no
/// URL is shared. Useful when an admin knows a colleague's email and
/// wants the join to happen automatically on first sign-in.
type PendingInviteByEmail = {
    /// Team the invitee will join on first authenticated request
    /// matching `email`.
    TeamId: string
    /// Role assigned at join time. Same `Owner` restriction as the
    /// link-based flow.
    Role: TeamRole
    /// Absolute expiry. The substrate refuses to consume an
    /// expired pending entry; cleanup of expired rows happens
    /// opportunistically on read.
    ExpiresAt: DateTime
    /// Inviter — captured at issue time for audit attribution.
    InviterUserId: string
}

module TeamInviteTypes =
    /// Reserved share-token `ResourceKind` constant identifying a
    /// team invitation. `ITeamInviteApi.AcceptInvite` rejects any
    /// token whose `ShareTokenClaim.ResourceKind` doesn't match —
    /// guards against a token issued for a different surface (a
    /// publishable Forms survey, a future shareable dashboard) being
    /// replayed against the invitation accept handler.
    [<Literal>]
    let ResourceKind = "platform.team_invite"

    /// Reserved `SourceModule` for invitation audit events. Filtering
    /// `IEventStore` reads on this constant returns the invitation
    /// audit trail only. Recorded under `team-{teamId}` scope so the
    /// audit row is naturally team-isolated.
    [<Literal>]
    let AuditSourceModule = "_platform.team_invites"

    /// Default expiry applied when `TeamInviteIssueRequest.ExpiresIn`
    /// is `None`. 7 days — long enough for "click the link in your
    /// inbox next week" without being indefinite. Distinct from the
    /// share-token substrate's 30-day default because invitations
    /// to a team are higher-trust than publishable survey links and
    /// warrant a tighter window.
    let DefaultExpiry: TimeSpan = TimeSpan.FromDays 7.0

    /// Default `MaxUses` when `TeamInviteIssueRequest.MaxUses` is
    /// `None`. One-and-done; the common case is "invite this one
    /// person".
    let DefaultMaxUses: int = 1

    /// Blob path the email-keyed pre-invite store persists to under
    /// `_platform/` scope. Read by `ScopeResolutionMiddleware` on
    /// every sign-in resolve (in-memory cached, 30-second TTL).
    [<Literal>]
    let PendingInvitesBlobPath = "_platform/pending-invites.json"
// (Audit payload records live in `AuditTypes.fs` alongside the other
// share-token audit payloads — appended at the end of that file. The
// `AuditEvent` DU and its `eventTypeName` projection are extended in
// the same file with the five `TeamInvite*` cases.)