// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform.Grounding

// ─── Population query (Phase 701) ────────────────────────────────────
//
// The **cross-subject** read over the fact base. `FactQuery` is a point
// read: it names a subject (or leaves it open) and hands back an
// unordered, unbounded `Fact list`. That is the right shape for "what is
// revenue for the UK in Q2" and structurally the wrong shape for "which
// of these 300,000 subjects is the most elastic" — a question that needs
// an *ordering*, a *bound*, and a *summary of the population it ranked
// over*.
//
// A `PopulationQuery` therefore carries three things a `FactQuery` does
// not:
//
//   - **A subject SET rather than a subject.** A hierarchy id, plus an
//     optional depth (`Level`) and an optional `PathPrefix`, describe a
//     population without enumerating it.
//   - **An ordering that is DECLARED, never guessed (GP 9).**
//     `RegistryDirection` resolves through the metric registry's
//     `DirectionOfBetter` — "most elastic" on a negatively-signed metric
//     is a registry fact, not a model judgment — and refuses when the
//     registry cannot answer. `Ascending` / `Descending` are the caller's
//     own explicit choice and work registry-free.
//   - **A hard ceiling.** A population read returns a *ranking and a
//     summary*, never the population. `PopulationQuery.MaxTopK` is the
//     ceiling every implementation clamps to, so the size of the answer
//     is bounded by the contract rather than by the caller's manners.
//
// **Bitemporal, current-heads-only.** The read resolves over the current
// heads visible at `AsOf` (law L4): a superseded value never ranks, and
// an `AsOf` dated before a supersession ranks the head that was current
// then. There is deliberately **no `IncludeSuperseded`** — a ranking that
// mixes a value with the value that replaced it is not a population.
//
// **Generic + OSS-safe.** No domain vocabulary; a metric is a registry
// id string and a subject a hierarchy id + path, exactly as in
// `FactTypes`.
//
// Fable-safe: records / DUs over primitives and `FactTypes`, no
// reflection, no crypto, no BCL surface beyond `System.DateTime`.

/// How a population ranking is ordered.
type PopulationOrdering =
    /// Order by the metric registry's declared `DirectionOfBetter`, best
    /// first: `HigherIsBetter` ranks descending, `LowerIsBetter` ranks
    /// ascending. A metric that is unregistered — or registered
    /// `Neutral`, which is a *declaration that there is no better
    /// direction* — is a typed refusal naming the gap (GP 9), never a
    /// guessed sort order.
    | RegistryDirection
    /// Smallest value first. The caller's own explicit choice; needs no
    /// registry.
    | Ascending
    /// Largest value first. The caller's own explicit choice; needs no
    /// registry.
    | Descending

/// The concrete sort direction a ranking applies, once
/// `PopulationOrdering` has been resolved against the registry. Distinct
/// from `PopulationOrdering` on purpose: the *request* may defer to the
/// registry, the *resolution* never can.
type RankDirection =
    /// Smallest comparable value first.
    | LowestFirst
    /// Largest comparable value first.
    | HighestFirst

/// An optional numeric filter over a population's values — "how many
/// subjects exceed T", "the members between A and B". Bounds are
/// **inclusive**, and a value whose shape carries no single magnitude
/// (see `PopulationValue.comparable`) satisfies no threshold: it cannot
/// be tested, so it is not in the filtered population.
type ValueThreshold =
    /// Value ≥ bound.
    | AtLeast of decimal
    /// Value ≤ bound.
    | AtMost of decimal
    /// `low` ≤ value ≤ `high`.
    | Between of low: decimal * high: decimal

/// Which of several *competing* methods (plan D19) a population read
/// ranks over. Competing facts are never merged, so a population that
/// admitted every method would rank one subject more than once.
type PopulationMethodSelection =
    /// The metric's registry-declared canonical method where one exists;
    /// every competing head where none is declared (the D19 default, and
    /// byte-for-byte the `FactQuery.Method = None` semantics).
    | CanonicalMethodOnly
    /// Every competing current head, even where a canonical method is
    /// declared — the "all on request" half of D19: competition is
    /// surfaced, never hidden.
    | AllCompetingMethods
    /// One named method's lineage.
    | OneMethod of MethodRef

