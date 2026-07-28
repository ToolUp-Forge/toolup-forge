// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.HealthChecks

// ─── Component health rollup (ComponentHealthRollup) ─────────────────
//
// Keys `IHealthCheck` results by Phase 279 `ComponentId` and aggregates
// them into a structured rollup over the composed surface, so health
// reads **per-component** ("which companion is degraded") instead of as
// an undifferentiated flat list. A health check's id is its Phase 279
// companion-impl id (`companion:IHealthCheck/<Name>`), the SAME id the
// Phase 280 manifest's `IHealthCheck` companion entries carry — so the
// rollup attaches to the manifest by id-join without widening the
// manifest shape.
//
// A check that cannot name itself (a blank `Name`) cannot be keyed by id
// — it is retained under `Unkeyed` rather than dropped, so the rollup is
// total over the registered probes.
//
// **Read-only + on demand (GP 13).** The rollup is computed only when a
// caller asks (`run` / `forApp`); a deployment that never reads it runs
// no extra probe and allocates nothing. Computing it never mutates the
// probes or the app.

/// A per-component view of health: each keyed probe's latest outcome
/// under its `ComponentId`, plus any probe that could not be keyed (a
/// blank `Name`) retained under `Unkeyed` rather than dropped.
type ComponentHealthRollup = {
    /// The latest outcome per keyed component probe.
    ByComponent: Map<ComponentId, HealthResult>
    /// Probes with no addressable id (blank `Name`), retained as
    /// `(name, outcome)` so nothing is silently lost.
    Unkeyed: (string * HealthResult) list
}

/// Phase 437 — the health rollup with a BUDGET-PRESSURE dimension
/// attached: how close each component sits to the `ResourceEnvelope` it
/// declared. A sidecar type rather than a third field on
/// `ComponentHealthRollup`, for the reason recorded on `DataFootprint`
/// and `AuthorizationSurface`: growing a shipped F# record breaks its
/// constructor for every consumer.
///
/// **Absent means undeclared, and undeclared means absent.** A component
/// with no envelope contributes NO entry — not an entry with an empty
/// list, and never a zero limit that would read as "budgeted at nothing".
/// A composition with no envelopes at all produces an empty
/// `PressureByComponent`, so the pressure dimension is invisible to a
/// pre-437 deployment (GP 11 / GP 13).
///
/// Field names are deliberately distinct from `ComponentHealthRollup`'s —
/// two records sharing a full field-name set make every unannotated
/// construction ambiguous under F#'s last-declared-wins inference.
type ComponentPressureRollup = {
    /// The Phase 290 rollup, verbatim and unmodified.
    PressureHealth: ComponentHealthRollup
    /// Per-component readings, only for components declaring a budget,
    /// each list in `EnvelopeDimension.all` order.
    PressureByComponent: Map<ComponentId, EnvelopePressure list>
}

