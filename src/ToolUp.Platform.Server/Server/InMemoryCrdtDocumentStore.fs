// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── In-memory co-editing store + fan-out relay — Phase 535 ──────────
//
// Two types, deliberately separate:
//
//   * `InMemoryCrdtDocumentStore` — the single-instance default log.
//   * `NotifyingCrdtDocumentStore` — a DECORATOR that fans appends and
//     compactions out over `INotificationChannel`.
//
// The split is what makes the relay reusable. Phase 442's presence and
// lock defaults each fold their own publish call into the in-memory
// implementation, so a distributed replacement has to re-implement
// fan-out to stay behaviourally equivalent. A decorator instead gives
// EVERY implementation of the seam the same fan-out for free — the
// `NotifyingNarrativeStore` shape — which matters more here because the
// in-memory store is explicitly the one implementation a real
// multi-instance deployment will replace.

/// Dev / single-instance `ICrdtDocumentStore`. Holds update logs in
/// process memory — correct for a single node, NOT shared across
/// replicas, and NOT durable across a restart. A multi-instance or
/// production deployment supplies an implementation over a real log
/// (append-only blob, database table, event store) with no change to
/// consuming code; this default is flagged single-instance to the Phase
/// 9c distributed-companion family.
///
/// **Cursor encoding is this implementation's own business.**
/// `StateVector` is opaque by contract, so what these bytes mean is not
/// part of the seam: here they are the UTF-8 decimal form of the
/// per-document sequence watermark, which is the cheapest cursor that
/// satisfies the three laws in `StateVector`'s doc comment. A
/// database-backed implementation would put its own LSN there and a
/// client could not tell the difference. What every implementation MUST
/// honour is `StateVector.empty` meaning "send everything", and an
/// unrecognised vector degrading to the same rather than failing.
///
/// `now` is injectable for deterministic tests. Mutations serialise on
/// the per-document log, so concurrent appends to one document each get
/// a distinct sequence and appends to different documents never contend.
type InMemoryCrdtDocumentStore(?now: unit -> DateTime) =
    let clock = defaultArg now (fun () -> DateTime.UtcNow)

    // One log per ref. The ref carries the scope, so two scopes naming
    // the same DocId are structurally distinct keys — a cross-tenant
    // read is an impossible lookup, not a filtered one (GP 4).
    let logs = ConcurrentDictionary<CrdtDocRef, ResizeArray<CrdtUpdate>>()

    // Highest sequence assigned per ref. Held beside the log rather than
    // derived from it so a compaction (which removes the covered prefix
    // and re-uses the covered watermark for the base) cannot rewind the
    // counter and hand out a sequence twice.
    let watermarks = ConcurrentDictionary<CrdtDocRef, int64>()

    let logFor (ref: CrdtDocRef) =
        logs.GetOrAdd(ref, fun _ -> ResizeArray<CrdtUpdate>())

    let watermarkOf (ref: CrdtDocRef) =
        match watermarks.TryGetValue ref with
        | true, w -> w
        | _ -> 0L

    /// Watermark -> opaque cursor. Zero (an untouched document) encodes
    /// as `StateVector.empty`, so law 1 holds without a special case at
    /// the read site.
    let encode (watermark: int64) : StateVector =
        if watermark <= 0L then
            StateVector.empty
        else
            {
                Bytes = Encoding.UTF8.GetBytes(string watermark)
            }

    /// Opaque cursor -> watermark. `None` for a vector this store did
    /// not issue (a different implementation's cursor, or one from
    /// before a restart), which the callers below turn into "send
    /// everything".
    let decode (vector: StateVector) : int64 option =
        if StateVector.isEmpty vector then
            Some 0L
        else
            try
                match Int64.TryParse(Encoding.UTF8.GetString vector.Bytes) with
                | true, w when w >= 0L -> Some w
                | _ -> None
            with _ ->
                // Non-UTF-8 bytes — a foreign cursor, not an error.
                None

    interface ICrdtDocumentStore with
        member _.Append(ref, payload, originSession) = async {
            let log = logFor ref

            return
                lock log (fun () ->
                    let sequence = watermarkOf ref + 1L
                    watermarks[ref] <- sequence

                    let update = {
                        Ref = ref
                        Payload = payload
                        OriginSession = originSession
                        Sequence = sequence
                        AppendedAt = clock ()
                    }

                    log.Add update
                    update)
        }

        member _.GetStateVector(ref) = async { return encode (watermarkOf ref) }

        member _.GetDiff(ref, since) = async {
            let log = logFor ref

            return
                lock log (fun () ->
                    match decode since with
                    | Some watermark -> log |> Seq.filter (fun u -> u.Sequence > watermark) |> List.ofSeq
                    | None ->
                        // Unrecognised cursor — send the whole document.
                        // Re-applying a held update is free; losing one
                        // is not.
                        List.ofSeq log)
        }

        member _.Snapshot(ref) = async {
            let log = logFor ref

            return
                lock log (fun () -> {
                    Ref = ref
                    Updates = List.ofSeq log
                    Vector = encode (watermarkOf ref)
                })
        }

        member _.Compact(ref, merged, covers) = async {
            let covered =
                match decode covers with
                | Some w when w > 0L -> w
                | Some _ ->
                    invalidArg
                        "covers"
                        "Compact requires a state vector this store issued; StateVector.empty covers nothing and would append a second copy of the document rather than compact it."
                | None ->
                    invalidArg
                        "covers"
                        "Compact requires a state vector this store issued; the supplied vector is not one this store can interpret."

            let log = logFor ref

            return
                lock log (fun () ->
                    let tail = log |> Seq.filter (fun u -> u.Sequence > covered) |> List.ofSeq

                    // The merged base takes the slot of the last covered
                    // update, so it sorts ahead of the surviving tail and
                    // a client already past `covered` is not re-sent it.
                    let baseUpdate = {
                        Ref = ref
                        Payload = merged
                        OriginSession = CrdtDocument.CompactionOrigin
                        Sequence = covered
                        AppendedAt = clock ()
                    }

                    log.Clear()
                    log.Add baseUpdate
                    log.AddRange tail

                    {
                        Ref = ref
                        Updates = List.ofSeq log
                        Vector = encode (watermarkOf ref)
                    })
        }

