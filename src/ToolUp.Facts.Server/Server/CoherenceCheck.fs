// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.HealthChecks

// ─── Fact-base coherence checking (Phase 563) ────────────────────────
//
// The self-auditing store. Facts at different aggregation levels of one
// subject hierarchy (a SKU volume vs the brand roll-up) are mechanically
// comparable when the metric declares **additive** roll-up semantics
// (`Grounding.RollUp.Additive tolerance`, Phase 519 registry data). A
// standing, decidable check compares each parent fact against the sum of
// its direct children for the same metric and period, over the current
// heads, and flags a cross-level inconsistency — a mixed-vintage
// aggregate, a partial load, a unit slip — as a typed finding.
//
// **Alert, never auto-correct (GP 9).** A finding is surfaced (an audit
// row under `_facts`, an `INotificationChannel` alert, an `IHealthCheck`
// degradation) — it *never* mutates a stored fact. Correction stays a
// human act: re-assert the right value through the ordinary
// `IFactStore.Assert` supersession path.
//
// **Derived from the registry, never configured per fact (563.A).**
// Comparability is decided by the metric's `RollUp` declaration + the
// subject hierarchy being registered — the check reads the registry, not
// any per-fact annotation. The numeric tolerance is registry data (the
// `Additive tolerance`), so a rounding / display-format wobble never
// flags a real inconsistency.
//
// **Zero-weight when unused (GP 11 / GP 13).** The check is opt-in
// (`FactsCompose.withCoherenceChecks`); a metric that declares no `RollUp`
// (or `NonAdditive`) is never checked; a registry-less deployment produces
// no findings. A deployment that does not opt in composes and runs
// byte-for-byte identically.

/// The plausible cause class of a cross-level coherence discrepancy — a
/// decidable hint the finding carries, never a certainty. Ordered by the
/// strength of the signal the classifier tests for.
type CoherenceCauseClass =
    /// The parent and its children carry materially different transaction
    /// vintages (their `AsOf` spread exceeds the configured window) — the
    /// aggregate was computed from a different data load than its parts.
    | MixedVintage
    /// Some children are missing or explicitly `Absent`-valued, so the
    /// children sum falls short of the parent — a partial load.
    | PartialLoad
    /// The parent/children ratio is close to a power of ten — a scale /
    /// unit slip (thousands vs units, percent vs ratio).
    | UnitSlip
    /// A discrepancy beyond tolerance that matches no specific fingerprint.
    | Unclassified

/// Canonical string form of a cause class — the stable token an audit
/// payload, alert, and health message share so external monitors can key
/// off it.
module CoherenceCauseClass =
    let toString (cause: CoherenceCauseClass) : string =
        match cause with
        | MixedVintage -> "MixedVintage"
        | PartialLoad -> "PartialLoad"
        | UnitSlip -> "UnitSlip"
        | Unclassified -> "Unclassified"

/// A typed coherence finding (task 563.B) — a parent subject whose value
/// does not reconcile with the sum of its direct children for one metric
/// and period. Pure data: it records what was found, never a correction.
type CoherenceFinding = {
    /// The parent subject whose roll-up failed to reconcile.
    Subject: SubjectRef
    Metric: MetricRef
    Period: TemporalExtent
    /// The sum of the direct children's values (the expected parent value).
    Expected: decimal
    /// The parent's asserted value.
    Found: decimal
    /// `Found - Expected` — signed, so the direction is legible.
    Discrepancy: decimal
    /// The metric's declared additive tolerance the discrepancy exceeded.
    Tolerance: decimal
    /// The classifier's plausible cause.
    Cause: CoherenceCauseClass
    /// The number of direct children compared against the parent.
    ChildCount: int
}

