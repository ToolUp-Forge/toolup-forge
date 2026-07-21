// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Security.Cryptography

// ─── Phase 449 — Model-fit envelope core types ──────────────────────────
//
// The neutral fit contract (statistical-modelling substrate plan, Stage
// 2a): `(datasetVersion, opaque spec, seed, gates)` →
// `(artifact, diagnostics, gate verdicts)`. Forge stores and compares; it
// never interprets a diagnostic or judges model quality (plan D10). All
// modelling math lives behind `IModelFitProvider` — these types carry no
// estimator or domain vocabulary (GP 1).
//
// **Why server-only, not Core/Shared.** A dataset (Phase 448) is a
// client-renderable surface, so its types sit in `Platform.Core` under
// `fable/`. A fit run is a server-side compute envelope with no client
// view and no ToolUp.Remoting API in this phase, and its identity is
// SHA-256-addressed — `System.Security.Cryptography` is not Fable-
// compilable, so these types deliberately live server-side (the phase
// key-files named a `Server/Shared/` folder that does not exist; the
// nearest correct home is `Server/` alongside the interface). The audit
// payloads that *do* cross into persisted `ModuleEvent`s stay in
// `Core/Shared/AuditTypes.fs`; they carry only the composite-key strings
// forge computes here.
//
// **Seed + gates are typed fields (plan D4).** A seeded fit is
// reproducible-to-tolerance; a convergence gate is deterministic pass/fail
// *data*, evaluated by forge as a float comparison — never an exception,
// never a judgement.

/// Opaque, provider-interpreted model specification. Forge never parses
/// the payload — it stores the bytes-as-string and computes a content hash
/// for identity + dedup (plan D5). No estimator / domain vocabulary leaks
/// through this boundary (GP 1).
type ModelSpecRef = {
    /// Opaque provider payload — forge stores + hashes it, never inspects.
    Payload: string
    /// Lowercase SHA-256 hex of `Payload` (UTF-8). Part of the composite
    /// identity; two specs with identical bytes share a hash.
    SpecHash: string
}

module ModelSpecRef =
    /// Lowercase SHA-256 hex of a spec payload's UTF-8 bytes.
    let hashOf (payload: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes payload) |> Convert.ToHexStringLower

    /// Wrap an opaque payload, computing its `SpecHash`. The canonical
    /// constructor — callers never set `SpecHash` by hand.
    let ofPayload (payload: string) : ModelSpecRef = {
        Payload = payload
        SpecHash = hashOf payload
    }

/// Value reference to an immutable dataset vintage a fit reads against
/// (Phase 448). Identity by value (GP 12 rule 1) — scope + id + version,
/// never a live store handle. Kept distinct from Phase 448's
/// `DatasetVersion` (the full metadata record) so the fit envelope stays
/// decoupled from the dataset store's metadata shape.
type DatasetVersionRef = {
    ScopeId: string
    DatasetId: string
    Version: int
}

module DatasetVersionRef =
    /// Stable identity token for the vintage — the value that enters the
    /// composite key. `{scopeId}/{datasetId}@v{version}`.
    let key (r: DatasetVersionRef) : string =
        sprintf "%s/%s@v%d" r.ScopeId r.DatasetId r.Version

/// Direction of a diagnostic gate's threshold comparison. A gate is a
/// pure float comparison forge evaluates — never a judgement of model
/// quality (plan D10).
[<RequireQualifiedAccess>]
type GateDirection =
    /// Passes when the observed diagnostic is `>=` the threshold (e.g. a
    /// minimum acceptable score).
    | AtLeast
    /// Passes when the observed diagnostic is `<=` the threshold (e.g. a
    /// maximum acceptable error).
    | AtMost

module GateDirection =
    /// Stable case-name string (audit payloads, telemetry). Round-trips
    /// through `parse`.
    let name =
        function
        | GateDirection.AtLeast -> "AtLeast"
        | GateDirection.AtMost -> "AtMost"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "AtLeast" -> Some GateDirection.AtLeast
        | "AtMost" -> Some GateDirection.AtMost
        | _ -> None

/// A provider-declared convergence / quality gate: a named diagnostic, a
/// threshold, and a direction. A `FitRequest` carries the gates to
/// evaluate for the run; forge compares floats and assigns no meaning.
type GateSpec = {
    /// Diagnostic key the gate reads from `FitOutcome.Diagnostics`.
    Name: string
    Threshold: float
    Direction: GateDirection
}

/// The typed, data verdict of one gate — never an exception (acceptance:
/// a failed gate is a typed, audited verdict). `Observed` is the
/// diagnostic value the provider reported; `nan` when the provider did
/// not report the named diagnostic (which fails the gate closed).
type GateVerdict = {
    Name: string
    Threshold: float
    Direction: GateDirection
    Observed: float
    /// `true` iff the provider reported the diagnostic and it satisfied
    /// the threshold in the declared direction.
    Passed: bool
}

