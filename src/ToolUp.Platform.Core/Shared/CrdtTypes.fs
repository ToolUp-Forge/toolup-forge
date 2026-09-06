// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── CRDT co-editing value types — Phase 535 ─────────────────────────
//
// The shared-tier half of the merge-free co-editing substrate. Phase 442
// shipped the *awareness* floor — who is here (`IPresenceTracker`) and
// who is editing what (`IEntityLockStore`, advisory soft-locks). This
// phase adds the tier above it: several people editing the SAME document
// at the SAME time, with no lock and no merge conflict.
//
// **The SDK never interprets a document.** Every payload below is an
// opaque `byte[]` the store appends, relays and hands back — the
// convergence maths lives entirely in the client's CRDT library (Yjs,
// MIT, an npm dependency of the consuming app — GP 1 / GP 2). The server
// therefore builds with no npm dependency and no CRDT library of any
// kind, and a deployment may swap Yjs for any other update-encoding CRDT
// without touching a line of SDK code. What the SDK owns is the *log*:
// ordering within one document, a resumable cursor over it, scope
// isolation, and fan-out.
//
// Relationship to Phase 442, stated because both are "collaboration"
// and the two are complementary rather than alternatives:
//
//   * **Soft-lock mode** (442) — one editor at a time, others read-only.
//     Right for documents whose format cannot merge (a binary artefact,
//     a schema whose invariants span fields).
//   * **Co-edit mode** (535, this file) — everyone edits, the CRDT
//     merges. Right for text and structured content whose encoding is
//     commutative.
//
//   A deployment picks per document class; they compose (a co-edited
//   document may still take a soft-lock over an adjacent
//   non-mergeable attachment). `docs/platform/co-editing.md` is the
//   decision page.
//
// **Awareness does NOT ride this log** (535.B). Cursors and selections
// are ephemeral, per-participant, and worthless once stale — exactly the
// shape `IPresenceTracker`'s location descriptor already carries, so
// `CrdtAwareness` below projects a co-editing position into a
// `PresenceLocation` rather than minting a second presence family. The
// update log stays durable-content-only.
//
// Portability (GP 12): every type here is by value — strings, `byte[]`,
// `int64`, `DateTime` — never a live handle. `CrdtDocRef` is the shard
// key, and the only ordering the substrate promises is within one ref
// (rule 5). Fable-safe: BCL primitives only, no server dependency, so
// the client tier shares these exact shapes (GP 10).

/// Value key identifying one co-edited document. `Scope` is the owning
/// team / tenant scope, `DocId` an opaque module-owned document
/// identifier the SDK never interprets.
///
/// **The scope is part of the key, not a filter** (GP 4). Two scopes
/// naming the same `DocId` address two structurally distinct documents —
/// a store shards on the whole ref, so a cross-tenant read is not a
/// forgotten `WHERE` clause but an impossible lookup. As everywhere else
/// in the SDK, `Scope` is resolved from the caller's authenticated
/// request and never accepted from an untrusted source (the same trust
/// boundary as `INotificationChannel.Publish`).
///
/// It is also the **shard key**: ordering is promised within one ref and
/// nowhere else (GP 12 rule 5).
type CrdtDocRef = { Scope: string; DocId: string }

module CrdtDocRef =
    /// A document ref within one scope.
    let create (scope: string) (docId: string) : CrdtDocRef = { Scope = scope; DocId = docId }

    /// Stable string form — `"<scope>/<docId>"`. Used as a client-side
    /// map key and in log lines; not a wire contract.
    let toKey (ref: CrdtDocRef) : string = sprintf "%s/%s" ref.Scope ref.DocId

/// A resumable cursor over one document's update log — the "I already
/// have everything up to here" token a client hands back on reconnect.
///
/// **Opaque by contract.** The bytes are the issuing store's own
/// encoding; no caller may parse, construct, compare-for-ordering, or
/// persist them across stores. That is what lets an implementation
/// choose whatever cursor shape its backing wants (a log watermark, a
/// per-origin map, a database LSN) without the seam changing. Three laws
/// every implementation honours, and the contract pack enforces:
///
///   1. `StateVector.empty` means "I have nothing" for EVERY
///      implementation — a diff against it returns the whole document.
///      It is the one vector a caller may construct.
///   2. A diff against the vector a store just returned is empty.
///   3. Appending the same set of updates in any order yields the same
///      vector (the convergence property — see
///      `ICrdtDocumentStore`).
///
/// An unrecognised vector (one issued by a different store, or by the
/// same store before a restart) is never an error: the store falls back
/// to returning the whole document. Re-applying an update a client
/// already holds is free — CRDT updates are idempotent — so the safe
/// direction is always "send more".
type StateVector = { Bytes: byte[] }

