// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System.Collections.Concurrent
open ToolUp.Platform.VectorKnowledgeTypes

// ─── The fact-clause feeder (Phase 708) ──────────────────────────────
//
// Phase 522 built the push path — a `RetrievalRequest.FactClause`
// resolved ahead of vector search, merged at score 1.0 under the
// verbatim-quoting contract — and Phase 558 wired its resolver into the
// composed pipeline. Nothing ever produced a clause. Every non-test
// construction of `RetrievalRequest` left the field `None`, so the whole
// path was dormant in production and facts reached the model only when
// it thought to call `query_facts`.
//
// This is the missing feeder, and it is deliberately thin: the Phase 560
// planner already compiles a question into (subject, metric, period)
// triples against the registry vocabulary and resolves each to a typed
// `PlanStep`. A `UseFact` step means "a current, fresh, disclosable fact
// answers this triple" — precisely the facts worth pushing into the
// prompt. So the adapter below projects those steps' *candidates* back
// into the fact-companion-free `FactClause` the pipeline speaks, and
// nothing else happens here.
//
// **The planner's posture is what makes this safe (GP 9).** A question
// naming no registered vocabulary compiles to a typed refusal, not a
// nearest match, so an unresolvable turn yields no clause and retrieval
// is byte-identical to its pre-708 self. No similarity guessing can
// enter retrieval through this door, because there is no similarity step
// on this side of it.
//
// **Why the plan is retained rather than discarded (708.B / 560.D).**
// Compiling a question can cost a provider round-trip. The answer's
// provenance recording needs the same plan a moment later, and
// recompiling would both double that cost and — because the compiler is
// a model — risk recording a *different* plan than the one that shaped
// the prompt. So the plan is kept, named by its id, and recorded from
// the retained copy. The id is the only thing that crosses the seam:
// `AnswerPlan` is a fact-companion type and `IFactClausePlanner` exists
// precisely so the RAG tier never sees one.

/// Record an answer plan that the clause planner already computed for
/// this turn, naming it by id rather than handing the plan back (Phase
/// 708.B). The durable event is 560.D's `AnswerPlanRecorded` — the same
/// one `IAnswerPlanner.Record` writes, from the same plan value.
///
/// GP 12 audit: identity by value (three strings); async at the
/// boundary; no callbacks; the retention it reads is bounded and
/// per-process, so a distributed implementation would carry the plan in
/// a shared store without changing this signature.
type IPlannedAnswerRecorder =
    /// Record the retained plan `planId` against the answer message it
    /// produced. Returns `false` — never throws — when the id names no
    /// retained plan (an id from another process, or one evicted by the
    /// retention bound); the answer stands, it simply carries no plan
    /// node.
    abstract RecordPlanned: scopeId: string * messageId: string * planId: string -> Async<bool>

