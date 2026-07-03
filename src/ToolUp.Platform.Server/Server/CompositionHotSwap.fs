// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Collections.Concurrent

// ─── Live composition hot-swap (CompositionHotSwap) ──────────────────
//
// Mutates a **running** app's composition — swap a companion
// implementation (or a module) — **without a full redeploy**, targeted by
// the stable Phase 279 `ComponentId` and ordered by the Phase 291
// lifecycle (init the new component, dispose the old, in dependency
// order). A near-term, general forge capability: zero-downtime
// config/companion swaps (rotate an `IBlobStorage` backend, flip an
// `IAIProvider`) + a faster dev loop (hot-reload a module without
// restarting the host).
//
// **Safe by construction (GP 4):**
//   * **Only declared composed components are swappable.** A swap targets
//     an id already present in the registry — never arbitrary-code
//     injection. An unknown id is `SwapRejected`.
//   * **In-flight requests finish on the old component.** The registry
//     re-point is a single atomic map write; a caller that already
//     resolved the old implementation keeps using that reference, and
//     only *new* resolutions see the replacement — no mid-request swap.
//   * **Atomic with rollback.** A replacement that fails to initialise
//     leaves the registry untouched (old stays live); a post-commit
//     dispose failure rolls the registry back to the old implementation.
//
// **Opt-in, off by default (GP 11 / GP 13).** Gated by `HotSwapMode`
// (default `NoHotSwap` → every swap is `SwapRejected`, nothing is composed);
// a deployment that never enables it pays nothing and is byte-for-byte
// unchanged. Every attempt emits a `HotSwapEvent` keyed by `ComponentId`
// for the audit / telemetry trail.

/// Opt-in gate for live composition hot-swap. `NoHotSwap` (the default)
/// refuses every swap — a deployment that does not enable it is unchanged.
type HotSwapMode =
    | NoHotSwap
    | EnabledHotSwap

/// The outcome of a hot-swap attempt, keyed by the target component.
type HotSwapOutcome =
    /// The replacement is live; the old implementation was disposed.
    | SwapApplied of ComponentId
    /// The swap was refused before any change (disabled, unknown target,
    /// cyclic lifecycle order) — the reason names which.
    | SwapRejected of ComponentId * reason: string
    /// The swap was attempted but rolled back to the old implementation
    /// (init or post-commit dispose failed) — nothing is left half-swapped.
    | SwapRolledBack of ComponentId * reason: string

/// A swap event for the audit / telemetry trail, keyed by `ComponentId`.
type HotSwapEvent = {
    Component: ComponentId
    Outcome: HotSwapOutcome
}

/// A thread-safe registry of composed implementations keyed by stable
/// `ComponentId`. `Resolve` returns the *current* implementation; a
/// hot-swap re-points a key with a single atomic write, so a reference
/// already resolved keeps pointing at the old implementation (in-flight
/// safety) while new resolutions see the replacement.
type ComponentRegistry<'Impl>(initial: (ComponentId * 'Impl) seq) =
    let map = ConcurrentDictionary<ComponentId, 'Impl>()

    do
        for id, impl in initial do
            map.[id] <- impl

    /// The current implementation for `id`, if the component is declared.
    member _.Resolve(id: ComponentId) : 'Impl option =
        match map.TryGetValue id with
        | true, v -> Some v
        | _ -> None

    /// Whether `id` is a declared composed component (and therefore
    /// swappable).
    member _.Contains(id: ComponentId) : bool = map.ContainsKey id

    /// The declared component ids.
    member _.Ids: ComponentId list = map.Keys |> List.ofSeq

    /// Atomically re-point `id` to `impl`. Internal — a re-point only
    /// happens through a governed `CompositionHotSwap.swap`.
    member internal _.Set(id: ComponentId, impl: 'Impl) : unit = map.[id] <- impl

module CompositionHotSwap =

    /// The no-op event sink — the default when a deployment wires no
    /// audit / telemetry emitter.
    let noEmit: HotSwapEvent -> unit = ignore

    /// Attempt to swap the implementation at `target` for `replacement`,
    /// governed by `mode` (opt-in) and `order` (the Phase 291 lifecycle,
    /// which must be acyclic to dispose deterministically). Steps: guard →
    /// init the replacement → atomically re-point the registry → dispose
    /// the old. A pre-commit init failure leaves the registry untouched;
    /// a post-commit dispose failure rolls back to the old implementation.
    /// Every outcome is emitted as a `HotSwapEvent`.
    let swap
        (mode: HotSwapMode)
        (emit: HotSwapEvent -> unit)
        (order: ComponentOrder)
        (registry: ComponentRegistry<'Impl>)
        (init: 'Impl -> unit)
        (dispose: 'Impl -> unit)
        (target: ComponentId)
        (replacement: 'Impl)
        : HotSwapOutcome =
        let record (outcome: HotSwapOutcome) : HotSwapOutcome =
            emit {
                Component = target
                Outcome = outcome
            }

            outcome

        match mode with
        | NoHotSwap ->
            record (
                SwapRejected(
                    target,
                    "live composition hot-swap is disabled (HotSwapMode = NoHotSwap); opt in via ServerConfig"
                )
            )
        | EnabledHotSwap ->
            // The lifecycle order must be satisfiable to dispose in a
            // deterministic Phase-291 reverse order.
            match ComponentLifecycle.disposeSequence order with
            | Error reason -> record (SwapRejected(target, sprintf "lifecycle order is not satisfiable: %s" reason))
            | Ok _ ->
                match registry.Resolve target with
                | None ->
                    record (
                        SwapRejected(
                            target,
                            "target ComponentId is not a declared composed component — only declared components are swappable (no arbitrary-code injection)"
                        )
                    )
                | Some old ->
                    // 1. Initialise the replacement. A failure here is
                    //    pre-commit — the registry is untouched, the old
                    //    implementation stays live, and the partially
                    //    initialised replacement is best-effort disposed.
                    let initialised =
                        try
                            init replacement
                            Ok()
                        with ex ->
                            Error ex.Message

                    match initialised with
                    | Error reason ->
                        try
                            dispose replacement
                        with _ ->
                            ()

                        record (SwapRolledBack(target, sprintf "replacement failed to initialise: %s" reason))
                    | Ok() ->
                        // 2. Commit — a single atomic re-point. New
                        //    resolutions see the replacement; references
                        //    already resolved keep the old (in-flight
                        //    requests finish on the old component).
                        registry.Set(target, replacement)

                        // 3. Dispose the old. A failure here is post-commit
                        //    — roll the registry back to the old
                        //    implementation so nothing is left half-swapped.
                        try
                            dispose old
                            record (SwapApplied target)
                        with ex ->
                            registry.Set(target, old)

                            try
                                dispose replacement
                            with _ ->
                                ()

                            record (
                                SwapRolledBack(
                                    target,
                                    sprintf "old implementation failed to dispose post-commit: %s" ex.Message
                                )
                            )