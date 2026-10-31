// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NarrativeOffenceHistoryTests

// ─── Phase 1177 — a grounded run learns from refusals that recur ──────
//
// The pure half: the classifier, table-tested against every
// `GroundingOffenceReason` and against the audit-trail line each one is
// written as; the recurrence rule (3 of 5 recurs, 2 of 5 does not, a new
// wording version starts afresh, a dismissed class stays quiet until it
// recurs under a later version); and the drafting helpers.
//
// The composed half runs the REAL grounded run, decorated by
// `NarrativeOffenceLearning.compose`, over a gate that refuses any digit or
// number word in prose and a model that answers every narrative with a
// figure. It pins the operator's rule that a proposal is never in force:
// a run made after a proposal is saved sends the wording in force, and only
// an Owner or Admin approving it changes what the next run sends.

open System
open System.Collections.Concurrent
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Narrative
open ToolUp.Platform.StorageScopeResolver
open ToolUp.AI
open ToolUp.AI.GroundedNarrativeRun
open ToolUp.AI.NarrativeOffenceHistory
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ── Pure fixtures ────────────────────────────────────────────────

let private location = "section 'summary' › element 1 › span 1"

let private offence (reason: GroundingOffenceReason) (claim: string) : GroundingOffence = {
    Location = location
    Claim = claim
    Reason = reason
}

let private at (day: int) =
    DateTime(2026, 10, day, 6, 0, 0, DateTimeKind.Utc)

let private policy = NarrativeLearningPolicy.defaults

let private found (cls: NarrativeOffenceClass) : NarrativeOffence = {
    Class = cls
    Section = "summary"
    ElementKind = "prose"
    Location = location
    Claim = sprintf "claim of %A" cls
    Repaired = false
}

/// A run under `version` that refused `classes` (a clean run when empty).
let private run (day: int) (version: int) (classes: NarrativeOffenceClass list) : NarrativeOffenceRun = {
    RanAt = at day
    WordingVersion = version
    Outcome = (if List.isEmpty classes then "published" else "refused")
    Offences = classes |> List.map found
}

let private recordAll (runs: NarrativeOffenceRun list) (learning: NarrativeLearning) =
    runs |> List.fold (fun l r -> NarrativeLearning.record policy r l) learning

let private unref = NarrativeOffenceClass.UnreferencedNumber

let private proposal (cls: NarrativeOffenceClass) (version: int) (state: NarrativeProposalState) = {
    Id = Guid.NewGuid().ToString "N"
    Class = cls
    Sentence = "Cite every figure you state."
    WordingVersion = version
    Evidence = []
    Earlier = []
    ProposedAt = at 20
    State = state
    DecidedBy = None
    DecidedAt = None
}

