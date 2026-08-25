// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.CoverageToolTests

open System
open System.Text.Json
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 705 — `list_metric_coverage` + MetricDefinition.Context ───
//
// The discovery surface. `query_facts` and `query_metric_population` both
// take ids and neither can list them, so until this tool a model either
// received metric ids out of band or guessed — and a guessed id is
// refused, not approximated. Covered here: the registry's new `Context`
// field (declared once with the metric, surfaced on discovery AND on a
// population answer), the coverage projection over a seeded multi-metric
// store, the disclosure folding (wholly restricted ⇒ existence and policy
// only; partly restricted ⇒ described and said so; unprobeable ⇒
// withheld and SAID to be unprobeable), the metric filter and its
// discovery-shaped refusal, and the GP 11 / GP 13 zero-registration and
// no-store parity.
//
// The load-bearing negative: coverage reports counts and never a value.
// Two tests assert the absence directly against the raw payload rather
// than against a parsed field, because the failure mode is a field
// APPEARING, and a test that reads named fields cannot see one arrive.

// ── Shared harness (mirrors PopulationQueryToolTests) ─────────────

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private q3: TemporalExtent = {
    From = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q3-2026"
}

let private draft
    (hierarchy: string)
    (path: string list)
    (metric: string)
    (value: FactValue)
    (period: TemporalExtent)
    (method: MethodRef)
    (disclosure: Disclosure)
    : FactDraft =
    {
        Subject = { Hierarchy = hierarchy; Path = path }
        Metric = MetricRef metric
        Value = value
        Period = period
        Method = method
        Evidence = {
            ResultRef = None
            InputHashes = [ String.concat "/" (metric :: path) ]
            TriggerRef = None
        }
        Confidence = None
        Disclosure = disclosure
    }

let private rollup = Computed("rollup", "1", "p0")

/// The ordinary seeded fact: one hierarchy member, one scalar, Q2, the
/// rollup method, surfaceable.
let private scalarIn (hierarchy: string) (member': string) (metric: string) (value: decimal) : FactDraft =
    draft hierarchy [ member' ] metric (Scalar value) q2 rollup Surfaceable

let private assertFact (store: IFactStore) (scope: string) (d: FactDraft) : Fact =
    match store.Assert(scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

let private metricDef
    (id: string)
    (direction: DirectionOfBetter)
    (staleness: StalenessPolicy)
    (context: string option)
    : MetricDefinition =
    {
        Id = id
        Name = id.ToUpperInvariant()
        Unit = "GBP"
        Dimensionality = "currency"
        Direction = direction
        DisplayFormat = "N0"
        Staleness = staleness
        ProducingOperation = None
        CanonicalMethod = None
        RecomputePolicy = None
        RollUp = None
        Context = context
    }

let private subjectDef (id: string) (levels: string list) : SubjectDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Levels = levels
    Calendar = None
}

let private registryOf (metrics: MetricDefinition list) (subjects: SubjectDefinition list) : IMetricRegistry =
    MetricRegistry.build
        (metrics
         |> List.map (fun d -> {
             Module = "TestModule"
             Definition = d
         }))
        (subjects
         |> List.map (fun d -> {
             Module = "TestModule"
             Definition = d
         }))

/// The two-metric, two-hierarchy registry most of these tests read:
/// `revenue` carries an interpretive context and a direction; `churn` is
/// declared `Neutral` — it has no best-first order and very much has
/// coverage, which is the case the discovery surface must not refuse.
let private twoMetricRegistry: IMetricRegistry =
    registryOf [
        metricDef
            "revenue"
            HigherIsBetter
            (FreshFor(TimeSpan.FromDays 1.0))
            (Some "Net invoiced sales excluding VAT, rolled up nightly from the ledger.")
        metricDef "churn" Neutral UntilSuperseded None
    ] [ subjectDef "brand" [ "brand"; "sku" ]; subjectDef "region" [ "country" ] ]

/// The composed fact tier the way a deployment builds it.
let private composedUnder (knob: FactStoreMode) (registry: IMetricRegistry option) : ServerApp * ServiceProvider =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = knob
                }
        }
        |> FactsCompose.withFactStore

    let services = ServiceCollection()

    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    registry
    |> Option.iter (fun r -> services.AddSingleton<IMetricRegistry>(r) |> ignore)

    match app.Extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    app, services.BuildServiceProvider()

