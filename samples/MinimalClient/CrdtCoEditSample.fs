// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module MinimalClient.CrdtCoEditSample

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform

// ─── Phase 535 worked example — a co-edited text area ────────────────
//
// The reference wiring for the CRDT co-editing substrate: a shared text
// area two browser tabs can type into at once, with no lock and no
// conflict. A text area rather than an editor deliberately — the point
// is the plumbing (join, catch-up, publish, apply), and a real editor
// binding would bury it.
//
// **The npm dependency lives HERE, in the consuming app.** That is the
// whole shape of the boundary: `ToolUp.Platform.Client` imports nothing
// from `yjs` (see `CrdtSyncClient`'s header — the CRDT library is a
// parameter), so a deployment that never co-edits carries no vendor
// bundle weight, and one that prefers a different update-encoding CRDT
// implements `IYjs`'s four functions over its own library and changes
// nothing else. Yjs is MIT (GP 2) and declared in this sample's
// `package.json`.
//
// The server, meanwhile, has never heard of any of this: it stores and
// relays opaque bytes (`ICrdtDocumentStore`), and builds with no npm
// dependency at all.

/// The Yjs module namespace object — the one line that names the vendor.
let yjs: CrdtSyncClient.IYjs = importAll "yjs"

/// The `Y.Doc` constructor.
let YDocCtor: obj = import "Doc" "yjs"

[<Emit("$0.getText($1)")>]
let getText (doc: obj) (name: string) : obj = jsNative

[<Emit("$0.toString()")>]
let textValue (ytext: obj) : string = jsNative

[<Emit("$0.observe($1)")>]
let observeText (ytext: obj) (handler: unit -> unit) : unit = jsNative

[<Emit("$0.delete($1, $2)")>]
let private deleteRange (ytext: obj) (index: int) (length: int) : unit = jsNative

[<Emit("$0.insert($1, $2)")>]
let private insertAt (ytext: obj) (index: int) (value: string) : unit = jsNative

/// What a deployment's own module-owned API over the resolved
/// `ICrdtDocumentStore` looks like. Phase 535 is seam-first — the SDK
/// registers the substrate and mounts no route for it, exactly as Phase
/// 442's presence substrate was consumed before a platform API existed —
/// so this record is the piece a consumer writes, and it is thin: the
/// handler resolves the scope from the authenticated request, builds the
/// `CrdtDocRef`, and forwards.
type CoEditApi = {
    /// `ICrdtDocumentStore.Append`, with the scope resolved server-side.
    Append: string * byte[] * string -> Async<unit>
    /// `ICrdtDocumentStore.GetDiff` paired with `GetStateVector`.
    Diff: string * StateVector -> Async<CrdtUpdate list * StateVector>
}

/// Bind the pump's transport to that API for one document.
///
/// Note what does NOT cross the wire: the scope. It is part of
/// `CrdtDocRef` server-side, resolved from the caller's authenticated
/// request — a client that could name a scope could name another team's
/// (GP 4), so the wire gives it nowhere to say one.
let transportFor (api: CoEditApi) (docId: string) (sessionId: string) : CrdtSyncClient.CrdtTransport = {
    Publish = fun payload -> api.Append(docId, payload, sessionId)
    FetchDiff = fun since -> api.Diff(docId, since)
}

// ─── Phase 764 — staying live after the join ─────────────────────────
//
// `CrdtSyncClient.start` catches up ONCE, at join: it reads the diff
// from an empty cursor, then publishes local edits as the CRDT library
// emits them. Nothing in the pump ever asks for a co-editor's edits
// again — so a document wired with the pump alone converges at mount
// and silently stops relaying afterwards.
//
// The piece that closes that gap lives HERE rather than inside the
// pump, for the same reason the Yjs import does: the fan-out is a
// transport, and a pump that opened its own connection would be an
// always-on service every consumer paid for, including the ones that
// never co-edit (GP 13). Instead the subscription rides the EXISTING
// per-tab `NotificationClient` stream — the one `EventSource` every
// other server-driven event already arrives on — so a co-editing page
// opens no second connection at all.

/// The two fields a relay signal is read for: which document moved, and
/// who moved it.
///
/// Read out of the payload with `JSON.parse` and two property reads
/// rather than deserialised into `CrdtUpdateEvent`, because the event's
/// `Payload` is a `byte[]`: the server writes it base64 (the SDK's
/// `ByteArrayConverter`) and the browser's decoder expects a number
/// array, so a whole-record decode would fail on the one field this
/// handler has no use for. Returns `null` for anything it cannot read.
[<Emit("(function (json) { try { var e = JSON.parse(json); var u = e && e.Update; if (!u || !u.Ref) { return null; } return [String(u.OriginSession), String(u.Ref.DocId)]; } catch (err) { return null; } })($0)")>]
let private relayTarget (payloadJson: string) : string[] = jsNative

