// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.CoverageNarrativeTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 707 — coverage narrative auto-commit ──────────────────────
//
// One knowledge chunk per populated metric, superseded when the coverage
// moves MATERIALLY and not when it merely moves. Covered here:
//
//   * the generator as a pure function of (declaration, coverage) — one
//     self-contained section per populated hierarchy, the stable
//     per-metric provenance key, the registry context quoted verbatim,
//     and the two negatives that matter (no exact count, no value);
//   * the band predicate — the ladder, what is material, and the two
//     things deliberately excluded from it;
//   * the disclosure posture, folded through Phase 706's shared
//     `PopulationDisclosure.fold` rather than a second implementation;
//   * the trigger — one commit per metric under repeated assertion,
//     batch and scalar doors behaving identically, supersession only on
//     a band crossing, and both halves of the double gate;
//   * the real knowledge-base door end to end: after repeated assertion
//     the index holds exactly ONE coverage document, and the second
//     commit replaces it rather than adding beside it.
//
// Two of these assert against the RENDERED MARKDOWN rather than against
// the document tree, deliberately: the failure mode is a number
// APPEARING in the corpus, and a test that reads named fields cannot see
// one arrive through a field it does not read.

// ── Shared doubles ────────────────────────────────────────────────

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private noopNotifications =
    { new INotificationChannel with
        member _.Publish(_, _) = async.Return()

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()
    }

/// Records every programmatic commit, so a test can count them and read
/// what was committed. `refuse` makes the door answer the way the real
/// one answers a disclosure refusal.
type private RecordingIngestor(refuse: string option) =
    let commits = ResizeArray<StorageScope * string * NarrativeDocument>()

    member _.Commits = commits |> List.ofSeq

    /// Commits keyed by the provenance settings key — the "one document
    /// per metric family" question, asked directly.
    member this.KeysCommitted =
        this.Commits
        |> List.map (fun (_, _, doc) -> doc.Provenance.Value.SettingsKey)
        |> List.distinct

    interface INarrativeIngestor with
        member _.Ingest(scope, principal, document) = async {
            commits.Add((scope, principal, document))

            match refuse with
            | Some reason -> return NarrativeIngestRefused reason
            | None -> return NarrativeIngested(sprintf "doc-%d" commits.Count)
        }

/// Preset-verdict gate double. Ids absent from `verdicts` deny as
/// `unknown-fact` — the conservative contract the real gate honours.
type private PresetGate(verdicts: Map<string, FactDisclosureVerdict>) =
    interface IFactDisclosureGate with
        member _.Check(_scopeId, _principal, _surface, factIds) = async {
            return
                factIds
                |> List.map (fun id ->
                    id,
                    verdicts
                    |> Map.tryFind id
                    |> Option.defaultValue (FactNotDisclosable "unknown-fact"))
                |> Map.ofList
        }

// ── Registry + coverage fixtures ──────────────────────────────────

let private metricDef (id: string) (context: string option) : MetricDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = "N0"
    Staleness = UntilSuperseded
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

let private rollup = Computed("rollup", "1", "p0")

let private statsOf (subjects: int) (facts: int) : PopulationStats = {
    PopulationStats.empty with
        SubjectCount = subjects
        FactCount = facts
        ComparableCount = facts
        NonComparableCount = 0
        PeriodFrom = Some q2.From
        PeriodTo = Some q2.To
        Freshness = { FreshCount = facts; StaleCount = 0 }
        MethodMix = [ "computed:rollup:1:p0", facts ]
}

let private describableCoverage
    (definition: MetricDefinition)
    (stats: PopulationStats)
    : CoverageNarrative.MetricCoverage =
    {
        Definition = definition
        Populations = [
            {
                Hierarchy = subjectDef "brand" [ "brand"; "sku" ]
                Posture = CoverageNarrative.Describable []
                Stats = Some stats
                Cited = [
                    {
                        FactId = "fact-aaa"
                        Subject = "brand/acme>sku-1"
                        Period = "Q2-2026"
                    }
                    {
                        FactId = "fact-bbb"
                        Subject = "brand/acme>sku-2"
                        Period = "Q2-2026"
                    }
                ]
            }
        ]
    }

let private generatedAt = DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero)

let private renderOf (coverage: CoverageNarrative.MetricCoverage) : string =
    NarrativeMarkdown.render (CoverageNarrative.generate generatedAt coverage)

// ── 707.A — the generator ─────────────────────────────────────────