module StateVector =
    /// "I have nothing." The only vector a caller may construct; every
    /// implementation treats it as covering no update at all.
    let empty: StateVector = { Bytes = [||] }

    /// Wrap bytes a store issued. Callers echo a vector back; they do
    /// not mint one.
    let ofBytes (bytes: byte[]) : StateVector = { Bytes = bytes }

    /// `true` for the "I have nothing" vector. A null payload (an
    /// additive-field deserialisation of a record persisted before the
    /// field existed) reads as empty rather than throwing.
    let isEmpty (vector: StateVector) : bool =
        isNull (box vector.Bytes) || Array.isEmpty vector.Bytes

/// One appended update. `Payload` is the opaque, commutative CRDT update
/// encoding the SDK never interprets; `OriginSession` names the client
/// session that produced it (so a relayed update can be recognised and
/// dropped by its own author); `Sequence` is monotonic **within
/// `Ref`** — assigned by the store, never by the client, and carrying no
/// cross-document meaning whatsoever (GP 12 rule 5).
type CrdtUpdate = {
    Ref: CrdtDocRef
    Payload: byte[]
    OriginSession: string
    Sequence: int64
    AppendedAt: DateTime
}

/// A document as it currently stands: the updates which, applied **in
/// any order** to an empty CRDT document, reconstruct it, plus the
/// vector covering them. There is no single "state blob" because the SDK
/// cannot merge opaque payloads — a merged base only exists after a
/// client-attested `Compact`, and then it is simply the first update in
/// this list.
type CrdtSnapshot = {
    Ref: CrdtDocRef
    Updates: CrdtUpdate list
    Vector: StateVector
}

/// The kind of log change a co-editing event describes. `Compacted`
/// carries the merged base an editor folded the covered prefix into —
/// a joiner treats it exactly like any other update.
[<RequireQualifiedAccess>]
type CrdtChange =
    | Appended
    | Compacted

/// Payload published on the reserved `_platform.crdt` notification key
/// when an update is appended or a prefix compacted. Scope-gated exactly
/// like `PresenceEvent` / `LockEvent`: published on the document ref's
/// own `Scope`, so another team never sees it (GP 4).
///
/// A co-editor drops any event whose `Update.OriginSession` is its own —
/// that is the echo suppression, and it is why the origin travels with
/// the update rather than being a transport concern.
type CrdtUpdateEvent = {
    Change: CrdtChange
    Update: CrdtUpdate
}

/// Reserved `CustomNotification` key for co-editing fan-out. Lives in
/// the `_platform.*` reserved namespace alongside
/// `CollaborationTopics.Presence` / `.Lock` (Phase 442) so a module's own
/// keys never collide.
///
/// A separate module rather than a case added to `CollaborationTopics`
/// only because that module belongs to Phase 442's file; the namespace
/// convention and the scope-gating rule are identical, and the two
/// should be read together.
module CrdtTopics =
    [<Literal>]
    let Update = "_platform.crdt"

/// Constants shared by the store implementation, its contract pack, and
/// the client sync layer (GP 10) so the three cannot drift.
module CrdtDocument =
    /// The `OriginSession` a store stamps on the merged base an editor
    /// hands to `Compact`. Reserved: a client session must never use it,
    /// and a co-editor never suppresses an update bearing it (the base
    /// is content it may well be missing, whoever computed it).
    [<Literal>]
    let CompactionOrigin = "_platform.compaction"

/// Projecting a co-editing position into the Phase 442 presence
/// location descriptor — the reason this substrate ships no awareness
/// channel of its own.
///
/// Cursors and selections are ephemeral and per-participant: putting
/// them in the durable update log would grow it without bound for data
/// that is worthless a second later. `IPresenceTracker` already carries
/// exactly that shape, fans out on `_platform.presence`, and expires a
/// peer that stops beating — so a co-editing client calls
/// `IPresenceTracker.Move` (or the platform `IPresenceApi.Heartbeat`)
/// with the location below, and the roster view renders who is where
/// inside the document.
module CrdtAwareness =
    /// The presence location for a participant editing `ref`, optionally
    /// at a finer position within it (a field id, a paragraph, an
    /// encoded selection range — the module owns the meaning; the SDK
    /// never interprets it).
    ///
    /// `Module` stays the module/route id so an existing presence view
    /// keeps grouping by module; the document and position ride `Page`,
    /// which Phase 442 already documents as module-owned.
    let location (moduleId: string) (ref: CrdtDocRef) (position: string option) : PresenceLocation = {
        Module = moduleId
        Page =
            match position with
            | Some p -> Some(sprintf "%s#%s" ref.DocId p)
            | None -> Some ref.DocId
    }

