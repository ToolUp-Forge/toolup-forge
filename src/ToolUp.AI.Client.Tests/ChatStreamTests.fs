// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The AI stream on the client: one `EventSource` per tab, the out-of-band
/// cases routed once, and one reducer semantics for both chat surfaces.
module ToolUp.AI.Client.Tests.ChatStreamTests

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open ToolUp.AI
open ToolUp.AI.Client
open ToolUp.AI.Client.Tests.NodeTest

// ─── The browser substrate ───────────────────────────────────────────
//
// A recording `EventSource` and a resolved `window.fetch`, installed for the
// duration of one case and then put back, so the identity-lifecycle pack's
// own fake (NotificationClientTests) is untouched. The server broadcasts an
// event to every connection a scope holds, so delivering it means calling
// every open instance's `onmessage`.

[<Emit("""(() => {
    const saved = { es: globalThis.EventSource, win: globalThis.window };
    class FakeEventSource {
        constructor(url) {
            this.url = url;
            this.readyState = 1;
            this.closed = false;
            this.onmessage = null;
            this.onerror = null;
            FakeEventSource.instances.push(this);
        }
        close() { this.closed = true; this.readyState = 2; }
    }
    FakeEventSource.instances = [];
    globalThis.EventSource = FakeEventSource;
    const win = globalThis.window || {};
    globalThis.window = win;
    win.__chatStreamSavedFetch = win.fetch;
    win.fetch = (url, opts) => { (win.__chatStreamPosts = win.__chatStreamPosts || []).push(url); return Promise.resolve({ ok: true }); };
    win.__chatStreamPosts = [];
    globalThis.__chatStreamSaved = saved;
})()""")>]
let private installSubstrate () : unit = jsNative

[<Emit("""(() => {
    const saved = globalThis.__chatStreamSaved;
    const win = globalThis.window;
    if (win) { win.fetch = win.__chatStreamSavedFetch; }
    globalThis.EventSource = saved.es;
})()""")>]
let private restoreSubstrate () : unit = jsNative

[<Emit("globalThis.EventSource.instances.filter(i => !i.closed).length")>]
let private openStreams () : int = jsNative

[<Emit("globalThis.EventSource.instances.filter(i => !i.closed).forEach(i => i.onmessage && i.onmessage({ data: $0 }))")>]
let private broadcast (json: string) : unit = jsNative

[<Emit("globalThis.window.__chatStreamPosts.filter(u => u === $0).length")>]
let private postsTo (url: string) : int = jsNative

// ─── One stream, one routing ─────────────────────────────────────────

let private probeTool = "_test.chat-stream.probe"

let private oneStreamTests =
    testList "one EventSource per tab" [
        testCaseDeferred "with both surfaces mounted a client-resident tool runs once" 30 (fun () ->
            installSubstrate ()
            let mutable runs = 0

            ClientToolRuntime.register probeTool (fun (_ctx, _args) -> async {
                runs <- runs + 1
                return "{}"
            })

            // The two subscriptions the two surfaces open: the side panel's
            // program-lifetime effect and the full page's mounted component.
            let sidePanel = SSEClient.subscribe ignore
            let fullPage = SSEClient.subscribe ignore

            let invoke =
                ClientToolInvoke(Guid.NewGuid(), Guid.NewGuid(), probeTool, "{}", None, None)

            broadcast (Json.serialize invoke)

            fun () ->
                let streams = openStreams ()
                let posts = postsTo "/api/ai/tool-result"
                sidePanel ()
                fullPage ()
                restoreSubstrate ()
                Expect.equal runs 1 "the tool ran once"
                Expect.equal streams 1 "one EventSource is open for the tab"
                Expect.equal posts 1 "and its result was posted once")
    ]

let tests = testList "Phase 1080 — the chat stream" [ oneStreamTests ]