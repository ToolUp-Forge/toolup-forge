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
            let sidePanel = AIClientConfig.streamSubscription ignore
            let fullPage = AIAssistantUI.streamSubscription ignore

            let invoke =
                ClientToolInvoke(Guid.NewGuid(), Guid.NewGuid(), probeTool, "{}", None, None)

            broadcast (Json.serialize invoke)

            fun () ->
                let streams = openStreams ()
                let posts = postsTo "/api/ai/tool-result"
                sidePanel.Dispose()
                let openWithOneSurface = openStreams ()
                fullPage.Dispose()
                let openWithNone = openStreams ()
                restoreSubstrate ()
                Expect.equal openWithOneSurface 1 "the stream outlives one surface unmounting"
                Expect.equal openWithNone 0 "and closes when the last one goes"
                Expect.equal runs 1 "the tool ran once"
                Expect.equal streams 1 "one EventSource is open for the tab"
                Expect.equal posts 1 "and its result was posted once")
    ]

// ─── One semantics on both surfaces ──────────────────────────────────
//
// The side panel and the full page used to fold the stream through two
// reducers that differed in nine places. Each place is a recorded decision
// (the migration note lists them); each is pinned here on BOTH surfaces, so
// a surface that drifts from the other goes red.

let private updateOuter =
    let modules, config = ComposerScope.testComposition ()
    AIClientConfig.composerUpdate config Unchecked.defaultof<ToolUp.Platform.IModuleQueryBus> modules

let private panel (msg: AIClientConfig.SidePanelMsg) (model: AIClientConfig.OuterModel) =
    updateOuter (AIClientConfig.SidePanelMsg msg) model |> fst

let private idlePanel () : AIClientConfig.OuterModel = {
    Shell = ComposerScope.shellModel0 ()
    SidePanel = AIClientConfig.composerSidePanelInit ()
}

let private aTask (conv: Guid) : AITask = {
    TaskId = Guid.NewGuid()
    ConversationId = conv
    Prompt = "how did sales do?"
    Status = InProgress
    CreatedAt = DateTime.UtcNow
    CompletedAt = None
}

/// A side panel with a question sent and its task on record.
let private panelSent () =
    let m =
        idlePanel ()
        |> panel (AIClientConfig.Send("module", None, None, "how did sales do?"))

    let t = aTask m.SidePanel.ConversationId
    m |> panel (AIClientConfig.Submitted t), m.SidePanel.ConversationId, t

let private page (msg: AIAssistantUI.Msg) (model: AIAssistantUI.Model) = AIAssistantUI.update msg model |> fst

let private prepared (conv: Guid) (content: string) : AIAssistantUI.Msg =
    AIAssistantUI.SubmitMessagePrepared {
        Content = content
        ConversationId = conv
        MessageId = Guid.NewGuid()
        Timestamp = DateTime.UtcNow
        WatchdogId = Guid.NewGuid()
    }

/// A full page with a question sent and its task on record.
let private pageSent () =
    let conv = Guid.NewGuid()
    let model, _ = AIAssistantUI.init ()
    let m = model |> page (prepared conv "how did sales do?")
    let t = aTask conv
    m |> page (AIAssistantUI.MessageSubmitted(ToolUp.Platform.Finished t)), conv, t

let private panelEvent e = panel (AIClientConfig.SSEEvent e)
let private pageEvent e = page (AIAssistantUI.SSEEvent e)

let private panelTask (m: AIClientConfig.OuterModel) =
    m.SidePanel.CurrentTask |> Option.map _.TaskId

let private pageTask (m: AIAssistantUI.Model) = m.CurrentTask |> Option.map _.TaskId

let private lastContent (messages: ConversationMessage list) =
    messages |> List.tryLast |> Option.map _.Content |> Option.defaultValue ""

