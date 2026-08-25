// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PopulationQueryToolTests

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

// ─── Phase 703 — the `query_metric_population` AI tool ───────────────
//
// Population questions go conversational: a ServerResident tool over
// Phase 701's `IFactStore.QueryPopulation`, returning a BOUNDED ranking
// plus the population summary — never the population. Covered here: the
// declaration + the one-knob double registration beside `query_facts`,
// the ranked/stats result shape, the server-side ceiling (reported, not
// merely applied), registry-resolved ordering through a COMPOSED store,
// the mixed-disclosure posture (a policy-grouped count, true rank gaps,
// the magnitude statistics gated with the members), cross-scope denial
// through a leaky store, the two typed ordering refusals, parameter
// validation, and the GP 11 / GP 13 no-store / no-AI parity.
//
// The last test list is the requirement's demo-facing acceptance, made
// executable: a 250-subject population, a fake provider, one tool call,
// and the assertion that the answer quotes a returned rendering verbatim
// while 247 of the 250 subjects never enter model context at all.

// ── Shared harness (mirrors FactQueryToolTests) ───────────────────

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

/// A draft for one subject in the population. `value` is the ranked
/// magnitude; `disclosure` is what the Phase 525 gate reads.
let private draftFor (path: string list) (value: FactValue) (disclosure: Disclosure) : FactDraft = {
    Subject = { Hierarchy = "brand"; Path = path }
    Metric = MetricRef "revenue"
    Value = value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ String.concat "/" path ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = disclosure
}

let private scalarDraft (sku: string) (value: decimal) : FactDraft =
    draftFor [ sku ] (Scalar value) Surfaceable

let private assertFact (store: IFactStore) (scope: string) (d: FactDraft) : Fact =
    match store.Assert(scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// A real (Phase 519) registry carrying one declared metric with the
/// given direction-of-better.
let private registryWith (direction: DirectionOfBetter) : IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "TestModule"
            Definition = {
                Id = "revenue"
                Name = "Revenue"
                Unit = "GBP"
                Dimensionality = "currency"
                Direction = direction
                DisplayFormat = "N0"
                Staleness = UntilSuperseded
                ProducingOperation = None
                CanonicalMethod = None
                RecomputePolicy = None
                RollUp = None
                Context = None
            }
        }
    ] []

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

