// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Offline

open System

// ─── Phase 24 — offline-first queued-mutation model ──────────────────
//
// The wire vocabulary shared by the client queue, the sync coordinator
// and the server-side sync handler. Everything here is FSharp.Core +
// BCL primitives so the whole file Fable-compiles (GP 10) — the client
// tier constructs these values in the browser and the server tier
// consumes the identical shapes off the Fable.Remoting wire.
//
// DESIGN NOTE — last-writer-wins, not CRDT. A queued mutation carries
// the entity bytes the user produced offline plus the server version
// the edit was based on. On replay the server either applies it (the
// base version is still head) or reports a conflict and hands BOTH
// documents back for the user to choose between. There is no automatic
// merge; that is a deliberate v1 boundary, recorded in the phase's
// out-of-scope list, and the reason `Conflict` carries two payloads
// rather than a merged one.

/// Client-minted identity for one queued mutation. A string rather than
/// a `Guid` so the value survives IndexedDB, JSON and MsgPack round
/// trips unchanged, and so any queue implementation can mint one
/// (portability rule 1 — identity by value, never a live handle).
type MutationId = string

/// What a queued mutation asks the server to do on replay.
///
/// Deliberately only the two operations `IEntityStore` exposes as
/// writes. A queued "query" is meaningless — reads are served from the
/// service worker's cache, never queued.
type MutationOp =
    /// Create-or-update. `Payload` carries the serialised entity.
    | SaveOp
    /// Delete by id. `Payload` is empty.
    | DeleteOp

module MutationOp =
    /// Stable wire name. Used as the IndexedDB record discriminator and
    /// in audit/diagnostic strings, so it must not drift with the case
    /// name.
    let name (op: MutationOp) : string =
        match op with
        | SaveOp -> "save"
        | DeleteOp -> "delete"

    /// Inverse of `name`. `None` for an unrecognised token — a queue
    /// record written by a newer SDK is skipped rather than guessed at.
    let tryParse (token: string) : MutationOp option =
        match token with
        | "save" -> Some SaveOp
        | "delete" -> Some DeleteOp
        | _ -> None

/// One pending write, as it sits in the client's durable queue.
///
/// `EnqueuedAt` is the ORIGINATION time — when the user performed the
/// edit, not when it reached the server. It is the value the server
/// stamps onto the replayed audit record, which is the whole point of
/// keeping it: an inspector's edit made at 09:14 in a tunnel and synced
/// at 11:02 is audited as having happened at 09:14.
type QueuedMutation = {
    Id: MutationId
    /// When the user made the edit, client clock, UTC offset preserved.
    EnqueuedAt: DateTimeOffset
    /// Entity-store scope the write belongs to (team / tenant).
    ScopeId: string
    /// `EntityFieldsCore.Type` of the entity being written.
    EntityType: string
    /// `EntityFieldsCore.Id` of the entity being written.
    EntityId: string
    Operation: MutationOp
    /// Serialised entity for `SaveOp`; empty for `DeleteOp`.
    Payload: byte[]
    /// The server `Version` the offline edit was based on. `0` for an
    /// entity created entirely offline (nothing to conflict with).
    BaseVersion: int
    /// Monotonic per-client sequence number. Replay order within one
    /// client is this order; see the ordering contract on
    /// `IOfflineQueue`.
    LocalRevision: int
}

/// What the server did with one replayed mutation.
///
/// `Conflict` carries both documents rather than a merge because v1 is
/// last-writer-wins with an explicit user choice — see the design note
/// at the head of this file.
type SyncOutcome =
    /// Applied. Carries the server's post-write entity bytes so the
    /// client can replace its local copy (and pick up the new version).
    | Applied of serverEntity: byte[]
    /// The base version was stale. Both documents are returned; the
    /// user resolves via `ConflictResolution`.
    | Conflict of localEntity: byte[] * serverEntity: byte[]
    /// The mutation can never succeed (unknown entity type, malformed
    /// payload, refused by policy). The client drops it and surfaces
    /// the reason — retrying would loop forever.
    | Rejected of reason: string

/// How the user resolved a `Conflict`.
type ConflictResolution =
    /// Re-apply the local document over the server's, rebased onto the
    /// server's current version.
    | KeepLocal
    /// Abandon the local edit; the server document wins.
    | KeepServer
    /// Leave the conflict pending — the queue keeps the mutation in the
    /// `Conflicted` state and the badge keeps reporting it.
    | Defer