let private divergenceTests =
    testList "the nine divergences, decided" [
        testCase "1 · a delta is correlated by the active conversation on both" (fun () ->
            let p, conv, _ = panelSent ()
            let p1 = p |> panelEvent (MessageDelta(Guid.NewGuid(), "not ours"))
            Expect.equal p1.SidePanel.Streaming "" "side panel ignores another conversation"
            let p2 = p |> panelEvent (MessageDelta(conv, "ours"))
            Expect.equal p2.SidePanel.Streaming "ours" "side panel takes its own"
            let f, fconv, _ = pageSent ()
            let f1 = f |> pageEvent (MessageDelta(Guid.NewGuid(), "not ours"))
            Expect.equal f1.StreamingContent "" "full page ignores another conversation"
            let f2 = f |> pageEvent (MessageDelta(fconv, "ours"))
            Expect.equal f2.StreamingContent "ours" "full page takes its own")

        testCase "2 · completion commits and clears the watchdog and the task on both" (fun () ->
            let p, conv, _ = panelSent ()

            let p1 =
                p
                |> panelEvent (MessageDelta(conv, "answer"))
                |> panelEvent (MessageComplete(conv, Guid.NewGuid()))

            Expect.equal (lastContent p1.SidePanel.Messages) "answer" "side panel commits"
            Expect.isNone p1.SidePanel.WatchdogToken "side panel clears the watchdog"
            Expect.isNone (panelTask p1) "side panel clears the task"
            let f, fconv, _ = pageSent ()

            let f1 =
                f
                |> pageEvent (MessageDelta(fconv, "answer"))
                |> pageEvent (MessageComplete(fconv, Guid.NewGuid()))

            Expect.equal (lastContent f1.Messages) "answer" "full page commits"
            Expect.isNone f1.WatchdogToken "full page clears the watchdog"
            Expect.isNone (pageTask f1) "full page clears the task")

        testCase "3 · a tool call writes no marker into the reply on either surface" (fun () ->
            let p, conv, _ = panelSent ()
            let p1 = p |> panelEvent (ToolCallStarted(conv, "query_facts", Guid.NewGuid()))
            Expect.equal p1.SidePanel.Streaming "" "side panel: the reply buffer is untouched"
            let f, fconv, _ = pageSent ()
            let f1 = f |> pageEvent (ToolCallStarted(fconv, "query_facts", Guid.NewGuid()))
            Expect.equal f1.StreamingContent "" "full page: the reply buffer is untouched"

            let f2 =
                f1
                |> pageEvent (MessageDelta(fconv, "done"))
                |> pageEvent (MessageComplete(fconv, Guid.NewGuid()))

            Expect.equal (lastContent f2.Messages) "done" "and the committed reply carries no marker")

        testCase "4 · a task failure reaches the reader without the debug display, and ends the turn" (fun () ->
            let p, _, t = panelSent ()
            let p1 = p |> panelEvent (TaskStatusChanged(t.TaskId, AITaskFailed "boom"))
            Expect.isTrue ((lastContent p1.SidePanel.Messages).Contains "boom") "side panel shows it"
            Expect.isNone p1.SidePanel.WatchdogToken "side panel ends the watch"
            Expect.isNone (panelTask p1) "side panel ends the task"
            let f, _, ft = pageSent ()
            let f1 = f |> pageEvent (TaskStatusChanged(ft.TaskId, AITaskFailed "boom"))
            Expect.isTrue (f1.ErrorMessage |> Option.exists _.Contains("boom")) "full page shows it"
            Expect.isNone f1.WatchdogToken "full page ends the watch"
            Expect.isNone (pageTask f1) "full page ends the task")

        testCase "5 · a stream error is shown, and the watch and the task stay live for the status poll" (fun () ->
            let p, _, t = panelSent ()
            let p1 = p |> panelEvent (StreamError "blip")
            Expect.isTrue ((lastContent p1.SidePanel.Messages).Contains "blip") "side panel shows it"
            Expect.isTrue p1.SidePanel.WatchdogToken.IsSome "side panel keeps the watch"
            Expect.equal (panelTask p1) (Some t.TaskId) "side panel keeps the task"
            let f, _, ft = pageSent ()
            let f1 = f |> pageEvent (StreamError "blip")
            Expect.isTrue (f1.ErrorMessage |> Option.exists _.Contains("blip")) "full page shows it"
            Expect.isTrue f1.WatchdogToken.IsSome "full page keeps the watch"
            Expect.equal (pageTask f1) (Some ft.TaskId) "full page keeps the task")

        testCase "6 · a cancel commits the partial reply and ends the turn on both" (fun () ->
            let p, conv, _ = panelSent ()

            let p1 =
                p
                |> panelEvent (MessageDelta(conv, "partial"))
                |> panelEvent (StreamCancelled conv)

            Expect.isTrue ((lastContent p1.SidePanel.Messages).StartsWith "partial") "side panel keeps the partial"
            Expect.isNone (panelTask p1) "side panel ends the task"
            let f, fconv, _ = pageSent ()

            let f1 =
                f
                |> pageEvent (MessageDelta(fconv, "partial"))
                |> pageEvent (StreamCancelled fconv)

            Expect.isTrue ((lastContent f1.Messages).StartsWith "partial") "full page keeps the partial"
            Expect.isNone (pageTask f1) "full page ends the task")

        testCase "7 · a verdict is a sign of life on both" (fun () ->
            let verdict: AnswerVerification = {
                Verified = 0
                Unmatched = 0
                Unverifiable = 0
                Numbers = []
                Mode = "Annotate"
            }

            let p, conv, _ = panelSent ()
            let p1 = p |> panelEvent (AnswerVerified(conv, verdict))
            Expect.isTrue (p1.SidePanel.WatchdogToken <> p.SidePanel.WatchdogToken) "side panel re-arms"
            let f, fconv, _ = pageSent ()
            let f1 = f |> pageEvent (AnswerVerified(fconv, verdict))
            Expect.isTrue (f1.WatchdogToken <> f.WatchdogToken) "full page re-arms")

        testCase "8 · the watchdog firing with a task on record asks the server first, on both" (fun () ->
            let p, _, _ = panelSent ()
            let token = p.SidePanel.WatchdogToken.Value
            let p1 = p |> panel (AIClientConfig.WatchdogFired token)
            Expect.equal p1.SidePanel.WatchdogToken (Some token) "side panel: the watch is still live"
            Expect.equal p1.SidePanel.Messages.Length p.SidePanel.Messages.Length "side panel: no note yet"
            let f, _, _ = pageSent ()
            let ftoken = f.WatchdogToken.Value
            let f1 = f |> page (AIAssistantUI.WatchdogFired ftoken)
            Expect.equal f1.WatchdogToken (Some ftoken) "full page: the watch is still live"
            Expect.equal f1.Messages.Length f.Messages.Length "full page: no note yet")

        testCase "9 · one debug command grammar on both" (fun () ->
            let p =
                idlePanel ()
                |> panel (AIClientConfig.Send("module", None, None, "  DEBUG ON  "))

            Expect.isTrue p.SidePanel.DebugMode "side panel"
            let model, _ = AIAssistantUI.init ()
            let f = model |> page (prepared (Guid.NewGuid()) "  DEBUG ON  ")
            Expect.isTrue f.DebugMode "full page")

        testCase "1131.t3 · an attached block rides the in-flight reply into the committed message" (fun () ->
            let part = ToolUp.Platform.AI.TextPart "a block"
            let p, conv, _ = panelSent ()

            let p1 =
                p
                |> panelEvent (MessageDelta(conv, "see:"))
                |> panelEvent (BlockAttached(conv, part))
                |> panelEvent (MessageComplete(conv, Guid.NewGuid()))

            Expect.equal (p1.SidePanel.Messages |> List.last |> _.Parts) [ part ] "side panel"
            let f, fconv, _ = pageSent ()

            let f1 =
                f
                |> pageEvent (MessageDelta(fconv, "see:"))
                |> pageEvent (BlockAttached(fconv, part))
                |> pageEvent (MessageComplete(fconv, Guid.NewGuid()))

            Expect.equal (f1.Messages |> List.last |> _.Parts) [ part ] "full page")
    ]