let private classifierTests =
    testList "the classifier" [
        test "every gate reason maps to its class" {
            let table = [
                UnreferencedNumber, "Revenue rose 12 percent.", NarrativeOffenceClass.UnreferencedNumber
                UnreferencedNumber, "Revenue rose by three points.", NarrativeOffenceClass.NumberWord
                UnreferencedNumber, "Sales on B2B were strong.", NarrativeOffenceClass.NameReadAsFigure
                MetricWithoutReference, "revenue = 1,250", NarrativeOffenceClass.UnreferencedNumber
                UnresolvedReference "f-missing", "revenue = 1,250", NarrativeOffenceClass.MisattributedReference
                UnregisteredReferenceKind("budget", "budget:q"),
                "budget = 9",
                NarrativeOffenceClass.MisattributedReference
                SupersededReference("f-old", Some "f-new"), "revenue = 1,000", NarrativeOffenceClass.Superseded
                SupersededReference("f-old", None), "revenue = 1,000", NarrativeOffenceClass.Superseded
                UndisclosableReference("f-cost", "internal-only"), "cost = 900", NarrativeOffenceClass.Withheld
                MisstatedValue("f-rev", "1,200", "1,250"), "revenue = 1,200", NarrativeOffenceClass.MisstatedValue
            ]

            for reason, claim, expected in table do
                Expect.equal
                    (NarrativeOffenceClassifier.classify (offence reason claim))
                    expected
                    (sprintf "%A over '%s'" reason claim)
        }

        test "every reason reads back from its audit-trail line as the same class, place and claim" {
            let reasons = [
                UnreferencedNumber
                MetricWithoutReference
                UnresolvedReference "f-missing"
                UnregisteredReferenceKind("budget", "budget:q")
                SupersededReference("f-old", Some "f-new")
                SupersededReference("f-old", None)
                UndisclosableReference("f-cost", "internal-only")
                MisstatedValue("f-rev", "1,200", "1,250")
            ]

            for reason in reasons do
                for claim in
                    [
                        "Revenue rose 12 percent."
                        "a \"quoted\" claim — with a dash"
                        "three of them"
                    ] do
                    let o = offence reason claim

                    Expect.equal
                        (NarrativeOffenceClassifier.tryClassifyDescribed (GroundingOffence.describe o))
                        (Some(NarrativeOffenceClassifier.classify o, location, claim))
                        (sprintf "%A round-trips" reason)
        }

        test "a line that is no offence reads as none" {
            Expect.isNone (NarrativeOffenceClassifier.tryClassifyDescribed "not an offence") "no reason"
            Expect.isNone (NarrativeOffenceClassifier.tryClassifyDescribed "") "empty"
        }

        test "the place names the section and the element kind" {
            Expect.equal (NarrativeOffenceClassifier.placeOf location) ("summary", "prose") "a span is prose"

            Expect.equal
                (NarrativeOffenceClassifier.placeOf "section 'brands' › element 2 › row 3 › column 2")
                ("brands", "table")
                "a row is a table"

            Expect.equal (NarrativeOffenceClassifier.placeOf "the title") ("", "element") "no place"
        }

        test "a claim a repair round fixed is kept as repaired; one still refused at the end is not" {
            let fixedOne = offence UnreferencedNumber "Revenue rose 12 percent."
            let kept = offence (MisstatedValue("f-rev", "1,200", "1,250")) "revenue = 1,200"

            let offences =
                NarrativeOffenceClassifier.runOffences [ kept ] [
                    GroundingOffence.describe fixedOne
                    GroundingOffence.describe kept
                    GroundingOffence.describe kept
                ]

            Expect.equal
                (offences |> List.map (fun o -> o.Class, o.Repaired))
                [
                    NarrativeOffenceClass.MisstatedValue, false
                    NarrativeOffenceClass.UnreferencedNumber, true
                ]
                "each claim once, the fixed one repaired"
        }
    ]

