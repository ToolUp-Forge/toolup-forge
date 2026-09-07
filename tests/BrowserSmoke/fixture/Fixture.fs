// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module BrowserSmoke.Fixture

open System
open Fable.Core
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform
open ToolUp.Offline
open ToolUp.Offline.Client
open MinimalClient

// ─── Phase 761 — the page the browser smoke harness drives ───────────
//
// One page, two scenarios, selected by query string. Everything of
// substance is imported: the CRDT pump is `ToolUp.Platform.CrdtSyncClient`,
// the queue and the routed coordinator are `ToolUp.Offline.Client`, and
// the wiring that joins them is `samples/MinimalClient`'s own — the
// Phase 535 and 760 reference examples, compiled from where they live.
//
// What this file adds is only what a DEPLOYMENT owns and the SDK
// deliberately does not:
//
//   * the module-owned HTTP API over `ICrdtDocumentStore` (Phase 535 is
//     seam-first: it registers the substrate and mounts no route, so
//     every consumer writes this handful of lines — see
//     `CrdtCoEditSample.CoEditApi`);
//   * the FAN-OUT. `CrdtSyncClient.start` catches up once on join and
//     then publishes; a relayed update reaches a tab only when something
//     tells it to `Resync`. A real deployment subscribes to the reserved
//     `_platform.crdt` notification topic; the harness polls, because a
//     notification transport is a much larger surface than the thing
//     under test and polling makes the timing of the scenarios explicit
//     rather than incidental.
//
// ## The fault switches, and why they live here
//
// The phase's acceptance is not "the scenarios pass" — it is "breaking
// the relay or the queue turns the respective scenario RED". A go-red
// observed once, by hand, on one machine, is not evidence anyone can
// re-check; so each break is a query-string switch the harness drives,
// and the harness asserts BOTH directions every run. The switches cut
// the two seams a deployment supplies — the fan-out and the hold — and
// nothing inside the SDK is touched or mocked to produce them.
//
//   ?fault=relay — the fan-out poll never starts. Scenario 1 must fail
//                  to converge: each tab's own edits are published, and
//                  nothing ever asks for the other's.
//   ?fault=queue — the offline queue handed to the Phase 760 wiring
//                  drops on `Enqueue`. Scenario 2 must fail to apply:
//                  the update is neither published (offline) nor held.

// ─── Query string ────────────────────────────────────────────────────

[<Emit("(new URLSearchParams(window.location.search)).get($0)")>]
let private rawParam (name: string) : string = jsNative

let private queryParam (name: string) (fallback: string) : string =
    match rawParam name with
    | null -> fallback
    | value -> value

// ─── Base64 over the opaque CRDT payloads ────────────────────────────
//
// The payloads are `byte[]` (a `Uint8Array` after Fable) and the wire is
// JSON, so they travel base64. A loop rather than
// `String.fromCharCode.apply` — the apply form blows the argument limit
// on a payload of any size, and a smoke fixture that works only for
// short documents is a trap for whoever adds the next scenario.

[<Emit("(function (a) { let s = ''; for (let i = 0; i < a.length; i++) { s += String.fromCharCode(a[i]); } return btoa(s); })($0)")>]
let private toBase64 (bytes: byte[]) : string = jsNative

[<Emit("Uint8Array.from(atob($0), function (c) { return c.charCodeAt(0); })")>]
let private ofBase64 (text: string) : byte[] = jsNative

[<Emit("fetch($0, { method: 'POST', headers: { 'content-type': 'application/json' }, body: $1 }).then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.text(); })")>]
let private postJson (url: string) (body: string) : JS.Promise<string> = jsNative

let private post (url: string) (payload: obj) : Async<string> =
    postJson url (JS.JSON.stringify payload) |> Async.AwaitPromise

// ─── The deployment's own API over `ICrdtDocumentStore` ──────────────
//
// Note what does not cross the wire: the scope. `FixtureHost` resolves
// it, exactly as `CrdtCoEditSample.transportFor`'s header explains a
// real handler must (GP 4).