// ─── An async fast-path resolver (the seam 1003 asked for) ───────────

let private request: FastPathBridge.FastPathRequest = {
    ConversationId = Guid.NewGuid()
    Instruction = "go to sales"
    ActiveModule = None
    ActivePage = None
}

let private answered =
    FastPathBridge.Resolved(ConversationMessage.note AIAssistant request.ConversationId "Done.", ignore, ignore)

let private isResolved =
    function
    | FastPathBridge.Resolved _ -> true
    | FastPathBridge.NoResolution -> false

/// Resolve once under `install`, and assert on the outcome `delayMs` later.
let private resolvedAfter (delayMs: int) (install: unit -> unit) (check: FastPathBridge.ResolveOutcome option -> unit) =
    testCaseDeferred "" delayMs (fun () ->
        install ()
        let mutable outcome = None

        Async.StartImmediate(
            async {
                let! o = FastPathBridge.resolve request
                outcome <- Some o
            }
        )

        fun () ->
            FastPathBridge.setResolver None
            check outcome)

let private named (name: string) (item: TestItem) =
    match item with
    | DeferredCase(_, delayMs, setup) -> DeferredCase(name, delayMs, setup)
    | other -> other

let private resolverTests =
    testList "the fast-path bridge awaits an async resolver" [
        resolvedAfter
            50
            (fun () ->
                FastPathBridge.setAsyncResolver (
                    Some(fun _ -> async {
                        do! Async.Sleep 10
                        return answered
                    })
                ))
            (fun outcome -> Expect.isTrue (outcome |> Option.exists isResolved) "the async answer is taken")
        |> named "an async resolver's answer is awaited without blocking"

        resolvedAfter 50 (fun () -> FastPathBridge.setResolver (Some(fun _ -> answered))) (fun outcome ->
            Expect.isTrue (outcome |> Option.exists isResolved) "the sync answer is taken")
        |> named "a synchronous resolver keeps working through the same seam"

        resolvedAfter
            50
            (fun () -> FastPathBridge.setAsyncResolver (Some(fun _ -> async { return failwith "resolver down" })))
            (fun outcome ->
                Expect.isTrue (outcome |> Option.exists (isResolved >> not)) "a failing resolver falls through")
        |> named "a resolver that fails falls through to the model path"

        resolvedAfter
            (FastPathBridge.ResolveTimeoutMs + 200)
            (fun () ->
                FastPathBridge.setAsyncResolver (
                    Some(fun _ -> async {
                        do! Async.Sleep(FastPathBridge.ResolveTimeoutMs * 3)
                        return answered
                    })
                ))
            (fun outcome ->
                Expect.isTrue (outcome |> Option.exists (isResolved >> not)) "a slow resolver falls through")
        |> named "a resolver slower than the stated timeout falls through to the model path"
    ]

let tests =
    testList "Phase 1080 — the chat stream" [ oneStreamTests; divergenceTests; resolverTests ]