// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── PopulationQueryTool ─────────────────────────────────────────────
//
// The ServerResident `query_metric_population` AI tool — *population*
// questions go conversational. `query_facts` hard-requires a
// subject, so the model can ask about one subject and structurally cannot
// ask "which subject is highest on M?", "how many exceed T?", or "what
// does the distribution look like?" — the questions a high-cardinality
// population exists to answer. This tool is the typed
// `IFactStore.QueryPopulation` at the model's door.
//
// **The ceiling is the contract, not a courtesy.** A population read
// returns a bounded ranking plus a summary, never the population, and
// the bound is enforced by the store (`PopulationQuery.MaxTopK`) rather
// than by the caller's manners. The result reports `requestedTopK`
// alongside `effectiveTopK` and a `topKCapped` flag, so a model that
// asked for more is *told* its k was capped instead of inferring it from
// a short list — and `truncated` says whether ranked members exist below
// the ceiling. That reporting is the whole reason `PopulationResult`
// carries `EffectiveTopK` / `Truncated`.
//
// **Ordering is declared, never guessed (GP 9).** `best_first` resolves
// through the metric registry's `DirectionOfBetter`; a metric that is
// unregistered, or registered `Neutral`, is a typed refusal naming the
// remedy — two different refusals, because "never heard of it" and "has
// no better direction" lead a caller different places. `ascending` /
// `descending` are the caller's own explicit choice and need no registry.
//
// **Disclosure at the door.** Every ranked fact
// passes the `IFactDisclosureGate` at the `FactToolResult` surface before
// projection, and the gate re-resolves each id through the scope-filtered
// store — so a store implementation that leaked a cross-scope fact into a
// ranking is still caught at the door. What is different from `query_facts` is the
// *shape* of the refusal, and deliberately so: `query_facts` returns one typed
// marker per withheld fact, which at population scale would be up to
// `MaxTopK` markers — a subject-by-subject listing, i.e. a second
// disclosure channel wearing a refusal's clothes. Here withheld members
// fold into a **count grouped by policy ref**: existence disclosed,
// identity and value never (the point read's posture at the scale a
// population forces).
//
// Ranks are the ranking's TRUE positions, so a withheld member leaves a
// visible gap rather than silently promoting the member below it. A
// contiguous renumbering would have the model report the third-best as
// the second-best — a correctness defect dressed as tidiness.
//
// **The value statistics are gated with the members.**
// `minimum` and `maximum` are individual members' values wearing an
// aggregate's clothes: reporting them over a population whose members are
// partly restricted can round-trip exactly the value the gate refused. So
// the magnitude block — minimum / maximum / mean — is suppressed whenever
// anything was withheld from the ranking, and the counts, the period
// coverage and the freshness histogram (all existence-level) ride
// regardless. The residual is stated rather than hidden: the tool sees
// disclosure only for the members it returns, so a restricted member
// ranked *below* the ceiling still contributes to the store's `Mean` and
// can *be* its `Minimum` under a highest-first ranking. The suppression is
// a floor, not a proof; a whole-population disclosure probe is a different
// (and, at 10^5 members, a differently-priced) capability.
//
// **Freshness costs no extra read.** `query_facts` walks each fact's supersession
// chain because it can return superseded facts; a population read cannot
// (there is deliberately no `IncludeSuperseded` — a ranking that mixes a
// value with the value that replaced it is not a population), so freshness
// derives from the head's transaction time through the same
// `Freshness.deriveAt` the store's own histogram uses. The per-member
// freshness and the summary histogram therefore agree by construction
// rather than by coincidence.
//
// **Companions are point reads after the gate.** A population is one
// metric; `with_metrics` names further registered metrics read for each
// ranked member that crossed the door, for its subject and its ranked
// fact's period, so "the top subjects by revenue, with their shoppers" is
// one call. The ranking, its order and its statistics are the ranked
// metric's alone and are identical with or without companions. A member
// withheld from the ranking gets no companion read at all: its existence
// is disclosed once, as the count above. Each companion fact passes the
// gate at `FactToolResult` on its own, and a withheld one is the point
// read's typed marker in its cell, never its value. No fact for that
// subject and period is reported absent, never zero.
//
// **Registration (GP 13).** `FactsCompose.withFactStore` declares this
// tool on `ServerApp.AITools` beside `query_facts`; the declaration
// reaches the AI tool registry only when the AI companion composes. No
// fact store ⇒ never declared; no AI ⇒ never registered.

