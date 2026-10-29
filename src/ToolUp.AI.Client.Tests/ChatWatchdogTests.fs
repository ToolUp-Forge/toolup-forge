// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1140 — the chat watchdog measures idleness, not elapsed time.
module ToolUp.AI.Client.Tests.ChatWatchdogTests

open System
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AI.Client
open ToolUp.AI.Client.Tests.NodeTest

// ─── What these pin ──────────────────────────────────────────────────
//
// Both chat surfaces arm a 60-second watchdog when the user sends. It used
// to be armed once, so a turn that streamed or worked through tool calls
// for longer than 60 seconds was reported as dropped and then completed
// beneath the note. Every event of the live turn now replaces the token and
// schedules a fresh firing; the existing token comparison discards the
// firing the previous token had pending.
//
// The commands `update` returns are not executed here, and no clock runs:
// the passage of the timeout is modelled by feeding the firing a token's
// timer would deliver. A turn that streams past the timeout is therefore a
// run of events each followed by the firing of the token it superseded; a
// turn that falls silent is the firing of the token still live.

let private noteMarker = "No response after 60 seconds"

let private liveTurn = Guid.NewGuid()

// ─── The side panel ──────────────────────────────────────────────────

let private updateOuter =
    let modules, config = ComposerScope.testComposition ()
    AIClientConfig.composerUpdate config Unchecked.defaultof<IModuleQueryBus> modules

let private panel (msg: AIClientConfig.SidePanelMsg) (model: AIClientConfig.OuterModel) =
    updateOuter (AIClientConfig.SidePanelMsg msg) model |> fst

let private panelEvent (event: AIStreamEvent) = panel (AIClientConfig.SSEEvent event)

let private idlePanel () : AIClientConfig.OuterModel = {
    Shell = ComposerScope.shellModel0 ()
    SidePanel = AIClientConfig.composerSidePanelInit ()
}

let private ask = AIClientConfig.Send("module", None, None, "how did sales do?")

/// A side panel with a question sent: the watchdog armed for the first time.
let private sent () : AIClientConfig.OuterModel = idlePanel () |> panel ask

let private panelToken (model: AIClientConfig.OuterModel) = model.SidePanel.WatchdogToken

let private panelConversation (model: AIClientConfig.OuterModel) = model.SidePanel.ConversationId

let private panelNoted (model: AIClientConfig.OuterModel) =
    model.SidePanel.Messages |> List.exists _.Content.Contains(noteMarker)

/// Fire the watchdog with `token`, as its timer would after the timeout.
let private panelFire (token: Guid option) (model: AIClientConfig.OuterModel) =
    model |> panel (AIClientConfig.WatchdogFired token.Value)