/// A typed cross-subject read: rank one metric across a subject
/// population and summarise what was ranked over.
type PopulationQuery = {
    /// The metric every ranked fact carries. A population is one metric
    /// across many subjects — never many metrics.
    Metric: MetricRef
    /// The subject hierarchy id (`SubjectRef.Hierarchy`) the population
    /// is drawn from.
    Hierarchy: string
    /// Optional depth filter — the number of `SubjectRef.Path` segments,
    /// so `0` is the hierarchy root and `2` the second level down. `None`
    /// admits every depth. Ranking a mixed-depth population is legal but
    /// rarely meaningful (a parent and its children are not peers), which
    /// is why the filter exists.
    Level: int option
    /// Optional path-prefix filter — admit only subjects whose `Path`
    /// starts with these segments ("every SKU under this brand"). `None`
    /// admits the whole hierarchy.
    PathPrefix: string list option
    /// Optional valid-time filter — admit only facts whose `Period`
    /// overlaps this extent. `None` admits every period, and the result's
    /// `PopulationStats` then reports the period coverage it actually saw.
    PeriodOverlaps: TemporalExtent option
    /// Optional value filter (see `ValueThreshold`). Applied *before*
    /// ranking and before the statistics, so the stats describe the
    /// population the query matched.
    Threshold: ValueThreshold option
    /// How the ranking is ordered.
    Ordering: PopulationOrdering
    /// How many ranked facts to return. Clamped into
    /// `[1, PopulationQuery.MaxTopK]` by every implementation — a caller
    /// asking for the population gets the ceiling, not the population.
    TopK: int
    /// Law L4 visibility instant. `None` = now (the current head of each
    /// lineage). `Some t` ranks the heads that were current at `t`.
    AsOf: DateTime option
    /// Which competing method's heads to rank (plan D19).
    Methods: PopulationMethodSelection
}

/// Distribution of derived freshness across a ranked population —
/// **counts, never members**. A population's freshness is a property of
/// the population; naming which members are stale at population scale is
/// a second listing, not a summary.
type FreshnessHistogram = {
    /// Members whose derived freshness is `Fresh` at the query instant.
    FreshCount: int
    /// Members whose derived freshness is `Stale` at the query instant.
    StaleCount: int
}

/// What a population *looks like* — the summary a caller reads instead
/// of materialising the population. Every field is derived per query
/// from the matched members; nothing here is stored on any fact.
type PopulationStats = {
    /// Distinct subject instances in the matched population.
    SubjectCount: int
    /// Matched current heads. Exceeds `SubjectCount` when one subject
    /// contributes several periods, or several competing methods under
    /// `AllCompetingMethods`.
    FactCount: int
    /// Matched heads carrying a rankable value shape (see
    /// `PopulationValue.comparable`).
    ComparableCount: int
    /// Matched heads counted but never ranked — a `Categorical`,
    /// `Absent`, `Series`, `Distribution` or `Interval` value asserts no
    /// single magnitude, so it is part of the population and outside the
    /// ranking (GP 9). `Absent` members in particular are the queryable
    /// data gaps the closed loop reads.
    NonComparableCount: int
    /// Earliest `Period.From` across the matched population; `None` when
    /// the population is empty.
    PeriodFrom: DateTime option
    /// Latest `Period.To` across the matched population; `None` when the
    /// population is empty.
    PeriodTo: DateTime option
    /// Smallest comparable value; `None` when nothing was comparable.
    Minimum: decimal option
    /// Largest comparable value; `None` when nothing was comparable.
    Maximum: decimal option
    /// Arithmetic mean of the comparable values; `None` when nothing was
    /// comparable. A mean over a population of mixed *units* would be
    /// meaningless, which is exactly why a population is one metric.
    Mean: decimal option
    /// Derived freshness distribution at the query instant.
    Freshness: FreshnessHistogram
}

/// The answer to a population question: a bounded ranking, plus the
/// summary of everything it ranked over.
type PopulationResult = {
    /// The top-k current heads, best first under `Direction`. Members
    /// with a non-comparable value shape are counted in `Stats` and never
    /// appear here.
    Ranked: Fact list
    /// The resolved sort direction the ranking applied — reported so a
    /// caller (or an answer surface) can say *why* this order, rather
    /// than restating the request.
    Direction: RankDirection
    /// The top-k actually applied, after the `MaxTopK` clamp. A caller
    /// that asked for more sees the ceiling here rather than inferring it.
    EffectiveTopK: int
    /// Whether comparable members were dropped by the ceiling — i.e.
    /// `ComparableCount > EffectiveTopK`.
    Truncated: bool
    /// What the ranked population looks like.
    Stats: PopulationStats
}