let private recurrenceTests =
    testList "recurrence" [
        test "a class refused on 3 of the last 5 runs recurs" {
            let learning =
                NarrativeLearning.empty
                |> recordAll [
                    run 1 0 [ unref ]
                    run 2 0 []
                    run 3 0 [ unref ]
                    run 4 0 []
                    run 5 0 [ unref ]
                ]

            Expect.isTrue (NarrativeLearning.recurs policy learning.Runs 0 unref) "3 of 5"
            Expect.equal (NarrativeLearning.due policy 0 learning) [ unref ] "due a proposal"
        }

        test "a class refused on 2 of the last 5 runs does not" {
            let learning =
                NarrativeLearning.empty
                |> recordAll [ run 1 0 [ unref ]; run 2 0 []; run 3 0 []; run 4 0 []; run 5 0 [ unref ] ]

            Expect.isFalse (NarrativeLearning.recurs policy learning.Runs 0 unref) "2 of 5"
            Expect.isEmpty (NarrativeLearning.due policy 0 learning) "nothing due"
        }

        test "only the last five runs count" {
            let learning =
                NarrativeLearning.empty
                |> recordAll [
                    run 1 0 [ unref ]
                    run 2 0 [ unref ]
                    run 3 0 []
                    run 4 0 []
                    run 5 0 []
                    run 6 0 [ unref ]
                    run 7 0 [ unref ]
                ]

            Expect.isFalse (NarrativeLearning.recurs policy learning.Runs 0 unref) "the two oldest fell out"
        }

        test "a new wording version starts the count afresh" {
            let learning =
                NarrativeLearning.empty
                |> recordAll [ run 1 0 [ unref ]; run 2 0 [ unref ]; run 3 1 [ unref ]; run 4 1 [ unref ] ]

            Expect.isFalse (NarrativeLearning.recurs policy learning.Runs 1 unref) "two under version 1"

            Expect.equal
                (NarrativeLearning.standing policy learning.Runs 1 unref).Runs
                2
                "version 0's runs are not counted under version 1"
        }

        test "an open proposal of the class blocks a second one" {
            let learning = {
                (NarrativeLearning.empty
                 |> recordAll [ run 1 0 [ unref ]; run 2 0 [ unref ]; run 3 0 [ unref ] ]) with
                    Proposals = [ proposal unref 0 NarrativeProposalState.Open ]
            }

            Expect.isEmpty (NarrativeLearning.due policy 0 learning) "at most one open proposal per class"
        }

        test "a dismissed class stays quiet under its version, and is proposed again when it recurs under a later one" {
            let dismissed = {
                (NarrativeLearning.empty
                 |> recordAll [ run 1 0 [ unref ]; run 2 0 [ unref ]; run 3 0 [ unref ] ]) with
                    Proposals = [ proposal unref 0 NarrativeProposalState.Dismissed ]
            }

            Expect.isEmpty (NarrativeLearning.due policy 0 dismissed) "quiet under the version it was dismissed in"

            let stillQuiet = dismissed |> recordAll [ run 4 0 [ unref ]; run 5 0 [ unref ] ]
            Expect.isEmpty (NarrativeLearning.due policy 0 stillQuiet) "recurring more under it changes nothing"

            let later =
                dismissed
                |> recordAll [ run 6 2 [ unref ]; run 7 2 [ unref ]; run 8 2 [ unref ] ]

            Expect.equal (NarrativeLearning.due policy 2 later) [ unref ] "recurs under a later version"
        }

        test "an open proposal lapses when a run uses another wording, and an approved one's sentence is named again" {
            let approved = {
                proposal unref 0 (NarrativeProposalState.Approved 1) with
                    Sentence = "Never state a figure without its citation."
            }

            let learning =
                {
                    NarrativeLearning.empty with
                        Proposals = [
                            approved
                            proposal NarrativeOffenceClass.NumberWord 0 NarrativeProposalState.Open
                        ]
                }
                |> recordAll [ run 1 1 [ unref ]; run 2 1 [ unref ]; run 3 1 [ unref ] ]

            Expect.equal
                (learning.Proposals |> List.map _.State)
                [ NarrativeProposalState.Approved 1; NarrativeProposalState.Lapsed ]
                "the open one lapsed"

            Expect.equal (NarrativeLearning.due policy 1 learning) [ unref ] "recurs under the approved version"

            Expect.equal
                (NarrativeLearning.earlier unref learning)
                [ approved.Sentence ]
                "the earlier sentence is handed to the model"

            let view = NarrativeLearning.view policy "k" "Write it." 1 learning

            Expect.equal
                (view.Proposals
                 |> List.choose _.FollowThrough
                 |> List.map (fun s -> s.RunsWithClass, s.Runs, s.Recurs))
                [ 3, 3, true ]
                "the follow-through says the sentence did not stop it"
        }

        test "the history keeps the newest runs only" {
            let learning =
                NarrativeLearning.empty
                |> recordAll [ for day in 1..28 -> run day 0 [] ]
                |> recordAll [ for day in 1..28 -> run day 0 [] ]

            Expect.equal learning.Runs.Length policy.MaxRunsKept "capped"
        }

        test "a decision applies to an open proposal once" {
            let p = proposal unref 0 NarrativeProposalState.Open

            let learning = {
                NarrativeLearning.empty with
                    Proposals = [ p ]
            }

            match NarrativeLearning.decide p.Id NarrativeProposalState.Dismissed "olive" (at 21) learning with
            | Error reason -> failtestf "refused: %s" reason
            | Ok(decided, _) ->
                Expect.equal (decided.Proposals |> List.map _.State) [ NarrativeProposalState.Dismissed ] "dismissed"

                Expect.isError
                    (NarrativeLearning.decide p.Id (NarrativeProposalState.Approved 1) "olive" (at 22) decided)
                    "a decided proposal is not decided again"
        }
    ]