let private panelTests =
    testList "side panel" [
        testCase "a turn streaming past the timeout in total, with no gap of the timeout, gets no note" (fun () ->
            let m0 = sent ()
            let conv = panelConversation m0
            let t0 = panelToken m0
            Expect.isTrue t0.IsSome "sending arms the watchdog"

            // Each event restarts the clock; the firing of the token it
            // replaced then arrives and must find nothing to report.
            let m1 = m0 |> panelEvent (MessageDelta(conv, "Sales rose "))
            let t1 = panelToken m1
            Expect.isTrue (t1.IsSome && t1 <> t0) "a delta replaces the live token"
            let m1 = m1 |> panelFire t0
            Expect.isFalse (panelNoted m1) "the superseded firing is not a note"
            Expect.equal (panelToken m1) t1 "and it leaves the live token alone"

            let m2 = m1 |> panelEvent (ToolCallStarted(conv, "query_facts", Guid.NewGuid()))
            let t2 = panelToken m2
            Expect.isTrue (t2.IsSome && t2 <> t1) "a tool call starting replaces the live token"
            let m2 = m2 |> panelFire t1
            Expect.isFalse (panelNoted m2) "the superseded firing is not a note"

            let m3 = m2 |> panelEvent (ToolCallCompleted(conv, Guid.NewGuid(), "{}"))
            let t3 = panelToken m3
            Expect.isTrue (t3.IsSome && t3 <> t2) "a tool call completing replaces the live token"
            let m3 = m3 |> panelFire t2
            Expect.isFalse (panelNoted m3) "the superseded firing is not a note"

            let m4 = m3 |> panelEvent (MessageDelta(conv, "by 4%."))
            let m4 = m4 |> panelFire t3
            Expect.isFalse (panelNoted m4) "the superseded firing is not a note"

            let m5 = m4 |> panelEvent (MessageComplete(conv, Guid.NewGuid()))
            Expect.isFalse (panelNoted m5) "the turn completed without a dropped-connection note"
            Expect.equal m5.SidePanel.Messages.Length 2 "the question and its answer"
            Expect.isNone (panelToken m5) "completing clears the watchdog")

        testCase "a tool call re-arms the watchdog with the debug display on as well as off" (fun () ->
            let debugOn =
                idlePanel () |> panel (AIClientConfig.Send("module", None, None, "Debug on"))

            Expect.isTrue debugOn.SidePanel.DebugMode "the debug display is on"

            let m0 = debugOn |> panel ask
            let conv = panelConversation m0
            let t0 = panelToken m0
            let m1 = m0 |> panelEvent (ToolCallStarted(conv, "query_facts", Guid.NewGuid()))
            let t1 = panelToken m1
            Expect.isTrue (t1.IsSome && t1 <> t0) "the call replaces the live token"
            Expect.isTrue (m1.SidePanel.Messages.Length > m0.SidePanel.Messages.Length) "and it is still displayed"

            let m2 = m1 |> panelEvent (ToolCallCompleted(conv, Guid.NewGuid(), "{}"))
            Expect.isTrue (panelToken m2 <> t1) "the result replaces it again")

        testCase "a turn that falls silent for the timeout gets the note" (fun () ->
            let m0 = sent ()
            let conv = panelConversation m0
            let m1 = m0 |> panelEvent (MessageDelta(conv, "Sales rose "))

            let m2 = m1 |> panelFire (panelToken m1)
            Expect.isTrue (panelNoted m2) "the live token's firing is the note"
            Expect.isNone (panelToken m2) "and it ends the watch"
            Expect.equal m2.SidePanel.Streaming "" "and the partial text is dropped")

        testCase "a firing whose token was superseded by a re-arm is ignored" (fun () ->
            let m0 = sent ()
            let conv = panelConversation m0
            let t0 = panelToken m0
            let m1 = m0 |> panelEvent (MessageDelta(conv, "Sales rose "))
            let m2 = m1 |> panelFire t0
            Expect.isTrue (obj.ReferenceEquals(m2.SidePanel, m1.SidePanel)) "the model is untouched"
            Expect.isTrue (panelToken m2 = panelToken m1) "the re-armed token is still live")

        testCase "an event of another conversation does not re-arm the watchdog" (fun () ->
            let m0 = sent ()
            let m1 = m0 |> panelEvent (MessageDelta(Guid.NewGuid(), "someone else's answer"))
            Expect.isTrue (panelToken m1 = panelToken m0) "the live token is unchanged")

        testCase "an event with no turn in flight arms nothing" (fun () ->
            let idle = idlePanel ()
            let m1 = idle |> panelEvent (MessageDelta(panelConversation idle, "late text"))

            Expect.isNone (panelToken m1) "a late delta does not start a watch")
    ]

// ─── The assistant page ──────────────────────────────────────────────

let private page (msg: AIAssistantUI.Msg) (model: AIAssistantUI.Model) = AIAssistantUI.update msg model |> fst

let private pageEvent (event: AIStreamEvent) = page (AIAssistantUI.SSEEvent event)

let private pageToken (model: AIAssistantUI.Model) = model.WatchdogToken

let private pageNoted (model: AIAssistantUI.Model) =
    model.Messages |> List.exists _.Content.Contains(noteMarker)

let private pageFire (token: Guid option) (model: AIAssistantUI.Model) =
    model |> page (AIAssistantUI.WatchdogFired token.Value)

let private task (conv: Guid) : AITask = {
    TaskId = Guid.NewGuid()
    ConversationId = conv
    Prompt = "how did sales do?"
    Status = InProgress
    CreatedAt = DateTime.UtcNow
    CompletedAt = None
}

/// An assistant page with a question sent on `conv`: the watchdog armed.
let private pageSent (conv: Guid) : AIAssistantUI.Model =
    let model, _ = AIAssistantUI.init ()

    model
    |> page (
        AIAssistantUI.SubmitMessagePrepared {
            Content = "how did sales do?"
            ConversationId = conv
            MessageId = Guid.NewGuid()
            Timestamp = DateTime.UtcNow
            WatchdogId = Guid.NewGuid()
        }
    )

