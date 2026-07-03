// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Threading
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 271 — neutral tree-patch transport envelope ─────────────────
//
// Phase 112's `ILiveChannel` pushes OPAQUE frames over scope-isolated
// SSE, and Phase 264 ships the read-side, but there is no neutral way to
// stream STRUCTURAL diffs (insert / remove / move / update) *reliably*: a
// consumer that wants ordered incremental updates must hand-roll
// sequencing, gap detection, and resync on top of the opaque-frame
// transport. This file layers that reliability contract over Phase 112 —
// a `TreePatchEnvelope` (monotonic sequence + diff base) and an
// `ITreePatchChannel` (push / ack / resync-on-gap) — so any external tree
// language streams ordered incremental patches without re-implementing
// the plumbing.
//
// **The patch VOCABULARY is not forge's (GP 1).** The envelope carries an
// opaque `Payload: string` — the tree language owns the diff wire format
// (insert/remove/move/update ops); the transport owns only the envelope
// around it. No tree-language type appears here, so the contract is
// forge-public and grep-clean.
//
// **Scope isolation is inherited, not re-checked (GP 4).** A
// `TreePatchChannel` wraps ONE Phase 112 `ILiveChannel`, which is already
// resolved within a single `(scopeId, sessionId)` partition by
// `ILiveSessionHost.TryGetChannel`. A patch pushed through it reaches only
// that session's subscribers — a frame addressed to scope A can never
// reach scope B because the wrapped channel was resolved in scope A's
// partition. There is no cross-scope path to forget to filter.
//
// **Six portability rules (GP 12):**
//   1. Identity by value — sequences are `int64`, payloads `string`; no
//      live handle crosses `ITreePatchChannel`.
//   2. Async at every boundary — `Push` / `Ack` / `RequestResync` all
//      return `Async<_>`.
//   3. Retry / supervision as data — a gap is reported as a
//      `TreePatchReceipt.Gap` value (expected vs got), not a callback; the
//      recovery (`RequestResync`) is an ordinary async call.
//   4. Stateless handlers between invocations — the client's gap detector
//      (`TreePatchReceiver.classify`) is a PURE function: the caller
//      threads its last-accepted sequence in and gets the decision out, so
//      a restarted grain / re-hydrated actor resumes from the durable
//      watermark rather than from in-memory receiver state.
//   5. Ordering guaranteed only within one session shard — sequence
//      monotonicity is per-channel (per session); NO cross-session
//      ordering is promised, exactly as Phase 112's frame transport.
//   6. Precision — N/A (no scheduling primitive on this interface).
//
// **In-process default + distributed slot.** `InMemoryTreePatchChannel`
// holds the per-session sequence counter + ack watermark in-process, the
// same dev / single-node posture as `InMemoryLiveSessionHost`. A
// distributed implementation behind `ITreePatchChannel` owns a durable
// per-session sequence and a retained-patch buffer trimmed to the acked
// watermark (so a reconnecting client resyncs from the last durable
// snapshot); it satisfies the same contract pack
// (`ITreePatchChannelContract`).

/// One incremental patch over a live session's tree, carrying the
/// reliability metadata a consumer needs to detect a gap and request a
/// resync WITHOUT hand-rolling sequencing. Identity-by-value (GP 12
/// rule 1): a plain record of an `int64` sequence, an optional `int64`
/// diff base, and an opaque `string` payload.
type TreePatchEnvelope = {
    /// Monotonic per-session sequence number, strictly increasing by 1 for
    /// consecutive envelopes on one channel. A client detects a gap when a
    /// received `Seq` is not its last-accepted `Seq + 1`.
    Seq: int64
    /// The `Seq` this patch diffs against — `Some prev` for an incremental
    /// diff (the first patch diffs against the initial empty state, `Some
    /// 0`), `None` for a full SNAPSHOT (a resync reply that re-bases the
    /// stream). `BaseSeq = None` is the sole discriminant of a snapshot.
    BaseSeq: int64 option
    /// The opaque patch payload — the tree language's own diff wire format.
    /// Forge never interprets it beyond framing it onto the transport (GP 1).
    Payload: string
}

