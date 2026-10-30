// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PipelineAssertionTests

// ─── A pipeline result becomes a `Computed` fact ─────────────────────
//
// `evaluate_pipeline`, composed with derived assertions, asserts one numeric
// column of its result as facts of a registered derived metric. What this pack
// pins:
//
//   1. **Refused by name.** A column the result lacks, a withheld column, a
//      non-numeric or host-rounded column, an unregistered metric, a metric
//      not produced by the pipeline operation, a level other than the slice's,
//      a result whose rows name no subject or one subject twice, and a row
//      whose value reads no fact. Without the composition the argument is
//      absent from the declaration and refused.
//   2. **The draft.** The method identity's spelling, the subject from the
//      key, the slice's period, and each row's inputs: the facts of its own
//      subject when the pipeline is subject-local, every fact the column reads
//      when it is not.
//   3. **The write.** One batch, the ids and the receipt digest; an unchanged
//      re-run is idempotent, and a re-run after one input is superseded
//      supersedes exactly that subject's derived fact.
//   4. **The gates.** The asserted figure, quoted, verifies under `Strict` in a
//      chat turn, and the grounding gate admits a citation of it.
//
// The derived fact's disclosure (the join of its inputs' labels) is pinned in
// the taint pack beside every other derivation.

open System
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Fuaran.Core
open Fuaran.Compute
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.Narrative
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AIAssistantHandler
open ToolUp.AI.AnswerVerifier
open ToolUp.Facts
open ToolUp.Facts.Presentation
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures
open ToolUp.Platform.Tests.InProcess.PipelineToolTests.PipelineFixtures

// ─── Fixtures ────────────────────────────────────────────────────────

let private definition (id: string) (format: string) (producing: string option) : MetricRegistration = {
    Module = "trade"
    Definition = {
        Id = id
        Name = id
        Unit = "GBP"
        Dimensionality = "currency"
        Direction = HigherIsBetter
        DisplayFormat = format
        Staleness = UntilSuperseded
        ProducingOperation = producing
        CanonicalMethod = None
        RecomputePolicy = None
        RollUp = Some(Additive 0.01m)
        Context = None
    }
}

/// The fixture's registry, with `site_total` registered as a derived metric
/// the pipeline operation produces.
let private derivedRegistry: IMetricRegistry =
    MetricRegistry.build [
        definition "revenue" "" None
        definition "margin" "" None
        definition "site_total" "N2" (Some PipelineAssertion.OperationId)
    ] [
        {
            SubjectRegistration.Module = "trade"
            Definition = {
                Id = "sites"
                Name = "Sites"
                Levels = [ "region"; "site" ]
                Calendar = None
            }
        }
    ]

/// A world whose registry declares the derived metric.
let private assertingWorld () : World =
    let w = world ()

    {
        w with
            Deps = {
                w.Deps with
                    Registry = Some derivedRegistry
            }
    }

/// Revenue over the three seeded days, `pipeline`, and the `assert` argument.
let private assertArgs (pipeline: Transform list) (column: string) (metric: string) (level: string) : string =
    let args = revenueArgs pipeline

    args.Substring(0, args.Length - 1)
    + sprintf ""","assert":{"column":"%s","as_metric":"%s","level":"%s"}}""" column metric level

/// The site totals asserted as `site_total` at the site level.
let private totalsArgs = assertArgs perSubject "total" "site_total" "site"

let private asserted (w: World) (args: string) =
    PipelineAssertion.answerWith w.Deps w.Scope "u" args |> Async.RunSynchronously

let private assertionOf (w: World) (args: string) : PipelineAssertionResult =
    match asserted w args with
    | Ok(_, Some assertion) -> assertion
    | Ok(_, None) -> failtest "expected an assertion, got an evaluation alone"
    | Error refusal -> failtestf "expected an assertion, got %s" (PipelineRefusal.describe refusal)

let private refusalOf (w: World) (args: string) : PipelineRefusal =
    match asserted w args with
    | Error refusal -> refusal
    | Ok _ -> failtest "expected a refusal"

