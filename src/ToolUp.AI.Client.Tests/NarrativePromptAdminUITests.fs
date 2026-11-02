module ToolUp.AI.Client.Tests.NarrativePromptAdminUITests

open System
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI
open ToolUp.AI.Client.NarrativePromptAdminUI

// ─── Phase 1177 — the report admin's proposal panel ──────────────────
//
// Drives the panel's pure decisions — which proposal may be approved, how
// each reads, whether an approved sentence stopped its class recurring —
// and the messages that carry an approval or a dismissal. Fable-side for
// the reason `JobsAdminUITests` gives: the module declares a module-level
// `Api.makeProxy`. The commands `update` returns are not executed here.

let private at = DateTime(2026, 10, 31, 6, 0, 0, DateTimeKind.Utc)

let private proposal (version: int) (state: NarrativeProposalState) : NarrativeWordingProposal = {
    Id = "p-1"
    Class = NarrativeOffenceClass.UnreferencedNumber
    Sentence = "Cite every figure."
    WordingVersion = version
    Evidence = [ "Revenue rose 12 percent." ]
    Earlier = []
    ProposedAt = at
    State = state
    DecidedBy = None
    DecidedAt = None
}

let private standing (runs: int) (withClass: int) (recurs: bool) : NarrativeOffenceStanding = {
    Class = NarrativeOffenceClass.UnreferencedNumber
    WordingVersion = 1
    Runs = runs
    RunsWithClass = withClass
    Recurs = recurs
}

let private viewOf (current: int option) : NarrativePromptView = {
    RunKey = "summary"
    DisplayName = "Summary"
    Title = "Summary"
    DeclaredPrompt = "Write it."
    DeclaredSections = []
    Current =
        current
        |> Option.map (fun version -> {
            Version = version
            IsReset = false
            Prompt = "Write it well."
            Guidance = []
            Author = "olive"
            SavedAt = at
        })
    LatestVersion = defaultArg current 0
    Rules = ""
    LastRun = None
}

let private learning (proposals: NarrativeWordingProposal list) : NarrativeLearningView = {
    RunKey = "summary"
    Proposals =
        proposals
        |> List.map (fun p -> {
            Proposal = p
            ResultingPrompt = "Write it.\n" + p.Sentence
            FollowThrough = None
        })
    Standing = []
    Threshold = 3
    Window = 5
}

let tests =
    testList "NarrativePromptAdminUI proposals (Phase 1177)" [
        testCase "an open proposal drafted against the wording in force can be approved"
        <| fun () ->
            Expect.isTrue (canApprove 0 (proposal 0 NarrativeProposalState.Open)) "same version"
            Expect.equal (proposalStatus 0 (proposal 0 NarrativeProposalState.Open)) "Waiting for approval" "reads"

        testCase "an open proposal whose wording has changed since can only be dismissed"
        <| fun () ->
            let stale = proposal 0 NarrativeProposalState.Open
            Expect.isFalse (canApprove 2 stale) "another version is in force"
            Expect.isTrue ((proposalStatus 2 stale).Contains "can only be dismissed") "says so"

        testCase "a decided proposal cannot be approved, and says how it was decided"
        <| fun () ->
            for state, expected in
                [
                    NarrativeProposalState.Approved 1, "Approved as version 1"
                    NarrativeProposalState.Dismissed, "Dismissed"
                    NarrativeProposalState.Lapsed, "Lapsed: the wording changed before it was decided"
                ] do
                Expect.isFalse (canApprove 0 (proposal 0 state)) "decided"
                Expect.equal (proposalStatus 0 (proposal 0 state)) expected "reads"

        testCase "the version in force is the saved one, else the declaration's 0"
        <| fun () ->
            Expect.equal (versionInForce (viewOf None)) 0 "the declaration"
            Expect.equal (versionInForce (viewOf (Some 3))) 3 "a saved version"

        testCase "only open proposals are open"
        <| fun () ->
            let page =
                learning [
                    proposal 0 NarrativeProposalState.Open
                    proposal 0 NarrativeProposalState.Dismissed
                ]

            Expect.equal (openProposals page |> List.length) 1 "one open"

        testCase "the follow-through says whether the sentence stopped the class recurring"
        <| fun () ->
            Expect.equal (stoppedRecurring 5 (standing 2 0 false)) None "too few runs to say"
            Expect.equal (stoppedRecurring 5 (standing 5 1 false)) (Some true) "a full window without recurring"
            Expect.equal (stoppedRecurring 5 (standing 3 3 true)) (Some false) "it still recurs"
            Expect.equal (followThroughText 5 (standing 0 0 false)) "Not run since approval" "not run"
            Expect.isTrue ((followThroughText 5 (standing 5 1 false)).Contains "stopped recurring") "stopped"
            Expect.isTrue ((followThroughText 5 (standing 3 3 true)).Contains "still recurs") "still recurs"

        testCase "loading the panel, approving and dismissing"
        <| fun () ->
            let model, _ = init ()
            let page = learning [ proposal 0 NarrativeProposalState.Open ]

            let loaded, _ = update (LearningLoaded(Ok page)) model
            Expect.equal loaded.Learning (Some page) "the panel shows"

            let absent, _ = update (LearningLoaded(Error "not composed")) loaded
            Expect.equal absent.Learning None "no learning loop composed: no panel, no error"
            Expect.equal absent.Banner None "and no banner"

            let decision = {
                RunKey = "summary"
                ProposalId = "p-1"
            }

            let approving, _ = update (ApproveProposal decision) loaded
            Expect.isTrue approving.Busy "an approval is in flight"

            let dismissedPage = learning [ proposal 0 NarrativeProposalState.Dismissed ]
            let dismissing, _ = update (DismissProposal decision) loaded
            Expect.isTrue dismissing.Busy "a dismissal is in flight"

            let dismissed, _ = update (ProposalDismissed(Ok dismissedPage)) dismissing
            Expect.isFalse dismissed.Busy "done"
            Expect.equal dismissed.Learning (Some dismissedPage) "the panel shows the decision"

            let refused, _ = update (ProposalDismissed(Error "already decided")) dismissing
            Expect.equal refused.Banner (Some(Error "already decided")) "a refusal is shown"
    ]