[<RequireQualifiedAccess>]
module TreePatchEnvelope =

    /// STJ options with the full F# converter set (`Option` / DU / record)
    /// — the canonical non-Remoting JSON wire (matches the `Fable.SimpleJson`
    /// shape a browser client decodes). Built once at module load.
    let private wireOptions = FableConverters.create ()

    /// True when the envelope is a full-state SNAPSHOT (a resync reply or a
    /// re-base) rather than an incremental diff — i.e. it carries no
    /// `BaseSeq`.
    let isSnapshot (env: TreePatchEnvelope) : bool = Option.isNone env.BaseSeq

    /// Frame an envelope onto the `ILiveChannel` wire — a JSON string the
    /// opaque-frame transport carries verbatim. The `Payload` is embedded
    /// as-is (an opaque string), so a payload containing any delimiter is
    /// safe.
    let encode (env: TreePatchEnvelope) : string =
        JsonSerializer.Serialize(env, wireOptions)

    /// Decode a wire frame back into an envelope. Raises on a malformed
    /// frame (a transport corruption is not a routine gap — it surfaces as
    /// a decode error, not a silent drop).
    let decode (frame: string) : TreePatchEnvelope =
        JsonSerializer.Deserialize<TreePatchEnvelope>(frame, wireOptions)

/// The client's decision after classifying an incoming envelope against
/// the last sequence it accepted. A value, not a callback (GP 12 rule 3):
/// the caller acts on it (advance / ignore / resync).
[<RequireQualifiedAccess>]
type TreePatchReceipt =
    /// In order (or a snapshot re-base): apply the patch and advance the
    /// client's last-accepted sequence to `newLastSeq`.
    | Accept of newLastSeq: int64
    /// Already seen (`Seq` at or below the last accepted): ignore
    /// idempotently — a redelivery after reconnect is not an error.
    | Duplicate
    /// A sequence gap: the client expected `expected` but received `got`
    /// (higher). One or more envelopes were lost; the client calls
    /// `ITreePatchChannel.RequestResync` and resumes from the snapshot.
    | Gap of expected: int64 * got: int64

[<RequireQualifiedAccess>]
module TreePatchReceiver =

    /// Pure gap-detector (GP 12 rule 4 — stateless). Given the last
    /// in-order `Seq` the client accepted (`0L` before any frame) and an
    /// incoming envelope, decide what to do. A SNAPSHOT is always accepted
    /// — it re-bases the stream (the resync reply or the first frame), so
    /// it can never be a gap. An incremental envelope is accepted only when
    /// its `Seq` is exactly `lastSeen + 1`; a lower `Seq` is a `Duplicate`,
    /// a higher one is a `Gap`.
    let classify (lastSeen: int64) (env: TreePatchEnvelope) : TreePatchReceipt =
        if TreePatchEnvelope.isSnapshot env then
            TreePatchReceipt.Accept env.Seq
        elif env.Seq <= lastSeen then
            TreePatchReceipt.Duplicate
        elif env.Seq = lastSeen + 1L then
            TreePatchReceipt.Accept env.Seq
        else
            TreePatchReceipt.Gap(lastSeen + 1L, env.Seq)