/// Cause-classification heuristics + the scope the health probe reads.
/// The numeric *tolerance* is NOT here — it is per-metric registry data
/// (`RollUp.Additive`); these are the cross-cutting knobs the classifier
/// and the composed surface need. `CoherenceConfig.defaults` is the
/// zero-config posture.
type CoherenceConfig = {
    /// Transaction-time (`AsOf`) spread across a comparable set beyond
    /// which a discrepancy is classified `MixedVintage`. Default: one day.
    VintageWindow: TimeSpan
    /// How close the parent/children ratio must be to a power of ten
    /// (relative) to classify `UnitSlip`. Default: 1%.
    UnitSlipRelativeTolerance: decimal
    /// The scope the `IHealthCheck` probe re-scans for current findings.
    /// Default: the reserved `_platform` scope. Multi-tenant deployments
    /// rely on the per-scope scheduled job's audit rows + alerts; the
    /// health probe is a coarse process-level signal for this one scope.
    HealthScope: string
    /// Derive each metric's vintage window from its registry
    /// `StalenessPolicy` instead of applying `VintageWindow` globally.
    ///
    /// The rest of this check is already registry-derived —
    /// comparability comes from `RollUp.Additive` and the registered
    /// hierarchy, and the numeric tolerance is registry data. The
    /// vintage window was the one heuristic left on a single global
    /// knob, which is a poor fit for a deployment mixing an hourly
    /// metric with a quarterly one: one day is far too loose for the
    /// first and far too tight for the second, and no single value
    /// serves both.
    ///
    /// With this on, a metric declaring `FreshFor window` is classified
    /// against **its own** declared freshness — the deployment has
    /// already said how long a value of this metric stays current, and a
    /// comparable set spread wider than that is mixed-vintage by that
    /// declaration. `UntilSuperseded` and `UntilUpstreamChange` name no
    /// wall-clock window at all, so they keep `VintageWindow`, as does a
    /// metric with no declaration and any run with no registry.
    ///
    /// **Off by default (GP 11).** Classification feeds audit rows and
    /// alerts, so turning it on changes what an existing deployment's
    /// alerting fires on — that is a deployment's decision to make, not
    /// an upgrade's to make for it.
    DeriveVintageFromStaleness: bool
}

/// Construction for `CoherenceConfig`.
module CoherenceConfig =
    /// The zero-config posture: a one-day vintage window, a 1% unit-slip
    /// sensitivity, the `_platform` health scope, and the global vintage
    /// window rather than per-metric derivation.
    let defaults: CoherenceConfig = {
        VintageWindow = TimeSpan.FromDays 1.0
        UnitSlipRelativeTolerance = 0.01m
        HealthScope = "_platform"
        DeriveVintageFromStaleness = false
    }

    /// `defaults` with per-metric vintage derivation on — the
    /// per-metric-honesty posture for a deployment whose metrics declare
    /// meaningfully different `FreshFor` windows.
    let withDerivedVintage (config: CoherenceConfig) : CoherenceConfig = {
        config with
            DeriveVintageFromStaleness = true
    }

/// Reserved event-type discriminator for a coherence finding audit row,
/// written under the fact store's `_facts` source module (Phase 520).
/// Filter `IEventStore.ReadBySource scope FactEvents.SourceModule` then by
/// this type for the coherence audit trail in isolation.
module CoherenceEvents =
    [<Literal>]
    let FindingType = "FactCoherenceFinding"

/// Audit-row payload for a coherence finding (JSON-serialised into
/// `ModuleEvent.Payload`). Flattened + PII-free — identifiers, magnitudes,
/// and the classification only.
type CoherenceFindingEvent = {
    /// Readable subject reference (`hierarchy/level>level`).
    Subject: string
    Metric: string
    PeriodFrom: DateTime
    PeriodTo: DateTime
    Expected: decimal
    Found: decimal
    Discrepancy: decimal
    Tolerance: decimal
    Cause: string
    ChildCount: int
    DetectedAt: DateTime
}