let private contextFor (sp: IServiceProvider) (scopeId: string) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- sp

    let scope: StorageScope = {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Items["ToolUp.UserId"] <- box "user-1"
    ctx :> HttpContext

let private executeRaw (sp: IServiceProvider) (scopeId: string) (argsJson: string) : string =
    CoverageTool.execute (contextFor sp scopeId) argsJson |> Async.RunSynchronously

let private executeVia (sp: IServiceProvider) (scopeId: string) (argsJson: string) : JsonElement =
    (JsonDocument.Parse(executeRaw sp scopeId argsJson)).RootElement.Clone()

let private str (el: JsonElement) (name: string) : string = el.GetProperty(name).GetString()

let private num (el: JsonElement) (name: string) : int = el.GetProperty(name).GetInt32()

let private flag (el: JsonElement) (name: string) : bool = el.GetProperty(name).GetBoolean()

let private items (el: JsonElement) (name: string) : JsonElement list =
    el.GetProperty(name).EnumerateArray() |> Seq.map _.Clone() |> List.ofSeq

let private isNullField (el: JsonElement) (name: string) : bool =
    el.GetProperty(name).ValueKind = JsonValueKind.Null

/// The metric row for `id`, or a failure naming what the report did hold
/// — a missing row is the interesting failure and "key not found" hides
/// it.
let private metricRow (el: JsonElement) (id: string) : JsonElement =
    let rows = items el "metrics"

    match rows |> List.tryFind (fun m -> str m "id" = id) with
    | Some row -> row
    | None ->
        failtestf "no coverage row for metric '%s'; the report listed %A" id (rows |> List.map (fun m -> str m "id"))

let private populationRow (metric: JsonElement) (hierarchy: string) : JsonElement =
    let rows = items metric "populations"

    match rows |> List.tryFind (fun p -> str p "hierarchy" = hierarchy) with
    | Some row -> row
    | None ->
        failtestf
            "no population row for hierarchy '%s'; the metric listed %A"
            hierarchy
            (rows |> List.map (fun p -> str p "hierarchy"))

let private noArgs = "{}"

// ── Declaration + registration (705.C) ────────────────────────────

let registrationTests =
    testList "Phase 705 list_metric_coverage registration" [

        test "the declaration: ServerResident, surface Both, reserved _facts source, ONE optional parameter" {
            let def = CoverageTool.definition
            Expect.equal def.Name "list_metric_coverage" "tool name"
            Expect.equal def.Location ServerResident "runs on the server"
            Expect.equal def.Surface Both "offered on both AI surfaces"
            Expect.equal def.SourceModule FactEvents.SourceModule "the fact store's reserved _facts source"
            Expect.isNone def.EmitsActions "chat-only tool"

            Expect.isEmpty
                (def.Parameters |> List.filter _.Required)
                "nothing is required — a discovery tool that needed an argument would need discovering"

            Expect.equal (def.Parameters |> List.map _.Name) [ "metric" ] "one optional narrowing filter"

            let description = def.Description.ToLowerInvariant()

            Expect.stringContains description "first" "the description positions it as the entry point"

            Expect.stringContains description "query_metric_population" "and names the tools whose ids it supplies"

            Expect.stringContains description "never a value" "and is explicit that it reports coverage, not values"
        }

        test "EnabledFactStore declares ALL THREE fact tools on one knob" {
            let app, sp = composedUnder EnabledFactStore None

            Expect.equal
                (app.AITools |> List.map (fun (def, _) -> def.Name))
                [ "query_facts"; "query_metric_population"; "list_metric_coverage" ]
                "discovery arrives with the reads it makes usable — a deployment cannot arm one without the others"

            Expect.isFalse (isNull (box (sp.GetService<IFactStore>()))) "the store rides the same knob"
        }

        test "NoFactStore declares no coverage tool (GP 11 / GP 13)" {
            let before = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = NoFactStore
                    }
            }

            let after = FactsCompose.withFactStore before

            Expect.isTrue (obj.ReferenceEquals(before, after)) "withFactStore returns the app itself unchanged"
            Expect.isEmpty after.AITools "no declaration of any kind"
        }

        test "the composeAI-shaped pickup registers the coverage tool into the AI tool registry" {
            let app, _ = composedUnder EnabledFactStore None

            let registry = AIToolRegistry()
            registry.RegisterAll(app.AITools |> List.map (fun (def, exec) -> createTool def exec))

            match registry.FindByName "list_metric_coverage" with
            | Some tool -> Expect.equal tool.Definition.SourceModule "_facts" "the registered tool is the fact tool"
            | None -> failtest "list_metric_coverage did not reach the AI tool registry"
        }

        test "a deployment with neither store nor AI resolves no coverage surface at all" {
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- ServiceCollection().BuildServiceProvider()

            let result =
                CoverageTool.execute (ctx :> HttpContext) noArgs |> Async.RunSynchronously

            let el = (JsonDocument.Parse result).RootElement

            Expect.stringContains (str el "error") "not composed" "the defensive arm names the missing substrate"

            Expect.stringContains (str el "error") "list_metric_coverage" "and names THIS tool, not a sibling"
        }
    ]

