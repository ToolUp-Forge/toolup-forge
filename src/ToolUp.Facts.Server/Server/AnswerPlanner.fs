// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes

// ─── AnswerPlanner (Phase 560) ───────────────────────────────────────
//
// The grounded answer planner — the Stage-2 centrepiece: compile a
// question into (subject, metric, period) triples against the registry
// vocabulary, resolve each triple to a typed `PlanStep`, and record the
// plan into the answer's provenance chain (Phase 524) — "EXPLAIN for
// answers". The same machinery a later standing-question evaluator
// re-triggers; rungs 3–5 of the continuous-intelligence ladder are
// configuration over it.
//
// **The LLM is only the compiler.** Question → candidate triples goes
// through the structured-output surface (`IAIProvider.
// SendStructuredMessage`, Phase 67b), prompted with the registry
// vocabulary. Everything downstream is deterministic: vocabulary
// validation (unknown id ⇒ typed `Refuse` naming what was unrecognised —
// GP 9, never a silent similarity fallback), store lookup, freshness
// derivation, and disclosure. A deployment can substitute any
// `TripleCompiler` (a deterministic parser, a cached compiler) — the
// planner never cares where candidates came from.
//
// **Resolution honours freshness + disclosure (560.C).** Per triple:
// the scope-filtered store query returns the current heads (canonical-
// method default per Phase 566); every head passes the Phase 525
// disclosure gate at the `FactRetrieval` surface *before* it can be
// planned — a denied fact plans as `Refuse` naming the policy, never
// `UseFact` (the model must not even see it). The best disclosable head
// resolves Fresh ⇒ `UseFact`, stale ⇒ `RefreshFact`; a recorded
// `Absent` head resolves to `RequestData` naming the recorded gap (the
// closed loop stops re-planning a known dead end); no head at all
// resolves via the registry's `ProducingOperation` to `ComputeFact`, or
// to `RequestData` when no computation path exists. Execution of the
// deferred steps is the execution-tier phase; here they land typed.
//
// **Populations compile too (Phase 706).** A `TripleCandidate` names one
// subject, so every superlative or aggregate question — "most elastic",
// "top 10 by X", "how many above T" — either failed vocabulary
// resolution or degraded to an unanswerable point lookup. The
// `QuestionCompiler` seam emits `PopulationTriple`s from the same
// registry vocabulary through the same structured-output surface, and
// `resolvePopulation` resolves each through Phase 701's
// `IFactStore.QueryPopulation` to a `UseAggregate` step: ordering
// resolved (never guessed — an unrecognised token and an unregistered
// direction-of-better are two *different* typed refusals), disclosure
// folded through the shared `PopulationDisclosure` the AI tool also runs,
// freshness derived per member. The plan records the population query and
// the gated stats digest — never the member list — so "how was this
// ranking produced?" is a chain walk exactly as it is for a point answer.
//
// **The plan is provenance (560.D).** `Record` writes the plan into the
// fact companion's `_facts` audit trail (`AnswerPlanRecorded`), and
// `AnswerPlanProvenance.chainForMessage` stitches it into the Phase 524
// chain walk: message --PlannedBy--> plan --CitesFact--> facts, with the
// facts' upstream walked as usual.
//
// GP 12 audit: identity by value (strings, value records); async at
// every boundary; no callbacks; stateless between calls (every plan
// recomputes from the stores); scope is the shard key with no
// cross-scope ordering promise; the clock is an injected seam.

/// The compiler seam (Phase 560.B): question → raw candidate triples,
/// or a failure detail (which the planner surfaces as a typed
/// `QuestionNotCompiled` refusal — GP 9). `AnswerPlanner.
/// structuredCompiler` builds one over the 67b structured-output
/// surface; tests and deterministic front-ends inject their own.
type TripleCompiler = string -> Async<Result<TripleCandidate list, string>>

/// The Phase 706 compiler seam: question → point triples **and**
/// population triples in one compilation, or a failure detail.
///
/// Additive rather than a retyping of `TripleCompiler`, and one seam
/// rather than two: a superlative question and a point question are one
/// extraction over one vocabulary, so splitting them would buy a second
/// provider round-trip per question for nothing. A `TripleCompiler` lifts
/// through `QuestionCompiler.ofTriples` and plans exactly as it did
/// (GP 11).
type QuestionCompiler = string -> Async<Result<CompiledQuestion, string>>

module QuestionCompiler =

    /// Lift a point-only `TripleCompiler` — the pre-706 behaviour,
    /// unchanged.
    let ofTriples (compiler: TripleCompiler) : QuestionCompiler =
        fun question -> async {
            let! compiled = compiler question
            return compiled |> Result.map CompiledQuestion.ofTriples
        }

/// Reserved event-type discriminator for recorded answer plans. Rides
/// the fact store's `_facts` source module (`FactEvents.SourceModule`),
/// joining asserts / supersessions / disclosure denies in one queryable
/// audit record (GP 6).
module PlanEvents =
    /// An answer plan was recorded against a conversation message.
    [<Literal>]
    let RecordedType = "AnswerPlanRecorded"