let private draftingTests =
    testList "drafting" [
        test "the sentence is the first line, without markers or quotes, within the limit" {
            Expect.equal
                (NarrativeProposalDrafting.sentenceOf 400 "\n- \"Cite every figure.\"\nmore")
                (Some "Cite every figure.")
                "first line"

            Expect.isNone (NarrativeProposalDrafting.sentenceOf 400 "  \n ") "blank"
            Expect.isNone (NarrativeProposalDrafting.sentenceOf 5 "Cite every figure.") "too long"
        }

        test "the request names the class, the evidence, the wording and the sentences tried before" {
            let request =
                NarrativeProposalDrafting.request unref [ "Revenue rose 12 percent." ] "Write it." [ "Cite figures." ]

            for expected in
                [
                    NarrativeOffenceClass.describe unref
                    "Revenue rose 12 percent."
                    "Write it."
                    "Cite figures."
                ] do
                Expect.stringContains request expected "named"
        }
    ]

// ── The composed run ─────────────────────────────────────────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private numberWords = [ "one"; "two"; "three"; "four"; "five"; "twelve" ]

let private statesNumber (text: string) =
    text |> Seq.exists Char.IsDigit
    || text.ToLowerInvariant().Split([| ' '; '.'; ','; ';' |], StringSplitOptions.RemoveEmptyEntries)
       |> Array.exists (fun word -> List.contains word numberWords)

/// A gate that refuses every digit or number word in a prose span.
type private NumberGate() =
    interface INarrativeGroundingGate with
        member _.Check(_, _, _, document) = async {
            let offences = [
                for section in document.Sections do
                    for i, element in List.indexed section.Elements do
                        match element with
                        | Paragraph spans ->
                            for j, span in List.indexed spans do
                                match span with
                                | Text t when statesNumber t ->
                                    yield {
                                        Location =
                                            sprintf "section '%s' › element %d › span %d" section.Id (i + 1) (j + 1)
                                        Claim = t
                                        Reason = UnreferencedNumber
                                    }
                                | _ -> ()
                        | _ -> ()
            ]

            return
                if List.isEmpty offences then
                    Grounded []
                else
                    Ungrounded offences
        }

        member _.Certify(_, _, _, _) = async { return Error "no signer in this test" }