let private contextFor (sp: IServiceProvider) (scopeId: string) (userId: string) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- sp

    let scope: StorageScope = {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx :> HttpContext

let private executeRaw (sp: IServiceProvider) (scopeId: string) (argsJson: string) : string =
    PopulationQueryTool.execute (contextFor sp scopeId "user-1") argsJson
    |> Async.RunSynchronously

let private executeVia (sp: IServiceProvider) (scopeId: string) (argsJson: string) : JsonElement =
    (JsonDocument.Parse(executeRaw sp scopeId argsJson)).RootElement.Clone()

let private str (el: JsonElement) (name: string) : string = el.GetProperty(name).GetString()

let private num (el: JsonElement) (name: string) : int = el.GetProperty(name).GetInt32()

let private flag (el: JsonElement) (name: string) : bool = el.GetProperty(name).GetBoolean()

let private items (el: JsonElement) (name: string) : JsonElement list =
    el.GetProperty(name).EnumerateArray() |> Seq.map _.Clone() |> List.ofSeq

let private baseArgs = """{"metric":"revenue","subject_hierarchy":"brand"}"""

/// Seed `count` scalar-valued subjects `sku-000 …`, ascending in value so
/// the ranking's identity is checkable from the index alone.
let private seedPopulation (store: IFactStore) (scope: string) (count: int) : (string * decimal) list =
    [ 0 .. count - 1 ]
    |> List.map (fun i ->
        let sku = sprintf "sku-%03d" i
        let value = decimal (1000 + (i * 7))
        assertFact store scope (scalarDraft sku value) |> ignore
        sku, value)

// ── Declaration + registration (703.C) ────────────────────────────

let registrationTests =
    testList "Phase 703 query_metric_population registration" [

        test "the declaration: ServerResident, surface Both, reserved _facts source" {
            let def = PopulationQueryTool.definition
            Expect.equal def.Name "query_metric_population" "tool name"
            Expect.equal def.Location ServerResident "runs on the server"
            Expect.equal def.Surface Both "offered on both AI surfaces"
            Expect.equal def.SourceModule FactEvents.SourceModule "the fact store's reserved _facts source"
            Expect.isNone def.EmitsActions "chat-only tool"

            Expect.equal
                (def.Parameters |> List.filter _.Required |> List.map _.Name)
                [ "metric"; "subject_hierarchy" ]
                "exactly the metric + subject hierarchy are required — a population needs no subject"

            let description = def.Description.ToLowerInvariant()

            Expect.stringContains
                description
                "query_facts"
                "the description teaches WHEN to reach for the population tool rather than the point read"

            Expect.stringContains
                description
                "direction-of-better"
                "and that ordering comes from the registry's declared direction-of-better"
        }

        test "EnabledFactStore declares BOTH fact tools on ServerApp.AITools — one knob, never two" {
            let app, sp = composedUnder EnabledFactStore None

            Expect.equal
                (app.AITools |> List.map (fun (def, _) -> def.Name))
                [ "query_facts"; "query_metric_population"; "list_metric_coverage" ]
                "the point read, the population read and (Phase 705) the discovery surface arrive together"

            Expect.isFalse (isNull (box (sp.GetService<IFactStore>()))) "the store rides the same knob"

            Expect.isTrue (isNull (sp.GetService(typeof<AIToolRegistry>))) "no AI tool registry in a no-AI composition"
        }

        test "NoFactStore composes byte-identically: neither tool declared (GP 11 / GP 13)" {
            let before = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = NoFactStore
                    }
            }

            let after = FactsCompose.withFactStore before

            Expect.isTrue (obj.ReferenceEquals(before, after)) "withFactStore returns the app itself unchanged"
            Expect.isEmpty after.AITools "no population tool declaration"
        }

        test "the composeAI-shaped pickup registers the population tool into the AI tool registry" {
            let app, _ = composedUnder EnabledFactStore None

            let registry = AIToolRegistry()
            registry.RegisterAll(app.AITools |> List.map (fun (def, exec) -> createTool def exec))

            match registry.FindByName "query_metric_population" with
            | Some tool -> Expect.equal tool.Definition.SourceModule "_facts" "the registered tool is the fact tool"
            | None -> failtest "query_metric_population did not reach the AI tool registry"
        }

        test "a deployment with neither store nor AI resolves no population surface at all" {
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- ServiceCollection().BuildServiceProvider()

            let result =
                PopulationQueryTool.execute (ctx :> HttpContext) baseArgs
                |> Async.RunSynchronously

            let el = (JsonDocument.Parse result).RootElement

            Expect.stringContains (str el "error") "not composed" "the defensive arm names the missing substrate"

            Expect.stringContains (str el "error") "query_metric_population" "and names THIS tool, not its sibling"
        }
    ]

// ── Ranking + statistics (703.A) ──────────────────────────────────