/// Payload of an `AnswerPlanRecorded` audit event (JSON-serialised into
/// `ModuleEvent.Payload`). Carries the full typed plan — identifiers,
/// steps, and refusal reasons; never a fact *value* (a plan cites fact
/// ids, values stay in the store behind the disclosure gate).
type AnswerPlanRecordedEvent = {
    PlanId: string
    /// The conversation message (the grounded answer) this plan
    /// produced — the chain root the Phase 524 walk starts from.
    MessageId: string
    Plan: AnswerPlan
}

/// The grounded answer planner surface (Phase 560). Registered in DI by
/// `FactsCompose.withFactStore` whenever the fact store is composed —
/// one compose knob, never several (GP 13).
type IAnswerPlanner =
    /// Compile `question` into candidate triples (560.B) and resolve
    /// each to a typed `PlanStep` (560.C) under the caller's resolved
    /// storage scope (GP 4). Never throws for an unanswerable question —
    /// the refusal is typed data in the returned plan.
    abstract Plan: scopeId: string * principal: string * question: string -> Async<AnswerPlan>

    /// Record a compiled plan against the answer message it produced
    /// (560.D) — the durable `AnswerPlanRecorded` event the provenance
    /// chain walk (`AnswerPlanProvenance.chainForMessage`) surfaces as a
    /// plan node.
    abstract Record: scopeId: string * messageId: string * plan: AnswerPlan -> Async<unit>