// ── The registry projection + Context (705.A) ─────────────────────

let definitionTests =
    testList "Phase 705 list_metric_coverage metric definitions" [

        testCase "every registered metric is reported with its declaration, including its Context"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let el = executeVia sp (newScope ()) noArgs

            Expect.equal (num el "metricCount") 2 "both declarations are reported, facts or no facts"

            let revenue = metricRow el "revenue"
            Expect.equal (str revenue "name") "REVENUE" "the display name"
            Expect.equal (str revenue "unit") "GBP" ""
            Expect.equal (str revenue "dimensionality") "currency" ""
            Expect.equal (str revenue "displayFormat") "N0" "the format a quoted number must be canonicalised through"
            Expect.equal (str revenue "direction") "HigherIsBetter" ""
            Expect.stringContains (str revenue "staleness") "FreshFor" "the policy, carrying its window"

            Expect.stringContains
                (str revenue "context")
                "Net invoiced sales"
                "the analyst's narrative for the FAMILY, declared once with the metric"

            Expect.isTrue (isNullField (metricRow el "churn") "context") "an undeclared context is absent, not invented"

        testCase "a Neutral metric is reported AND flagged as having no best-first order"
        <| fun () ->
            // Phase 701's ordering resolver has two refusals — unregistered,
            // and declared Neutral. This tool makes the first unreachable by
            // listing the ids; it cannot make the second unreachable, so it
            // answers it in advance instead of leaving the model to provoke
            // it.
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let el = executeVia sp (newScope ()) noArgs

            Expect.isTrue (flag (metricRow el "revenue") "supportsBestFirst") "a declared direction resolves best_first"

            Expect.isFalse
                (flag (metricRow el "churn") "supportsBestFirst")
                "Neutral is a declaration that there is no better direction"

            Expect.equal (str (metricRow el "churn") "direction") "Neutral" "and the declaration itself is reported"

        testCase "the registered subject hierarchies are reported with their level labels"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let el = executeVia sp (newScope ()) noArgs

            let brand =
                items el "subjectHierarchies" |> List.find (fun s -> str s "id" = "brand")

            Expect.equal
                (items brand "levels" |> List.map _.GetString())
                [ "brand"; "sku" ]
                "the shape a subject path must follow, so a path_prefix can be built without guessing"
    ]

// ── Coverage over a seeded store (705.B) ──────────────────────────