let rankingTests =
    testList "Phase 703 query_metric_population ranking + statistics" [

        testCase "a ranked population answers with renderings, true ranks, and the population summary"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 5 |> ignore

            // A shape that asserts no single magnitude: counted in the
            // population, never placed in the ranking (GP 9).
            assertFact store scope (draftFor [ "sku-cat" ] (Categorical "tier-a") Surfaceable)
            |> ignore

            let el =
                executeVia sp scope """{"metric":"revenue","subject_hierarchy":"brand","top_k":3}"""

            Expect.equal (str el "direction") "HighestFirst" "HigherIsBetter ranks descending"
            Expect.equal (num el "requestedTopK") 3 "the request is echoed"
            Expect.equal (num el "effectiveTopK") 3 "and applied"
            Expect.isFalse (flag el "topKCapped") "3 is under the ceiling"
            Expect.isTrue (flag el "truncated") "5 comparable members, 3 returned"

            match items el "ranked" with
            | [ first; second; third ] ->
                Expect.equal (num first "rank") 1 "ranks are 1-based"
                Expect.equal (num second "rank") 2 "and contiguous when nothing is withheld"
                Expect.equal (num third "rank") 3 ""
                Expect.equal (str first "subject") "brand/sku-004" "the highest-valued subject leads"
                Expect.equal (str first "rendering") "1,028" "rendered under the declared N0 format"
                Expect.equal (str first "metric") "revenue" "the registered metric id"
                Expect.equal (str first "freshness") "Fresh" "a current head is fresh under UntilSuperseded"
                Expect.equal (str first "method") "computed:rollup:1:p0" "the method identity, not the payload"
                Expect.equal (str first "periodLabel") "Q2-2026" "the valid-time label"
                Expect.equal (str second "subject") "brand/sku-003" "then the next"
                Expect.equal (str third "subject") "brand/sku-002" ""

                Expect.isFalse (first.TryGetProperty("evidence") |> fst) "raw evidence never rides the result"
                Expect.isFalse (first.TryGetProperty("disclosure") |> fst) "raw classification never rides the result"
                Expect.isFalse (first.TryGetProperty("value") |> fst) "the raw value DU never rides the result"
            | other -> failtestf "expected exactly three ranked members, got %d" (List.length other)

            Expect.equal (num el "withheldCount") 0 "nothing withheld"

            let population = el.GetProperty "population"
            Expect.equal (num population "subjectCount") 6 "the categorical member is part of the population"
            Expect.equal (num population "factCount") 6 ""
            Expect.equal (num population "comparableCount") 5 "only the scalars rank"
            Expect.equal (num population "nonComparableCount") 1 "the categorical is counted, never ranked"
            Expect.equal (num population "freshCount") 6 "every head is current"
            Expect.equal (num population "staleCount") 0 ""
            Expect.equal (str population "minimum") "1,000" "the summary renders through the same display format"
            Expect.equal (str population "maximum") "1,028" ""
            Expect.equal (str population "mean") "1,014" ""
            Expect.isFalse (flag population "valueStatisticsWithheld") "nothing restricted ⇒ the magnitudes ride"

        testCase "a COMPOSED store resolves best_first from the registry's declared direction (703.C)"
        <| fun () ->
            // The wiring this asserts was a gap until Phase 703: the
            // composed store was built registry-less, so a `best_first`
            // ordering refused with "metric 'revenue' is not registered"
            // in a deployment that had registered it. The refusal was not
            // merely unhelpful — it was untrue.
            let _, sp = composedUnder EnabledFactStore (Some(registryWith LowerIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 4 |> ignore

            let el =
                executeVia sp scope """{"metric":"revenue","subject_hierarchy":"brand","top_k":2}"""

            Expect.isFalse (el.TryGetProperty("error") |> fst) "a registered metric resolves rather than refusing"
            Expect.equal (str el "direction") "LowestFirst" "LowerIsBetter ranks ascending — best first"

            Expect.equal
                (items el "ranked" |> List.map (fun f -> str f "subject"))
                [ "brand/sku-000"; "brand/sku-001" ]
                "the lowest values lead on a lower-is-better metric"

        testCase "an explicit ordering needs no registry at all"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore None
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 3 |> ignore

            let el =
                executeVia sp scope """{"metric":"revenue","subject_hierarchy":"brand","ordering":"ascending"}"""

            Expect.equal (str el "direction") "LowestFirst" "the caller's own choice"

            Expect.equal
                (items el "ranked" |> List.map (fun f -> str f "subject"))
                [ "brand/sku-000"; "brand/sku-001"; "brand/sku-002" ]
                "ascending by value"

            // No registry ⇒ no display format ⇒ verbatim rendering.
            Expect.equal
                (items el "ranked" |> List.head |> (fun f -> str f "rendering"))
                "1000"
                "an unregistered metric renders verbatim"

        testCase "the value band filters the population BEFORE the ranking and the statistics"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 10 |> ignore

            let el =
                executeVia
                    sp
                    scope
                    """{"metric":"revenue","subject_hierarchy":"brand","value_at_least":1021,"value_at_most":1042}"""

            Expect.equal
                (items el "ranked" |> List.map (fun f -> str f "subject"))
                [ "brand/sku-006"; "brand/sku-005"; "brand/sku-004"; "brand/sku-003" ]
                "only the band's members rank"

            let population = el.GetProperty "population"
            Expect.equal (num population "factCount") 4 "the summary describes the FILTERED population"
            Expect.equal (str population "minimum") "1,021" ""
            Expect.equal (str population "maximum") "1,042" ""

        // ── Phase 705 additions to this answer ────────────────────

        testCase "the metric's declared Context rides the answer, beside the numbers it explains"
        <| fun () ->
            let narrative =
                "Net invoiced sales excluding VAT, rolled up nightly from the ledger."

            let registry =
                MetricRegistry.build [
                    {
                        Module = "TestModule"
                        Definition = {
                            Id = "revenue"
                            Name = "Revenue"
                            Unit = "GBP"
                            Dimensionality = "currency"
                            Direction = HigherIsBetter
                            DisplayFormat = "N0"
                            Staleness = UntilSuperseded
                            ProducingOperation = None
                            CanonicalMethod = None
                            RecomputePolicy = None
                            RollUp = None
                            Context = Some narrative
                        }
                    }
                ] []

            let _, sp = composedUnder EnabledFactStore (Some registry)
            let scope = newScope ()
            seedPopulation (sp.GetRequiredService<IFactStore>()) scope 3 |> ignore

            let el = executeVia sp scope baseArgs

            Expect.equal
                (str el "metricContext")
                narrative
                "an answer that quotes a rendering verbatim still needs the reader to know what the quantity IS"

            let _, spPlain = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scopePlain = newScope ()

            seedPopulation (spPlain.GetRequiredService<IFactStore>()) scopePlain 2 |> ignore

            Expect.equal
                ((executeVia spPlain scopePlain baseArgs).GetProperty("metricContext").ValueKind)
                JsonValueKind.Null
                "and a metric that declared none carries none — nothing is invented (GP 11)"

        testCase "the population's method mix is reported, and rides regardless of the magnitude suppression"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            assertFact store scope (scalarDraft "sku-a" 100m) |> ignore

            // A competing method over the same (subject, period) — D19:
            // never merged, and now countable.
            assertFact store scope {
                draftFor [ "sku-a" ] (Scalar 105m) Surfaceable with
                    Method = Computed("mmm", "2", "p9")
            }
            |> ignore

            // …and a restricted member, so the magnitude block is
            // suppressed and the mix can be seen to survive it.
            assertFact store scope (draftFor [ "sku-shut" ] (Scalar 900m) Internal)
            |> ignore

            let el =
                executeVia sp scope """{"metric":"revenue","subject_hierarchy":"brand","methods":"all_competing"}"""

            let population = el.GetProperty "population"

            Expect.isTrue (flag population "valueStatisticsWithheld") "a restricted member gates the magnitudes"

            Expect.equal
                (items population "methods"
                 |> List.map (fun m -> str m "method", num m "factCount"))
                [ "computed:mmm:2:p9", 1; "computed:rollup:1:p0", 2 ]
                "how the population was computed is existence-level — a procedure name, never a value"
    ]

// ── The ceiling (703.A) ───────────────────────────────────────────

let ceilingTests =
    testList "Phase 703 query_metric_population ceiling" [

        testCase "a request above MaxTopK is capped server-side and TOLD so, not silently shortened"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 12 |> ignore

            let el =
                executeVia sp scope """{"metric":"revenue","subject_hierarchy":"brand","top_k":5000}"""

            Expect.equal (num el "requestedTopK") 5000 "the request is reported verbatim"

            Expect.equal
                (num el "effectiveTopK")
                PopulationQuery.MaxTopK
                "the contract's ceiling, not the caller's manners"

            Expect.isTrue (flag el "topKCapped") "the model is told its k was capped"
            Expect.isFalse (flag el "truncated") "12 members fit under the ceiling, so nothing was dropped"
            Expect.equal (List.length (items el "ranked")) 12 "the whole comparable population, being smaller than k"

        testCase "a non-positive top_k clamps up to one rather than answering nothing"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 4 |> ignore

            let el =
                executeVia sp scope """{"metric":"revenue","subject_hierarchy":"brand","top_k":0}"""

            Expect.equal (num el "effectiveTopK") 1 "clamped up — a zero-k request is a caller slip, not an answer"
            Expect.equal (List.length (items el "ranked")) 1 ""
            Expect.isTrue (flag el "truncated") "and the rest of the population is reported as dropped"

        testCase "an empty population is an ANSWER, not an error"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let el = executeVia sp (newScope ()) baseArgs

            Expect.isFalse (el.TryGetProperty("error") |> fst) "nothing matched is a fact about the population"
            Expect.isEmpty (items el "ranked") "no ranking"

            let population = el.GetProperty "population"
            Expect.equal (num population "subjectCount") 0 ""
            Expect.equal (num population "factCount") 0 ""

            Expect.equal
                (population.GetProperty("minimum").ValueKind)
                JsonValueKind.Null
                "no comparable member ⇒ no magnitude to report"
    ]

// ── Disclosure at the door (703.B) ────────────────────────────────

/// A deliberately *leaky* store double: `QueryPopulation` ignores the
/// caller's scope and ranks whatever it was handed. The disclosure gate
/// must still hold the door — it re-resolves ids through the REAL
/// scope-filtered store.
type private LeakyPopulationStore(leaked: Fact list) =
    interface IFactStore with
        member _.Assert(_, _) = async { return Error "read-only double" }
        member _.Get(_, _) = async { return None }
        member _.Query(_, _) = async { return [] }

        member _.QueryWithCompetition(_, _) = async { return [] }

        member _.QuerySupersessionChain(_, _) = async { return [] }

        member _.QueryPopulation(_, _) = async {
            return
                Ok {
                    Ranked = leaked
                    Direction = HighestFirst
                    EffectiveTopK = 10
                    Truncated = false
                    Stats = PopulationStats.ofPopulation (fun _ -> Fresh) leaked
                }
        }

let disclosureTests =
    testList "Phase 703 query_metric_population disclosure" [

        testCase "restricted members fold into a policy-grouped COUNT — never an id, a subject, or a value"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            // Ranks 1 and 3 restricted; 2, 4, 5 disclosable.
            assertFact store scope (scalarDraft "sku-a" 5000m) |> ignore
            assertFact store scope (scalarDraft "sku-b" 4000m) |> ignore
            assertFact store scope (scalarDraft "sku-c" 3000m) |> ignore

            assertFact store scope (draftFor [ "sku-top" ] (Scalar 9999m) Internal)
            |> ignore

            assertFact store scope (draftFor [ "sku-mid" ] (Scalar 4500m) (Restricted "licence-x"))
            |> ignore

            let raw =
                executeRaw sp scope """{"metric":"revenue","subject_hierarchy":"brand","top_k":5}"""

            let el = (JsonDocument.Parse raw).RootElement

            Expect.equal (num el "withheldCount") 2 "both restricted members are accounted for"

            Expect.equal
                (items el "withheld" |> List.map (fun w -> str w "policyRef", num w "count"))
                [ "Internal", 1; "licence-x", 1 ]
                "grouped by policy ref — a count, not a per-subject listing"

            Expect.equal
                (items el "withheld" |> List.head |> (fun w -> str w "status"))
                (FactDisclosureVerdict.refusalText "Internal")
                "the canonical refusal wording — refusal text never drifts between doors"

            // The RENDERED forms: a bare digit run can occur by chance
            // inside a content-addressed hex id, so the probe that would
            // flake is the wrong probe. "9,999" cannot appear in a hex id.
            Expect.isFalse (raw.Contains "9,999") "the denied value is nowhere in the payload"
            Expect.isFalse (raw.Contains "4,500") "nor the other one"
            Expect.isFalse (raw.Contains "sku-top") "and neither restricted subject is named"
            Expect.isFalse (raw.Contains "sku-mid") ""

            // True ranks: the withheld members leave gaps at 1 and 3
            // rather than promoting the members below them.
            Expect.equal
                (items el "ranked" |> List.map (fun f -> num f "rank", str f "subject"))
                [ 2, "brand/sku-a"; 4, "brand/sku-b"; 5, "brand/sku-c" ]
                "the disclosable ranking keeps its true positions — gaps, not a renumbering"

            let population = el.GetProperty "population"

            Expect.isTrue
                (flag population "valueStatisticsWithheld")
                "a minimum or maximum IS a member's value, so the magnitude block is gated with the members"

            Expect.equal (population.GetProperty("minimum").ValueKind) JsonValueKind.Null ""
            Expect.equal (population.GetProperty("maximum").ValueKind) JsonValueKind.Null ""
            Expect.equal (population.GetProperty("mean").ValueKind) JsonValueKind.Null ""

            Expect.stringContains
                (str population "valueStatisticsWithheldReason")
                "not permitted"
                "and the suppression says why, so the model can explain it"

            // Existence-level facts still ride: the model can say how big
            // the population is and how much of it it could not see.
            Expect.equal (num population "factCount") 5 "the counts describe the WHOLE matched population"
            Expect.equal (num population "comparableCount") 5 ""

        testCaseAsync "a denied member writes a FactDisclosureDenied audit row at the ToolResult surface (525.E)"
        <| async {
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            assertFact store scope (draftFor [ "sku-x" ] (Scalar 19999m) Internal) |> ignore

            let! _ = PopulationQueryTool.execute (contextFor sp scope "user-1") baseArgs

            let events = sp.GetRequiredService<IEventStore>()
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal (List.length denies) 1 "exactly one deny audited"
            Expect.stringContains denies.Head.Payload "ToolResult" "the audit row names the egress surface"
            Expect.isFalse (denies.Head.Payload.Contains "19999") "the audit row never carries the value"
        }

        testCaseAsync "another scope's population is structurally unreachable (GP 4)"
        <| async {
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scopeA = newScope ()
            let scopeB = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scopeA 3 |> ignore

            let own = executeVia sp scopeA baseArgs
            Expect.equal (List.length (items own "ranked")) 3 "rankable in its own scope"

            let foreign = executeVia sp scopeB baseArgs
            Expect.isEmpty (items foreign "ranked") "no cross-scope member"
            Expect.equal (num foreign "withheldCount") 0 "and nothing to count — the read itself is scope-filtered"
        }

        testCaseAsync "a cross-scope member leaking past the store is denied at the door, never disclosed"
        <| async {
            let realStore =
                BlobFactStore.create (InMemoryBlobStorage()) (InMemoryEventStore.InMemoryEventStore())

            let scopeA = newScope ()
            let scopeB = newScope ()
            // A fractional value so the leak probe cannot false-positive
            // against a content-addressed hex id: "12345.67" carries a
            // character hex does not.
            let crossScope = assertFact realStore scopeA (scalarDraft "sku-secret" 12345.67m)

            let gate =
                FactDisclosureGate.create realStore (InMemoryEventStore.InMemoryEventStore())

            let! raw =
                PopulationQueryTool.executeWith
                    (LeakyPopulationStore [ crossScope ])
                    gate
                    None
                    (fun () -> DateTime.UtcNow)
                    scopeB
                    "user-1"
                    baseArgs

            let el = (JsonDocument.Parse raw).RootElement

            Expect.isEmpty (items el "ranked") "the leaked member never crosses the door"
            Expect.equal (num el "withheldCount") 1 "it is counted as withheld, never silently dropped"

            Expect.equal
                (items el "withheld" |> List.head |> (fun w -> str w "policyRef"))
                "unknown-fact"
                "unresolvable in this scope ⇒ unknown-id deny"

            Expect.isFalse (raw.Contains "12345.67") "the cross-scope value is nowhere in the payload"
            Expect.isFalse (raw.Contains "sku-secret") "nor its subject"
        }
    ]

// ── Ordering refusals (703.A / GP 9) ──────────────────────────────

let refusalTests =
    testList "Phase 703 query_metric_population ordering refusals" [

        testCase "an unregistered metric refuses best_first with the registration remedy named"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore None
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 2 |> ignore

            let el = executeVia sp scope baseArgs
            let error = str el "error"

            Expect.stringContains error "not registered" "the refusal names the gap"
            Expect.stringContains error "revenue" "and the metric"
            Expect.stringContains error "Ascending" "and the caller-side remedy"
            Expect.isFalse (el.TryGetProperty("ranked") |> fst) "no ranking on a refusal"

        testCase "a Neutral metric refuses best_first SEPARATELY — a different fact, a different remedy"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith Neutral))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 2 |> ignore

            let error = str (executeVia sp scope baseArgs) "error"

            Expect.stringContains error "Neutral" "the refusal names the declaration"

            Expect.isFalse
                (error.Contains "not registered")
                "'has no better direction' is not 'never heard of it' — the two refusals stay distinct"

        testCase "an explicit ordering answers a Neutral metric fine"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith Neutral))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            seedPopulation store scope 2 |> ignore

            let el =
                executeVia sp scope """{"metric":"revenue","subject_hierarchy":"brand","ordering":"descending"}"""

            Expect.isFalse (el.TryGetProperty("error") |> fst) "the caller's own choice needs no declaration"
            Expect.equal (str el "direction") "HighestFirst" ""
    ]

