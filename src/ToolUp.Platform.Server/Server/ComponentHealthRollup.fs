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