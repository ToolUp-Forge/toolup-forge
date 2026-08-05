// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 600 — model-execution submitter wire surface ─────────────────
//
// Fable-safe wire records for the out-of-process submitter face of the
// model-execution envelope (fit submission, outcome retrieval, dataset
// resolution, scoring, registry query) plus the closed typed-refusal DU.
// Mirrors the in-process server types **as data** (strings + records —
// the `DatasetTypes.fs` precedent) without leaking server-only
// dependencies into the client assembly: the SHA-addressed server types
// (`FitCompositeKey`, `ModelArtifact`) stay server-side; this file
// carries their value projections.
//
// **SpecHash is submitter-minted and opaque (interface-plan D4).** The
// submission carries the submitter's own `SpecHash` alongside the opaque
// payload; forge stores and keys exactly the hash it was handed — it
// never re-derives, normalises, or validates it against the payload
// (the Phase 603 opacity posture). Handlers MUST NOT re-hash.
//
// **Scope is never wire-supplied.** Every method executes under the
// caller's resolved scope (GP 4) — a submission cannot name another
// team's scope; the handler overwrites any ambient scope assumption the
// caller makes (the `JobApi` anti-impersonation precedent).

/// One requested diagnostic gate, direction as its stable case-name
/// string (`"AtLeast"` / `"AtMost"` — `GateDirection.name`).
type ModelExecutionGate = {
    Name: string
    Threshold: float
    Direction: string
}

/// A single fit submission: which vintage of which dataset (in the
/// caller's scope), the opaque provider spec + the submitter-minted spec
/// hash, the provider kind, the reproducibility seed, and the gates to
/// evaluate.
type ModelExecutionFitSubmission = {
    DatasetId: string
    DatasetVersion: int
    /// Opaque provider payload — forge stores it, never inspects it (GP 1).
    SpecPayload: string
    /// Submitter-minted content hash of the payload under the submitter's
    /// own canonicalisation rule. Opaque to forge (never re-derived).
    SpecHash: string
    ProviderKind: string
    Seed: int64
    Gates: ModelExecutionGate list
    /// Phase 451 (interface-plan decision D5) — who asked for this fit.
    /// Declared by the submitter and used only by compute-budget policy,
    /// which can gate agent-driven exploration harder than interactive
    /// use. Forge never infers it and never acts on it beyond the budget
    /// check, so a deployment with no budget composed is unaffected by
    /// what it says.
    SubmitterClass: SubmitterClass
}

/// An ordered batch of fit submissions under one caller-supplied
/// correlation id (the Phase 599 batch shape on the wire).
type ModelExecutionBatchSubmission = {
    BatchId: string
    Items: ModelExecutionFitSubmission list
}

/// One enqueued item's job reference — the poll handle for its fit run.
type ModelExecutionJobRef = { Index: int; JobId: Guid }

/// The receipt of an accepted submission: which items were enqueued
/// (with job refs) and which failed to enqueue, as data. Long fits
/// follow submit → receipt → poll (`QueryOutcomes` / `GetOutcome`) — no
/// long-held connections.
type ModelExecutionReceipt = {
    BatchId: string
    ItemCount: int
    Jobs: ModelExecutionJobRef list
    /// `(index, reason)` per item whose enqueue failed — partial
    /// acceptance is data, never an exception.
    EnqueueFailures: (int * string) list
}

/// A gate verdict as data (the fit envelope's `GateVerdict` projection).
type ModelExecutionGateVerdict = {
    Name: string
    Threshold: float
    Direction: string
    Observed: float
    Passed: bool
}

