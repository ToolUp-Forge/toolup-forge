// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DisclosureProofOracleTests

open System
open System.Numerics
open Expecto
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts

// ─── Phase 790 — the proved fold as oracle ───────────────────────────
//
// `proofs/DisclosureFold.fst` models `DisclosureEgress.evaluate` and the
// population fold — `PopulationDisclosure.fold` / `valuesWithheld` /
// `disclosedStats` — clause for clause, and proves five lemmas over
// them: no undisclosed output, withheld-is-a-count-only, ranks
// preserved, magnitudes absent iff withheld, and the relational
// `verdict_noninterference`. `proofs/check.ps1` extracts that model to
// F# and byte-compares the result against the committed
// `proofs/oracle/DisclosureFold.fs`, which this pack references and
// runs.
//
// **A proof is about the MODEL, and this pack is the only thing that
// says the model is about the code.** So every arm below is
// differential, in the shape Phase 787's pack established: a ranking
// and a verdict function go through production and through the
// extracted model, and the two must agree on the DISCLOSED LIST, the
// WITHHELD PROJECTION and the GATED SUMMARY at once. The rankings are
// the ones the two doors' packs pin — the answer planner's, the
// `query_metric_population` tool's, the coverage narrative's —
// reproduced here as data, plus generated rankings with generated
// verdicts, because a pinned instance says the fold is right on the
// cases someone thought of.
//
// **The go-red case is committed, and it is the rank gap.** An oracle
// that renumbers the disclosed ranks contiguously — the tidy-looking
// mistake `PopulationQueryTypes.fs` calls "a correctness defect dressed
// as tidiness", reporting the third-best as the second-best — must be
// CAUGHT by the comparison. A differential that has never been shown to
// fail agrees with whatever it is shown.

// ─── The bridge ──────────────────────────────────────────────────────
//
// Case for case, and short on purpose: a defect here would make the
// comparison compare the wrong thing, and the way to keep that
// checkable is to keep it readable.

let private ix (n: int) : BigInteger = BigInteger n
let private ofNat (n: BigInteger) : int = int n

let private toModelDisclosure (d: Disclosure) : DisclosureFold.disclosure =
    match d with
    | Surfaceable -> DisclosureFold.Surfaceable
    | Internal -> DisclosureFold.Internal
    | Restricted policyRef -> DisclosureFold.Restricted policyRef

let private toModelVerdict (v: FactDisclosureVerdict) : DisclosureFold.verdict =
    match v with
    | FactDisclosable -> DisclosureFold.Disclosable
    | FactNotDisclosable policyRef -> DisclosureFold.NotDisclosable policyRef

let private ofModelVerdict (v: DisclosureFold.verdict) : FactDisclosureVerdict =
    match v with
    | DisclosureFold.Disclosable -> FactDisclosable
    | DisclosureFold.NotDisclosable policyRef -> FactNotDisclosable policyRef

