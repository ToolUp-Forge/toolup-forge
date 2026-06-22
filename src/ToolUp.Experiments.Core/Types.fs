// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Experiments

// ─── A/B experiment substrate — Phase 242 ────────────────────────────
//
// The honest minimal experimentation floor over the Phase 5c flag layer:
// a scoped experiment store, deterministic weight-respecting variant
// assignment, and exposure logging. Statistical-significance verdicts
// are deliberately OUT OF SCOPE — the exposure stream is the input a
// downstream analytics / telemetry sink consumes. Definitions and
// assignments are immutable records (GP 5); the substrate names no
// specific event store — production wires the `IExposureSink` to the
// shipped `IEventStore` (GP 1, GP 12).

/// One arm of an experiment with its relative weight (need not sum to
/// 1.0 — weights are normalised at assignment time).
type Variant = { Key: string; Weight: float }

type ExperimentStatus =
    | Draft
    | Running
    | Stopped

/// An experiment definition — immutable; a change produces a new value.
type Experiment = {
    Id: string
    Variants: Variant list
    Status: ExperimentStatus
}

/// The variant a principal is assigned to for an experiment.
type ExperimentAssignment = {
    ExperimentId: string
    PrincipalId: string
    VariantKey: string
}

/// An exposure record — emitted the first time a principal is
/// assigned-and-observed for an experiment.
type ExposureEvent = {
    ExperimentId: string
    PrincipalId: string
    VariantKey: string
}

/// Scope-indexed persistent store for experiment definitions. Scope
/// isolation is the caller's responsibility (resolve the authenticated
/// scope, then call through) — the same trust boundary as every other
/// scoped store. Portability (GP 12): identity by value, async at every
/// boundary, stateless between calls, per-scope sharding.
type IExperimentStore =
    abstract Get: scopeId: string * experimentId: string -> Async<Experiment option>
    abstract List: scopeId: string -> Async<Experiment list>
    abstract Set: scopeId: string * experiment: Experiment -> Async<Result<unit, string>>
    abstract Remove: scopeId: string * experimentId: string -> Async<unit>

/// Sink for exposure events. The substrate names no specific event
/// store; a deployment adapts this to the shipped `IEventStore` (or a
/// telemetry pipeline) at compose time.
type IExposureSink =
    abstract Record: scopeId: string * exposure: ExposureEvent -> Async<unit>