module Gate =
    /// Evaluate one gate against a diagnostics map. A missing diagnostic is
    /// a fail-closed verdict (`Observed = nan`, `Passed = false`) — a gate
    /// forge cannot confirm is never silently passed (GP 6).
    let evaluate (diagnostics: Map<string, float>) (spec: GateSpec) : GateVerdict =
        match Map.tryFind spec.Name diagnostics with
        | None -> {
            Name = spec.Name
            Threshold = spec.Threshold
            Direction = spec.Direction
            Observed = nan
            Passed = false
          }
        | Some observed ->
            let passed =
                match spec.Direction with
                | GateDirection.AtLeast -> observed >= spec.Threshold
                | GateDirection.AtMost -> observed <= spec.Threshold

            {
                Name = spec.Name
                Threshold = spec.Threshold
                Direction = spec.Direction
                Observed = observed
                Passed = passed
            }

    /// Evaluate every requested gate against the provider's diagnostics,
    /// preserving request order.
    let evaluateAll (diagnostics: Map<string, float>) (specs: GateSpec list) : GateVerdict list =
        specs |> List.map (evaluate diagnostics)

/// The composite identity of a fit run (plan D5): everything that makes a
/// fitted artifact reproducible. Two runs with an equal canonical form are
/// the same fit; `Hash` is the addressable form that travels with the
/// outcome and every audit row. Timing / cost are **not** part of identity
/// — a re-run reproduces the artifact, not the clock.
type FitCompositeKey = {
    SpecHash: string
    /// `DatasetVersionRef.key` of the vintage the fit read.
    DatasetVersion: string
    Seed: int64
    /// The resolved provider's `Kind`.
    ProviderId: string
    ProviderVersion: string
    /// Lowercase SHA-256 hex over the canonical identity tuple.
    Hash: string
}

module FitCompositeKey =
    /// Canonical, order-fixed string form of the identity tuple. Stable —
    /// the hash is computed over exactly this shape, so do not reorder.
    let canonical
        (specHash: string)
        (datasetVersion: string)
        (seed: int64)
        (providerId: string)
        (providerVersion: string)
        : string =
        sprintf "spec=%s|dataset=%s|seed=%d|provider=%s|pver=%s" specHash datasetVersion seed providerId providerVersion

    /// Compute the composite key + its hash from the identity components.
    let compute
        (specHash: string)
        (datasetVersion: string)
        (seed: int64)
        (providerId: string)
        (providerVersion: string)
        : FitCompositeKey =
        let c = canonical specHash datasetVersion seed providerId providerVersion

        {
            SpecHash = specHash
            DatasetVersion = datasetVersion
            Seed = seed
            ProviderId = providerId
            ProviderVersion = providerVersion
            Hash = SHA256.HashData(Encoding.UTF8.GetBytes c) |> Convert.ToHexStringLower
        }

/// Value reference to a fitted artifact the provider produced. Opaque to
/// forge (GP 1) — an id + content hash + size, never the artifact bytes
/// inline. A production provider persists the bytes (e.g. to blob storage)
/// and returns this reference; the trivial reference provider derives it
/// deterministically from the request identity.
type ArtifactRef = {
    ArtifactId: string
    ContentHash: string
    ByteLength: int64
}

/// A neutral request to fit a model: which vintage, which opaque spec,
/// which provider, the seed, and the gates to evaluate. Everything a
/// seeded, reproducible fit needs (plan D4) — and nothing domain-specific.
type FitRequest = {
    /// Scope the fit runs under (GP 4 — structural isolation).
    ScopeId: string
    /// The immutable dataset vintage the fit reads (Phase 448).
    DatasetVersion: DatasetVersionRef
    /// Opaque provider spec + its content hash.
    SpecRef: ModelSpecRef
    /// Which registered provider `Kind` executes the fit.
    ProviderKind: string
    /// Seed making the fit reproducible-to-tolerance (plan D4). Part of the
    /// composite identity.
    Seed: int64
    /// Gates to evaluate against the provider's reported diagnostics.
    Gates: GateSpec list
}

/// The outcome of a fit run: the artifact reference, the provider's
/// reported diagnostics, forge's gate verdicts, the composite identity,
/// and provider-reported (deterministic) timing + cost. Forge owns
/// `CompositeKey` + `GateVerdicts`; the provider owns `ArtifactRef` +
/// `Diagnostics` + `DurationMs` + `CostUnits`. A deterministic provider's
/// outcome is byte-identical across re-runs of the same seeded request —
/// so `DurationMs` / `CostUnits` are the provider's own deterministic
/// self-report, never wall-clock (which would break reproducibility).
type FitOutcome = {
    CompositeKey: FitCompositeKey
    ArtifactRef: ArtifactRef
    /// Provider-reported diagnostics (name → float). Forge stores + compares
    /// them; it never interprets them (plan D10).
    Diagnostics: Map<string, float>
    /// Forge's typed verdict per requested gate.
    GateVerdicts: GateVerdict list
    /// Provider-reported compute duration (deterministic — NOT wall-clock).
    DurationMs: int64
    /// Provider-reported cost units (deterministic). `0.0` for the trivial
    /// reference provider.
    CostUnits: float
}

