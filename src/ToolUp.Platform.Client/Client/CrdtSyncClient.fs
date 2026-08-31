// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.CrdtSyncClient

open System
open Fable.Core
open ToolUp.Platform

// ─── CRDT sync client — Phase 535 (client tier) ──────────────────────
//
// The client half of the co-editing substrate: the pump that joins a
// local CRDT document to the server's `ICrdtDocumentStore` log. Three
// jobs, and no more —
//
//   * **catch up** on connect / reconnect, from the retained cursor;
//   * **publish** every local update the CRDT library emits;
//   * **apply** every relayed update that is not this session's own echo.
//
// ## Why this file imports nothing
//
// Yjs is MIT and free (GP 2), but a `let yjs = importAll "yjs"` HERE
// would put a `import … from "yjs"` statement into every consumer of
// `ToolUp.Platform.Client` — including the overwhelming majority that
// never co-edit anything. That is precisely the cost GP 13 forbids: an
// npm dependency, and bundle weight, for a feature a deployment did not
// opt into.
//
// So the vendor surface is a **parameter**. `IYjs` below is a typed
// binding over the Yjs module namespace object, and the consuming app —
// which owns the npm dependency, per GP 1 — supplies it with a one-line
// `importAll "yjs"`. Nothing in the SDK names the package, so a
// deployment that swaps Yjs for another update-encoding CRDT
// implements the same three functions over its own library and this
// pump is unchanged. `samples/MinimalClient/CrdtCoEditSample.fs` is the
// reference wiring.
//
// ## What the cursor is, and what it is not
//
// `StateVector` is opaque (see its doc comment in the Core tier). This
// pump retains whatever the store last issued and hands it straight
// back — it never parses one, so it works against any implementation of
// the seam.
//
// **The cursor advances on `Resync`, not on a live update**, because a
// relayed `CrdtUpdate` carries no cursor and an opaque value cannot be
// advanced by inference. The consequence is benign and worth stating so
// nobody "fixes" it: an update applied live may be delivered again by
// the next `Resync`, and re-applying an update a CRDT document already
// holds is a no-op. The alternative — inferring cursor positions
// client-side — would couple this file to one implementation's cursor
// encoding to save a few duplicated bytes.
//
// ## Awareness is elsewhere
//
// Cursors and selections do NOT ride this log (Phase 535.B). They are
// ephemeral and belong on the Phase 442 presence location descriptor —
// see `CrdtAwareness.location` in the Core tier, which a co-editing view
// hands to `IPresenceApi.Heartbeat` / `IPresenceTracker.Move`.

[<Emit("$0 === $1")>]
let private isSameReference (a: obj) (b: obj) : bool = jsNative

/// A CRDT document instance (a Yjs `Y.Doc`). Opaque to the SDK: the pump
/// never reads its content, only subscribes to its update stream and
/// feeds remote updates into it.
///
/// `on` / `off` take the same handler value to subscribe and detach, so
/// hold the `Action` you passed — see `start`, which does.
type IYDoc =
    /// Subscribe to the document's update stream. The handler receives
    /// the opaque update encoding and the `origin` the mutation was
    /// applied with (the pump's own sentinel for a relayed update).
    abstract on: event: string * handler: Action<byte[], obj> -> unit
    /// Detach a handler previously passed to `on`.
    abstract off: event: string * handler: Action<byte[], obj> -> unit

/// The CRDT library surface this pump uses — the Yjs module namespace
/// object, or any library exposing the same four functions.
///
/// Supplied by the consuming app (`let yjs: IYjs = importAll "yjs"`), so
/// the SDK emits no import for a package a deployment may not use.
type IYjs =
    /// Apply an opaque update to `doc`, tagging the mutation with
    /// `origin` so the document's own update handler can tell a relayed
    /// change from a local one.
    abstract applyUpdate: doc: IYDoc * update: byte[] * origin: obj -> unit
    /// The whole document encoded as a single update — the merged base
    /// handed to `ICrdtDocumentStore.Compact`.
    abstract encodeStateAsUpdate: doc: IYDoc -> byte[]
    /// The document's own CRDT-level state vector. Distinct from the
    /// SDK's opaque log cursor and not interchangeable with it; exposed
    /// for deployments doing a document-level handshake alongside the
    /// log catch-up.
    abstract encodeStateVector: doc: IYDoc -> byte[]
    /// Merge several opaque updates into one, without a document.
    abstract mergeUpdates: updates: byte[][] -> byte[]