/// The default `IFactClausePlanner` over the Phase 560 answer planner.
/// Registered in DI by `FactsCompose.withFactStore` alongside the planner
/// itself — one compose knob, never two (GP 13).
///
/// `retention` bounds how many recently-planned answers are held for the
/// provenance recording that follows. A plan is retained only when it
/// produced at least one clause (a refusal-only plan has nothing to push
/// and nothing to reuse), removed the moment it is recorded, and the
/// oldest is evicted once the bound is reached — so a deployment whose
/// answer path never records cannot grow this without limit.
type AnswerPlanClausePlanner(planner: IAnswerPlanner, retention: int) =

    let bound = max 1 retention
    let retained = ConcurrentDictionary<string, AnswerPlan>()
    let order = ConcurrentQueue<string>()

    /// Project a compiled candidate onto the generic clause the retrieval
    /// pipeline speaks. `AsOf = None` deliberately: the plan resolved the
    /// *current* head (that is what `UseFact` means), so the clause asks
    /// the resolver for the current head too. Pinning the plan's compile
    /// instant here would ask a different question — "what did we know
    /// when the question was compiled" — and answer it milliseconds later.
    static member internal ClauseOf(candidate: TripleCandidate) : FactClause = {
        SubjectHierarchy = candidate.SubjectHierarchy
        SubjectPath = candidate.SubjectPath
        Metric = candidate.Metric
        PeriodFrom = candidate.PeriodFrom
        PeriodTo = candidate.PeriodTo
        AsOf = None
    }

    /// The pushable clauses a resolved plan carries, in plan order.
    ///
    /// `UseFact` only, and that is the contract rather than an oversight.
    /// `RefreshFact` is quotable-with-a-caveat, but the resolver derives
    /// freshness itself at the retrieval stage and renders the caveat
    /// there, so pushing a stale head through this door would duplicate
    /// that judgement in a second place. `UseAggregate` cannot be
    /// expressed as a clause at all — a `FactClause` names ONE subject
    /// path, and a population is a ranking over many (Phase 706); its
    /// facts ride the answer through the plan's own citations, not
    /// through retrieval. `ComputeFact` / `RequestData` / `Refuse` name
    /// gaps, and a gap is not a fact.
    static member internal ClausesOf(plan: AnswerPlan) : FactClause list =
        plan.Steps
        |> List.choose (fun planned ->
            match planned.Step with
            | UseFact _ -> Some(AnswerPlanClausePlanner.ClauseOf planned.Candidate)
            | RefreshFact _
            | ComputeFact _
            | RequestData _
            | UseAggregate _
            | Refuse _ -> None)
        // A compiler may emit the same triple twice ("revenue this quarter
        // vs revenue this quarter"); one clause is one retrieval read.
        |> List.distinct

    member private _.Retain(plan: AnswerPlan) =
        if retained.TryAdd(plan.PlanId, plan) then
            order.Enqueue plan.PlanId

            // Evict oldest-first until back inside the bound. The queue can
            // hold ids already removed by a record, so a dequeue that finds
            // nothing to remove simply continues.
            while retained.Count > bound do
                match order.TryDequeue() with
                | true, oldest -> retained.TryRemove oldest |> ignore
                | false, _ ->
                    // Queue drained but the dictionary is still over bound
                    // — only reachable under a concurrent add racing the
                    // dequeue. Stop rather than spin; the next retain
                    // trims it.
                    retained.Clear()

    /// The plan behind `planId`, if it is still retained. Exposed for the
    /// recorder below and for tests that pin the no-recompile property.
    member _.TryPlan(planId: string) : AnswerPlan option =
        match retained.TryGetValue planId with
        | true, plan -> Some plan
        | false, _ -> None

    interface IFactClausePlanner with

        member this.PlanClauses(scopeId, principal, question) = async {
            let! plan = planner.Plan(scopeId, principal, question)

            match AnswerPlanClausePlanner.ClausesOf plan with
            | [] ->
                // Nothing to push. Deliberately indistinguishable from an
                // unwired planner at the request the caller then builds —
                // the turn is byte-identical to its pre-708 self (GP 11).
                return PlannedFactClauses.none
            | clauses ->
                this.Retain plan

                return {
                    PlanId = plan.PlanId
                    Clauses = clauses
                }
        }

    interface IPlannedAnswerRecorder with

        member _.RecordPlanned(scopeId, messageId, planId) = async {
            match retained.TryRemove planId with
            | true, plan ->
                do! planner.Record(scopeId, messageId, plan)
                return true
            | false, _ -> return false
        }

/// Construction for `AnswerPlanClausePlanner`.
module AnswerPlanClausePlanner =

    /// How many recently-planned answers are held for provenance
    /// recording by default. Sized for "the answer path records within
    /// the same turn", not for a durable queue — a deployment that never
    /// records simply rolls this window.
    [<Literal>]
    let DefaultRetention = 256

    /// The clause planner over a composed answer planner, at the default
    /// retention bound.
    let create (planner: IAnswerPlanner) : AnswerPlanClausePlanner =
        AnswerPlanClausePlanner(planner, DefaultRetention)

    /// The clause planner with an explicit retention bound (tests, and
    /// deployments tuning the window).
    let createWithRetention (planner: IAnswerPlanner) (retention: int) : AnswerPlanClausePlanner =
        AnswerPlanClausePlanner(planner, retention)