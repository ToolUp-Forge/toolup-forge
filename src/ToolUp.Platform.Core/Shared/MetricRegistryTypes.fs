// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Grounding

open System

// ─── Metric & subject registry (Phase 519) ───────────────────────────
//
// **Namespace `ToolUp.Platform.Grounding`** — deliberately *not* the
// root `ToolUp.Platform` namespace, so the grounding `MetricDefinition`
// does not collide with the observability `ToolUp.Platform.Metrics
// .MetricDefinition` (a Prometheus signal). Consumers reference these as
// `Grounding.MetricDefinition` / `Grounding.IMetricRegistry`, keeping the
// two "metric" vocabularies unambiguous by construction.
//
// Modules declare the *quantities they compute* (metrics) and the
// *entity hierarchies they compute them over* (subjects) exactly the way
// they declare `DataType`s — a compose-time fan-in with duplicate-id
// rejection. Without a registry, module outputs are stringly-typed and
// incomparable across modules; with it, the platform gains an
// ontology-lite that fact storage, retrieval planning, and capability
// discovery all stand on.
//
// **Distinct from the observability `Metrics.MetricDefinition`.** That
// type (namespace `ToolUp.Platform.Metrics`) describes a Prometheus
// counter/gauge/histogram — a *telemetry* signal. A `MetricDefinition`
// here is a *business quantity* (a KPI: elasticity, share-of-voice, MRR)
// with a unit, a direction-of-better, a staleness policy, and an optional
// producing-operation link. The two never interact; a module may declare
// both, neither, or either.
//
// **Optional; zero-weight when unused (GP 13).** A module that declares
// no metrics or subjects composes and runs byte-for-byte identically to
// today; the registry is empty and every read surface returns nothing.
// Registration is a gift a module gives to planners and discovery tools,
// never an obligation.
//
// **Generic substrate (GP 1).** The types carry no domain or vendor
// vocabulary — a metric's `Name` / `Unit` / `Dimensionality` are opaque
// strings the declaring module supplies. Domain metric packs (media,
// retail, finance vocabularies) live in consumer packages, never here.

/// Which direction of movement in a metric's value is "better" — the
/// polarity a planner or dashboard uses to colour a change green or red.
/// A metric with no natural polarity declares `Neutral`.
type DirectionOfBetter =
    /// A larger value is preferable (revenue, retention, throughput).
    | HigherIsBetter
    /// A smaller value is preferable (churn, latency, cost).
    | LowerIsBetter
    /// No natural polarity (a count, a category share, a raw reading).
    | Neutral

/// How long a fact carrying this metric stays "fresh" before retrieval
/// flags it stale. Freshness is a *derived* status per this policy — no
/// mutable flag is stored on any fact (see Phase 520).
type StalenessPolicy =
    /// Fresh for a fixed wall-clock window after the fact's transaction
    /// time; stale thereafter. The window is the `TimeSpan`.
    | FreshFor of TimeSpan
    /// Never stale on age alone — a fact stays current until a newer
    /// fact in its lineage supersedes it (the default for slow-moving
    /// or asserted quantities).
    | UntilSuperseded
    /// Stale as soon as any upstream input the fact was computed from
    /// changes (lineage-driven; the reactive-recomputation policy).
    | UntilUpstreamChange

/// What a fact carrying this metric does when the lineage walk (Phase 8a)
/// marks it `InputsChanged` — an upstream data-object version it was
/// computed from has been superseded (Phase 561, continuous-intelligence
/// ladder rung 2). The invalidation state is *derived* (never a stored
/// mutable flag — law L1); this policy only governs what *executes* in
/// response. An **undeclared** metric (`MetricDefinition.RecomputePolicy =
/// None`) behaves as `Manual`: nothing recomputes unbidden on upgrade
/// (GP 11), so a deployment that declares no policy is byte-for-byte
/// unchanged (GP 13).
type RecomputePolicy =
    /// On invalidation, enqueue a recompute job through `IJobScheduler`
    /// (GP 12 job-scheduler rules). The job re-asserts through the ordinary
    /// `IFactStore.Assert` path, so supersession edges stay derived and the
    /// audit trail is the same one assert always writes. The eager posture
    /// — the fact base chases its inputs the moment they move.
    | Eager
    /// On invalidation, do nothing until the next store read of the
    /// affected lineage drives the recompute inline. Amortises cost against
    /// demand — a fact nobody reads never recomputes.
    | OnQuery
    /// On invalidation, surface the changed state only — freshness derives
    /// stale under `UntilUpstreamChange`, and a human or an explicit
    /// trigger drives any recompute. The default (an undeclared metric).
    | Manual

