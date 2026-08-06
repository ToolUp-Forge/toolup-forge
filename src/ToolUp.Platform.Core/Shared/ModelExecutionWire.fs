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
//
// ─── Phase 640 — closing the certified carry gaps ───────────────────────
//
// The conformance harness that certifies this face against the external
// model-execution corpus records its carry gaps as an explicit residue
// per family, so what this face cannot express is inventoried rather than
// implied. This phase closes five of them, and the *shape* of each fix is
// chosen so the gap cannot silently reopen:
//
//   * **Identity that is not ours to shape (the opaque handle).** A
//     submission receipt's job handle is now a `string`. Every `Guid` is a valid
//     handle; not every handle is a `Guid`, so typing the field as one
//     made the losing direction *inbound* — a conformant executor whose
//     handles are ULIDs, or opaque cursors, or anything else, could not be
//     represented on this face at all.
//   * **Denials a program can branch on.** An enqueue failure is now a
//     typed refusal rather than a diagnostic string, and the refusal DU
//     carries the four classes it was missing. A consumer that has to
//     string-match a denial cannot branch on it, which is the whole reason
//     the vocabulary is closed and enumerable.
//   * **Absence that is distinguishable from emptiness.** The outcome's
//     artifact reference is optional, so "no retained artifact" (a refused
//     or failed run, recorded because a failure is still evidence) is no
//     longer the same value as "an artifact with empty identifiers".
//   * **Facts that were simply not carried.** Timing, cost, and the spec
//     hash's minting rule.

/// One requested diagnostic gate, direction as its stable case-name
/// string (`"AtLeast"` / `"AtMost"` — `GateDirection.name`).
type ModelExecutionGate = {
    Name: string
    Threshold: float
    Direction: string
}