/// One registered fit outcome (the registry artifact's wire projection).
/// `CompositeKeyHash` is the addressable id for `GetOutcome`.
type ModelExecutionOutcome = {
    CompositeKeyHash: string
    SpecHash: string
    /// `{scopeId}/{datasetId}@v{version}` key of the vintage the fit read.
    DatasetVersion: string
    Seed: int64
    ProviderId: string
    ProviderVersion: string
    ArtifactId: string
    ArtifactContentHash: string
    Diagnostics: Map<string, float>
    GateVerdicts: ModelExecutionGateVerdict list
    /// Lifecycle status as its stable case-name string
    /// (`ModelArtifactStatus.name`).
    Status: string
    Annotations: Map<string, string>
    RegisteredAt: DateTimeOffset
}

/// Multi-key outcome filter (conjunctive; empty list / `None` = any) —
/// the Phase 599 `ModelRegistryQuery` on the wire. Statuses are stable
/// case-name strings.
type ModelExecutionOutcomeQuery = {
    SpecHashes: string list
    DatasetVersions: string list
    Statuses: string list
    BatchId: string option
}

/// One page of a cursor-paginated outcome read (deterministic ordering —
/// composite-key hash, ordinal ascending).
type ModelExecutionOutcomePage = {
    Outcomes: ModelExecutionOutcome list
    NextCursor: string option
}

/// A scoring request: a registered artifact (by composite-key hash)
/// against an input vintage, predictions landing under `OutputDatasetId`
/// in the caller's scope.
type ModelExecutionScoreRequest = {
    ArtifactKeyHash: string
    InputDatasetId: string
    InputVersion: int
    OutputDatasetId: string
}

/// A dataset version's resolution info — enough for a submitter to pin a
/// vintage and for a worker to fetch the content blob (format-honest
/// handoff, the Phase 448 `DatasetContentRef` projection).
type ModelExecutionDatasetVersion = {
    DatasetId: string
    Version: int
    RowCount: int64
    Format: string
    ContentHash: string
    CreatedAt: DateTimeOffset
}

/// A scoring refusal, mirroring the server's `ScoreError` cases as wire
/// data so a client enumerates the refusal class without string-matching.
[<RequireQualifiedAccess>]
type ModelExecutionScoreRefusal =
    | ProviderNotFound of kind: string
    | NotApproved of status: string
    | InputSchemaMismatch of reason: string
    | InputUnavailable of reason: string
    | ProviderFailed of kind: string * message: string
    | StorageFailure of reason: string

/// The closed typed-refusal DU for the submitter surface (interface-plan
/// D6): every denial an endpoint can produce is one of these cases — a
/// client enumerates what was refused and why without string-matching,
/// and no endpoint returns a bare exception (`Unexpected` is the proven
/// catch-all mapping).
[<RequireQualifiedAccess>]
type ModelExecutionRefusal =
    /// The named substrate is not composed in this deployment (model
    /// fitting / job scheduler / registry / datasets / scorer).
    | SubstrateDisabled of surface: string
    /// The caller has no persistent scope (anonymous without a session
    /// scope) — model execution requires one (GP 4).
    | ScopeUnavailable
    /// The caller's role does not permit the operation (Owner/Admin write
    /// gate in team modes).
    | Forbidden of reason: string
    /// No fit provider is registered for the submission's kind.
    | UnknownProvider of kind: string
    /// The submission failed validation (empty batch, missing batch id,
    /// malformed gate direction, …). Denied before any work.
    | InvalidSubmission of reason: string
    /// No outcome / artifact / dataset with the given key in the caller's
    /// scope.
    | NotFound of what: string
    /// A malformed query (non-positive limit, unknown status tag).
    | InvalidQuery of reason: string
    /// The scoring envelope refused, with the typed scoring reason.
    | ScoreRefused of ModelExecutionScoreRefusal
    /// Phase 451 — the caller's scope has no compute budget left for this
    /// submission, or the submission exceeds a per-class ceiling. Denied
    /// before any work is enqueued; the payload names which ceiling, what
    /// the quota is, and what has been spent, so a submitter (very often an
    /// agent deciding whether to narrow its search) can act on the refusal
    /// rather than merely observe it.
    | BudgetDenied of ComputeBudgetDenial
    /// Underlying storage failure (diagnostic, not stable).
    | StorageFailure of reason: string
    /// An unmapped server-side exception, surfaced as data — the
    /// contract-proven "no bare exception on the wire" backstop.
    | Unexpected of message: string