/// Reliable incremental-patch channel for ONE live session, layered over
/// a Phase 112 `ILiveChannel`. Scope isolation is inherited from the
/// wrapped channel (resolved within a single scope partition), so every
/// operation here is already scope-bound (GP 4).
type ITreePatchChannel =
    /// Push one incremental patch. Assigns the next monotonic `Seq`
    /// (`BaseSeq = Some previousSeq`), frames it onto the underlying
    /// `ILiveChannel`, and returns the envelope pushed.
    abstract Push: payload: string -> Async<TreePatchEnvelope>

    /// The client acknowledges receipt through `seq`. Advances the host's
    /// acked-through watermark (monotonic — a lower `seq` is ignored); a
    /// distributed implementation trims its retained-patch buffer to this
    /// point.
    abstract Ack: seq: int64 -> Async<unit>

    /// The client detected a gap and requests a full-state resync. The host
    /// produces a fresh SNAPSHOT envelope (`BaseSeq = None`) from the
    /// snapshot source, assigns the next `Seq`, frames + pushes it, and
    /// resumes incremental delivery from there. Returns the snapshot pushed.
    abstract RequestResync: unit -> Async<TreePatchEnvelope>

    /// The highest `Seq` pushed so far (diagnostics / cap accounting).
    abstract LastSeq: int64

    /// The client-acknowledged watermark (`0L` until the first `Ack`).
    abstract AckedThrough: int64

/// In-process, single-instance default. **Dev / single-node only** in the
/// same sense as `InMemoryLiveSessionHost`: the sequence counter + ack
/// watermark are per-process, so a multi-node deployment needs a
/// distributed implementation behind the same interface (validated by the
/// `ITreePatchChannelContract` pack). `snapshot` yields the current
/// full-tree payload for a resync — the host holds the authoritative tree
/// and the tree language serialises it.
type InMemoryTreePatchChannel(live: ILiveChannel, snapshot: unit -> string) =

    // Per-session state the HOST owns (not a handler — GP 12 rule 4 is
    // about handlers holding nothing between calls). `seq` guards
    // monotonicity across concurrent `Push` / `RequestResync`; `acked`
    // tracks the client watermark.
    let mutable seq = 0L
    let mutable acked = 0L
    let ackLock = obj ()

    // Assign the next sequence atomically and frame `env` onto the wire.
    let pushEnvelope (baseSeq: int64 option) (payload: string) = async {
        let next = Interlocked.Increment(&seq)

        let env = {
            Seq = next
            BaseSeq = baseSeq
            Payload = payload
        }

        do! live.PushFrame(TreePatchEnvelope.encode env)
        return env
    }

    interface ITreePatchChannel with
        member _.Push payload =
            // Incremental: diff against the previously-assigned sequence.
            // The first push (seq 0 → 1) bases on `Some 0` — the initial
            // empty state — so a fresh client (lastSeen 0) accepts it as
            // `0 + 1`.
            let baseSeq = Some(Interlocked.Read(&seq))
            pushEnvelope baseSeq payload

        member _.Ack s = async {
            lock ackLock (fun () ->
                if s > acked then
                    acked <- s)
        }

        member _.RequestResync() =
            // Snapshot: re-base the stream. `BaseSeq = None` marks it a
            // snapshot; the client accepts it unconditionally and resumes.
            pushEnvelope None (snapshot ())

        member _.LastSeq = Interlocked.Read(&seq)
        member _.AckedThrough = lock ackLock (fun () -> acked)

[<RequireQualifiedAccess>]
module TreePatchChannel =

    /// Wrap a resolved Phase 112 `ILiveChannel` in the in-process reliable
    /// patch channel. `snapshot` yields the current full-tree payload for a
    /// resync reply.
    let createInMemory (live: ILiveChannel) (snapshot: unit -> string) : ITreePatchChannel =
        InMemoryTreePatchChannel(live, snapshot) :> ITreePatchChannel

    /// Resolve a session's push channel WITHIN `scopeId` (the structural
    /// cross-scope denial is Phase 112's — `TryGetChannel` returns `None`
    /// for a session that does not live in `scopeId`) and wrap it in a
    /// reliable patch channel. `None` when the session does not exist in
    /// that scope, so a caller cannot obtain a patch channel across scopes.
    let forSession
        (host: ILiveSessionHost)
        (scopeId: string)
        (sessionId: string)
        (snapshot: unit -> string)
        : Async<ITreePatchChannel option> =
        async {
            match! host.TryGetChannel(scopeId, sessionId) with
            | Some live -> return Some(createInMemory live snapshot)
            | None -> return None
        }