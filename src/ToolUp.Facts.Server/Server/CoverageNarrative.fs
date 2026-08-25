// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes

// ─── CoverageNarrative (Phase 707) ───────────────────────────────────
//
// **The problem this closes.** The population tier keeps per-subject
// prose out of the retrieval corpus deliberately: a fact base of 300,000
// SKUs must not become 300,000 knowledge chunks, and Phase 520's whole
// argument is that a fact is an assertion to be *queried*, never prose to
// be *retrieved*. The cost of that correctness is invisibility. A user
// asks "which SKUs have the worst elasticity?", plain vector retrieval
// finds nothing — there is nothing to find — and the model answers from
// its own head, or says it does not know, while a tool that would have
// answered exactly sits one call away.
//
// So: per registered metric that actually holds facts, **one** narrative
// describing what the deployment TRACKS — never what the values are —
// committed into the knowledge base through the ordinary ingestion path.
// A question brushing the metric family retrieves that chunk, and the
// chunk's whole job is to say "this is queryable, here is the tool and
// here is the id it takes". The corpus grows by the number of registered
// metrics, which is a property of the deployment's declarations, not of
// its data: 12 metrics over 3 hierarchies is at most 36 chunks whether
// the store holds four facts or four hundred thousand.
//
// **Three rules the rest of this file implements.**
//
//   1. *It references facts; it never copies their values* (plan D4).
//      Coverage figures are counts and period reach — derived properties
//      of a population, not a member's value. Where the document points
//      at a fact it does so by `factRef`, so citing the narrative
//      transitively cites the fact and a superseded fact mechanically
//      flags the narrative that cites it.
//
//   2. *It supersedes on a MATERIAL change, not on every assertion.*
//      A document regenerated per assert would rewrite the corpus
//      continuously, re-embed on every write, and — because the KB
//      dedups by provenance and replaces in place — churn the vector
//      store for a document whose meaning did not move. The comparison
//      is therefore over BANDS (see `CoverageBand`), and the document
//      is written in bands to match: it says "between 10,000 and 99,999
//      subjects", never "37,412", because a document that claimed the
//      exact count would be wrong the moment the next fact landed and
//      would not be rewritten to fix it.
//
//   3. *Its disclosure posture is Phase 705's, applied by the SAME fold.*
//      A metric whose population this principal may not see reports its
//      EXISTENCE and the restricting POLICY, and nothing else — the
//      705.B outcome, derived here through `PopulationDisclosure.fold`
//      (Phase 706's shared partition) rather than through a second
//      implementation that would agree for a while and then quietly not.
//
// **Off by default (GP 13), behind a double gate.** A deployment opts in
// with `FactsCompose.withCoverageNarratives`; without it nothing is
// decorated and the composition is byte-for-byte unchanged. Having opted
// in, the trigger is still inert unless an `INarrativeIngestor` is
// composed — i.e. unless the deployment has a knowledge base to commit
// into — and it checks that FIRST, before it reads anything, so a
// deployment that armed the knob without a knowledge base pays no store
// work at all.