module FitOutcome =
    /// `true` iff every requested gate passed (vacuously true when no gate
    /// was requested).
    let gatesPassed (o: FitOutcome) : bool =
        o.GateVerdicts |> List.forall (fun v -> v.Passed)

    /// The subset of verdicts that failed — the payload of a
    /// `ModelFitGateFailed` audit row.
    let failedGates (o: FitOutcome) : GateVerdict list =
        o.GateVerdicts |> List.filter (fun v -> not v.Passed)

/// Typed failure of the fit *envelope*. A failed gate is **not** a
/// `ModelFitError` — it is a `GateVerdict` carried in the outcome
/// (acceptance: a failed gate is a typed, audited verdict, not an
/// exception, not a judgement). This DU covers only envelope-level
/// failures: no provider for the kind, or the provider raised.
[<RequireQualifiedAccess>]
type ModelFitError =
    /// No provider is registered for the request's `ProviderKind`. Terminal
    /// — retrying will not register the provider.
    | ProviderNotFound of kind: string
    /// The provider's `Fit` raised. The message is the exception's; the job
    /// layer treats this as retryable (transient).
    | ProviderFailed of kind: string * message: string

module ModelFitError =
    /// Human-readable one-line description for logs + job-result messages.
    let describe =
        function
        | ModelFitError.ProviderNotFound k -> sprintf "no IModelFitProvider registered for kind '%s'" k
        | ModelFitError.ProviderFailed(k, m) -> sprintf "provider '%s' failed: %s" k m

// ─── Phase 599 — batch fit submission types ─────────────────────────────
//
// Fleet-shaped consumers submit hundreds of fits per wave; the batch shape
// makes single-item the degenerate case without changing any envelope
// semantics — per-item seed, gates, composite key, and audit are exactly
// the Phase 449 single-item machinery, fanned out. A batch is **audited as
// a unit** (`ModelFitBatchSubmitted`, one row) and then each item runs
// independently: a per-item failure never aborts sibling items (partial
// completion is the normal case, reported as data).

/// An ordered batch of fit requests under one caller-supplied correlation
/// id. Every item must share the batch's scope (GP 4 — a batch is audited
/// under one scope; a cross-scope batch is refused as data, not fanned
/// out).
type FitRequestBatch = {
    /// Caller-supplied correlation id. Per-item outcomes carry it in their
    /// registration annotations (`FitRequestBatch.BatchIdAnnotationKey`),
    /// which is what makes a whole wave retrievable in one query.
    BatchId: string
    /// Scope the batch is audited under. Every item's `ScopeId` must match.
    ScopeId: string
    /// Ordered fit requests. Item index (0-based) is the batch-local
    /// identity a partial-failure report names.
    Requests: FitRequest list
}

/// Typed refusal of a batch *submission* — checked before any item is
/// enqueued (deny early, deny typed; the shape Phase 451's budget refusals
/// will align with). Item-level fit failures are NOT here — they are
/// per-item data reported after execution.
[<RequireQualifiedAccess>]
type FitBatchError =
    /// The batch carries no requests.
    | EmptyBatch
    /// The batch id is null/empty — outcomes would be uncorrelatable.
    | MissingBatchId
    /// Item `index` declares a scope other than the batch's (GP 4).
    | ScopeMismatch of index: int * itemScope: string

module FitBatchError =
    let describe =
        function
        | FitBatchError.EmptyBatch -> "fit batch contains no requests"
        | FitBatchError.MissingBatchId -> "fit batch has no batch id"
        | FitBatchError.ScopeMismatch(i, s) -> sprintf "fit batch item %d declares scope '%s', not the batch scope" i s

module FitRequestBatch =
    /// Registration-annotation key carrying the batch correlation id on
    /// every per-item outcome registered into `IModelRegistry`.
    [<Literal>]
    let BatchIdAnnotationKey = "batch.id"

    /// Registration-annotation key carrying the item's 0-based index
    /// within its batch.
    [<Literal>]
    let BatchIndexAnnotationKey = "batch.index"

    /// Validate a batch for submission: non-empty, correlatable, and
    /// scope-uniform. The first violation is returned (typed, data).
    let validate (batch: FitRequestBatch) : Result<unit, FitBatchError> =
        if String.IsNullOrWhiteSpace batch.BatchId then
            Error FitBatchError.MissingBatchId
        elif List.isEmpty batch.Requests then
            Error FitBatchError.EmptyBatch
        else
            batch.Requests
            |> List.indexed
            |> List.tryPick (fun (i, r) ->
                if r.ScopeId <> batch.ScopeId then
                    Some(FitBatchError.ScopeMismatch(i, r.ScopeId))
                else
                    None)
            |> function
                | Some e -> Error e
                | None -> Ok()