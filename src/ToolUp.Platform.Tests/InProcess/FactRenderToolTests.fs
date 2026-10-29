// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactRenderToolTests

// ─── Phase 1132 — the render tools ───────────────────────────────────
//
// `render_chart` and `render_table`: the model writes a presentation
// request, the server reads the series it names under the disclosure gate at
// the tool-result door and answers with a text summary and a block part.
// What this pack pins:
//
//   1. **Refused by name.** A malformed request, an unknown table, metric or
//      level, and a kind the other tool draws are each refused with a
//      sentence naming what to repair, and attach nothing.
//   2. **The summary's wording**, word for word: counts, the canonical period
//      and the title, never a value.
//   3. **The part.** Exactly one block part, carrying the request the model
//      wrote and the provenance of the read; the request decodes back and the
//      node rebuilds from it (what the conversation panel does).
//   4. **The door.** The gate is asked at `FactToolResult`, never `FactBrowse`.
//   5. **The composition.** Opt-in, idempotent, nothing under `NoFactStore`,
//      registered by `composeAI` through the record-form funnel; the fact
//      tier itself stays free of the Fuaran-UI dependency.
//   6. **End to end.** A scripted turn calls `render_chart` through the
//      composed fact tier and the final message carries the block.
//
// The egress instances (a withheld point, a denied principal) live in the
// disclosure egress pack beside every other door, over the fixtures below.

open System
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Providers
open ToolUp.Platform.Grounding
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.Facts
open ToolUp.Facts.Presentation
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Fixtures (shared with the disclosure egress pack) ──────────────

