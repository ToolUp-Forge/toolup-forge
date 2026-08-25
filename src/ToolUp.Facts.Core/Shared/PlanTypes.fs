// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Answer-plan types (Phase 560) ───────────────────────────────────
//
// The grounded answer planner compiles a question into (subject, metric,
// period) triples against the registry vocabulary (Phase 519) and
// resolves each triple to a typed `PlanStep`. The plan is a first-class,
// auditable artifact — **"EXPLAIN for answers"**: recorded into the
// answer's provenance chain (Phase 524), so every grounded answer can
// show not just its sources but its *reasoning plan*, the way a database
// shows a query plan.
//
// The LLM's only job in this pipeline is the *compiler* role (question →
// candidate triples); everything after that — vocabulary validation,
// store lookup, freshness, disclosure — is deterministic, typed
// machinery. Unresolvable vocabulary is a typed refusal naming what was
// unrecognised (GP 9 — refusal over fabrication), never a silent
// similarity fallback.
//
// **Value-typed, serialisable, Fable-safe.** Records + DUs over
// primitives and FSharp.Core collections only — the plan can cross a
// wire or render in a client surface unchanged.

/// A raw candidate triple as compiled from the question — registry
/// vocabulary ids as plain strings, *pre*-validation. The compiler (an
/// LLM in the structured-output role, or any deterministic front-end)
/// emits these; the planner validates them against the registry and
/// resolves each to a typed `PlanStep`.
type TripleCandidate = {
    /// Registered subject-hierarchy id the triple is about (Phase 519
    /// `SubjectDefinition.Id`).
    SubjectHierarchy: string
    /// Ordered member path from the hierarchy root down (empty = the
    /// hierarchy-root roll-up).
    SubjectPath: string list
    /// Registered metric id (Phase 519 `MetricDefinition.Id`).
    Metric: string
    /// Optional valid-time window the answer is about — half-open
    /// `[From, To)` in UTC; either bound may be absent.
    PeriodFrom: DateTime option
    PeriodTo: DateTime option
    /// Optional human period tag (`"Q2-2026"`), carried for display
    /// only — never compared across calendars (law L5).
    PeriodLabel: string option
}

// ─── Population triples (Phase 706) ──────────────────────────────────
//
// A `TripleCandidate` names ONE subject, so every superlative or
// aggregate question — "which brand is most elastic", "top 10 by
// revenue", "how many exceed T" — either failed vocabulary resolution or
// degraded to an unanswerable point lookup. A `PopulationTriple` is the
// same compiled-candidate idea over a subject *set*: hierarchy + optional
// depth/subtree filter, one metric, a period, an ordering and a k.
//
// **Pre-validation, exactly like `TripleCandidate`.** The ordering rides
// as the vocabulary token the compiler emitted rather than a resolved
// `PopulationOrdering`, because an unrecognised token must reach the
// planner as a *typed refusal naming the gap* (GP 9) and not vanish into
// a parse failure that condemns the whole question. Resolution is the
// planner's deterministic job, here as everywhere.

/// A raw candidate *population* triple as compiled from the question —
/// registry vocabulary ids as plain strings, *pre*-validation. The
/// planner validates it against the registry and resolves it to a
/// `UseAggregate` step through Phase 701's `IFactStore.QueryPopulation`.
type PopulationTriple = {
    /// Registered subject-hierarchy id the population is drawn from.
    SubjectHierarchy: string
    /// Optional depth filter — the number of subject-path segments, so
    /// `0` is the hierarchy root and `2` the second level down. `None`
    /// admits every depth.
    Level: int option
    /// Optional subtree filter — admit only subjects whose path starts
    /// with these segments. Empty admits the whole hierarchy.
    PathPrefix: string list
    /// Registered metric id the population is ranked on. A population is
    /// ONE metric across many subjects — never many metrics.
    Metric: string
    /// Optional valid-time window — half-open `[From, To)` in UTC.
    PeriodFrom: DateTime option
    PeriodTo: DateTime option
    /// Optional human period tag, carried for display only (law L5).
    PeriodLabel: string option
    /// The ordering vocabulary token **as the compiler emitted it** —
    /// resolved by the planner against `PopulationTriple.tryOrdering`,
    /// never guessed. An unrecognised token is a typed refusal.
    Ordering: string
    /// How many ranked members to plan for. Clamped by the store into
    /// `[1, PopulationQuery.MaxTopK]`.
    TopK: int
    /// Optional inclusive lower bound on the value — the "how many reach
    /// at least T" half of an aggregate question.
    ValueAtLeast: decimal option
    /// Optional inclusive upper bound on the value.
    ValueAtMost: decimal option
}