module ComponentHealthRollup =

    /// The empty rollup — what an app with no health probes rolls up to.
    let empty: ComponentHealthRollup = {
        ByComponent = Map.empty
        Unkeyed = []
    }

    /// The Phase 279 id a health probe rolls up under:
    /// `companion:IHealthCheck/<Name>`. `None` when the probe carries no
    /// addressable name.
    let componentIdFor (check: IHealthCheck) : ComponentId option =
        if String.IsNullOrWhiteSpace check.Name then
            None
        else
            Some(ComponentId.forCompanionImpl "IHealthCheck" check.Name)

    /// Build a rollup from already-run `(probe, outcome)` pairs. Pure — a
    /// keyable probe lands under its id; an unkeyable probe (blank name)
    /// is retained under `Unkeyed`. A duplicate id keeps the last outcome
    /// (probe `Name`s are required unique by the `IHealthCheck` contract,
    /// so this is a defensive tail).
    let build (results: (IHealthCheck * HealthResult) list) : ComponentHealthRollup =
        results
        |> List.fold
            (fun rollup (check, result) ->
                match componentIdFor check with
                | Some id -> {
                    rollup with
                        ByComponent = rollup.ByComponent |> Map.add id result
                  }
                | None -> {
                    rollup with
                        Unkeyed = rollup.Unkeyed @ [ check.Name, result ]
                  })
            empty

    /// Run each probe (in parallel) and roll the outcomes up by id. The
    /// probes are the source of truth; this adds no probe of its own.
    let run (checks: IHealthCheck list) : Async<ComponentHealthRollup> = async {
        let! results =
            checks
            |> List.map (fun check -> async {
                let! result = check.Check()
                return check, result
            })
            |> Async.Parallel

        return build (List.ofArray results)
    }

    /// Roll up the health of every `IHealthCheck` a composed app
    /// registered, keyed by the same `ComponentId` the manifest's
    /// `IHealthCheck` companion entries carry. On demand (GP 13).
    let forApp (app: ServerApp) : Async<ComponentHealthRollup> = run app.HealthChecks

    /// The single worst outcome across the keyed probes — a convenience
    /// for an operator status board (`Unhealthy` > `Degraded` > `Healthy`;
    /// `Healthy` when there are no keyed probes). `Unkeyed` probes are
    /// excluded from the roll-up-to-one since they carry no component.
    let worst (rollup: ComponentHealthRollup) : HealthResult =
        let rank =
            function
            | Healthy -> 0
            | Degraded _ -> 1
            | Unhealthy _ -> 2

        rollup.ByComponent
        |> Map.toList
        |> List.map snd
        |> List.sortByDescending rank
        |> List.tryHead
        |> Option.defaultValue Healthy

    // ── Phase 437 — the budget-pressure dimension ────────────────────

    /// Attach budget-pressure readings to a rollup. `observedBy` supplies
    /// the current level for a `(component, dimension)` pair — the
    /// in-flight job count, the requests served this minute, the queue
    /// depth — from wherever the deployment already tracks it; nothing
    /// here starts a probe or keeps a counter of its own (GP 13).
    ///
    /// Only components declaring an envelope appear, and within one
    /// component only the dimensions it declares. An empty signature
    /// yields an empty pressure map with the health rollup passed
    /// through untouched, so a pre-437 deployment reads exactly as
    /// before (GP 11).
    let withPressure
        (envelopes: EnvelopeSignature)
        (observedBy: ComponentId -> EnvelopeDimension -> int)
        (rollup: ComponentHealthRollup)
        : ComponentPressureRollup =
        let byComponent =
            if Map.isEmpty envelopes then
                Map.empty
            else
                ResourceEnvelope.all envelopes
                |> List.choose (fun (componentId, envelope) ->
                    match ResourceEnvelope.pressuresFor (observedBy componentId) componentId envelope with
                    | [] -> None
                    | pressures -> Some(componentId, pressures))
                |> Map.ofList

        {
            PressureHealth = rollup
            PressureByComponent = byComponent
        }

    /// The pressure readings at or above `thresholdPercent` utilisation,
    /// in deterministic order — the "which component is about to hit its
    /// ceiling" query an operator board asks. `100` reports only
    /// saturated dimensions.
    let underPressure (thresholdPercent: int) (rollup: ComponentPressureRollup) : EnvelopePressure list =
        rollup.PressureByComponent
        |> Map.toList
        |> List.sortBy (fst >> ComponentId.value)
        |> List.collect snd
        |> List.filter (fun pressure -> ResourceEnvelope.utilisationPercent pressure >= thresholdPercent)

    /// A deterministic, human-readable rendering of the pressure
    /// dimension — one line per budgeted component. Empty for a
    /// composition that declares no envelope.
    let describePressure (rollup: ComponentPressureRollup) : string list =
        rollup.PressureByComponent
        |> Map.toList
        |> List.sortBy (fst >> ComponentId.value)
        |> List.map (fun (componentId, pressures) ->
            ComponentId.value componentId
            + ": "
            + (pressures |> List.map ResourceEnvelope.describePressure |> String.concat ", "))