/// Where one queued mutation currently stands.
type MutationState =
    /// Waiting for the next drain.
    | Pending
    /// Replayed and applied; retained only until `MarkApplied` prunes it.
    | AppliedState
    /// Replayed and conflicted; waiting on a `ConflictResolution`.
    | Conflicted
    /// Replay failed transiently; eligible again after the backoff.
    | Failed of reason: string

module MutationState =
    /// Stable wire name, for the same reason `MutationOp.name` exists.
    let name (state: MutationState) : string =
        match state with
        | Pending -> "pending"
        | AppliedState -> "applied"
        | Conflicted -> "conflicted"
        | Failed _ -> "failed"

/// Retry/backoff behaviour expressed as DATA, never as callbacks —
/// portability rule 3. A distributed or worker-thread queue
/// implementation reads the same record; nothing about the retry
/// schedule is captured in a closure the implementation cannot inspect.
///
/// **Precision (rule 6):** delays are MILLISECONDS and honoured to
/// whole milliseconds. Browser timers are throttled in background tabs
/// (typically to >= 1 s), so a caller must not read a delay below
/// ~1000 ms as a promise about wall-clock wake-up — it is a lower
/// bound on how long the coordinator waits, not an upper bound.
type RetryPolicy = {
    /// Delay before the first retry.
    InitialDelayMs: int
    /// Multiplier applied per successive attempt.
    Multiplier: float
    /// Ceiling — the delay never exceeds this however many attempts
    /// have failed.
    MaxDelayMs: int
    /// Attempts before the mutation is parked as `Failed` and stops
    /// consuming drain budget. `0` means never park (retry forever on
    /// every reconnect).
    MaxAttempts: int
}

module RetryPolicy =
    /// The SDK default: 1 s, doubling, capped at 5 min, 8 attempts.
    ///
    /// Eight attempts under this schedule spans roughly 20 minutes of
    /// connectivity, which is long enough to ride out a flapping link
    /// and short enough that a genuinely dead endpoint parks rather
    /// than spinning for the life of the tab.
    let defaults: RetryPolicy = {
        InitialDelayMs = 1000
        Multiplier = 2.0
        MaxDelayMs = 300_000
        MaxAttempts = 8
    }

    /// Delay before attempt `attempt` (1-based: `delayFor policy 1` is
    /// the wait after the FIRST failure).
    ///
    /// Total, monotonic and clamped: attempt <= 0 yields the initial
    /// delay rather than raising, and the exponent is capped so a
    /// pathological attempt count cannot overflow into a negative or
    /// infinite delay. A retry schedule that throws is worse than one
    /// that is merely slow.
    let delayFor (policy: RetryPolicy) (attempt: int) : int =
        let initial = max 0 policy.InitialDelayMs
        let ceiling = max initial policy.MaxDelayMs

        if attempt <= 1 then
            min initial ceiling
        else
            // Cap the exponent before computing the power. 2^30 ms is
            // already ~12 days, far past any sane MaxDelayMs, so this
            // clamps without changing any reachable result.
            let exponent = min (attempt - 1) 30
            let multiplier = if policy.Multiplier < 1.0 then 1.0 else policy.Multiplier
            let scaled = float initial * (multiplier ** float exponent)

            if Double.IsNaN scaled || Double.IsInfinity scaled || scaled >= float ceiling then
                ceiling
            else
                max initial (int scaled)

    /// True when `attempts` failures have exhausted the policy and the
    /// mutation should be parked. `MaxAttempts = 0` never exhausts.
    let isExhausted (policy: RetryPolicy) (attempts: int) : bool =
        policy.MaxAttempts > 0 && attempts >= policy.MaxAttempts

/// A queue entry as READ back — the mutation plus the bookkeeping the
/// queue maintains around it. Returned by `Drain` / `List` so a caller
/// can render the badge and the conflict resolver without a second
/// round trip.
type QueueEntry = {
    Mutation: QueuedMutation
    State: MutationState
    /// Failed replay attempts so far. Feeds `RetryPolicy.delayFor`.
    Attempts: int
    /// Server document from the last `Conflict`, if any. `None` in
    /// every other state.
    ServerEntity: byte[] option
}

