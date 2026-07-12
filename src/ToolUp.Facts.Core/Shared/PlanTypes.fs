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

/// The typed resolution for one compiled triple — the planner's verdict
/// on how (or whether) the triple can be answered. First slice (Phase
/// 560): `UseFact` and `Refuse` are live end-to-end; `RefreshFact` /
/// `ComputeFact` / `RequestData` land typed with execution deferred to
/// the execution-tier phase.
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

    /// Fact ids the plan's answer will cite (`UseFact` + `RefreshFact`
    /// steps) — the `CitesFact` edge set the provenance recording uses.
    let citedFactIds (plan: AnswerPlan) : string list =
        plan.Steps
        |> List.choose (fun planned ->
            match planned.Step with
            | UseFact factId -> Some factId
            | RefreshFact(factId, _) -> Some factId
            | ComputeFact _
            | RequestData _
            | Refuse _ -> None)
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
                | ComputeFact _
                | RequestData _ -> None)

        match plan.Refusal with
        | Some reason -> reason :: stepRefusals
        | None -> stepRefusals