/// The ordering vocabulary, the default population triple, and the
/// point-shaped shadow candidate.
module PopulationTriple =

    /// The ordering tokens a compiled population triple may carry —
    /// deliberately the same three the `query_metric_population` tool
    /// accepts, so one deployment does not speak two ordering vocabularies
    /// depending on which door the question came through.
    let OrderingVocabulary = [ "best_first"; "ascending"; "descending" ]

    /// Resolve an emitted ordering token; `None` for anything outside the
    /// vocabulary — never a nearest match, and never a silent default to
    /// `best_first` (which would be a guessed sort order wearing a
    /// default's clothes).
    let tryOrdering (token: string) : PopulationOrdering option =
        match (if isNull token then "" else token.Trim().ToLowerInvariant()) with
        | "best_first" -> Some RegistryDirection
        | "ascending" -> Some Ascending
        | "descending" -> Some Descending
        | _ -> None

    /// The default population triple for a metric across a hierarchy —
    /// registry-directed ordering, top 10, no filters. Mirrors
    /// `PopulationQuery.create`.
    let create (hierarchy: string) (metric: string) : PopulationTriple = {
        SubjectHierarchy = hierarchy
        Level = None
        PathPrefix = []
        Metric = metric
        PeriodFrom = None
        PeriodTo = None
        PeriodLabel = None
        Ordering = "best_first"
        TopK = 10
        ValueAtLeast = None
        ValueAtMost = None
    }

    /// The value filter the triple's bounds describe, if any. Mirrors the
    /// `query_metric_population` argument pair so the two doors filter
    /// identically.
    let threshold (triple: PopulationTriple) : ValueThreshold option =
        match triple.ValueAtLeast, triple.ValueAtMost with
        | None, None -> None
        | Some low, Some high -> Some(Between(low, high))
        | Some low, None -> Some(AtLeast low)
        | None, Some high -> Some(AtMost high)

    /// The **point-shaped shadow** of a population triple: the hierarchy,
    /// the subtree root and the period, in the `TripleCandidate` shape.
    ///
    /// A plan's `Steps` are `PlannedTriple`s and stay so — widening
    /// `AnswerPlan` with a second step list would retype its constructor
    /// and break every consumer that builds one, for no gain (GP 11). The
    /// population *form* lives where it belongs: in the resolution, on the
    /// `UseAggregate` step, which carries the whole triple it ran. The
    /// shadow is what the question asked at triple granularity, and it is
    /// a projection rather than a claim — nothing reads it as the query.
    let toCandidate (triple: PopulationTriple) : TripleCandidate = {
        SubjectHierarchy = triple.SubjectHierarchy
        SubjectPath = triple.PathPrefix
        Metric = triple.Metric
        PeriodFrom = triple.PeriodFrom
        PeriodTo = triple.PeriodTo
        PeriodLabel = triple.PeriodLabel
    }

/// What a compiler produced from one question: point triples, population
/// triples, or both. Additive — `CompiledQuestion.ofTriples` is exactly
/// the pre-706 shape, and a compiler that emits no populations plans
/// byte-for-byte as it did (GP 11).
type CompiledQuestion = {
    Triples: TripleCandidate list
    Populations: PopulationTriple list
}

module CompiledQuestion =

    /// The empty compilation — nothing in the question mapped to
    /// registered vocabulary.
    let empty: CompiledQuestion = { Triples = []; Populations = [] }

    /// A point-only compilation — the pre-706 `TripleCompiler` result,
    /// lifted.
    let ofTriples (triples: TripleCandidate list) : CompiledQuestion = { Triples = triples; Populations = [] }

    /// Whether the compilation produced nothing at all to resolve.
    let isEmpty (compiled: CompiledQuestion) : bool =
        List.isEmpty compiled.Triples && List.isEmpty compiled.Populations

/// Why a triple (or a whole question) is refused — typed, always naming
/// the gap (GP 9): the caller learns *what* was unrecognised or
/// withheld, never a fabricated nearest match.
type PlanRefusalReason =
    /// The metric id is not in the deployment's registry vocabulary.
    | UnrecognisedMetric of metricId: string
    /// The subject-hierarchy id is not in the registry vocabulary.
    | UnrecognisedSubject of hierarchyId: string
    /// The subject path is deeper than the hierarchy's declared levels.
    | InvalidSubjectPath of hierarchyId: string * path: string list
    /// A fact answers the triple but is not disclosable at the answer
    /// surface — names the policy, never the value (Phase 525).
    | UndisclosableFact of factId: string * policyRef: string
    /// The question produced no compilable triples at all — `detail`
    /// names why (no compiler composed, extraction failed, or nothing in
    /// the question maps to registered vocabulary).
    | QuestionNotCompiled of detail: string
    /// Phase 706 — the compiler emitted an ordering token outside the
    /// declared vocabulary. A guessed sort order is the one thing a
    /// ranking must never invent, so this refuses rather than defaulting.
    | UnrecognisedOrdering of ordering: string
    /// Phase 706 — the population read itself refused: the metric's
    /// direction-of-better is unregistered, or declared `Neutral` (which
    /// is a declaration that there is *no* better direction). `detail` is
    /// Phase 701's own refusal text, carried verbatim because it already
    /// names the remedy and re-wording it here would fork the wording.
    | PopulationNotOrderable of metricId: string * detail: string

