// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── IPresenceChannel — Phase 241 ────────────────────────────────────
//
// The thin first slice of real-time collaboration: "who is in this
// scope right now, and when were they last seen". Co-editing / cursors
// (OT/CRDT) are deliberately out of scope — a much larger problem left
// to a future phase. Presence rides the shipped INotificationChannel
// (Phase 6a) for roster-change fan-out; the in-memory default serves a
// single node, and a distributed deployment swaps in the distributed
// notification channel companion with no presence-code change.
//
// Scope isolation (GP 4): a roster read returns ONLY the calling
// scope's members. The scopeId is always resolved from the caller's
// authenticated request, never accepted from an untrusted source — the
// same trust boundary as INotificationChannel.
//
// Portability (GP 12): identity by value (scopeId / principalId
// strings, PresenceEntry record), async at every boundary, stateless
// between calls (a distributed impl may serve successive calls from
// different nodes), per-scope sharding with no cross-scope ordering
// claim, expiry precision declared by the implementation's heartbeat
// window.

/// A participant's liveness within a scope.
type PresenceStatus =
    /// Heartbeat seen within the channel's freshness window.
    | Online
    /// Last heartbeat is stale (past the away threshold) but the entry
    /// has not yet expired out of the roster.
    | Away

/// One participant's presence within a scope.
type PresenceEntry = {
    PrincipalId: string
    DisplayName: string option
    LastSeen: DateTime
    Status: PresenceStatus
}

/// Scope-isolated presence roster. `Join` / `Heartbeat` keep a
/// participant live; `Leave` removes them; `Roster` reads the current
/// (non-expired) members of one scope.
type IPresenceChannel =
    /// Mark a principal present in a scope (idempotent — re-join
    /// refreshes liveness).
    abstract Join: scopeId: string * principalId: string * displayName: string option -> Async<unit>
    /// Refresh a principal's liveness (called on an interval by the
    /// client). Upserts if the principal was not already present.
    abstract Heartbeat: scopeId: string * principalId: string -> Async<unit>
    /// Remove a principal from a scope's roster.
    abstract Leave: scopeId: string * principalId: string -> Async<unit>
    /// The current non-expired roster for a scope. Scope-isolated:
    /// returns only this scope's members.
    abstract Roster: scopeId: string -> Async<PresenceEntry list>