/// Which `FactValue` shapes carry a single magnitude and can therefore be
/// ranked, and which are counted but never ordered.
module PopulationValue =

    /// The rankable magnitude of a value, or `None` when the shape
    /// asserts none.
    ///
    /// **Only `Scalar` is comparable, and that is a decision rather than
    /// an omission (GP 9).** `Categorical`, `Absent` and `Series` plainly
    /// carry no magnitude. `Distribution` is a bucket map — collapsing it
    /// to one number picks a statistic the fact never asserted. And
    /// `Interval` asserts *bounds*: ranking it by its low bound, its high
    /// bound, or its midpoint each encodes an optimism the assertion does
    /// not contain. Ordering is declared, never guessed — so an interval
    /// is part of the population, is counted in the statistics, and is
    /// not placed in a ranking that would silently invent its position.
    let comparable (value: FactValue) : decimal option =
        match value with
        | Scalar d -> Some d
        | Interval _
        | Series _
        | Distribution _
        | Categorical _
        | Absent _ -> None

/// Inclusive-bound threshold evaluation.
module ValueThreshold =

    /// Whether a value satisfies a threshold. A value with no comparable
    /// magnitude satisfies no threshold — it cannot be tested, so it is
    /// not in the filtered population (and its absence is visible in the
    /// difference between an unfiltered and a filtered `FactCount`).
    let satisfies (threshold: ValueThreshold) (value: FactValue) : bool =
        match PopulationValue.comparable value with
        | None -> false
        | Some d ->
            match threshold with
            | AtLeast bound -> d >= bound
            | AtMost bound -> d <= bound
            | Between(low, high) -> d >= low && d <= high

/// Resolution of a requested ordering into a concrete rank direction.
module PopulationOrdering =

    /// Resolve `ordering` for `metricId`, given the metric's
    /// registry-declared `DirectionOfBetter` (`None` when the metric is
    /// unregistered, or no registry is composed).
    ///
    /// An explicit `Ascending` / `Descending` always resolves and never
    /// consults the registry. `RegistryDirection` resolves only from a
    /// declaration that names a direction; the two ways it cannot —
    /// unregistered, and declared `Neutral` — are **separate refusals
    /// with separate remedies**, because "I have never heard of this
    /// metric" and "this metric has no better direction" are different
    /// facts and lead the caller different places.
    let resolve
        (metricId: string)
        (ordering: PopulationOrdering)
        (declared: DirectionOfBetter option)
        : Result<RankDirection, string> =
        match ordering with
        | Ascending -> Ok LowestFirst
        | Descending -> Ok HighestFirst
        | RegistryDirection ->
            match declared with
            | Some HigherIsBetter -> Ok HighestFirst
            | Some LowerIsBetter -> Ok LowestFirst
            | Some Neutral ->
                Error(
                    sprintf
                        "population ordering: metric '%s' declares DirectionOfBetter = Neutral, so it has no best-first order. Rank it with an explicit Ascending or Descending ordering, or declare a direction on the metric."
                        metricId
                )
            | None ->
                Error(
                    sprintf
                        "population ordering: metric '%s' is not registered, so its DirectionOfBetter is unknown and RegistryDirection cannot be resolved. Register the metric (Grounding.MetricDefinition), or rank with an explicit Ascending or Descending ordering."
                        metricId
                )

