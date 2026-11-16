// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Grounding

open System
open Fuaran.Facts

// ─── Metric & subject registry ───────────────────────────────────────
//
// **The definitions are the published Fact kernel's.** A metric is a
// `Fuaran.Facts.MetricDefinition` and a subject hierarchy a
// `Fuaran.Facts.SubjectDefinition` — the package's types, checked by its
// `Definition.checkMetric` / `checkSubject`, digested and written by its
// codec, rendered by its standard. This namespace keeps what is forge's:
// the compose-time registry, its provenance and its duplicate rejection.
//
// **Namespace `ToolUp.Platform.Grounding`** — deliberately *not* the root
// `ToolUp.Platform` namespace, so nothing here collides with the
// observability `ToolUp.Platform.Metrics.MetricDefinition` (a Prometheus
// signal). That type describes a *telemetry* signal; a grounding metric is
// a *business quantity* (a KPI: elasticity, share-of-voice, MRR). The two
// never interact; a module may declare both, neither, or either.
//
// Modules declare the *quantities they compute* (metrics) and the
// *entity hierarchies they compute them over* (subjects) exactly the way
// they declare `DataType`s — a compose-time fan-in with duplicate-id
// rejection.
//
// **Optional; zero-weight when unused (GP 13).** A module that declares
// no metrics or subjects composes and runs byte-for-byte identically; the
// registry is empty and every read surface returns nothing.

/// Resolution for an optional `MetricDefinition.RecomputePolicy`
/// declaration — the undeclared metric (`None`) is `Manual`: nothing
/// recomputes unbidden (GP 11).
[<RequireQualifiedAccess>]
module RecomputePolicy =

    /// The effective policy for an optional declaration — `None` ⇒
    /// `Manual`.
    let resolve (declared: RecomputePolicy option) : RecomputePolicy = declared |> Option.defaultValue Manual

/// Resolution for an optional `MetricDefinition.RollUp` declaration.
[<RequireQualifiedAccess>]
module RollUp =

    /// The additive summation tolerance a metric's roll-up declaration
    /// carries, or `None` when the metric is `NonAdditive` or undeclared —
    /// the single signal the coherence check gates comparability on. An
    /// undeclared metric (`None`) is treated as non-additive: it is excluded
    /// from coherence checking (GP 11 / GP 13).
    let additiveTolerance (declared: RollUp option) : DecimalText option =
        match declared with
        | Some(Additive tolerance) -> Some tolerance
        | Some NonAdditive
        | None -> None

/// One registered metric paired with the module that declared it — the
/// provenance the registry keeps so `MetricsByModule` and the
/// duplicate-diagnostic can name the producer.
type MetricRegistration = {
    /// The `ServerModule.Name` that declared this metric.
    Module: string
    Definition: MetricDefinition
}

/// One registered subject paired with the module that declared it.
type SubjectRegistration = {
    /// The `ServerModule.Name` that declared this subject.
    Module: string
    Definition: SubjectDefinition
}

/// What a registered subject hierarchy MEANS, beside the declaration it
/// describes: a description of the subject and of any of its levels, keyed
/// by subject id. A record of its own rather than a field on
/// `SubjectDefinition`, so every existing full-literal declaration compiles
/// unchanged. Registered by `ServerModule.describeSubjects`; a description
/// naming an undeclared subject or level is refused at compose, naming
/// both. Reported by `list_metric_coverage` beside the subject's levels.
///
/// **Prose about the SUBJECT, never a value.** A description is
/// interpretation ("a site is a trading outlet; a market groups sites; a
/// country groups markets"), not data. A figure belongs in a fact, where it
/// is bitemporal, superseded when it changes and disclosure-classified; a
/// value copied here is a claim nothing can supersede, on a compose-time
/// declaration the disclosure gate never sees.
///
/// Generic substrate (GP 1): opaque text the declaring module supplies;
/// nothing here parses or interprets it.
type SubjectDescription = {
    /// The described subject's `SubjectDefinition.Id`.
    SubjectId: string
    /// What the hierarchy is and how its levels nest.
    Description: string
    /// Per-level descriptions as `(level, description)`, each naming one of
    /// the subject's `Levels`. A level left out is undescribed.
    Levels: (string * string) list
}