/// The drafts for `args` in `w`, read through the same slice the executor
/// reads.
let private draftsOf (w: World) (args: string) : FactDraft list =
    let request =
        match PipelineTool.assertionOf args with
        | Ok(Some request) -> request
        | other -> failtestf "no assertion in the arguments: %A" other

    match PipelineTool.evaluateSliced w.Deps w.Scope "u" args |> Async.RunSynchronously with
    | Error refusal -> failtestf "the evaluation was refused: %s" (PipelineRefusal.describe refusal)
    | Ok(slice, result) ->
        match PipelineAssertion.draft w.Deps.Registry slice result request (Some "trigger") with
        | Ok drafts -> drafts
        | Error refusal -> failtestf "the draft was refused: %s" (PipelineRefusal.describe refusal)

let private site (name: string) : SubjectRef = {
    Hierarchy = "sites"
    Path = [ "north"; name ]
}

let private inputsOf (d: FactDraft) : string list = d.Evidence.InputHashes

let private revenueInputs (w: World) (name: string) : string list =
    [ 0..2 ]
    |> List.map (fun day -> FactInput.ofFactId (revenueFactId w name day))
    |> List.sort

/// The fixture's revenue total per site over the three days.
let private totalOf (name: string) : decimal =
    [ 0..2 ] |> List.sumBy (fun day -> revenueOn day name)

// ─── 1. Refused by name ──────────────────────────────────────────────

let private refusalTests =
    testList "refused by name" [

        test "a column the result lacks" {
            let w = assertingWorld ()

            Expect.equal
                (refusalOf w (assertArgs perSubject "nope" "site_total" "site"))
                (PipelineRefusal.AssertedColumnUnknown("nope", [ "subject"; "total"; "days" ]))
                "names the column and the result's columns"
        }

        test "a withheld column: its values are partial" {
            let w = assertingWorld ()
            w.Gate.Deny [ revenueFactId w "s2" 1 ]

            match refusalOf w totalsArgs with
            | PipelineRefusal.AssertedColumnWithheld("total",
                                                     [ {
                                                           PolicyRef = "trade-figures"
                                                           Count = 1
                                                       } ]) -> ()
            | other -> failtestf "expected the withheld column, got %A" other
        }

        test "a column that holds no numbers" {
            let w = assertingWorld ()

            Expect.equal
                (refusalOf w (assertArgs perSubject "subject" "site_total" "site"))
                (PipelineRefusal.AssertedColumnNotNumeric("subject", "text"))
                "names the column and what it holds"
        }

        test "a host-rounded column is not one fact" {
            let w = assertingWorld ()

            let logged = [
                Derive("log_revenue", ApplyFn(Ln, [ Cast(FloatType, Col "revenue") ]))
                GroupBy(
                    [ FactBrowseSlice.SubjectColumn ],
                    [
                        {
                            Name = "log_total"
                            Fn = Sum
                            Of = "log_revenue"
                        }
                    ]
                )
            ]

            Expect.equal
                (refusalOf w (assertArgs logged "log_total" "site_total" "site"))
                (PipelineRefusal.AssertedColumnHostRounded "log_total")
                "names the column"
        }

        test "an unregistered metric, and one the pipeline operation does not produce" {
            let w = assertingWorld ()

            Expect.equal
                (refusalOf w (assertArgs perSubject "total" "unheard_of" "site"))
                (PipelineRefusal.UnregisteredMetric "unheard_of")
                "an unregistered metric"

            Expect.equal
                (refusalOf w (assertArgs perSubject "total" "revenue" "site"))
                (PipelineRefusal.NotADerivedMetric("revenue", None))
                "a source metric is never asserted by the assistant"
        }

        test "a level other than the slice's, a result naming no subject, and a subject named twice" {
            let w = assertingWorld ()

            match refusalOf w (assertArgs perSubject "total" "site_total" "region") with
            | PipelineRefusal.SubjectKeyUnresolved reason ->
                Expect.stringContains reason "'site'" "names the slice's level"
            | other -> failtestf "expected an unresolved key, got %A" other

            let overall = [
                GroupBy(
                    [],
                    [
                        {
                            Name = "total"
                            Fn = Sum
                            Of = "revenue"
                        }
                    ]
                )
            ]

            match refusalOf w (assertArgs overall "total" "site_total" "site") with
            | PipelineRefusal.SubjectKeyUnresolved reason -> Expect.stringContains reason "subject" "says what to keep"
            | other -> failtestf "expected an unresolved key, got %A" other

            let daily = [ Project [ FactBrowseSlice.SubjectColumn, "subject"; "revenue", "revenue" ] ]

            Expect.equal
                (refusalOf w (assertArgs daily "revenue" "site_total" "site"))
                (PipelineRefusal.SubjectRepeated "sites/north>s1")
                "one row per day is three rows per subject"
        }

        test "a value that reads no fact has no inputs to name" {
            let w = assertingWorld ()

            Expect.equal
                (refusalOf w (assertArgs perSubject "days" "site_total" "site"))
                (PipelineRefusal.InputsUnnamed "sites/north>s1")
                "a count of rows reads no fact"
        }

        test "without the composition the argument is absent and refused" {
            let w = assertingWorld ()

            Expect.equal
                (PipelineTool.answerWith w.Deps w.Scope "u" totalsArgs |> Async.RunSynchronously)
                (Error PipelineRefusal.AssertionNotOffered)
                "the plain executor refuses it"

            let names (d: AIToolDefinition) = d.Parameters |> List.map _.Name

            Expect.isFalse (names PipelineTool.definition |> List.contains "assert") "absent from the plain declaration"

            Expect.isTrue
                (names PipelineTool.definitionWithAssertions |> List.contains "assert")
                "present when composed"

            Expect.equal
                PipelineTool.definitionWithAssertions.Effects
                (ToolEffectDeclaration.declare [ ReadFacts; ComputeFacts; WriteState FactEvents.SourceModule ])
                "and the write is declared beside the read"
        }
    ]