// ── Parameter validation (703.D) ──────────────────────────────────

let validationTests =
    testList "Phase 703 query_metric_population parameter validation" [

        let expectError (args: string) (fragment: string) (label: string) =
            testCase label
            <| fun () ->
                let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
                let el = executeVia sp (newScope ()) args

                Expect.stringContains (str el "error") fragment "the error names the offending argument"

                Expect.isFalse (el.TryGetProperty("ranked") |> fst) "no result payload on a validation failure"

        expectError """{"subject_hierarchy":"brand"}""" "metric" "a missing metric is refused"

        expectError """{"metric":"revenue"}""" "subject_hierarchy" "a missing subject_hierarchy is refused"

        expectError "not json at all" "not valid JSON" "malformed argument JSON is refused"

        expectError
            """{"metric":"revenue","subject_hierarchy":"brand","ordering":"whatever"}"""
            "best_first"
            "an unrecognised ordering is refused with the accepted set enumerated, never silently defaulted"

        expectError
            """{"metric":"revenue","subject_hierarchy":"brand","methods":"some"}"""
            "all_competing"
            "an unrecognised method selection is refused the same way"

        expectError
            """{"metric":"revenue","subject_hierarchy":"brand","top_k":2.5}"""
            "whole number"
            "a fractional top_k is refused rather than truncated"

        expectError
            """{"metric":"revenue","subject_hierarchy":"brand","top_k":"three"}"""
            "must be a number"
            "a non-numeric top_k is refused"

        expectError
            """{"metric":"revenue","subject_hierarchy":"brand","level":-1}"""
            "zero or greater"
            "a negative level is refused"

        expectError
            """{"metric":"revenue","subject_hierarchy":"brand","as_of":"yesterday-ish"}"""
            "as_of"
            "an unparseable as_of instant is refused"

        expectError
            """{"metric":"revenue","subject_hierarchy":"brand","period_from":"2026-04-01T00:00:00Z","period_to":"2026-01-01T00:00:00Z"}"""
            "period_from"
            "an inverted period window is refused"

        expectError
            """{"metric":"revenue","subject_hierarchy":"brand","value_at_least":900,"value_at_most":100}"""
            "value_at_least"
            "an inverted value band is refused"

        expectError """{"metric":42,"subject_hierarchy":"brand"}""" "metric" "a non-string metric is refused"
    ]

