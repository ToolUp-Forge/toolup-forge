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
let private yjs: CrdtSyncClient.IYjs = importAll "yjs"

/// The `Y.Doc` constructor.
let private YDocCtor: obj = import "Doc" "yjs"

[<Emit("$0.getText($1)")>]
let private getText (doc: obj) (name: string) : obj = jsNative

[<Emit("$0.toString()")>]
let private textValue (ytext: obj) : string = jsNative

[<Emit("$0.observe($1)")>]
let private observeText (ytext: obj) (handler: unit -> unit) : unit = jsNative

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

        let session =
            CrdtSyncClient.start yjs (unbox<CrdtSyncClient.IYDoc> ydoc) (transportFor api docId sessionId) sessionId

        // Announce where this participant is, on the presence substrate.
        // The scope is server-resolved, so the client names only the
        // document it is in.
        onLocation (CrdtAwareness.location "minimal-client" (CrdtDocRef.create "" docId) None)

        let cleanup: unit -> unit =
            fun () ->
                session.Dispose()
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