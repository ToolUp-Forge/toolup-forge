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
        compiler: TripleCompiler,
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
            | Ok [] ->
                return unanswerable "the question references no registered subject or metric — nothing to resolve"
            | Ok candidates ->
                let! steps =
                    candidates
                    |> List.map (fun candidate -> async {
                        let! step = resolveCandidate scopeId principal candidate
                        return { Candidate = candidate; Step = step }
                    })
                    |> Async.Sequential

                return {
                    PlanId = planId
                    Question = question
                    CompiledAt = compiledAt
                    Steps = Array.toList steps
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

    // Parse the structured-output content into candidates. Conservative:
    // any shape violation is an Error (⇒ a typed QuestionNotCompiled
    // refusal), never a partial silent salvage.
    let private parseCandidates (content: string) : Result<TripleCandidate list, string> =
        let parsed =
            try
                Ok((JsonDocument.Parse content).RootElement.Clone())
            with _ ->
                Error "the compiler returned non-JSON content"

        match parsed with
        | Error e -> Error e
        | Ok root when root.ValueKind <> JsonValueKind.Object -> Error "the compiler returned a non-object document"
        | Ok root ->
            match root.TryGetProperty "triples" with
            | false, _ -> Error "the compiler returned no 'triples' array"
            | true, triples when triples.ValueKind <> JsonValueKind.Array ->
                Error "the compiler's 'triples' is not an array"
            | true, triples ->
                let str (el: JsonElement) (name: string) : string option =
                    match el.TryGetProperty name with
                    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                    | _ -> None

                let instant (el: JsonElement) (name: string) : Result<DateTime option, string> =
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

                let folder (acc: Result<TripleCandidate list, string>) (item: JsonElement) =
                    match acc with
                    | Error e -> Error e
                    | Ok collected ->
                        match str item "subject_hierarchy", str item "metric" with
                        | None, _ -> Error "a compiled triple is missing 'subject_hierarchy'"
                        | _, None -> Error "a compiled triple is missing 'metric'"
                        | Some hierarchy, Some metric ->
                            let path =
                                match item.TryGetProperty "subject_path" with
                                | true, v when v.ValueKind = JsonValueKind.Array ->
                                    v.EnumerateArray()
                                    |> Seq.choose (fun seg ->
                                        if seg.ValueKind = JsonValueKind.String then
                                            Some(seg.GetString())
                                        else
                                            None)
                                    |> List.ofSeq
                                | _ -> []

                            match instant item "period_from", instant item "period_to" with
                            | Error e, _
                            | _, Error e -> Error e
                            | Ok from, Ok to' ->
                                Ok(
                                    collected
                                    @ [
                                        {
                                            SubjectHierarchy = hierarchy
                                            SubjectPath = path
                                            Metric = metric
                                            PeriodFrom = from
                                            PeriodTo = to'
                                            PeriodLabel = str item "period_label"
                                        }
                                    ]
                                )

                triples.EnumerateArray() |> Seq.fold folder (Ok [])

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