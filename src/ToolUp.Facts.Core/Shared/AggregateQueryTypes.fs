// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System

// ─── Aggregate query ─────────────────────────────────────────────────
//
// A roll-up the answer verifier can check. The metric registry already says
// how each metric combines (`Grounding.RollUp`): `Additive tolerance` means a
// parent is the sum of its children, `NonAdditive` (or no declaration) means
// it is not. An aggregate reads one population and sums it by group, and only
// where the declaration says summing is meaningful.
//
// **What a group is.** The request names a subject hierarchy and one of its
// declared LEVELS. The members are the population's subjects AT that level,
// and each member's group is its own segment at that level: every other
// segment above it is collapsed. Under `[ "site"; "category" ]`, grouping by
// `category` sums each category across every site.
//
// **What a total is never.** A sum over a group with a withheld member is a
// different number wearing the same label, so such a group is reported
// withheld, with its member count, and never as the sum of the disclosed
// remainder. A group whose members cover different periods, or name one
// subject twice (two competing methods), is refused: neither has one total.
// A population larger than the read's ceiling, or holding members with no
// single magnitude, has no decidable total at all, so the whole request is
// refused rather than partly summed.
//
// Fable-safe: records and DUs over primitives and `FactTypes`.

/// The roll-up request, as the `aggregate_metric` tool reads it.
type AggregateQuery = {
    /// The registered metric to sum. Its declared roll-up must be
    /// `Additive`.
    Metric: MetricRef
    /// The registered subject hierarchy the population is drawn from.
    Hierarchy: string
    /// One of the hierarchy's declared level labels: the members are the
    /// subjects at this level, grouped by their segment at it.
    GroupByLevel: string
    /// Optional subtree filter: ordered member ids from the hierarchy root.
    PathPrefix: string list option
    /// Optional valid-time window the members' periods must overlap.
    PeriodOverlaps: TemporalExtent option
    /// Optional transaction time: sum the heads current then.
    AsOf: DateTime option
}

/// The population read's ceiling, mirrored: how many members the aggregate
/// asked for, how many the store applied, and whether any were dropped. A
/// dropped member would make every total partial, so `Truncated` is only
/// ever `true` on the refusal that says so.
type AggregateCeiling = {
    RequestedTopK: int
    EffectiveTopK: int
    Truncated: bool
}

/// One fact a total was built from: one the disclosure gate admitted.
type AggregateInput = {
    FactId: string
    Subject: SubjectRef
    /// The value's canonical rendering under the metric's display format.
    Rendering: string
}

/// A group's total and the facts it was summed from.
type AggregateTotal = {
    /// The group's segment at the requested level.
    Group: string
    /// The total's canonical rendering under the metric's display format.
    Rendering: string
    /// The total, unrounded.
    Value: decimal
    /// The one period every input covers.
    Period: TemporalExtent
    /// How many members the group holds; equal to `Inputs`' length.
    MemberCount: int
    /// Every input, in subject order.
    Inputs: AggregateInput list
}

/// Why one group has no total although nothing in it was withheld.
type AggregateGroupRefusal =
    /// The members cover more than one period, so their sum describes no
    /// period at all.
    | PeriodsDisagree of periods: int
    /// One subject holds more than one value for the period: competing
    /// methods with no canonical one declared. Summing both counts the
    /// subject twice.
    | CompetingMethods of subject: string

/// One group of an aggregate.
type AggregateGroup =
    /// Every member was disclosed and the members agree: the total.
    | GroupTotal of AggregateTotal
    /// At least one member was withheld and at least one disclosed: the
    /// group's label (which the disclosed members already carry) and its
    /// member count, never a sum.
    | GroupWithheld of group: string * memberCount: int
    /// Every member was disclosed, and the group still has no single total.
    | GroupRefused of group: string * memberCount: int * reason: AggregateGroupRefusal

/// The answer to an aggregate request.
type AggregateResult = {
    Metric: string
    Hierarchy: string
    GroupByLevel: string
    /// Totals first, largest first; then withheld and refused groups by
    /// label.
    Groups: AggregateGroup list
    /// Groups whose every member was withheld: counted, never named, since
    /// the label itself is part of a withheld subject's identity.
    HiddenGroupCount: int
    /// Every withheld member, as a count grouped by policy ref.
    WithheldByPolicy: (string * int) list
    Ceiling: AggregateCeiling
}

/// Why an aggregate request has no answer.
type AggregateRefusal =
    /// No metric registry is composed, so no roll-up can be read.
    | NoRegistry
    /// The metric is not registered.
    | UnknownMetric of metric: string
    /// The metric's declared roll-up is not `Additive`: `declared` is
    /// `"NonAdditive"` or `"undeclared"`.
    | NotAdditive of metric: string * declared: string
    /// The hierarchy is not registered.
    | UnknownHierarchy of hierarchy: string
    /// The level is not one of the hierarchy's declared levels.
    | UnknownLevel of level: string * declared: string list
    /// The population matched more members than the read returns, so every
    /// total would be partial.
    | PopulationTruncated of ceiling: AggregateCeiling * comparable: int
    /// Members with no single magnitude (an absence, an interval, a series)
    /// sit somewhere in the population, and a read cannot say in which group.
    | NonComparableMembers of count: int
    /// The store refused the population read; its words, verbatim.
    | StoreRefused of reason: string

module AggregateRefusal =

    /// The refusal as the model reads it: the reason and the remedy.
    let describe (refusal: AggregateRefusal) : string =
        match refusal with
        | NoRegistry ->
            "No metric registry is composed in this deployment, so no metric's roll-up is declared and nothing can be summed."
        | UnknownMetric metric ->
            sprintf
                "Metric '%s' is not registered. Use a metric id from `list_metric_coverage`; a total is computed only for a registered metric that declares how it rolls up."
                metric
        | NotAdditive(metric, declared) ->
            sprintf
                "Metric '%s' declares its roll-up as %s, so its values do not add up across subjects and no total is computed. Report the members' values individually (query_metric_population) rather than summing them."
                metric
                declared
        | UnknownHierarchy hierarchy ->
            sprintf
                "Subject hierarchy '%s' is not registered. Use a hierarchy id from `list_metric_coverage`."
                hierarchy
        | UnknownLevel(level, declared) ->
            sprintf
                "'%s' is not a level of this hierarchy. Its levels, root to leaf, are: %s."
                level
                (String.Join(", ", declared))
        | PopulationTruncated(ceiling, comparable) ->
            sprintf
                "The population holds %d members and one read returns at most %d, so every total would leave members out. Narrow it with `path_prefix` or a period window and ask again."
                comparable
                ceiling.EffectiveTopK
        | NonComparableMembers count ->
            sprintf
                "%d member(s) of this population carry no single value (a recorded absence, a range or a series), so no group's total is decidable. Narrow the population with `path_prefix` or a period window."
                count
        | StoreRefused reason -> reason

    /// The group refusal's reason, as the model reads it.
    let describeGroup (refusal: AggregateGroupRefusal) : string =
        match refusal with
        | PeriodsDisagree periods ->
            sprintf
                "The members cover %d different periods, so their sum describes no one period. Ask with a period window."
                periods
        | CompetingMethods subject ->
            sprintf
                "Subject %s holds more than one value for the period (competing methods and no canonical one declared), so summing would count it twice."
                subject