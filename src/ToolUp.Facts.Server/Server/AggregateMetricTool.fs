// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── AggregateMetricTool ─────────────────────────────────────────────
//
// The ServerResident `aggregate_metric` AI tool: a total computed by the
// server from facts the gate admitted, so the model never adds numbers up
// itself. Asked for "the top categories across two sites", a model with only
// `query_metric_population` ranks each site and sums the pairs in its head,
// and the figures it prints are nobody's facts. This tool reads the
// population once, groups the members at the requested level and sums each
// group, but only for a metric whose registry declaration says its values add
// up (`Grounding.RollUp.Additive`).
//
// **Every total names its inputs.** Each group's total carries the canonical
// rendering of every fact it was summed from, beside its id, so the numeric
// answer gate can verify a quoted total against facts it holds and the audit
// row reaches those facts, never this tool.
//
// **Disclosure first.** Every member passes the `IFactDisclosureGate` at the
// `FactToolResult` surface before anything is summed. A group with a withheld
// member is reported withheld with its member count; a group whose every
// member is withheld is counted and never named; withheld members are also
// counted by policy ref. Nothing below the gate reaches the result.
//
// **Refused rather than approximated.** A metric declared `NonAdditive` or
// with no roll-up, an unknown hierarchy or level, a population larger than
// one read returns, members with no single magnitude, a group whose members
// cover different periods, and a subject holding two competing values each
// get a typed refusal naming why (`AggregateRefusal`,
// `AggregateGroupRefusal`). A partial sum is a different number wearing the
// same label.