// ─── 2. The draft ────────────────────────────────────────────────────

let private draftTests =
    testList "the draft" [

        test "one fact per subject, its method naming the pipeline and the evaluator's version" {
            let w = assertingWorld ()
            let drafts = draftsOf w totalsArgs
            let result = answered w (revenueArgs perSubject)

            Expect.equal (drafts |> List.map _.Subject) [ site "s1"; site "s2" ] "one per subject, in result order"
            Expect.equal (drafts |> List.map _.Value) [ Scalar(totalOf "s1"); Scalar(totalOf "s2") ] "the totals"

            for d in drafts do
                Expect.equal
                    d.Method
                    (Computed("fuaran.compute.pipeline", PipelineAssertion.ComputeVersion, result.PipelineHash))
                    "the method identity"

                Expect.equal d.Metric (MetricRef "site_total") "the derived metric"
                Expect.equal (d.Period.From, d.Period.To) (day1, day1.AddDays 3.0) "the slice's period"
                Expect.equal d.Disclosure Surfaceable "a registered metric's fact"
                Expect.equal d.Evidence.TriggerRef (Some "trigger") "the trigger"

            Expect.isTrue
                (Text.RegularExpressions.Regex.IsMatch(PipelineAssertion.ComputeVersion, @"^\d+\.\d+\.\d+$"))
                (sprintf "the package version, with no build metadata: %s" PipelineAssertion.ComputeVersion)
        }

        test "a subject-local pipeline names the row's own facts" {
            let w = assertingWorld ()

            Expect.equal
                (draftsOf w totalsArgs |> List.map inputsOf)
                [ revenueInputs w "s1"; revenueInputs w "s2" ]
                "each total names its own site's three revenue facts"

            for input in draftsOf w totalsArgs |> List.collect inputsOf do
                Expect.isTrue (InputHash.isContentHash input) (sprintf "a named input: %s" input)
        }

        test "a pipeline that is not subject-local names every fact the column reads" {
            let w = assertingWorld ()
            // The limit cuts the row set before the aggregation, so a site's
            // total depends on which rows the other site left.
            let limited = Transform.limit 4 0 :: perSubject
            let every = revenueInputs w "s1" @ revenueInputs w "s2" |> List.sort

            for d in draftsOf w (assertArgs limited "total" "site_total" "site") do
                Expect.equal (inputsOf d) every "every revenue fact of the slice"
        }
    ]

