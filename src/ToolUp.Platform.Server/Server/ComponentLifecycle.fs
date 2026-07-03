// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Component lifecycle ordering (ComponentLifecycle) ───────────────
//
// Lets a deployment declare **init / dispose ordering** keyed by Phase
// 279 `ComponentId`, so startup and shutdown run in a deterministic,
// dependency-respecting order (e.g. the secret store initialises before
// the audit sink that reads it; dispose runs in reverse) rather than
// relying on incidental registration order.
//
// The order is a partial order expressed as "init-before" edges over the
// registered components. `initSequence` is a **stable** topological sort
// — an unconstrained component keeps its registration position, so a
// deployment that declares no edges gets exactly today's
// registration-order behaviour (GP 11), and dispose is that sequence
// reversed. A declared **cycle** cannot be satisfied and is rejected at
// compose (`ensureAcyclic` / an `Error` from `initSequence`), not left to
// surface as a runtime deadlock.

/// A declarative init/dispose ordering over composed components: the
/// registered components (in registration order) plus "init-before"
/// edges. `(a, b)` means *a initialises before b* — and therefore *b
/// disposes before a*.
type ComponentOrder = {
    /// The components to order, in registration order. An unconstrained
    /// component keeps this position (stable sort).
    Components: ComponentId list
    /// Init-before edges: `(a, b)` ⇒ a inits before b.
    Edges: (ComponentId * ComponentId) list
}

module ComponentLifecycle =

    /// The empty order.
    let empty: ComponentOrder = { Components = []; Edges = [] }

    /// An order over the given components with no constraints — resolves
    /// to exactly registration order (GP 11).
    let ofComponents (components: ComponentId list) : ComponentOrder = { Components = components; Edges = [] }

    /// Add an "a initialises before b" constraint. Dispose reverses it (b
    /// disposes before a).
    let before (a: ComponentId) (b: ComponentId) (order: ComponentOrder) : ComponentOrder = {
        order with
            Edges = order.Edges @ [ a, b ]
    }

    /// The initialisation sequence: a **stable** topological sort of the
    /// components under the declared edges (an unconstrained component
    /// keeps its registration position). `Error` when the edges contain a
    /// cycle — naming the components that could not be ordered. Edges
    /// referencing an unregistered id are ignored (the id is not part of
    /// this order).
    let initSequence (order: ComponentOrder) : Result<ComponentId list, string> =
        let nodes = order.Components |> List.distinct
        let nodeSet = Set.ofList nodes

        let edges =
            order.Edges
            |> List.filter (fun (a, b) -> nodeSet.Contains a && nodeSet.Contains b && a <> b)
            |> List.distinct

        let indeg0 = nodes |> List.map (fun n -> n, 0) |> Map.ofList

        let indeg =
            edges
            |> List.fold (fun (m: Map<ComponentId, int>) (_, b) -> m |> Map.add b (m.[b] + 1)) indeg0

        // Kahn's algorithm, selecting the next zero-indegree node in
        // registration order so the sort is deterministic + stable.
        let rec go (emitted: ComponentId list) (emittedSet: Set<ComponentId>) (indeg: Map<ComponentId, int>) =
            let next =
                nodes |> List.tryFind (fun n -> not (emittedSet.Contains n) && indeg.[n] = 0)

            match next with
            | Some n ->
                let indeg' =
                    edges
                    |> List.fold
                        (fun (m: Map<ComponentId, int>) (a, b) -> if a = n then m |> Map.add b (m.[b] - 1) else m)
                        indeg

                go (n :: emitted) (emittedSet |> Set.add n) indeg'
            | None ->
                if List.length emitted = List.length nodes then
                    Ok(List.rev emitted)
                else
                    let remaining =
                        nodes
                        |> List.filter (fun n -> not (emittedSet.Contains n))
                        |> List.map ComponentId.value
                        |> String.concat ", "

                    Error(
                        sprintf
                            "cyclic component lifecycle order: the init-before edges cannot be satisfied — [%s] form (or depend on) a cycle. Break the cycle so the components can initialise in a deterministic order."
                            remaining
                    )

        go [] Set.empty indeg

    /// The dispose sequence — the init sequence reversed. `Error` on the
    /// same cycle `initSequence` reports.
    let disposeSequence (order: ComponentOrder) : Result<ComponentId list, string> =
        initSequence order |> Result.map List.rev

    /// Raise on a cyclic order — the compose-time failure surface. A
    /// cycle breaks any deterministic init, so it is a compose-time
    /// error, not a runtime surprise (mirrors `ComponentId.ensureUnique`).
    /// No-op for an acyclic (or empty) order.
    let ensureAcyclic (context: string) (order: ComponentOrder) : unit =
        match initSequence order with
        | Ok _ -> ()
        | Error message -> failwithf "%s: %s" context message

    /// Run `effect` over each component in initialisation order. `Error`
    /// (without invoking any effect) when the order is cyclic.
    let runInit (effect: ComponentId -> unit) (order: ComponentOrder) : Result<unit, string> =
        initSequence order |> Result.map (List.iter effect)

    /// Run `effect` over each component in dispose order (init reversed).
    /// `Error` (without invoking any effect) when the order is cyclic.
    let runDispose (effect: ComponentId -> unit) (order: ComponentOrder) : Result<unit, string> =
        disposeSequence order |> Result.map (List.iter effect)