/// The `aggregate_metric` AI tool: definition + executor over the composed
/// `IFactStore.QueryPopulation`, `IFactDisclosureGate` and metric registry.
/// Declared by `FactsCompose.withFactStore`; consumers normally never call
/// this module directly.
module AggregateMetricTool =

    /// The `aggregate_metric` tool declaration.
    let definition: AIToolDefinition = {
        Name = "aggregate_metric"
        Description =
            "Sum ONE verified metric over groups of subjects — the server adds the values up, so you never add figures yourself. Use it whenever an answer needs a total across subjects: \"revenue by category across both sites\", \"total units per region\". Name the subject hierarchy and the LEVEL to group by (a level label from `list_metric_coverage`'s hierarchy levels, e.g. \"category\"): the members are the subjects at that level, and each is grouped by its own segment at that level, every segment above it collapsed — so grouping by category sums each category across every site. Narrow with `path_prefix` (e.g. \"site-a\" for one site) and a period window. Each total carries its canonical rendering (quote it exactly — do not recompute or reformat), how many members it was summed from, the period it covers, and every input fact with its id and rendering. Only a metric whose registry declares it ADDITIVE is summed; a ratio, an average, a share or an undeclared metric is refused by name, so report its members individually instead. A group containing any member you are not permitted to see is reported as withheld with its member count, never as the sum of the rest; a group whose members cover different periods, or where one subject holds two competing values, is refused with the reason. A population larger than one read can return is refused as a whole: narrow it and ask again. Results are scoped to the current user/team."
        Parameters = [
            {
                Name = "metric"
                Type = "string"
                Description =
                    "Registered metric id to sum (e.g. \"revenue\"). Its registry declaration must say it is additive."
                Required = true
                Default = None
            }
            {
                Name = "subject_hierarchy"
                Type = "string"
                Description = "Stable id of the registered subject hierarchy (e.g. \"store\")."
                Required = true
                Default = None
            }
            {
                Name = "group_by_level"
                Type = "string"
                Description =
                    "One of the hierarchy's declared level labels (e.g. \"category\"). The members are the subjects at this level; each is grouped by its segment at it, the segments above collapsed."
                Required = true
                Default = None
            }
            {
                Name = "path_prefix"
                Type = "string"
                Description =
                    "Optional subtree filter — ordered member ids from the hierarchy root separated by \">\" (e.g. \"site-a\"). Omit to sum across the whole hierarchy."
                Required = false
                Default = None
            }
            {
                Name = "period_from"
                Type = "string"
                Description =
                    "Optional ISO-8601 UTC instant — admit only facts whose valid-time period overlaps [period_from, period_to). Either bound may be given alone."
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
                Name = "as_of"
                Type = "string"
                Description =
                    "Optional ISO-8601 UTC instant — sum the values that were current at this transaction time. Omit for the current view."
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
        Effects = ToolEffectDeclaration.readFacts
    }

    // ── Argument parsing ──────────────────────────────────────────

    let private pathSegments (raw: string) : string list =
        raw.Split('>')
        |> Array.map _.Trim()
        |> Array.filter (fun seg -> seg <> "")
        |> Array.toList

    let private parseArgs (argsJson: string) : Result<AggregateQuery, string> =
        match FactTool.parseObject argsJson with
        | Error e -> Error e
        | Ok root ->
            let required =
                FactTool.requiredArg
                    "Metric and subject-hierarchy ids, and the hierarchy's level labels, come from `list_metric_coverage`."
                    root

            let period =
                match FactTool.instantArg root "period_from", FactTool.instantArg root "period_to" with
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

            match required "metric", required "subject_hierarchy", required "group_by_level" with
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
            | Ok metric, Ok hierarchy, Ok level ->
                match period, FactTool.instantArg root "as_of", FactTool.stringArg root "path_prefix" with
                | Error e, _, _
                | _, Error e, _
                | _, _, Error e -> Error e
                | Ok period, Ok asOf, Ok prefix ->
                    Ok {
                        Metric = MetricRef metric
                        Hierarchy = hierarchy
                        GroupByLevel = level.Trim()
                        PathPrefix = prefix |> Option.map pathSegments |> Option.filter (List.isEmpty >> not)
                        PeriodOverlaps = period
                        AsOf = asOf
                    }

    // ── The roll-up ───────────────────────────────────────────────

    /// What the registry declares for a request: the metric's display format
    /// and the level's index, or the refusal naming what is missing.
    let resolve
        (registry: Grounding.IMetricRegistry option)
        (query: AggregateQuery)
        : Result<Grounding.MetricDefinition * int, AggregateRefusal> =
        match registry with
        | None -> Error NoRegistry
        | Some registry ->
            match registry.TryGetMetric query.Metric.Value with
            | None -> Error(UnknownMetric query.Metric.Value)
            | Some metric ->
                match metric.RollUp with
                | None -> Error(NotAdditive(metric.Id, "undeclared"))
                | Some Grounding.NonAdditive -> Error(NotAdditive(metric.Id, "NonAdditive"))
                | Some(Grounding.Additive _) ->
                    match registry.TryGetSubject query.Hierarchy with
                    | None -> Error(UnknownHierarchy query.Hierarchy)
                    | Some subject ->
                        match subject.Levels |> List.tryFindIndex ((=) query.GroupByLevel) with
                        | None -> Error(UnknownLevel(query.GroupByLevel, subject.Levels))
                        | Some index -> Ok(metric, index)

    /// The population read an aggregate asks for: every member at the
    /// level, as many as one read returns, each subject's canonical head.
    let populationQuery (query: AggregateQuery) (levelIndex: int) : PopulationQuery = {
        Metric = query.Metric
        Hierarchy = query.Hierarchy
        Level = Some(levelIndex + 1)
        PathPrefix = query.PathPrefix
        PeriodOverlaps = query.PeriodOverlaps
        Threshold = None
        Ordering = Descending
        TopK = PopulationQuery.MaxTopK
        AsOf = query.AsOf
        Methods = CanonicalMethodOnly
    }

    /// Group and sum a population the gate has judged. Pure: `verdictFor`
    /// is total (the caller supplies the conservative deny for an id the
    /// gate returned nothing for), and `render` is the metric's display
    /// format. Members are grouped by their segment at `levelIndex`; the
    /// population read returned only subjects at that level.
    let rollUp
        (query: AggregateQuery)
        (levelIndex: int)
        (render: decimal -> string)
        (verdictFor: string -> FactDisclosureVerdict)
        (population: PopulationResult)
        : Result<AggregateResult, AggregateRefusal> =
        let ceiling = {
            RequestedTopK = PopulationQuery.MaxTopK
            EffectiveTopK = population.EffectiveTopK
            Truncated = population.Truncated
        }

        // The read's contract is that only members with a single magnitude
        // are ranked; a member without one is counted and never placed. Each
        // ranked member is paired with its magnitude here, once, so nothing
        // below has a shape to fall back on.
        let measured =
            population.Ranked
            |> List.choose (fun fact -> PopulationValue.comparable fact.Value |> Option.map (fun d -> fact, d))

        let unmeasured =
            population.Stats.NonComparableCount
            + (population.Ranked.Length - measured.Length)

        if population.Truncated then
            Error(PopulationTruncated(ceiling, population.Stats.ComparableCount))
        elif unmeasured > 0 then
            Error(NonComparableMembers unmeasured)
        else
            let segmentOf (fact: Fact) =
                fact.Subject.Path |> List.tryItem levelIndex |> Option.defaultValue ""

            let judged =
                measured
                |> List.map (fun (fact, magnitude) -> fact, magnitude, verdictFor fact.FactId)

            let withheldPolicies =
                judged
                |> List.choose (fun (_, _, verdict) ->
                    match verdict with
                    | FactDisclosable -> None
                    | FactNotDisclosable policyRef -> Some policyRef)

            let groups =
                judged
                |> List.groupBy (fun (fact, _, _) -> segmentOf fact)
                |> List.map (fun (group, members) ->
                    let disclosed =
                        members
                        |> List.choose (fun (fact, magnitude, verdict) ->
                            match verdict with
                            | FactDisclosable -> Some(fact, magnitude)
                            | FactNotDisclosable _ -> None)

                    let count = List.length members

                    if List.isEmpty disclosed then
                        None
                    elif disclosed.Length < count then
                        Some(GroupWithheld(group, count))
                    else
                        let periods =
                            disclosed
                            |> List.map (fun (f, _) -> f.Period.From, f.Period.To)
                            |> List.distinct

                        let repeated =
                            disclosed
                            |> List.countBy (fun (f, _) -> f.Subject)
                            |> List.tryFind (fun (_, n) -> n > 1)

                        match periods, repeated with
                        | _ :: _ :: _, _ -> Some(GroupRefused(group, count, PeriodsDisagree periods.Length))
                        | _, Some(subject, _) ->
                            Some(GroupRefused(group, count, CompetingMethods(SubjectRef.toString subject)))
                        | _ ->
                            let inputs = disclosed |> List.sortBy (fun (f, _) -> SubjectRef.toString f.Subject)

                            let total = inputs |> List.sumBy snd

                            Some(
                                GroupTotal {
                                    Group = group
                                    Rendering = render total
                                    Value = total
                                    Period = (fst inputs.Head).Period
                                    MemberCount = count
                                    Inputs =
                                        inputs
                                        |> List.map (fun (f, magnitude) -> {
                                            FactId = f.FactId
                                            Subject = f.Subject
                                            Rendering = render magnitude
                                        })
                                }
                            ))

            let shown = groups |> List.choose id

            let totals =
                shown
                |> List.choose (function
                    | GroupTotal t -> Some t
                    | _ -> None)
                |> List.sortBy (fun t -> -t.Value, t.Group)
                |> List.map GroupTotal

            let others =
                shown
                |> List.filter (function
                    | GroupTotal _ -> false
                    | _ -> true)
                |> List.sortBy (function
                    | GroupWithheld(group, _)
                    | GroupRefused(group, _, _) -> group
                    | GroupTotal t -> t.Group)

            Ok {
                Metric = query.Metric.Value
                Hierarchy = query.Hierarchy
                GroupByLevel = query.GroupByLevel
                Groups = totals @ others
                HiddenGroupCount = groups |> List.filter Option.isNone |> List.length
                WithheldByPolicy = withheldPolicies |> List.countBy id |> List.sortBy fst
                Ceiling = ceiling
            }

    // ── Execution ─────────────────────────────────────────────────

    let private periodOf (period: TemporalExtent) = {|
        periodFrom = period.From.ToUniversalTime().ToString "o"
        periodTo = period.To.ToUniversalTime().ToString "o"
        periodLabel = period.Label
    |}

    /// The result as the model reads it. Each total carries `metric`,
    /// `rendering` and `inputFactIds`, and each input `factId`, `metric` and
    /// `rendering`: the shapes the numeric answer gate reads a fact tool's
    /// result by.
    let private project (metricDef: Grounding.MetricDefinition) (result: AggregateResult) : obj =
        let totals =
            result.Groups
            |> List.choose (function
                | GroupTotal t ->
                    let period = periodOf t.Period

                    Some {|
                        group = t.Group
                        metric = result.Metric
                        rendering = t.Rendering
                        memberCount = t.MemberCount
                        periodFrom = period.periodFrom
                        periodTo = period.periodTo
                        periodLabel = period.periodLabel
                        inputFactIds = t.Inputs |> List.map _.FactId
                        inputs =
                            t.Inputs
                            |> List.map (fun i -> {|
                                factId = i.FactId
                                subject = SubjectRef.toString i.Subject
                                metric = result.Metric
                                rendering = i.Rendering
                            |})
                    |}
                | _ -> None)

        let withheld =
            result.Groups
            |> List.choose (function
                | GroupWithheld(group, count) ->
                    Some {|
                        group = group
                        memberCount = count
                        status =
                            "Withheld: this group contains members you are not permitted to see, so no total is reported for it."
                    |}
                | _ -> None)

        let refused =
            result.Groups
            |> List.choose (function
                | GroupRefused(group, count, reason) ->
                    Some {|
                        group = group
                        memberCount = count
                        reason = AggregateRefusal.describeGroup reason
                    |}
                | _ -> None)

        box {|
            metric = result.Metric
            metricContext = metricDef.Context
            hierarchy = result.Hierarchy
            groupByLevel = result.GroupByLevel
            requestedTopK = result.Ceiling.RequestedTopK
            effectiveTopK = result.Ceiling.EffectiveTopK
            truncated = result.Ceiling.Truncated
            totals = totals
            withheld = withheld
            refused = refused
            hiddenGroupCount = result.HiddenGroupCount
            withheldByPolicy =
                result.WithheldByPolicy
                |> List.map (fun (policyRef, count) -> {|
                    policyRef = policyRef
                    count = count
                    status = FactDisclosureVerdict.refusalText policyRef
                |})
        |}

    /// The executor over explicit dependencies, the testable core `execute`
    /// adapts `HttpContext` onto. Every member passes `gate` at the
    /// `FactToolResult` surface before anything is summed.
    let executeWith
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        let refuse refusal =
            FactTool.serialize {|
                error = AggregateRefusal.describe refusal
            |}

        async {
            match parseArgs argsJson with
            | Error message -> return FactTool.serialize {| error = message |}
            | Ok query ->
                match resolve registry query with
                | Error refusal -> return refuse refusal
                | Ok(metricDef, levelIndex) ->
                    match! store.QueryPopulation(scope, populationQuery query levelIndex) with
                    | Error reason -> return refuse (StoreRefused reason)
                    | Ok population ->
                        let! verdicts =
                            gate.Check(scope, principal, FactToolResult, population.Ranked |> List.map _.FactId)

                        // An id the gate returned no verdict for is denied:
                        // the door never fails open.
                        let verdictFor (factId: string) =
                            verdicts
                            |> Map.tryFind factId
                            |> Option.defaultValue (FactNotDisclosable "unknown-fact")

                        let render (value: decimal) =
                            FactRendering.render metricDef.DisplayFormat (Scalar value)

                        match rollUp query levelIndex render verdictFor population with
                        | Error refusal -> return refuse refusal
                        | Ok result -> return FactTool.serialize (project metricDef result)
        }

    // ── HttpContext adapter ───────────────────────────────────────

    /// The registered `HttpContext` executor.
    let execute (ctx: HttpContext) (argsJson: string) : Async<string> =
        FactTool.define
            "aggregate_metric"
            (fun c argsJson -> executeWith c.Store c.Gate c.Registry c.Scope c.UserId argsJson)
            ctx
            argsJson