/// The default `IAnswerPlanner` over the composed fact tier. Construct
/// via `AnswerPlanner.create` / `createWithClock`; registered in DI by
/// `FactsCompose.withFactStore`.
type AnswerPlanner
    (
        store: IFactStore,
        gate: IFactDisclosureGate,
        registry: Grounding.IMetricRegistry option,
        events: IEventStore,
        compiler: QuestionCompiler,
        clock: unit -> DateTime
    ) =

    static let jsonOptions = FableConverters.create ()

    // ── Per-triple resolution (560.C) ─────────────────────────────

    // Vocabulary validation is deterministic and conservative: an id the
    // registry does not declare refuses, naming the id (GP 9) — the
    // planner never "helpfully" resolves a near-miss.
    let resolveCandidate (scopeId: string) (principal: string) (candidate: TripleCandidate) : Async<PlanStep> = async {
        let metricDef = registry |> Option.bind (fun r -> r.TryGetMetric candidate.Metric)

        let subjectDef =
            registry |> Option.bind (fun r -> r.TryGetSubject candidate.SubjectHierarchy)

        match metricDef, subjectDef with
        | None, _ -> return Refuse(UnrecognisedMetric candidate.Metric)
        | _, None -> return Refuse(UnrecognisedSubject candidate.SubjectHierarchy)
        | Some metric, Some subject ->
            if List.length candidate.SubjectPath > List.length subject.Levels then
                return Refuse(InvalidSubjectPath(subject.Id, candidate.SubjectPath))
            else
                let query: FactQuery = {
                    Subject =
                        Some {
                            Hierarchy = subject.Id
                            Path = candidate.SubjectPath
                        }
                    Metric = Some(MetricRef metric.Id)
                    PeriodOverlaps =
                        match candidate.PeriodFrom, candidate.PeriodTo with
                        | None, None -> None
                        | from, to' ->
                            Some {
                                From = from |> Option.defaultValue DateTime.MinValue
                                To = to' |> Option.defaultValue DateTime.MaxValue
                                Label = None
                            }
                    // The canonical-method default (Phase 566) applies —
                    // a method-less query resolves the declared canonical
                    // lineage among competitors.
                    Method = None
                    AsOf = None
                    IncludeSuperseded = false
                }

                let! heads = store.Query(scopeId, query)

                match heads with
                | [] ->
                    // MISS. Computable when the registry maps the metric
                    // to a producing operation; otherwise the data (or an
                    // assertion) must be requested.
                    match metric.ProducingOperation with
                    | Some operationId -> return ComputeFact operationId
                    | None ->
                        return
                            RequestData(
                                metric.Id,
                                sprintf
                                    "no fact is recorded for metric '%s' and no producing operation is declared — load the data or assert the value"
                                    metric.Id
                            )
                | heads ->
                    // Disclosure at plan time (560.C): every candidate
                    // head passes the gate at the FactRetrieval surface —
                    // a planned UseFact rides the retrieval path, and the
                    // model must never see a denied fact. An id the gate
                    // returned no verdict for is denied, conservatively.
                    let! verdicts = gate.Check(scopeId, principal, FactRetrieval, heads |> List.map _.FactId)

                    let verdictFor (factId: string) =
                        verdicts
                        |> Map.tryFind factId
                        |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                    let disclosable, denied =
                        heads |> List.partition (fun f -> verdictFor f.FactId = FactDisclosable)

                    match disclosable |> List.sortByDescending _.AsOf with
                    | [] ->
                        // Every head is withheld — the triple refuses,
                        // naming the policy (never UseFact, never the
                        // value). The gate audited each deny (GP 6).
                        let latestDenied = denied |> List.sortByDescending _.AsOf |> List.head

                        let policyRef =
                            match verdictFor latestDenied.FactId with
                            | FactNotDisclosable policyRef -> policyRef
                            | FactDisclosable -> "unknown-fact" // unreachable — partitioned above

                        return Refuse(UndisclosableFact(latestDenied.FactId, policyRef))
                    | head :: _ ->
                        match head.Value with
                        | Absent reason ->
                            // A recorded absence fact IS the gap — the
                            // closed loop plans the acquisition instead
                            // of re-quoting a known dead end.
                            return RequestData(metric.Id, reason)
                        | _ ->
                            // Freshness is derived, never stored (Phase
                            // 520): the head's successor (if a later
                            // assertion corrected it) feeds isCurrent.
                            let! chain = store.QuerySupersessionChain(scopeId, head.FactId)

                            let successor = chain |> List.tryFind (fun g -> g.Supersedes = Some head.FactId)

                            match
                                Freshness.derive metric.Staleness head successor.IsNone (clock().ToUniversalTime())
                            with
                            | Fresh -> return UseFact head.FactId
                            | Stale since -> return RefreshFact(head.FactId, since)
    }

    // ── Population resolution (706.C) ─────────────────────────────
    //
    // The same shape as `resolveCandidate`, over a subject SET. Every
    // deterministic step it runs already exists once: the subject-set
    // predicate and the ranking live in `PopulationQueryTypes` (Phase
    // 702), the ordering resolution and the two ways it can refuse are
    // Phase 701's, and the disclosure fold is Phase 703's — lifted to
    // `PopulationDisclosure` so the planner and the AI tool cannot
    // disclose differently from one store. What is genuinely new here is
    // only the freshness disposition of a *population*, which a point
    // read has no analogue for.
    let resolvePopulation (scopeId: string) (principal: string) (triple: PopulationTriple) : Async<PlanStep> = async {
        let metricDef = registry |> Option.bind (fun r -> r.TryGetMetric triple.Metric)

        let subjectDef =
            registry |> Option.bind (fun r -> r.TryGetSubject triple.SubjectHierarchy)

        match metricDef, subjectDef with
        | None, _ -> return Refuse(UnrecognisedMetric triple.Metric)
        | _, None -> return Refuse(UnrecognisedSubject triple.SubjectHierarchy)
        | Some metric, Some subject ->
            if List.length triple.PathPrefix > List.length subject.Levels then
                return Refuse(InvalidSubjectPath(subject.Id, triple.PathPrefix))
            else
                // Ordering vocabulary first, and refused rather than
                // defaulted: a sort order is the one thing a ranking must
                // never invent, and "best_first" is a *particular* claim
                // about the metric, not a neutral fallback (GP 9).
                match PopulationTriple.tryOrdering triple.Ordering with
                | None -> return Refuse(UnrecognisedOrdering triple.Ordering)
                | Some ordering ->
                    let query: PopulationQuery = {
                        Metric = MetricRef metric.Id
                        Hierarchy = subject.Id
                        Level = triple.Level
                        PathPrefix =
                            if List.isEmpty triple.PathPrefix then
                                None
                            else
                                Some triple.PathPrefix
                        PeriodOverlaps =
                            match triple.PeriodFrom, triple.PeriodTo with
                            | None, None -> None
                            | from, to' ->
                                Some {
                                    From = from |> Option.defaultValue DateTime.MinValue
                                    To = to' |> Option.defaultValue DateTime.MaxValue
                                    Label = None
                                }
                        Threshold = PopulationTriple.threshold triple
                        Ordering = ordering
                        TopK = triple.TopK
                        // The planner plans against now; a population's
                        // ranking is the current heads by construction
                        // (there is deliberately no IncludeSuperseded).
                        AsOf = None
                        // Never rank one subject twice: the canonical
                        // lineage, exactly as the point path's method-less
                        // query resolves it (Phase 566 / D19).
                        Methods = CanonicalMethodOnly
                    }

                    let! outcome = store.QueryPopulation(scopeId, query)

                    match outcome with
                    // Phase 701's `Error` is a typed REFUSAL — an
                    // unregistered direction-of-better, or one declared
                    // `Neutral` — never a failure, and it already names
                    // its own remedy.
                    | Error refusal -> return Refuse(PopulationNotOrderable(metric.Id, refusal))
                    | Ok result when result.Stats.FactCount = 0 ->
                        // MISS at population scale, resolved by 560's own
                        // rule: computable when the registry maps the
                        // metric to a producing operation, otherwise the
                        // data must be requested. Note the miss is
                        // `FactCount`, not an empty ranking — a population
                        // of non-comparable values (categories, ranges,
                        // recorded absences) matched real facts and is a
                        // legitimate aggregate answer, not a gap.
                        match metric.ProducingOperation with
                        | Some operationId -> return ComputeFact operationId
                        | None ->
                            return
                                RequestData(
                                    metric.Id,
                                    sprintf
                                        "no fact is recorded for metric '%s' anywhere in subject hierarchy '%s' and no producing operation is declared — load the data or assert the values"
                                        metric.Id
                                        subject.Id
                                )
                    | Ok result ->
                        // Disclosure at plan time, at the FactRetrieval
                        // surface the point path already uses: a planned
                        // citation rides the retrieval path, and the model
                        // must never see a denied fact. An id the gate
                        // returned no verdict for is denied, conservatively.
                        let! verdicts =
                            gate.Check(scopeId, principal, FactRetrieval, result.Ranked |> List.map _.FactId)

                        let verdictFor (factId: string) =
                            verdicts
                            |> Map.tryFind factId
                            |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                        let disclosure = PopulationDisclosure.fold verdictFor result.Ranked

                        let now = clock().ToUniversalTime()

                        // A population read resolves current heads by
                        // construction, so freshness costs no supersession
                        // walk and cannot disagree with the store's own
                        // histogram — the same `deriveAt … isCurrent =
                        // true` both run.
                        let staleSinces =
                            disclosure.Disclosable
                            |> List.choose (fun (_, fact) ->
                                match Freshness.deriveAt metric.Staleness fact.AsOf true now with
                                | Stale since -> Some since
                                | Fresh -> None)

                        let rankedCount = List.length disclosure.Disclosable
                        let staleCount = List.length staleSinces

                        let refresh =
                            if rankedCount > 0 && staleCount = rankedCount then
                                Some {
                                    FactId = disclosure.Disclosable |> List.head |> snd |> _.FactId
                                    StaleSince = List.min staleSinces
                                }
                            else
                                None

                        let caveat =
                            if staleCount = 0 then
                                None
                            elif staleCount = rankedCount then
                                Some(
                                    sprintf
                                        "every one of the %d ranked members is stale under the metric's staleness policy — the ranking is quotable with a freshness caveat while a refresh is planned"
                                        rankedCount
                                )
                            else
                                Some(
                                    sprintf
                                        "%d of the %d ranked members are stale under the metric's staleness policy — quote the ranking with a freshness caveat"
                                        staleCount
                                        rankedCount
                                )

                        return
                            UseAggregate {
                                Population = triple
                                Direction = result.Direction
                                EffectiveTopK = result.EffectiveTopK
                                TopKCapped = triple.TopK > result.EffectiveTopK
                                Truncated = result.Truncated
                                Ranked =
                                    disclosure.Disclosable
                                    |> List.map (fun (rank, fact) -> { Rank = rank; FactId = fact.FactId })
                                WithheldCount = disclosure.WithheldCount
                                WithheldByPolicy = disclosure.WithheldByPolicy
                                Stats = PopulationDisclosure.disclosedStats disclosure result.Stats
                                ValueStatisticsWithheld = PopulationDisclosure.valuesWithheld disclosure
                                Refresh = refresh
                                FreshnessCaveat = caveat
                            }
    }

    /// The pre-706 construction, preserved exactly: a point-only
    /// `TripleCompiler` lifted onto the question seam. Kept as its own
    /// constructor rather than an optional argument, so the existing
    /// public token stays intact and the diff is additive (an `?compiler`
    /// would fold both into one widened ctor and read as a removal).
    new
        (
            store: IFactStore,
            gate: IFactDisclosureGate,
            registry: Grounding.IMetricRegistry option,
            events: IEventStore,
            compiler: TripleCompiler,
            clock: unit -> DateTime
        ) =
        AnswerPlanner(store, gate, registry, events, QuestionCompiler.ofTriples compiler, clock)

    interface IAnswerPlanner with

        member _.Plan(scopeId, principal, question) = async {
            let planId = "plan-" + Guid.NewGuid().ToString "N"
            let compiledAt = clock().ToUniversalTime()

            let unanswerable detail = {
                PlanId = planId
                Question = question
                CompiledAt = compiledAt
                Steps = []
                Refusal = Some(QuestionNotCompiled detail)
            }

            let! compiled = compiler question

            match compiled with
            | Error detail -> return unanswerable detail
            | Ok compiled when CompiledQuestion.isEmpty compiled ->
                return unanswerable "the question references no registered subject or metric — nothing to resolve"
            | Ok compiled ->
                let! pointSteps =
                    compiled.Triples
                    |> List.map (fun candidate -> async {
                        let! step = resolveCandidate scopeId principal candidate
                        return { Candidate = candidate; Step = step }
                    })
                    |> Async.Sequential

                // Population steps ride the same `PlannedTriple` list
                // behind the point-shaped shadow candidate — the plan's
                // shape is unchanged, the population form lives on the
                // step (see `PopulationTriple.toCandidate`).
                let! populationSteps =
                    compiled.Populations
                    |> List.map (fun triple -> async {
                        let! step = resolvePopulation scopeId principal triple

                        return {
                            Candidate = PopulationTriple.toCandidate triple
                            Step = step
                        }
                    })
                    |> Async.Sequential

                return {
                    PlanId = planId
                    Question = question
                    CompiledAt = compiledAt
                    Steps = Array.toList pointSteps @ Array.toList populationSteps
                    Refusal = None
                }
        }

        member _.Record(scopeId, messageId, plan) = async {
            do!
                events.Write {
                    Id = Guid.NewGuid()
                    OccurredAt = clock().ToUniversalTime()
                    ScopeId = scopeId
                    SourceModule = FactEvents.SourceModule
                    EventType = PlanEvents.RecordedType
                    Payload =
                        JsonSerializer.Serialize(
                            {
                                PlanId = plan.PlanId
                                MessageId = messageId
                                Plan = plan
                            },
                            jsonOptions
                        )
                }
        }

