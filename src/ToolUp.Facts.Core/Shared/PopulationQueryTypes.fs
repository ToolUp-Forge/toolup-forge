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
    /// **How the population was computed** (Phase 705): `(methodIdentity,
    /// count)` over the matched heads, ordered by identity (ordinal) so
    /// two implementations of the same read report the same list rather
    /// than the same set.
    ///
    /// Counts, never members — the `FreshnessHistogram` posture, for the
    /// same reason: naming which subjects a given method produced is a
    /// second listing of the population wearing a summary's clothes. What
    /// the mix answers is the question a discovery surface must answer
    /// before a value means anything — is this one estimator over the
    /// whole population, or three, and how much of the population does
    /// each account for. A single-entry mix says the population is
    /// methodologically uniform; several entries under
    /// `AllCompetingMethods` are the D19 competitors, made countable.
    ///
    /// Method identity is `Fact.methodIdentity` (`computed:op:ver:hash` /
    /// `asserted:principal` / `imported:cert`) — a wire token, not a
    /// value: it names how a number was produced and discloses nothing
    /// about what the number is.
    MethodMix: (string * int) list
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

/// The **decidable projection** of one current head (Phase 702): every
/// field the population pipeline reads, and deliberately nothing else.
///
/// A population read decides five things — is this subject in the set,
/// does its value clear the threshold, which of several competing methods
/// counts, where does it rank, and what does the population look like —
/// and *none* of them touch the fact's evidence, confidence, supersession
/// edge, or the value's shape beyond its magnitude. Naming that projection
/// is what lets an **indexed read model** hold a compact columnar snapshot
/// of the current heads instead of the heads themselves, and still produce
/// a byte-identical answer: both paths run the functions below over
/// `PopulationMember` values, so equivalence is a property of the code
/// rather than a claim a test has to keep re-establishing.
///
/// `Magnitude` is already `PopulationValue.comparable` of the head's value
/// — the projection is lossy on purpose, and lossy exactly where the
/// ranking is blind. A non-comparable shape projects to `None` and is
/// counted, never ordered (GP 9), the same as it is on the fact.
type PopulationMember = {
    /// The head's content-addressed `FactId` — the ranking's deterministic
    /// tiebreak, and the key an indexed implementation re-reads the full
    /// fact by once the top-k is known.
    FactId: string
    /// The head's subject instance, for the subject-set predicate and the
    /// distinct-subject count.
    Subject: SubjectRef
    /// `PopulationValue.comparable` of the head's value: `Some d` for a
    /// rankable magnitude, `None` for a shape that asserts none.
    Magnitude: decimal option
    /// `Period.From` — the valid-time lower bound (the `Label` is cosmetic
    /// and no decidable step reads it, so it is not projected).
    PeriodFrom: DateTime
    /// `Period.To` — the valid-time upper bound.
    PeriodTo: DateTime
    /// Transaction time — the whole of what freshness derivation reads
    /// (`Freshness.deriveAt`).
    AsOf: DateTime
    /// `Fact.methodIdentity` of the head's method — the D19 competing-
    /// method discriminator, and what a canonical-method selector matches
    /// against.
    MethodIdentity: string
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

/// Projection of a `Fact` into the decidable population shape.
module PopulationMember =

    /// Project a fact. Total and pure — the same fact always projects to
    /// the same member, which is what makes a persisted projection
    /// verifiable against the fact it came from.
    let ofFact (f: Fact) : PopulationMember = {
        FactId = f.FactId
        Subject = f.Subject
        Magnitude = PopulationValue.comparable f.Value
        PeriodFrom = f.Period.From
        PeriodTo = f.Period.To
        AsOf = f.AsOf
        MethodIdentity = Fact.methodIdentity f.Method
    }

    /// The **competition key** (plan D19): two current heads compete when
    /// they share (subject, period) within one metric but were produced by
    /// different methods. A member set is always one metric, so the metric
    /// is constant and out of the key; the period `Label` is cosmetic and
    /// excluded, mirroring the lineage key's canonical period.
    let competitionKey (m: PopulationMember) = m.Subject, m.PeriodFrom, m.PeriodTo

/// Inclusive-bound threshold evaluation.
module ValueThreshold =

    /// Whether a magnitude satisfies a threshold. `None` — a value shape
    /// asserting no single magnitude — satisfies no threshold: it cannot
    /// be tested, so it is not in the filtered population (and its absence
    /// is visible in the difference between an unfiltered and a filtered
    /// `FactCount`).
    let satisfiesMagnitude (threshold: ValueThreshold) (magnitude: decimal option) : bool =
        match magnitude with
        | None -> false
        | Some d ->
            match threshold with
            | AtLeast bound -> d >= bound
            | AtMost bound -> d <= bound
            | Between(low, high) -> d >= low && d <= high

    /// Whether a value satisfies a threshold — `satisfiesMagnitude` over
    /// the value's comparable magnitude.
    let satisfies (threshold: ValueThreshold) (value: FactValue) : bool =
        satisfiesMagnitude threshold (PopulationValue.comparable value)

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

/// Canonical-method selection over competing current heads (plan D19).
module PopulationSelection =

    /// Resolve competing heads to a metric's registry-declared canonical
    /// method, where one is declared. Per competing group: no declaration
    /// → every head (the pre-566 behaviour, GP 11); a declaration with at
    /// least one matching head → only the matching head(s); a declaration
    /// no head matches → every head (an empty canonical lineage must
    /// surface the competitors, never hide the metric entirely — GP 9).
    ///
    /// Generic over the element on purpose. The fact-enumerating store and
    /// the indexed read model select over different shapes — `Fact` and
    /// `PopulationMember` — and a second implementation of a three-branch
    /// rule is exactly the kind of thing that agrees for a year and then
    /// quietly does not.
    let canonicalHeads
        (keyOf: 'a -> 'k)
        (methodOf: 'a -> string)
        (selectorOf: 'a -> string option)
        (heads: 'a list)
        : 'a list =
        // Competition needs two heads under DIFFERENT method identities
        // sharing a key, so a population under a single method throughout
        // cannot contain a contested group and the grouping is a no-op on
        // it. Worth checking first because the grouping key contains a
        // subject — a record carrying a string list — and structurally
        // hashing one per member is a large fraction of a population read
        // at scale, paid to discover that nothing competes. One method per
        // metric is the ordinary case; competition is the interesting one.
        match heads with
        | []
        | [ _ ] -> heads
        | first :: rest when rest |> List.forall (fun x -> methodOf x = methodOf first) -> heads
        | _ ->

            heads
            |> List.groupBy keyOf
            |> List.collect (fun (_, group) ->
                match group with
                | []
                | [ _ ] -> group
                | contested ->
                    match selectorOf (List.head contested) with
                    | None -> contested
                    | Some selector ->
                        match
                            contested
                            |> List.filter (fun x -> CanonicalMethod.matches selector (methodOf x))
                        with
                        | [] -> contested
                        | matching -> matching)

/// Deterministic ranking over a matched population.
module PopulationRanking =

    /// Order the comparable items best-first under `direction`, ties
    /// broken by content-addressed id (ordinal). The tiebreak is
    /// load-bearing rather than tidy: a population read must return the
    /// same ranking from an enumerating store and from an indexed
    /// projection over the same heads, and equal values are common at
    /// population scale — so the two paths share this comparator rather
    /// than each writing one.
    let rankBy
        (direction: RankDirection)
        (magnitudeOf: 'a -> decimal option)
        (idOf: 'a -> string)
        (items: 'a list)
        : 'a list =
        let keyed =
            items |> List.choose (fun x -> magnitudeOf x |> Option.map (fun d -> d, x))

        // Spelled out rather than `compare (dx, idOf x) (dy, idOf y)`.
        // Lexicographic order on a pair IS "first key, then tiebreak", so
        // the relation is identical — but the tuple form allocates two
        // tuples per comparison and routes through F#'s generic structural
        // comparer, and at population scale that is most of what a ranking
        // costs. `compare` on `string` is ordinal, hence
        // `String.CompareOrdinal`.
        let ordered =
            match direction with
            | LowestFirst ->
                keyed
                |> List.sortWith (fun (dx, x) (dy, y) ->
                    let byValue = compare dx dy

                    if byValue <> 0 then
                        byValue
                    else
                        String.CompareOrdinal(idOf x, idOf y))
            | HighestFirst ->
                keyed
                |> List.sortWith (fun (dx, x) (dy, y) ->
                    let byValue = compare dy dx

                    if byValue <> 0 then
                        byValue
                    else
                        String.CompareOrdinal(idOf x, idOf y))

        ordered |> List.map snd

    /// Order the comparable facts best-first under `direction`.
    let rank (direction: RankDirection) (facts: Fact list) : Fact list =
        rankBy direction (fun f -> PopulationValue.comparable f.Value) (fun f -> f.FactId) facts

    /// Order the comparable projected members best-first under
    /// `direction` — the same comparator `rank` applies, over the
    /// projection.
    let rankMembers (direction: RankDirection) (members: PopulationMember list) : PopulationMember list =
        rankBy direction _.Magnitude _.FactId members

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
        MethodMix = []
    }

    /// **The** population fold — over projected members already paired
    /// with their derived freshness. Every other entry point in this
    /// module reduces to this one, so an enumerating store and an indexed
    /// read model cannot compute two different summaries of the same
    /// population: there is one summary function and they both call it.
    ///
    /// Freshness arrives paired rather than as a callback because the two
    /// callers derive it from different things — a fact, and a row in a
    /// projection — and pairing keeps the fold blind to which.
    let ofMembersWithFreshness (members: (PopulationMember * FactFreshness) list) : PopulationStats =
        // One traversal rather than ten. Each accumulator below reproduces
        // its list-combinator exactly, including the tie behaviour that is
        // easy to lose: `List.min` / `List.max` keep the FIRST extreme
        // they meet, and `List.sum` folds left to right — which matters for
        // `decimal`, where 1.0 and 1.00 are equal but not identical, and
        // where a caller comparing two implementations' summaries would
        // see the difference.
        match members with
        | [] -> empty
        | (firstMember, _) :: _ ->
            let mutable factCount = 0
            let mutable comparableCount = 0
            let mutable freshCount = 0
            let mutable periodFrom = firstMember.PeriodFrom
            let mutable periodTo = firstMember.PeriodTo
            let mutable minimum = 0m
            let mutable maximum = 0m
            let mutable total = 0m

            for m, freshness in members do
                factCount <- factCount + 1

                if m.PeriodFrom < periodFrom then
                    periodFrom <- m.PeriodFrom

                if m.PeriodTo > periodTo then
                    periodTo <- m.PeriodTo

                match freshness with
                | Fresh -> freshCount <- freshCount + 1
                | Stale _ -> ()

                match m.Magnitude with
                | None -> ()
                | Some d ->
                    if comparableCount = 0 then
                        minimum <- d
                        maximum <- d
                        total <- d
                    else
                        if d < minimum then
                            minimum <- d

                        if d > maximum then
                            maximum <- d

                        total <- total + d

                    comparableCount <- comparableCount + 1

            {
                // The two derivations left as their own passes:
                // `List.distinct` / `List.countBy` are already hash-backed
                // and linear, and hand-rolling a set or a dictionary here
                // would mean reaching for a BCL collection this Fable-safe
                // module deliberately does without.
                SubjectCount = members |> List.map (fun (m, _) -> m.Subject) |> List.distinct |> List.length
                FactCount = factCount
                ComparableCount = comparableCount
                NonComparableCount = factCount - comparableCount
                PeriodFrom = Some periodFrom
                PeriodTo = Some periodTo
                Minimum = if comparableCount = 0 then None else Some minimum
                Maximum = if comparableCount = 0 then None else Some maximum
                Mean =
                    if comparableCount = 0 then
                        None
                    else
                        Some(total / decimal comparableCount)
                Freshness = {
                    FreshCount = freshCount
                    StaleCount = factCount - freshCount
                }
                MethodMix =
                    members
                    |> List.countBy (fun (m, _) -> m.MethodIdentity)
                    |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            }

    /// Fold projected members into their summary, deriving freshness per
    /// member.
    let ofMembers (freshnessOf: PopulationMember -> FactFreshness) (members: PopulationMember list) : PopulationStats =
        members |> List.map (fun m -> m, freshnessOf m) |> ofMembersWithFreshness

    /// Fold a matched population into its summary. `freshnessOf` is
    /// supplied by the caller rather than derived here because a metric's
    /// `StalenessPolicy` is a *registry* fact and this module sits below
    /// the registry — the same split `Freshness.derive` already makes.
    let ofPopulation (freshnessOf: Fact -> FactFreshness) (facts: Fact list) : PopulationStats =
        facts
        |> List.map (fun f -> PopulationMember.ofFact f, freshnessOf f)
        |> ofMembersWithFreshness