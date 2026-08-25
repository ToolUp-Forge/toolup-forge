// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── CoverageTool (Phase 705) ────────────────────────────────────────
//
// The **discovery** surface over the fact base — the one tool that
// answers "what do you actually have?".
//
// Both existing fact tools require the caller to already know what to
// ask for. `query_facts` needs a metric id AND a subject path;
// `query_metric_population` needs a metric id and a hierarchy id. Neither
// has a listing parameter, and both say so in their own descriptions —
// which leaves the ids to arrive out of band, in a system prompt someone
// maintains by hand, or not at all. A model that has to guess a metric id
// guesses wrongly, and Phase 701's ordering resolver then answers with
// the more expensive of its two refusals: *"metric 'sales' is not
// registered"*. That refusal is correct and useless. **This tool exists
// to make the unregistered-metric refusal unreachable**: a model that can
// list the metrics never guesses one.
//
// The other half is interpretive. `MetricDefinition.Context` (task 705.A)
// gives the analyst's narrative for a metric FAMILY a home beside the
// declaration, rather than duplicated into a knowledge chunk per subject.
// It is read here, and carried on a population answer beside the metric
// it explains.
//
// **The result is bounded by the REGISTRY, never by the population.**
// One row per registered metric, and within it one row per registered
// subject hierarchy that actually holds facts. A deployment with 12
// metrics over 3 hierarchies answers in at most 36 rows whether it stores
// four facts or four hundred thousand. The optional `metric` argument
// narrows it further. (Generic tool-result budgeting is Phase 709's; this
// tool bounds its own result by construction and does not reach for a
// budget it would then own.)
//
// **Coverage never reports a magnitude — not a minimum, a maximum, a
// mean, nor a single value.** That is a decision, not an omission. Phase
// 703 established that a population's minimum and maximum are individual
// members' values wearing an aggregate's clothes, and gates them with the
// members. A discovery surface that reported them would be a second door
// onto the same disclosure, at a different price, and the two doors would
// have to be kept in step forever. So this door answers *what is
// queryable* — cardinality, period reach, freshness, method mix — and
// leaves *what the values are* to `query_metric_population`, which gates
// every member it returns. One door discloses values; the other does not
// mention them.
//
// **The disclosure probe, and its honest limit (task 705.B).** Even
// existence-level coverage should not describe a population the caller
// may not see at all, so each (metric, hierarchy) pair's ranked head
// sample is checked through the Phase 525 gate at the `FactToolResult`
// surface. Three outcomes:
//
//   - some probed member is disclosable  ⇒ report the coverage detail
//     (counts, period reach, freshness, method mix), plus the policy refs
//     of anything withheld from the probe;
//   - every probed member is withheld    ⇒ report EXISTENCE AND POLICY
//     ONLY — this metric has facts in this hierarchy, restricted under
//     these policies — and no distribution detail whatsoever;
//   - nothing could be probed (no member of the population carries a
//     rankable value, so the ranking is empty) ⇒ withhold the detail and
//     SAY SO. "I could not check" is reported as itself rather than
//     resolved to either answer.
//
// The probe is a floor, not a proof, and for the same reason Phase 703's
// magnitude suppression is: it sees a bounded head sample, not the
// population. A population whose sampled heads are disclosable may still
// hold restricted members below them. What that costs here is much less
// than it costs a value surface — the detail at stake is a count, not a
// number someone asserted — and the capability that would close it
// properly is the same one 703 named: a whole-population disclosure
// probe, which at 10^5 members is a differently-priced thing and is not
// smuggled in behind a discovery call.
//
// **The method mix is read, never re-derived.** `PopulationStats
// .MethodMix` (Phase 705, on the shared fold) counts the matched heads by
// method identity, so the enumerating store and Phase 702's indexed
// surface report the same mix by construction. Coverage asks for
// `AllCompetingMethods` deliberately: canonical selection would report
// the mix a *default query* sees, and the question a discovery surface
// answers is what the store HOLDS.
//
// **Registration (GP 13).** `FactsCompose.withFactStore` declares this
// tool on `ServerApp.AITools` beside `query_facts` and
// `query_metric_population` — one knob, three siblings. No fact store ⇒
// never declared; no AI ⇒ never registered.