/// Construction + the compiler implementations for `AnswerPlanner`.
module AnswerPlanner =

    let private jsonOptions = FableConverters.create ()

    // ── Compilers (560.B) ─────────────────────────────────────────

    /// The compiler used when no AI provider (and no custom compiler) is
    /// composed: every question refuses, naming the missing substrate —
    /// typed and honest (GP 9 / GP 13), never a guess.
    let noCompiler: TripleCompiler =
        fun _ -> async {
            return
                Error
                    "no question compiler is composed — register an IAIProvider (or supply a custom TripleCompiler) to compile questions into registry triples"
        }

    /// The JSON Schema the 67b structured-output call constrains the
    /// compiler's answer to — an object with a `triples` array of
    /// candidate (subject, metric, period) objects.
    [<Literal>]
    let TripleSchema =
        """{
  "type": "object",
  "properties": {
    "triples": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "subject_hierarchy": { "type": "string", "description": "Registered subject-hierarchy id the question is about." },
          "subject_path": { "type": "array", "items": { "type": "string" }, "description": "Ordered member ids from the hierarchy root; empty for the root roll-up." },
          "metric": { "type": "string", "description": "Registered metric id." },
          "period_from": { "type": "string", "description": "Optional ISO-8601 UTC instant — inclusive start of the asked period." },
          "period_to": { "type": "string", "description": "Optional ISO-8601 UTC instant — exclusive end of the asked period." },
          "period_label": { "type": "string", "description": "Optional human period tag, e.g. Q2-2026." }
        },
        "required": ["subject_hierarchy", "subject_path", "metric"]
      }
    }
  },
  "required": ["triples"]
}"""

    /// The Phase 706 schema: `TripleSchema` plus a `populations` array for
    /// superlative and aggregate intent. `triples` stays the only required
    /// key, so a compilation with no population is the pre-706 document
    /// exactly (GP 11).
    [<Literal>]
    let QuestionSchema =
        """{
  "type": "object",
  "properties": {
    "triples": {
      "type": "array",
      "description": "Point questions: one subject, one metric, one period. Use this when the question names WHICH subject it is about.",
      "items": {
        "type": "object",
        "properties": {
          "subject_hierarchy": { "type": "string", "description": "Registered subject-hierarchy id the question is about." },
          "subject_path": { "type": "array", "items": { "type": "string" }, "description": "Ordered member ids from the hierarchy root; empty for the root roll-up." },
          "metric": { "type": "string", "description": "Registered metric id." },
          "period_from": { "type": "string", "description": "Optional ISO-8601 UTC instant — inclusive start of the asked period." },
          "period_to": { "type": "string", "description": "Optional ISO-8601 UTC instant — exclusive end of the asked period." },
          "period_label": { "type": "string", "description": "Optional human period tag, e.g. Q2-2026." }
        },
        "required": ["subject_hierarchy", "subject_path", "metric"]
      }
    },
    "populations": {
      "type": "array",
      "description": "Superlative and aggregate questions: ONE metric ranked across MANY subjects. Use this when the question asks which subject is highest/lowest/most/least, for a top-N, or for a count or spread across a set — i.e. when the question does NOT name which subject it is about.",
      "items": {
        "type": "object",
        "properties": {
          "subject_hierarchy": { "type": "string", "description": "Registered subject-hierarchy id the population is drawn from." },
          "metric": { "type": "string", "description": "Registered metric id the population is ranked on. ONE metric across many subjects — never many metrics." },
          "level": { "type": "number", "description": "Optional depth filter — the number of subject-path segments, so 0 is the hierarchy root and 2 the second level down. Omit to admit every depth." },
          "path_prefix": { "type": "array", "items": { "type": "string" }, "description": "Optional subtree filter — ordered member ids from the hierarchy root (e.g. one brand, to rank only the SKUs under it). Omit for the whole hierarchy." },
          "period_from": { "type": "string", "description": "Optional ISO-8601 UTC instant — inclusive start of the asked period." },
          "period_to": { "type": "string", "description": "Optional ISO-8601 UTC instant — exclusive end of the asked period." },
          "period_label": { "type": "string", "description": "Optional human period tag, e.g. Q2-2026." },
          "ordering": { "type": "string", "description": "One of: best_first (the registry's declared direction-of-better for the metric — use this for 'best', 'most', 'worst', 'least'), descending (largest value first), ascending (smallest value first). Emit exactly one of these three tokens; never invent an ordering word." },
          "top_k": { "type": "number", "description": "How many ranked members the answer needs. Ask for what the question needs, not for the population." },
          "value_at_least": { "type": "number", "description": "Optional inclusive lower bound on the value — for 'how many exceed T' questions." },
          "value_at_most": { "type": "number", "description": "Optional inclusive upper bound on the value." }
        },
        "required": ["subject_hierarchy", "metric"]
      }
    }
  },
  "required": ["triples"]
}"""

    /// The compiler-role system prompt: the registry vocabulary the
    /// candidates must be drawn from, and the refusal-over-fabrication
    /// instruction (an unmappable question compiles to an empty list —
    /// never an invented id).
    let vocabularyPrompt (registry: Grounding.IMetricRegistry option) : string =
        let sb = StringBuilder()

        sb.AppendLine
            "You are a compiler, not an assistant. Compile the user's question into (subject, metric, period) triples drawn ONLY from the registered vocabulary below. Use ids exactly as declared. If a part of the question maps to nothing declared, omit it; if nothing maps, return an empty triples array. Never invent, guess, or approximate an id."
        |> ignore

        match registry with
        | None ->
            sb.AppendLine "Registered metrics: none. Registered subject hierarchies: none."
            |> ignore
        | Some r ->
            sb.AppendLine "Registered metrics (id — name, unit):" |> ignore

            for m in r.Metrics do
                sb.AppendLine(sprintf "  %s — %s, %s" m.Id m.Name m.Unit) |> ignore

            sb.AppendLine "Registered subject hierarchies (id — levels root→leaf):"
            |> ignore

            for s in r.Subjects do
                sb.AppendLine(sprintf "  %s — %s" s.Id (String.concat " > " s.Levels)) |> ignore

        sb.ToString()

    /// The Phase 706 compiler prompt: the point-triple vocabulary above,
    /// plus what a *population* triple is for and which metrics can be
    /// ranked `best_first`.
    ///
    /// The direction census is the load-bearing addition. "Most elastic"
    /// on a negatively-signed metric ranks *ascending*, and that is a
    /// registry fact rather than a model judgement — so the prompt states
    /// which metrics declare a direction (and which do not, where
    /// `best_first` will refuse), rather than leaving the compiler to
    /// infer a sort order from the metric's name. A separate prompt from
    /// `vocabularyPrompt` so the pre-706 compiler's prompt is byte-for-byte
    /// what it was (GP 11).
    let questionVocabularyPrompt (registry: Grounding.IMetricRegistry option) : string =
        let sb = StringBuilder()
        sb.Append(vocabularyPrompt registry) |> ignore

        sb.AppendLine
            "Two kinds of question compile here. A POINT question names which subject it is about — emit a `triples` entry. A POPULATION question ranks ONE metric across MANY subjects (\"which subject is highest/most/least\", \"the top 10 by X\", \"how many exceed T\", \"what does the spread look like\") — emit a `populations` entry instead, and do NOT invent a subject path for it."
        |> ignore

        sb.AppendLine
            "Ordering is DECLARED, never guessed: emit exactly one of best_first, descending, ascending. Prefer best_first for superlatives — it resolves through the metric's registered direction-of-better, so \"most\" on a lower-is-better metric ranks the right way round without you deciding."
        |> ignore

        match registry with
        | None -> ()
        | Some r ->
            sb.AppendLine "Direction-of-better per metric (best_first refuses where none is declared):"
            |> ignore

            for m in r.Metrics do
                let direction =
                    match m.Direction with
                    | Grounding.HigherIsBetter -> "higher is better — best_first ranks descending"
                    | Grounding.LowerIsBetter -> "lower is better — best_first ranks ascending"
                    | Grounding.Neutral -> "no better direction — best_first REFUSES; pass ascending or descending"

                sb.AppendLine(sprintf "  %s — %s" m.Id direction) |> ignore

        sb.ToString()

    // ── Structured-output parsing ─────────────────────────────────
    //
    // Conservative throughout: any shape violation is an Error (⇒ a typed
    // QuestionNotCompiled refusal), never a partial silent salvage. The
    // element readers are shared by the point-triple and population-triple
    // parsers rather than written twice — two readings of the same
    // structured payload is exactly the kind of thing that agrees until
    // one of them is edited.

    let private jsonRoot (content: string) : Result<JsonElement, string> =
        let parsed =
            try
                Ok((JsonDocument.Parse content).RootElement.Clone())
            with _ ->
                Error "the compiler returned non-JSON content"

        match parsed with
        | Error e -> Error e
        | Ok root when root.ValueKind <> JsonValueKind.Object -> Error "the compiler returned a non-object document"
        | Ok root -> Ok root

    let private str (el: JsonElement) (name: string) : string option =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private instant (el: JsonElement) (name: string) : Result<DateTime option, string> =
        match str el name with
        | None -> Ok None
        | Some s when String.IsNullOrWhiteSpace s -> Ok None
        | Some s ->
            match
                DateTime.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            with
            | true, t -> Ok(Some t)
            | false, _ -> Error(sprintf "the compiler emitted an unparseable '%s' instant: '%s'" name s)

    let private number (el: JsonElement) (name: string) : Result<decimal option, string> =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetDecimal() with
            | true, d -> Ok(Some d)
            | _ -> Error(sprintf "the compiler emitted an unrepresentable '%s' number" name)
        | true, v when v.ValueKind <> JsonValueKind.Null -> Error(sprintf "the compiler's '%s' is not a number" name)
        | _ -> Ok None

    /// A whole-number field. Fractional is refused rather than truncated:
    /// silently reading 2.7 as 2 answers a question nobody asked.
    let private whole (el: JsonElement) (name: string) : Result<int option, string> =
        match number el name with
        | Error e -> Error e
        | Ok None -> Ok None
        | Ok(Some d) when Decimal.Truncate d <> d -> Error(sprintf "the compiler's '%s' is not a whole number" name)
        | Ok(Some d) when d < -1000000m || d > 1000000m -> Error(sprintf "the compiler's '%s' is out of range" name)
        | Ok(Some d) -> Ok(Some(int d))

    let private strings (el: JsonElement) (name: string) : string list =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            v.EnumerateArray()
            |> Seq.choose (fun seg ->
                if seg.ValueKind = JsonValueKind.String then
                    Some(seg.GetString())
                else
                    None)
            |> List.ofSeq
        | _ -> []

    let private triplesOf (root: JsonElement) : Result<TripleCandidate list, string> =
        match root.TryGetProperty "triples" with
        | false, _ -> Error "the compiler returned no 'triples' array"
        | true, triples when triples.ValueKind <> JsonValueKind.Array ->
            Error "the compiler's 'triples' is not an array"
        | true, triples ->
            let folder (acc: Result<TripleCandidate list, string>) (item: JsonElement) =
                match acc with
                | Error e -> Error e
                | Ok collected ->
                    match str item "subject_hierarchy", str item "metric" with
                    | None, _ -> Error "a compiled triple is missing 'subject_hierarchy'"
                    | _, None -> Error "a compiled triple is missing 'metric'"
                    | Some hierarchy, Some metric ->
                        match instant item "period_from", instant item "period_to" with
                        | Error e, _
                        | _, Error e -> Error e
                        | Ok from, Ok to' ->
                            Ok(
                                collected
                                @ [
                                    {
                                        SubjectHierarchy = hierarchy
                                        SubjectPath = strings item "subject_path"
                                        Metric = metric
                                        PeriodFrom = from
                                        PeriodTo = to'
                                        PeriodLabel = str item "period_label"
                                    }
                                ]
                            )

            triples.EnumerateArray() |> Seq.fold folder (Ok [])

    /// Population triples (706.B). **Absent is legal, and that is GP 11**:
    /// a compiler that never learned about populations returns the pre-706
    /// document and compiles exactly as it did.
    ///
    /// The `ordering` token is carried through UNVALIDATED on purpose. An
    /// unrecognised ordering must reach the planner as a typed refusal
    /// naming the gap on *that* triple; refusing it here would condemn the
    /// whole question — including the point triples beside it — for one
    /// bad word.
    let private populationsOf (root: JsonElement) : Result<PopulationTriple list, string> =
        match root.TryGetProperty "populations" with
        | false, _ -> Ok []
        | true, populations when populations.ValueKind = JsonValueKind.Null -> Ok []
        | true, populations when populations.ValueKind <> JsonValueKind.Array ->
            Error "the compiler's 'populations' is not an array"
        | true, populations ->
            let folder (acc: Result<PopulationTriple list, string>) (item: JsonElement) =
                match acc with
                | Error e -> Error e
                | Ok collected ->
                    match str item "subject_hierarchy", str item "metric" with
                    | None, _ -> Error "a compiled population triple is missing 'subject_hierarchy'"
                    | _, None -> Error "a compiled population triple is missing 'metric'"
                    | Some hierarchy, Some metric ->
                        match instant item "period_from", instant item "period_to" with
                        | Error e, _
                        | _, Error e -> Error e
                        | Ok from, Ok to' ->
                            match whole item "level", whole item "top_k" with
                            | Error e, _
                            | _, Error e -> Error e
                            | Ok level, Ok topK ->
                                match number item "value_at_least", number item "value_at_most" with
                                | Error e, _
                                | _, Error e -> Error e
                                | Ok atLeast, Ok atMost ->
                                    let baseline = PopulationTriple.create hierarchy metric

                                    Ok(
                                        collected
                                        @ [
                                            {
                                                baseline with
                                                    Level = level |> Option.filter (fun l -> l >= 0)
                                                    PathPrefix = strings item "path_prefix"
                                                    PeriodFrom = from
                                                    PeriodTo = to'
                                                    PeriodLabel = str item "period_label"
                                                    Ordering =
                                                        str item "ordering"
                                                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                                        |> Option.defaultValue baseline.Ordering
                                                    TopK = topK |> Option.defaultValue baseline.TopK
                                                    ValueAtLeast = atLeast
                                                    ValueAtMost = atMost
                                            }
                                        ]
                                    )

            populations.EnumerateArray() |> Seq.fold folder (Ok [])

    let private parseCandidates (content: string) : Result<TripleCandidate list, string> =
        jsonRoot content |> Result.bind triplesOf

    /// Parse a Phase 706 compilation: point triples plus population
    /// triples. `triples` stays required so a compiler that emits neither
    /// key is a shape violation rather than a silently empty question.
    let private parseQuestion (content: string) : Result<CompiledQuestion, string> =
        match jsonRoot content with
        | Error e -> Error e
        | Ok root ->
            match triplesOf root, populationsOf root with
            | Error e, _
            | _, Error e -> Error e
            | Ok triples, Ok populations ->
                Ok {
                    Triples = triples
                    Populations = populations
                }

    /// The 67b structured-output compiler over an `IAIProvider`: the
    /// model's job is *only* extraction to candidate triples under the
    /// registry-vocabulary prompt; validation stays deterministic in the
    /// planner. Provider / parse failures surface as `Error detail` — a
    /// typed `QuestionNotCompiled` refusal downstream, never a retryable
    /// guess loop.
    let structuredCompiler (provider: IAIProvider) (registry: Grounding.IMetricRegistry option) : TripleCompiler =
        fun question -> async {
            let! result =
                provider.SendStructuredMessage(
                    [ AIProviderMessage.text "user" question ],
                    [],
                    Some(vocabularyPrompt registry),
                    TripleSchema,
                    RetryPolicy.defaults
                )

            match result with
            | Error err -> return Error(sprintf "structured extraction failed: %A" err)
            | Ok response -> return parseCandidates response.Content
        }

    /// The compiler used when no AI provider (and no custom compiler) is
    /// composed, on the Phase 706 seam. Same wording as `noCompiler` — the
    /// missing substrate is the same substrate, and two refusals for one
    /// gap would read as two gaps.
    let noQuestionCompiler: QuestionCompiler =
        fun _ -> async {
            return
                Error
                    "no question compiler is composed — register an IAIProvider (or supply a custom TripleCompiler) to compile questions into registry triples"
        }

    /// The Phase 706 structured-output compiler: ONE 67b call producing
    /// point triples and population triples together, under the
    /// direction-aware vocabulary prompt. The model's job is still *only*
    /// extraction; validation — vocabulary, ordering, subject depth —
    /// stays deterministic in the planner.
    let structuredQuestionCompiler
        (provider: IAIProvider)
        (registry: Grounding.IMetricRegistry option)
        : QuestionCompiler =
        fun question -> async {
            let! result =
                provider.SendStructuredMessage(
                    [ AIProviderMessage.text "user" question ],
                    [],
                    Some(questionVocabularyPrompt registry),
                    QuestionSchema,
                    RetryPolicy.defaults
                )

            match result with
            | Error err -> return Error(sprintf "structured extraction failed: %A" err)
            | Ok response -> return parseQuestion response.Content
        }

    // ── Construction ──────────────────────────────────────────────

    /// The default planner over the composed fact tier. `registry`
    /// supplies the triple vocabulary + staleness policies (Phase 519) —
    /// `None` behaves as the empty vocabulary, so every triple refuses
    /// honestly as unrecognised. Freshness is evaluated at
    /// `DateTime.UtcNow`.
    let create
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (events: IEventStore)
        (compiler: TripleCompiler)
        : IAnswerPlanner =
        AnswerPlanner(store, gate, registry, events, compiler, fun () -> DateTime.UtcNow) :> IAnswerPlanner

    /// `create` with an explicit clock (test seam / deterministic
    /// freshness + timestamps).
    let createWithClock
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (events: IEventStore)
        (compiler: TripleCompiler)
        (clock: unit -> DateTime)
        : IAnswerPlanner =
        AnswerPlanner(store, gate, registry, events, compiler, clock) :> IAnswerPlanner

    /// `create` over the Phase 706 question seam — point triples *and*
    /// population triples from one compilation.
    let createCompiling
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (events: IEventStore)
        (compiler: QuestionCompiler)
        : IAnswerPlanner =
        AnswerPlanner(store, gate, registry, events, compiler, fun () -> DateTime.UtcNow) :> IAnswerPlanner

    /// `createCompiling` with an explicit clock (test seam / deterministic
    /// freshness + timestamps).
    let createCompilingWithClock
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (events: IEventStore)
        (compiler: QuestionCompiler)
        (clock: unit -> DateTime)
        : IAnswerPlanner =
        AnswerPlanner(store, gate, registry, events, compiler, clock) :> IAnswerPlanner

