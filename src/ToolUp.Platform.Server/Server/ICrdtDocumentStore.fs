// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── ICrdtDocumentStore — Phase 535 ──────────────────────────────────
//
// The co-editing substrate: an append-only log of opaque CRDT updates
// per document, with a resumable cursor over it. Five operations —
// `Append`, `GetStateVector`, `GetDiff`, `Snapshot`, `Compact` — and not
// one of them looks inside a payload.
//
// **The store is a relay with a memory, not a merge engine.** It cannot
// merge two updates, cannot tell you what the document says, and cannot
// validate a payload beyond its length. That is the point: the merge
// maths lives in the client's CRDT library (Yjs, MIT — an npm dependency
// of the consuming app, GP 1 / GP 2), so the server carries no npm
// dependency, no CRDT library, and no opinion about the document
// encoding. Swap Yjs for Automerge or a hand-rolled encoding and nothing
// here changes.
//
// What the SDK owns instead is everything a CRDT library does NOT give
// you and every consumer would otherwise improvise incompatibly:
//
//   * **Durability** — a document that survives every participant
//     closing their tab.
//   * **Catch-up** — a resumable cursor, so a client offline for N
//     updates fetches exactly the tail it missed rather than the whole
//     history (`GetDiff`).
//   * **Scope isolation** — structural, via `CrdtDocRef` carrying the
//     scope as part of the key (GP 4).
//   * **Fan-out** — over the shipped `INotificationChannel` (Phase 6a).
//     No new transport; see `NotifyingCrdtDocumentStore`.
//   * **Compaction** — bounding a log that would otherwise grow forever.
//
// ## The join / catch-up / live protocol
//
// A co-editing client runs the same three steps whether it is joining
// for the first time, reconnecting after a network blip, or resuming
// after a day offline — there is deliberately no separate "resume" path
// to get wrong:
//
//   1. `GetDiff(ref, vector)` with the vector it retained, or
//      `StateVector.empty` on a cold start. Apply every returned payload
//      to the local CRDT document, in any order. Retain the new vector.
//   2. Subscribe to `_platform.crdt` on its scope and apply each
//      incoming `CrdtUpdateEvent` whose `OriginSession` is not its own.
//   3. `Append` each local update as the CRDT library emits it.
//
// A missed live event is not a correctness problem, only a latency one:
// the next `GetDiff` recovers it. That is the whole reason the cursor
// exists rather than the fan-out being made reliable.
//
// ## Offline interplay (Phase 24)
//
// CRDT updates are the one payload class that merges cleanly on
// reconnect, which is what makes an offline queue over this seam
// tractable where a general write queue needs conflict policy. Stated in
// prose deliberately: this phase takes no dependency on the offline
// queue's types, and the queue is not required to use this store. The
// properties that make the pairing work are (a) `Append` is
// order-insensitive — replaying a backlog in any order converges, so a
// queue needs no ordering guarantee beyond its own; (b) an update is
// idempotent, so a replay that duplicates a send costs a byte, not a
// corruption; and (c) `GetDiff` from the retained vector is the catch-up
// half, which the queue does not have to implement. What a queue must
// still own is retention — an update the client never managed to send is
// only in that client's local document until it does.
//
// ## Six-rule portability audit (GP 12)
//
//   1. **Identity by value.** `CrdtDocRef` / `StateVector` / `CrdtUpdate`
//      are records of strings, `byte[]`, `int64` and `DateTime`. No live
//      handle, no stream, no session object crosses the seam — a client
//      that resumes on a different node hands back the same vector value
//      and gets the same answer.
//   2. **Async at every boundary.** All five methods return `Async<_>`;
//      no compose-time-only carve-out is claimed.
//   3. **Retry / supervision as data.** The seam declares no retry
//      policy and takes no failure callback, because it has no
//      background work to supervise: every operation is caller-driven.
//      Fan-out failure is *deliberately* not a retry concern — see
//      `NotifyingCrdtDocumentStore`, which treats a publish failure as
//      advisory precisely because `GetDiff` is the durable recovery
//      path.
//   4. **Stateless between invocations.** Nothing is remembered about a
//      caller between calls. The resumable cursor is the *caller's*
//      state, handed in on every `GetDiff`; that is why it is a value
//      rather than a server-held session, and it is what lets successive
//      calls land on different nodes.
//   5. **No cross-shard ordering promises.** `CrdtDocRef` is the shard
//      key. `Sequence` is monotonic within one ref and means nothing
//      across refs — two documents' sequences are incomparable, and no
//      implementation may promise otherwise. Within a ref the log is
//      ordered, which the CRDT does not need but catch-up does.
//   6. **Precision at the lower bound.** No timing promise is made.
//      `AppendedAt` is a record-keeping timestamp for retention and
//      diagnostics, NOT a merge input: convergence must never depend on
//      clocks, because a distributed implementation's nodes disagree
//      about the time and the CRDT itself is what resolves concurrency.
//      Fan-out inherits `INotificationChannel`'s stated bound (delivery
//      is near-real-time, not sub-second guaranteed).
//
// ## Composition
//
// `ServerConfig.CrdtDocuments = EnabledCrdtDocuments` registers the
// in-memory default, relay-wrapped, into DI (see `ComposeStores`);
// `NoCrdtDocuments` (the default) registers nothing and costs nothing
// (GP 11 + GP 13). Substrate only: a deployment exposes its own
// module-owned API over the resolved store, exactly as Phase 442's
// presence substrate was consumed before Phase 622 mounted a platform
// API over it.

