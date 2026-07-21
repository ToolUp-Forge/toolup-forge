// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 453 — IModelRegistry core types ──────────────────────────────
//
// A fitted model as a provenance-complete, lifecycle-governed platform
// artifact (statistical-modelling substrate plan, Stage 4). Identity is the
// Phase 449 composite key `(specHash, datasetVersion, seed, providerId,
// providerVersion)` (plan D5) — two fits differing in any component are
// different artifacts, which is what makes branch × vintage evidence bases
// queryable. The registry manages the artifact's *lifecycle* + *provenance*;
// it is deliberately **distinct** from `IResultStore` (module-output
// caching — plan D11) and stores no module outputs.
//
// **Why server-only, not Core/Shared.** A `ModelArtifact` embeds the fit
// envelope's `FitCompositeKey` + `GateVerdict` (Phase 449, `Server/`) — those
// are SHA-256-addressed server-side compute types (`System.Security.
// Cryptography` is not Fable-compilable), so the artifact model is forced
// server-side too. The audit payloads that cross into persisted
// `ModuleEvent`s stay in `Core/Shared/AuditTypes.fs`; they carry only the
// composite-key strings the registry computes here. (Registry *query
// results* becoming a client surface is plan risk #8 — a later Fable-facing
// projection, not this phase.)
//
// **Immutability (GP 5, plan D5).** An artifact's *identity* (the composite
// key), its fit-derived core (diagnostics, gate verdicts, the opaque
// parameter-blob reference), and its registration provenance are fixed at
// registration and copied verbatim into every subsequent version. The only
// fields a lifecycle transition changes are `Status` (and the transition is
// recorded as a new immutable version via `StrictlyVersioned` storage) — so
// "lifecycle is a status, not a mutation" is enforced structurally, not by
// convention. The parameter blob itself lives in `IDataObjectStore` (plan
// D5), inheriting content dedup + version immutability rather than
// re-implementing them.

/// The governed lifecycle state of a `ModelArtifact` (plan Stage 4). A
/// forward lifecycle: an artifact is born `Fitted` (registration carries a
/// completed `FitOutcome`), may be `Approved` (a gated governance decision,
/// Owner/Admin only — GP 4), and may be `Retired` from any active state.
/// `Draft` is the pre-fit reservation state the DU carries for completeness;
/// registration-from-a-fit never mints it. `Retired` is terminal.
[<RequireQualifiedAccess>]
type ModelArtifactStatus =
    /// Reserved but not yet fit — a pre-fit placeholder. Registration from a
    /// `FitOutcome` never produces this; it exists so the lifecycle DU
    /// matches the plan's four states.
    | Draft
    /// Registered from a completed fit: diagnostics + gate verdicts attached.
    /// The birth status of every registered artifact.
    | Fitted
    /// Promoted to an approved evidence base by an Owner/Admin (GP 4). The
    /// only transition that requires an elevated role.
    | Approved
    /// Retired from active use. Terminal — no transition leaves `Retired`.
    | Retired

module ModelArtifactStatus =
    /// Stable case-name string (audit payloads, metadata sidecar, telemetry).
    /// Round-trips through `parse`; do not rename without a wire-format bump.
    let name =
        function
        | ModelArtifactStatus.Draft -> "Draft"
        | ModelArtifactStatus.Fitted -> "Fitted"
        | ModelArtifactStatus.Approved -> "Approved"
        | ModelArtifactStatus.Retired -> "Retired"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "Draft" -> Some ModelArtifactStatus.Draft
        | "Fitted" -> Some ModelArtifactStatus.Fitted
        | "Approved" -> Some ModelArtifactStatus.Approved
        | "Retired" -> Some ModelArtifactStatus.Retired
        | _ -> None

    /// The status a fit-outcome registration mints. Every artifact is born
    /// `Fitted` — it carries a completed fit's diagnostics + gate verdicts.
    let initial = ModelArtifactStatus.Fitted

    /// Whether transitioning **into** `target` requires an elevated
    /// (Owner/Admin) role. Only `Approved` does (plan Stage 4 / GP 4); every
    /// other legal transition is available to any team member the storage
    /// scope already admits. Kept as data so the gate is one predicate, not
    /// scattered role matches.
    let requiresElevatedRole (target: ModelArtifactStatus) : bool =
        match target with
        | ModelArtifactStatus.Approved -> true
        | ModelArtifactStatus.Draft
        | ModelArtifactStatus.Fitted
        | ModelArtifactStatus.Retired -> false

    /// Whether `from -> target` is a legal lifecycle edge. The forward
    /// lifecycle:
    ///   Draft    → Fitted | Retired
    ///   Fitted   → Approved | Retired
    ///   Approved → Retired
    ///   Retired  → (terminal)
    /// A self-transition (`from = target`) is never legal — a transition is a
    /// state *change*. Illegality is a typed `ModelRegistryError`, never an
    /// exception.
    let canTransition (from: ModelArtifactStatus) (target: ModelArtifactStatus) : bool =
        match from, target with
        | ModelArtifactStatus.Draft, ModelArtifactStatus.Fitted
        | ModelArtifactStatus.Draft, ModelArtifactStatus.Retired
        | ModelArtifactStatus.Fitted, ModelArtifactStatus.Approved
        | ModelArtifactStatus.Fitted, ModelArtifactStatus.Retired
        | ModelArtifactStatus.Approved, ModelArtifactStatus.Retired -> true
        | _ -> false