/// The model: a narrative stating a figure, and — to the drafting call — the
/// sentence it is told to answer. Records every narrative instruction.
type private Model() =
    let instructions = ConcurrentQueue<string>()

    member val Sentence = "Cite every figure as a citation token, never as plain text." with get, set
    member val Paragraph = "Revenue rose 12 percent." with get, set
    member _.Instructions = List.ofSeq instructions

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "scripted-learning"
            Model = "scripted-learning-model"
        }

        member this.SendMessage(_messages, _tools, systemPrompt, _onStream, _retryPolicy) = async {
            let system = defaultArg systemPrompt ""

            let content =
                if system = NarrativeProposalDrafting.systemPrompt then
                    this.Sentence
                else
                    instructions.Enqueue system
                    sprintf """{"sections":[{"id":"summary","paragraphs":["%s"]}]}""" this.Paragraph

            return
                Ok {
                    Content = content
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

let private declaredPrompt = "Summarise the month's brand revenue."

let private definition: GroundedNarrativeDefinition =
    GroundedNarrativeDefinition.create
        "learning-summary"
        "Learning summary"
        "sales"
        "narrator"
        "Learning summary"
        declaredPrompt
        [
            {
                Id = "summary"
                Heading = "Summary"
                Guidance = "One paragraph."
            }
        ]
    |> GroundedNarrativeDefinition.withRepairPolicy GroundedRepairPolicy.refuseOnOffence

type private Deployment = {
    Services: ServiceProvider
    Model: Model
    Teams: TeamManagement.ITeamStore
    TeamId: string
    Scope: ResolvedScope
}

/// Grounded narratives, the wording admin and the learning loop, composed as
/// a deployment composes them.
let private deploy () : Deployment =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let model = Model()

    let app =
        ServerApp.empty
        |> GroundedNarratives.compose (GroundedNarrativeRunOptions.create []) [ definition ]
        |> NarrativePromptHandler.withPromptAdmin
        |> NarrativeOffenceLearning.compose NarrativeLearningPolicy.defaults

    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IBlobStorage>(storage) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore() :> IEventStore)
    |> ignore

    services.AddSingleton<ILogger>(silentLogger) |> ignore
    services.AddSingleton<IAIProvider>(model) |> ignore
    services.AddSingleton<INarrativeGroundingGate>(NumberGate()) |> ignore

    services.AddSingleton<INarrativeStore>(InMemoryNarrativeStore() :> INarrativeStore)
    |> ignore

    let services =
        match ServerApp.serviceConfig app with
        | Some configure -> configure services
        | None -> services

    let sp = services.BuildServiceProvider()

    for hosted in sp.GetServices<IHostedService>() do
        hosted.StartAsync(CancellationToken.None)
        |> Async.AwaitTask
        |> Async.RunSynchronously

    let teamId = "team-" + Guid.NewGuid().ToString "N"

    let teams =
        TeamManagement.TeamStore(
            InMemoryBlobStorage() :> IBlobStorage,
            NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel,
            silentLogger
        )
        :> TeamManagement.ITeamStore

    async {
        let! _ = teams.CreateTeam(teamId, "Team")
        let! _ = teams.AddMember(teamId, "olive", TeamRole.Owner)
        let! _ = teams.AddMember(teamId, "mo", TeamRole.Member)
        return ()
    }
    |> Async.RunSynchronously

    {
        Services = sp
        Model = model
        Teams = teams
        TeamId = teamId
        Scope =
            ScopeResolution.ofStorageScope {
                ScopeId = teamId
                Container = $"team-{teamId}"
                Persist = true
            }
    }

let private runOnce (deployment: Deployment) : GroundedNarrativeOutcome =
    deployment.Services
        .GetRequiredService<IGroundedNarrativeRun>()
        .Run(
            {
                RunKey = definition.Key
                Scope = deployment.Scope
                Access = None
                Trigger = "test"
            }
        )
    |> Async.RunSynchronously

/// The wording admin API as `userId`, on a request resolved to the team.
let private apiAs (deployment: Deployment) (userId: string) : NarrativePromptApi =
    let services = ServiceCollection()

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (TeamMember(userId, deployment.TeamId)))
    |> ignore

    services.AddSingleton<TeamManagement.ITeamStore>(deployment.Teams) |> ignore

    for passThrough in
        [
            typeof<IBlobStorage>
            typeof<IEventStore>
            typeof<GroundedNarrativeRegistry>
            typeof<NarrativeLearningPolicy>
        ] do
        services.AddSingleton(passThrough, deployment.Services.GetRequiredService passThrough)
        |> ignore

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ScopeResolution.carry ctx deployment.Scope
    NarrativePromptHandler.narrativePromptApi ctx