/// The `query_metric_population` AI tool: definition + executor over the
/// composed `IFactStore.QueryPopulation` / `IFactDisclosureGate`.
/// Declared by `FactsCompose.withFactStore`; consumers normally never
/// call this module directly.
module PopulationQueryTool =

    /// The most companion metrics one call may name in `with_metrics`.
    [<Literal>]
    let MaxCompanionMetrics = 5

    /// The largest `top_k` a call naming companions may ask for. Each
    /// companion is a point read per ranked member, so the ceiling bounds
    /// the reads one call can cost at `MaxCompanionMetrics` times this.
    [<Literal>]
    let MaxCompanionTopK = 100

    // ── Tool definition ───────────────────────────────────────────

    /// The `query_metric_population` tool declaration. `SourceModule` is
    /// the fact store's reserved `_facts` platform source; `Surface =
    /// Both` — a ranked population answers as well in the side panel as
    /// in the full-page surface.
    let definition: AIToolDefinition = {
        Name = "query_metric_population"
        Description =
            "Rank ONE verified metric across MANY subjects and summarise the population it ranked over — the cross-subject counterpart to `query_facts`. Reach for this tool when the question is about a population rather than a subject: \"which subject is highest/lowest on M?\", \"how many subjects exceed T?\", \"what does the spread of M look like?\". Use `query_facts` instead when you already know which subject you are asking about. The result is a BOUNDED ranking plus population statistics — never the population itself: the server caps the top-k regardless of what you request, and reports `requestedTopK`, `effectiveTopK` and `topKCapped` so you can say so plainly rather than imply you saw everything. Each ranked entry carries its true rank, its stable fact id, the canonical rendering of its value (quote it exactly — do not recompute or reformat), its derived freshness, and the identity of the method that produced it. Ordering is DECLARED, never guessed: the default `best_first` resolves through the metric registry's declared direction-of-better (so \"best\" on a lower-is-better metric ranks ascending), and a metric that is unregistered or has no better direction refuses with the remedy named — pass `ascending` or `descending` to rank explicitly. Values whose shape carries no single magnitude (a category, a range, a distribution, a series, a recorded absence) are counted in the statistics and never ranked. Members you are not permitted to see are reported only as a count grouped by the restricting policy — never their identity and never their value — and the population's minimum/maximum/mean are withheld alongside them. Metric and subject-hierarchy ids come from the deployment's grounding registry; use ids given in the conversation or system context — there is no discovery/listing parameter on this tool. Results are scoped to the current user/team."
        Parameters = [
            {
                Name = "metric"
                Type = "string"
                Description =
                    "Registered metric id the population is ranked on (e.g. \"revenue\"). A population is ONE metric across many subjects — never many metrics."
                Required = true
                Default = None
            }
            {
                Name = "subject_hierarchy"
                Type = "string"
                Description =
                    "Stable id of the registered subject hierarchy the population is drawn from (e.g. \"brand\")."
                Required = true
                Default = None
            }
            {
                Name = "level"
                Type = "number"
                Description =
                    "Optional depth filter — the number of subject-path segments, so 0 is the hierarchy root and 2 the second level down. Omit to admit every depth. Ranking a mixed-depth population is legal but rarely meaningful: a parent and its children are not peers."
                Required = false
                Default = None
            }
            {
                Name = "path_prefix"
                Type = "string"
                Description =
                    "Optional subtree filter — ordered member ids from the hierarchy root separated by \">\" (e.g. \"acme\" for every SKU under that brand). Omit to admit the whole hierarchy."
                Required = false
                Default = None
            }
            ToolSchema.parameter
                "path_any_of"
                "Optional any-of filter — subject paths in the `path_prefix` form; a subject is admitted when its path starts with ANY of them (e.g. [\"eu>fr\", \"eu>de\", \"na>us\"] for those three branches). Combines with `path_prefix` and `level`. Use it instead of ranking a wider population and picking members out by eye: the ranks and the population statistics then describe exactly the subjects you asked about. A path that names no subject is not an error — it simply matches nothing."
                false
                (ToolSchema.arrayOf ToolSchema.text)
            ToolSchema.parameter
                "path_exclude"
                "Optional exclusion filter — subject paths in the `path_prefix` form; a subject whose path starts with ANY of them is dropped (e.g. [\"eu>uk\"] for \"outside the UK\"). Applied after `path_prefix` and `path_any_of` and before ranking, so the ranks, the top-k ceiling and the population statistics all describe the subjects that remain."
                false
                (ToolSchema.arrayOf ToolSchema.text)
            {
                Name = "period_from"
                Type = "string"
                Description =
                    "Optional ISO-8601 UTC instant — admit only facts whose valid-time period overlaps [period_from, period_to). Either bound may be given alone for an open-ended window."
                Required = false
                Default = None
            }
            {
                Name = "period_to"
                Type = "string"
                Description = "Optional ISO-8601 UTC instant — the exclusive upper bound of the period window."
                Required = false
                Default = None
            }
            {
                Name = "value_at_least"
                Type = "number"
                Description =
                    "Optional inclusive lower bound on the value — \"how many subjects reach at least T\". Combine with `value_at_most` for a band. Applied before ranking AND before the statistics, so the summary describes the filtered population."
                Required = false
                Default = None
            }
            {
                Name = "value_at_most"
                Type = "number"
                Description = "Optional inclusive upper bound on the value."
                Required = false
                Default = None
            }
            {
                Name = "ordering"
                Type = "string"
                Description =
                    "How the ranking is ordered: \"best_first\" (the default — resolved from the metric registry's declared direction-of-better, and refused rather than guessed when the registry cannot answer), \"descending\" (largest value first), or \"ascending\" (smallest value first)."
                Required = false
                Default = Some "best_first"
            }
            {
                Name = "top_k"
                Type = "number"
                Description =
                    "How many ranked members to return. Clamped server-side into [1, 1000]; the result reports what was actually applied. Ask for what you need to answer the question, not for the population."
                Required = false
                Default = Some "10"
            }
            {
                Name = "as_of"
                Type = "string"
                Description =
                    "Optional ISO-8601 UTC instant — rank the heads that were current at this transaction time (\"what did we believe then?\"). Omit for the current view."
                Required = false
                Default = None
            }
            {
                Name = "methods"
                Type = "string"
                Description =
                    "Which competing method's values to rank when a metric was computed more than one way: \"canonical\" (the default — the metric's declared canonical method where one exists, every competing head where none is) or \"all_competing\" (every competing head, which can rank one subject more than once and is how you surface that alternatives were computed)."
                Required = false
                Default = Some "canonical"
            }
            ToolSchema.parameter
                "with_metrics"
                $"Optional companion metrics — up to {MaxCompanionMetrics} registered metric ids read for EACH ranked subject, for the same subject and the same period as its ranked fact (e.g. [\"shoppers\"] beside a ranking by revenue), so a table of several metrics per subject takes one call instead of a `query_facts` call per subject. The ranking, its order and the population statistics are those of `metric` alone, exactly as without companions. Each ranked entry then carries `companions`: per metric its facts (id, method and canonical rendering — quote it exactly), `absent: true` where the subject has no such fact for that period (never a zero), and a `withheld` marker naming the policy where you may not see the value. A companion is read under its metric's canonical method where one is declared. With companions, `top_k` may be at most {MaxCompanionTopK}. Every id must be registered; an unknown one is refused by name before anything is read."
                false
                (ToolSchema.arrayOf ToolSchema.text)
        ]
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = ToolEffectDeclaration.readFacts
    }

    // ── Argument parsing + validation ─────────────────────────────

    type private PopulationArgs = {
        Metric: string
        Hierarchy: string
        Level: int option
        PathPrefix: string list option
        PathAnyOf: string list list option
        PathExclude: string list list option
        PeriodOverlaps: TemporalExtent option
        Threshold: ValueThreshold option
        Ordering: PopulationOrdering
        RequestedTopK: int
        AsOf: DateTime option
        Methods: PopulationMethodSelection
        /// The companion metric ids, in the order asked; empty when none.
        WithMetrics: string list
    }

    /// The optional `with_metrics` list. Refused by name when it is not an
    /// array, is empty (omitting it is the way to read no companion), names
    /// more than `MaxCompanionMetrics`, carries an entry that is not a
    /// non-blank string, names a metric twice, or names the ranked metric
    /// itself (its value is already each entry's rendering).
    let private companionsArg (root: JsonElement) (ranked: string) : Result<string list, string> =
        match FactTool.tryProperty root "with_metrics" with
        | None -> Ok []
        | Some v when v.ValueKind <> JsonValueKind.Array ->
            Error "Argument 'with_metrics' must be an array of registered metric ids."
        | Some v ->
            let entries = v.EnumerateArray() |> List.ofSeq

            let ids =
                entries
                |> List.mapi (fun i e ->
                    if
                        e.ValueKind = JsonValueKind.String
                        && not (String.IsNullOrWhiteSpace(e.GetString()))
                    then
                        Ok(e.GetString().Trim())
                    else
                        Error(sprintf "Argument 'with_metrics' entry %d is not a metric id." i))

            match
                ids
                |> List.tryPick (function
                    | Error e -> Some e
                    | Ok _ -> None)
            with
            | Some refused -> Error refused
            | None ->
                let ids = ids |> List.choose Result.toOption

                let repeated =
                    ids |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

                if List.isEmpty ids then
                    Error "Argument 'with_metrics' is an empty array; omit it to read no companion metric."
                elif List.length ids > MaxCompanionMetrics then
                    Error(
                        sprintf
                            "Argument 'with_metrics' names %d metrics; at most %d companions may be read in one call."
                            (List.length ids)
                            MaxCompanionMetrics
                    )
                elif not (List.isEmpty repeated) then
                    Error(sprintf "Argument 'with_metrics' names '%s' more than once." (String.concat "', '" repeated))
                elif List.contains ranked ids then
                    Error(
                        sprintf
                            "Argument 'with_metrics' names the ranked metric '%s'; its value is already each ranked entry's rendering."
                            ranked
                    )
                else
                    Ok ids

    /// An optional list of subject paths, each in `path_prefix`'s `>`-separated
    /// form. Refused by name when the member is not an array, when it is empty
    /// (omitting it is the way to apply no filter), or when an entry is not a
    /// string or names no member id — a blank entry read as the empty prefix
    /// would admit, or exclude, the whole hierarchy.
    let private pathListArg (root: JsonElement) (name: string) : Result<string list list option, string> =
        let form =
            "an array of subject paths, each the member ids from the hierarchy root separated by \">\""

        match FactTool.tryProperty root name with
        | None -> Ok None
        | Some v when v.ValueKind <> JsonValueKind.Array -> Error(sprintf "Argument '%s' must be %s." name form)
        | Some v ->
            let entries = v.EnumerateArray() |> List.ofSeq

            if List.isEmpty entries then
                Error(sprintf "Argument '%s' is an empty array; omit it to apply no filter, or pass %s." name form)
            else
                let paths =
                    entries
                    |> List.mapi (fun i e ->
                        if e.ValueKind <> JsonValueKind.String then
                            Error(sprintf "Argument '%s' entry %d is not a string; it must be %s." name i form)
                        else
                            match FactTool.pathSegments (e.GetString()) with
                            | [] ->
                                Error(sprintf "Argument '%s' entry %d names no member id; it must be %s." name i form)
                            | segments -> Ok segments)

                match
                    paths
                    |> List.tryPick (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | Some refused -> Error refused
                | None -> Ok(Some(paths |> List.choose Result.toOption))

    let private parseArgs (argsJson: string) : Result<PopulationArgs, string> =
        match FactTool.parseObject argsJson with
        | Error e -> Error e
        | Ok root ->
            let required =
                FactTool.requiredArg
                    "Metric and subject-hierarchy ids come from the deployment's grounding registry."
                    root

            let ordering =
                FactTool.enumArg
                    root
                    "ordering"
                    [
                        "best_first", RegistryDirection
                        "ascending", Ascending
                        "descending", Descending
                    ]
                    RegistryDirection

            let methods =
                FactTool.enumArg
                    root
                    "methods"
                    [ "canonical", CanonicalMethodOnly; "all_competing", AllCompetingMethods ]
                    CanonicalMethodOnly

            let period = FactTool.periodArg root

            let threshold =
                match FactTool.numberArg root "value_at_least", FactTool.numberArg root "value_at_most" with
                | Error e, _
                | _, Error e -> Error e
                | Ok None, Ok None -> Ok None
                | Ok(Some low), Ok(Some high) when low > high ->
                    Error "Argument 'value_at_least' must not exceed 'value_at_most'."
                | Ok(Some low), Ok(Some high) -> Ok(Some(Between(low, high)))
                | Ok(Some low), Ok None -> Ok(Some(AtLeast low))
                | Ok None, Ok(Some high) -> Ok(Some(AtMost high))

            let level =
                match FactTool.intArg root "level" with
                | Error e -> Error e
                | Ok(Some l) when l < 0 -> Error "Argument 'level' must be zero or greater (0 is the hierarchy root)."
                | Ok l -> Ok l

            match required "metric", required "subject_hierarchy" with
            | Error e, _
            | _, Error e -> Error e
            | Ok metric, Ok hierarchy ->
                match ordering, methods with
                | Error e, _
                | _, Error e -> Error e
                | Ok ordering, Ok methods ->
                    match period, threshold, level with
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error e
                    | Ok period, Ok threshold, Ok level ->
                        match
                            FactTool.intArg root "top_k",
                            FactTool.instantArg root "as_of",
                            FactTool.stringArg root "path_prefix",
                            pathListArg root "path_any_of",
                            pathListArg root "path_exclude"
                        with
                        | Error e, _, _, _, _
                        | _, Error e, _, _, _
                        | _, _, Error e, _, _
                        | _, _, _, Error e, _
                        | _, _, _, _, Error e -> Error e
                        | Ok topK, Ok asOf, Ok prefix, Ok anyOf, Ok exclude ->
                            let requestedTopK = topK |> Option.defaultValue 10

                            match companionsArg root metric with
                            | Error e -> Error e
                            | Ok(_ :: _) when requestedTopK > MaxCompanionTopK ->
                                Error(
                                    sprintf
                                        "Argument 'top_k' is %d; with 'with_metrics' it may be at most %d, because each companion is read for each ranked subject."
                                        requestedTopK
                                        MaxCompanionTopK
                                )
                            | Ok withMetrics ->
                                Ok {
                                    Metric = metric
                                    Hierarchy = hierarchy
                                    Level = level
                                    PathPrefix =
                                        prefix
                                        |> Option.map FactTool.pathSegments
                                        |> Option.filter (List.isEmpty >> not)
                                    PathAnyOf = anyOf
                                    PathExclude = exclude
                                    PeriodOverlaps = period
                                    Threshold = threshold
                                    Ordering = ordering
                                    RequestedTopK = requestedTopK
                                    AsOf = asOf
                                    Methods = methods
                                    WithMetrics = withMetrics
                                }

    /// Every companion must be a registered metric: one that is not is
    /// refused by name before anything is read, and with no registry
    /// composed none is registered.
    let private companionsRegistered
        (registry: Grounding.IMetricRegistry option)
        (args: PopulationArgs)
        : Result<PopulationArgs, string> =
        match
            args.WithMetrics
            |> List.filter (fun m -> registry |> Option.bind (fun r -> r.TryGetMetric m) |> Option.isNone)
        with
        | [] -> Ok args
        | unknown ->
            Error(
                sprintf
                    "Argument 'with_metrics' names metrics the deployment's grounding registry does not register: %s. Companion ids, like the ranked metric's, come from the registry."
                    (String.concat ", " unknown)
            )

    // ── Companions ────────────────────────────────────────────────

    /// The companion facts of one ranked fact: for each companion metric,
    /// the current heads for the ranked fact's subject whose period is the
    /// ranked fact's period, read at the request's `as_of`. A point read per
    /// (member, metric): the store offers no read of many named subjects, and
    /// a branch-wide read would cost the population the ranking exists to
    /// avoid. The read takes the store's default method selection, so a
    /// metric with a declared canonical method answers that lineage.
    let private readCompanions
        (store: IFactStore)
        (scope: ResolvedScope)
        (asOf: DateTime option)
        (metrics: string list)
        (ranked: Fact)
        : Async<(string * Fact list) list> =
        let readOne (metric: string) = async {
            let! found =
                store.Query(
                    scope,
                    {
                        FactQuery.forSubjectMetric ranked.Subject (MetricRef metric) with
                            PeriodOverlaps = Some ranked.Period
                            AsOf = asOf
                    }
                )

            let samePeriod =
                found
                |> List.filter (fun c -> c.Period.From = ranked.Period.From && c.Period.To = ranked.Period.To)
                |> List.sortBy (fun c -> Fact.methodIdentity c.Method, c.FactId)

            return metric, samePeriod
        }

        async {
            let! read = metrics |> List.map readOne |> Async.Sequential
            return List.ofArray read
        }

    // ── Execution ─────────────────────────────────────────────────

    /// The executor over explicit dependencies — the testable core
    /// `execute` adapts `HttpContext` onto. `registry` supplies the
    /// metric's display format + staleness policy; the
    /// *ordering* is resolved by the store against the same registry, so
    /// a `best_first` request refuses identically wherever it is asked
    /// from. Every ranked fact passes `gate` at the `FactToolResult`
    /// surface; withheld members fold into a policy-grouped count, never
    /// a per-subject marker and never a value.
    let executeWith
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (clock: unit -> DateTime)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            match parseArgs argsJson |> Result.bind (companionsRegistered registry) with
            | Error message -> return FactTool.serialize {| error = message |}
            | Ok args ->
                let query: PopulationQuery = {
                    Metric = MetricRef args.Metric
                    Hierarchy = args.Hierarchy
                    Level = args.Level
                    PathPrefix = args.PathPrefix
                    PathAnyOf = args.PathAnyOf
                    PathExclude = args.PathExclude
                    PeriodOverlaps = args.PeriodOverlaps
                    Threshold = args.Threshold
                    Ordering = args.Ordering
                    TopK = args.RequestedTopK
                    AsOf = args.AsOf
                    Methods = args.Methods
                }

                let! outcome = store.QueryPopulation(scope, query)

                match outcome with
                // The store's `Error` is a typed REFUSAL — an unregistered
                // metric, or one declared `Neutral` — never a failure. It
                // names its own remedy, so it reaches the model verbatim.
                | Error refusal -> return FactTool.serialize {| error = refusal |}
                | Ok result ->
                    // Disclosure at the door: one gate check over
                    // the ranked ids at the FactToolResult surface. The
                    // gate audits each deny (GP 6) and re-resolves ids
                    // through the scope-filtered store.
                    let! verdicts = gate.Check(scope, principal, FactToolResult, result.Ranked |> List.map _.FactId)

                    // An id the gate returned no verdict for is denied,
                    // conservatively — the door never fails open.
                    let verdictFor (factId: string) =
                        verdicts
                        |> Map.tryFind factId
                        |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                    // The shared population disclosure fold: true ranks kept
                    // through the gate so a withheld member leaves a gap
                    // rather than promoting the member below it, and
                    // withheld members folded into a COUNT grouped by
                    // policy ref — never an id, never a subject, never a
                    // value. The answer planner's `UseAggregate` step runs
                    // the identical fold, so one store cannot disclose
                    // differently through its two population doors.
                    let disclosure = PopulationDisclosure.fold verdictFor result.Ranked

                    let disclosable = disclosure.Disclosable

                    let withheldByPolicy =
                        disclosure.WithheldByPolicy
                        |> List.map (fun (policyRef, count) -> {|
                            policyRef = policyRef
                            count = count
                            status = FactDisclosureVerdict.refusalText policyRef
                        |})

                    let metricDef = registry |> Option.bind (fun r -> r.TryGetMetric args.Metric)

                    let policy =
                        metricDef
                        |> Option.map _.Staleness
                        |> Option.defaultValue Grounding.UntilSuperseded

                    let now = clock().ToUniversalTime()

                    // The same derivation the store's own freshness
                    // histogram runs (`Freshness.deriveAt … isCurrent =
                    // true`): a population read resolves current heads by
                    // construction, so no supersession-chain walk is owed
                    // — and the per-member freshness cannot disagree with
                    // the summary.
                    let projected =
                        disclosable
                        |> List.map (fun (rank, fact) ->
                            let freshness, staleSince =
                                match Freshness.deriveAt policy fact.AsOf true now with
                                | Fresh -> "Fresh", None
                                | Stale since -> "Stale", Some(since.ToUniversalTime().ToString "o")

                            {|
                                rank = rank
                                factId = fact.FactId
                                subject = SubjectRef.toString fact.Subject
                                metric = fact.Metric.Value
                                rendering = FactRendering.renderFor metricDef fact.Value
                                periodFrom = fact.Period.From.ToUniversalTime().ToString "o"
                                periodTo = fact.Period.To.ToUniversalTime().ToString "o"
                                periodLabel = fact.Period.Label
                                asOf = fact.AsOf.ToUniversalTime().ToString "o"
                                freshness = freshness
                                staleSince = staleSince
                                method = Fact.methodIdentity fact.Method
                            |})

                    // Companions: read only for the members that crossed the
                    // door, then gated together in one check at the same
                    // surface; an id with no verdict is denied, as above.
                    let! read =
                        disclosable
                        |> List.map (fun (_, fact) -> readCompanions store scope args.AsOf args.WithMetrics fact)
                        |> Async.Sequential

                    let companionReads = List.ofArray read

                    let companionIds =
                        companionReads
                        |> Seq.collect (List.collect snd)
                        |> Seq.map _.FactId
                        |> Seq.distinct
                        |> List.ofSeq

                    let! companionVerdicts =
                        if List.isEmpty companionIds then
                            async.Return Map.empty
                        else
                            gate.Check(scope, principal, FactToolResult, companionIds)

                    let companionVerdict (factId: string) =
                        companionVerdicts
                        |> Map.tryFind factId
                        |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                    let companionCells (read: (string * Fact list) list) =
                        read
                        |> List.map (fun (metric, found) ->
                            let companionDef = registry |> Option.bind (fun r -> r.TryGetMetric metric)

                            let shown, withheld =
                                found |> List.partition (fun c -> companionVerdict c.FactId = FactDisclosable)

                            {|
                                metric = metric
                                absent = List.isEmpty found
                                facts =
                                    shown
                                    |> List.map (fun c -> {|
                                        factId = c.FactId
                                        metric = metric
                                        rendering = FactRendering.renderFor companionDef c.Value
                                        method = Fact.methodIdentity c.Method
                                    |})
                                withheld =
                                    withheld
                                    |> List.map (fun c ->
                                        let policyRef =
                                            match companionVerdict c.FactId with
                                            | FactNotDisclosable policyRef -> policyRef
                                            | FactDisclosable -> "unknown-fact"

                                        {|
                                            factId = c.FactId
                                            policyRef = policyRef
                                            status = FactDisclosureVerdict.refusalText policyRef
                                        |})
                            |})

                    // The magnitude block is gated with the members: a
                    // minimum or a maximum IS some member's value, so over
                    // a partly-restricted population it can round-trip
                    // exactly what the gate refused. Counts and coverage
                    // are existence-level and ride regardless. Both the
                    // gating and its wording live on the shared fold.
                    let stats = PopulationDisclosure.disclosedStats disclosure result.Stats

                    let valuesWithheld = PopulationDisclosure.valuesWithheld disclosure

                    let renderPath (path: string list) = String.concat ">" path

                    let renderStat (value: decimal option) =
                        value |> Option.map (fun d -> FactRendering.renderFor metricDef (Scalar d))

                    let directionLabel =
                        match result.Direction with
                        | HighestFirst -> "HighestFirst"
                        | LowestFirst -> "LowestFirst"

                    let withheldReason =
                        if valuesWithheld then
                            Some PopulationDisclosure.MagnitudesWithheldReason
                        else
                            None

                    let answer = {|
                        metric = args.Metric
                        // The metric family's declared
                        // interpretation, carried beside the numbers
                        // it explains. An answer that quotes a
                        // rendering verbatim (which this tool exists
                        // to make possible) still needs the reader to
                        // know what the quantity IS, and the
                        // alternative is the model supplying that
                        // sentence from its own priors. `None` where
                        // the deployment declared none, exactly as
                        // before.
                        metricContext = metricDef |> Option.bind _.Context
                        hierarchy = args.Hierarchy
                        // The subject filters the store applied, echoed
                        // in the form they were asked in, so an answer
                        // can say which population it ranked.
                        filters = {|
                            level = args.Level
                            pathPrefix = args.PathPrefix |> Option.map renderPath
                            pathAnyOf = args.PathAnyOf |> Option.map (List.map renderPath)
                            pathExclude = args.PathExclude |> Option.map (List.map renderPath)
                        |}
                        direction = directionLabel
                        requestedTopK = args.RequestedTopK
                        effectiveTopK = result.EffectiveTopK
                        topKCapped = args.RequestedTopK > result.EffectiveTopK
                        truncated = result.Truncated
                        withheldCount = disclosure.WithheldCount
                        withheld = withheldByPolicy
                        population = {|
                            subjectCount = stats.SubjectCount
                            factCount = stats.FactCount
                            comparableCount = stats.ComparableCount
                            nonComparableCount = stats.NonComparableCount
                            periodFrom = stats.PeriodFrom |> Option.map (fun t -> t.ToUniversalTime().ToString "o")
                            periodTo = stats.PeriodTo |> Option.map (fun t -> t.ToUniversalTime().ToString "o")
                            freshCount = stats.Freshness.FreshCount
                            staleCount = stats.Freshness.StaleCount
                            // How the population was
                            // computed, counted by method identity.
                            // Existence-level like the counts beside
                            // it (a method identity names a procedure,
                            // never a value), so it rides regardless
                            // of the magnitude suppression below: one
                            // estimator over the whole population and
                            // three competing ones are different
                            // answers to "how much should I trust the
                            // spread", and both are answerable without
                            // disclosing a number.
                            methods =
                                stats.MethodMix
                                |> List.map (fun (identity, count) -> {|
                                    method = identity
                                    factCount = count
                                |})
                            minimum = renderStat stats.Minimum
                            maximum = renderStat stats.Maximum
                            mean = renderStat stats.Mean
                            valueStatisticsWithheld = valuesWithheld
                            valueStatisticsWithheldReason = withheldReason
                        |}
                    |}

                    // Without companions the answer is exactly what it was
                    // before they existed; with them each ranked entry gains
                    // its cells and the answer names the companions read.
                    if List.isEmpty args.WithMetrics then
                        return FactTool.serialize {| answer with ranked = projected |}
                    else
                        let ranked =
                            List.zip projected companionReads
                            |> List.map (fun (entry, read) -> {|
                                entry with
                                    companions = companionCells read
                            |})

                        return
                            FactTool.serialize {|
                                answer with
                                    ranked = ranked
                                    withMetrics = args.WithMetrics
                            |}
        }

    // ── HttpContext adapter ───────────────────────────────────────

    /// The registered `HttpContext` executor.
    let execute (ctx: HttpContext) (argsJson: string) : Async<string> =
        FactTool.define
            "query_metric_population"
            (fun c argsJson ->
                executeWith c.Store c.Gate c.Registry (fun () -> DateTime.UtcNow) c.Scope c.UserId argsJson)
            ctx
            argsJson