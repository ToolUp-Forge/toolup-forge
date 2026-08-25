// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── PopulationQueryTool (Phase 703) ─────────────────────────────────
//
// The ServerResident `query_metric_population` AI tool — *population*
// questions go conversational. Phase 559's `query_facts` hard-requires a
// subject, so the model can ask about one subject and structurally cannot
// ask "which subject is highest on M?", "how many exceed T?", or "what
// does the distribution look like?" — the questions a high-cardinality
// population exists to answer. This tool is Phase 701's typed
// `IFactStore.QueryPopulation` at the model's door.
//
// **The ceiling is the contract, not a courtesy.** A population read
// returns a bounded ranking plus a summary, never the population, and
// the bound is enforced by the store (`PopulationQuery.MaxTopK`) rather
// than by the caller's manners. The result reports `requestedTopK`
// alongside `effectiveTopK` and a `topKCapped` flag, so a model that
// asked for more is *told* its k was capped instead of inferring it from
// a short list — and `truncated` says whether ranked members exist below
// the ceiling. That reporting is the whole reason Phase 701 put
// `EffectiveTopK` / `Truncated` on `PopulationResult`.
//
// **Ordering is declared, never guessed (GP 9).** `best_first` resolves
// through the metric registry's `DirectionOfBetter`; a metric that is
// unregistered, or registered `Neutral`, is a typed refusal naming the
// remedy — two different refusals, because "never heard of it" and "has
// no better direction" lead a caller different places. `ascending` /
// `descending` are the caller's own explicit choice and need no registry.
//
// **Disclosure at the door (Phase 525 / task 703.B).** Every ranked fact
// passes the `IFactDisclosureGate` at the `FactToolResult` surface before
// projection, and the gate re-resolves each id through the scope-filtered
// store — so a store implementation that leaked a cross-scope fact into a
// ranking is still caught at the door. What is different from 559 is the
// *shape* of the refusal, and deliberately so: 559 returns one typed
// marker per withheld fact, which at population scale would be up to
// `MaxTopK` markers — a subject-by-subject listing, i.e. a second
// disclosure channel wearing a refusal's clothes. Here withheld members
// fold into a **count grouped by policy ref**: existence disclosed,
// identity and value never (the 559.B posture at the scale a population
// forces).
//
// Ranks are the ranking's TRUE positions, so a withheld member leaves a
// visible gap rather than silently promoting the member below it. A
// contiguous renumbering would have the model report the third-best as
// the second-best — a correctness defect dressed as tidiness.
//
// **The value statistics are gated with the members (task 703.B).**
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
// **Freshness costs no extra read.** 559 walks each fact's supersession
// chain because it can return superseded facts; a population read cannot
// (there is deliberately no `IncludeSuperseded` — a ranking that mixes a
// value with the value that replaced it is not a population), so freshness
// derives from the head's transaction time through the same
// `Freshness.deriveAt` the store's own histogram uses. The per-member
// freshness and the summary histogram therefore agree by construction
// rather than by coincidence.
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

    let private jsonOptions = FableConverters.create ()

    let private serialize (value: obj) : string =
        JsonSerializer.Serialize(value, jsonOptions)

    // ── Tool definition (703.A / 703.C) ───────────────────────────

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
        ]
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
    }

    // ── Argument parsing + validation (703.A / 703.D) ──────────────

    type private PopulationArgs = {
        Metric: string
        Hierarchy: string
        Level: int option
        PathPrefix: string list option
        PeriodOverlaps: TemporalExtent option
        Threshold: ValueThreshold option
        Ordering: PopulationOrdering
        RequestedTopK: int
        AsOf: DateTime option
        Methods: PopulationMethodSelection
    }

    let private tryProperty (root: JsonElement) (name: string) : JsonElement option =
        match root.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
        | _ -> None

    let private stringArg (root: JsonElement) (name: string) : Result<string option, string> =
        match tryProperty root name with
        | None -> Ok None
        | Some v when v.ValueKind = JsonValueKind.String -> Ok(Some(v.GetString()))
        | Some _ -> Error(sprintf "Argument '%s' must be a string." name)

    let private numberArg (root: JsonElement) (name: string) : Result<decimal option, string> =
        match tryProperty root name with
        | None -> Ok None
        | Some v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetDecimal() with
            | true, d -> Ok(Some d)
            | _ -> Error(sprintf "Argument '%s' is not a representable number." name)
        | Some _ -> Error(sprintf "Argument '%s' must be a number." name)

    /// A whole-number argument. A fractional or out-of-range value is
    /// refused rather than truncated: silently reading 2.7 as 2 answers a
    /// question the caller did not ask.
    let private intArg (root: JsonElement) (name: string) : Result<int option, string> =
        match numberArg root name with
        | Error e -> Error e
        | Ok None -> Ok None
        | Ok(Some d) when Decimal.Truncate d <> d -> Error(sprintf "Argument '%s' must be a whole number." name)
        | Ok(Some d) when d < -1000000m || d > 1000000m ->
            Error(sprintf "Argument '%s' is out of range (expected a whole number within ±1,000,000)." name)
        | Ok(Some d) -> Ok(Some(int d))

    let private instantArg (root: JsonElement) (name: string) : Result<DateTime option, string> =
        match stringArg root name with
        | Error e -> Error e
        | Ok None -> Ok None
        | Ok(Some s) when String.IsNullOrWhiteSpace s -> Ok None
        | Ok(Some s) ->
            match
                DateTime.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            with
            | true, t -> Ok(Some t)
            | false, _ -> Error(sprintf "Argument '%s' is not a parseable ISO-8601 instant: '%s'." name s)

    /// A closed enum argument — an unrecognised value is refused with the
    /// accepted set enumerated, never silently defaulted.
    let private enumArg
        (root: JsonElement)
        (name: string)
        (cases: (string * 'a) list)
        (fallback: 'a)
        : Result<'a, string> =
        match stringArg root name with
        | Error e -> Error e
        | Ok None -> Ok fallback
        | Ok(Some s) when String.IsNullOrWhiteSpace s -> Ok fallback
        | Ok(Some s) ->
            match
                cases
                |> List.tryFind (fun (caseId, _) -> String.Equals(caseId, s.Trim(), StringComparison.OrdinalIgnoreCase))
            with
            | Some(_, value) -> Ok value
            | None ->
                Error(
                    sprintf
                        "Argument '%s' must be one of: %s (got '%s')."
                        name
                        (cases |> List.map fst |> String.concat ", ")
                        s
                )

    let private pathSegments (raw: string) : string list =
        raw.Split('>')
        |> Array.map _.Trim()
        |> Array.filter (fun seg -> seg <> "")
        |> Array.toList

    let private parseArgs (argsJson: string) : Result<PopulationArgs, string> =
        let parsed =
            try
                Ok((JsonDocument.Parse argsJson).RootElement.Clone())
            with _ ->
                Error "Arguments are not valid JSON."

        match parsed with
        | Error e -> Error e
        | Ok root when root.ValueKind <> JsonValueKind.Object -> Error "Arguments must be a JSON object."
        | Ok root ->
            let required name =
                match stringArg root name with
                | Ok(Some s) when not (String.IsNullOrWhiteSpace s) -> Ok s
                | Ok _ ->
                    Error(
                        sprintf
                            "Required argument '%s' is missing. Metric and subject-hierarchy ids come from the deployment's grounding registry."
                            name
                    )
                | Error e -> Error e

            let ordering =
                enumArg
                    root
                    "ordering"
                    [
                        "best_first", RegistryDirection
                        "ascending", Ascending
                        "descending", Descending
                    ]
                    RegistryDirection

            let methods =
                enumArg
                    root
                    "methods"
                    [ "canonical", CanonicalMethodOnly; "all_competing", AllCompetingMethods ]
                    CanonicalMethodOnly

            let period =
                match instantArg root "period_from", instantArg root "period_to" with
                | Error e, _
                | _, Error e -> Error e
                | Ok None, Ok None -> Ok None
                | Ok(Some f), Ok(Some t) when f >= t ->
                    Error "Argument 'period_from' must be strictly before 'period_to'."
                | Ok f, Ok t ->
                    Ok(
                        Some {
                            From = f |> Option.defaultValue DateTime.MinValue
                            To = t |> Option.defaultValue DateTime.MaxValue
                            Label = None
                        }
                    )

            let threshold =
                match numberArg root "value_at_least", numberArg root "value_at_most" with
                | Error e, _
                | _, Error e -> Error e
                | Ok None, Ok None -> Ok None
                | Ok(Some low), Ok(Some high) when low > high ->
                    Error "Argument 'value_at_least' must not exceed 'value_at_most'."
                | Ok(Some low), Ok(Some high) -> Ok(Some(Between(low, high)))
                | Ok(Some low), Ok None -> Ok(Some(AtLeast low))
                | Ok None, Ok(Some high) -> Ok(Some(AtMost high))

            let level =
                match intArg root "level" with
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
                        match intArg root "top_k", instantArg root "as_of", stringArg root "path_prefix" with
                        | Error e, _, _
                        | _, Error e, _
                        | _, _, Error e -> Error e
                        | Ok topK, Ok asOf, Ok prefix ->
                            Ok {
                                Metric = metric
                                Hierarchy = hierarchy
                                Level = level
                                PathPrefix = prefix |> Option.map pathSegments |> Option.filter (List.isEmpty >> not)
                                PeriodOverlaps = period
                                Threshold = threshold
                                Ordering = ordering
                                RequestedTopK = topK |> Option.defaultValue 10
                                AsOf = asOf
                                Methods = methods
                            }

    // ── Execution (703.A + 703.B) ─────────────────────────────────

    /// The executor over explicit dependencies — the testable core
    /// `execute` adapts `HttpContext` onto. `registry` supplies the
    /// metric's display format + staleness policy (Phase 519); the
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
        (scopeId: string)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            match parseArgs argsJson with
            | Error message -> return serialize {| error = message |}
            | Ok args ->
                let query: PopulationQuery = {
                    Metric = MetricRef args.Metric
                    Hierarchy = args.Hierarchy
                    Level = args.Level
                    PathPrefix = args.PathPrefix
                    PeriodOverlaps = args.PeriodOverlaps
                    Threshold = args.Threshold
                    Ordering = args.Ordering
                    TopK = args.RequestedTopK
                    AsOf = args.AsOf
                    Methods = args.Methods
                }

                let! outcome = store.QueryPopulation(scopeId, query)

                match outcome with
                // Phase 701's `Error` is a typed REFUSAL — an unregistered
                // metric, or one declared `Neutral` — never a failure. It
                // names its own remedy, so it reaches the model verbatim.
                | Error refusal -> return serialize {| error = refusal |}
                | Ok result ->
                    // Disclosure at the door (703.B): one gate check over
                    // the ranked ids at the FactToolResult surface. The
                    // gate audits each deny (GP 6) and re-resolves ids
                    // through the scope-filtered store.
                    let! verdicts = gate.Check(scopeId, principal, FactToolResult, result.Ranked |> List.map _.FactId)

                    // An id the gate returned no verdict for is denied,
                    // conservatively — the door never fails open.
                    let verdictFor (factId: string) =
                        verdicts
                        |> Map.tryFind factId
                        |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                    // True ranks: position in the store's ranking, kept
                    // through the gate so a withheld member leaves a gap
                    // rather than promoting the member below it.
                    let ranked =
                        result.Ranked |> List.mapi (fun i fact -> i + 1, fact, verdictFor fact.FactId)

                    let disclosable =
                        ranked |> List.filter (fun (_, _, verdict) -> verdict = FactDisclosable)

                    let withheld =
                        ranked |> List.filter (fun (_, _, verdict) -> verdict <> FactDisclosable)

                    // Withheld members at population scale: a COUNT grouped
                    // by policy ref. Never an id, never a subject, never a
                    // value — 10^5 per-member markers would be a listing of
                    // the restricted population, which is the thing the
                    // classification exists to prevent.
                    let withheldByPolicy =
                        withheld
                        |> List.map (fun (_, _, verdict) ->
                            match verdict with
                            | FactNotDisclosable policyRef -> policyRef
                            | FactDisclosable -> "unknown-fact" // unreachable — filtered above
                        )
                        |> List.countBy id
                        |> List.sortBy fst
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

                    let displayFormat =
                        metricDef |> Option.map _.DisplayFormat |> Option.defaultValue ""

                    let now = clock().ToUniversalTime()

                    // The same derivation the store's own freshness
                    // histogram runs (`Freshness.deriveAt … isCurrent =
                    // true`): a population read resolves current heads by
                    // construction, so no supersession-chain walk is owed
                    // — and the per-member freshness cannot disagree with
                    // the summary.
                    let projected =
                        disclosable
                        |> List.map (fun (rank, fact, _) ->
                            let freshness, staleSince =
                                match Freshness.deriveAt policy fact.AsOf true now with
                                | Fresh -> "Fresh", None
                                | Stale since -> "Stale", Some(since.ToUniversalTime().ToString "o")

                            {|
                                rank = rank
                                factId = fact.FactId
                                subject = SubjectRef.toString fact.Subject
                                metric = fact.Metric.Value
                                rendering = FactRendering.render displayFormat fact.Value
                                periodFrom = fact.Period.From.ToUniversalTime().ToString "o"
                                periodTo = fact.Period.To.ToUniversalTime().ToString "o"
                                periodLabel = fact.Period.Label
                                asOf = fact.AsOf.ToUniversalTime().ToString "o"
                                freshness = freshness
                                staleSince = staleSince
                                method = Fact.methodIdentity fact.Method
                            |})

                    let stats = result.Stats

                    // The magnitude block is gated with the members: a
                    // minimum or a maximum IS some member's value, so over
                    // a partly-restricted population it can round-trip
                    // exactly what the gate refused. Counts and coverage
                    // are existence-level and ride regardless.
                    let valuesWithheld = not (List.isEmpty withheld)

                    let renderStat (value: decimal option) =
                        if valuesWithheld then
                            None
                        else
                            value |> Option.map (fun d -> FactRendering.render displayFormat (Scalar d))

                    let directionLabel =
                        match result.Direction with
                        | HighestFirst -> "HighestFirst"
                        | LowestFirst -> "LowestFirst"

                    let withheldReason =
                        if valuesWithheld then
                            Some
                                "The population's minimum, maximum and mean are withheld because it contains members you are not permitted to see — a minimum or maximum is a member's own value. The counts, period coverage and freshness above describe the whole matched population."
                        else
                            None

                    return
                        serialize {|
                            metric = args.Metric
                            // Phase 705 — the metric family's declared
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
                            direction = directionLabel
                            requestedTopK = args.RequestedTopK
                            effectiveTopK = result.EffectiveTopK
                            topKCapped = args.RequestedTopK > result.EffectiveTopK
                            truncated = result.Truncated
                            ranked = projected
                            withheldCount = List.length withheld
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
                                // Phase 705 — how the population was
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

            executeWith store gate registry (fun () -> DateTime.UtcNow) (scopeIdOf ctx) (userIdOf ctx) argsJson
        | _ -> async {
            return
                serialize {|
                    error =
                        "The fact store is not composed in this deployment — `query_metric_population` is unavailable."
                |}
          }