/// The pure coherence derivation (task 563.A/B) + the effectful standing
/// run (task 563.C).
module CoherenceCheck =

    let private jsonOptions = FableConverters.create ()

    let private scalarOf (v: FactValue) : decimal option =
        match v with
        | Scalar d -> Some d
        | _ -> None

    let private isAbsent (v: FactValue) : bool =
        match v with
        | Absent _ -> true
        | _ -> false

    /// Whether `childPath` is a *direct* child of `parentPath` — one level
    /// deeper, with `parentPath` as an exact prefix. Structure is read from
    /// the fact paths themselves (a subject instance is an ordered path
    /// through the hierarchy's levels).
    let private isDirectChild (parentPath: string list) (childPath: string list) : bool =
        childPath.Length = parentPath.Length + 1
        && List.truncate parentPath.Length childPath = parentPath

    // Ratio proximity to a power of ten — the unit-slip fingerprint.
    let private powersOfTen = [ 1000m; 100m; 10m; 0.1m; 0.01m; 0.001m ]

    let private isUnitSlip (config: CoherenceConfig) (found: decimal) (expected: decimal) : bool =
        if found = 0m || expected = 0m then
            false
        else
            let ratio = abs (found / expected)

            powersOfTen
            |> List.exists (fun p -> abs (ratio - p) <= p * config.UnitSlipRelativeTolerance)

    let private vintageSpread (facts: Fact list) : TimeSpan =
        match facts |> List.map _.AsOf with
        | [] -> TimeSpan.Zero
        | asOfs -> (List.max asOfs) - (List.min asOfs)

    /// The vintage window a given metric is classified against: its own
    /// declared `FreshFor` when the deployment opted into per-metric
    /// derivation and the registry declares one, else the global
    /// `CoherenceConfig.VintageWindow`.
    ///
    /// `UntilSuperseded` / `UntilUpstreamChange` deliberately fall
    /// through to the global default rather than to "infinite": they say
    /// a fact does not go stale *on age*, which is a statement about
    /// staleness, not a claim that any `AsOf` spread is coherent. Reading
    /// them as an unbounded window would silently switch `MixedVintage`
    /// off for exactly the metrics whose values move by supersession.
    let internal vintageWindowFor
        (registry: Grounding.IMetricRegistry option)
        (config: CoherenceConfig)
        (metric: MetricRef)
        : TimeSpan =
        if not config.DeriveVintageFromStaleness then
            config.VintageWindow
        else
            registry
            |> Option.bind (fun reg -> reg.TryGetMetric metric.Value)
            |> Option.bind (fun d ->
                match d.Staleness with
                | Grounding.FreshFor window -> Some window
                | Grounding.UntilSuperseded
                | Grounding.UntilUpstreamChange -> None)
            |> Option.defaultValue config.VintageWindow

    /// Classify a discrepancy over its comparable set (parent :: children).
    /// Ordered strongest-signal first: an explicit `Absent` child ⇒
    /// partial load; a power-of-ten ratio ⇒ unit slip; a wide `AsOf`
    /// spread ⇒ mixed vintage; a parent exceeding its children sum ⇒
    /// partial load (children under-loaded, no explicit `Absent`);
    /// otherwise unclassified.
    let private classify
        (config: CoherenceConfig)
        (vintageWindow: TimeSpan)
        (tolerance: decimal)
        (found: decimal)
        (expected: decimal)
        (comparable: Fact list)
        : CoherenceCauseClass =
        if comparable |> List.exists (fun f -> isAbsent f.Value) then
            PartialLoad
        elif isUnitSlip config found expected then
            UnitSlip
        elif vintageSpread comparable > vintageWindow then
            MixedVintage
        elif found - expected > tolerance then
            PartialLoad
        else
            Unclassified

    /// The pure check (task 563.A/B): over a set of current-head facts,
    /// derive the coherence findings. Comparability is derived wholly from
    /// the registry — a metric participates only when it declares
    /// `RollUp.Additive` and its subject hierarchy is registered — never
    /// from any per-fact annotation. No registry (or no additive metric) ⇒
    /// no findings (GP 11 / GP 13). Never mutates anything; the input list
    /// is read-only.
    let check
        (registry: Grounding.IMetricRegistry option)
        (config: CoherenceConfig)
        (facts: Fact list)
        : CoherenceFinding list =
        match registry with
        | None -> []
        | Some reg ->
            let periodKey (p: TemporalExtent) = p.From, p.To

            // Keep only additive-metric facts over registered hierarchies,
            // pairing each with its declared tolerance.
            facts
            |> List.choose (fun f ->
                let tolerance =
                    reg.TryGetMetric f.Metric.Value
                    |> Option.bind _.RollUp
                    |> Grounding.RollUp.additiveTolerance

                match tolerance with
                | Some tol when (reg.TryGetSubject f.Subject.Hierarchy |> Option.isSome) -> Some(f, tol)
                | _ -> None)
            // Compare within one (metric, hierarchy, period) at a time.
            |> List.groupBy (fun (f, _) -> f.Metric.Value, f.Subject.Hierarchy, periodKey f.Period)
            |> List.collect (fun (_, group) ->
                let tolerance = group |> List.head |> snd

                // One representative fact per subject path (the freshest
                // head — guards against a method-less query returning
                // several competing heads for one subject).
                let bySubject =
                    group
                    |> List.map fst
                    |> List.groupBy _.Subject.Path
                    |> List.map (fun (path, fs) -> path, fs |> List.maxBy _.AsOf)

                bySubject
                |> List.choose (fun (parentPath, parent) ->
                    let children =
                        bySubject
                        |> List.filter (fun (childPath, _) -> isDirectChild parentPath childPath)
                        |> List.map snd

                    match children, scalarOf parent.Value with
                    | [], _ -> None
                    | _, None -> None
                    | _, Some found ->
                        let expected = children |> List.choose (fun c -> scalarOf c.Value) |> List.sum
                        let discrepancy = found - expected

                        if abs discrepancy <= tolerance then
                            None
                        else
                            Some {
                                Subject = parent.Subject
                                Metric = parent.Metric
                                Period = parent.Period
                                Expected = expected
                                Found = found
                                Discrepancy = discrepancy
                                Tolerance = tolerance
                                Cause =
                                    classify
                                        config
                                        (vintageWindowFor registry config parent.Metric)
                                        tolerance
                                        found
                                        expected
                                        (parent :: children)
                                ChildCount = List.length children
                            }))

    let private writeFindingEvent
        (events: IEventStore)
        (scopeId: string)
        (now: DateTime)
        (finding: CoherenceFinding)
        : Async<unit> =
        let payload: CoherenceFindingEvent = {
            Subject = SubjectRef.toString finding.Subject
            Metric = finding.Metric.Value
            PeriodFrom = finding.Period.From
            PeriodTo = finding.Period.To
            Expected = finding.Expected
            Found = finding.Found
            Discrepancy = finding.Discrepancy
            Tolerance = finding.Tolerance
            Cause = CoherenceCauseClass.toString finding.Cause
            ChildCount = finding.ChildCount
            DetectedAt = now
        }

        events.Write {
            Id = Guid.NewGuid()
            OccurredAt = now
            ScopeId = scopeId
            SourceModule = FactEvents.SourceModule
            EventType = CoherenceEvents.FindingType
            Payload = JsonSerializer.Serialize(payload, jsonOptions)
        }

    /// A one-line summary of the findings by cause class — the alert /
    /// health text.
    let summarise (findings: CoherenceFinding list) : string =
        findings
        |> List.countBy (fun f -> CoherenceCauseClass.toString f.Cause)
        |> List.sortBy fst
        |> List.map (fun (cause, n) -> sprintf "%s×%d" cause n)
        |> String.concat ", "

    /// Run the standing coherence check against `scopeId` (task 563.C): query
    /// the current heads, derive the findings, emit one audit row per finding
    /// under `_facts`, and publish a single `INotificationChannel` alert when
    /// any finding exists. Reads only — never mutates a stored fact (GP 9:
    /// alert, never auto-correct). Returns the findings so the caller (job
    /// handler / health probe / tests) can inspect them.
    let run
        (store: IFactStore)
        (registry: Grounding.IMetricRegistry option)
        (events: IEventStore)
        (notifications: INotificationChannel)
        (config: CoherenceConfig)
        (clock: unit -> DateTime)
        (scopeId: string)
        : Async<CoherenceFinding list> =
        async {
            let! heads = store.Query(scopeId, FactQuery.all)
            let findings = check registry config heads
            let now = clock().ToUniversalTime()

            for finding in findings do
                do! writeFindingEvent events scopeId now finding

            if not (List.isEmpty findings) then
                let text =
                    sprintf
                        "Fact-base coherence: %d cross-level inconsistenc%s detected (%s). Re-assert the correct value through the ordinary supersession path — findings never auto-correct."
                        (List.length findings)
                        (if List.length findings = 1 then "y" else "ies")
                        (summarise findings)

                do! notifications.Publish(scopeId, SystemMessage(SystemMessageLevel.Warning, text))

            return findings
        }