/// A gate verdict as data (the fit envelope's `GateVerdict` projection).
type ModelExecutionGateVerdict = {
    Name: string
    Threshold: float
    Direction: string
    Observed: float
    Passed: bool
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
    /// The identifier of the minting rule the submitter used to produce
    /// `SpecHash`.
    ///
    /// **Stored verbatim; never validated, and the opacity posture is
    /// unchanged by carrying it.** Forge does not re-derive the hash under
    /// the named rule, does not check the name against any registry, and
    /// does not refuse an unrecognised one — a submitter whose rendering is
    /// outside a registered rule's domain names its own identifier, and
    /// interop is then bounded by agreement between submitters rather than
    /// by forge. The field exists because a receiver that stores a hash
    /// opaquely still needs a rotation to be *visible*: without it, two
    /// hashes minted under different rules are indistinguishable on this
    /// face, and the day a submitter changes rule is the day its whole
    /// history silently stops joining anything. Empty means the submitter
    /// stated no rule, which is exactly as conformant as naming one.
    SpecHashAlgorithm: string
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
///
/// **The vocabulary is closed for interpretation and open for addition.**
/// A case may be added (Phase 640 added four); a case is never removed and
/// never has its meaning changed, because removing one turns a handled
/// condition into an unhandled one at every consumer that was branching
/// on it.
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
    ///
    /// This is a *budget* refusal and not a `PolicyRefused` one, and the
    /// distinction is load-bearing rather than taxonomic: a budget denial
    /// carries the quota and the spend, so the recipient can compute what
    /// would fit, whereas a policy refusal carries a rule identifier and
    /// nothing to arithmetic against. Collapsing the two would throw away
    /// the only numbers that make the refusal actionable.
    | BudgetDenied of ComputeBudgetDenial
    /// Phase 640 — the peer declared a profile version this deployment does
    /// not read. Carries what arrived and the versions that are accepted,
    /// so the caller can re-aim without a second round trip.
    | EnvelopeVersionMismatch of received: int * accepted: int list
    /// Phase 640 — the caller named a document shape / operation this
    /// participant does not implement. `known` is the set it does, for the
    /// same reason `UnknownProvider` carries one.
    | UnknownDocumentKind of kind: string * known: string list
    /// Phase 640 — work completed but did not clear the gates its submitter
    /// declared, and this deployment's policy is to refuse rather than hand
    /// the artifact on. Carries the verdicts, so the caller need not
    /// re-query to learn which gate failed and by how much.
    ///
    /// Note what this case does **not** mean: forge still *registers* a
    /// gate-failed fit (a failed gate is a typed verdict, not an error —
    /// Phase 449's standing acceptance), because a failure is evidence and
    /// deleting it would make the evidence base a survivorship sample. This
    /// refusal governs what a submitter is handed downstream of that, and
    /// only where a deployment opted in (`ModelExecutionPolicy`).
    | GateFailed of verdicts: ModelExecutionGateVerdict list
    /// Phase 640 — a named policy refused the request. The rule's
    /// identifier is stable and is the thing to branch on; any wording that
    /// accompanies it is diagnostic.
    | PolicyRefused of rule: string
    /// Underlying storage failure (diagnostic, not stable).
    | StorageFailure of reason: string
    /// An unmapped server-side exception, surfaced as data — the
    /// contract-proven "no bare exception on the wire" backstop.
    | Unexpected of message: string

module ModelExecutionRefusal =
    /// Bound rather than inlined: F# interpolation holes cannot contain a
    /// double-quoted literal.
    let private separator = ", "

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
        | ModelExecutionRefusal.EnvelopeVersionMismatch(received, accepted) ->
            let acceptedText = accepted |> List.map string |> String.concat separator

            $"envelope version {received} is not read here (accepted: {acceptedText})"
        | ModelExecutionRefusal.UnknownDocumentKind(kind, known) ->
            let knownText = known |> String.concat separator

            $"document kind '{kind}' is not implemented here (known: {knownText})"
        | ModelExecutionRefusal.GateFailed verdicts ->
            let failedText =
                verdicts
                |> List.filter (fun v -> not v.Passed)
                |> List.map _.Name
                |> String.concat separator

            $"gates did not pass: {failedText}"
        | ModelExecutionRefusal.PolicyRefused rule -> $"refused by policy rule '{rule}'"
        | ModelExecutionRefusal.StorageFailure r -> $"storage failure: {r}"
        | ModelExecutionRefusal.Unexpected m -> $"unexpected server failure: {m}"

/// One enqueued item's job reference — the poll handle for its fit run.
///
/// **`JobId` is an opaque string, and deliberately not the `Guid` the
/// in-process scheduler happens to mint.** A handle is an identity this
/// face relays, not one it owns: every `Guid` renders as a valid handle,
/// but a conformant executor whose handles are not `Guid`s cannot be
/// represented at all if the field is typed as one — so the direction that
/// lost was inbound, which is the one that matters for a wire surface.
/// Callers treat it as bytes: compare it, echo it, poll with it, never
/// parse it.
type ModelExecutionJobRef = { Index: int; JobId: string }

/// The receipt of an accepted submission: which items were enqueued
/// (with job refs) and which failed to enqueue, as data. Long fits
/// follow submit → receipt → poll (`QueryOutcomes` / `GetOutcome`) — no
/// long-held connections.
type ModelExecutionReceipt = {
    BatchId: string
    ItemCount: int
    Jobs: ModelExecutionJobRef list
    /// `(index, refusal)` per item whose enqueue failed — partial
    /// acceptance is data, never an exception, and the reason is the same
    /// typed vocabulary every other denial on this face uses. It was a
    /// diagnostic string until Phase 640, which meant the one refusal a
    /// caller most needs to act on programmatically (which items of my wave
    /// did not start?) was the one it had to string-match.
    EnqueueFailures: (int * ModelExecutionRefusal) list
}

/// A reference to the stored artifact a fit produced (the Phase 449
/// `ArtifactRef` projection). Optional on an outcome — see
/// `ModelExecutionOutcome.Artifact`.
type ModelExecutionArtifactRef = {
    ArtifactId: string
    ContentHash: string
    /// A registered format identifier where the executor declares one.
    /// `None` is conformant: a format is a label on a reference, and a
    /// party relaying a reference it does not itself open has nothing to
    /// declare.
    Format: string option
}

/// When a fit run passed through its lifecycle. Every member may be
/// absent on an outcome that never ran; `SubmittedAt` is the one point
/// that always exists, because an outcome exists only because something
/// was submitted.
type ModelExecutionTiming = {
    SubmittedAt: DateTimeOffset
    StartedAt: DateTimeOffset option
    CompletedAt: DateTimeOffset option
    DurationMs: int64 option
}

/// What a fit run cost, in the executor's own accounting unit. The unit
/// is uninterpreted here — forge stores and relays it, and assigns it no
/// meaning (GP 1).
type ModelExecutionCost = { Unit: string; Amount: float }

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
    /// The fitted artifact, or `None` when the run produced no retained
    /// artifact — a refused or failed run, recorded because a failure is
    /// still evidence. Before Phase 640 these were two non-optional
    /// strings, so "nothing was retained" and "something was retained under
    /// empty identifiers" were the same value, and no consumer could tell
    /// an absent artifact from a malformed one.
    Artifact: ModelExecutionArtifactRef option
    Diagnostics: Map<string, float>
    GateVerdicts: ModelExecutionGateVerdict list
    /// Lifecycle status as its stable case-name string
    /// (`ModelArtifactStatus.name`).
    Status: string
    Timing: ModelExecutionTiming
    /// `None` where the executor does not account for cost.
    Cost: ModelExecutionCost option
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

/// Phase 640 — opt-in executor policy for the submitter face.
///
/// Resolved from DI per request like every other substrate this face uses,
/// so a deployment that registers nothing gets the permissive value and is
/// byte-for-byte unchanged (GP 11 + GP 13). It is a record rather than a
/// callback for the usual reason (GP 12 rule 3): the same value can be
/// logged, shown on an admin surface, and compared between deployments.
type ModelExecutionPolicy = {
    /// When `true`, a score request naming an artifact whose recorded gate
    /// verdicts contain a failure is refused with `GateFailed` rather than
    /// scored.
    ///
    /// Default `false` — and the default is not timidity. Forge's standing
    /// position is that a gate is a *verdict*, not a judgement of model
    /// quality, so refusing to act on a failed one is a governance choice a
    /// deployment makes, not a correctness property forge asserts. A
    /// deployment that promotes a gate-failed artifact to `Approved` has
    /// said something, and forge does not overrule it uninvited.
    RefuseGateFailedArtifacts: bool
}

module ModelExecutionPolicy =
    /// The value an unconfigured deployment gets: nothing is refused that
    /// was not refused before this policy existed.
    let permissive: ModelExecutionPolicy = { RefuseGateFailedArtifacts = false }

    /// Gate-governed: a score request against an artifact that failed its
    /// declared gates is refused.
    let refuseGateFailures: ModelExecutionPolicy = { RefuseGateFailedArtifacts = true }

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