// Read as a raw string rather than through the typed `VisibilityState`
// enum, per `SyncCoordinator`'s note: the enum's shape has moved
// between Fable.Browser.Dom majors and this comparison must not.
[<Emit("(typeof document !== 'undefined' && document !== null && document.visibilityState === 'visible')")>]
let private documentVisible () : bool = jsNative

/// Subscribe `onRelay` to the reserved `_platform.crdt` topic for one
/// document, and return the thunk that unsubscribes.
///
/// `_platform.crdt` is the key the server's relay
/// (`NotifyingCrdtDocumentStore`) publishes on after every append and
/// compaction. The channel gates it by the document ref's own scope, so
/// this handler is never offered another team's traffic (GP 4) — the
/// `docId` test below narrows within one scope, and is not the security
/// boundary.
///
/// An event whose `OriginSession` is this tab's own is this tab's echo
/// and is dropped. A compaction base carries the reserved
/// `_platform.compaction` origin, which is nobody's session, so it is
/// never dropped — a joiner may well be missing the content it merges.
///
/// **An unreadable payload resyncs rather than being ignored.** The
/// event still says something on this scope moved, a resync is
/// idempotent and cheap, and the alternative is a payload-shape change
/// that stops a document relaying with nothing to see. This is the same
/// direction the store's own relay takes when a publish fails: prefer
/// the redundant read to the missed one.
let subscribeRelay (docId: string) (sessionId: string) (onRelay: unit -> unit) : unit -> unit =
    NotificationClient.subscribe (fun envelope ->
        match envelope.Notification with
        | CustomNotification(key, payloadJson) when key = CrdtTopics.Update ->
            let target = relayTarget payloadJson

            if isNull (box target) then
                onRelay ()
            elif target[1] = docId && target[0] <> sessionId then
                onRelay ()
        | _ -> ())

/// One live co-editing session: the Phase 535 pump plus the
/// subscription that keeps it live.
type LiveCoEdit = {
    /// The pump. `Resync` and `ApplyRemote` are unchanged — a caller
    /// that wants to re-anchor on a visibility change still calls them.
    Session: CrdtSyncClient.CrdtSession
    /// Tear down the subscription AND the pump. One thunk because they
    /// have one lifetime: a view that disposed the session and left the
    /// handler registered would resync a document nobody is showing.
    Dispose: unit -> unit
}

/// Join `docId` and stay live: catch up at join, then resync on every
/// relay signal.
///
/// What arrives on the topic is treated as a SIGNAL, not as content —
/// the handler calls `Resync`, which reads the diff from the cursor the
/// session retained. Applying the relayed payload directly would save a
/// round trip and leave the cursor un-advanced (see `CrdtSyncClient`'s
/// header), so signal-then-diff is what keeps the next reconnect cheap.
///
/// **The join-time catch-up remains the fallback.** A tab whose stream
/// is down, or whose event the server dropped (the relay swallows
/// publish failures, deliberately), loses nothing: the retained cursor
/// recovers it on the next resync. A missed signal costs latency, never
/// content.
let startLive
    (yjs: CrdtSyncClient.IYjs)
    (doc: CrdtSyncClient.IYDoc)
    (transport: CrdtSyncClient.CrdtTransport)
    (docId: string)
    (sessionId: string)
    : LiveCoEdit =
    let session = CrdtSyncClient.start yjs doc transport sessionId

    // A resync that fails is swallowed: a tab whose link is down reads
    // into nothing, which is exactly the state a reconnect recovers
    // from — and surfacing it would turn a transient blip into an error
    // the view has no answer for.
    let resync () =
        async {
            try
                do! session.Resync()
            with _ ->
                ()
        }
        |> Async.StartImmediate

    let unsubscribe = subscribeRelay docId sessionId resync

    // The other two moments `CrdtSession.Resync` names as its own
    // triggers, registered here rather than left to every consumer to
    // remember. Neither is a service and neither is a timer — they are
    // listeners with the session's lifetime, and a page that never
    // loses its link never pays for them (GP 13).
    //
    //   * `online` — a stream that was down missed every event
    //     published while it was down, and the browser's own
    //     reconnection restores the CONNECTION, not the gap. The
    //     retained cursor is the only thing that closes the gap, and
    //     this is the moment to ask it to. Without this a tab that
    //     drops its link for one edit is dark until it is reloaded.
    //   * `visibilitychange` — a backgrounded tab is throttled and its
    //     stream can be dropped by an intermediary; re-anchor when it
    //     comes back rather than assume nothing was missed.
    let onOnline = fun (_: Browser.Types.Event) -> resync ()

    let onVisible =
        fun (_: Browser.Types.Event) ->
            if documentVisible () then
                resync ()

    Browser.Dom.window.addEventListener ("online", unbox onOnline)
    Browser.Dom.document.addEventListener ("visibilitychange", unbox onVisible)

    {
        Session = session
        Dispose =
            fun () ->
                unsubscribe ()
                Browser.Dom.window.removeEventListener ("online", unbox onOnline)
                Browser.Dom.document.removeEventListener ("visibilitychange", unbox onVisible)
                session.Dispose()
    }