/// The fact tables, the registry and a world to read them in.
module RenderFixtures =

    let private metric (id: string) : MetricDefinition = {
        Id = id
        Name = id
        Unit = "GBP"
        Dimensionality = "currency"
        Direction = HigherIsBetter
        DisplayFormat = ""
        Staleness = UntilSuperseded
        ProducingOperation = None
        CanonicalMethod = None
        RecomputePolicy = None
        RollUp = None
        Context = None
    }

    let private sites: SubjectDefinition = {
        Id = "sites"
        Name = "Sites"
        Levels = [ "region"; "site" ]
        Calendar = None
    }

    /// The metric registry: revenue and margin over the sites hierarchy.
    let registry: IMetricRegistry =
        MetricRegistry.build [
            {
                MetricRegistration.Module = "trade"
                Definition = metric "revenue"
            }
            {
                MetricRegistration.Module = "trade"
                Definition = metric "margin"
            }
        ] [
            {
                SubjectRegistration.Module = "trade"
                Definition = sites
            }
        ]

    let private dailyTable
        (id: string)
        (column: string)
        (disclosure: FactTableDisclosure option)
        : FactTableDefinition =
        {
            Id = id
            SchemaVersion = 1
            Hierarchy = "sites"
            Level = "site"
            Columns = [
                {
                    (FactTableDefinition.column column FactTableValueShape.Scalar) with
                        Disclosure = disclosure
                }
            ]
            PeriodGrain = FactTablePeriodGrain.Day
            ProducingOperation = "trade-rollup"
            RefreshCadence = TimeSpan.FromDays 1.0
            HistoryMode = FactTableHistoryMode.Replace
            Disclosure = FactTableDisclosure.Surfaceable
            Requirement = FactTableRequirement.Optional
        }

    /// Daily revenue per site, surfaceable.
    let trade = dailyTable "daily-trade" "revenue" None

    /// Daily margin per site, restricted under a policy no resolver knows —
    /// so the conservative gate withholds every point of it.
    let margins =
        dailyTable "daily-margin" "margin" (Some(FactTableDisclosure.Restricted "trade-figures"))

    /// The declared tables.
    let tables: IFactTableRegistry =
        FactTableRegistry.build [
            {
                FactTableRegistration.Module = "trade"
                Definition = trade
            }
            {
                FactTableRegistration.Module = "trade"
                Definition = margins
            }
        ] [ BindAllFactTables DefaultFactTableWriter.Destination ]

    /// The first day of the fixture's data.
    let day1 = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)

    /// A value no other point carries, for the absence checks.
    [<Literal>]
    let DistinctiveValue = 987654.32M

    /// Its rendering's integer digits, for the absence checks.
    [<Literal>]
    let DistinctiveDigits = "987654"

    let private dayExtent (d: DateTime) : TemporalExtent = {
        From = d
        To = d.AddDays 1.0
        Label = None
    }

    /// The revenue on `day` (0-based) at `site`: day 2 at s2 is the
    /// distinctive value.
    let revenueOn (day: int) (site: string) : decimal =
        if day = 1 && site = "s2" then
            DistinctiveValue
        else
            decimal (1000 + 100 * day) + (if site = "s1" then 0.25M else 0.75M)

    /// A gate that consults the real one in its held and row forms and also
    /// denies the ids in `denied` under `trade-figures`, recording every
    /// surface it was asked at.
    type OverlayGate(inner: FactDisclosureGate) =
        let surfaces = ResizeArray<FactEgressSurface>()
        let mutable denied: Set<string> = Set.empty

        let overlay (verdicts: Map<string, FactDisclosureVerdict>) =
            verdicts
            |> Map.map (fun id v ->
                if denied.Contains id then
                    FactNotDisclosable "trade-figures"
                else
                    v)

        /// The surfaces asked, in order.
        member _.Surfaces = lock surfaces (fun () -> List.ofSeq surfaces)

        /// Deny these ids from now on.
        member _.Deny(ids: string list) = denied <- Set.ofList ids

        interface IHeldFactDisclosureGate with
            member _.CheckHeld(scope, principal, surface, factIds, held) = async {
                lock surfaces (fun () -> surfaces.Add surface)
                let! verdicts = (inner :> IHeldFactDisclosureGate).CheckHeld(scope, principal, surface, factIds, held)
                return overlay verdicts
            }

        interface IHeldFactRowDisclosureGate with
            member _.CheckHeldRows(scope, principal, surface, factIds, held) = async {
                lock surfaces (fun () -> surfaces.Add surface)

                let! verdicts =
                    (inner :> IHeldFactRowDisclosureGate).CheckHeldRows(scope, principal, surface, factIds, held)

                return overlay verdicts
            }

    /// A world: the fact browse's dependencies over an in-memory store, the
    /// overlay gate, and the scope the tables are written in.
    type World = {
        Deps: FactBrowseHandler.FactBrowseDeps
        Gate: OverlayGate
        Scope: ResolvedScope
    }

    let private ok label (r: Async<Result<'T, 'E>>) : 'T =
        match Async.RunSynchronously r with
        | Ok v -> v
        | Error e -> failtestf "%s: %A" label e

    /// One committed run of `table` writing `column` for three days and two
    /// sites (north>s1, north>s2).
    let seed (writer: IFactTableWriter) (scope: ResolvedScope) (table: FactTableDefinition) (column: string) =
        let rows = [
            for day in 0..2 do
                for site in [ "s1"; "s2" ] do
                    {
                        Subject = [ "north"; site ]
                        Period = dayExtent (day1.AddDays(float day))
                        Values = Map.ofList [ column, Scalar(revenueOn day site) ]
                    }
        ]

        let opened = ok "open" (writer.OpenRun(scope.ScopeId, table.Id))
        ok "write" (writer.WriteRows(scope.ScopeId, opened.RunId, rows)) |> ignore
        ok "commit" (writer.Commit(scope.ScopeId, opened.RunId)) |> ignore

    /// A fresh world with both tables written.
    let world () : World =
        let storage = InMemoryBlobStorage()
        let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
        // A clock that moves one second per reading, so the two runs and the
        // facts they assert are ordered in time.
        let ticks = ref 0

        let clock () =
            ticks.Value <- ticks.Value + 1
            day1.AddDays(10.0).AddSeconds(float ticks.Value)

        let store =
            BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

        let writer =
            DefaultFactTableWriter.createWithClock store storage events tables (Some registry) clock

        let gate = OverlayGate(FactDisclosureGate(store, events))
        let scopeId = "team-" + Guid.NewGuid().ToString "N"

        let w = {
            Deps = {
                Tables = tables
                Writer = Some writer
                Store = store
                Gate = gate
                Registry = Some registry
                Clock = clock
            }
            Gate = gate
            Scope =
                ScopeResolution.ofStorageScope {
                    ScopeId = scopeId
                    Container = "container-" + scopeId
                    Persist = true
                }
        }

        seed writer w.Scope trade "revenue"
        seed writer w.Scope margins "margin"
        w

    /// A trend of daily revenue by site over the three seeded days.
    let trendRequest: PresentationRequest = {
        Kind = PresentationKind.Trend
        TableId = trade.Id
        Metrics = [ "revenue" ]
        SubjectPrefix = []
        SubjectLevel = None
        Period = PresentationPeriod.Ending(day1.AddDays 2.0, 3)
        CompareTo = None
        Title = Some "Revenue per day by site"
        Annotations = []
    }

    /// A tool's answer to `args` in `w`, for principal `u`.
    let answer (tool: RenderTool) (w: World) (args: string) : ToolHelpers.ToolAnswer =
        RenderTools.answerWith tool w.Deps w.Scope "u" args |> Async.RunSynchronously

    /// The fact id of the revenue point at `site` on the day `offset` days
    /// after the first.
    let revenueFactId (w: World) (site: string) (offset: int) : string =
        let series =
            ok
                "series"
                (FactBrowseHandler.querySeries w.Deps w.Scope "u" {
                    TableId = trade.Id
                    Metric = "revenue"
                    SubjectPrefix = []
                    SubjectLevel = None
                    PeriodFrom = day1
                    PeriodTo = day1.AddDays 3.0
                    MaxPoints = FactBrowseApi.MaxSeriesPoints
                })

        series.Points
        |> List.find (fun p -> List.last p.SubjectPath = site && p.PeriodFrom = day1.AddDays(float offset))
        |> _.FactId

    /// The single block part of an answer.
    let blockOf (answer: ToolHelpers.ToolAnswer) : string * BlockProvenance =
        match answer.Parts with
        | [ BlockPart(node, provenance) ] -> node, provenance
        | other -> failtestf "expected exactly one block part, got %A (text: %s)" other answer.Text