/// The coverage-narrative generator, its material-change predicate, and
/// the `IFactStore` decorator that drives them. Composed by
/// `FactsCompose.withCoverageNarratives`; consumers normally never call
/// this module directly.
module CoverageNarrative =

    /// `NarrativeProvenance.ModuleId` every coverage narrative is stamped
    /// with — the fact store's reserved `_facts` source, suffixed so a
    /// coverage document is distinguishable from any other narrative a
    /// deployment might one day commit under the fact tier's name.
    [<Literal>]
    let ModuleId = FactEvents.SourceModule + ".coverage"

    /// The stable, per-metric provenance key. This ONE string is what
    /// makes the whole "one document per metric family" invariant hold:
    /// the knowledge base dedups narratives by `(ModuleId, SettingsKey)`
    /// and an overwriting commit replaces in place, so every regeneration
    /// of a metric's coverage lands on the document the previous one
    /// wrote. Derive it from the metric id and nothing else — folding in
    /// a timestamp, a scope, or a coverage figure would turn replacement
    /// into accumulation, which is the failure this phase exists to
    /// prevent.
    let settingsKey (metricId: string) : string = sprintf "metric-coverage:%s" metricId

    // ── The coverage record (707.A input) ─────────────────────────
    //
    // Phase 705 computes exactly this and renders it straight to JSON.
    // Naming it as a value is what lets the generator be a pure function
    // of (declaration, coverage) — testable without a store, and unable
    // to reach for a population value it was not given.

    /// What the disclosure probe established about the caller's
    /// permission to be told anything about one population. The three
    /// outcomes are Phase 705.B's, unchanged.
    type CoveragePosture =
        /// At least one probed head is disclosable; the coverage detail
        /// may be described. Carries the policy refs of anything the
        /// probe was refused, so a partly-restricted population says so.
        | Describable of withheldPolicies: string list
        /// Every probed head was withheld — existence and policy only.
        | WhollyRestricted of policies: string list
        /// Nothing could be probed: no member carries a rankable value,
        /// so the ranking the probe reads is empty. Reported as itself
        /// rather than resolved to either of the other two.
        | Unprobed

    /// One fact the narrative cites, reduced to the three things a
    /// citation may carry: the opaque id, the subject it is about, and
    /// the period it covers. **No value and no rank.**
    type CitedFact = {
        FactId: string
        Subject: string
        Period: string
    }

    /// One populated (metric, hierarchy) pair's coverage.
    type HierarchyCoverage = {
        Hierarchy: Grounding.SubjectDefinition
        Posture: CoveragePosture
        /// `None` whenever the posture withholds the detail — the same
        /// one-shape rule Phase 705's JSON row follows, so "withheld" and
        /// "empty" stay distinguishable.
        Stats: PopulationStats option
        /// A bounded sample of this population's facts, every one of them
        /// disclosable to the acting principal. Empty under a withholding
        /// posture.
        Cited: CitedFact list
    }

    /// A metric's coverage across every hierarchy that holds facts under
    /// it. A hierarchy holding none is absent, never a zero row.
    type MetricCoverage = {
        Definition: Grounding.MetricDefinition
        Populations: HierarchyCoverage list
    }

    // ── Cardinality bands (707.B) ─────────────────────────────────

    /// The order-of-magnitude band a count falls in. Exact integer
    /// comparisons rather than `log10`, because `log10 1000.0` is
    /// `2.9999999999999996` on some runtimes and a band boundary that
    /// depends on floating-point rounding is a supersession that fires
    /// on a machine and not on its replica.
    let cardinalityBand (n: int) : int =
        if n <= 0 then 0
        elif n < 10 then 1
        elif n < 100 then 2
        elif n < 1_000 then 3
        elif n < 10_000 then 4
        elif n < 100_000 then 5
        elif n < 1_000_000 then 6
        else 7

    /// The readable form of a band — what the document actually says.
    /// The document is written in bands because it is only REWRITTEN in
    /// bands: prose claiming "37,412 subjects" would be false one
    /// assertion later and would not be regenerated to fix it, whereas
    /// "between 10,000 and 99,999" stays true for exactly as long as the
    /// supersession rule says it does.
    let bandLabel (band: int) : string =
        match band with
        | 0 -> "none"
        | 1 -> "fewer than 10"
        | 2 -> "between 10 and 99"
        | 3 -> "between 100 and 999"
        | 4 -> "between 1,000 and 9,999"
        | 5 -> "between 10,000 and 99,999"
        | 6 -> "between 100,000 and 999,999"
        | _ -> "1,000,000 or more"

    // ── The material-change band (707.B) ──────────────────────────

    /// One hierarchy's coverage reduced to the axes a supersession is
    /// allowed to turn on.
    ///
    /// What is IN: the cardinality bands, the period reach, the method
    /// mix as a set, the disclosure posture and the policies named by it.
    /// Each of those is a sentence the document makes.
    ///
    /// What is deliberately OUT: **freshness**. A fresh/stale split moves
    /// with the wall clock and with nothing anyone did, so a band
    /// carrying it would supersede the document on a timer. It follows
    /// that the document must not report freshness either — a standing
    /// document may only claim what its supersession rule keeps true, and
    /// the live answer is one `list_metric_coverage` call away.
    type HierarchyBand = {
        Hierarchy: string
        SubjectBand: int
        FactBand: int
        ComparableBand: int
        NonComparableBand: int
        PeriodFrom: DateTime option
        PeriodTo: DateTime option
        Methods: string list
        Posture: string
        Policies: string list
    }

    /// A metric's whole coverage reduced to what a supersession turns on.
    ///
    /// `Declaration` folds in the registry fields the document RENDERS —
    /// name, unit, dimensionality, direction, format, staleness, the
    /// interpretive context. Strictly those are declarations rather than
    /// coverage, and including them widens "material coverage change"
    /// past its literal reading. It is included anyway: a deployment that
    /// rewrites a metric's analyst context and finds the knowledge base
    /// still quoting the old one has a document that is confidently
    /// wrong, which is worse than a document regenerated once on a
    /// redeploy that changed nothing else.
    type CoverageBand = {
        Metric: string
        Declaration: string
        Populations: HierarchyBand list
    }

    let private postureLabel (posture: CoveragePosture) =
        match posture with
        | Describable _ -> "described"
        | WhollyRestricted _ -> "restricted"
        | Unprobed -> "unprobed"

    let private posturePolicies (posture: CoveragePosture) =
        match posture with
        | Describable withheld -> withheld
        | WhollyRestricted policies -> policies
        | Unprobed -> []

    let private directionLabel (d: Grounding.DirectionOfBetter) =
        match d with
        | Grounding.HigherIsBetter -> "higher is better"
        | Grounding.LowerIsBetter -> "lower is better"
        | Grounding.Neutral -> "neither direction is better"

    let private stalenessLabel (s: Grounding.StalenessPolicy) =
        match s with
        | Grounding.FreshFor window -> sprintf "fresh for %s" (window.ToString())
        | Grounding.UntilSuperseded -> "fresh until superseded"
        | Grounding.UntilUpstreamChange -> "fresh until its inputs change"

    let private declarationFingerprint (d: Grounding.MetricDefinition) : string =
        String.Join(
            "",
            [
                d.Id
                d.Name
                d.Unit
                d.Dimensionality
                directionLabel d.Direction
                d.DisplayFormat
                stalenessLabel d.Staleness
                defaultArg d.ProducingOperation ""
                defaultArg d.Context ""
            ]
        )

    /// Reduce a coverage record to its band. Structural equality on the
    /// result IS the material-change test (see `isMaterialChange`), so
    /// every axis a supersession may turn on is a field here and nothing
    /// else is.
    let bandOf (coverage: MetricCoverage) : CoverageBand = {
        Metric = coverage.Definition.Id
        Declaration = declarationFingerprint coverage.Definition
        Populations =
            coverage.Populations
            |> List.map (fun p ->
                let count (select: PopulationStats -> int) =
                    p.Stats |> Option.map select |> Option.defaultValue 0 |> cardinalityBand

                {
                    Hierarchy = p.Hierarchy.Id
                    SubjectBand = count _.SubjectCount
                    FactBand = count _.FactCount
                    ComparableBand = count _.ComparableCount
                    NonComparableBand = count _.NonComparableCount
                    PeriodFrom = p.Stats |> Option.bind _.PeriodFrom
                    PeriodTo = p.Stats |> Option.bind _.PeriodTo
                    Methods =
                        p.Stats
                        |> Option.map _.MethodMix
                        |> Option.defaultValue []
                        // The mix as a SET: a method's fact COUNT moving
                        // is already covered by the cardinality bands, and
                        // carrying the counts here would make every
                        // assertion under a small population material.
                        |> List.map fst
                        |> List.distinct
                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                    Posture = postureLabel p.Posture
                    Policies =
                        posturePolicies p.Posture
                        |> List.distinct
                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                })
            // The registry's declaration order is stable across calls, but
            // sorting makes the band independent of it, so a registry
            // re-ordered by a refactor does not supersede every document.
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.Hierarchy, b.Hierarchy))
    }

    /// Whether the coverage moved materially enough to rewrite the
    /// document. `None` for the previous band means "nothing committed
    /// yet", which is material by definition — including after a process
    /// restart, where the first assertion recommits the document it
    /// already wrote. That recommit is an overwrite of the same
    /// provenance key, so it costs one write and changes nothing a reader
    /// sees; the alternative — persisting the band — would be a second
    /// store of derived state to keep coherent with the first.
    let isMaterialChange (previous: CoverageBand option) (current: CoverageBand) : bool =
        match previous with
        | None -> true
        | Some prior -> prior <> current

    // ── The generator (707.A) ─────────────────────────────────────

    let private metricSpan (label: string) (value: string) = Metric(label, value, None)

    let private declarationGrid (d: Grounding.MetricDefinition) : NarrativeElement =
        KeyValueGrid [
            "Metric id", [ Code d.Id ]
            "Unit", [ Text d.Unit ]
            "Dimension", [ Text d.Dimensionality ]
            "Direction of better", [ Text(directionLabel d.Direction) ]
            "Best-first ranking",
            [
                Text(
                    if d.Direction = Grounding.Neutral then
                        "not available — this metric declares no better direction, so a population read must name its own order"
                    else
                        "available"
                )
            ]
            "Freshness policy", [ Text(stalenessLabel d.Staleness) ]
            match d.ProducingOperation with
            | Some op -> "Produced by", [ Code op ]
            | None -> ()
        ]

    let private contextElements (d: Grounding.MetricDefinition) : NarrativeElement list =
        match d.Context with
        | Some context when not (String.IsNullOrWhiteSpace context) -> [
            // Verbatim, and quoted as the analyst's own words. The
            // registry's context is prose, never data (Phase 705.A):
            // rewriting it here would put a second, drifting
            // interpretation of the metric into the corpus beside the
            // declared one.
            Blockquote(Some(sprintf "%s — as declared by the deployment" d.Name), [ Text context ])
          ]
        | _ -> [
            Paragraph [
                Text(
                    sprintf
                        "This deployment declares %s but records no interpretive context for it, so nothing here explains how to read its sign or magnitude. Treat the figures as unannotated."
                        d.Name
                )
            ]
          ]

    let private citedFactElements (cited: CitedFact list) : NarrativeElement list =
        match cited with
        | [] -> []
        | _ -> [
            Paragraph [
                Text
                    "The facts below are a bounded sample of this population, listed so that retrieving this summary also reaches them. Each is named by its subject and the period it covers; none of their values appears here."
            ]
            BulletList(
                cited
                |> List.map (fun fact -> [
                    // `Metric(label, value, Some factRef)` — the label and
                    // the "value" are both COVERAGE (which subject, which
                    // period). The fact's own value is never rendered; the
                    // `factRef` is what carries the citation, and it is
                    // what Phase 521.D stamps into the chunk's metadata so
                    // the Phase 522 fact-to-narrative join can prefer this
                    // chunk when one of these facts is resolved.
                    Metric(fact.Subject, fact.Period, Some fact.FactId)
                ])
            )
          ]

    let private coverageElements
        (d: Grounding.MetricDefinition)
        (population: HierarchyCoverage)
        : NarrativeElement list =
        match population.Posture, population.Stats with
        | WhollyRestricted policies, _ -> [
            Paragraph [
                Text(
                    sprintf
                        "%s is tracked in this hierarchy, and you are not permitted to see any of it. The facts exist; their number, their period coverage and the methods that produced them are all withheld."
                        d.Name
                )
            ]
            KeyValueGrid [
                "Facts exist", [ Text "yes" ]
                "Coverage described", [ Text "no — every sampled fact is restricted to you" ]
                "Restricting policies",
                [
                    Text(
                        if List.isEmpty policies then
                            "none named"
                        else
                            String.concat ", " policies
                    )
                ]
            ]
          ]
        | Unprobed, _ -> [
            Paragraph [
                Text(
                    sprintf
                        "%s is tracked in this hierarchy, but no fact in it carries a rankable value, so no member could be checked against the disclosure policy. The coverage is withheld rather than assumed safe to describe."
                        d.Name
                )
            ]
          ]
        | Describable _, None -> [
            Paragraph [
                Text(sprintf "%s is tracked in this hierarchy. No coverage detail was computed for it." d.Name)
            ]
          ]
        | Describable withheldPolicies, Some stats ->
            let period =
                match stats.PeriodFrom, stats.PeriodTo with
                | Some from, Some until ->
                    sprintf
                        "%s to %s"
                        (from.ToUniversalTime().ToString "yyyy-MM-dd")
                        (until.ToUniversalTime().ToString "yyyy-MM-dd")
                | _ -> "no period recorded"

            let methods =
                stats.MethodMix
                |> List.map fst
                |> List.distinct
                |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

            [
                Paragraph [
                    metricSpan "Subjects tracked" (bandLabel (cardinalityBand stats.SubjectCount))
                    Text " · "
                    metricSpan "Facts held" (bandLabel (cardinalityBand stats.FactCount))
                    Text " · "
                    metricSpan "Period covered" period
                ]
                KeyValueGrid [
                    "Distinct subjects", [ metricSpan "subjects" (bandLabel (cardinalityBand stats.SubjectCount)) ]
                    "Current facts", [ metricSpan "facts" (bandLabel (cardinalityBand stats.FactCount)) ]
                    "Rankable facts", [ metricSpan "comparable" (bandLabel (cardinalityBand stats.ComparableCount)) ]
                    "Facts asserting no single magnitude",
                    [
                        metricSpan "non-comparable" (bandLabel (cardinalityBand stats.NonComparableCount))
                    ]
                    "Valid-time coverage", [ metricSpan "period" period ]
                    "Methods that produced them",
                    [
                        Text(
                            if List.isEmpty methods then
                                "none recorded"
                            else
                                String.concat ", " methods
                        )
                    ]
                ]
                if not (List.isEmpty withheldPolicies) then
                    Callout(
                        Severity.Warning,
                        [
                            Text(
                                sprintf
                                    "Part of this population is restricted to you under %s. The counts above describe the whole matched population; the restricted members are counted, never shown."
                                    (String.concat ", " withheldPolicies)
                            )
                        ]
                    )
            ]

    let private howToQuery (d: Grounding.MetricDefinition) (hierarchy: Grounding.SubjectDefinition) : NarrativeElement =
        Paragraph [
            Text "These are counts, not values. To read the values, call "
            Code "query_metric_population"
            Text " with metric "
            Code d.Id
            Text " and hierarchy "
            Code hierarchy.Id
            Text
                " — it ranks the population and returns each member through the disclosure gate. For one subject in isolation call "
            Code "query_facts"
            Text ". To see every metric this deployment holds, call "
            Code "list_metric_coverage"
            Text ". Do not answer a question about "
            Strong d.Name
            Text " from this summary alone: it says what is queryable, and the tools say what the numbers are."
        ]

    /// The coverage narrative for one metric — a pure function of the
    /// declaration and the coverage record, so it can neither reach for a
    /// population value nor differ between a test and a deployment.
    ///
    /// **One section per populated hierarchy, and each section is
    /// self-contained.** The knowledge base chunks a narrative by
    /// section, so this is what makes the unit of retrieval one
    /// metric-population: a chunk that only made sense beside its sibling
    /// would be a bad chunk, and a reader retrieving "coverage of
    /// elasticity across SKUs" must get the metric's declaration and its
    /// interpretive context in the same breath as the counts. The
    /// repetition of the declaration across a metric's hierarchies is the
    /// price, and a metric populated in one hierarchy — which is the
    /// ordinary case — pays none of it.
    ///
    /// A metric with no populated hierarchy yields a document with no
    /// sections; `shouldCommit` refuses it rather than committing an
    /// empty document that would claim coverage it does not have.
    let generate (generatedAt: DateTimeOffset) (coverage: MetricCoverage) : NarrativeDocument =
        let d = coverage.Definition

        {
            Title = sprintf "What this deployment tracks: %s" d.Name
            Subtitle =
                Some(sprintf "Coverage of the verified metric '%s' — what is queryable, not what the values are" d.Id)
            Sections =
                coverage.Populations
                |> List.map (fun population -> {
                    Id = sprintf "coverage-%s-%s" d.Id population.Hierarchy.Id
                    Heading = sprintf "%s across %s" d.Name population.Hierarchy.Name
                    Subheading =
                        Some(
                            sprintf
                                "Verified facts, queryable by metric id '%s' in subject hierarchy '%s' (%s)"
                                d.Id
                                population.Hierarchy.Id
                                (if List.isEmpty population.Hierarchy.Levels then
                                     "no declared levels"
                                 else
                                     String.concat " > " population.Hierarchy.Levels)
                        )
                    Elements =
                        [ declarationGrid d ]
                        @ contextElements d
                        @ coverageElements d population
                        @ citedFactElements population.Cited
                        @ [ howToQuery d population.Hierarchy ]
                })
            Provenance =
                Some {
                    ModuleId = ModuleId
                    PageRoute = None
                    GeneratedAt = generatedAt
                    SettingsKey = settingsKey d.Id
                    SettingsDisplay = [ "Metric", d.Id; "Generated by", "fact-store coverage" ]
                }
            Lang = None
            CanonicalUrl = None
        }

    /// Whether this coverage is worth committing at all. A metric with no
    /// populated hierarchy is not: the document would have no sections,
    /// therefore no chunks, and a chunkless knowledge document is a row
    /// in a listing that can never be retrieved.
    let shouldCommit (coverage: MetricCoverage) : bool = not (List.isEmpty coverage.Populations)

    // ── Reading the coverage (the impure half) ────────────────────

    /// The population read one coverage record is derived from —
    /// deliberately the SAME query `list_metric_coverage` issues, for the
    /// same reasons stated there: an explicit ordering so a `Neutral`
    /// metric cannot provoke Phase 701's registry refusal from a surface
    /// that is only asking what exists, and `AllCompetingMethods` because
    /// coverage reports what the store HOLDS rather than what a default
    /// query would select.
    let private coverageQuery (metricId: string) (hierarchyId: string) : PopulationQuery = {
        Metric = MetricRef metricId
        Hierarchy = hierarchyId
        Level = None
        PathPrefix = None
        PeriodOverlaps = None
        Threshold = None
        Ordering = Descending
        TopK = CoverageTool.DisclosureProbeK
        AsOf = None
        Methods = AllCompetingMethods
    }

    let private periodLabel (period: TemporalExtent) =
        match period.Label with
        | Some label when not (String.IsNullOrWhiteSpace label) -> label
        | _ ->
            sprintf
                "%s to %s"
                (period.From.ToUniversalTime().ToString "yyyy-MM-dd")
                (period.To.ToUniversalTime().ToString "yyyy-MM-dd")

    /// Read one (metric, hierarchy) pair's coverage, folding the gate's
    /// verdicts through `PopulationDisclosure.fold` — Phase 706's shared
    /// partition, so this door and the two population doors cannot
    /// disclose differently. `None` when the pair holds no facts.
    let readHierarchy
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (scopeId: string)
        (principal: string)
        (metricId: string)
        (hierarchy: Grounding.SubjectDefinition)
        : Async<HierarchyCoverage option> =
        async {
            let! outcome = store.QueryPopulation(scopeId, coverageQuery metricId hierarchy.Id)

            match outcome with
            // A store fault is not a coverage answer. Reported as "this
            // pair holds nothing" rather than as an error interleaved into
            // a document: a narrative is not the place to surface a store
            // fault, and the ordering is explicit so Phase 701's two typed
            // refusals are both unreachable here.
            | Error _ -> return None
            | Ok result when result.Stats.FactCount = 0 -> return None
            | Ok result ->
                match result.Ranked with
                | [] ->
                    return
                        Some {
                            Hierarchy = hierarchy
                            Posture = Unprobed
                            Stats = None
                            Cited = []
                        }
                | ranked ->
                    let ids = ranked |> List.map _.FactId

                    let! verdicts = gate.Check(scopeId, principal, FactToolResult, ids)

                    // An id the gate returned no verdict for is denied,
                    // conservatively — the door never fails open. Same
                    // rule as both population doors', for the same reason.
                    let verdictFor (factId: string) =
                        verdicts
                        |> Map.tryFind factId
                        |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                    let disclosure = PopulationDisclosure.fold verdictFor ranked

                    let policies =
                        disclosure.WithheldByPolicy
                        |> List.map fst
                        |> List.distinct
                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

                    match disclosure.Disclosable with
                    | [] ->
                        return
                            Some {
                                Hierarchy = hierarchy
                                Posture = WhollyRestricted policies
                                Stats = None
                                Cited = []
                            }
                    | disclosable ->
                        // The citation sample. Every member of it cleared
                        // the gate, and the document renders neither its
                        // value nor its rank — but the SET is drawn from
                        // the ranked head, so its membership is weak
                        // ordering information. That residual is stated
                        // rather than hidden: it is why the sample is
                        // small, why it is sorted by id here so the
                        // document carries no order of its own, and why
                        // the whole mechanism is opt-in (GP 13). A
                        // deployment that cannot accept it does not
                        // compose this.
                        let cited =
                            disclosable
                            |> List.map (fun (_, fact) -> {
                                FactId = fact.FactId
                                Subject = SubjectRef.toString fact.Subject
                                Period = periodLabel fact.Period
                            })
                            |> List.sortWith (fun a b -> String.CompareOrdinal(a.FactId, b.FactId))

                        return
                            Some {
                                Hierarchy = hierarchy
                                Posture = Describable policies
                                Stats = Some result.Stats
                                Cited = cited
                            }
        }

    /// Read one metric's coverage across every declared hierarchy.
    /// Sequential rather than parallel, deliberately: this runs off the
    /// assertion path and a fan-out would let a background regeneration
    /// become the heaviest concurrent thing the store is asked to do.
    let readCoverage
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (scopeId: string)
        (principal: string)
        (definition: Grounding.MetricDefinition)
        (hierarchies: Grounding.SubjectDefinition list)
        : Async<MetricCoverage> =
        async {
            let! populations =
                hierarchies
                |> List.map (fun hierarchy -> readHierarchy store gate scopeId principal definition.Id hierarchy)
                |> Async.Sequential

            return {
                Definition = definition
                Populations = populations |> Array.choose id |> Array.toList
            }
        }

    // ── The compose-time options (707.C) ──────────────────────────

    /// Where a coverage narrative is committed, and as whom.
    ///
    /// **`CommitScope` exists because the two tiers do not agree about
    /// containers, and neither can be told to.** The fact store passes
    /// its `scopeId` straight to `IBlobStorage` as the container, while
    /// the knowledge base uses the middleware-resolved
    /// `StorageScope.Container` (`user-{id}` / `team-{id}` / `_platform`
    /// per the `IBlobStorage` contract). A fact-store scope id is
    /// therefore NOT a knowledge-base container, and guessing the mapping
    /// would put a tenant's coverage document in a container that is not
    /// their knowledge base — silently, and only discoverably by its
    /// absence. So the deployment says. `None` for a scope means "commit
    /// nothing for this one", which is also the whole of what an
    /// undeclared scope gets.
    type CoverageNarrativeOptions = {
        CommitScope: string -> StorageScope option
        /// The principal the commit is attributed to, and — more
        /// importantly — the principal the disclosure gate judges. A
        /// coverage narrative is a standing corpus document readable by
        /// everyone in the scope, so this should name a principal whose
        /// permissions are the FLOOR of what the scope may see, never a
        /// privileged service identity: the gate cannot protect a reader
        /// it was never asked about.
        Principal: string
    }

    module CoverageNarrativeOptions =

        /// Commit into exactly the declared scopes, keyed by the
        /// fact-store scope id each one carries. The ordinary opt-in: a
        /// deployment names the tenants whose coverage it wants
        /// summarised, the way `withCoherenceChecks` names the scopes it
        /// sweeps.
        let forScopes (principal: string) (scopes: StorageScope list) : CoverageNarrativeOptions =
            let index = scopes |> List.map (fun scope -> scope.ScopeId, scope) |> Map.ofList

            {
                CommitScope = index.TryFind
                Principal = principal
            }

        /// Commit into the fact scope's own container — correct only for
        /// a deployment whose knowledge base and fact store share one
        /// container per scope. Offered because that is exactly the shape
        /// a single-tenant or test composition takes, and refusing to
        /// express it would push every such deployment into writing the
        /// mapping by hand.
        let sameContainer (principal: string) : CoverageNarrativeOptions = {
            CommitScope =
                fun scopeId ->
                    Some {
                        ScopeId = scopeId
                        Container = scopeId
                        Persist = true
                    }
            Principal = principal
        }

    // ── The trigger (707.B) ───────────────────────────────────────

    /// Optional-substrate lookup, at module level because an explicit
    /// type parameter is not allowed on a `let` inside a class.
    let private tryResolve<'T when 'T: not struct> (services: IServiceProvider) : 'T option =
        match services.GetService(typeof<'T>) with
        | :? 'T as service -> Some service
        | _ -> None

    /// The `IFactStore` decorator that recomputes and, on a material
    /// change, recommits.
    ///
    /// **Both assertion doors funnel into one hook.** `Assert` is a batch
    /// of one everywhere below the interface (Phase 704 made it so
    /// inside the store), and it is a batch of one here too: each door
    /// hands the SAME function the same `FactDraft list`, so a batch
    /// assertion triggers exactly what a scalar assertion triggers. Two
    /// hooks would be two chances to arm one door and miss the other.
    ///
    /// **The recompute never runs on the caller's thread.** An assertion
    /// is durable the moment the inner store returns; a coverage read is
    /// one population scan per declared hierarchy, and paying that on
    /// every `Assert` would make a seeding loop quadratic. Work is posted
    /// to a single agent which COALESCES: ten thousand assertions against
    /// one metric collapse into however few regenerations the agent gets
    /// round to, and each of those commits only if the band moved. The
    /// same posture `maintainSurface` takes inside the store — derived
    /// state cannot fail the assert.
    ///
    /// `WaitIdle` is the seam that makes this testable (and diagnosable)
    /// without exposing the agent: it returns once every regeneration
    /// queued before the call has finished.
    type CoverageNarrativeFactStore
        (
            inner: IFactStore,
            services: IServiceProvider,
            options: CoverageNarrativeOptions,
            logger: ILogger,
            clock: unit -> DateTimeOffset
        ) =

        /// Last committed band per (scopeId, metricId). In memory, and
        /// deliberately: it is a cache over state that is fully derivable
        /// from the store, so losing it costs one redundant overwrite of
        /// an identical document, while persisting it would be a second
        /// store of derived state to keep coherent with the first.
        let committed = ConcurrentDictionary<string * string, CoverageBand>()

        let regenerate (scopeId: string, metricId: string) : Async<unit> = async {
            // The second half of the double gate, checked FIRST: no
            // knowledge base composed ⇒ nowhere to commit ⇒ no store
            // work at all (GP 13). The knob may be armed on a
            // deployment that has no knowledge base; that deployment
            // pays one DI lookup per coalesced regeneration and
            // nothing else.
            match tryResolve<INarrativeIngestor> services with
            | None -> return ()
            | Some ingestor ->
                match options.CommitScope scopeId with
                | None -> return ()
                | Some scope ->
                    // A registry-less deployment has told the platform
                    // nothing about what its facts MEAN (Phase 519),
                    // so there is no metric family to describe and no
                    // context to quote — the document would be counts
                    // with no subject. Skipped rather than
                    // approximated.
                    match tryResolve<Grounding.IMetricRegistry> services with
                    | None -> return ()
                    | Some registry ->
                        match registry.TryGetMetric metricId with
                        | None -> return ()
                        | Some definition ->
                            match tryResolve<IFactDisclosureGate> services with
                            | None -> return ()
                            | Some gate ->
                                let! coverage =
                                    readCoverage inner gate scopeId options.Principal definition registry.Subjects

                                if not (shouldCommit coverage) then
                                    return ()
                                else
                                    let band = bandOf coverage

                                    let previous =
                                        match committed.TryGetValue((scopeId, metricId)) with
                                        | true, prior -> Some prior
                                        | _ -> None

                                    if not (isMaterialChange previous band) then
                                        return ()
                                    else
                                        let document = generate (clock ()) coverage

                                        let! outcome = ingestor.Ingest(scope, options.Principal, document)

                                        match outcome with
                                        | NarrativeIngested _ ->
                                            // Recorded only on success,
                                            // so a refused or failed
                                            // commit is retried by the
                                            // next assertion rather
                                            // than remembered as done.
                                            committed[(scopeId, metricId)] <- band
                                        | NarrativeIngestRefused reason ->
                                            logger.Info(
                                                sprintf
                                                    "[Phase 707] Coverage narrative for metric '%s' in scope %s was refused by the knowledge base: %s"
                                                    metricId
                                                    scopeId
                                                    reason
                                            )
                                        | NarrativeIngestFailed reason ->
                                            logger.Warn(
                                                sprintf
                                                    "[Phase 707] Coverage narrative for metric '%s' in scope %s could not be committed: %s"
                                                    metricId
                                                    scopeId
                                                    reason
                                            )
        }

        let agent =
            MailboxProcessor<Choice<string * string, AsyncReplyChannel<unit>>>.Start(fun inbox ->
                let rec loop () = async {
                    let! message = inbox.Receive()

                    // Drain whatever else is queued RIGHT NOW into one
                    // working set. This is the coalescing: a seeding
                    // loop posts one message per assertion, and the
                    // agent regenerates once per distinct
                    // (scope, metric) it finds waiting rather than once
                    // per assertion.
                    let work = System.Collections.Generic.HashSet<string * string>()
                    let mutable waiters: AsyncReplyChannel<unit> list = []

                    let take (m: Choice<string * string, AsyncReplyChannel<unit>>) =
                        match m with
                        | Choice1Of2 key -> work.Add key |> ignore
                        | Choice2Of2 reply -> waiters <- reply :: waiters

                    take message

                    while inbox.CurrentQueueLength > 0 do
                        let! queued = inbox.Receive()
                        take queued

                    for key in work do
                        try
                            do! regenerate key
                        with ex ->
                            // Derived state cannot fail an assertion,
                            // and by here the assertion has already
                            // returned — so a fault is logged and the
                            // agent survives it. An agent that died
                            // here would leave every later assertion
                            // posting into a dead mailbox, silently.
                            logger.Warn(
                                sprintf
                                    "[Phase 707] Coverage narrative regeneration failed for metric '%s' in scope %s: %s"
                                    (snd key)
                                    (fst key)
                                    ex.Message
                            )

                    // Replied AFTER the batch, so `WaitIdle` means
                    // "everything queued before you asked is done".
                    for waiter in List.rev waiters do
                        waiter.Reply()

                    return! loop ()
                }

                loop ())

        /// The one hook both assertion doors call. Takes drafts, not
        /// facts, so the scalar door and the batch door hand it the same
        /// shape and cannot diverge.
        let noteAssertion (scopeId: string) (drafts: FactDraft list) =
            drafts
            |> List.map (fun draft -> draft.Metric.Value)
            |> List.distinct
            |> List.iter (fun metricId -> agent.Post(Choice1Of2(scopeId, metricId)))

        /// Block until every regeneration queued before this call has
        /// completed. For tests and diagnostics; a request path never
        /// calls it.
        member _.WaitIdle() : Async<unit> =
            agent.PostAndAsyncReply(fun reply -> Choice2Of2 reply)

        interface IDisposable with
            member _.Dispose() = (agent :> IDisposable).Dispose()

        interface IFactStore with

            member _.Assert(scopeId: string, draft: FactDraft) = async {
                let! outcome = inner.Assert(scopeId, draft)

                // Only a successful assertion can have moved coverage.
                // An idempotent re-assert IS successful and does note —
                // it costs one coalesced recompute that finds an
                // unchanged band and commits nothing.
                match outcome with
                | Ok _ -> noteAssertion scopeId [ draft ]
                | Error _ -> ()

                return outcome
            }

            member _.AssertBatch(scopeId: string, drafts: FactDraft list) = async {
                let! outcome = inner.AssertBatch(scopeId, drafts)

                match outcome with
                | Ok _ -> noteAssertion scopeId drafts
                | Error _ -> ()

                return outcome
            }

            member _.Get(scopeId: string, factId: string) = inner.Get(scopeId, factId)

            member _.Query(scopeId: string, query: FactQuery) = inner.Query(scopeId, query)

            member _.QueryWithCompetition(scopeId: string, query: FactQuery) =
                inner.QueryWithCompetition(scopeId, query)

            member _.QuerySupersessionChain(scopeId: string, factId: string) =
                inner.QuerySupersessionChain(scopeId, factId)

            member _.QueryPopulation(scopeId: string, query: PopulationQuery) = inner.QueryPopulation(scopeId, query)

    /// Decorate a fact store with the coverage-narrative trigger.
    let decorate
        (inner: IFactStore)
        (services: IServiceProvider)
        (options: CoverageNarrativeOptions)
        (logger: ILogger)
        : IFactStore =
        let decorated =
            new CoverageNarrativeFactStore(inner, services, options, logger, (fun () -> DateTimeOffset.UtcNow))

        decorated :> IFactStore