let private toModelOpt (o: 'a option) : DisclosureFold.opt<'a> =
    match o with
    | Some x -> DisclosureFold.OSome x
    | None -> DisclosureFold.ONone

let private ofModelOpt (o: DisclosureFold.opt<'a>) : 'a option =
    match o with
    | DisclosureFold.OSome x -> Some x
    | DisclosureFold.ONone -> None

/// The resolver seam, passed through: the model's surface type is
/// opaque, so the production DU rides it untouched.
let private toModelResolver (resolve: DisclosurePolicyResolver) : DisclosureFold.resolver<FactEgressSurface> =
    fun policyRef surface -> toModelOpt (resolve policyRef surface)

/// A `Fact` is its id plus itself as the opaque payload: the model reads
/// `FactId` and carries the rest through, which is exactly what the fold
/// does.
let private toModelFact (fact: Fact) : DisclosureFold.fact<Fact> = {
    fact_id = fact.FactId
    payload = fact
}

/// The host's string ordering, supplied to the model as data. `List.sortBy
/// fst` compares strings ordinally, and the model's header explains why
/// it does not recompute that.
let private ordinalBefore (a: string) (b: string) : bool = String.CompareOrdinal(a, b) < 0

/// The model's fold at the host's types, read back into production's
/// record so the two can be compared structurally.
let private modelFold (verdictFor: string -> FactDisclosureVerdict) (ranked: Fact list) : PopulationDisclosure =
    let folded =
        DisclosureFold.fold
            ordinalBefore
            (fun factId -> toModelVerdict (verdictFor factId))
            (ranked |> List.map toModelFact)

    {
        Disclosable =
            folded.disclosable
            |> List.map (fun (DisclosureFold.Pair(rank, fact)) -> ofNat rank, fact.payload)
        WithheldCount = ofNat folded.withheld_count
        WithheldByPolicy =
            folded.withheld_by_policy
            |> List.map (fun (DisclosureFold.Pair(policyRef, count)) -> policyRef, ofNat count)
    }

/// Back to the model's shape, for the two functions that take the
/// disclosure as input.
let private toModelDisclosureRecord (d: PopulationDisclosure) : DisclosureFold.population_disclosure<Fact> = {
    disclosable =
        d.Disclosable
        |> List.map (fun (rank, fact) -> DisclosureFold.Pair(ix rank, toModelFact fact))
    withheld_count = ix d.WithheldCount
    withheld_by_policy =
        d.WithheldByPolicy
        |> List.map (fun (policyRef, count) -> DisclosureFold.Pair(policyRef, ix count))
}

/// The magnitude block bridged field for field; every existence-level
/// field rides as the opaque `existence` — the whole production record,
/// which is what lets the read-back below be a structural comparison.
let private toModelStats (stats: PopulationStats) : DisclosureFold.stats<decimal, PopulationStats> = {
    minimum = toModelOpt stats.Minimum
    maximum = toModelOpt stats.Maximum
    mean = toModelOpt stats.Mean
    existence = stats
}

let private ofModelStats (stats: DisclosureFold.stats<decimal, PopulationStats>) : PopulationStats = {
    stats.existence with
        Minimum = ofModelOpt stats.minimum
        Maximum = ofModelOpt stats.maximum
        Mean = ofModelOpt stats.mean
}

let private modelValuesWithheld (d: PopulationDisclosure) : bool =
    DisclosureFold.values_withheld (toModelDisclosureRecord d)

let private modelDisclosedStats (d: PopulationDisclosure) (stats: PopulationStats) : PopulationStats =
    DisclosureFold.disclosed_stats (toModelDisclosureRecord d) (toModelStats stats)
    |> ofModelStats

/// **The go-red oracle.** The model's fold, then the disclosed ranks
/// renumbered 1..k. Every other field is right; only the gap a withheld
/// member leaves has been closed, so the member below it is promoted.
let private contiguousFold (verdictFor: string -> FactDisclosureVerdict) (ranked: Fact list) : PopulationDisclosure =
    let honest = modelFold verdictFor ranked

    {
        honest with
            Disclosable = honest.Disclosable |> List.mapi (fun i (_, fact) -> i + 1, fact)
    }

// ─── The rankings ────────────────────────────────────────────────────

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private factOf (factId: string) (subject: string) (value: decimal) (disclosure: Disclosure) : Fact = {
    FactId = factId
    Subject = {
        Hierarchy = "brand"
        Path = [ subject ]
    }
    Metric = MetricRef "revenue"
    Value = Scalar value
    Period = q2
    AsOf = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ subject ]
        TriggerRef = None
    }
    Confidence = None
    Supersedes = None
    Disclosure = disclosure
}

/// One ranking, its verdict function, and the summary the store would
/// hand the gate. `Name` is what a disagreement reports.
type private Ranking = {
    Name: string
    Ranked: Fact list
    VerdictFor: string -> FactDisclosureVerdict
    Stats: PopulationStats
}

/// The verdict function the way the doors build it: the gate's verdict
/// for every fact it can see, evaluated through the one predicate, and
/// the conservative deny for an id it returned nothing for. `visible`
/// narrows which facts the gate saw at all.
let private gateOver
    (resolve: DisclosurePolicyResolver)
    (surface: FactEgressSurface)
    (visible: Fact -> bool)
    (ranked: Fact list)
    : string -> FactDisclosureVerdict =
    let verdicts =
        ranked
        |> List.filter visible
        |> List.map (fun fact -> fact.FactId, DisclosureEgress.evaluateFact resolve surface fact)
        |> Map.ofList

    fun factId ->
        verdicts
        |> Map.tryFind factId
        |> Option.defaultValue (FactNotDisclosable "unknown-fact")

let private everyone (_: Fact) = true

let private storeStats (ranked: Fact list) : PopulationStats =
    PopulationStats.ofPopulation (fun _ -> Fresh) ranked