let generatorTests =
    testList "Phase 707 generator" [

        test "one self-contained section per populated hierarchy, each naming the metric id and its tools" {
            let coverage: CoverageNarrative.MetricCoverage = {
                Definition = metricDef "revenue" (Some "Net invoiced sales excluding VAT.")
                Populations = [
                    {
                        Hierarchy = subjectDef "brand" [ "brand"; "sku" ]
                        Posture = CoverageNarrative.Describable []
                        Stats = Some(statsOf 12 40)
                        Cited = []
                    }
                    {
                        Hierarchy = subjectDef "region" [ "country" ]
                        Posture = CoverageNarrative.Describable []
                        Stats = Some(statsOf 3 3)
                        Cited = []
                    }
                ]
            }

            let document = CoverageNarrative.generate generatedAt coverage

            Expect.equal document.Sections.Length 2 "one section — therefore one chunk — per populated hierarchy"

            for section in document.Sections do
                let rendered = NarrativeMarkdown.render { document with Sections = [ section ] }

                Expect.stringContains rendered "revenue" "each section names the metric id on its own"

                Expect.stringContains
                    rendered
                    "query_metric_population"
                    "and names the tool it exists to steer toward — a chunk that only made sense beside its sibling would be a bad chunk"

                Expect.stringContains rendered "Net invoiced sales" "and carries the declared context on its own"
        }

        test "provenance is the stable per-metric key — regeneration replaces, never accumulates" {
            let coverage = describableCoverage (metricDef "revenue" None) (statsOf 12 40)
            let first = CoverageNarrative.generate generatedAt coverage

            let second =
                CoverageNarrative.generate
                    (generatedAt.AddDays 30.0)
                    (describableCoverage (metricDef "revenue" None) (statsOf 900 4000))

            let key (d: NarrativeDocument) = d.Provenance.Value.SettingsKey

            Expect.equal (key first) "metric-coverage:revenue" "the key is derived from the metric id and nothing else"

            Expect.equal
                (key second)
                (key first)
                "a month later and three bands up, the SAME key — this is what makes the knowledge base replace rather than accumulate"

            Expect.equal
                first.Provenance.Value.ModuleId
                "_facts.coverage"
                "stamped under the fact store's reserved source module"

            Expect.notEqual
                second.Provenance.Value.GeneratedAt
                first.Provenance.Value.GeneratedAt
                "the timestamp moves; it is provenance, not identity"
        }

        test "the registry context is quoted verbatim, never paraphrased" {
            let context =
                "Net invoiced sales excluding VAT, rolled up nightly from the ledger. A negative value is a credit note."

            let rendered =
                renderOf (describableCoverage (metricDef "revenue" (Some context)) (statsOf 12 40))

            Expect.stringContains rendered context "the analyst's declared prose reaches the corpus unchanged"
        }

        test "a metric with no declared context says so rather than inventing one" {
            let rendered =
                renderOf (describableCoverage (metricDef "revenue" None) (statsOf 12 40))

            Expect.stringContains
                rendered
                "records no interpretive context"
                "the absence is stated — an invented interpretation would be worse than none"
        }

        test "coverage is reported in BANDS, and the exact count never appears" {
            let rendered =
                renderOf (describableCoverage (metricDef "revenue" None) (statsOf 37_412 91_000))

            Expect.stringContains rendered "between 10,000 and 99,999" "the subject count is reported as its band"

            Expect.isFalse (rendered.Contains "37412") "the exact subject count never reaches the corpus…"
            Expect.isFalse (rendered.Contains "37,412") "…in either rendering"
            Expect.isFalse (rendered.Contains "91000") "nor the exact fact count"
            Expect.isFalse (rendered.Contains "91,000") "…in either rendering"
        }

        test "no population VALUE appears — not a minimum, a maximum, nor a mean (D4)" {
            let stats = {
                statsOf 12 40 with
                    Minimum = Some 1039.55m
                    Maximum = Some 88123.75m
                    Mean = Some 4211.10m
            }

            let rendered = renderOf (describableCoverage (metricDef "revenue" None) stats)

            for forbidden in [ "1039"; "1,039"; "88123"; "88,123"; "4211"; "4,211" ] do
                Expect.isFalse
                    (rendered.Contains forbidden)
                    (sprintf "a coverage narrative reports what is queryable, never a value — found '%s'" forbidden)
        }

        test "cited facts are stamped as fact refs, carrying no value and no rank" {
            let document =
                CoverageNarrative.generate generatedAt (describableCoverage (metricDef "revenue" None) (statsOf 12 40))

            let refs = NarrativeFacts.factRefs document

            Expect.equal
                (refs |> Set.toList |> List.sort)
                [ "fact-aaa"; "fact-bbb" ]
                "every cited fact reaches the document as a Metric span's factRef — this is what Phase 521.D stamps into the chunk metadata and what the Phase 522 join reads"

            let rendered = NarrativeMarkdown.render document

            Expect.stringContains rendered "brand/acme>sku-1" "a citation names its subject…"
            Expect.stringContains rendered "Q2-2026" "…and the period it covers"

            // The invariant, asserted on the tree rather than on the prose:
            // EVERY fact-bearing span carries coverage in its value slot —
            // the subject in the label, the period in the value. The sample
            // is drawn from the ranked head, so a rank or a magnitude
            // leaking into that slot is the failure this pins.
            let citedSpans =
                document.Sections
                |> List.collect _.Elements
                |> List.collect (fun element ->
                    match element with
                    | BulletList items -> items |> List.collect id
                    | _ -> [])
                |> List.choose (fun span ->
                    match span with
                    | Metric(label, value, Some factRef) -> Some(label, value, factRef)
                    | _ -> None)

            Expect.equal (List.length citedSpans) 2 "both citations reached the document as fact-bearing spans"

            for label, value, _ in citedSpans do
                Expect.stringStarts label "brand/" "the label is the subject path…"

                Expect.equal
                    value
                    "Q2-2026"
                    "…and the value slot is the PERIOD, never the fact's value and never its rank"
        }

        test "a metric with no populated hierarchy is not committed at all" {
            let coverage: CoverageNarrative.MetricCoverage = {
                Definition = metricDef "revenue" None
                Populations = []
            }

            Expect.isFalse
                (CoverageNarrative.shouldCommit coverage)
                "a document with no sections has no chunks; committing one would be a listing row that can never be retrieved"

            Expect.isTrue
                (CoverageNarrative.shouldCommit (describableCoverage (metricDef "revenue" None) (statsOf 1 1)))
                "one populated hierarchy is enough"
        }
    ]