let coverageTests =
    testList "Phase 705 list_metric_coverage coverage" [

        testCase "coverage over a seeded multi-metric store: per metric, per hierarchy, with the method mix"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            // revenue over `brand`: four scalars + one categorical, two
            // quarters, one method.
            for i in 0..3 do
                assertFact store scope (scalarIn "brand" (sprintf "sku-%d" i) "revenue" (decimal (100 + i)))
                |> ignore

            assertFact store scope (draft "brand" [ "sku-cat" ] "revenue" (Categorical "tier-a") q3 rollup Surfaceable)
            |> ignore

            // churn over `region`: one scalar, and nothing at all under
            // `brand`.
            assertFact store scope (scalarIn "region" "uk" "churn" 12m) |> ignore

            let el = executeVia sp scope noArgs
            let revenue = metricRow el "revenue"

            Expect.isTrue (flag revenue "hasFacts") ""

            Expect.equal
                (items revenue "populations" |> List.map (fun p -> str p "hierarchy"))
                [ "brand" ]
                "a hierarchy holding nothing for this metric is omitted, not reported as a row of zeroes"

            let brand = populationRow revenue "brand"
            Expect.isFalse (flag brand "coverageWithheld") "nothing restricted"
            Expect.equal (num brand "subjectCount") 5 "the categorical member is part of the population"
            Expect.equal (num brand "factCount") 5 ""
            Expect.equal (num brand "comparableCount") 4 "only the scalars are rankable"
            Expect.equal (num brand "nonComparableCount") 1 "the categorical is counted, never ranked"
            Expect.equal (num brand "freshCount") 5 "every head is current"
            Expect.equal (num brand "staleCount") 0 ""
            Expect.equal (str brand "hierarchyName") "BRAND" ""

            Expect.stringContains (str brand "periodFrom") "2026-04-01" "the earliest valid-time bound seen"
            Expect.stringContains (str brand "periodTo") "2026-10-01" "and the latest — Q2 through Q3"

            Expect.equal
                (items brand "methods" |> List.map (fun m -> str m "method", num m "factCount"))
                [ "computed:rollup:1:p0", 5 ]
                "one estimator over the whole population"

            let churnRegion = populationRow (metricRow el "churn") "region"
            Expect.equal (num churnRegion "factCount") 1 "the second metric is covered in its own hierarchy"

        testCase "several competing methods are counted separately — the mix, not a merge"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            // Two methods over the SAME (subject, period): D19 competing
            // heads, never merged by the store and never merged here.
            assertFact store scope (scalarIn "brand" "sku-0" "revenue" 100m) |> ignore

            assertFact
                store
                scope
                (draft "brand" [ "sku-0" ] "revenue" (Scalar 105m) q2 (Computed("mmm", "2", "p9")) Surfaceable)
            |> ignore

            let brand = populationRow (metricRow (executeVia sp scope noArgs) "revenue") "brand"

            Expect.equal
                (items brand "methods" |> List.map (fun m -> str m "method", num m "factCount"))
                [ "computed:mmm:2:p9", 1; "computed:rollup:1:p0", 1 ]
                "both competitors counted, ordered by identity so two implementations report the same LIST"

            Expect.equal (num brand "subjectCount") 1 "one subject, computed two ways"
            Expect.equal (num brand "factCount") 2 ""

        testCase "coverage reports counts and NEVER a value — no minimum, maximum, mean, or rendering"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            // A value whose rendered form cannot occur inside a
            // content-addressed hex id, so the absence probe cannot
            // false-positive.
            assertFact store scope (scalarIn "brand" "sku-0" "revenue" 987654m) |> ignore

            let raw = executeRaw sp scope noArgs

            Expect.isFalse (raw.Contains "987654") "the raw value is nowhere in a discovery answer"
            Expect.isFalse (raw.Contains "987,654") "nor its rendering"

            Expect.isFalse
                (raw.Contains "minimum")
                "no magnitude block: values are the population tool's door, gated there"

            Expect.isFalse (raw.Contains "maximum") ""
            Expect.isFalse (raw.Contains "\"mean\"") ""

            Expect.isFalse
                (raw.Contains "sku-0")
                "and no subject is enumerated — coverage counts subjects, never lists them"

        testCase "another scope's facts are structurally invisible (GP 4)"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scopeA = newScope ()
            let scopeB = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            assertFact store scopeA (scalarIn "brand" "sku-0" "revenue" 100m) |> ignore

            Expect.isTrue
                (flag (metricRow (executeVia sp scopeA noArgs) "revenue") "hasFacts")
                "covered in its own scope"

            let foreign = metricRow (executeVia sp scopeB noArgs) "revenue"
            Expect.isFalse (flag foreign "hasFacts") "no cross-scope coverage"

            Expect.isEmpty
                (items foreign "populations")
                "and the declaration is still reported — what EXISTS is registry-level, what is HELD is scoped"
    ]

// ── Disclosure folding (705.B) ────────────────────────────────────