let private rankingOf (name: string) (verdictFor: Fact list -> string -> FactDisclosureVerdict) (ranked: Fact list) = {
    Name = name
    Ranked = ranked
    VerdictFor = verdictFor ranked
    Stats = storeStats ranked
}

/// The shipped resolver source over a registered vocabulary — the Phase
/// 562 policy registry — so the `Restricted` arm is exercised with a
/// resolver that answers `Some true`, `Some false` AND `None`.
let private registered (policies: (string * FactEgressSurface list) list) : DisclosurePolicyResolver =
    DisclosureTaintConfig.ofLists
        (policies
         |> List.map (fun (policyRef, permit) -> {
             PolicyRef = policyRef
             Mode = Plain
             PermitSurfaces = permit
             ContributorScope = None
         }))
        []
    |> DisclosureTaintConfig.resolver

let private licenceXAtToolResult = registered [ "licence-x", [ FactToolResult ] ]

/// The rankings the doors' own packs pin, as data. Highest-first, as the
/// store ranks them. Each names the pack it reproduces.
let private suiteRankings: Ranking list = [
    // AnswerPlannerTests — a restricted member is a withheld COUNT, leaves
    // its rank as a gap, and suppresses the magnitude block.
    rankingOf
        "planner: Internal member in the middle"
        (gateOver DisclosurePolicyResolver.denyUnknown FactRetrieval everyone)
        [
            factOf "planner-acme" "acme" 300m Surfaceable
            factOf "planner-beta" "beta" 200m Internal
            factOf "planner-gamma" "gamma" 100m Surfaceable
        ]
    // AnswerPlannerTests — a wholly restricted population discloses
    // counts and policies, never a fact id, never a value.
    rankingOf
        "planner: wholly restricted under one policy"
        (gateOver DisclosurePolicyResolver.denyUnknown FactRetrieval everyone)
        [
            factOf "planner-acme-x" "acme" 300m (Restricted "licence-x")
            factOf "planner-gamma-x" "gamma" 100m (Restricted "licence-x")
        ]
    // AnswerPlannerTests — the planner and the tool run the SAME fold:
    // an explicit verdict function rather than a gate.
    rankingOf
        "planner: explicit verdict function, rank 2 withheld"
        (fun _ factId ->
            if factId = "same-beta" then
                FactNotDisclosable "licence-x"
            else
                FactDisclosable)
        [
            factOf "same-acme" "acme" 300m Surfaceable
            factOf "same-beta" "beta" 200m Surfaceable
            factOf "same-gamma" "gamma" 100m Surfaceable
        ]
    // PopulationQueryToolTests — restricted members fold into a
    // policy-grouped COUNT: ranks 1 and 3 restricted, 2, 4, 5 disclosable.
    rankingOf
        "tool: ranks 1 and 3 restricted under two policies"
        (gateOver DisclosurePolicyResolver.denyUnknown FactToolResult everyone)
        [
            factOf "tool-top" "sku-top" 9999m Internal
            factOf "tool-a" "sku-a" 5000m Surfaceable
            factOf "tool-mid" "sku-mid" 4500m (Restricted "licence-x")
            factOf "tool-b" "sku-b" 4000m Surfaceable
            factOf "tool-c" "sku-c" 3000m Surfaceable
        ]
    // The same ranking under a registry that PERMITS licence-x at the
    // tool surface: the Restricted member now discloses, the Internal
    // one still does not.
    rankingOf "tool: licence-x permitted at the tool surface" (gateOver licenceXAtToolResult FactToolResult everyone) [
        factOf "tool-top" "sku-top" 9999m Internal
        factOf "tool-a" "sku-a" 5000m Surfaceable
        factOf "tool-mid" "sku-mid" 4500m (Restricted "licence-x")
        factOf "tool-b" "sku-b" 4000m Surfaceable
        factOf "tool-c" "sku-c" 3000m Surfaceable
    ]
    // …and at a surface the registry does NOT permit it: `Some false`.
    rankingOf
        "tool: licence-x registered but forbidden at retrieval"
        (gateOver licenceXAtToolResult FactRetrieval everyone)
        [
            factOf "tool-top" "sku-top" 9999m Internal
            factOf "tool-a" "sku-a" 5000m Surfaceable
            factOf "tool-mid" "sku-mid" 4500m (Restricted "licence-x")
            factOf "tool-b" "sku-b" 4000m Surfaceable
        ]
    // PopulationQueryToolTests — a restricted member atop a competing-
    // method mix, so the magnitudes go and the mix survives.
    rankingOf
        "tool: restricted member over a method mix"
        (gateOver DisclosurePolicyResolver.denyUnknown FactToolResult everyone)
        [
            factOf "tool-shut" "sku-shut" 900m Internal
            factOf "tool-a-mmm" "sku-a" 105m Surfaceable
            factOf "tool-a-rollup" "sku-a" 100m Surfaceable
        ]
    // PopulationQueryToolTests — a single denied member.
    rankingOf "tool: one Internal member alone" (gateOver DisclosurePolicyResolver.denyUnknown FactToolResult everyone) [
        factOf "tool-x" "sku-x" 19999m Internal
    ]
    // CoverageNarrativeTests — every head denied by a gate that saw
    // nothing: the conservative `unknown-fact` deny for every member.
    rankingOf
        "coverage: gate saw nothing, every member unknown-fact"
        (gateOver DisclosurePolicyResolver.denyUnknown FactRetrieval (fun _ -> false))
        [
            factOf "cov-2" "sku-2" 20m Surfaceable
            factOf "cov-1" "sku-1" 10m Surfaceable
        ]
    // CoverageNarrativeTests — one head allowed, the other unseen.
    rankingOf
        "coverage: one head seen, one unknown-fact"
        (gateOver DisclosurePolicyResolver.denyUnknown FactRetrieval (fun f -> f.FactId = "cov-1"))
        [
            factOf "cov-2" "sku-2" 20m Surfaceable
            factOf "cov-1" "sku-1" 10m Surfaceable
        ]
    // Nothing withheld: the magnitudes must ride through untouched.
    rankingOf "nothing withheld" (gateOver DisclosurePolicyResolver.denyUnknown FactRetrieval everyone) [
        factOf "open-a" "a" 3m Surfaceable
        factOf "open-b" "b" 2m Surfaceable
        factOf "open-c" "c" 1m Surfaceable
    ]
    // The empty ranking.
    rankingOf "empty ranking" (gateOver DisclosurePolicyResolver.denyUnknown FactRetrieval everyone) []
]