// ─── Standing execution — job handler + health check (task 563.C) ─────

/// `IJobHandler` that runs one coherence sweep for the scope in its
/// `JobContext`. Scheduled on an opt-in cadence via `IJobScheduler`
/// (`FactsCompose.withCoherenceChecks`) and fireable on demand
/// (`IJobScheduler.TriggerOnce`). Stateless between invocations (GP 12
/// rule 4): everything arrives via `JobContext` + the injected substrate.
type CoherenceJobHandler
    (
        store: IFactStore,
        registry: Grounding.IMetricRegistry option,
        notifications: INotificationChannel,
        events: IEventStore,
        config: CoherenceConfig,
        clock: unit -> DateTime,
        logger: ILogger
    ) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            try
                let! findings = CoherenceCheck.run store registry events notifications config clock ctx.ScopeId

                logger.Info(
                    sprintf "[Phase 563] Coherence check for scope %s — %d finding(s)." ctx.ScopeId findings.Length
                )

                return JobResult.Success
            with ex ->
                let msg =
                    sprintf "[Phase 563] Coherence check failed for scope %s: %s" ctx.ScopeId ex.Message

                logger.Warn msg
                // A scan / store hiccup is transient — retry on the next tick.
                return JobResult.TransientFailure msg
        }

/// Construction + the compose-time scheduled-job name.
module CoherenceJobHandler =

    /// Logical handler name registered with the scheduler.
    [<Literal>]
    let HandlerName = "_facts.coherence.check"

    /// Create the coherence job handler over the composed substrate.
    let create
        (store: IFactStore)
        (registry: Grounding.IMetricRegistry option)
        (notifications: INotificationChannel)
        (events: IEventStore)
        (config: CoherenceConfig)
        (clock: unit -> DateTime)
        (logger: ILogger)
        : IJobHandler =
        CoherenceJobHandler(store, registry, notifications, events, config, clock, logger) :> IJobHandler

