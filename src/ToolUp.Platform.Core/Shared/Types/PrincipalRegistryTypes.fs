// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 543 — derived principal enumeration (shared types) ────────
//
// One row of the platform's derived, read-only principal enumeration:
// every user the substrate has *evidence* for, aggregated from the
// membership blobs, the `user-*` storage scopes, and the `UserLoggedIn`
// audit trail. A projection over existing stores — never a stored
// registry, so a stale entry cannot exist. Server-side aggregation
// lives in `ToolUp.Platform.PrincipalRegistry`; the admin wire surface
// is `IPlatformTenantApi.ListPrincipals`.

/// A principal the substrate has evidence for. Identity is carried by
/// value (`UserId: string`, `teamId: string`) — no live handles
/// (GP 12 rule 1). Fable-compatible: plain fields + a computed member.
type PrincipalSummary = {
    /// The principal's user id, as it appears in membership blobs,
    /// `user-{userId}` scope names, and `UserLoggedInPayload.UserId`.
    UserId: string
    /// Every `(teamId, role)` membership row recorded for the user in
    /// the `_platform/memberships/` store. Empty when the user belongs
    /// to no team.
    Memberships: (string * TeamRole) list
    /// The most recent `UserLoggedIn` audit event's `OccurredAt` within
    /// the aggregation's look-back window. `None` when the user has not
    /// signed in inside the window (or audit is not composed).
    LastSeenAt: DateTime option
    /// Whether the substrate holds data in the user's personal
    /// `user-{userId}` scope — blob(s) in the scope container or
    /// event(s) persisted under the scope.
    HasUserScopeData: bool
} with

    /// Derived, not stored: `true` exactly when no membership row
    /// exists. Computed from `Memberships` so it can never be
    /// constructed inconsistently with the evidence.
    member this.TeamLess = List.isEmpty this.Memberships