/// Which queue entries are eligible for replay right now.
///
/// Lives in Core, not beside the IndexedDB implementation, for two
/// reasons: it is the queue's only non-trivial decision, and keeping it
/// here makes it testable off-browser — a rule that lives only inside a
/// `[<Emit>]`-bearing client file is a rule nothing can assert.
module DrainSelection =
    /// True when an entry may be replayed now.
    ///
    /// The backoff runs from the ORIGINAL enqueue time plus the
    /// accumulated delay rather than from a stored last-attempt stamp.
    /// Deliberately conservative: the browser may have been closed
    /// across the whole backoff window, in which case the entry is due
    /// immediately on the next boot — which is the behaviour a field
    /// user wants when they reopen the app in signal.
    let isRetryDue (policy: RetryPolicy) (now: DateTimeOffset) (entry: QueueEntry) : bool =
        match entry.State with
        | Pending -> true
        | AppliedState
        | Conflicted -> false
        | Failed _ ->
            if RetryPolicy.isExhausted policy entry.Attempts then
                false
            else
                let delayMs = RetryPolicy.delayFor policy entry.Attempts
                entry.Mutation.EnqueuedAt.AddMilliseconds(float delayMs) <= now

    /// Eligible entries, in enqueue order. Every queue implementation
    /// routes `Drain` through this, so two implementations cannot
    /// disagree about what is due.
    let eligible (policy: RetryPolicy) (now: DateTimeOffset) (entries: QueueEntry list) : QueuedMutation list =
        entries
        |> List.filter (isRetryDue policy now)
        |> List.sortBy _.Mutation.LocalRevision
        |> List.map _.Mutation

/// Aggregate counts for the status badge. Derived, never stored.
type QueueStats = {
    Pending: int
    Conflicted: int
    Failed: int
}

module QueueStats =
    let empty: QueueStats = {
        Pending = 0
        Conflicted = 0
        Failed = 0
    }

    /// Fold a set of entries into counts. `AppliedState` entries are
    /// counted nowhere — they are settled and awaiting prune.
    let ofEntries (entries: QueueEntry list) : QueueStats =
        entries
        |> List.fold
            (fun acc e ->
                match e.State with
                | Pending -> { acc with Pending = acc.Pending + 1 }
                | Conflicted -> {
                    acc with
                        Conflicted = acc.Conflicted + 1
                  }
                | Failed _ -> { acc with Failed = acc.Failed + 1 }
                | AppliedState -> acc)
            empty

    /// True when nothing is outstanding — the badge's "all clear".
    let isSettled (stats: QueueStats) : bool =
        stats.Pending = 0 && stats.Conflicted = 0 && stats.Failed = 0

/// What the status badge shows. Derived from connectivity + queue
/// stats by `SyncStatus.derive`, so the badge never holds its own
/// state machine.
type SyncStatus =
    /// Online, nothing queued.
    | Online
    /// No connectivity. `pending` is what will replay on reconnect.
    | Offline of pending: int
    /// A drain is in flight.
    | Syncing of remaining: int
    /// Online and drained, but conflicts await the user.
    | ConflictsPending of count: int

module SyncStatus =
    /// The single derivation. `draining` is the coordinator's own
    /// in-flight flag; everything else comes from the queue.
    ///
    /// Order matters: an offline client with conflicts reports
    /// `Offline`, because reconnecting is the action that unblocks it
    /// and telling the user to resolve conflicts they cannot yet
    /// submit is noise.
    let derive (isOnline: bool) (draining: bool) (stats: QueueStats) : SyncStatus =
        if not isOnline then
            Offline stats.Pending
        elif draining then
            Syncing(stats.Pending + stats.Failed)
        elif stats.Conflicted > 0 then
            ConflictsPending stats.Conflicted
        else
            Online

    /// Short human label. Kept beside `derive` so a new case cannot
    /// gain a status without gaining a label (FS0025 is an error
    /// tree-wide, so this match must stay exhaustive).
    let label (status: SyncStatus) : string =
        match status with
        | Online -> "Online"
        | Offline 0 -> "Offline"
        | Offline n -> sprintf "Offline — %d queued" n
        | Syncing n -> sprintf "Syncing — %d left" n
        | ConflictsPending n -> sprintf "%d conflict(s)" n