/// Scope-isolated append-only log of opaque CRDT updates, one log per
/// `CrdtDocRef`. Every payload is commutative bytes the store relays and
/// never interprets.
type ICrdtDocumentStore =
    /// Append one opaque update to `ref`'s log and return it with the
    /// store-assigned `Sequence` and `AppendedAt`. The sequence is
    /// monotonic within `ref` only.
    ///
    /// `originSession` names the producing client session so co-editors
    /// can drop their own echo; it is a value the store records, never
    /// interprets, and never uses to order anything.
    abstract Append: ref: CrdtDocRef * payload: byte[] * originSession: string -> Async<CrdtUpdate>

    /// The cursor covering everything currently logged for `ref`. A
    /// document with no updates yields `StateVector.empty`.
    abstract GetStateVector: ref: CrdtDocRef -> Async<StateVector>

    /// Every update `since` does not already cover, in append order.
    /// `StateVector.empty` yields the whole document; a vector this
    /// store issued yields exactly the tail after it; an unrecognised
    /// vector yields the whole document rather than failing (re-applying
    /// a held update is free, losing one is not).
    abstract GetDiff: ref: CrdtDocRef * since: StateVector -> Async<CrdtUpdate list>

    /// The document as it stands — the updates which, applied in any
    /// order to an empty CRDT document, reconstruct it, plus the vector
    /// covering them. Equivalent to `GetDiff` from `StateVector.empty`
    /// with the covering vector attached, and the read a joiner makes
    /// when it has retained nothing.
    abstract Snapshot: ref: CrdtDocRef -> Async<CrdtSnapshot>

    /// Replace every update `covers` accounts for with the single
    /// `merged` payload, and return the resulting snapshot.
    ///
    /// **Compaction is client-attested, and that is a structural
    /// consequence rather than a shortcut.** The store cannot merge
    /// opaque payloads, so the merged base must be computed by a
    /// participant that holds the document (the CRDT library's
    /// whole-state encoding) and handed back with the vector it was
    /// computed from. The base is stored under the reserved
    /// `CrdtDocument.CompactionOrigin`, sorts where the covered prefix
    /// sat, and is delivered to a joiner as an ordinary update.
    ///
    /// Two consequences a deployment must weigh before exposing this on
    /// a wire: a caller supplying a base that is not a faithful merge
    /// silently rewrites history for every later joiner (existing
    /// editors are unaffected — their local documents already hold the
    /// prefix), and a caller supplying a stale `covers` compacts less
    /// than it could. Neither can cross a scope boundary. The
    /// recommended posture is to compact server-side on a schedule from
    /// a trusted headless participant, or to restrict the operation to
    /// an authenticated editor of that document — never to expose it
    /// unauthenticated.
    ///
    /// `covers` must be a vector this store issued; `StateVector.empty`
    /// covers nothing and is refused (it would append a second copy of
    /// the whole document rather than compacting it).
    abstract Compact: ref: CrdtDocRef * merged: byte[] * covers: StateVector -> Async<CrdtSnapshot>