module ModelExecutionRefusal =
    /// Human-readable one-line description (logs + client display; the
    /// case, not this string, is the contract).
    let describe =
        function
        | ModelExecutionRefusal.SubstrateDisabled s -> $"substrate not composed in this deployment: {s}"
        | ModelExecutionRefusal.ScopeUnavailable ->
            "model execution requires a persistent scope (sign in or join a team)"
        | ModelExecutionRefusal.Forbidden r -> $"forbidden: {r}"
        | ModelExecutionRefusal.UnknownProvider k -> $"no model-fit provider registered for kind '{k}'"
        | ModelExecutionRefusal.InvalidSubmission r -> $"invalid submission: {r}"
        | ModelExecutionRefusal.NotFound w -> $"not found: {w}"
        | ModelExecutionRefusal.InvalidQuery r -> $"invalid query: {r}"
        | ModelExecutionRefusal.ScoreRefused _ -> "scoring refused (see typed reason)"
        | ModelExecutionRefusal.BudgetDenied d -> ComputeBudgetDenial.describe d
        | ModelExecutionRefusal.StorageFailure r -> $"storage failure: {r}"
        | ModelExecutionRefusal.Unexpected m -> $"unexpected server failure: {m}"

/// The authenticated remoting surface for out-of-process submitters
/// (Phase 600). Every method is team-scoped through the caller's
/// resolved `AccessContext` (GP 4) and audited with the submitter
/// identity (GP 6); mutating methods carry the Owner/Admin write gate in
/// team modes. Dispatcher-anonymous like `JobApi` — the handler enforces
/// scope + role server-side, and single-user modes own their scope.
type ModelExecutionApi = {
    /// Submit one fit (the degenerate batch — same semantics as a
    /// batch of one).
    [<AllowAnonymous>]
    SubmitFit: ModelExecutionFitSubmission -> Async<Result<ModelExecutionReceipt, ModelExecutionRefusal>>

    /// Submit an ordered batch of fits under one correlation id.
    [<AllowAnonymous>]
    SubmitFitBatch: ModelExecutionBatchSubmission -> Async<Result<ModelExecutionReceipt, ModelExecutionRefusal>>

    /// Fetch one registered outcome by composite-key hash.
    [<AllowAnonymous>]
    GetOutcome: string -> Async<Result<ModelExecutionOutcome, ModelExecutionRefusal>>

    /// Cursor-paginated multi-key outcome read (poll a whole wave in one
    /// call). Arguments: query, cursor (from a previous page), limit.
    [<AllowAnonymous>]
    QueryOutcomes:
        ModelExecutionOutcomeQuery * string option * int
            -> Async<Result<ModelExecutionOutcomePage, ModelExecutionRefusal>>

    /// Resolve a dataset's latest version in the caller's scope (pin a
    /// vintage before submitting).
    [<AllowAnonymous>]
    ResolveLatestDatasetVersion: string -> Async<Result<ModelExecutionDatasetVersion, ModelExecutionRefusal>>

    /// Resolve one specific dataset version in the caller's scope.
    [<AllowAnonymous>]
    ResolveDatasetVersion: string * int -> Async<Result<ModelExecutionDatasetVersion, ModelExecutionRefusal>>

    /// Score a registered artifact against an input vintage; predictions
    /// land as a new dataset version in the caller's scope. Synchronous
    /// small-frame path (the job-shaped path is the scorer's own batch
    /// handler).
    [<AllowAnonymous>]
    RequestScore: ModelExecutionScoreRequest -> Async<Result<ModelExecutionDatasetVersion, ModelExecutionRefusal>>
}