// ── 707.B — the material-change band ──────────────────────────────

let bandTests =
    testList "Phase 707 material-change band" [

        test "the cardinality ladder is exact at every boundary" {
            let cases = [
                0, 0
                1, 1
                9, 1
                10, 2
                99, 2
                100, 3
                999, 3
                1_000, 4
                9_999, 4
                10_000, 5
                99_999, 5
                100_000, 6
                999_999, 6
                1_000_000, 7
                50_000_000, 7
            ]

            for count, expected in cases do
                Expect.equal
                    (CoverageNarrative.cardinalityBand count)
                    expected
                    (sprintf "band of %d — integer comparisons, so a boundary cannot move with a runtime's log10" count)

            Expect.equal
                (CoverageNarrative.cardinalityBand -5)
                0
                "a negative count cannot arise, and bands as 'none' if it does"
        }

        test "more facts WITHIN a band is not material" {
            let definition = metricDef "revenue" None

            let before =
                CoverageNarrative.bandOf (describableCoverage definition (statsOf 12 40))

            let after =
                CoverageNarrative.bandOf (describableCoverage definition (statsOf 61 88))

            Expect.isFalse
                (CoverageNarrative.isMaterialChange (Some before) after)
                "twelve subjects to sixty-one is a real change and not a MATERIAL one — this is the whole point of the phase"
        }

        test "crossing a cardinality band IS material" {
            let definition = metricDef "revenue" None

            let before =
                CoverageNarrative.bandOf (describableCoverage definition (statsOf 61 88))

            let after =
                CoverageNarrative.bandOf (describableCoverage definition (statsOf 120 140))

            Expect.isTrue
                (CoverageNarrative.isMaterialChange (Some before) after)
                "sixty-one to a hundred and twenty crosses a band, and the document's own sentence changes with it"
        }

        test "nothing committed yet is material by definition" {
            let band =
                CoverageNarrative.bandOf (describableCoverage (metricDef "revenue" None) (statsOf 12 40))

            Expect.isTrue
                (CoverageNarrative.isMaterialChange None band)
                "the first regeneration always commits — including the one after a restart, which is a redundant overwrite and not a second document"
        }

        test "a new period IS material" {
            let definition = metricDef "revenue" None

            let before =
                CoverageNarrative.bandOf (describableCoverage definition (statsOf 12 40))

            let extended = {
                statsOf 12 40 with
                    PeriodTo = Some q3.To
            }

            Expect.isTrue
                (CoverageNarrative.isMaterialChange
                    (Some before)
                    (CoverageNarrative.bandOf (describableCoverage definition extended)))
                "the period reach is a sentence the document makes"
        }

        test "a new method in the mix IS material; the mix's COUNTS are not" {
            let definition = metricDef "revenue" None

            let before =
                CoverageNarrative.bandOf (describableCoverage definition (statsOf 12 40))

            let recounted = {
                statsOf 12 40 with
                    MethodMix = [ "computed:rollup:1:p0", 39 ]
            }

            Expect.isFalse
                (CoverageNarrative.isMaterialChange
                    (Some before)
                    (CoverageNarrative.bandOf (describableCoverage definition recounted)))
                "the mix is compared as a SET — a per-method count is already covered by the cardinality bands"

            let second = {
                statsOf 12 40 with
                    MethodMix = [ "asserted:analyst", 3; "computed:rollup:1:p0", 40 ]
            }

            Expect.isTrue
                (CoverageNarrative.isMaterialChange
                    (Some before)
                    (CoverageNarrative.bandOf (describableCoverage definition second)))
                "a second estimator appearing changes what the document says about how the numbers were produced"
        }

        test "FRESHNESS is deliberately outside the band — a document must not supersede on a timer" {
            let definition = metricDef "revenue" None

            let before =
                CoverageNarrative.bandOf (describableCoverage definition (statsOf 12 40))

            let staled = {
                statsOf 12 40 with
                    Freshness = { FreshCount = 0; StaleCount = 40 }
            }

            Expect.isFalse
                (CoverageNarrative.isMaterialChange
                    (Some before)
                    (CoverageNarrative.bandOf (describableCoverage definition staled)))
                "a fresh/stale split moves with the wall clock and with nothing anyone did"

            let rendered = renderOf (describableCoverage definition staled)

            Expect.isFalse
                (rendered.ToLowerInvariant().Contains "stale")
                "and it follows that the document must not report freshness either — a standing document may only claim what its supersession rule keeps true"
        }

        test "a changed declaration IS material — a confidently wrong document is worse than a redundant rewrite" {
            let before =
                CoverageNarrative.bandOf (
                    describableCoverage (metricDef "revenue" (Some "Old reading.")) (statsOf 12 40)
                )

            let after =
                CoverageNarrative.bandOf (
                    describableCoverage (metricDef "revenue" (Some "New reading.")) (statsOf 12 40)
                )

            Expect.isTrue
                (CoverageNarrative.isMaterialChange (Some before) after)
                "the context is quoted into the corpus, so a context edit must reach it"
        }

        test "the disclosure posture is in the band — a population becoming visible changes the document" {
            let definition = metricDef "revenue" None

            let described =
                CoverageNarrative.bandOf (describableCoverage definition (statsOf 12 40))

            let restricted =
                CoverageNarrative.bandOf {
                    Definition = definition
                    Populations = [
                        {
                            Hierarchy = subjectDef "brand" [ "brand"; "sku" ]
                            Posture = CoverageNarrative.WhollyRestricted [ "Internal" ]
                            Stats = None
                            Cited = []
                        }
                    ]
                }

            Expect.isTrue
                (CoverageNarrative.isMaterialChange (Some described) restricted)
                "described ⇒ restricted is material"

            Expect.isTrue
                (CoverageNarrative.isMaterialChange (Some restricted) described)
                "and so is the other direction"
        }
    ]