// ─── 3. The write ────────────────────────────────────────────────────

/// A committed run rewriting every day of `trade`, with `s1`'s first day
/// corrected.
let private correctFirstDay (w: World) =
    let writer = Option.get w.Deps.Writer

    let rows = [
        for day in 0..2 do
            for name in [ "s1"; "s2" ] do
                {
                    Subject = [ "north"; name ]
                    Period = {
                        From = day1.AddDays(float day)
                        To = day1.AddDays(float day + 1.0)
                        Label = None
                    }
                    Values =
                        Map.ofList [
                            "revenue",
                            Scalar(
                                if day = 0 && name = "s1" then
                                    revenueOn day name + 50m
                                else
                                    revenueOn day name
                            )
                        ]
                }
    ]

    let run (r: Async<Result<'T, 'E>>) =
        match Async.RunSynchronously r with
        | Ok v -> v
        | Error e -> failtestf "the correcting run failed: %A" e

    let opened = run (writer.OpenRun(w.Scope, trade.Id))
    run (writer.WriteRows(w.Scope, opened.RunId, rows)) |> ignore
    run (writer.Commit(w.Scope, opened.RunId)) |> ignore

let private writeTests =
    testList "the write" [

        test "one batch: the facts, their ids, renderings and the receipt" {
            let w = assertingWorld ()
            let assertion = assertionOf w totalsArgs
            let drafts = draftsOf w totalsArgs

            Expect.equal assertion.AssertedCount 2 "two new facts"
            Expect.isNotEmpty assertion.ReceiptDigest "the receipt's digest"

            Expect.equal
                (assertion.Facts |> List.map _.FactId)
                (drafts |> List.map PipelineAssertion.factIdOf)
                "the ids the drafts address"

            Expect.equal
                (assertion.Facts |> List.map _.Rendering)
                [
                    Some(FactRendering.render "N2" (Scalar(totalOf "s1")))
                    Some(FactRendering.render "N2" (Scalar(totalOf "s2")))
                ]
                "rendered with the derived metric's display format"

            let stored =
                w.Deps.Store.Get(w.Scope, assertion.Facts.Head.FactId)
                |> TestScope.ok
                |> Async.RunSynchronously

            match stored with
            | Some fact -> Expect.equal fact.Metric (MetricRef "site_total") "stored as the derived metric"
            | None -> failtest "the fact is not in the store"

            Expect.equal
                (toolResultFacts (PipelineAssertion.text assertion) |> List.map _.FactId)
                (assertion.Facts |> List.map _.FactId)
                "the text carries them where the answer verifier reads"
        }

        test "an unchanged re-run is idempotent; a re-run after one input moved supersedes that subject only" {
            let w = assertingWorld ()
            let first = assertionOf w totalsArgs
            let again = assertionOf w totalsArgs

            Expect.equal again.IdempotentCount 2 "nothing new"
            Expect.equal (again.Facts |> List.map _.FactId) (first.Facts |> List.map _.FactId) "the same facts"

            correctFirstDay w
            let corrected = assertionOf w totalsArgs

            Expect.equal
                (corrected.SupersedingCount, corrected.IdempotentCount)
                (1, 1)
                "the corrected site supersedes, the other is unchanged"

            let s1 = corrected.Facts.Head

            match w.Deps.Store.Get(w.Scope, s1.FactId) |> TestScope.ok |> Async.RunSynchronously with
            | Some fact ->
                Expect.equal
                    fact.Supersedes
                    (Some first.Facts.Head.FactId)
                    "the derived fact supersedes its predecessor"

                Expect.equal fact.Value (Scalar(totalOf "s1" + 50m)) "with the corrected total"
            | None -> failtest "the superseding fact is not in the store"

            Expect.equal corrected.Facts[1].FactId first.Facts[1].FactId "the other site's fact is unchanged"
        }
    ]