let private ok (result: Async<Result<'T, string>>) : 'T =
    match Async.RunSynchronously result with
    | Ok value -> value
    | Error reason -> failtestf "refused: %s" reason

let private composedTests =
    testList "composed" [
        test "two refused runs propose nothing; the third proposes one sentence, with its evidence" {
            let deployment = deploy ()
            let api = apiAs deployment "olive"

            for _ in 1..2 do
                runOnce deployment |> ignore

            Expect.isEmpty (ok (api.ListProposals definition.Key)).Proposals "2 of 5 does not recur"

            match runOnce deployment with
            | GroundedNarrativeRefused _ -> ()
            | other -> failtestf "expected a refusal, got %A" other

            let learning = ok (api.ListProposals definition.Key)

            match learning.Proposals with
            | [ item ] ->
                Expect.equal item.Proposal.Class unref "the class that recurred"
                Expect.equal item.Proposal.State NarrativeProposalState.Open "open"
                Expect.equal item.Proposal.Sentence deployment.Model.Sentence "the model's sentence"
                Expect.equal item.Proposal.Evidence [ "Revenue rose 12 percent." ] "the refused claim"

                Expect.equal
                    item.ResultingPrompt
                    (declaredPrompt + "\n" + deployment.Model.Sentence)
                    "the wording it would produce"
            | other -> failtestf "expected one proposal, got %A" other

            Expect.equal
                (learning.Standing |> List.map (fun s -> s.Class, s.RunsWithClass, s.Recurs))
                [ unref, 3, true ]
                "the standing under the declaration"

            runOnce deployment |> ignore

            Expect.equal
                (ok (api.ListProposals definition.Key)).Proposals.Length
                1
                "a fourth recurrence adds no second open proposal"
        }

        test "no run reads a proposal: the wording sent is the version in force until an admin approves it" {
            let deployment = deploy ()
            let api = apiAs deployment "olive"

            for _ in 1..3 do
                runOnce deployment |> ignore

            let item = (ok (api.ListProposals definition.Key)).Proposals |> List.exactlyOne

            runOnce deployment |> ignore
            let sentAfterProposal = List.last deployment.Model.Instructions

            Expect.stringContains sentAfterProposal declaredPrompt "the declaration is sent"

            Expect.isFalse
                (sentAfterProposal.Contains item.Proposal.Sentence)
                "a saved proposal is not in the wording sent"

            let view =
                ok (
                    api.ApproveProposal {
                        RunKey = definition.Key
                        ProposalId = item.Proposal.Id
                    }
                )

            Expect.equal (view.Current |> Option.map _.Version) (Some 1) "approval saved version 1"

            runOnce deployment |> ignore
            let sentAfterApproval = List.last deployment.Model.Instructions

            Expect.stringContains sentAfterApproval item.Proposal.Sentence "the approved sentence is sent"

            let after = ok (api.ListProposals definition.Key)

            Expect.equal
                (after.Proposals |> List.map _.Proposal.State)
                [ NarrativeProposalState.Approved 1 ]
                "recorded as approved"

            Expect.equal
                (after.Proposals
                 |> List.choose _.FollowThrough
                 |> List.map (fun s -> s.WordingVersion, s.Runs, s.RunsWithClass))
                [ 1, 1, 1 ]
                "the follow-through counts the run under the new version"
        }

        test "a member cannot approve, and a dismissal is recorded and not re-proposed under the same wording" {
            let deployment = deploy ()
            let owner = apiAs deployment "olive"

            for _ in 1..3 do
                runOnce deployment |> ignore

            let item = (ok (owner.ListProposals definition.Key)).Proposals |> List.exactlyOne

            let decision = {
                RunKey = definition.Key
                ProposalId = item.Proposal.Id
            }

            Expect.isError
                ((apiAs deployment "mo").ApproveProposal decision |> Async.RunSynchronously)
                "a member is refused"

            let dismissed = ok (owner.DismissProposal decision)

            Expect.equal
                (dismissed.Proposals
                 |> List.map (fun p -> p.Proposal.State, p.Proposal.DecidedBy))
                [ NarrativeProposalState.Dismissed, Some "olive" ]
                "dismissed, by whom"

            for _ in 1..3 do
                runOnce deployment |> ignore

            Expect.equal (ok (owner.ListProposals definition.Key)).Proposals.Length 1 "not proposed again"

            Expect.isError
                (owner.ApproveProposal decision |> Async.RunSynchronously)
                "a dismissed proposal cannot be approved"
        }

        test "a drafted sentence that states a figure is discarded, not saved" {
            let deployment = deploy ()
            deployment.Model.Sentence <- "Cite the three largest figures."
            let api = apiAs deployment "olive"

            for _ in 1..3 do
                runOnce deployment |> ignore

            Expect.isEmpty (ok (api.ListProposals definition.Key)).Proposals "discarded"
        }

        test "clean runs are counted and propose nothing" {
            let deployment = deploy ()
            deployment.Model.Paragraph <- "Revenue rose."
            let api = apiAs deployment "olive"

            for _ in 1..3 do
                match runOnce deployment with
                | GroundedNarrativePublished _ -> ()
                | other -> failtestf "expected a publication, got %A" other

            let learning = ok (api.ListProposals definition.Key)
            Expect.isEmpty learning.Proposals "nothing to propose"
            Expect.isEmpty learning.Standing "nothing refused"
        }
    ]

let tests =
    testList "Phase 1177 — narrative offence learning" [
        classifierTests
        recurrenceTests
        draftingTests
        composedTests
    ]