let disclosureTests =
    testList "Phase 705 list_metric_coverage disclosure" [

        testCase "a wholly restricted population reports EXISTENCE AND POLICY ONLY — no distribution detail"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            for i in 0..2 do
                assertFact
                    store
                    scope
                    (draft "brand" [ sprintf "sku-%d" i ] "revenue" (Scalar(decimal (100 + i))) q2 rollup Internal)
                |> ignore

            let raw = executeRaw sp scope noArgs
            let el = (JsonDocument.Parse raw).RootElement
            let brand = populationRow (metricRow el "revenue") "brand"

            Expect.isTrue (flag brand "factsExist") "existence IS disclosed — the 559.B posture"
            Expect.isTrue (flag brand "coverageWithheld") "and nothing beyond it"

            Expect.equal
                (items brand "restrictedUnderPolicies" |> List.map _.GetString())
                [ "Internal" ]
                "the policy is named, so the caller knows what to ask for"

            Expect.stringContains (str brand "coverageWithheldReason") "restricted" "and the withholding says why"

            Expect.isTrue (isNullField brand "subjectCount") "how many subjects is distribution detail"
            Expect.isTrue (isNullField brand "factCount") ""
            Expect.isTrue (isNullField brand "periodFrom") "so is the period reach"
            Expect.isTrue (isNullField brand "freshCount") "so is the freshness distribution"
            Expect.isEmpty (items brand "methods") "and so is the method mix"

            Expect.isFalse (raw.Contains "rollup") "the method identity is withheld with everything else"

        testCase "a partly restricted population IS described, and says that it is partial"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            assertFact store scope (scalarIn "brand" "sku-open" "revenue" 100m) |> ignore

            assertFact
                store
                scope
                (draft "brand" [ "sku-shut" ] "revenue" (Scalar 900m) q2 rollup (Restricted "licence-x"))
            |> ignore

            let brand = populationRow (metricRow (executeVia sp scope noArgs) "revenue") "brand"

            Expect.isFalse (flag brand "coverageWithheld") "something is visible, so the coverage is described"

            Expect.equal
                (num brand "factCount")
                2
                "and the counts describe the WHOLE population, restricted members included"

            Expect.isTrue (flag brand "partiallyRestricted") "with the partiality stated rather than implied"

            Expect.equal
                (items brand "restrictedUnderPolicies" |> List.map _.GetString())
                [ "licence-x" ]
                "naming the policy — never the member"

        testCase "a population with nothing rankable is WITHHELD and said to be unprobeable"
        <| fun () ->
            // The probe reads the ranking, and only a Scalar ranks. A
            // population of categoricals therefore offers no member to
            // check against the gate — which is neither "disclosable" nor
            // "restricted", and is reported as the third thing rather than
            // resolved to either.
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            assertFact store scope (draft "brand" [ "sku-a" ] "revenue" (Categorical "tier-a") q2 rollup Surfaceable)
            |> ignore

            let brand = populationRow (metricRow (executeVia sp scope noArgs) "revenue") "brand"

            Expect.isTrue (flag brand "factsExist") ""
            Expect.isTrue (flag brand "coverageWithheld") "cannot check ⇒ do not describe"

            Expect.isEmpty
                (items brand "restrictedUnderPolicies")
                "and no policy is invented — nothing refused it, nothing permitted it"

            Expect.stringContains
                (str brand "coverageWithheldReason")
                "rankable"
                "the reason distinguishes 'unprobeable' from 'restricted'"

        testCaseAsync "the probe's denials are audited at the ToolResult surface (525.E)"
        <| async {
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            assertFact store scope (draft "brand" [ "sku-x" ] "revenue" (Scalar 19999m) q2 rollup Internal)
            |> ignore

            let! _ = CoverageTool.execute (contextFor sp scope) noArgs

            let events = sp.GetRequiredService<IEventStore>()
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal (List.length denies) 1 "the discovery probe audits its deny like any other door"
            Expect.stringContains denies.Head.Payload "ToolResult" "naming the egress surface"
            Expect.isFalse (denies.Head.Payload.Contains "19999") "and never the value"
        }
    ]

// ── Filter, optionality, and the empty deployment ─────────────────

