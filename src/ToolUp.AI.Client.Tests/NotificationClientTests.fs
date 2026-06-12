// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.NotificationClientTests

// ─── Phase 117 — NotificationClient identity lifecycle ──────────────
//
// Exercises the Platform.Client `NotificationClient` reconnect/reset
// surface under Node (the module lives in Platform.Client, reached
// transitively — same Phase 110 precedent as `ClientHostBridgeTests`).
//
// The browser substrate is stubbed before any module under test runs:
// `globalThis.EventSource` is a recording fake (constructor URL +
// `close()` tracking + manually fireable `onerror`), and
// `globalThis.window.localStorage` is a Map-backed shim so
// `UserSession.getUserId` / `getAuthToken` behave normally.
//
// Cases are ORDER-DEPENDENT: `NotificationClient` is a per-tab
// singleton (module-level connection state by design), so each case
// continues from the previous one's state — the same way one browser
// tab would experience the sequence anonymous boot → sign-in →
// token refresh → server blip → sign-out.

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

// ─── Browser-substrate stubs (installed before first use) ───────────

[<Emit("""(() => {
    class FakeEventSource {
        constructor(url) {
            this.url = url;
            this.readyState = 0;
            this.closed = false;
            this.onerror = null;
            this.onopen = null;
            this._listeners = {};
            FakeEventSource.instances.push(this);
        }
        addEventListener(name, handler) {
            (this._listeners[name] = this._listeners[name] || []).push(handler);
        }
        close() {
            this.closed = true;
            this.readyState = 2;
        }
    }
    FakeEventSource.instances = [];
    globalThis.EventSource = FakeEventSource;
    const store = new Map();
    globalThis.window = globalThis.window || {};
    globalThis.window.localStorage = {
        getItem: (k) => (store.has(k) ? store.get(k) : null),
        setItem: (k, v) => { store.set(k, String(v)); },
        removeItem: (k) => { store.delete(k); },
    };
})()""")>]
let private installBrowserStubs () : unit = jsNative

[<Emit("globalThis.EventSource.instances.length")>]
let private instanceCount () : int = jsNative

[<Emit("globalThis.EventSource.instances[$0].url")>]
let private urlAt (i: int) : string = jsNative

[<Emit("globalThis.EventSource.instances[$0].closed")>]
let private closedAt (i: int) : bool = jsNative

[<Emit("globalThis.EventSource.instances[$0].readyState = 2")>]
let private markFatallyClosed (i: int) : unit = jsNative

[<Emit("globalThis.EventSource.instances[$0].onerror(null)")>]
let private fireError (i: int) : unit = jsNative

[<Emit("globalThis.window.localStorage.setItem($0, $1)")>]
let private setStorage (key: string) (value: string) : unit = jsNative

[<Emit("globalThis.window.localStorage.removeItem($0)")>]
let private removeStorage (key: string) : unit = jsNative

// Storage keys mirror the private constants in `UserSession.fs` — the
// localStorage wire shape is a stable contract (cookie name doc note
// there) so duplicating the strings here is the same manual-sync rule
// the notification kinds follow.
[<Literal>]
let private userIdKey = "toolup-user-id"

[<Literal>]
let private tokenKey = "toolup-auth-token"

[<Literal>]
let private tokenUserIdKey = "toolup-token-user-id"

let tests =
    installBrowserStubs ()

    let mutable disposeSubscription: (unit -> unit) option = None

    testList "NotificationClient identity lifecycle (Phase 117)" [
        testCase "anonymous connect carries ?userId= as the per-session marker" (fun () ->
            setStorage userIdKey "anon-a"
            UserSession.configure AnonymousKind

            disposeSubscription <- Some(NotificationClient.subscribe ignore)

            Expect.equal (instanceCount ()) 1 "first subscribe opens exactly one EventSource"
            Expect.equal (urlAt 0) "/api/notifications?userId=anon-a" "anonymous handshake uses the query-param marker")

        testCase
            "sign-in transition closes the old stream and reopens under the new identity via cookie handshake"
            (fun () ->
                // Simulate what `UserSession.setAuthToken` persists (the JWT
                // cookie write needs `document`, which Node doesn't have;
                // the localStorage half is what `getUserId` / `getAuthToken`
                // read).
                setStorage tokenKey "fake.jwt.token"
                setStorage tokenUserIdKey "user-b"
                UserSession.configure UserKind

                NotificationClient.reconnect ()

                Expect.isTrue (closedAt 0) "the previous user's stream is closed on identity transition"
                Expect.equal (instanceCount ()) 2 "a fresh EventSource is opened for the new identity"
                Expect.equal (urlAt 1) "/api/notifications" "token present → cookie handshake, no ?userId= in the URL")

        testCase "same-identity reconnect (token refresh) does not cycle the stream" (fun () ->
            NotificationClient.reconnect ()

            Expect.equal (instanceCount ()) 2 "no new EventSource for a same-user token refresh"
            Expect.isFalse (closedAt 1) "the live stream is untouched")

        testCase "one fatal close schedules a retry instead of latching; reconnect recovers immediately" (fun () ->
            // Pre-117 behaviour: first fatal close set a permanent
            // give-up latch (logged as \"404\"). Now it consumes one
            // bounded-retry attempt — and an auth-driven reconnect
            // cancels the pending timer and recovers at once.
            markFatallyClosed 1
            fireError 1

            Expect.equal (instanceCount ()) 2 "no instant reopen — the retry waits out its backoff delay"

            NotificationClient.reconnect ()

            Expect.equal
                (instanceCount ())
                3
                "reconnect after a fatal close opens a fresh EventSource (no permanent latch)"

            Expect.equal (urlAt 2) "/api/notifications" "recovered stream keeps the cookie handshake")

        testCase "sign-out reset closes the stream and does not reopen by itself" (fun () ->
            removeStorage tokenKey
            removeStorage tokenUserIdKey
            UserSession.configure AnonymousKind

            NotificationClient.reset ()

            Expect.isTrue (closedAt 2) "the signed-out user's stream is closed"
            Expect.equal (instanceCount ()) 3 "reset never reopens — the next subscribe/reconnect does"

            // Leave the singleton quiescent for any later test module:
            // drop the handler registered in case 1.
            match disposeSubscription with
            | Some dispose ->
                dispose ()
                disposeSubscription <- None
            | None -> ())
    ]