open RenderFixtures

let private refusedWith (expected: string) (answer: ToolHelpers.ToolAnswer) =
    Expect.equal answer.Text expected "the refusal names what to repair"
    Expect.isEmpty answer.Parts "a refusal attaches nothing"

let private withRequest (f: PresentationRequest -> PresentationRequest) =
    PresentationRequest.toJson (f trendRequest)

// ─── 1. Refused by name ──────────────────────────────────────────────

let private refusalTests =
    testList "refused by name" [
        test "a request with no table id is refused naming the field" {
            let w = world ()

            answer RenderTool.Chart w """{"kind":"trend","metrics":["revenue"],"period":{"day":"2026-10-03"}}"""
            |> refusedWith "render_chart refused the request: the presentation request has no 'tableId'."
        }

        test "text that is not a JSON object is refused" {
            let w = world ()

            answer RenderTool.Table w "not json"
            |> refusedWith "render_table refused the request: the presentation request is not a JSON object."
        }

        test "an unknown table is refused naming it and the declared tables" {
            let w = world ()

            answer RenderTool.Chart w (withRequest (fun r -> { r with TableId = "weekly-trade" }))
            |> refusedWith
                "render_chart refused the request: no fact table 'weekly-trade' is declared in this deployment (declared: 'daily-trade', 'daily-margin')."
        }

        test "an unknown metric is refused naming it and the table's metrics" {
            let w = world ()

            answer RenderTool.Chart w (withRequest (fun r -> { r with Metrics = [ "units" ] }))
            |> refusedWith
                "render_chart refused the request: fact table 'daily-trade' has no metric 'units' (its metrics: 'revenue')."
        }

        test "an unknown level is refused naming it and the hierarchy's levels" {
            let w = world ()

            answer RenderTool.Chart w (withRequest (fun r -> { r with SubjectLevel = Some "store" }))
            |> refusedWith
                "render_chart refused the request: hierarchy 'sites' has no level 'store' (its levels: 'region', 'site')."
        }

        test "each tool refuses the other's kind, naming the tool to use" {
            let w = world ()

            answer RenderTool.Chart w (withRequest (fun r -> { r with Kind = PresentationKind.Table }))
            |> refusedWith
                "render_chart refused the request: it draws 'trend', 'breakdown', 'comparison', and the request's kind is 'table'; use render_table."

            answer RenderTool.Table w (PresentationRequest.toJson trendRequest)
            |> refusedWith
                "render_table refused the request: it draws 'table', and the request's kind is 'trend'; use render_chart."
        }

        test "without the fact store the adapter refuses rather than drawing" {
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- ServiceCollection().BuildServiceProvider()

            RenderTools.answer RenderTool.Chart ctx (PresentationRequest.toJson trendRequest)
            |> Async.RunSynchronously
            |> refusedWith
                "render_chart refused the request: the fact store is not composed in this deployment, so there are no facts to draw."
        }
    ]

