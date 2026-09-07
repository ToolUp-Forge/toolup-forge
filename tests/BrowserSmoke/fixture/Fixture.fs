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
//   * the SERVER half of the fan-out — the notification endpoint the
//     relay's events reach a tab over. `FixtureHost` mounts it and
//     publishes through the shipped `NotifyingCrdtDocumentStore`.
//
// **What this file no longer owns is the CLIENT half.** Until Phase 764
// it polled `session.Resync` on a timer here, which meant the harness
// certified a wiring the sample did not ship: `CrdtCoEditSample`
// caught up once at join and then went quiet, and only the fixture
// stayed live. The subscription now lives in the sample
// (`CrdtCoEditSample.startLive`, over the reserved `_platform.crdt`
// topic and the existing `NotificationClient` stream), and this page
// simply starts it — so what the scenarios drive is the shipped shape
// and nothing else.
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
//   ?fault=relay — the page asks the host NOT to relay its appends, and
//                  the host writes them through the undecorated store.
//                  Scenario 1 must fail to converge: each tab's own
//                  edits are published and durable, and no event ever
//                  announces them. The cut moved server-side with the
//                  subscription's move client-side — cutting the
//                  sample's own `subscribeRelay` would break the SDK
//                  code under test, which is exactly what a go-red must
//                  not do.
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
//
// `relay` DOES cross it, and only because this is a fixture: it is the
// `?fault=relay` switch, and it names the one thing the deployment does
// on the page's behalf that the go-red needs cut. A real handler has no
// such field — it relays because it composed
// `EnabledCrdtDocuments`, and nothing on the wire can say otherwise.

let private coEditApi (relay: bool) : CrdtCoEditSample.CoEditApi = {
    Append =
        fun (docId, payload, originSession) -> async {
            let! _ =
                post
                    "/api/coedit/append"
                    (createObj [
                        "doc" ==> docId
                        "session" ==> originSession
                        "payload" ==> toBase64 payload
                        "relay" ==> relay
                    ])

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

// ─── Readiness ───────────────────────────────────────────────────────
//
// `data-ready` means BOTH halves of a live document are up: the pump is
// running, AND this page's notification stream — the one the sample's
// relay subscription rides — is open.
//
// The second half is not fussiness. A page that declares itself ready
// while its `EventSource` is still handshaking can miss the first edit
// a co-editor makes, and with the fixture's old poll gone nothing ever
// asks for it again: the scenario goes red for a reason that is about
// this page's boot order and not about the code under test. A real
// deployment does not have the race — its shell opens the stream at
// sign-in, long before any document mounts — so waiting for the same
// state here is modelling a deployment, not working around one.
//
// It is a FACT and not a delay: `FixtureHost` writes a hello envelope
// to each stream the moment it subscribes it, so the first envelope
// this page receives is proof its own connection is live.

let private mounted = ref false
let private streamOpen = ref false

let private markReadyWhenLive () =
    if mounted.Value && streamOpen.Value then
        let root = document.getElementById "fixture"
        root.setAttribute ("data-ready", "true")

let private markReady () =
    mounted.Value <- true
    markReadyWhenLive ()

/// Open the notification stream at page boot — the shell's job in a
/// real app, and the fixture's here. The handler is never disposed
/// because the page's own lifetime is the subscription's.
let private openNotificationStream () =
    NotificationClient.subscribe (fun _ ->
        if not streamOpen.Value then
            streamOpen.Value <- true
            markReadyWhenLive ())
    |> ignore

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

// ─── Scenario 1 — co-edit ────────────────────────────────────────────
//
// Nothing here keeps the document live. `CrdtCoEditSample.startLive`
// does, over the reserved `_platform.crdt` topic and the per-tab
// `NotificationClient` stream — which is the whole point of Phase 764:
// the page under the browser runs the wiring a consumer is told to
// copy, so a regression in that wiring turns this scenario red.

let private startCoEdit (docId: string) (sessionId: string) (faultRelay: bool) =
    let status, _, area = mountShell "coedit"
    let ydoc: obj = createNew CrdtCoEditSample.YDocCtor ()
    let ytext = CrdtCoEditSample.getText ydoc "content"
    bindTextArea area ytext

    let api = coEditApi (not faultRelay)

    CrdtCoEditSample.startLive
        CrdtCoEditSample.yjs
        (unbox<CrdtSyncClient.IYDoc> ydoc)
        (CrdtCoEditSample.transportFor api docId sessionId)
        docId
        sessionId
    |> ignore

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

    // The Phase 760 wiring starts live of its own accord now (it
    // composes `CrdtCoEditSample.startLive`), so this scenario relays
    // through the same subscription scenario 1 does and the fixture
    // registers no timer of its own for it.
    let joined =
        CrdtOfflineCoEditSample.start
            CrdtCoEditSample.yjs
            (unbox<CrdtSyncClient.IYDoc> ydoc)
            (CrdtCoEditSample.transportFor (coEditApi true) docId sessionId)
            entitySyncApi
            OfflineConfig.defaults
            queue
            docId
            "browser-smoke"
            sessionId

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
    // Before either scenario mounts, so the stream is handshaking while
    // the pump joins rather than after it.
    openNotificationStream ()

    match queryParam "mode" "coedit" with
    | "offline" -> startOffline docId sessionId (fault = "queue")
    | _ -> startCoEdit docId sessionId (fault = "relay")