/// `IHealthCheck` surfacing the fact base's current coherence for the
/// configured scope (task 563.C). Re-scans on each probe (GP 12 rule 4 —
/// reads fresh state, never a cached flag) and reports `Degraded` when any
/// finding stands. It NEVER reports `Unhealthy`: a data-quality signal must
/// not trip `/ready` to 503 and take the process out of rotation.
type CoherenceHealthCheck(store: IFactStore, registry: Grounding.IMetricRegistry option, config: CoherenceConfig) =
    interface IHealthCheck with
        member _.Name = "fact_coherence"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() : Async<HealthResult> = async {
            let! heads = store.Query(config.HealthScope, FactQuery.all)

            match CoherenceCheck.check registry config heads with
            | [] -> return Healthy
            | findings ->
                return
                    Degraded(
                        sprintf
                            "%d fact-base coherence finding(s) in scope %s (%s)"
                            findings.Length
                            config.HealthScope
                            (CoherenceCheck.summarise findings)
                    )
        }

/// Construction for `CoherenceHealthCheck`.
module CoherenceHealthCheck =
    let create
        (store: IFactStore)
        (registry: Grounding.IMetricRegistry option)
        (config: CoherenceConfig)
        : IHealthCheck =
        CoherenceHealthCheck(store, registry, config) :> IHealthCheck