// ─── 2–4. The summary, the part, the door ────────────────────────────

let private answerTests =
    testList "the summary, the part and the door" [
        test "a trend answers the summary word for word and one block part with the request and provenance" {
            let w = world ()
            let answered = answer RenderTool.Chart w (PresentationRequest.toJson trendRequest)

            Expect.equal
                answered.Text
                "Drew a trend of revenue from fact table 'daily-trade' over 2026-10-01/2026-10-04, titled \"Revenue per day by site\": 6 point(s) read, none withheld. The user sees the block; this result carries none of its values, so state none."
                "the summary: counts, the canonical period and the title"

            let node, provenance = blockOf answered
            Expect.equal provenance.Kind "trend" "the provenance names the kind"

            Expect.equal
                provenance.Request
                (PresentationRequest.toJson trendRequest)
                "and carries the request the model wrote"

            Expect.equal provenance.TableId "daily-trade" "the table"
            Expect.equal provenance.Metric "revenue" "the metric"
            Expect.equal provenance.Period "2026-10-01/2026-10-04" "the canonical period"
            Expect.equal (List.length provenance.FactIds) 6 "every point the read served is cited"
            Expect.isEmpty provenance.Withheld "nothing withheld"
            Expect.stringContains node "1100.25" "the node carries the gated values"

            Expect.isFalse
                (answered.Text.Contains "1100" || answered.Text.Contains "1000.75")
                "the summary carries no value"
        }

        test "the part's request decodes back and rebuilds the same node, as the conversation panel rebuilds it" {
            let w = world ()

            let node, provenance =
                blockOf (answer RenderTool.Chart w (PresentationRequest.toJson trendRequest))

            match PresentationRequest.ofJson provenance.Request with
            | Error e -> failtestf "the stored request did not decode: %s" (PresentationRequestError.describe e)
            | Ok request ->
                Expect.equal request trendRequest "the stored request is the request"

                let answers =
                    PresentationRequest.toQueries FactBrowseApi.MaxSeriesPoints request
                    |> List.map (fun q ->
                        match FactBrowseHandler.querySeries w.Deps w.Scope "u" q |> Async.RunSynchronously with
                        | Ok s -> SeriesAnswer.Answered s
                        | Error e -> SeriesAnswer.Refused(q.Metric, e))

                match Presentation.build<unit> request answers with
                | Error d -> failtestf "rebuild failed: %s" (PresentationDefect.describe d)
                | Ok block ->
                    Expect.equal (Fuaran.UI.Generated.encodeNode block.Node) node "the same node, byte for byte"
        }

        test "the gate is asked at the tool-result door and never at the browse door" {
            let w = world ()
            answer RenderTool.Chart w (PresentationRequest.toJson trendRequest) |> ignore

            Expect.isNonEmpty w.Gate.Surfaces "the read was judged"

            Expect.allEqual w.Gate.Surfaces FactToolResult "every judgement of the tool's read is at FactToolResult"
        }

        test "a table answers a table block over the last day of the period" {
            let w = world ()

            let answered =
                answer
                    RenderTool.Table
                    w
                    (withRequest (fun r -> {
                        r with
                            Kind = PresentationKind.Table
                            Title = Some "Revenue by site"
                    }))

            Expect.equal
                answered.Text
                "Drew a table of revenue from fact table 'daily-trade' over 2026-10-01/2026-10-04, titled \"Revenue by site\": 6 point(s) read, none withheld. The user sees the block; this result carries none of its values, so state none."
                "the table's summary"

            let _, provenance = blockOf answered
            Expect.equal provenance.Kind BlockProvenance.TableKind "a table block exports as a table"
        }
    ]

// ─── 5. The composition ──────────────────────────────────────────────

let private factory =
    { new IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Error NoProviderConfigured }
        member _.TryResolveByLabel(_, _) = async { return Error NoProviderConfigured }
        member _.BuildPlatform(_, _, _) = None
    }

