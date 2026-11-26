// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.AIScreenEffectsTests

// ─── The AI screens state their effects ──────────────────────────────
//
// Each AI screen's `update` returns its remote calls as `RemoteEffect` values
// in a `Fuaran.MVU` step list. A case here drives one transition and states the effect values it
// returns: the method each call names, its argument and, where a
// transition asks for several, their order; a legacy body by its site
// label; a follow-up by its message. A call's proxy and reply take no part
// in its equality, so the expected values are built with neither. Nothing
// is performed: a case only builds values.

open System
open Fuaran.MVU
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AI.Client
open ToolUp.AI.Client.Tests.NodeTest

let private noCall () : Async<'T> = async { return failwith "not called in this test" }

/// The expected value of a call: its proxy and reply take no part in
/// equality, so any will do.
let private call (methodName: string) (arg: 'a) : RemoteEffect<'msg> =
    RemoteEffect.call methodName (fun (_: 'a) -> noCall ()) arg (fun (_: Result<obj, exn>) -> Unchecked.defaultof<'msg>)

/// The expected value of a signal: it compares by its site label.
let private signal (site: string) : RemoteEffect<'msg> = RemoteEffect.signal site ignore

/// What kind of effect a value is, and the name or site it carries.
let private shape (effect: RemoteEffect<'msg>) : string * string =
    match effect with
    | Invoke call -> "call", call.Method
    | Await operation -> "await", operation.Method
    | Legacy body -> "legacy", body.Site
    | Fire body -> "signal", body.Site

let private effects (steps: Step<'msg, RemoteEffect<'msg>> list) : RemoteEffect<'msg> list = Step.effects steps

let private followUps (steps: Step<'msg, RemoteEffect<'msg>> list) : 'msg list =
    steps
    |> List.choose (function
        | Then msg -> Some msg
        | Do _ -> None)

let private same (actual: 'T) (expected: 'T) (message: string) : unit =
    Expect.isTrue (actual = expected) (sprintf "%s\n  actual:   %A\n  expected: %A" message actual expected)

// ─── Screens ─────────────────────────────────────────────────────────

let private platformKeysTests =
    testList "PlatformAIKeysAdminUI" [
        testCase "init reads descriptors, platform statuses and teams, in that order"
        <| fun () ->
            let _, steps = PlatformAIKeysAdminUI.init ()

            same
                (effects steps)
                [
                    call "PlatformAIKeysApi.ListPlatformDescriptors" ()
                    call "PlatformAIKeysApi.ListPlatformKeyStatuses" ()
                    call "PlatformAIKeysApi.ListTeams" ()
                ]
                "three reads, in the order the runtime performs them"

        testCase "a finished action with a team selected refreshes platform, then team"
        <| fun () ->
            let m0, _ = PlatformAIKeysAdminUI.init ()

            let m1, _ =
                PlatformAIKeysAdminUI.update (PlatformAIKeysAdminUI.SelectTeam(Some "t1")) m0

            let _, steps =
                PlatformAIKeysAdminUI.update (PlatformAIKeysAdminUI.ActionFinished(Ok(), "done")) m1

            same
                (effects steps)
                [
                    call "PlatformAIKeysApi.ListPlatformKeyStatuses" ()
                    call "PlatformAIKeysApi.ListTeamKeyStatuses" "t1"
                ]
                "two refreshes, platform first"

        testCase "a team key delete names the team and provider"
        <| fun () ->
            let m0, _ = PlatformAIKeysAdminUI.init ()

            let _, steps =
                PlatformAIKeysAdminUI.update (PlatformAIKeysAdminUI.DeleteTeamKey("t1", "anthropic")) m0

            same (effects steps) [ call "PlatformAIKeysApi.DeleteTeamKey" ("t1", "anthropic") ] "one delete"
    ]

let private settingsTests =
    testList "AISettingsUI" [
        testCase "init reads the four settings sources in order"
        <| fun () ->
            let _, steps = AISettingsUI.init ()

            same
                (effects steps)
                [
                    call "AISettingsApi.ListAvailable" ()
                    call "AISettingsApi.GetPlatformDescriptors" ()
                    call "PlatformAIKeysApi.ListPlatformKeyStatuses" ()
                    call "AISettingsApi.GetMyConfig" ()
                ]
                "four reads, in the order the runtime performs them"

        testCase "a delete names its label; a finished save re-reads the config"
        <| fun () ->
            let m0, _ = AISettingsUI.init ()
            let _, deleted = AISettingsUI.update (AISettingsUI.DeleteInstance "work") m0
            same (effects deleted) [ call "AISettingsApi.DeleteInstance" "work" ] "one delete"

            let _, saved = AISettingsUI.update (AISettingsUI.SaveFinished(Ok())) m0
            same (effects saved) [ call "AISettingsApi.GetMyConfig" () ] "one config read"
    ]

let private narrativeTests =
    testList "NarrativePromptAdminUI" [
        testCase "init lists the narratives"
        <| fun () ->
            let _, steps = NarrativePromptAdminUI.init ()
            same (effects steps) [ call "NarrativePromptApi.ListNarratives" () ] "one list read"

        testCase "opening a narrative reads it, its versions and its proposals, in order"
        <| fun () ->
            let m0, _ = NarrativePromptAdminUI.init ()

            let _, steps =
                NarrativePromptAdminUI.update (NarrativePromptAdminUI.OpenNarrative "summary") m0

            same
                (effects steps)
                [
                    call "NarrativePromptApi.GetNarrative" "summary"
                    call "NarrativePromptApi.ListVersions" "summary"
                    call "NarrativePromptApi.ListProposals" "summary"
                ]
                "three reads, in the order the runtime performs them"

        testCase "a dismissal names its decision"
        <| fun () ->
            let m0, _ = NarrativePromptAdminUI.init ()

            let decision: NarrativeProposalDecision = {
                RunKey = "summary"
                ProposalId = "p-1"
            }

            let _, steps =
                NarrativePromptAdminUI.update (NarrativePromptAdminUI.DismissProposal decision) m0

            same (effects steps) [ call "NarrativePromptApi.DismissProposal" decision ] "one dismissal"
    ]

let private task (conv: Guid) : AITask = {
    TaskId = Guid.NewGuid()
    ConversationId = conv
    Prompt = "how did sales do?"
    Status = InProgress
    CreatedAt = DateTime.UtcNow
    CompletedAt = None
}

let private prepared (conv: Guid) : AIAssistantUI.PreparedSubmission = {
    Content = "how did sales do?"
    ConversationId = conv
    MessageId = Guid.NewGuid()
    Timestamp = DateTime.UtcNow
    WatchdogId = Guid.NewGuid()
}

let private assistantTests =
    testList "AIAssistantUI" [
        testCase "init reads the first page, the tools and the providers, in order"
        <| fun () ->
            let _, steps = AIAssistantUI.init ()

            same
                (effects steps)
                [
                    call "AIAssistantApi.ListConversationsPage" ConversationListQuery.firstPage
                    call "AIAssistantApi.GetAvailableTools" ()
                    call "AISettingsApi.GetMyConfig" ()
                ]
                "three reads, in the order the runtime performs them"

        testCase "a prepared submission arms the watchdog, then asks the fast path"
        <| fun () ->
            let m0, _ = AIAssistantUI.init ()

            let p = prepared (Guid.NewGuid())
            let _, steps = AIAssistantUI.update (AIAssistantUI.SubmitMessagePrepared p) m0

            same
                (effects steps |> List.map shape)
                [ "await", "ChatStream.watchdogDelay"; "await", "FastPathBridge.resolve" ]
                "the watchdog first, in the order the runtime performs them"

            same
                (List.head (effects steps))
                (RemoteEffect.await "ChatStream.watchdogDelay" (fun (_: Guid) -> noCall ()) p.WatchdogId (fun _ ->
                    Unchecked.defaultof<AIAssistantUI.Msg>))
                "the watchdog names its token"

        testCase "a firing with a task in flight polls the task's status"
        <| fun () ->
            let conv = Guid.NewGuid()
            let p = prepared conv
            let m0, _ = AIAssistantUI.init ()
            let m1, _ = AIAssistantUI.update (AIAssistantUI.SubmitMessagePrepared p) m0
            let running = task conv

            let m2, _ =
                AIAssistantUI.update (AIAssistantUI.MessageSubmitted(Finished running)) m1

            let _, steps = AIAssistantUI.update (AIAssistantUI.WatchdogFired p.WatchdogId) m2

            same (effects steps) [ call "AIAssistantApi.GetTaskStatus" running.TaskId ] "one status read"

        testCase "a deleted conversation reloads the list as a follow-up"
        <| fun () ->
            let m0, _ = AIAssistantUI.init ()

            let _, steps =
                AIAssistantUI.update (AIAssistantUI.DeleteConversation(Finished(Ok()))) m0

            same (effects steps) [] "no effect of its own"

            let reloads =
                match followUps steps with
                | [ AIAssistantUI.LoadConversations(Start()) ] -> true
                | _ -> false

            Expect.isTrue reloads "one follow-up: the list reload"

        testCase "a cancel is one signal"
        <| fun () ->
            let m0, _ = AIAssistantUI.init ()
            let _, steps = AIAssistantUI.update (AIAssistantUI.CancelTask(Guid.NewGuid())) m0
            same (effects steps) [ signal "AIAssistantUI.cancel" ] "the cancel POST, by its site"
    ]

let tests =
    testList "AI screens as effect values" [ platformKeysTests; settingsTests; narrativeTests; assistantTests ]