let filterTests =
    testList "Phase 705 list_metric_coverage filter + optionality" [

        testCase "the optional metric filter narrows the report to one declaration"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)

            let el = executeVia sp (newScope ()) """{"metric":"churn"}"""

            Expect.equal (num el "metricCount") 1 ""
            Expect.equal (items el "metrics" |> List.map (fun m -> str m "id")) [ "churn" ] "only the named metric"

        testCase "an unregistered filter is refused with the DISCOVERY remedy named"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)

            let el = executeVia sp (newScope ()) """{"metric":"sales"}"""

            Expect.stringContains (str el "error") "'sales' is not registered" "the id it could not find"

            Expect.stringContains
                (str el "error")
                "with no arguments"
                "and the remedy is THIS tool — a discovery surface that refuses without teaching discovery is the gap it exists to close"

        testCase "a non-string metric filter is refused rather than coerced"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let el = executeVia sp (newScope ()) """{"metric":7}"""
            Expect.stringContains (str el "error") "must be a string" ""

        testCase "an empty argument object is the ordinary call"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            Expect.equal (num (executeVia sp (newScope ()) noArgs) "metricCount") 2 ""

        testCase "a deployment that declared no grounding vocabulary answers plainly, in the same shape"
        <| fun () ->
            // No module declared anything ⇒ no IMetricRegistry singleton
            // (Phase 519). "Nothing is declared" is an answer, and it
            // arrives in the ordinary envelope so a consumer parses one
            // shape.
            let _, sp = composedUnder EnabledFactStore None
            let el = executeVia sp (newScope ()) noArgs

            Expect.equal (num el "metricCount") 0 ""
            Expect.isEmpty (items el "metrics") ""
            Expect.isEmpty (items el "subjectHierarchies") ""

            Expect.stringContains
                (str el "note")
                "no grounding registry"
                "the empty answer explains itself rather than reading as a fault"

        testCase "a registry with declarations carries no note"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            Expect.isTrue (isNullField (executeVia sp (newScope ()) noArgs) "note") "nothing to explain"
    ]

// ── End-to-end: the acceptance, made executable ───────────────────
//
// The phase's acceptance: "In a composed app with two registered metrics
// (one with facts across a large seeded population), `list_metric_coverage`
// reports both definitions with context, and cardinality/period/freshness
// for the populated one; the assistant can answer 'what data do you have?'
// from that one call."
//
// The clause that no unit test above can reach is the last one, and the
// half of it that matters is HOW the assistant then queries. This phase's
// whole claim is that a model which can list metrics never guesses one —
// so the provider below is constructed with NO ids at all. It calls
// discovery, reads a metric id and a hierarchy id out of the ANSWER, and
// uses those to make the population call. If discovery did not carry the
// ids, or carried them in a shape a caller cannot lift, the second call
// cannot be built and the test fails rather than passing on prose.