/// Construction, the ceiling, and the subject-set predicate. The
/// predicate lives here rather than inside a store so that every
/// implementation — enumerating or indexed — admits exactly the same
/// population.
module PopulationQuery =

    /// The hard ceiling on a population read's ranking. A population read
    /// answers "which are the best/worst k" and "what does this look
    /// like"; it is never the transport for the population itself, so the
    /// bound is part of the contract rather than a caller's courtesy.
    [<Literal>]
    let MaxTopK = 1000

    /// The default population read for a metric across a hierarchy:
    /// registry-directed ordering, top 10, current heads, canonical
    /// method, no filters.
    let create (metric: MetricRef) (hierarchy: string) : PopulationQuery = {
        Metric = metric
        Hierarchy = hierarchy
        Level = None
        PathPrefix = None
        PeriodOverlaps = None
        Threshold = None
        Ordering = RegistryDirection
        TopK = 10
        AsOf = None
        Methods = CanonicalMethodOnly
    }

    /// The top-k a store actually applies: the request clamped into
    /// `[1, MaxTopK]`. A non-positive request is a caller error that
    /// would otherwise silently return nothing, so it clamps *up* to one.
    let effectiveTopK (query: PopulationQuery) : int =
        if query.TopK < 1 then 1
        elif query.TopK > MaxTopK then MaxTopK
        else query.TopK

    /// Reconstruct the fact base as of a transaction time (law L4).
    let asOf (t: DateTime) (query: PopulationQuery) : PopulationQuery = { query with AsOf = Some t }

    /// Whether a subject instance is in the query's subject set —
    /// hierarchy, then depth, then path prefix.
    let matchesSubject (query: PopulationQuery) (subject: SubjectRef) : bool =
        subject.Hierarchy = query.Hierarchy
        && (query.Level |> Option.forall (fun level -> List.length subject.Path = level))
        && (query.PathPrefix
            |> Option.forall (fun prefix ->
                List.length subject.Path >= List.length prefix
                && List.forall2 (=) prefix (subject.Path |> List.truncate (List.length prefix))))

/// Deterministic ranking over a matched population.
module PopulationRanking =

    /// Order the comparable members best-first under `direction`, ties
    /// broken by `FactId` (ordinal). The tiebreak is load-bearing rather
    /// than tidy: a population read must return the same ranking from an
    /// enumerating store and from an indexed projection over the same
    /// heads, and equal values are common at population scale.
    let rank (direction: RankDirection) (facts: Fact list) : Fact list =
        let keyed =
            facts
            |> List.choose (fun f -> PopulationValue.comparable f.Value |> Option.map (fun d -> d, f))

        let ordered =
            match direction with
            | LowestFirst ->
                keyed
                |> List.sortWith (fun (dx, fx) (dy, fy) -> compare (dx, fx.FactId) (dy, fy.FactId))
            | HighestFirst ->
                keyed
                |> List.sortWith (fun (dx, fx) (dy, fy) ->
                    match compare dy dx with
                    | 0 -> compare fx.FactId fy.FactId
                    | c -> c)

        ordered |> List.map snd

/// Derivation of the population summary.
module PopulationStats =

    /// The summary of an empty population.
    let empty: PopulationStats = {
        SubjectCount = 0
        FactCount = 0
        ComparableCount = 0
        NonComparableCount = 0
        PeriodFrom = None
        PeriodTo = None
        Minimum = None
        Maximum = None
        Mean = None
        Freshness = { FreshCount = 0; StaleCount = 0 }
    }

    /// Fold a matched population into its summary. `freshnessOf` is
    /// supplied by the caller rather than derived here because a metric's
    /// `StalenessPolicy` is a *registry* fact and this module sits below
    /// the registry — the same split `Freshness.derive` already makes.
    let ofPopulation (freshnessOf: Fact -> FactFreshness) (facts: Fact list) : PopulationStats =
        match facts with
        | [] -> empty
        | _ ->
            let comparableValues =
                facts |> List.choose (fun f -> PopulationValue.comparable f.Value)

            let comparableCount = List.length comparableValues

            let freshCount =
                facts
                |> List.sumBy (fun f ->
                    match freshnessOf f with
                    | Fresh -> 1
                    | Stale _ -> 0)

            {
                SubjectCount = facts |> List.map _.Subject |> List.distinct |> List.length
                FactCount = List.length facts
                ComparableCount = comparableCount
                NonComparableCount = List.length facts - comparableCount
                PeriodFrom = facts |> List.map _.Period.From |> List.min |> Some
                PeriodTo = facts |> List.map _.Period.To |> List.max |> Some
                Minimum =
                    if comparableCount = 0 then
                        None
                    else
                        Some(List.min comparableValues)
                Maximum =
                    if comparableCount = 0 then
                        None
                    else
                        Some(List.max comparableValues)
                Mean =
                    if comparableCount = 0 then
                        None
                    else
                        Some(List.sum comparableValues / decimal comparableCount)
                Freshness = {
                    FreshCount = freshCount
                    StaleCount = List.length facts - freshCount
                }
            }