/// How the pump reaches the server. Wire each to the deployment's own
/// module-owned API over the resolved `ICrdtDocumentStore` — the SDK
/// registers the substrate and mounts no route for it, exactly as Phase
/// 442's presence substrate was consumed before a platform API existed.
type CrdtTransport = {
    /// Append one local update to the document's log.
    Publish: byte[] -> Async<unit>
    /// Everything the store holds that the supplied cursor does not
    /// cover, with the cursor now covering it — i.e.
    /// `ICrdtDocumentStore.GetDiff` paired with `GetStateVector`.
    FetchDiff: StateVector -> Async<CrdtUpdate list * StateVector>
}

/// A live co-editing session over one document.
type CrdtSession = {
    /// Apply one relayed `CrdtUpdateEvent` payload. Drops this session's
    /// own echo by `OriginSession`; a compaction base (which carries the
    /// reserved `CrdtDocument.CompactionOrigin`) is never dropped,
    /// whoever computed it.
    ApplyRemote: CrdtUpdate -> unit
    /// Re-run catch-up from the retained cursor. Safe to call at any
    /// time — on reconnect, after a visibility change, or periodically
    /// to re-anchor the cursor after live updates.
    Resync: unit -> Async<unit>
    /// The cursor retained so far. Persist it to resume across a page
    /// reload; an unrecognised cursor costs a full re-read, never
    /// correctness.
    Cursor: unit -> StateVector
    /// Detach the local-update observer. Idempotent.
    Dispose: unit -> unit
}

/// The sentinel `origin` the pump applies relayed updates with, so the
/// document's update handler can skip re-publishing them. A distinct
/// object identity, compared by reference — never a string a consumer
/// could collide with.
let private remoteOrigin: obj = box (obj ())

/// Start co-editing `doc` against `transport`.
///
/// Performs the catch-up read immediately (so the document populates
/// without waiting for a first live event), then publishes every local
/// update. `sessionId` identifies this client session for echo
/// suppression and is the value handed to
/// `ICrdtDocumentStore.Append`'s `originSession`; make it unique per
/// browser tab, not per user.
///
/// Returns the session; call `Dispose` from the owning component's
/// teardown.
let start (yjs: IYjs) (doc: IYDoc) (transport: CrdtTransport) (sessionId: string) : CrdtSession =
    let cursor = ref StateVector.empty
    let disposed = ref false

    let applyRemotePayload (payload: byte[]) =
        yjs.applyUpdate (doc, payload, remoteOrigin)

    let resync () = async {
        let! updates, vector = transport.FetchDiff cursor.Value
        updates |> List.iter (fun u -> applyRemotePayload u.Payload)
        cursor.Value <- vector
    }

    // Local updates go up; relayed ones (applied with the sentinel
    // origin above) do not, or every edit would round-trip forever.
    let handler =
        Action<byte[], obj>(fun update origin ->
            if not (isSameReference origin remoteOrigin) then
                Async.StartImmediate(transport.Publish update))

    doc.on ("update", handler)

    // Immediate catch-up so the document populates on join rather than
    // at the first remote edit.
    Async.StartImmediate(resync ())

    {
        ApplyRemote =
            fun update ->
                if update.OriginSession <> sessionId then
                    applyRemotePayload update.Payload
        Resync = resync
        Cursor = fun () -> cursor.Value
        Dispose =
            fun () ->
                if not disposed.Value then
                    disposed.Value <- true
                    doc.off ("update", handler)
    }

/// The merged base for `ICrdtDocumentStore.Compact` — the whole document
/// as one opaque update. Pair it with the cursor the store issued at the
/// moment it was encoded (`session.Cursor()` immediately after a
/// `Resync`), so the compaction covers exactly what the base contains.
let mergedState (yjs: IYjs) (doc: IYDoc) : byte[] = yjs.encodeStateAsUpdate doc

/// Merge a set of opaque updates into one without a document — the
/// headless compaction path, for a trusted server-side or scheduled
/// participant folding a `Snapshot`'s payloads.
let mergePayloads (yjs: IYjs) (updates: CrdtUpdate list) : byte[] =
    updates |> List.map _.Payload |> Array.ofList |> yjs.mergeUpdates

/// The document's own CRDT-level state vector. Distinct from the SDK's
/// opaque log cursor — see `IYjs.encodeStateVector`.
let documentStateVector (yjs: IYjs) (doc: IYDoc) : byte[] = yjs.encodeStateVector doc