/// One subject description paired with the module that registered it — the
/// provenance the compose-time refusal names.
type SubjectDescriptionRegistration = {
    /// The `ServerModule.Name` that registered the description.
    Module: string
    /// The registered description.
    Description: SubjectDescription
}

/// The optional capability of a metric registry that carries declared
/// subject descriptions (`ServerModule.describeSubjects`). A capability
/// beside `IMetricRegistry` rather than a member on it, so no external
/// implementation breaks: a reader probes for it
/// (`MetricRegistry.trySubjectDescription`), and a registry without it
/// simply has no descriptions. The composed registry implements it.
type IDescribesSubjects =
    /// The description registered for a subject; `None` when the subject is
    /// undescribed or unregistered.
    abstract TryGetSubjectDescription: subjectId: string -> SubjectDescription option

/// The optional capability of a metric registry that keeps which module
/// declared each metric and subject. A capability beside `IMetricRegistry`
/// rather than a member on it, so no external implementation breaks: a
/// reader probes for it (`MetricRegistry.tryRegistrations`). The registry
/// answers `MetricsByModule` for a module it is told, and nothing at all for
/// a subject; this is the enumeration a reader needs to group by module
/// without knowing the module names. The composed registry implements it.
type IAttributesDeclarations =
    /// Every metric with its declaring module, in declaration order, one
    /// entry per `(module, id)`.
    abstract MetricRegistrations: MetricRegistration list
    /// Every subject hierarchy with its declaring module, in declaration
    /// order, one entry per `(module, id)`.
    abstract SubjectRegistrations: SubjectRegistration list

/// Read surface over the composed metric & subject registry, resolved
/// from DI by server-side consumers (fact store, planner, discovery
/// tooling). A compose-time immutable projection — every lookup is a
/// pure in-memory read, so the interface is synchronous (it is not a
/// distributed store and carries no GP 12 obligation). An empty registry
/// (no module declared anything) answers every lookup with nothing.
type IMetricRegistry =
    /// Every registered metric, in declaration order, deduplicated to one
    /// entry per `Id` (duplicate ids are rejected at compose, so this is
    /// already unique when the registry built successfully).
    abstract Metrics: MetricDefinition list
    /// Every registered subject hierarchy, in declaration order.
    abstract Subjects: SubjectDefinition list
    /// A metric by its stable `Id`; `None` when unregistered.
    abstract TryGetMetric: metricId: string -> MetricDefinition option
    /// A subject hierarchy by its stable `Id`; `None` when unregistered.
    abstract TryGetSubject: subjectId: string -> SubjectDefinition option
    /// Every metric declared by the named module (empty when the module
    /// declared none or is unknown).
    abstract MetricsByModule: moduleName: string -> MetricDefinition list
    /// Every metric whose `ProducingOperation` is the given operation id
    /// — the reverse index the gap planner walks (operation → metrics it
    /// produces). Empty when no metric links to that operation.
    abstract MetricsByOperation: operationId: string -> MetricDefinition list