/// Narrow a whole-value text-area change to the span that actually
/// changed, by common prefix and suffix.
///
/// Worth doing even in a sample: a whole-value delete-and-reinsert would
/// destroy a co-editor's concurrent edit outside the changed span — the
/// CRDT would merge it faithfully, and the merge would be "the other
/// person retyped the entire document". The prefix/suffix delta is the
/// minimum that keeps concurrent edits, and it is what makes this a
/// demonstration of co-editing rather than of last-write-wins.
let applyLocalEdit (ytext: obj) (current: string) (next: string) : unit =
    let mutable prefix = 0

    while prefix < current.Length
          && prefix < next.Length
          && current[prefix] = next[prefix] do
        prefix <- prefix + 1

    let mutable suffix = 0

    while suffix < current.Length - prefix
          && suffix < next.Length - prefix
          && current[current.Length - 1 - suffix] = next[next.Length - 1 - suffix] do
        suffix <- suffix + 1

    let deleted = current.Length - prefix - suffix

    if deleted > 0 then
        deleteRange ytext prefix deleted

    let inserted = next.Substring(prefix, next.Length - prefix - suffix)

    if inserted.Length > 0 then
        insertAt ytext prefix inserted

/// A text area several people edit at once.
///
/// `sessionId` identifies this tab — it is the echo-suppression key, so
/// it must be unique per tab rather than per user (two tabs open by one
/// person are two co-editors).
///
/// `onLocation` is where awareness goes: the co-editing position rides
/// the Phase 442 presence location descriptor
/// (`CrdtAwareness.location`), never the update log, because a cursor is
/// worthless a second after it moves and the log is durable. A real view
/// hands the value to `IPresenceApi.Heartbeat`.
[<ReactComponent>]
let SharedTextArea
    (api: CoEditApi)
    (docId: string)
    (sessionId: string)
    (onLocation: PresenceLocation -> unit)
    : ReactElement =
    let text, setText = React.useState ""
    let ytextHandle = React.useRef (None: obj option)

    React.useEffectOnce (fun () ->
        let ydoc: obj = createNew YDocCtor ()
        let ytext = getText ydoc "content"
        ytextHandle.current <- Some ytext

        // Every change to the shared text — local or merged in from a
        // co-editor — re-renders from the CRDT's own value, so the view
        // never holds a state the document disagrees with.
        observeText ytext (fun () -> setText (textValue ytext))

        // `startLive`, not `CrdtSyncClient.start`: the pump alone
        // catches up at join and never again, so the text area would
        // converge at mount and then quietly stop (Phase 764).
        let live =
            startLive yjs (unbox<CrdtSyncClient.IYDoc> ydoc) (transportFor api docId sessionId) docId sessionId

        // Announce where this participant is, on the presence substrate.
        // The scope is server-resolved, so the client names only the
        // document it is in.
        onLocation (CrdtAwareness.location "minimal-client" (CrdtDocRef.create "" docId) None)

        let cleanup: unit -> unit =
            fun () ->
                live.Dispose()
                ytextHandle.current <- None

        cleanup)

    Html.div [
        Html.label [ prop.htmlFor "coedit"; prop.text "Shared notes" ]
        Html.textarea [
            prop.id "coedit"
            prop.value text
            prop.rows 6
            prop.onChange (fun (next: string) ->
                match ytextHandle.current with
                | Some ytext -> applyLocalEdit ytext (textValue ytext) next
                | None -> ())
        ]
    ]