let private coEditApi: CrdtCoEditSample.CoEditApi = {
    Append =
        fun (docId, payload, originSession) -> async {
            let! _ =
                post
                    "/api/coedit/append"
                    (createObj [ "doc" ==> docId; "session" ==> originSession; "payload" ==> toBase64 payload ])

            return ()
        }
    Diff =
        fun (docId, since) -> async {
            let! body = post "/api/coedit/diff" (createObj [ "doc" ==> docId; "since" ==> toBase64 since.Bytes ])

            let parsed: obj = JS.JSON.parse body
            let rows: obj[] = unbox parsed?updates

            let updates =
                rows
                |> Array.toList
                |> List.map (fun row -> {
                    Ref = CrdtDocRef.create "" docId
                    Payload = ofBase64 (unbox row?payload)
                    OriginSession = unbox row?session
                    Sequence = unbox<float> row?sequence |> int64
                    AppendedAt = DateTime.UtcNow
                })

            return updates, StateVector.ofBytes (ofBase64 (unbox parsed?vector))
        }
}

/// The ordinary entity-sync half of the offline contract. The scenarios
/// queue only MERGEABLE payloads, which the coordinator routes to the
/// CRDT channel and never sends here — so this stub exists to satisfy
/// `startRouted`'s signature, and a call arriving on it would mean the
/// Phase 760 routing had failed. It records that rather than absorbing
/// it: `window.__smoke.strayEntitySync` is asserted zero by the harness.
let private entitySyncApi: OfflineSyncApi.IOfflineSyncApi =
    let stray () =
        let current: int = unbox window?__smoke?strayEntitySync
        window?__smoke?strayEntitySync <- current + 1

    {
        Apply =
            fun _ -> async {
                stray ()
                return SyncOutcome.Rejected "browser-smoke fixture: no entity sync"
            }
        ApplyBatch =
            fun mutations -> async {
                stray ()

                return
                    mutations
                    |> List.map (fun m -> {
                        OfflineSyncApi.SyncResult.MutationId = m.Id
                        Outcome = SyncOutcome.Rejected "browser-smoke fixture: no entity sync"
                    })
            }
        FetchCurrent = fun _ -> async { return None }
    }

/// A queue that loses everything handed to it — the `?fault=queue`
/// break. Wrapping the real queue rather than replacing it keeps every
/// other path (List, Drain, the mark verbs) genuinely the shipped one,
/// so the scenario fails for the reason named and not because the
/// fixture swapped a different object in.
type private DroppingQueue(inner: OfflineQueue.IOfflineQueue) =
    interface OfflineQueue.IOfflineQueue with
        member _.Enqueue _ = async { return () }
        member _.List() = inner.List()
        member _.Drain(policy, now) = inner.Drain(policy, now)
        member _.MarkApplied id = inner.MarkApplied id
        member _.MarkConflicted(id, serverEntity) = inner.MarkConflicted(id, serverEntity)

        member _.MarkConflict(id, resolution, rebaseVersion) =
            inner.MarkConflict(id, resolution, rebaseVersion)

        member _.MarkFailed(id, reason) = inner.MarkFailed(id, reason)
        member _.Discard id = inner.Discard id
        member _.Clear() = inner.Clear()

// ─── DOM ─────────────────────────────────────────────────────────────

let private el (tag: string) (id: string) : Types.HTMLElement =
    let node = document.createElement tag
    node.id <- id
    node

let private mountShell (textAreaId: string) =
    let root = document.getElementById "fixture"

    let status = el "span" "status"
    status.setAttribute ("data-status", "starting")
    status.textContent <- "starting"

    let queued = el "span" "queued"
    queued.setAttribute ("data-count", "0")
    queued.textContent <- "0"

    let area = el "textarea" textAreaId
    area.setAttribute ("rows", "6")
    area.setAttribute ("cols", "60")

    root.appendChild status |> ignore
    root.appendChild queued |> ignore
    root.appendChild area |> ignore
    status, queued, unbox<Types.HTMLTextAreaElement> area

/// Mark the page ready only once the pump is running, so the harness
/// waits on a fact rather than on a duration.
let private markReady () =
    let root = document.getElementById "fixture"
    root.setAttribute ("data-ready", "true")

// ─── Shared plumbing ─────────────────────────────────────────────────

let private bindTextArea (area: Types.HTMLTextAreaElement) (ytext: obj) =
    // Render from the CRDT's own value on every change — local or
    // merged in — so the view never holds a state the document
    // disagrees with (the sample's rule, kept).
    CrdtCoEditSample.observeText ytext (fun () ->
        let value = CrdtCoEditSample.textValue ytext

        if area.value <> value then
            area.value <- value)

    area.addEventListener (
        "input",
        fun _ -> CrdtCoEditSample.applyLocalEdit ytext (CrdtCoEditSample.textValue ytext) area.value
    )