// ─── 4. The gates ────────────────────────────────────────────────────

let private groundingTests =
    testList "the grounding gate" [

        test "admits a citation of an asserted figure" {
            let w = assertingWorld ()
            let assertion = assertionOf w totalsArgs
            let fact = assertion.Facts.Head
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let disclosure = FactDisclosureGate.create w.Deps.Store events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

            let graph =
                ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create w.Deps.Store)

            let issuer =
                GroundingCertificate.createIssuer graph w.Deps.Store disclosure events None

            let gate =
                FactNarrativeGroundingGate.create w.Deps.Store disclosure (Some derivedRegistry) [] issuer

            let document: NarrativeDocument = {
                Title = "Trade"
                Subtitle = None
                Sections = [
                    {
                        Id = "summary"
                        Heading = "Sites"
                        Subheading = None
                        Elements = [
                            NarrativeElement.Paragraph [
                                InlineSpan.Text "The first site took "
                                InlineSpan.Metric("total", Option.get fact.Rendering, Some fact.FactId)
                            ]
                        ]
                    }
                ]
                Provenance = None
                Lang = None
                CanonicalUrl = None
            }

            match
                gate.Check(w.Scope, "reader", FactNarrativePublication, document)
                |> Async.RunSynchronously
            with
            | Grounded _ -> ()
            | Ungrounded offences -> failtestf "the citation was refused: %A" offences
        }
    ]

/// The model: one `evaluate_pipeline` call asserting the site totals, then
/// an answer quoting the first site's total exactly as the tool rendered it.
type private QuotingModel(args: string) =
    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "scripted-pipeline"
            Model = "scripted-pipeline-model"
        }

        member _.SendMessage(messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            match messages |> List.collect _.ToolResults with
            | [] ->
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = PipelineTool.Name
                                Arguments = args
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | results ->
                let content = (List.last results).Content

                let rendering =
                    (JsonDocument.Parse content)
                        .RootElement.GetProperty("asserted")
                        .GetProperty("facts")
                        .EnumerateArray()
                    |> Seq.head
                    |> fun f -> f.GetProperty("rendering").GetString()

                return
                    Ok {
                        Content = sprintf "The first site took £%s in total." rendering
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

type private ScriptedFactory(provider: IAIProvider) =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Ok provider }
        member _.TryResolveByLabel(_, _) = async { return Ok provider }
        member _.BuildPlatform(_, _, _) = None

/// The fact tier composed by its knob, the fact tables, the registry naming
/// the derived metric, the asserting pipeline tool, and the numeric gate in
/// `Strict`.
let private chatServices (model: IAIProvider) : ServiceProvider =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = EnabledFactStore
                }
        }
        |> FactsCompose.withFactStore

    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IAIProviderFactory>(ScriptedFactory model) |> ignore

    services.AddSingleton<AnswerGate>(
        {
            Mode = AnswerGateStrict
            Verifier = NumericFidelityVerifier()
        }
    )
    |> ignore

    let tools = AIToolRegistry.AIToolRegistry()
    let definition, answer = PipelineAnsweringTool.assertingTool
    tools.RegisterAll [ AIToolRegistry.createAnsweringTool definition answer ]
    services.AddSingleton<AIToolRegistry.AIToolRegistry>(tools) |> ignore

    services.AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(
        ClientToolDispatch.ClientToolDispatchRegistry()
    )
    |> ignore

    services.AddSingleton<AICancellationRegistry.AICancellationRegistry>(
        AICancellationRegistry.AICancellationRegistry()
    )
    |> ignore

    let services =
        match ServerApp.serviceConfig app with
        | Some configure -> configure services
        | None -> services

    // After the composition, so these are the ones resolved.
    services.AddSingleton<IFactTableRegistry>(tables) |> ignore
    services.AddSingleton<IMetricRegistry>(derivedRegistry) |> ignore
    services.BuildServiceProvider()