/// Fan-out relay over any `ICrdtDocumentStore`. Publishes a
/// `CrdtUpdateEvent` on the reserved `_platform.crdt` key after every
/// append and compaction, on the document ref's OWN scope — so the
/// channel's structural scope-gating keeps co-editing traffic inside one
/// team with no post-hoc filter (GP 4). No new transport: this is the
/// shipped `INotificationChannel` (Phase 6a) every other server-driven
/// event rides.
///
/// **Publish failure is swallowed, and that is a design decision rather
/// than laziness.** The update is already durable in the inner store by
/// the time the publish is attempted, and every co-editor recovers a
/// missed event on its next `GetDiff` from the vector it retained. A
/// failed fan-out therefore costs latency, never content — so failing
/// the caller's `Append` (or retrying it) would trade a guaranteed
/// recovery for a spurious error. This is the same posture Phase 442's
/// presence and lock events take, for the same reason.
///
/// Reads pass straight through — the decorator adds no caching, so it
/// cannot go stale against the store it wraps.
type NotifyingCrdtDocumentStore(inner: ICrdtDocumentStore, channel: INotificationChannel) =
    let jsonOptions = FableConverters.create ()

    let publish (change: CrdtChange) (update: CrdtUpdate) = async {
        try
            let event: CrdtUpdateEvent = { Change = change; Update = update }
            let payloadJson = JsonSerializer.Serialize(event, jsonOptions)
            do! channel.Publish(update.Ref.Scope, CustomNotification(CrdtTopics.Update, payloadJson))
        with _ ->
            ()
    }

    interface ICrdtDocumentStore with
        member _.Append(ref, payload, originSession) = async {
            let! update = inner.Append(ref, payload, originSession)
            do! publish CrdtChange.Appended update
            return update
        }

        member _.GetStateVector(ref) = inner.GetStateVector ref
        member _.GetDiff(ref, since) = inner.GetDiff(ref, since)
        member _.Snapshot(ref) = inner.Snapshot ref

        member _.Compact(ref, merged, covers) = async {
            let! snapshot = inner.Compact(ref, merged, covers)

            // Announce the merged base a joiner will now receive in place
            // of the compacted prefix. An implementation that chose not
            // to materialise a base (nothing in the seam requires one)
            // simply publishes nothing here.
            match
                snapshot.Updates
                |> List.tryFind (fun u -> u.OriginSession = CrdtDocument.CompactionOrigin)
            with
            | Some baseUpdate -> do! publish CrdtChange.Compacted baseUpdate
            | None -> ()

            return snapshot
        }