/// The plan-in-the-chain composition (Phase 560.D): recorded plans join
/// the Phase 524 provenance walk as typed plan nodes — message
/// --PlannedBy--> plan --CitesFact--> facts — so "show me how this
/// answer was produced" returns the reasoning plan alongside the data
/// lineage.
module AnswerPlanProvenance =

    let private jsonOptions = FableConverters.create ()

    /// Every plan recorded against `messageId` in scope, oldest first.
    /// Reads the `_facts` audit trail (`AnswerPlanRecorded`); a payload
    /// that fails to deserialise is skipped, never thrown — the chain
    /// walk must stay a read-only view.
    let recordedFor (events: IEventStore) (scopeId: string) (messageId: string) : Async<AnswerPlan list> = async {
        let! rows = events.ReadBySource(scopeId, FactEvents.SourceModule)

        return
            rows
            |> List.filter (fun e -> e.EventType = PlanEvents.RecordedType)
            |> List.choose (fun e ->
                try
                    let recorded =
                        JsonSerializer.Deserialize<AnswerPlanRecordedEvent>(e.Payload, jsonOptions)

                    if recorded.MessageId = messageId then
                        Some recorded.Plan
                    else
                        None
                with _ ->
                    None)
    }

    /// The Phase 524 answer chain with the recorded plan(s) stitched in:
    /// `GetChainForMessage` walks the cited facts' upstream as usual;
    /// each recorded plan joins as an `AnswerPlanNode` with a
    /// message --PlannedBy--> plan edge and plan --CitesFact--> fact
    /// edges for the facts its steps cite. With no recorded plan the
    /// result is exactly the graph's own chain (GP 11).
    let chainForMessage
        (graph: IProvenanceGraph)
        (events: IEventStore)
        (scopeId: string)
        (messageId: string)
        (depth: int)
        : Async<ProvenanceChain> =
        async {
            let! plans = recordedFor events scopeId messageId

            let citedIds = plans |> List.collect AnswerPlan.citedFactIds |> List.distinct

            let! chain = graph.GetChainForMessage(scopeId, messageId, citedIds, depth)

            let planNodes =
                plans
                |> List.map (fun plan -> {
                    Id = plan.PlanId
                    Kind = AnswerPlanNode
                    Disclosure = None
                    Label = sprintf "answer plan: %s" plan.Question
                })

            let planEdges =
                plans
                |> List.collect (fun plan ->
                    {
                        From = messageId
                        To = plan.PlanId
                        Kind = PlannedBy
                    }
                    :: (AnswerPlan.citedFactIds plan
                        |> List.map (fun factId -> {
                            From = plan.PlanId
                            To = factId
                            Kind = CitesFact
                        })))

            return {
                chain with
                    Nodes = planNodes @ chain.Nodes
                    Edges = planEdges @ chain.Edges
            }
        }