/// Canonical wording for a plan refusal — names the gap, never a value.
/// The disclosure arm reuses the Phase 525 canonical refusal text so
/// wording never drifts between doors.
module PlanRefusalReason =
    let describe (reason: PlanRefusalReason) : string =
        match reason with
        | UnrecognisedMetric metricId -> sprintf "unrecognised metric id '%s' — not in the registry vocabulary" metricId
        | UnrecognisedSubject hierarchyId ->
            sprintf "unrecognised subject hierarchy '%s' — not in the registry vocabulary" hierarchyId
        | InvalidSubjectPath(hierarchyId, path) ->
            sprintf
                "subject path '%s' is deeper than the declared levels of hierarchy '%s'"
                (String.concat ">" path)
                hierarchyId
        | UndisclosableFact(_, policyRef) -> FactDisclosureVerdict.refusalText policyRef
        | QuestionNotCompiled detail -> sprintf "the question could not be compiled to registry triples: %s" detail
        | UnrecognisedOrdering ordering ->
            sprintf
                "unrecognised population ordering '%s' — expected one of: %s"
                ordering
                (String.concat ", " PopulationTriple.OrderingVocabulary)
        // Carried verbatim: Phase 701's refusal already distinguishes
        // "never heard of this metric" from "this metric has no better
        // direction" and names each remedy. A second wording here would
        // be a second answer to the same question.
        | PopulationNotOrderable(_, detail) -> detail

/// One member of a planned ranking: its **true** rank and the fact id it
/// cites — never its subject and never its value.
///
/// The same posture `UseFact` takes for a point read, at population
/// scale: a plan cites fact ids, and the values stay in the store behind
/// the disclosure gate where they can be re-checked at the surface that
/// finally quotes them. Ranks are the store's own positions, so a
/// withheld member leaves a visible gap rather than promoting the member
/// below it.
type RankedFactRef = {
    /// Position in the store's ranking, 1-based.
    Rank: int
    /// The head's content-addressed fact id — the `CitesFact` edge the
    /// provenance chain walks.
    FactId: string
}

/// The `RefreshFact` payload shape, for a population whose ranked members
/// are **wholly** stale: the top-ranked member's fact id and the earliest
/// derived stale-since across the ranking. Typed, execution deferred —
/// 560's posture, in a population's clothing.
type AggregateRefresh = { FactId: string; StaleSince: DateTime }

/// The resolved population read behind a `UseAggregate` step — what was
/// asked, what came back, and what the disclosure gate left of it.
///
/// **Query + digest, never the member list (706.D).** The plan is
/// recorded into the provenance chain, so "how was this ranking
/// produced?" must be answerable from the record alone: the population
/// triple that ran, the resolved direction, the ceiling that applied, the
/// fact ids cited, what was withheld and why, and the population summary.
/// What it deliberately does *not* carry is the population — no subjects,
/// no values, no per-member listing. A plan node is an audit artefact,
/// not a second egress door.
type AggregatePlan = {
    /// The population triple the planner ran, after ordering resolution.
    Population: PopulationTriple
    /// The concrete sort direction the store applied — reported so a
    /// surface can say *why* this order rather than restating the request.
    Direction: RankDirection
    /// The top-k actually applied, after the store's `MaxTopK` clamp.
    EffectiveTopK: int
    /// Whether the request asked for more than the ceiling allowed.
    TopKCapped: bool
    /// Whether comparable members exist below the ceiling.
    Truncated: bool
    /// The disclosable top-k, in true-rank order.
    Ranked: RankedFactRef list
    /// How many ranked members the disclosure gate withheld.
    WithheldCount: int
    /// Withheld members as a count grouped by policy ref — existence
    /// disclosed, identity and value never.
    WithheldByPolicy: (string * int) list
    /// The population summary, already gated: the magnitude block
    /// (minimum / maximum / mean) is suppressed whenever anything was
    /// withheld, because a minimum IS some member's own value. Counts,
    /// period coverage, the freshness histogram and the method mix are
    /// existence-level and ride regardless.
    Stats: PopulationStats
    /// Whether that magnitude suppression applied.
    ValueStatisticsWithheld: bool
    /// Present exactly when **every** ranked member is stale: the refresh
    /// the plan defers, in the `RefreshFact` shape.
    ///
    /// The step stays `UseAggregate` rather than collapsing to a bare
    /// `RefreshFact`, and that is a decision. `RefreshFact` means
    /// "quotable with a freshness caveat while a refresh is planned" — so
    /// for a population the ranking and the summary are exactly what
    /// remains quotable, and discarding them to emit one fact id would
    /// throw away the answer in order to report its caveat. The refresh
    /// therefore rides *inside* the aggregate, in the same (factId,
    /// staleSince) shape, and 706.D's chain content survives the stale
    /// case.
    Refresh: AggregateRefresh option
    /// Human-readable freshness caveat when any ranked member is stale;
    /// `None` when the whole ranking is fresh.
    FreshnessCaveat: string option
}