/// The fan-out stand-in. A real deployment subscribes to the reserved
/// `_platform.crdt` topic; this asks for the diff on a timer. Failures
/// are swallowed by design — a tab whose link is down polls into
/// nothing, which is exactly the state scenario 1's reconnect clause
/// puts it in.
///
/// **`cut` skips the RESYNC, never the timer.** The `?fault=relay`
/// switch has to change exactly one thing — whether anything ever asks
/// for the other participant's edits — because the scenario it serves
/// asserts that publishing still works while relaying does not. Not
/// registering the interval at all was the first shape of this switch
/// and it was wrong: on a page with no timer registered, nothing else
/// the fixture starts asynchronously ran either (the pump's own join-
/// time catch-up never issued its request), so the "broken" page was
/// not a page with a cut relay — it was a page that did nothing, which
/// proves nothing about the relay. Keeping the timer and skipping its
/// one call is the minimal cut.
let private startRelay (cut: bool) (resync: unit -> Async<unit>) =
    window.setInterval (
        (fun () ->
            if not cut then
                async {
                    try
                        do! resync ()
                    with _ ->
                        ()
                }
                |> Async.StartImmediate),
        200
    )
    |> ignore

// ─── Scenario 1 — co-edit ────────────────────────────────────────────

let private startCoEdit (docId: string) (sessionId: string) (faultRelay: bool) =
    let status, _, area = mountShell "coedit"
    let ydoc: obj = createNew CrdtCoEditSample.YDocCtor ()
    let ytext = CrdtCoEditSample.getText ydoc "content"
    bindTextArea area ytext

    let session =
        CrdtSyncClient.start
            CrdtCoEditSample.yjs
            (unbox<CrdtSyncClient.IYDoc> ydoc)
            (CrdtCoEditSample.transportFor coEditApi docId sessionId)
            sessionId

    startRelay faultRelay session.Resync

    status.setAttribute ("data-status", "co-editing")
    status.textContent <- "co-editing"
    markReady ()

// ─── Scenario 2 — offline ⇄ CRDT ─────────────────────────────────────

let private startOffline (docId: string) (sessionId: string) (faultQueue: bool) =
    let status, queued, area = mountShell "coedit-offline"
    let ydoc: obj = createNew CrdtCoEditSample.YDocCtor ()
    let ytext = CrdtCoEditSample.getText ydoc "content"
    bindTextArea area ytext

    // Per-deployment database name, per the queue's own guidance — and
    // per-SESSION here, so two contexts in one Playwright run never
    // share an IndexedDB store on the same origin.
    let real = OfflineQueue.create (sprintf "browser-smoke-%s" sessionId)

    let queue =
        if faultQueue then
            DroppingQueue real :> OfflineQueue.IOfflineQueue
        else
            real

    let joined =
        CrdtOfflineCoEditSample.start
            CrdtCoEditSample.yjs
            (unbox<CrdtSyncClient.IYDoc> ydoc)
            (CrdtCoEditSample.transportFor coEditApi docId sessionId)
            entitySyncApi
            OfflineConfig.defaults
            queue
            docId
            "browser-smoke"
            sessionId

    startRelay false joined.Session.Resync

    // The badge, from the coordinator's own derivation — the harness
    // reads `data-status` / `data-count`, never a rendered string.
    window.setInterval (
        (fun () ->
            async {
                let! current = joined.Coordinator.Status()
                let! entries = queue.List()
                let label = SyncStatus.label current
                status.setAttribute ("data-status", label)
                status.textContent <- label
                let pending = (QueueStats.ofEntries entries).Pending
                queued.setAttribute ("data-count", string pending)
                queued.textContent <- string pending
            }
            |> Async.StartImmediate),
        150
    )
    |> ignore

    markReady ()

// ─── Entry point ─────────────────────────────────────────────────────

window?__smoke <- createObj [ "strayEntitySync" ==> 0 ]

let private docId = queryParam "doc" "smoke-doc"
let private sessionId = queryParam "session" "smoke-session"
let private fault = queryParam "fault" ""

do
    match queryParam "mode" "coedit" with
    | "offline" -> startOffline docId sessionId (fault = "queue")
    | _ -> startCoEdit docId sessionId (fault = "relay")