/// The `list_metric_coverage` AI tool: definition + executor over the
/// composed metric registry, `IFactStore.QueryPopulation`, and
/// `IFactDisclosureGate`. Declared by `FactsCompose.withFactStore`;
/// consumers normally never call this module directly.
module CoverageTool =

    let private jsonOptions = FableConverters.create ()

    let private serialize (value: obj) : string =
        JsonSerializer.Serialize(value, jsonOptions)

    /// How many ranked heads per (metric, hierarchy) pair are checked
    /// through the disclosure gate to decide whether that pair's coverage
    /// detail may be described at all.
    ///
    /// Small on purpose. The probe answers a yes/no question — "may this
    /// caller see anything here?" — and every additional member is a gate
    /// check and, on a deny, an audit write, paid on every discovery call
    /// across every registered pair. Ten is enough that a population
    /// mixing classifications answers "yes" rather than tripping the
    /// wholly-restricted branch by sampling accident.
    [<Literal>]
    let DisclosureProbeK = 10

    // ── Tool definition (705.C) ───────────────────────────────────

    /// The `list_metric_coverage` tool declaration. `SourceModule` is the
    /// fact store's reserved `_facts` platform source; `Surface = Both` —
    /// a discovery answer is as useful in the side panel as on the full
    /// page, and is what a session should read before either fact tool.
    let definition: AIToolDefinition = {
        Name = "list_metric_coverage"
        Description =
            "Describe WHAT VERIFIED DATA THIS DEPLOYMENT HOLDS — call this FIRST, before `query_facts` or `query_metric_population`, whenever you do not already have a metric id and a subject-hierarchy id from the conversation. Neither of those tools has a discovery parameter: they answer about ids you name, and an id you guessed is refused rather than approximated. This tool is where the ids come from. It returns, for every registered metric: its id (the exact token the other tools take), its display name, its unit and dimensionality, its declared direction-of-better (and therefore whether `best_first` ordering is available on it), its canonical display format, its staleness policy, and — where the deployment declared one — the analyst's CONTEXT for the metric family: what the quantity means, how it is computed, and how to read its sign and magnitude. Quote that context when explaining a metric rather than inventing an interpretation. For each metric it also reports the subject hierarchies that actually hold facts and, per hierarchy, how many distinct subjects and facts, the range of valid-time periods covered, how many facts are currently fresh versus stale, and the mix of methods that produced them — so you can say \"this deployment tracks elasticity across roughly 300,000 SKUs, weekly, from one estimator\" from a single call, and reach for the population tool knowing it will hit. It reports COUNTS AND COVERAGE ONLY and never a value, a minimum, a maximum or an average: ask `query_metric_population` for values, which returns them through the disclosure gate. Where you are not permitted to see a metric's facts at all, this tool says the facts exist and names the restricting policy, and reports nothing further about them. The result is bounded by how many metrics the deployment registered, never by how many subjects it holds. Results are scoped to the current user/team."
        Parameters = [
            {
                Name = "metric"
                Type = "string"
                Description =
                    "Optional — restrict the report to ONE registered metric id, when you already know which metric you care about and want only its coverage. Omit it to list every registered metric, which is the ordinary use and the reason to call this tool."
                Required = false
                Default = None
            }
        ]
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
    }

    // ── Argument parsing ──────────────────────────────────────────

    let private parseMetricFilter (argsJson: string) : Result<string option, string> =
        let parsed =
            try
                Ok((JsonDocument.Parse argsJson).RootElement.Clone())
            with _ ->
                Error "Arguments are not valid JSON."

        match parsed with
        // An absent argument object is the ordinary call: this tool's
        // whole point is that it needs nothing to be useful.
        | Error e -> Error e
        | Ok root when root.ValueKind = JsonValueKind.Null -> Ok None
        | Ok root when root.ValueKind <> JsonValueKind.Object -> Error "Arguments must be a JSON object."
        | Ok root ->
            match root.TryGetProperty "metric" with
            | true, v when v.ValueKind = JsonValueKind.String ->
                let raw = v.GetString()

                if String.IsNullOrWhiteSpace raw then
                    Ok None
                else
                    Ok(Some(raw.Trim()))
            | true, v when v.ValueKind = JsonValueKind.Null -> Ok None
            | true, _ -> Error "Argument 'metric' must be a string."
            | _ -> Ok None

    // ── Registry rendering (705.A / 705.C) ────────────────────────

    let private directionLabel (d: Grounding.DirectionOfBetter) =
        match d with
        | Grounding.HigherIsBetter -> "HigherIsBetter"
        | Grounding.LowerIsBetter -> "LowerIsBetter"
        | Grounding.Neutral -> "Neutral"

    /// The staleness policy as a readable token. `FreshFor` carries its
    /// window, because "fresh for a day" and "fresh for an hour" are
    /// different answers to "how current is this?" and the discovery
    /// surface is where that question is asked.
    let private stalenessLabel (s: Grounding.StalenessPolicy) =
        match s with
        | Grounding.FreshFor window -> sprintf "FreshFor %s" (window.ToString())
        | Grounding.UntilSuperseded -> "UntilSuperseded"
        | Grounding.UntilUpstreamChange -> "UntilUpstreamChange"

    let private instant (t: DateTime) = t.ToUniversalTime().ToString "o"

    // ── Per-(metric, hierarchy) coverage (705.B) ──────────────────

    /// What a probe of one population's ranked heads established about
    /// the caller's permission to be told anything about it.
    type private ProbeOutcome =
        /// At least one probed head is disclosable; the coverage detail
        /// may be described. `withheldPolicies` names what the probe was
        /// refused, so a partly-restricted population says so.
        | Describable of withheldPolicies: string list
        /// Every probed head was withheld — existence and policy only.
        | WhollyRestricted of policies: string list
        /// Nothing could be probed: the population carries no rankable
        /// value, so the ranking the probe reads is empty. Reported as
        /// itself.
        | Unprobed

    let private probe
        (gate: IFactDisclosureGate)
        (scopeId: string)
        (principal: string)
        (ranked: Fact list)
        : Async<ProbeOutcome> =
        async {
            match ranked with
            | [] -> return Unprobed
            | _ ->
                let ids = ranked |> List.map _.FactId

                let! verdicts = gate.Check(scopeId, principal, FactToolResult, ids)

                // An id the gate returned no verdict for is denied,
                // conservatively — the door never fails open. Same rule
                // as the population tool's, for the same reason.
                let verdictFor (factId: string) =
                    verdicts
                    |> Map.tryFind factId
                    |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                let outcomes = ids |> List.map verdictFor

                let policies =
                    outcomes
                    |> List.choose (fun v ->
                        match v with
                        | FactNotDisclosable policyRef -> Some policyRef
                        | FactDisclosable -> None)
                    |> List.distinct
                    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

                if outcomes |> List.exists (fun v -> v = FactDisclosable) then
                    return Describable policies
                else
                    return WhollyRestricted policies
        }

    /// The population read one coverage row is derived from. Ordering is
    /// explicit rather than registry-directed: a `Neutral` metric has no
    /// best-first order and Phase 701 refuses to invent one, but it very
    /// much has coverage — so the discovery surface must never ask a
    /// question the registry can refuse. `AllCompetingMethods` because
    /// coverage reports what the store HOLDS, not what a default query
    /// would select. `TopK` is the disclosure probe's sample, not a
    /// result: no ranked member is reported by this tool.
    let private coverageQuery (metricId: string) (hierarchyId: string) : PopulationQuery = {
        Metric = MetricRef metricId
        Hierarchy = hierarchyId
        Level = None
        PathPrefix = None
        PeriodOverlaps = None
        Threshold = None
        Ordering = Descending
        TopK = DisclosureProbeK
        AsOf = None
        Methods = AllCompetingMethods
    }

    /// One population row, in ONE shape whatever the probe decided.
    ///
    /// A withheld row is the same record with the detail fields absent
    /// rather than a differently-shaped object, so a reader parses one
    /// schema and learns that "withheld" and "empty" are distinguishable
    /// (`coverageWithheld` says which). The alternative — a union of two
    /// shapes — makes the withheld case the one a careless consumer
    /// mis-reads, and that is the case whose handling matters most.
    let private populationRow
        (subject: Grounding.SubjectDefinition)
        (withheld: bool)
        (withheldReason: string option)
        (policies: string list)
        (partial: bool)
        (stats: PopulationStats option)
        =
        {|
            hierarchy = subject.Id
            hierarchyName = subject.Name
            levels = subject.Levels
            factsExist = true
            coverageWithheld = withheld
            coverageWithheldReason = withheldReason
            restrictedUnderPolicies = policies
            partiallyRestricted = partial
            subjectCount = stats |> Option.map _.SubjectCount
            factCount = stats |> Option.map _.FactCount
            comparableCount = stats |> Option.map _.ComparableCount
            nonComparableCount = stats |> Option.map _.NonComparableCount
            periodFrom = stats |> Option.bind _.PeriodFrom |> Option.map instant
            periodTo = stats |> Option.bind _.PeriodTo |> Option.map instant
            freshCount = stats |> Option.map _.Freshness.FreshCount
            staleCount = stats |> Option.map _.Freshness.StaleCount
            methods =
                stats
                |> Option.map _.MethodMix
                |> Option.defaultValue []
                |> List.map (fun (identity, count) -> {|
                    method = identity
                    factCount = count
                |})
        |}

    let private coverageFor
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (scopeId: string)
        (principal: string)
        (metricId: string)
        (subject: Grounding.SubjectDefinition)
        =
        async {
            let! outcome = store.QueryPopulation(scopeId, coverageQuery metricId subject.Id)

            match outcome with
            // A refusal here is not this tool's to relay: the ordering is
            // explicit, so Phase 701's two typed refusals are both
            // unreachable, and anything else is a store fault rather than
            // an answer about coverage. The pair is reported as holding
            // nothing rather than as an error interleaved into a list.
            | Error _ -> return None
            | Ok result when result.Stats.FactCount = 0 ->
                // No facts for this metric in this hierarchy. Omitted
                // rather than reported as a zero row: a discovery answer
                // listing every empty pair buries the populated ones.
                return None
            | Ok result ->
                let! probed = probe gate scopeId principal result.Ranked

                match probed with
                | WhollyRestricted policies ->
                    return
                        Some(
                            populationRow
                                subject
                                true
                                (Some
                                    "Every fact sampled from this population is restricted to you, so its coverage is not described. The facts exist, and the policies named here are why they are withheld.")
                                policies
                                false
                                None
                        )
                | Unprobed ->
                    return
                        Some(
                            populationRow
                                subject
                                true
                                (Some
                                    "No fact in this population carries a rankable value, so no member could be checked against the disclosure policy. Coverage is withheld rather than assumed disclosable.")
                                []
                                false
                                None
                        )
                | Describable withheldPolicies ->
                    return
                        Some(
                            populationRow
                                subject
                                false
                                None
                                withheldPolicies
                                (not (List.isEmpty withheldPolicies))
                                (Some result.Stats)
                        )
        }

    // ── Execution ─────────────────────────────────────────────────

    /// The executor over explicit dependencies — the testable core
    /// `execute` adapts `HttpContext` onto. The registry supplies the
    /// declarations (there is nothing to discover without it); the store
    /// supplies the coverage; the gate decides whether a given
    /// population's coverage may be described at all.
    let executeWith
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (scopeId: string)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            match parseMetricFilter argsJson with
            | Error message -> return serialize {| error = message |}
            | Ok filter ->
                // No declarations means no registry singleton at all
                // (Phase 519): the deployment may store facts, but it has
                // told the platform nothing about what they mean. That is
                // an ANSWER — "this deployment declares nothing queryable
                // by name" — not a failure, so it runs the ordinary path
                // over the empty registry and says so in a note. One
                // result shape, whatever the deployment declared: a
                // consumer that special-cased an empty-registry envelope
                // would be handling the case least likely to be tested.
                let resolved = registry |> Option.defaultValue Grounding.MetricRegistry.empty

                let note =
                    if registry.IsSome then
                        None
                    else
                        Some
                            "This deployment has no grounding registry: no module declared any metric or subject hierarchy, so there is nothing to discover by name. Facts may still be stored and readable through `query_facts` if a metric id and a subject path were given to you in the conversation."

                let selected =
                    match filter with
                    | None -> Ok resolved.Metrics
                    | Some metricId ->
                        match resolved.TryGetMetric metricId with
                        | Some def -> Ok [ def ]
                        | None ->
                            Error(
                                sprintf
                                    "metric '%s' is not registered in this deployment. Call `list_metric_coverage` with no arguments to see every registered metric id."
                                    metricId
                            )

                match selected with
                | Error refusal -> return serialize {| error = refusal |}
                | Ok metrics ->
                    let subjects = resolved.Subjects

                    // Sequential rather than parallel, deliberately. The
                    // work is one full population read per (metric,
                    // hierarchy) pair, and fanning every pair out at once
                    // would let a discovery call — the cheapest-looking
                    // thing a session does — become the heaviest thing the
                    // store is asked to do concurrently. Sequencing also
                    // makes the row order the registry's declaration
                    // order, which is stable across calls.
                    let! rows =
                        metrics
                        |> List.map (fun def -> async {
                            let! populations =
                                subjects
                                |> List.map (fun subject -> coverageFor store gate scopeId principal def.Id subject)
                                |> Async.Sequential

                            let populations = populations |> Array.choose id |> Array.toList

                            return {|
                                id = def.Id
                                name = def.Name
                                unit = def.Unit
                                dimensionality = def.Dimensionality
                                direction = directionLabel def.Direction
                                // The discriminator that decides whether
                                // `query_metric_population`'s default
                                // ordering resolves — Phase 701's OTHER
                                // typed refusal, answered here before it
                                // can be provoked.
                                supportsBestFirst = def.Direction <> Grounding.Neutral
                                displayFormat = def.DisplayFormat
                                staleness = stalenessLabel def.Staleness
                                context = def.Context
                                producingOperation = def.ProducingOperation
                                canonicalMethod = def.CanonicalMethod
                                hasFacts = not (List.isEmpty populations)
                                populations = populations
                            |}
                        })
                        |> Async.Sequential

                    return
                        serialize {|
                            metricCount = List.length metrics
                            metrics = rows |> Array.toList

                            subjectHierarchies =
                                subjects
                                |> List.map (fun s -> {|
                                    id = s.Id
                                    name = s.Name
                                    levels = s.Levels
                                    calendar = s.Calendar
                                |})

                            note = note
                        |}
        }

    // ── HttpContext adapter (the registered executor) ─────────────

    /// The caller's resolved storage-scope id — the same tenant boundary
    /// the store shards by and the gate resolves fact ids within (GP 4).
    let private scopeIdOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as scope) -> scope.ScopeId
        | _ ->
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

    let private userIdOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let private serviceOf<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
        match ctx.RequestServices.GetService(typeof<'T>) with
        | :? 'T as service -> Some service
        | _ -> None

    /// The registered `HttpContext` executor. Resolves the fact store,
    /// the disclosure gate, and the optional metric registry from DI —
    /// all registered by the same `FactsCompose.withFactStore` knob that
    /// declares this tool, so a composed deployment always finds them.
    let execute (ctx: HttpContext) (argsJson: string) : Async<string> =
        match serviceOf<IFactStore> ctx, serviceOf<IFactDisclosureGate> ctx with
        | Some store, Some gate ->
            let registry = serviceOf<Grounding.IMetricRegistry> ctx

            executeWith store gate registry (scopeIdOf ctx) (userIdOf ctx) argsJson
        | _ -> async {
            return
                serialize {|
                    error = "The fact store is not composed in this deployment — `list_metric_coverage` is unavailable."
                |}
          }