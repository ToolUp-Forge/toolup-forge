// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── ITeamInviteApi — Fable.Remoting team-invitation surface ────────
//
// Authenticated-only API for issuing, accepting, listing, and
// revoking team-invitation tokens on top of `IShareTokenStore`. The
// public accept page (`/invite/{token}`) calls `AcceptInvite`
// directly without prior authentication via the SDK's existing
// `withAnonymousRoute` mechanism — see the server-side handler for
// the gate that ensures the visitor still authenticates before the
// membership is applied.
//
// Authorisation gates (enforced server-side, mirroring the existing
// `AddTeamMember` patterns):
//
//   * `IssueInvite` — caller must hold `TeamRole.Owner` or `Admin`
//     on the target team (`TeamRoles.canManageMembers`).
//   * `AcceptInvite` — any authenticated caller. The token IS the
//     authorisation: anyone with the link can claim the role baked
//     into the claim, capped by `MaxUses` and `ExpiresAt`.
//   * `RevokeInvite` — caller must hold `Owner`/`Admin` on the team
//     associated with the token (looked up via `IShareTokenStore`).
//   * `ListPendingInvites` — caller must hold `Owner`/`Admin` on the
//     listed team.
//
// All `Error` strings are operator-readable and safe to render to
// end users — they never include token content or signature
// fragments.

/// Input parameters to `ITeamInviteApi.IssuePendingInviteByEmail`.
/// Wraps the values admins supply in the no-link "invite by email"
/// modal. Always single-shot (the email match itself is the gate);
/// the recipient joins automatically on first authenticated request
/// matching `Email`. Server fills in `IssuedAt` + `IssuedBy` from the
/// authenticated caller's `AccessContext`.
type PendingInviteIssueRequest = {
    /// Team the invitation will attach the recipient to. Caller must
    /// hold `Owner`/`Admin` on this team.
    TeamId: string
    /// Email claim the recipient's IdP will issue when they sign in.
    /// Matched case-insensitively against the `email` claim on their
    /// first authenticated request resolve.
    Email: string
    /// Role granted on auto-join. Same `Owner` restriction as the
    /// link-based flow — rejected at the handler.
    Role: TeamRole
    /// Lifetime from issue time. `None` -> default
    /// (`TeamInviteTypes.DefaultExpiry`, 7 days).
    ExpiresIn: TimeSpan option
}

/// Input parameters to `ITeamInviteApi.IssueInvite`. Wraps the values
/// admins supply in the issue-by-link modal. Server fills in
/// `TokenId`, `IssuedAt`, `IssuedBy`, and `UseCount = 0` from the
/// authenticated caller's `AccessContext`.
type TeamInviteIssueRequest = {
    /// Team the invitation will attach the recipient to.
    TeamId: string
    /// Role granted on acceptance. `Owner` rejected at the handler
    /// (a delegation surface should not be able to mint new
    /// owners; ownership transfer happens through a separate Phase 5f
    /// surface).
    Role: TeamRole
    /// Lifetime from issue time. `None` -> store's default
    /// (`ShareTokenTypes.DefaultLifetime`, typically 30 days).
    ExpiresIn: TimeSpan option
    /// Optional email hint. When present alongside a wired
    /// `INotificationSink` of `Email` kind, the issue handler
    /// dispatches the invitation link via the email companion.
    /// Best-effort — failure to send does not roll back issuance.
    EmailHint: string option
    /// Maximum number of acceptances permitted. `None` -> default
    /// `1`.
    MaxUses: int option
}

/// Fable.Remoting interface for team invitations.
type ITeamInviteApi = {
    /// Issue a new invite for the supplied team. Returns the
    /// consumer-facing URL (typically `https://<app>/invite/<token>`)
    /// and the substrate-level `TokenId` for later revocation. Caller
    /// must hold `Owner` or `Admin` on the team.
    IssueInvite: TeamInviteIssueRequest -> Async<Result<TeamInviteIssueResult, string>>

    /// Accept an invitation. Validates the supplied token, checks
    /// expiry and remaining uses, atomically calls
    /// `ITeamStore.AddMember` with the caller's authenticated user
    /// id and the role baked into the claim, decrements the
    /// token's use count, and emits `TeamInviteAccepted` +
    /// `TeamInviteRedeemed` audit events. The caller must be
    /// authenticated; the public `/invite/{token}` page is responsible
    /// for routing visitors through the configured sign-in flow before
    /// invoking this method.
    AcceptInvite: string -> Async<Result<TeamInviteAcceptResult, string>>

    /// Revoke an outstanding invite. Subsequent `AcceptInvite`
    /// against the same token returns
    /// `"This invitation has been revoked"`. Caller must hold
    /// `Owner`/`Admin` on the team the token was issued for.
    /// Idempotent — revoking an already-revoked token returns `Ok ()`.
    RevokeInvite: string -> Async<Result<unit, string>>

    /// List outstanding invitations for the supplied team. Caller
    /// must hold `Owner`/`Admin` on the team. Returns both active and
    /// revoked invites — the UI filters as needed (typically a
    /// "Show revoked" toggle).
    ListPendingInvites: string -> Async<Result<TeamInviteSummary list, string>>

    /// Issue a pending-by-email invitation. No link is minted; the
    /// recipient joins automatically on their first authenticated
    /// request whose email claim matches `request.Email`
    /// (case-insensitive). Caller must hold `Owner`/`Admin` on
    /// `request.TeamId`. Replaces any existing pending entry for the
    /// same email (last write wins).
    IssuePendingInviteByEmail: PendingInviteIssueRequest -> Async<Result<unit, string>>

    /// List every pending-by-email entry associated with the supplied
    /// team. Caller must hold `Owner`/`Admin` on the team. Returns
    /// `(email, pendingEntry)` pairs filtered to this team. Email
    /// strings are returned in their stored case (lower-invariant).
    ListPendingInvitesByEmail: string -> Async<Result<(string * PendingInviteByEmail) list, string>>

    /// Remove the pending entry for the supplied email. Idempotent —
    /// removing an absent entry returns `Ok ()`. Caller must hold
    /// `Owner`/`Admin` on the team the pending entry targets; the
    /// handler resolves the entry first to enforce the gate.
    RevokePendingInviteByEmail: string -> Async<Result<unit, string>>
}

module TeamInviteApi =
    /// Fable.Remoting `routeBuilder`. Surface lives at
    /// `/api/team-invite/<MethodName>`; authenticated by the existing
    /// `AuthEnforcementMiddleware` (no `withAnonymousRoute`
    /// registration — accepting an invite still requires a sign-in
    /// first).
    let routeBuilder (typeName: string) (methodName: string) : string =
        sprintf "/api/team-invite/%s" methodName