/// The typed resolution for one compiled triple — the planner's verdict
/// on how (or whether) the triple can be answered. First slice (Phase
/// 560): `UseFact` and `Refuse` are live end-to-end; `RefreshFact` /
/// `ComputeFact` / `RequestData` land typed with execution deferred to
/// the execution-tier phase. Phase 706 adds `UseAggregate` — the
/// population form, resolved through `IFactStore.QueryPopulation`.
type PlanStep =
    /// A current, fresh, disclosable fact answers the triple — the
    /// answer quotes it verbatim, citing the fact id.
    | UseFact of factId: string
    /// The best disclosable head is stale — quotable with a freshness
    /// caveat while a refresh is planned. `staleSince` is the derived
    /// instant it went stale (never a stored flag).
    | RefreshFact of factId: string * staleSince: DateTime
    /// No fact exists, but the registry maps the metric to a producing
    /// operation — computable. Names the operation the execution tier
    /// will dispatch.
    | ComputeFact of operationId: string
    /// No fact and no computation path — the data (or a human assertion)
    /// must be requested. `detail` names what is missing, so every
    /// unanswerable-for-data question becomes an acquisition prompt.
    | RequestData of metricId: string * detail: string
    /// Phase 706 — a *population* answers the question: a bounded ranking
    /// plus the summary of what it ranked over, resolved through Phase
    /// 701's `QueryPopulation` with freshness and disclosure honoured per
    /// member. Additive: a plan compiled before this case existed carries
    /// none, and resolves byte-for-byte as it did (GP 11).
    | UseAggregate of aggregate: AggregatePlan
    /// The triple cannot be answered — a typed refusal naming the gap.
    | Refuse of reason: PlanRefusalReason

/// One planned line: the candidate triple the compiler produced and the
/// planner's typed resolution for it.
type PlannedTriple = {
    Candidate: TripleCandidate
    Step: PlanStep
}

/// The compiled, resolved answer plan — the structured, auditable
/// artifact recorded into the answer's provenance chain (Phase 524).
type AnswerPlan = {
    /// Stable plan identity — the provenance-node id the chain walk
    /// surfaces (Phase 560.D).
    PlanId: string
    /// The question as asked.
    Question: string
    /// Transaction time the plan was compiled (UTC).
    CompiledAt: DateTime
    /// Per-triple typed resolutions, in compiler order.
    Steps: PlannedTriple list
    /// Plan-level refusal when the question compiled to no triples at
    /// all. `None` when `Steps` carry the resolution.
    Refusal: PlanRefusalReason option
}

module AnswerPlan =

    /// Fact ids the plan's answer will cite (`UseFact` + `RefreshFact` +
    /// every disclosable member of a `UseAggregate` ranking) — the
    /// `CitesFact` edge set the provenance recording uses.
    ///
    /// A ranking-backed answer therefore shows its sources exactly as a
    /// point answer does: the chain walk reaches the ranked heads and
    /// their upstream. Withheld members contribute no id by construction
    /// — they were never in `Ranked`.
    let citedFactIds (plan: AnswerPlan) : string list =
        plan.Steps
        |> List.collect (fun planned ->
            match planned.Step with
            | UseFact factId -> [ factId ]
            | RefreshFact(factId, _) -> [ factId ]
            | UseAggregate aggregate -> aggregate.Ranked |> List.map _.FactId
            | ComputeFact _
            | RequestData _
            | Refuse _ -> [])
        |> List.distinct

    /// Every refusal the plan carries (plan-level + per-triple), for
    /// surfaces that render "why can't this be answered".
    let refusals (plan: AnswerPlan) : PlanRefusalReason list =
        let stepRefusals =
            plan.Steps
            |> List.choose (fun planned ->
                match planned.Step with
                | Refuse reason -> Some reason
                | UseFact _
                | RefreshFact _
                | UseAggregate _
                | ComputeFact _
                | RequestData _ -> None)

        match plan.Refusal with
        | Some reason -> reason :: stepRefusals
        | None -> stepRefusals