// ── 707.B — the disclosure refusal path (705.B, same fold) ────────

let private restrictedCoverage (posture: CoverageNarrative.CoveragePosture) : CoverageNarrative.MetricCoverage = {
    Definition = metricDef "revenue" (Some "Net invoiced sales excluding VAT.")
    Populations = [
        {
            Hierarchy = subjectDef "brand" [ "brand"; "sku" ]
            Posture = posture
            Stats = None
            Cited = []
        }
    ]
}

let disclosureTests =
    testList "Phase 707 disclosure posture" [

        test "a wholly restricted population reports EXISTENCE and POLICY, and nothing else" {
            let coverage =
                restrictedCoverage (CoverageNarrative.WhollyRestricted [ "Internal" ])

            let rendered = renderOf coverage
            let document = CoverageNarrative.generate generatedAt coverage

            Expect.stringContains rendered "is tracked in this hierarchy" "existence is disclosed"
            Expect.stringContains rendered "Internal" "and the restricting policy is named"

            Expect.isFalse
                (rendered.Contains "between")
                "no cardinality band — this is 705.B's outcome, and a band would be distribution detail"

            Expect.isFalse (rendered.Contains "Subjects tracked") "no coverage grid at all"

            Expect.isEmpty
                (NarrativeFacts.factRefs document)
                "and no citation — a document citing facts this principal may not publish would be refused at the egress door, correctly and pointlessly"
        }

        test "an unprobeable population says it could not be checked, rather than assuming either answer" {
            let rendered = renderOf (restrictedCoverage CoverageNarrative.Unprobed)

            Expect.stringContains rendered "no fact in it carries a rankable value" "the reason is stated"

            Expect.stringContains
                rendered
                "withheld rather than assumed"
                "'I could not check' is reported as itself — the 705.B posture"
        }

        test "a partly restricted population is described AND says what was withheld" {
            let coverage: CoverageNarrative.MetricCoverage = {
                Definition = metricDef "revenue" None
                Populations = [
                    {
                        Hierarchy = subjectDef "brand" [ "brand"; "sku" ]
                        Posture = CoverageNarrative.Describable [ "Internal" ]
                        Stats = Some(statsOf 12 40)
                        Cited = []
                    }
                ]
            }

            let rendered = renderOf coverage

            Expect.stringContains rendered "between 10 and 99" "the counts describe the WHOLE matched population…"

            Expect.stringContains
                rendered
                "restricted to you under Internal"
                "…and the document says part of it is withheld from the reader"
        }

        testCaseAsync "readHierarchy folds the gate's verdicts through the SHARED PopulationDisclosure fold"
        <| async {
            let storage = InMemoryBlobStorage()
            let events = InMemoryEventStore.InMemoryEventStore()

            let registry =
                registryOf [ metricDef "revenue" None ] [ subjectDef "brand" [ "brand"; "sku" ] ]

            let store = BlobFactStore.createWithRegistry storage events (Some registry)
            let scope = "scope-" + Guid.NewGuid().ToString("N")

            let draft (sku: string) (value: decimal) : FactDraft = {
                Subject = { Hierarchy = "brand"; Path = [ sku ] }
                Metric = MetricRef "revenue"
                Value = Scalar value
                Period = q2
                Method = rollup
                Evidence = {
                    ResultRef = None
                    InputHashes = [ sku ]
                    TriggerRef = None
                }
                Confidence = None
                Disclosure = Surfaceable
            }

            let! a = store.Assert(scope, draft "sku-1" 10m)
            let! b = store.Assert(scope, draft "sku-2" 20m)

            let idOf r =
                match r with
                | Ok(f: Fact) -> f.FactId
                | Error e -> failtestf "assert failed: %s" e

            let hierarchy = subjectDef "brand" [ "brand"; "sku" ]

            // Every head denied ⇒ the wholly-restricted arm.
            let denied = PresetGate(Map.empty) :> IFactDisclosureGate

            let! whollyRestricted = CoverageNarrative.readHierarchy store denied scope "user-1" "revenue" hierarchy

            match whollyRestricted with
            | Some coverage ->
                match coverage.Posture with
                | CoverageNarrative.WhollyRestricted policies ->
                    Expect.equal
                        policies
                        [ "unknown-fact" ]
                        "the conservative deny reaches the posture with its policy ref"
                | other -> failtestf "expected WhollyRestricted, got %A" other

                Expect.isNone coverage.Stats "no detail survives a wholly-restricted probe"
                Expect.isEmpty coverage.Cited "and nothing is cited"
            | None -> failtest "the pair holds facts and must produce a coverage row"

            // One head allowed ⇒ described, and only the allowed one cited.
            let partial =
                PresetGate(Map.ofList [ idOf a, FactDisclosable ]) :> IFactDisclosureGate

            let! described = CoverageNarrative.readHierarchy store partial scope "user-1" "revenue" hierarchy

            match described with
            | Some coverage ->
                match coverage.Posture with
                | CoverageNarrative.Describable withheld ->
                    Expect.equal
                        withheld
                        [ "unknown-fact" ]
                        "the partly-restricted arm names what the probe was refused"
                | other -> failtestf "expected Describable, got %A" other

                Expect.equal
                    (coverage.Stats |> Option.map _.FactCount)
                    (Some 2)
                    "the counts describe the whole matched population"

                Expect.equal
                    (coverage.Cited |> List.map _.FactId)
                    [ idOf a ]
                    "only the disclosable head is cited; the denied one is counted and never named"

                Expect.isFalse (coverage.Cited |> List.exists (fun c -> c.FactId = idOf b)) "the denied head is absent"
            | None -> failtest "the pair holds facts and must produce a coverage row"
        }

        testCaseAsync "a pair holding no facts produces no coverage row at all"
        <| async {
            let storage = InMemoryBlobStorage()
            let events = InMemoryEventStore.InMemoryEventStore()

            let registry =
                registryOf [ metricDef "revenue" None ] [ subjectDef "brand" [ "brand" ] ]

            let store = BlobFactStore.createWithRegistry storage events (Some registry)
            let gate = PresetGate(Map.empty) :> IFactDisclosureGate

            let! empty =
                CoverageNarrative.readHierarchy
                    store
                    gate
                    ("scope-" + Guid.NewGuid().ToString("N"))
                    "user-1"
                    "revenue"
                    (subjectDef "brand" [ "brand" ])

            Expect.isNone empty "an empty pair is omitted, never reported as a zero row"
        }
    ]