// ─── Generated rankings ──────────────────────────────────────────────
//
// Seeded, so a disagreement is reproducible by name. Two populations:
// verdicts derived through the predicate the way a door derives them,
// and verdicts sampled directly, so the fold is exercised on verdict
// shapes no resolver in this file happens to produce (a multi-party
// refusal, a `purpose-*` ref).

let private surfaces = [
    FactRetrieval
    FactToolResult
    FactNarrativePublication
    FactExport
    FactWebhook
    FactPeerEgress
]

let private disclosures = [
    Surfaceable
    Internal
    Restricted "licence-x"
    Restricted "licence-y"
    Restricted "unregistered"
]

let private resolvers: (string * DisclosurePolicyResolver) list = [
    "deny-unknown", DisclosurePolicyResolver.denyUnknown
    "licence-x at tool result", licenceXAtToolResult
    "licence-x everywhere, licence-y sealed", registered [ "licence-x", surfaces; "licence-y", [] ]
    "licence-y at export and webhook", registered [ "licence-y", [ FactExport; FactWebhook ] ]
]

let private sampledVerdicts = [
    FactDisclosable
    FactDisclosable
    FactNotDisclosable "Internal"
    FactNotDisclosable "licence-x"
    FactNotDisclosable "licence-y"
    FactNotDisclosable "unknown-fact"
    FactNotDisclosable "multi-party-unsatisfied:party-a,party-b"
    FactNotDisclosable "purpose-marketing"
]

