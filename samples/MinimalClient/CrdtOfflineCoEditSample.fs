// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module MinimalClient.CrdtOfflineCoEditSample

open System
open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Offline
open ToolUp.Offline.Client
open ToolUp.Platform

// ─── Phase 760 worked example — co-editing through a dropped link ────
//
// `CrdtCoEditSample` is two tabs typing into one document while the
// network is up. This is the same document with the network pulled out,
// which is the scenario Phase 24's acceptance describes and Phase 535's
// spec anticipated in prose:
//
//   1. Two participants are co-editing. One goes offline mid-sentence.
//   2. They keep typing. Every local update fails to publish and is
//      HELD in the offline queue instead — durably, in IndexedDB, so it
//      survives closing the tab.
//   3. Meanwhile the other participant keeps editing, and the log moves
//      on without them.
//   4. The link returns. The coordinator catches up first (the
//      state-vector diff brings the other participant's edits in), then
//      replays the held updates.
//   5. Both documents converge. **No conflict prompt appears** — not
//      because one was suppressed, but because the payloads never
//      travelled the path that can produce one.
//
// ## What makes step 5 true rather than merely hoped for
//
// The offline queue is generic: it replays entity writes through
// `IOfflineSyncApi`, which compares base versions and reports a
// `Conflict` the user has to answer. That is right for an inspection
// record and wrong for a CRDT update, which cannot be stale because it
// does not overwrite anything.
//
// So a held CRDT update is stamped with the reserved `EntityType`
// `MergeablePayload.entityTypeFor "crdt"`, and the coordinator is
// started with a `MergeableChannel` registered under that name. On the
// drain pass the coordinator recognises the marker, skips
// `IOfflineSyncApi` entirely, and hands the payload to the channel —
// whose `Replay` is the CRDT transport's own publish.
//
// ## Why the wiring lives HERE
//
// Neither companion names the other. `ToolUp.Offline.Client` knows only
// "a mergeable channel called `crdt`" and hands it opaque bytes;
// `ToolUp.Platform.CrdtSyncClient` knows only "a `hold` function taking
// bytes". This file is the twenty lines that join them, exactly as
// `CrdtCoEditSample` is the one line that names Yjs (GP 1 / GP 10).
//
// The consequence is the one worth checking: a deployment on
// `NoCrdtDocuments` composes the offline companion unchanged, a
// deployment on `NoOffline` composes the CRDT pump unchanged, and
// neither pays anything for the other's existence (GP 13).

/// A client-minted, monotonic revision for the held updates. Per tab,
/// which is all the queue's ordering contract promises anyway — and
/// mergeable payloads converge in any order regardless, so this exists
/// for the queue's bookkeeping rather than for correctness.
let private nextRevision =
    let mutable revision = 0

    fun () ->
        revision <- revision + 1
        revision

/// Hold one un-publishable CRDT update in the offline queue.
///
/// `scopeId` is the queue's own bookkeeping field and never reaches the
/// CRDT store — the server resolves the scope from the authenticated
/// request, exactly as `CrdtCoEditSample.transportFor` explains (GP 4).
let holdInQueue (queue: OfflineQueue.IOfflineQueue) (docId: string) (scopeId: string) (payload: byte[]) : Async<unit> =
    queue.Enqueue(
        MergeablePayload.mint
            (Guid.NewGuid().ToString "N")
            "crdt"
            docId
            scopeId
            DateTimeOffset.UtcNow
            (nextRevision ())
            payload
    )

/// Everything one offline-tolerant co-editing session needs, wired.
type OfflineCoEdit = {
    /// The CRDT session. `Session.Resync` re-anchors on demand; the
    /// relay subscription below already calls it on every signal.
    Session: CrdtSyncClient.CrdtSession
    /// The drain coordinator. Its `Stop` must be called alongside the
    /// session's teardown, or a re-mount leaves two coordinators
    /// draining one queue — which is what `Dispose` below exists to
    /// make impossible to get wrong.
    Coordinator: SyncCoordinator.Coordinator
    /// Tear down all three lifetimes this record holds — the relay
    /// subscription, the coordinator, and the pump — in the order that
    /// leaves nothing draining a queue nobody is reading (Phase 764).
    Dispose: unit -> unit
}