// ── 707.B / 707.C — the trigger and the double gate ───────────────

let private triggerRegistry =
    registryOf [ metricDef "revenue" (Some "Net invoiced sales excluding VAT.") ] [
        subjectDef "brand" [ "brand"; "sku" ]
    ]

/// The composed fact tier the way a deployment builds it, with the
/// coverage knob applied (or not).
let private composed
    (armed: bool)
    (ingestor: INarrativeIngestor option)
    (options: CoverageNarrative.CoverageNarrativeOptions)
    : ServiceProvider =
    let baseApp =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = EnabledFactStore
                }
        }
        |> FactsCompose.withFactStore

    let app =
        if armed then
            FactsCompose.withCoverageNarratives options baseApp
        else
            baseApp

    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<ILogger>(noopLogger) |> ignore
    services.AddSingleton<IMetricRegistry>(triggerRegistry) |> ignore

    ingestor
    |> Option.iter (fun i -> services.AddSingleton<INarrativeIngestor>(i) |> ignore)

    match app.Extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    services.BuildServiceProvider()

let private triggerScope = "fact-scope"

let private options =
    CoverageNarrative.CoverageNarrativeOptions.sameContainer "coverage-reader"

let private skuDraft (sku: string) : FactDraft = {
    Subject = { Hierarchy = "brand"; Path = [ sku ] }
    Metric = MetricRef "revenue"
    Value = Scalar 10m
    Period = q2
    Method = rollup
    Evidence = {
        ResultRef = None
        InputHashes = [ sku ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private waitIdle (store: IFactStore) =
    match store with
    | :? CoverageNarrative.CoverageNarrativeFactStore as trigger -> trigger.WaitIdle()
    | _ -> async.Return()

let triggerTests =
    testList "Phase 707 commit trigger" [

        testCaseAsync "repeated assertion inside one band commits ONE document, once"
        <| async {
            let ingestor = RecordingIngestor None
            let sp = composed true (Some(ingestor :> INarrativeIngestor)) options
            let store = sp.GetRequiredService<IFactStore>()

            for i in 1..8 do
                let! _ = store.Assert(triggerScope, skuDraft (sprintf "sku-%d" i))
                ()

            do! waitIdle store

            Expect.equal
                ingestor.KeysCommitted
                [ "metric-coverage:revenue" ]
                "one metric family, one provenance key — never one per subject"

            Expect.equal
                (List.length ingestor.Commits)
                1
                "eight assertions, all inside the same cardinality band, produced exactly ONE commit"

            let _, principal, document = ingestor.Commits |> List.head

            Expect.equal
                principal
                "coverage-reader"
                "committed as the declared principal, whose permissions the gate judged"

            Expect.equal document.Sections.Length 1 "one populated hierarchy ⇒ one chunk"
        }

        testCaseAsync "crossing a band supersedes; the key is unchanged, so it replaces rather than accumulates"
        <| async {
            let ingestor = RecordingIngestor None
            let sp = composed true (Some(ingestor :> INarrativeIngestor)) options
            let store = sp.GetRequiredService<IFactStore>()

            for i in 1..8 do
                let! _ = store.Assert(triggerScope, skuDraft (sprintf "sku-%d" i))
                ()

            do! waitIdle store
            let afterFirstBand = List.length ingestor.Commits

            // Still band 1 (under ten subjects): nothing new.
            let! _ = store.Assert(triggerScope, skuDraft "sku-9")
            do! waitIdle store

            Expect.equal
                (List.length ingestor.Commits)
                afterFirstBand
                "a ninth subject is still 'fewer than 10' — the document's sentence did not change, so neither did the document"

            // Tenth subject crosses into "between 10 and 99".
            for i in 10..14 do
                let! _ = store.Assert(triggerScope, skuDraft (sprintf "sku-%d" i))
                ()

            do! waitIdle store

            Expect.isGreaterThan
                (List.length ingestor.Commits)
                afterFirstBand
                "crossing the band regenerates the document"

            Expect.equal
                ingestor.KeysCommitted
                [ "metric-coverage:revenue" ]
                "and every commit lands on the same provenance key — supersession, not a second document"
        }

        testCaseAsync "the BATCH door triggers exactly as the scalar door does"
        <| async {
            let ingestor = RecordingIngestor None
            let sp = composed true (Some(ingestor :> INarrativeIngestor)) options
            let store = sp.GetRequiredService<IFactStore>()

            let! receipt = store.AssertBatch(triggerScope, [ for i in 1..6 -> skuDraft (sprintf "sku-%d" i) ])

            Expect.isOk receipt "the batch asserted"
            do! waitIdle store

            Expect.equal
                (List.length ingestor.Commits)
                1
                "one batch, one regeneration — both doors hand the same hook the same draft list, so neither can be armed without the other"

            Expect.equal ingestor.KeysCommitted [ "metric-coverage:revenue" ] "under the metric's own key"
        }

        testCaseAsync "a refused commit is retried by the next assertion; a successful one is not"
        <| async {
            let refusing =
                RecordingIngestor(Some "Narrative commit refused: 1 referenced fact(s) are not disclosable")

            let sp = composed true (Some(refusing :> INarrativeIngestor)) options
            let store = sp.GetRequiredService<IFactStore>()

            let! _ = store.Assert(triggerScope, skuDraft "sku-1")
            do! waitIdle store
            let! _ = store.Assert(triggerScope, skuDraft "sku-2")
            do! waitIdle store

            Expect.equal
                (List.length refusing.Commits)
                2
                "the band is recorded only on SUCCESS, so a refusal is retried rather than remembered as done"
        }

        testCaseAsync "no INarrativeIngestor composed ⇒ dormant, and nothing is read (the second gate)"
        <| async {
            let sp = composed true None options
            let store = sp.GetRequiredService<IFactStore>()

            let! outcome = store.Assert(triggerScope, skuDraft "sku-1")
            Expect.isOk outcome "the assertion is unaffected — derived state cannot fail an assert"

            do! waitIdle store

            let! facts = store.Query(triggerScope, FactQuery.all)
            Expect.equal (List.length facts) 1 "and the store behaves exactly as it did before the knob was armed"
        }

        testCaseAsync "an undeclared scope commits nothing"
        <| async {
            let ingestor = RecordingIngestor None

            let scoped =
                CoverageNarrative.CoverageNarrativeOptions.forScopes "coverage-reader" [
                    {
                        ScopeId = "declared-scope"
                        Container = "declared-scope"
                        Persist = true
                    }
                ]

            let sp = composed true (Some(ingestor :> INarrativeIngestor)) scoped
            let store = sp.GetRequiredService<IFactStore>()

            let! _ = store.Assert("some-other-scope", skuDraft "sku-1")
            do! waitIdle store
            Expect.isEmpty ingestor.Commits "a scope the deployment did not name gets nothing"

            let! _ = store.Assert("declared-scope", skuDraft "sku-1")
            do! waitIdle store
            Expect.equal (List.length ingestor.Commits) 1 "and a scope it did name gets its coverage"

            let scope, _, _ = ingestor.Commits |> List.head

            Expect.equal
                scope.Container
                "declared-scope"
                "committed into the container the deployment declared, never one derived from the fact scope id"
        }

        testCaseAsync "an unregistered metric is skipped — there is no family to describe"
        <| async {
            let ingestor = RecordingIngestor None
            let sp = composed true (Some(ingestor :> INarrativeIngestor)) options
            let store = sp.GetRequiredService<IFactStore>()

            let! _ =
                store.Assert(
                    triggerScope,
                    {
                        skuDraft "sku-1" with
                            Metric = MetricRef "not_registered"
                    }
                )

            do! waitIdle store

            Expect.isEmpty
                ingestor.Commits
                "a metric the registry never declared has no context to quote and no name to give"
        }

        test "opt-out: without the knob the composed store is NOT decorated" {
            let ingestor = RecordingIngestor None
            let sp = composed false (Some(ingestor :> INarrativeIngestor)) options
            let store = sp.GetRequiredService<IFactStore>()

            Expect.isFalse
                (store :? CoverageNarrative.CoverageNarrativeFactStore)
                "a deployment that never calls withCoverageNarratives resolves the plain store — byte-for-byte the pre-707 composition (GP 11 / GP 13)"

            let armed = composed true (Some(ingestor :> INarrativeIngestor)) options

            Expect.isTrue
                (armed.GetRequiredService<IFactStore>() :? CoverageNarrative.CoverageNarrativeFactStore)
                "and one that does gets the decorator — the check is capable of telling them apart"
        }

        testCaseAsync "opt-out: asserting through an undecorated store commits nothing"
        <| async {
            let ingestor = RecordingIngestor None
            let sp = composed false (Some(ingestor :> INarrativeIngestor)) options
            let store = sp.GetRequiredService<IFactStore>()

            let! outcome = store.AssertBatch(triggerScope, [ for i in 1..4 -> skuDraft (sprintf "sku-%d" i) ])
            Expect.isOk outcome "the assertions land"
            Expect.isEmpty ingestor.Commits "and nothing is published — an ingestor sitting in DI is not a trigger"
        }
    ]

// ── The real knowledge-base door, end to end ──────────────────────

let ingestorDoorTests =
    testList "Phase 707 knowledge-base commit door" [

        testCaseAsync "repeated commits under one key leave the knowledge base holding exactly ONE document"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let services = ServiceCollection()
            services.AddSingleton<IBlobStorage>(storage) |> ignore
            services.AddSingleton<INotificationChannel>(noopNotifications) |> ignore
            services.AddSingleton<ILogger>(noopLogger) |> ignore
            services.AddSingleton<IngestionQueue>(IngestionQueue()) |> ignore
            let sp = services.BuildServiceProvider()

            let ingestor = KnowledgeBase.ServerApiNarrativeIngestor.create sp

            let scope: StorageScope = {
                ScopeId = "team-a"
                Container = "team-a"
                Persist = true
            }

            let commit (stats: PopulationStats) =
                ingestor.Ingest(
                    scope,
                    "coverage-reader",
                    CoverageNarrative.generate generatedAt (describableCoverage (metricDef "revenue" None) stats)
                )

            let! first = commit (statsOf 12 40)
            let! second = commit (statsOf 900 4_000)

            let idOf outcome =
                match outcome with
                | NarrativeIngested id -> id
                | other -> failtestf "expected a commit, got %A" other

            Expect.equal
                (idOf second)
                (idOf first)
                "the second commit REPLACED the first in place — same provenance key, same document id"

            let! index = KnowledgeBase.ServerIndexStorage.loadIndex storage scope.Container

            Expect.equal
                (List.length index)
                1
                "and the knowledge base holds one coverage document for the metric, not one per regeneration"

            Expect.equal (index |> List.head |> _.FileType) "narrative" "committed through the ordinary narrative path"
        }

        testCaseAsync "the door honours the ingestion path's disclosure refusal, verbatim"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let services = ServiceCollection()
            services.AddSingleton<IBlobStorage>(storage) |> ignore
            services.AddSingleton<INotificationChannel>(noopNotifications) |> ignore
            services.AddSingleton<ILogger>(noopLogger) |> ignore
            services.AddSingleton<IngestionQueue>(IngestionQueue()) |> ignore

            // The gate the knowledge base reads at the publication surface
            // — the SAME registration the fact companion's compose makes.
            services.AddSingleton<IFactDisclosureGate>(
                PresetGate(Map.ofList [ "fact-aaa", FactNotDisclosable "Internal" ]) :> IFactDisclosureGate
            )
            |> ignore

            let sp = services.BuildServiceProvider()
            let ingestor = KnowledgeBase.ServerApiNarrativeIngestor.create sp

            let! outcome =
                ingestor.Ingest(
                    {
                        ScopeId = "team-b"
                        Container = "team-b"
                        Persist = true
                    },
                    "coverage-reader",
                    CoverageNarrative.generate
                        generatedAt
                        (describableCoverage (metricDef "revenue" None) (statsOf 12 40))
                )

            match outcome with
            | NarrativeIngestRefused reason ->
                Expect.stringContains reason "fact-aaa" "the refusal names the offending ref"
                Expect.stringContains reason "policy Internal" "and the policy that denied it"
            | other -> failtestf "expected the Phase 525.D refusal to reach the door, got %A" other

            let! index = KnowledgeBase.ServerIndexStorage.loadIndex storage "team-b"
            Expect.isEmpty index "and nothing was persisted"
        }

        testCaseAsync "a document with no provenance is refused at the door"
        <| async {
            let services = ServiceCollection()
            services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore
            services.AddSingleton<INotificationChannel>(noopNotifications) |> ignore
            services.AddSingleton<ILogger>(noopLogger) |> ignore
            services.AddSingleton<IngestionQueue>(IngestionQueue()) |> ignore
            let sp = services.BuildServiceProvider()

            let ingestor = KnowledgeBase.ServerApiNarrativeIngestor.create sp

            let document = {
                CoverageNarrative.generate generatedAt (describableCoverage (metricDef "revenue" None) (statsOf 12 40)) with
                    Provenance = None
            }

            let! outcome =
                ingestor.Ingest(
                    {
                        ScopeId = "team-c"
                        Container = "team-c"
                        Persist = true
                    },
                    "coverage-reader",
                    document
                )

            match outcome with
            | NarrativeIngestRefused reason ->
                Expect.stringContains reason "NarrativeProvenance" "the refusal names what is missing"
            | other -> failtestf "expected a refusal, got %A" other
        }

        testCaseAsync "no blob storage composed ⇒ a named FAILURE, not a null-reference fault"
        <| async {
            let services = ServiceCollection()
            services.AddSingleton<ILogger>(noopLogger) |> ignore
            let sp = services.BuildServiceProvider()

            let ingestor = KnowledgeBase.ServerApiNarrativeIngestor.create sp

            let! outcome =
                ingestor.Ingest(
                    {
                        ScopeId = "team-d"
                        Container = "team-d"
                        Persist = true
                    },
                    "coverage-reader",
                    CoverageNarrative.generate
                        generatedAt
                        (describableCoverage (metricDef "revenue" None) (statsOf 12 40))
                )

            match outcome with
            | NarrativeIngestFailed reason ->
                Expect.stringContains reason "IBlobStorage" "the failure names the missing substrate"
            | other -> failtestf "a fault is distinct from a refusal; got %A" other
        }
    ]

let tests =
    testList "CoverageNarrative (Phase 707)" [
        generatorTests
        bandTests
        disclosureTests
        triggerTests
        ingestorDoorTests
    ]