let private pick (random: Random) (xs: 'a list) : 'a = xs[random.Next(List.length xs)]

let private generatedRankings: Ranking list =
    let random = Random 790

    let generatedFacts (tag: string) (n: int) (count: int) : Fact list =
        List.init count (fun i ->
            factOf
                (sprintf "%s-%d-%d" tag n i)
                (sprintf "s%d" i)
                (decimal (random.Next(-1000, 100000)) / 100m)
                (pick random disclosures))
        |> List.sortByDescending (fun f ->
            match f.Value with
            | Scalar v -> v
            | _ -> 0m)

    let viaPredicate =
        List.init 200 (fun n ->
            let ranked = generatedFacts "gen" n (random.Next(0, 13))
            let resolverName, resolve = pick random resolvers
            let surface = pick random surfaces
            let unseenEvery = random.Next(2, 6)

            let visible (fact: Fact) =
                Math.Abs(fact.FactId.GetHashCode()) % unseenEvery <> 0

            {
                Name = sprintf "generated #%d via the predicate (%s, %A)" n resolverName surface
                Ranked = ranked
                VerdictFor = gateOver resolve surface visible ranked
                Stats = storeStats ranked
            })

    let viaSampledVerdicts =
        List.init 200 (fun n ->
            let ranked = generatedFacts "sampled" n (random.Next(0, 13))

            let verdicts =
                ranked
                |> List.map (fun fact -> fact.FactId, pick random sampledVerdicts)
                |> Map.ofList

            let stats = storeStats ranked

            {
                Name = sprintf "generated #%d with sampled verdicts" n
                Ranked = ranked
                VerdictFor =
                    fun factId ->
                        verdicts
                        |> Map.tryFind factId
                        |> Option.defaultValue (FactNotDisclosable "unknown-fact")
                // A summary with magnitudes already absent, some of the
                // time, so "untouched when nothing withheld" is exercised
                // on a summary the gate has nothing to clear.
                Stats =
                    if random.Next 4 = 0 then
                        {
                            stats with
                                Minimum = None
                                Maximum = None
                                Mean = None
                        }
                    else
                        stats
            })

    viaPredicate @ viaSampledVerdicts

// ─── Running the two, and comparing all three things at once ─────────

/// The three outputs a door reads, from either implementation.
type private Outcome = {
    Disclosure: PopulationDisclosure
    ValuesWithheld: bool
    Stats: PopulationStats
}

let private viaProduction (r: Ranking) : Outcome =
    let disclosure = PopulationDisclosure.fold r.VerdictFor r.Ranked

    {
        Disclosure = disclosure
        ValuesWithheld = PopulationDisclosure.valuesWithheld disclosure
        Stats = PopulationDisclosure.disclosedStats disclosure r.Stats
    }

/// The model — with the fold pluggable, so the go-red oracle runs
/// through the identical comparison.
let private viaModelWith
    (fold: (string -> FactDisclosureVerdict) -> Fact list -> PopulationDisclosure)
    (r: Ranking)
    : Outcome =
    let disclosure = fold r.VerdictFor r.Ranked

    {
        Disclosure = disclosure
        ValuesWithheld = modelValuesWithheld disclosure
        Stats = modelDisclosedStats disclosure r.Stats
    }

/// Compact enough to read in a failure message; the comparison itself is
/// structural over the whole outcome, payloads included.
let private render (o: Outcome) : string =
    sprintf
        "disclosed=%A withheld=%d byPolicy=%A gate=%b min=%A max=%A mean=%A"
        (o.Disclosure.Disclosable |> List.map (fun (rank, fact) -> rank, fact.FactId))
        o.Disclosure.WithheldCount
        o.Disclosure.WithheldByPolicy
        o.ValuesWithheld
        o.Stats.Minimum
        o.Stats.Maximum
        o.Stats.Mean

/// Never throws. A throw from either side is a finding, not a crash.
let private disagreements
    (fold: (string -> FactDisclosureVerdict) -> Fact list -> PopulationDisclosure)
    (rankings: Ranking list)
    =
    rankings
    |> List.choose (fun r ->
        let production =
            try
                Choice1Of2(viaProduction r)
            with ex ->
                Choice2Of2(sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message)

        let model =
            try
                Choice1Of2(viaModelWith fold r)
            with ex ->
                Choice2Of2(sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message)

        match production, model with
        | Choice1Of2 p, Choice1Of2 m when p = m -> None
        | _ ->
            let show =
                function
                | Choice1Of2 o -> render o
                | Choice2Of2 text -> text

            Some(sprintf "%s\n    production: %s\n    model:      %s" r.Name (show production) (show model)))

let private allRankings () = suiteRankings @ generatedRankings

// ─── The pack ────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 790 - the proved fold as oracle" [

        testCase "the covered set is not vacuous"
        <| fun () ->
            // Every arm below is a `List.isEmpty` over a computed
            // population, which an empty population satisfies trivially -
            // so the population is asserted first.
            Expect.isGreaterThan (List.length suiteRankings) 10 "the doors' pinned rankings should all be here"
            Expect.isGreaterThan (List.length generatedRankings) 300 "and a generated population beside them"

            Expect.isTrue
                (suiteRankings @ generatedRankings
                 |> List.exists (fun r ->
                     let d = PopulationDisclosure.fold r.VerdictFor r.Ranked
                     d.WithheldCount > 0 && not (List.isEmpty d.Disclosable)))
                "at least one ranking must mix withheld and disclosed members, or the rank gap is never exercised"

        testCase "the extracted egress predicate agrees with production over every classification, resolver and surface"
        <| fun () ->
            // `evaluate` first, on its own: the fold takes verdicts as
            // given, so a predicate that drifted would be invisible to
            // the fold arms below whenever both sides were handed the
            // same wrong verdict.
            let found = [
                for resolverName, resolve in resolvers do
                    for surface in surfaces do
                        for disclosure in disclosures do
                            let production = DisclosureEgress.evaluate resolve surface disclosure

                            let model =
                                DisclosureFold.evaluate (toModelResolver resolve) surface (toModelDisclosure disclosure)
                                |> ofModelVerdict

                            if production <> model then
                                yield
                                    sprintf
                                        "%s / %A / %A: production %A, model %A"
                                        resolverName
                                        surface
                                        disclosure
                                        production
                                        model
            ]

            Expect.isEmpty
                found
                (sprintf "the proved predicate and the shipped one must agree:\n  %s" (String.concat "\n  " found))

        testCase "the extracted fold agrees with production over every suite ranking"
        <| fun () ->
            let found = disagreements modelFold suiteRankings

            Expect.isEmpty
                found
                (sprintf
                    "the proved fold and the shipped one must agree on the disclosed list, the withheld projection and the gated summary:\n  %s"
                    (String.concat "\n  " found))

        testCase "the extracted fold agrees with production over generated rankings with generated verdicts"
        <| fun () ->
            let found = disagreements modelFold generatedRankings

            Expect.isEmpty
                found
                (sprintf
                    "the proved fold and the shipped one must agree over the generated population:\n  %s"
                    (String.concat "\n  " found))

        testCase "the differential CATCHES an oracle that renumbers ranks contiguously - the go-red case"
        <| fun () ->
            let found = disagreements contiguousFold (allRankings ())

            Expect.isNonEmpty
                found
                "an oracle that closes the gap a withheld member leaves must be caught: if this passes, the comparison is \
                 agreeing with whatever it is shown and every other arm in this pack is worthless"

        testCase "and the true-rank gap is what it catches"
        <| fun () ->
            // The mechanism, pinned rather than left to the population:
            // three members, the second withheld. Production and the
            // honest oracle keep ranks 1 and 3; the contiguous oracle
            // reports the third-best as the second-best.
            let ranking =
                suiteRankings
                |> List.find (fun r -> r.Name = "planner: explicit verdict function, rank 2 withheld")

            let ranksOf (d: PopulationDisclosure) = d.Disclosable |> List.map fst

            Expect.equal
                (ranksOf (PopulationDisclosure.fold ranking.VerdictFor ranking.Ranked))
                [ 1; 3 ]
                "production keeps the true ranks, gap included"

            Expect.equal
                (ranksOf (modelFold ranking.VerdictFor ranking.Ranked))
                [ 1; 3 ]
                "the honest oracle agrees - this is `ranks_preserved` running"

            Expect.equal
                (ranksOf (contiguousFold ranking.VerdictFor ranking.Ranked))
                [ 1; 2 ]
                "the contiguous oracle promotes the third-best to second, which is the defect the comparison exists to see"

            Expect.isNonEmpty (disagreements contiguousFold [ ranking ]) "and the comparison reports exactly that"

        testCase "the model never throws, on any ranking, through any of the three functions"
        <| fun () ->
            // The lemmas' operational face: every definition in the model
            // is total, but the extraction and the bridge are trusted
            // rather than proved, and this is the arm that would catch
            // either turning a total function into an exception.
            let threw =
                allRankings ()
                |> List.choose (fun r ->
                    try
                        viaModelWith modelFold r |> ignore
                        None
                    with ex ->
                        Some(sprintf "%s: %s %s" r.Name (ex.GetType().Name) ex.Message))

            Expect.isEmpty threw (sprintf "the extracted model threw:\n  %s" (String.concat "\n  " threw))
    ]