let private requestFor (sp: IServiceProvider) (scopeId: string) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- sp

    let storage: StorageScope = {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

    ctx.Items["ToolUp.StorageScope"] <- box storage
    ScopeResolution.remember ctx storage |> ignore
    ctx.Items["ToolUp.UserId"] <- box "alice"
    ctx :> HttpContext

/// Submit one chat turn and return the assistant's reply as persisted.
let private chatTurn (ctx: HttpContext) : ConversationMessage =
    let api = fst (aiAssistantApi None Map.empty (new SSEConnectionManager()) ctx)
    let conversationId = Guid.NewGuid()

    async {
        let! submitted =
            api.SubmitMessage {
                ConversationId = conversationId
                Content = "What did each site take over the three days?"
                ActiveModule = None
                ActivePage = None
                ActivePageNarrative = None
                OverrideProviderLabel = None
                Surface = FullPage
                RetrievalFilters = None
            }

        let mutable finished = false
        let mutable attempts = 0

        while not finished && attempts < 400 do
            match! api.GetTaskStatus submitted.TaskId with
            | Some task ->
                match task.Status with
                | AITaskFailed reason -> failtestf "the turn failed: %A" reason
                | AITaskCompleted -> finished <- true
                | _ ->
                    attempts <- attempts + 1
                    do! Async.Sleep 50
            | None ->
                attempts <- attempts + 1
                do! Async.Sleep 50

        if not finished then
            failtest "the turn did not finish"

        let! messages = api.GetConversation conversationId

        return
            messages
            |> List.filter (fun m -> m.Participant = AIAssistant)
            |> List.tryLast
            |> Option.defaultWith (fun () -> failtest "no assistant reply was persisted")
    }
    |> Async.RunSynchronously

let private endToEndTests =
    testList "end to end" [
        test "a turn that asserts a grouped total and quotes it keeps the figure under Strict" {
            let sp = chatServices (QuotingModel totalsArgs)
            let store = sp.GetRequiredService<IFactStore>()
            let storage = sp.GetRequiredService<IBlobStorage>()
            let events = sp.GetRequiredService<IEventStore>()
            let scopeId = "team-" + Guid.NewGuid().ToString "N"

            let writer =
                DefaultFactTableWriter.createWithClock store storage events tables (Some derivedRegistry) (fun () ->
                    day1.AddDays 10.0)

            seed
                writer
                (ScopeResolution.ofStorageScope {
                    ScopeId = scopeId
                    Container = "container-" + scopeId
                    Persist = true
                })
                trade
                "revenue"

            let reply = chatTurn (requestFor sp scopeId)
            let expected = FactRendering.render "N2" (Scalar(totalOf "s1"))

            Expect.equal
                reply.Content
                (sprintf "The first site took £%s in total." expected)
                "the figure is not withheld"

            match reply.Verification with
            | Some v ->
                Expect.equal v.Mode "Strict" "the gate ran in Strict"
                Expect.equal v.Verified 1 "the figure verified"
                Expect.equal v.Unmatched 0 "nothing unmatched"

                let asserted =
                    store.Query(
                        TestScope.resolved scopeId,
                        {
                            FactQuery.all with
                                Metric = Some(MetricRef "site_total")
                        }
                    )
                    |> TestScope.ok
                    |> Async.RunSynchronously
                    |> List.map _.FactId

                Expect.contains asserted (List.exactlyOne v.Numbers).MatchedFactId.Value "against the asserted fact"
            | None -> failtest "the gate produced no verdict"
        }
    ]

let tests =
    testList "Phase 1146 — a pipeline result becomes a Computed fact" [
        refusalTests
        draftTests
        writeTests
        groundingTests
        endToEndTests
    ]