// ── Subject-set filters (703.A) ───────────────────────────────────

let subjectSetTests =
    testList "Phase 703 query_metric_population subject set" [

        testCase "level and path_prefix describe a population without enumerating it"
        <| fun () ->
            let _, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()

            assertFact store scope (draftFor [ "acme" ] (Scalar 9000m) Surfaceable)
            |> ignore

            assertFact store scope (draftFor [ "acme"; "widget" ] (Scalar 500m) Surfaceable)
            |> ignore

            assertFact store scope (draftFor [ "acme"; "gadget" ] (Scalar 700m) Surfaceable)
            |> ignore

            assertFact store scope (draftFor [ "globex"; "thing" ] (Scalar 800m) Surfaceable)
            |> ignore

            let atLevelTwo =
                executeVia sp scope """{"metric":"revenue","subject_hierarchy":"brand","level":2}"""

            Expect.equal
                (items atLevelTwo "ranked" |> List.map (fun f -> str f "subject"))
                [ "brand/globex>thing"; "brand/acme>gadget"; "brand/acme>widget" ]
                "the roll-up at level 1 is not a peer of the SKUs below it"

            let underAcme =
                executeVia
                    sp
                    scope
                    """{"metric":"revenue","subject_hierarchy":"brand","level":2,"path_prefix":"acme"}"""

            Expect.equal
                (items underAcme "ranked" |> List.map (fun f -> str f "subject"))
                [ "brand/acme>gadget"; "brand/acme>widget" ]
                "the subtree filter admits only that brand's SKUs"
    ]