/// Join `doc` to the co-editing log with the offline queue behind it.
///
/// The three joins, in the order they matter:
///
///  * `holdAndForward` wraps the transport so a failed publish is held
///    rather than lost;
///  * `holdInQueue` is what "held" means — a durable queue entry under
///    the reserved mergeable marker;
///  * the `MergeableChannel` is how the coordinator gets it back out,
///    catching up before it replays.
let start
    (yjs: CrdtSyncClient.IYjs)
    (doc: CrdtSyncClient.IYDoc)
    (transport: CrdtSyncClient.CrdtTransport)
    (api: OfflineSyncApi.IOfflineSyncApi)
    (config: OfflineConfig)
    (queue: OfflineQueue.IOfflineQueue)
    (docId: string)
    (scopeId: string)
    (sessionId: string)
    : OfflineCoEdit =
    let holding =
        CrdtSyncClient.holdAndForward transport (holdInQueue queue docId scopeId)

    // `startLive`, not the bare pump: offline tolerance is composed ON
    // TOP of a live co-editing surface, so this example must stay live
    // for exactly the reason `CrdtCoEditSample` does (Phase 764). The
    // subscription is the same one, over the same per-tab stream.
    let live = CrdtCoEditSample.startLive yjs doc holding.Transport docId sessionId
    let session = live.Session

    // `CatchUp` before `Replay` is the reconnect ordering the phase
    // asks for, and the coordinator enforces it: it runs the channel's
    // catch-up at the head of the pass, then flushes that channel's
    // backlog, and only then drains the ordinary entity mutations. A
    // held update therefore lands on a document that already holds
    // everything the other participant did while this tab was dark.
    let crdtChannel: SyncCoordinator.MergeableChannel = {
        Channel = "crdt"
        CatchUp = session.Resync
        Replay = holding.Replay
    }

    let coordinator = SyncCoordinator.startRouted [ crdtChannel ] queue api config

    {
        Session = session
        Coordinator = coordinator
        Dispose =
            fun () ->
                // The coordinator first: it is the only one of the three
                // that can still call into the session (its `CatchUp` is
                // `session.Resync`), so stopping it first means the
                // teardown never races a drain pass.
                coordinator.Stop()
                live.Dispose()
    }

/// The Phase 535 text area, with the link allowed to drop.
///
/// Identical to `CrdtCoEditSample.SharedTextArea` but for the two lines
/// that build the queue and start the routed coordinator — which is the
/// claim this example exists to make. Offline tolerance is composed on
/// top of a co-editing surface; it is not a different co-editing
/// surface.
[<ReactComponent>]
let OfflineSharedTextArea
    (api: CrdtCoEditSample.CoEditApi)
    (syncApi: OfflineSyncApi.IOfflineSyncApi)
    (docId: string)
    (scopeId: string)
    (sessionId: string)
    : ReactElement =
    let text, setText = React.useState ""
    let ytextHandle = React.useRef (None: obj option)

    React.useEffectOnce (fun () ->
        let ydoc: obj = createNew CrdtCoEditSample.YDocCtor ()
        let ytext = CrdtCoEditSample.getText ydoc "content"
        ytextHandle.current <- Some ytext

        CrdtCoEditSample.observeText ytext (fun () -> setText (CrdtCoEditSample.textValue ytext))

        // Per-deployment database name, per the queue's own guidance —
        // two apps on one origin must not share a queue.
        let queue = OfflineQueue.create "minimal-client-offline"

        let joined =
            start
                CrdtCoEditSample.yjs
                (unbox<CrdtSyncClient.IYDoc> ydoc)
                (CrdtCoEditSample.transportFor api docId sessionId)
                syncApi
                OfflineConfig.defaults
                queue
                docId
                scopeId
                sessionId

        let cleanup: unit -> unit =
            fun () ->
                joined.Dispose()
                ytextHandle.current <- None

        cleanup)

    Html.div [
        Html.label [ prop.htmlFor "coedit-offline"; prop.text "Shared notes (offline-tolerant)" ]
        Html.textarea [
            prop.id "coedit-offline"
            prop.value text
            prop.rows 6
            prop.onChange (fun (next: string) ->
                match ytextHandle.current with
                | Some ytext -> CrdtCoEditSample.applyLocalEdit ytext (CrdtCoEditSample.textValue ytext) next
                | None -> ())
        ]
    ]