/// A fake provider that calls `list_metric_coverage`, derives the ids it
/// needs FROM THAT RESULT, calls `query_metric_population` with them, and
/// answers from the two tool results — never from anything else. It
/// records every message it saw, and the arguments it derived.
type private DiscoveringProvider() =
    let mutable turns = 0
    let mutable derivedArgs = ""
    let seen = ResizeArray<string>()

    /// Every byte of context this provider was ever given.
    member _.ModelContext = String.concat "\n" (List.ofSeq seen)

    /// The population-call arguments the provider built out of the
    /// discovery answer. Empty until it has read one.
    member _.DerivedArgs = derivedArgs

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-coverage"
            Model = "test-coverage-model"
        }

        member _.SendMessage(messages, _tools, systemPrompt, _onStream, _retryPolicy) = async {
            turns <- turns + 1
            seen.Add(defaultArg systemPrompt "")

            for m in messages do
                seen.Add m.Content

                for tr in m.ToolResults do
                    seen.Add tr.Content

            let lastToolResult () =
                messages
                |> List.collect _.ToolResults
                |> List.tryLast
                |> Option.map _.Content
                |> Option.defaultValue "{}"

            match turns with
            | 1 ->
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "list_metric_coverage"
                                Arguments = "{}"
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | 2 ->
                // The ids come out of the discovery answer. Nothing in
                // this provider knows what a metric is called.
                let coverage = (JsonDocument.Parse(lastToolResult ())).RootElement

                let populated =
                    coverage.GetProperty("metrics").EnumerateArray()
                    |> Seq.find _.GetProperty("hasFacts").GetBoolean()

                let hierarchy =
                    (populated.GetProperty("populations").EnumerateArray() |> Seq.head)
                        .GetProperty("hierarchy")
                        .GetString()

                derivedArgs <-
                    sprintf
                        """{"metric":"%s","subject_hierarchy":"%s","top_k":2}"""
                        (populated.GetProperty("id").GetString())
                        hierarchy

                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "query_metric_population"
                                Arguments = derivedArgs
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | _ ->
                // Answer strictly from what the two calls returned: the
                // declared interpretation, the population's size, and a
                // rendering quoted verbatim.
                let population = (JsonDocument.Parse(lastToolResult ())).RootElement

                let top = population.GetProperty("ranked").EnumerateArray() |> Seq.head

                let answer =
                    sprintf
                        "%s Across %d subjects, the highest is %s at %s."
                        (population.GetProperty("metricContext").GetString())
                        (population.GetProperty("population").GetProperty("subjectCount").GetInt32())
                        (top.GetProperty("subject").GetString())
                        (top.GetProperty("rendering").GetString())

                return
                    Ok {
                        Content = answer
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

let demoTests =
    testList "Phase 705 list_metric_coverage end-to-end" [

        testCaseAsync "the assistant discovers what the deployment holds, then queries ids it READ rather than guessed"
        <| async {
            let app, sp = composedUnder EnabledFactStore (Some twoMetricRegistry)
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            for i in 0..39 do
                assertFact store scope (scalarIn "brand" (sprintf "sku-%02d" i) "revenue" (decimal (1000 + i)))
                |> ignore

            // The composeAI-shaped pickup: exactly what a composed
            // deployment's AI companion does with `ServerApp.AITools`.
            let toolRegistry = AIToolRegistry()

            toolRegistry.RegisterAll(app.AITools |> List.map (fun (def, exec) -> createTool def exec))

            let provider = DiscoveringProvider()
            let events = ResizeArray<AIStreamEvent>()
            let onEvent (evt: AIStreamEvent) = lock events (fun () -> events.Add evt)

            let! finalMessages =
                AIAgentEngine.runAgentLoop
                    (provider :> IAIProvider)
                    toolRegistry
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    (contextFor sp scope)
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    CancellationToken.None
                    [ AIProviderMessage.text "user" "What data do you have?" ]
                    None
                    onEvent

            let captured = lock events (fun () -> events.ToArray() |> Array.toList)

            let toolCalls =
                captured
                |> List.choose (function
                    | ToolCallStarted(_, name, _) -> Some name
                    | _ -> None)

            Expect.equal
                toolCalls
                [ "list_metric_coverage"; "query_metric_population" ]
                "discovery first, then the read it made possible"

            // The ids in the second call were LIFTED from the first
            // call's answer — the provider was given none.
            Expect.stringContains
                provider.DerivedArgs
                "\"metric\":\"revenue\""
                "the metric id came out of the coverage report"

            Expect.stringContains
                provider.DerivedArgs
                "\"subject_hierarchy\":\"brand\""
                "and so did the hierarchy id — this is the guess the phase exists to remove"

            let answer =
                finalMessages
                |> List.filter (fun m -> m.Role = "assistant" && m.Content <> "")
                |> List.tryLast
                |> Option.map _.Content
                |> Option.defaultValue ""

            Expect.stringContains
                answer
                "Net invoiced sales"
                "the answer explains the metric from its DECLARED context, not from the model's priors"

            Expect.stringContains answer "40 subjects" "and reports the population's size from the read"
            Expect.stringContains answer "1,039" "quoting a returned rendering verbatim"

            // Discovery is a summary, not a dump: forty subjects were
            // covered and at most two ever entered model context.
            let named =
                [ 0..39 ]
                |> List.map (fun i -> sprintf "sku-%02d" i)
                |> List.filter provider.ModelContext.Contains

            Expect.equal
                (List.length named)
                2
                "only the top-2 the population call returned; the other 38 never reach the model at all"
        }
    ]

let tests =
    testList "CoverageTool (Phase 705)" [
        registrationTests
        definitionTests
        coverageTests
        disclosureTests
        filterTests
        demoTests
    ]