let private pageTests =
    testList "assistant page" [
        testCase "a turn streaming past the timeout in total, with no gap of the timeout, gets no note" (fun () ->
            let conv = liveTurn
            let m0 = pageSent conv
            let t0 = pageToken m0
            Expect.isTrue t0.IsSome "sending arms the watchdog"

            let m1 = m0 |> pageEvent (MessageDelta(conv, "Sales rose "))
            let t1 = pageToken m1
            Expect.isTrue (t1.IsSome && t1 <> t0) "a delta replaces the live token"
            let m1 = m1 |> pageFire t0
            Expect.isFalse (pageNoted m1) "the superseded firing is not a note"
            Expect.equal (pageToken m1) t1 "and it leaves the live token alone"

            let m2 = m1 |> pageEvent (ToolCallStarted(conv, "query_facts", Guid.NewGuid()))
            let t2 = pageToken m2
            Expect.isTrue (t2.IsSome && t2 <> t1) "a tool call starting replaces the live token"
            let m2 = m2 |> pageFire t1
            Expect.isFalse (pageNoted m2) "the superseded firing is not a note"

            let m3 = m2 |> pageEvent (ToolCallCompleted(conv, Guid.NewGuid(), "{}"))
            let t3 = pageToken m3
            Expect.isTrue (t3.IsSome && t3 <> t2) "a tool call completing replaces the live token"
            let m3 = m3 |> pageFire t2
            Expect.isFalse (pageNoted m3) "the superseded firing is not a note"

            let m4 = m3 |> pageEvent (TaskStatusChanged(Guid.NewGuid(), InProgress))
            Expect.isTrue (pageToken m4 <> t3) "a status of the live task replaces the live token"
            let m4 = m4 |> pageFire t3
            Expect.isFalse (pageNoted m4) "the superseded firing is not a note"

            let m5 = m4 |> pageEvent (MessageDelta(conv, "by 4%."))
            let m5 = m5 |> pageFire (pageToken m4)
            Expect.isFalse (pageNoted m5) "the superseded firing is not a note"

            let m6 = m5 |> pageEvent (MessageComplete(conv, Guid.NewGuid()))
            Expect.isFalse (pageNoted m6) "the turn completed without a dropped-connection note"
            Expect.equal m6.Messages.Length 2 "the question and its answer"
            Expect.isNone (pageToken m6) "completing clears the watchdog")

        testCase "a tool call re-arms the watchdog whether or not the debug display is on" (fun () ->
            let conv = liveTurn
            let off = pageSent conv
            let off' = off |> pageEvent (ToolCallCompleted(conv, Guid.NewGuid(), "{}"))
            Expect.isTrue (pageToken off' <> pageToken off) "re-armed with the debug display off"
            Expect.equal off'.Messages.Length off.Messages.Length "and nothing is displayed"

            let on = { off with DebugMode = true }
            let on' = on |> pageEvent (ToolCallCompleted(conv, Guid.NewGuid(), "{}"))
            Expect.isTrue (pageToken on' <> pageToken on) "re-armed with the debug display on"
            Expect.equal on'.Messages.Length (on.Messages.Length + 1) "and the result is displayed")

        testCase "a status that ends the task does not extend the watch" (fun () ->
            let conv = liveTurn
            let m0 = pageSent conv
            let m1 = m0 |> pageEvent (TaskStatusChanged(Guid.NewGuid(), AITaskCompleted))
            Expect.isTrue (pageToken m1 = pageToken m0) "the live token is unchanged")

        testCase "a turn that falls silent for the timeout gets the note" (fun () ->
            let conv = liveTurn
            let m0 = pageSent conv
            let m1 = m0 |> pageEvent (MessageDelta(conv, "Sales rose "))

            let m2 = m1 |> pageFire (pageToken m1)
            Expect.isTrue (pageNoted m2) "the live token's firing is the note"
            Expect.isNone (pageToken m2) "and it ends the watch"
            Expect.equal m2.StreamingContent "" "and the partial text is dropped")

        testCase "a silent turn the server no longer knows gets the note after the status poll" (fun () ->
            let conv = liveTurn
            let m0 = pageSent conv
            let m1 = m0 |> page (AIAssistantUI.MessageSubmitted(Finished(task conv)))
            let m2 = m1 |> pageEvent (MessageDelta(conv, "Sales rose "))
            let live = pageToken m2

            // With a task on record the firing asks the server first.
            let m3 = m2 |> pageFire live
            Expect.isFalse (pageNoted m3) "the firing alone is not yet the note"
            Expect.equal (pageToken m3) live "the watch is still live"

            let m4 = m3 |> page (AIAssistantUI.TaskStatusPolled(live.Value, None))
            Expect.isTrue (pageNoted m4) "an unknown task is the note"
            Expect.isNone (pageToken m4) "and it ends the watch")

        testCase "a firing whose token was superseded by a re-arm is ignored" (fun () ->
            let conv = liveTurn
            let m0 = pageSent conv
            let t0 = pageToken m0
            let m1 = m0 |> pageEvent (MessageDelta(conv, "Sales rose "))
            let m2 = m1 |> pageFire t0
            Expect.isTrue (obj.ReferenceEquals(m2, m1)) "the model is untouched"
            Expect.isTrue (pageToken m2 = pageToken m1) "the re-armed token is still live")

        testCase "an event of another conversation does not re-arm the watchdog" (fun () ->
            let m0 = pageSent liveTurn
            let m1 = m0 |> pageEvent (MessageDelta(Guid.NewGuid(), "someone else's answer"))
            Expect.isTrue (pageToken m1 = pageToken m0) "the live token is unchanged")
    ]

let tests =
    testList "Phase 1140 — the chat watchdog measures idleness" [ panelTests; pageTests ]