/// Resolution for an optional `MetricDefinition.RecomputePolicy`
/// declaration — the undeclared metric (`None`) is `Manual` (GP 11:
/// nothing recomputes unbidden).
[<RequireQualifiedAccess>]
module RecomputePolicy =

    /// The effective policy for an optional declaration — `None` ⇒
    /// `Manual`.
    let resolve (declared: RecomputePolicy option) : RecomputePolicy = declared |> Option.defaultValue Manual

/// A registered business quantity a module computes. Identity is the
/// stable `Id` string (a `ComponentId`-class token, never the display
/// `Name`) — two modules declaring the same `Id` is a compose-time
/// duplicate-registration error.
type MetricDefinition = {
    /// Stable identity token (e.g. `"elasticity"`, `"share_of_voice"`).
    /// `ComponentId`-class: lowercase, rename-stable, never a display
    /// name. The key every fact, plan, and discovery tool references.
    Id: string
    /// Human-readable display name (`"Price Elasticity"`). Cosmetic —
    /// may be renamed without breaking any reference.
    Name: string
    /// Unit symbol the value is measured in (`"GBP"`, `"%"`, `"count"`,
    /// `"ratio"`). Opaque here; a later dimensional-analysis check
    /// (plan law L6) reads `Dimensionality`.
    Unit: string
    /// Coarse dimension label for comparability (`"currency"`,
    /// `"count"`, `"ratio"`, `"duration"`, `"dimensionless"`). Two
    /// metrics are only combinable when their dimensionalities agree;
    /// the check itself is a later phase, the declaration lands now.
    Dimensionality: string
    /// Which direction of movement is "better".
    Direction: DirectionOfBetter
    /// Canonical rendering format for the value — a .NET numeric format
    /// string (`"N2"`, `"P1"`, `"C0"`) or an empty string for "render
    /// verbatim". The numeric-fidelity gate (Phase 523) canonicalises a
    /// quoted number through this format before comparing it to a fact.
    DisplayFormat: string
    /// How freshness of a fact carrying this metric is derived.
    Staleness: StalenessPolicy
    /// Optional link to the catalog operation that *produces* this
    /// metric (an operation id). `None` for a metric a module declares
    /// but computes off-catalog, or asserts by hand. When present, the
    /// gap planner can map a missing fact → this operation → its input
    /// schema → the data catalog (the closed loop's "computable" branch).
    ProducingOperation: string option
    /// Optional **canonical-method** declaration (Phase 566 — D19
    /// closure). Several methods computing one metric is normal and the
    /// fact store never merges the competitors; this selector names which
    /// method's lineage a *method-less* query resolves to by default, so
    /// retrieval and planning pick deterministically instead of returning
    /// whichever competing head enumerates first. The selector is matched
    /// against method-identity strings (`computed:op:ver:hash` /
    /// `asserted:principal` / `imported:cert`) per `CanonicalMethod.matches`
    /// — a full identity matches exactly; a shorter selector matches any
    /// identity it prefixes at a `:` boundary (`"computed:rollup"` matches
    /// every version/parameterisation of the `rollup` operation). `None`
    /// (the default) preserves the undeclared behaviour exactly (GP 11):
    /// a method-less query surfaces every competing head. Selection is
    /// never silent — competitors stay fully queryable, and the query
    /// surface carries a derived competing-methods indicator (GP 9).
    CanonicalMethod: string option
    /// Optional **recompute policy** (Phase 561 — reactive fact
    /// recomputation). Governs what executes when the lineage walk marks a
    /// fact carrying this metric `InputsChanged`: `Eager` enqueues a
    /// recompute job, `OnQuery` recomputes lazily at the next read, `Manual`
    /// surfaces the changed state only. `None` (the default) resolves to
    /// `Manual` via `RecomputePolicy.resolve` — an undeclared metric
    /// recomputes nothing unbidden, so the composition is byte-for-byte
    /// unchanged (GP 11 / GP 13).
    RecomputePolicy: RecomputePolicy option
}