let private profile =
    { new IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

let private aiApp (factStore: FactStoreMode) : AIServerApp =
    AIServerApp.createFrom factory profile {
        ServerApp.empty with
            Config = {
                ServerConfig.defaults with
                    FactStore = factStore
            }
    }

let private answeringNames (app: AIServerApp) =
    app.AIAnsweringTools |> List.map (fun (d, _) -> d.Name)

/// The registry `composeAI` registers, read back out of the composition's
/// own service registrations.
let private composedRegistry (app: AIServerApp) : AIToolRegistry.AIToolRegistry =
    let composed = composeAI app
    let services = ServiceCollection() :> IServiceCollection

    match composed.Extensions.ServiceConfig with
    | Some configure -> configure services |> ignore
    | None -> failtest "composeAI registers its services"

    services
    |> Seq.pick (fun d ->
        if d.ServiceType = typeof<AIToolRegistry.AIToolRegistry> then
            match d.ImplementationInstance with
            | :? AIToolRegistry.AIToolRegistry as r -> Some r
            | _ -> None
        else
            None)

let private compositionTests =
    testList "the composition" [
        test "withRenderTools composes both tools under the fact store, once however often it is called" {
            let once = aiApp EnabledFactStore |> PresentationCompose.withRenderTools
            Expect.equal (answeringNames once) [ "render_chart"; "render_table" ] "both tools"

            let twice = once |> PresentationCompose.withRenderTools
            Expect.equal (answeringNames twice) [ "render_chart"; "render_table" ] "idempotent"
        }

        test "without the fact store withRenderTools changes nothing" {
            let before = aiApp NoFactStore
            let after = before |> PresentationCompose.withRenderTools
            Expect.isTrue (obj.ReferenceEquals(before, after)) "the app itself, unchanged"
        }

        test
            "composeAI registers the composed tools through the record-form funnel, and a deployment that does not compose them has neither" {
            let registry =
                composedRegistry (aiApp EnabledFactStore |> PresentationCompose.withRenderTools)

            for toolName in [ RenderTools.ChartToolName; RenderTools.TableToolName ] do
                match registry.FindByName toolName with
                | Some tool ->
                    Expect.isSome tool.Answer (sprintf "%s answers the record form" toolName)
                    Expect.equal tool.Definition.Location ServerResident "server-resident"
                    Expect.equal tool.Definition.Effects ToolEffectDeclaration.readFacts "declares ReadFacts only"
                | None -> failtestf "%s did not reach the AI tool registry" toolName

            let plain = composedRegistry (aiApp EnabledFactStore)

            for toolName in [ RenderTools.ChartToolName; RenderTools.TableToolName ] do
                Expect.isNone (plain.FindByName toolName) (sprintf "%s is absent unless composed" toolName)
        }

        test "composeAI refuses an answering tool whose name collides with a string tool" {
            let colliding =
                aiApp EnabledFactStore
                |> AIServerApp.withAIAnsweringTools [
                    {
                        RenderTools.definition RenderTool.Chart with
                            Name = "query_facts"
                    },
                    RenderTools.answer RenderTool.Chart
                ]

            let withStringTool = {
                colliding with
                    Base = {
                        colliding.Base with
                            AITools = colliding.Base.AITools @ [ FactQueryTool.definition, FactQueryTool.execute ]
                    }
            }

            Expect.throwsC (fun () -> composeAI withStringTool |> ignore) (fun ex ->
                Expect.stringContains ex.Message "query_facts" "the collision names the tool")
        }

        test "the fact tier stays free of the Fuaran-UI dependency" {
            for assembly in [ typeof<IFactStore>.Assembly; typeof<AIToolRegistry.RegisteredTool>.Assembly ] do
                let references = assembly.GetReferencedAssemblies() |> Array.map _.Name

                Expect.isFalse
                    (references |> Array.exists (fun n -> n.StartsWith "Fuaran.UI"))
                    (sprintf "%s references no Fuaran.UI assembly" (assembly.GetName().Name))
        }
    ]

// ─── 6. End to end ───────────────────────────────────────────────────

let private capabilities: AIProviderCapabilities = {
    Streaming = false
    ToolUse = true
    Vision = false
    SupportsPromptCaching = false
    SupportsTriage = false
    TriageModelId = None
    ProviderName = "test-render"
    Model = "test-render-model"
}

/// A provider whose every call is answered by `script` with the call's
/// one-based number.
type private ScriptedProvider(script: int -> AIProviderResponse) =
    let mutable calls = 0

    interface IAIProvider with
        member _.Capabilities = capabilities

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            return Ok(script (Interlocked.Increment(&calls)))
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

/// The composed fact tier — the store, the table writer over it — in a
/// request context, with both tables written.
let private composedContext () : HttpContext =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = EnabledFactStore
                }
        }
        |> FactsCompose.withFactStore
        |> FactsCompose.withFactTableWriter

    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IMetricRegistry>(registry) |> ignore
    services.AddSingleton<IFactTableRegistry>(tables) |> ignore

    match app.Extensions.ServiceConfig with
    | Some configure -> configure services |> ignore
    | None -> failtest "the composed fact tier registers its services"

    let built = services.BuildServiceProvider() :> IServiceProvider
    let scopeId = "team-" + Guid.NewGuid().ToString "N"

    let scope: StorageScope = {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- built
    ctx.Items["ToolUp.StorageScope"] <- box scope
    let resolved = ScopeResolution.remember ctx scope
    ctx.Items["ToolUp.UserId"] <- box "u"

    seed (built.GetRequiredService<IFactTableWriter>()) resolved trade "revenue"
    ctx :> HttpContext

let private endToEndTests =
    testList "end to end" [
        testCaseAsync
            "a scripted turn calls render_chart and the final message carries the block, the model having read the summary"
        <| async {
            let ctx = composedContext ()
            let args = PresentationRequest.toJson trendRequest

            let provider =
                ScriptedProvider(fun n ->
                    if n = 1 then
                        {
                            Content = ""
                            ToolCalls = [
                                {
                                    Id = "call-1"
                                    Name = RenderTools.ChartToolName
                                    Arguments = args
                                }
                            ]
                            StopReason = "tool_use"
                            Usage = None
                        }
                    else
                        {
                            Content = "Here is daily revenue by site."
                            ToolCalls = []
                            StopReason = "end_turn"
                            Usage = None
                        })

            let registry = AIToolRegistry.AIToolRegistry()

            registry.RegisterAll(
                (aiApp EnabledFactStore |> PresentationCompose.withRenderTools).AIAnsweringTools
                |> List.map (fun (definition, answer) -> AIToolRegistry.createAnsweringTool definition answer)
            )

            let events = ResizeArray<AIStreamEvent>()

            let turn =
                AIAgentEngine.TurnContext.create
                    provider
                    registry
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    ctx
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    ModelInput.empty
                    AIAgentEngine.AgentCallBudget.interactive
                    SystemPromptBuilder.AgentLoopLimits.defaults
                    (fun evt -> lock events (fun () -> events.Add evt))
                    CancellationToken.None

            let! result =
                AIAgentEngine.run turn AIAgentEngine.TurnPlan.RunLoop [
                    AIProviderMessage.text "user" "Show me daily revenue by site."
                ]

            match result with
            | Error stop -> failtestf "the turn was expected to answer, and stopped: %A" stop
            | Ok state ->
                let streamed = lock events (fun () -> List.ofSeq events)

                let modelRead =
                    streamed
                    |> List.choose (function
                        | ToolCallCompleted(_, _, content) -> Some content
                        | _ -> None)

                match modelRead with
                | [ summary ] ->
                    Expect.stringStarts
                        summary
                        "Drew a trend of revenue from fact table 'daily-trade' over 2026-10-01/2026-10-04"
                        "the model read the summary"

                    Expect.isFalse (summary.Contains "1100.25") "and no value"
                | other -> failtestf "expected one tool result, got %A" other

                match state.Attached with
                | [ BlockPart(node, provenance) ] ->
                    Expect.equal provenance.Request args "the block carries the request the model wrote"
                    Expect.stringContains node "1100.25" "and the gated values, drawn"

                    Expect.equal
                        (AIAgentEngine.LoopState.messageParts "Here is daily revenue by site." state)
                        [ TextPart "Here is daily revenue by site."; BlockPart(node, provenance) ]
                        "the final message carries its text, then the block"
                | other -> failtestf "expected one attached block, got %A" other
        }
    ]

let tests =
    testList "Phase 1132 — render tools draw a chart or table from gated facts" [
        refusalTests
        answerTests
        compositionTests
        endToEndTests
    ]