/// A fitted model as a governed platform artifact (plan D5 / Stage 4).
/// Identity is the Phase 449 `FitCompositeKey`; the addressable id within a
/// scope is its `Hash`. The immutable fit-derived core (`ArtifactRef`,
/// `Diagnostics`, `GateVerdicts`) is carried verbatim from the fit; only
/// `Status` advances through the lifecycle, each transition recorded as a new
/// immutable version (GP 5). Annotations are the registrar's structured +
/// free-text notes.
type ModelArtifact = {
    /// The composite identity (plan D5). `CompositeKey.Hash` is the artifact's
    /// addressable id within `ScopeId`.
    CompositeKey: FitCompositeKey
    /// Team scope the artifact lives under (GP 4 — structural isolation
    /// inherited from `IDataObjectStore`).
    ScopeId: string
    /// Reference to the opaque fitted-parameter blob (Phase 449). The bytes
    /// live in `IDataObjectStore` (plan D5) — the registry stores the
    /// reference, never the parameters inline (GP 1).
    ArtifactRef: ArtifactRef
    /// Provider-reported diagnostics carried from the fit. Forge stores +
    /// compares; it never interprets them (plan D10).
    Diagnostics: Map<string, float>
    /// Forge's gate verdicts carried from the fit.
    GateVerdicts: GateVerdict list
    /// Current lifecycle state. The only field a transition changes.
    Status: ModelArtifactStatus
    /// Structured, queryable annotations the registrar attached (e.g.
    /// `branch`, `campaign`). Free-form keys — forge assigns no meaning.
    Annotations: Map<string, string>
    /// Free-text note the registrar attached. Empty string when none.
    Notes: string
    /// Actor who registered the artifact.
    RegisteredBy: string
    /// When the artifact was first registered (v1). Preserved verbatim across
    /// lifecycle versions.
    RegisteredAt: DateTimeOffset
    /// The `IDataObjectStore` version of this artifact record. `1` at
    /// registration; each lifecycle transition appends a version (GP 5).
    Version: int
}

module ModelArtifact =
    /// The artifact's addressable id within its scope — the composite-key
    /// hash (plan D5). Every query + `Get` + `TransitionStatus` keys on this.
    let id (artifact: ModelArtifact) : string = artifact.CompositeKey.Hash

/// Typed failures from `IModelRegistry`. A closed DU so callers pattern-match
/// on cases rather than parse messages (mirrors `DataObjectError` /
/// `DatasetError`).
[<RequireQualifiedAccess>]
type ModelRegistryError =
    /// No artifact with the given composite-key hash exists in the scope.
    | NotFound
    /// A `TransitionStatus` to `Approved` was attempted by a caller lacking
    /// the Owner/Admin role (GP 4). Carries the human-readable reason.
    | Forbidden of reason: string
    /// A `TransitionStatus` requested an edge the lifecycle graph forbids
    /// (including a self-transition). Carries the attempted edge.
    | IllegalTransition of from: ModelArtifactStatus * target: ModelArtifactStatus
    /// Underlying storage failure. Message is the raw error; not stable, only
    /// useful for diagnostics.
    | StorageFailure of string
    /// Phase 599 — a `QueryPage` call was malformed (non-positive limit).
    /// Carries the human-readable reason.
    | InvalidQuery of reason: string

module ModelRegistryError =
    /// Human-readable one-line description for logs + error surfaces.
    let describe =
        function
        | ModelRegistryError.NotFound -> "model artifact not found"
        | ModelRegistryError.Forbidden reason -> $"model artifact transition forbidden: {reason}"
        | ModelRegistryError.IllegalTransition(from, target) ->
            $"illegal model artifact transition: {ModelArtifactStatus.name from} → {ModelArtifactStatus.name target}"
        | ModelRegistryError.StorageFailure r -> $"model registry storage failure: {r}"
        | ModelRegistryError.InvalidQuery r -> $"invalid model registry query: {r}"