/// Matching semantics for a `MetricDefinition.CanonicalMethod` selector
/// (Phase 566). Pure string logic over method-identity strings, shared by
/// the fact store's query default and any surface that needs to explain
/// which competitor a canonical declaration picks.
[<RequireQualifiedAccess>]
module CanonicalMethod =

    /// Whether a method identity (`computed:op:ver:hash` /
    /// `asserted:principal` / `imported:cert`) matches a canonical-method
    /// selector: an exact match, or a segment-prefix match at a `:`
    /// boundary (`"computed:rollup"` matches `"computed:rollup:2:p7"` but
    /// never `"computed:rollup-alt:1:p0"`). Ordinal throughout — identity
    /// strings are wire tokens, never localised text.
    let matches (selector: string) (methodIdentity: string) : bool =
        methodIdentity = selector
        || methodIdentity.StartsWith(selector + ":", StringComparison.Ordinal)

/// A registered subject hierarchy a module computes metrics over — a
/// *dimension* of the entity space (a product hierarchy, a geography, an
/// org chart). Identity is the stable `Id`; the ordered `Levels` name the
/// kind of entity at each depth from root to leaf.
type SubjectDefinition = {
    /// Stable identity token for this hierarchy (e.g.
    /// `"product_hierarchy"`, `"geography"`). `ComponentId`-class.
    Id: string
    /// Human-readable display name (`"Product Hierarchy"`). Cosmetic.
    Name: string
    /// Ordered level labels from root to leaf — each names the *kind* of
    /// entity at that depth (`[ "market"; "brand"; "sku" ]`). A subject
    /// *instance* (a concrete SKU) is an ordered path through these
    /// levels; the definition declares the shape, not the members.
    Levels: string list
    /// Optional calendar tag scoping period comparability within this
    /// hierarchy (plan law L5 — fiscal calendars are registry-scoped per
    /// subject hierarchy, so "Q2" is unambiguous *within* a subject and
    /// never silently compared across calendars). Opaque here; `None`
    /// means the deployment's default (calendar) periods apply.
    Calendar: string option
}

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

    let private firstDuplicate
        (idOf: 'a -> string)
        (moduleOf: 'a -> string)
        (regs: 'a list)
        : (string * string list) option =
        regs
        |> List.groupBy idOf
        |> List.tryPick (fun (id, group) ->
            if List.length group > 1 then
                Some(id, group |> List.map moduleOf |> List.distinct)
            else
                None)

    /// The immutable registry implementation. Private — consumers hold
    /// the `IMetricRegistry` interface; construction is through `build`
    /// (or `tryBuild`) so the duplicate check is never bypassed.
    type private Registry(metrics: MetricRegistration list, subjects: SubjectRegistration list) =
        // Deduplicated declaration-order projections. `build` has already
        // proven ids are unique, so `distinctBy` only guards against a
        // module declaring the SAME id twice itself (idempotent), which
        // is not a cross-module conflict and is silently collapsed.
        let metricDefs = metrics |> List.map _.Definition |> List.distinctBy _.Id

        let subjectDefs = subjects |> List.map _.Definition |> List.distinctBy _.Id

        let metricById = metricDefs |> List.map (fun d -> d.Id, d) |> Map.ofList

        let subjectById = subjectDefs |> List.map (fun d -> d.Id, d) |> Map.ofList

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

    /// Fold accumulated registrations into an `IMetricRegistry`, or a
    /// diagnostic naming both modules when a metric or subject id is
    /// declared by two different modules. A single module re-declaring
    /// the same id is not a conflict (idempotent — collapsed). The
    /// diagnostic shape matches the SDK's duplicate-registration idiom.
    let tryBuild
        (metrics: MetricRegistration list)
        (subjects: SubjectRegistration list)
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
        | None, None -> Ok(Registry(metrics, subjects) :> IMetricRegistry)

    /// `tryBuild` that raises on conflict — the compose-time call site
    /// (structural enforcement: a collision fails startup, not a request).
    let build (metrics: MetricRegistration list) (subjects: SubjectRegistration list) : IMetricRegistry =
        match tryBuild metrics subjects with
        | Ok registry -> registry
        | Error message -> failwith message

    /// The empty registry — what a composition that registered nothing
    /// projects to. `Metrics` / `Subjects` are empty; every lookup is
    /// `None` / `[]`. Byte-for-byte the no-registration path (GP 13).
    let empty: IMetricRegistry = build [] []