/// Phase 756 — how aggressively the blob-backed store folds its loose
/// per-update log blobs into one snapshot blob.
///
/// **This is a read-amplification knob, not a retention policy, and the
/// distinction is the whole reason it is separate from `Compact`.** A
/// fold rewrites the SAME updates into one blob and deletes the loose
/// ones: byte count is essentially unchanged, blob count falls, and no
/// client's converged state can move because no payload is touched.
/// Reducing *bytes* needs a merged base only a participant holding the
/// document can compute — that is `ICrdtDocumentStore.Compact`, and it
/// stays client-attested for exactly the reason stated there.
///
/// Portability (GP 12): a plain record of primitives, so a distributed
/// implementation may honour, widen or ignore it without the
/// composition surface changing shape.
type CrdtSnapshotPolicy = {
    /// Fold once this many un-folded update blobs have accumulated for
    /// one document. `0` or less disables folding entirely — every
    /// update stays a loose blob, which is the cheapest write path and
    /// the most expensive read path.
    ///
    /// The threshold is per document (the shard key), never estate-wide:
    /// a busy document folds often and an idle one never does, with no
    /// shared counter between them (GP 12 rule 5).
    SnapshotThreshold: int
}

module CrdtSnapshotPolicy =
    /// Fold every 64 loose updates. Chosen so a document's cold read is
    /// a small constant number of blob fetches under ordinary editing
    /// traffic while a fold stays cheap enough to ride the append that
    /// crosses the threshold rather than needing a background sweep.
    let defaults: CrdtSnapshotPolicy = { SnapshotThreshold = 64 }

/// Selects whether `compose` registers the CRDT co-editing document
/// store (Phase 535). Default `NoCrdtDocuments` — no
/// `ICrdtDocumentStore` in DI, no allocation, no `_platform.crdt`
/// fan-out; an existing deployment that upgrades stays byte-for-byte
/// identical until it opts in (GP 11 + GP 13). Mirrors `PresenceMode` /
/// `PeerSubstrateMode` (binary, opt-in).
type CrdtDocumentMode =
    /// No co-editing substrate registered. The default.
    | NoCrdtDocuments
    /// Register the in-memory `ICrdtDocumentStore` default into DI,
    /// wrapped in the notifying relay so appends and compactions fan out
    /// on the reserved `_platform.crdt` key, scope-isolated per team.
    ///
    /// Substrate only — as with `EnabledPresence`, the SDK registers the
    /// store and a deployment exposes its own module-owned API over the
    /// resolved service. The in-memory default is **single-instance**: a
    /// multi-instance deployment supplies a distributed implementation
    /// (see the Phase 9c distributed-companion family) with no change to
    /// consuming code.
    | EnabledCrdtDocuments
    /// Phase 756 — register the **durable** `BlobCrdtDocumentStore` over
    /// the composed `IBlobStorage`, relay-wrapped exactly as
    /// `EnabledCrdtDocuments` is. Co-edited documents then survive a
    /// process restart: a client hands back the cursor it retained and
    /// catches up on exactly the tail it missed, rather than the whole
    /// document being gone.
    ///
    /// Shaped against the `EventStoreMode` precedent
    /// (`InMemoryOnly | PersistentBlobBacked of EventRetentionPolicy`):
    /// the durable arm carries its own policy record, the in-memory arm
    /// carries nothing, and the default remains the do-nothing case.
    /// Added as a THIRD case rather than by giving
    /// `EnabledCrdtDocuments` a payload, so every existing composition
    /// keeps compiling and the dev default stays the cheap one.
    ///
    /// Still **single-instance for fan-out**: the relay publishes
    /// in-process, and sequence assignment serialises on a per-document
    /// in-process gate. What this case buys is durability across a
    /// restart of ONE process, not correctness across several writing
    /// concurrently — see the file header of `BlobCrdtDocumentStore` and
    /// the Phase 9c distributed-companion family.
    | PersistentCrdtDocuments of CrdtSnapshotPolicy