// ── The demo-facing acceptance, made executable ───────────────────
//
// "In a composed app the assistant answers 'which subject has the
// highest M?' by calling `query_metric_population` and quoting the
// returned rendering verbatim; the response contains at most the capped
// top-k regardless of population size."
//
// The half that matters and is easy to leave unasserted is the second
// clause. A test that only checks the answer is right cannot tell a tool
// call from a 250-subject dump the model happened to summarise well — so
// this one records EVERY byte the provider was ever handed and asserts
// that 247 of the 250 subjects never appear in it.

/// A fake provider that calls the population tool once, then answers by
/// reading the tool result it was handed — never any wider context. It
/// records every message it saw so the test can audit the model context.
type private PopulationAskingProvider(argsJson: string) =
    let mutable turns = 0
    let seen = ResizeArray<string>()

    /// Every byte of context this provider was ever given.
    member _.ModelContext = String.concat "\n" (List.ofSeq seen)

    member _.Turns = turns

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-population"
            Model = "test-population-model"
        }

        member _.SendMessage(messages, _tools, systemPrompt, _onStream, _retryPolicy) = async {
            turns <- turns + 1
            seen.Add(defaultArg systemPrompt "")

            for m in messages do
                seen.Add m.Content

                for tr in m.ToolResults do
                    seen.Add tr.Content

            if turns = 1 then
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "query_metric_population"
                                Arguments = argsJson
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            else
                // Answer strictly from the tool result: parse it, quote
                // the leading rendering verbatim, and say plainly that the
                // ranking was bounded.
                let toolResult =
                    messages
                    |> List.collect _.ToolResults
                    |> List.tryLast
                    |> Option.map _.Content
                    |> Option.defaultValue "{}"

                let root = (JsonDocument.Parse toolResult).RootElement

                let top = root.GetProperty("ranked").EnumerateArray() |> Seq.head

                let answer =
                    sprintf
                        "%s has the highest revenue at %s. That is the top of a ranking capped at %d members of a %d-member population."
                        (top.GetProperty("subject").GetString())
                        (top.GetProperty("rendering").GetString())
                        (root.GetProperty("effectiveTopK").GetInt32())
                        (root.GetProperty("population").GetProperty("subjectCount").GetInt32())

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
    testList "Phase 703 query_metric_population end-to-end" [

        testCaseAsync
            "a 250-subject population answers by tool call, quoting the rendering verbatim, with 247 subjects never in model context"
        <| async {
            let populationSize = 250
            let topK = 3

            let app, sp = composedUnder EnabledFactStore (Some(registryWith HigherIsBetter))
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            let seeded = seedPopulation store scope populationSize

            // The composeAI-shaped pickup: exactly what a composed
            // deployment's AI companion does with `ServerApp.AITools`.
            let registry = AIToolRegistry()
            registry.RegisterAll(app.AITools |> List.map (fun (def, exec) -> createTool def exec))

            let provider =
                PopulationAskingProvider(sprintf """{"metric":"revenue","subject_hierarchy":"brand","top_k":%d}""" topK)

            let events = ResizeArray<AIStreamEvent>()
            let onEvent (evt: AIStreamEvent) = lock events (fun () -> events.Add evt)

            let! finalMessages =
                AIAgentEngine.runAgentLoop
                    (provider :> IAIProvider)
                    registry
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    (contextFor sp scope "user-1")
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    CancellationToken.None
                    [ AIProviderMessage.text "user" "Which brand has the highest revenue?" ]
                    None
                    onEvent

            let captured = lock events (fun () -> events.ToArray() |> Array.toList)

            // (1) The population question was answered by ONE tool call —
            // not by 250 point reads, and not from the model's own memory.
            let toolCalls =
                captured
                |> List.choose (function
                    | ToolCallStarted(_, name, _) -> Some name
                    | _ -> None)

            Expect.equal toolCalls [ "query_metric_population" ] "exactly one population tool call"

            // (2) The answer quotes a RETURNED rendering verbatim. The
            // expected string is derived from the same rendering path the
            // tool uses, so this asserts identity, not resemblance.
            let topSku, topValue = seeded |> List.maxBy snd
            let expectedRendering = FactRendering.render "N0" (Scalar topValue)

            let finalContent =
                finalMessages
                |> List.filter (fun m -> m.Role = "assistant" && not (String.IsNullOrWhiteSpace m.Content))
                |> List.tryLast
                |> Option.map _.Content
                |> Option.defaultWith (fun () -> failtest "the agent loop produced no assistant answer")

            Expect.stringContains finalContent expectedRendering "the answer quotes the returned rendering verbatim"

            Expect.stringContains finalContent ("brand/" + topSku) "and names the subject the ranking led with"

            // (3) THE POINT: the full population never entered model
            // context. Subject ids are the honest probe — unlike values,
            // they cannot coincide with a summary statistic.
            let context = provider.ModelContext

            let present =
                seeded |> List.map fst |> List.filter (fun sku -> context.Contains sku)

            Expect.equal
                (List.length present)
                topK
                (sprintf
                    "exactly the returned top-%d subjects reached the model; the other %d of %d never did"
                    topK
                    (populationSize - topK)
                    populationSize)

            Expect.equal
                (present |> List.sort)
                (seeded
                 |> List.sortByDescending snd
                 |> List.truncate topK
                 |> List.map fst
                 |> List.sort)
                "and they are the top-k, not an arbitrary three"

            // (4) The bound is legible to the model rather than implicit:
            // it was told the ranking was truncated and how big the
            // population was, so the answer it composed says so.
            Expect.stringContains context "\"truncated\":true" "the tool told the model its ranking was bounded"

            Expect.stringContains
                finalContent
                (string populationSize)
                "and the model could state the population size it never saw"

            Expect.equal provider.Turns 2 "one tool turn, one answer turn"
        }
    ]

let tests =
    testList "Phase 703 query_metric_population AI tool" [
        registrationTests
        rankingTests
        ceilingTests
        disclosureTests
        refusalTests
        validationTests
        subjectSetTests
        demoTests
    ]