/// Construction + duplicate-rejection for the composed registry. The
/// fan-in (`ServerApp.addModule`) accumulates `(module, definition)`
/// registrations; `build` folds them into an `IMetricRegistry`, rejecting
/// a duplicate metric or subject `Id` declared by two modules with a
/// diagnostic that names both.
module MetricRegistry =

    /// The immutable registry implementation. Private — consumers hold
    /// the `IMetricRegistry` interface; construction is through `build`
    /// (or `tryBuild`) so the duplicate check is never bypassed.
    type private Registry
        (metrics: MetricRegistration list, subjects: SubjectRegistration list, descriptions: SubjectDescription list) =
        // Deduplicated declaration-order projections. `build` has already
        // proven ids are unique, so `distinctBy` only guards against a
        // module declaring the SAME id twice itself (idempotent), which
        // is not a cross-module conflict and is silently collapsed.
        let metricDefs = metrics |> List.map _.Definition |> List.distinctBy _.Id

        let subjectDefs = subjects |> List.map _.Definition |> List.distinctBy _.Id

        let metricById = metricDefs |> List.map (fun d -> d.Id, d) |> Map.ofList

        let subjectById = subjectDefs |> List.map (fun d -> d.Id, d) |> Map.ofList

        let descriptionById =
            descriptions |> List.map (fun d -> d.SubjectId, d) |> Map.ofList

        interface IDescribesSubjects with
            member _.TryGetSubjectDescription subjectId =
                descriptionById |> Map.tryFind subjectId

        interface IAttributesDeclarations with
            member _.MetricRegistrations =
                metrics |> List.distinctBy (fun r -> r.Module, r.Definition.Id)

            member _.SubjectRegistrations =
                subjects |> List.distinctBy (fun r -> r.Module, r.Definition.Id)

        interface IMetricRegistry with
            member _.Metrics = metricDefs
            member _.Subjects = subjectDefs
            member _.TryGetMetric metricId = metricById |> Map.tryFind metricId
            member _.TryGetSubject subjectId = subjectById |> Map.tryFind subjectId

            member _.MetricsByModule moduleName =
                metrics
                |> List.filter (fun r -> r.Module = moduleName)
                |> List.map _.Definition
                |> List.distinctBy _.Id

            member _.MetricsByOperation operationId =
                metricDefs |> List.filter (fun d -> d.ProducingOperation = Some operationId)

    let private quoted (values: string list) : string =
        match values |> List.distinct with
        | [] -> "none"
        | vs -> vs |> List.map (sprintf "'%s'") |> String.concat ", "

    /// Every reason a subject description cannot stand beside the
    /// declarations, each naming the describing module, the subject and
    /// (where it applies) the level: a subject no module declares, a level
    /// the subject does not carry, a subject described twice, a level
    /// described twice.
    let private descriptionProblems
        (subjects: SubjectRegistration list)
        (descriptions: SubjectDescriptionRegistration list)
        : string list =
        let declared =
            subjects
            |> List.distinctBy _.Definition.Id
            |> List.map (fun r -> r.Definition.Id, r.Definition)
            |> Map.ofList

        let twice =
            descriptions
            |> List.groupBy _.Description.SubjectId
            |> List.choose (fun (subjectId, group) ->
                if List.length group > 1 then
                    Some(
                        sprintf
                            "Subject '%s' is described more than once (by modules %s). Describe a subject once."
                            subjectId
                            (quoted (group |> List.map _.Module))
                    )
                else
                    None)

        let perDescription =
            descriptions
            |> List.collect (fun r ->
                let d = r.Description

                match declared |> Map.tryFind d.SubjectId with
                | None -> [
                    sprintf
                        "Module '%s' describes subject '%s', which no module declares. Declared subjects: %s."
                        r.Module
                        d.SubjectId
                        (quoted (declared |> Map.toList |> List.map fst))
                  ]
                | Some subject -> [
                    for level, _ in d.Levels do
                        if not (List.contains level subject.Levels) then
                            sprintf
                                "Module '%s' describes level '%s' of subject '%s', which the subject does not declare. Its levels: %s."
                                r.Module
                                level
                                d.SubjectId
                                (quoted subject.Levels)
                    for level, count in d.Levels |> List.countBy fst do
                        if count > 1 then
                            sprintf
                                "Module '%s' describes level '%s' of subject '%s' %d times. Describe a level once."
                                r.Module
                                level
                                d.SubjectId
                                count
                  ])

        twice @ perDescription

    /// Every declared definition the kernel's own checks refuse
    /// (`Definition.checkMetric` / `checkSubject`: a malformed id, an empty
    /// name, a currency format over a unit that is not one currency, a
    /// negative roll-up tolerance), each naming the declaring module, the
    /// definition and the refusal.
    let private definitionProblems
        (metrics: MetricRegistration list)
        (subjects: SubjectRegistration list)
        : string list =
        let describe kind (r: string) id (refusal: Refusal) =
            sprintf
                "Module '%s' declares %s '%s', which the Fact kernel refuses: %s"
                r
                kind
                id
                (Refusal.describe refusal)

        [
            for r in metrics do
                match Definition.checkMetric r.Definition with
                | Error refusal -> describe "metric" r.Module r.Definition.Id refusal
                | Ok() -> ()
            for r in subjects do
                match Definition.checkSubject r.Definition with
                | Error refusal -> describe "subject" r.Module r.Definition.Id refusal
                | Ok() -> ()
        ]

    /// Fold accumulated registrations and subject descriptions into an
    /// `IMetricRegistry` that also carries `IDescribesSubjects`, or a
    /// diagnostic naming both modules when a metric or subject id is
    /// declared by two different modules, or every reason a description is
    /// refused (a subject or level no module declares, a subject or level
    /// described twice), each naming the module, the subject and the level.
    /// A single module re-declaring the same id is not a conflict
    /// (idempotent — collapsed). The diagnostic shape matches the SDK's
    /// duplicate-registration idiom.
    let tryBuildDescribed
        (metrics: MetricRegistration list)
        (subjects: SubjectRegistration list)
        (descriptions: SubjectDescriptionRegistration list)
        : Result<IMetricRegistry, string> =
        // Group by id; a conflict is an id whose registrations span >1
        // distinct module.
        let metricConflict =
            metrics
            |> List.groupBy (fun r -> r.Definition.Id)
            |> List.tryPick (fun (id, group) ->
                let mods = group |> List.map _.Module |> List.distinct
                if List.length mods > 1 then Some(id, mods) else None)

        let subjectConflict =
            subjects
            |> List.groupBy (fun r -> r.Definition.Id)
            |> List.tryPick (fun (id, group) ->
                let mods = group |> List.map _.Module |> List.distinct
                if List.length mods > 1 then Some(id, mods) else None)

        match metricConflict, subjectConflict with
        | Some(id, mods), _ ->
            Error(
                sprintf
                    "Duplicate metric id '%s' declared by modules: %s. Every registered metric must resolve to a unique id — rename the metric in one module to disambiguate."
                    id
                    (String.concat ", " mods)
            )
        | _, Some(id, mods) ->
            Error(
                sprintf
                    "Duplicate subject id '%s' declared by modules: %s. Every registered subject hierarchy must resolve to a unique id — rename the subject in one module to disambiguate."
                    id
                    (String.concat ", " mods)
            )
        | None, None ->
            match definitionProblems metrics subjects @ descriptionProblems subjects descriptions with
            | [] -> Ok(Registry(metrics, subjects, descriptions |> List.map _.Description) :> IMetricRegistry)
            | problems ->
                Error(
                    String.concat
                        "
"
                        problems
                )

    /// Fold accumulated registrations into an `IMetricRegistry` with no
    /// subject descriptions, or the duplicate-id diagnostic.
    let tryBuild
        (metrics: MetricRegistration list)
        (subjects: SubjectRegistration list)
        : Result<IMetricRegistry, string> =
        tryBuildDescribed metrics subjects []

    /// `tryBuildDescribed` that raises on a refusal — the compose-time call
    /// site (structural enforcement: a collision, or a description of
    /// nothing, fails startup rather than a request).
    let buildDescribed
        (metrics: MetricRegistration list)
        (subjects: SubjectRegistration list)
        (descriptions: SubjectDescriptionRegistration list)
        : IMetricRegistry =
        match tryBuildDescribed metrics subjects descriptions with
        | Ok registry -> registry
        | Error message -> failwith message

    /// `tryBuild` that raises on conflict — the compose-time call site
    /// (structural enforcement: a collision fails startup, not a request).
    let build (metrics: MetricRegistration list) (subjects: SubjectRegistration list) : IMetricRegistry =
        buildDescribed metrics subjects []

    /// The description a registry carries for a subject: `None` when the
    /// subject is undescribed, or when the registry does not carry
    /// descriptions (`IDescribesSubjects` is an optional capability).
    let trySubjectDescription (registry: IMetricRegistry) (subjectId: string) : SubjectDescription option =
        match registry with
        | :? IDescribesSubjects as described -> described.TryGetSubjectDescription subjectId
        | _ -> None

    /// The metric and subject registrations a registry keeps, with their
    /// declaring modules: `None` when the registry does not carry them
    /// (`IAttributesDeclarations` is an optional capability).
    let tryRegistrations (registry: IMetricRegistry) : (MetricRegistration list * SubjectRegistration list) option =
        match registry with
        | :? IAttributesDeclarations as attributed ->
            Some(attributed.MetricRegistrations, attributed.SubjectRegistrations)
        | _ -> None

    /// The empty registry — what a composition that registered nothing
    /// projects to. `Metrics` / `Subjects` are empty; every lookup is
    /// `None` / `[]`. Byte-for-byte the no-registration path (GP 13).
    let empty: IMetricRegistry = build [] []