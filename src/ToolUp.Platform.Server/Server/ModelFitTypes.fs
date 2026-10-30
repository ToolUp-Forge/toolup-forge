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
// `Core/Shared/Audit/ModelAuditPayloads.fs`; they carry only the composite-key strings
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
    /// Phase 640 — the identifier of the minting rule that produced
    /// `SpecHash`, where the submitter named one. **Stored verbatim and
    /// never acted on**: forge does not re-derive the hash under it, does
    /// not validate the name, and does not refuse an unrecognised one (the
    /// Phase 603 opacity posture, unchanged by carrying the field).
    ///
    /// It is deliberately **not** part of `FitCompositeKey`. Identity is
    /// the hash, not the rule that produced it; folding the rule into the
    /// key would make the same fit under a renamed-but-identical rule a
    /// different fit, and split the registry for a change that altered
    /// nothing.
    ///
    /// Empty for a locally-minted ref (`ofPayload`) and for a submitter
    /// that stated no rule — the two are the same claim, namely none.
    SpecHashAlgorithm: string
}

module ModelSpecRef =
    /// Lowercase SHA-256 hex of a spec payload's UTF-8 bytes.
    let hashOf (payload: string) : string = Sha256.hexOfUtf8 payload

    /// Wrap an opaque payload, computing its `SpecHash`. The canonical
    /// constructor — callers never set `SpecHash` by hand.
    ///
    /// `SpecHashAlgorithm` is empty here on purpose. This mints a bare
    /// content hash over the payload's bytes, which is not a *canonical*
    /// rendering rule and must not be advertised as one: a submitter
    /// joining on the strength of a named rule would be joining on
    /// something forge never promised.
    let ofPayload (payload: string) : ModelSpecRef = {
        Payload = payload
        SpecHash = hashOf payload
        SpecHashAlgorithm = ""
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

    /// Phase 641 — the inverse of `key`. `None` when the token is not a
    /// dataset-version key. The composite key stores the training vintage as
    /// this token, so a consumer holding an artifact can recover the vintage
    /// it was fitted against without a side channel — which is why the
    /// inverse belongs beside `key` rather than being re-derived (and
    /// re-tested) in every consumer.
    ///
    /// A dataset id may itself contain `/` and `@`, so the parse anchors on
    /// the LAST `@v` and the FIRST `/` — the exact positions `key` writes.
    let tryParseKey (token: string) : DatasetVersionRef option =
        match token.LastIndexOf "@v" with
        | -1 -> None
        | at ->
            let head = token.Substring(0, at)
            let versionText = token.Substring(at + 2)

            match Int32.TryParse versionText, head.IndexOf '/' with
            | (true, version), slash when slash > 0 ->
                Some {
                    ScopeId = head.Substring(0, slash)
                    DatasetId = head.Substring(slash + 1)
                    Version = version
                }
            | _ -> None

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
    /// Phase 1011 — the unit `Threshold` is stated in (unit text), or `None`
    /// where the gate states none. The gate is evaluated only where this is
    /// the unit the provider reports for the diagnostic (`Gate.evaluate`).
    Unit: string option
    Direction: GateDirection
}

/// The typed, data verdict of one gate — never an exception (acceptance:
/// a failed gate is a typed, audited verdict). `Observed` is the
/// diagnostic value the provider reported; `nan` when the provider did
/// not report the named diagnostic (which fails the gate closed).
type GateVerdict = {
    Name: string
    Threshold: float
    /// Phase 1011 — the unit `Threshold` and `Observed` are both in: the
    /// gate's, which is the diagnostic's wherever a verdict exists.
    Unit: string option
    Direction: GateDirection
    Observed: float
    /// `true` iff the provider reported the diagnostic and it satisfied
    /// the threshold in the declared direction.
    Passed: bool
}

/// Phase 1011 — a gate that was NOT evaluated, because its unit is not the
/// unit the provider reports its diagnostic in. `Unit` is the gate's and
/// `Reported` the diagnostic's; either is `None` where that side states no
/// unit. A refusal, not a verdict: a comparison across units is never made,
/// so there is no `Observed` to record and no `Passed` to decide.
type GateUnitMismatch = {
    Name: string
    Unit: string option
    Reported: string option
}

module Gate =
    /// Evaluate one gate against a diagnostics map and the units the provider
    /// reports them in (diagnostic name -> unit text; a diagnostic with no
    /// entry states no unit).
    ///
    /// **The unit rule comes first, and a comparison is never made across
    /// units.** The gate is evaluated only where its `Unit` and the reported
    /// unit are the same text or both absent (`ModelExecutionUnits.comparable`);
    /// otherwise it is refused as a `GateUnitMismatch` and neither value is
    /// converted. A gate stating no unit against a diagnostic that has one is
    /// refused too, because a threshold read in a unit nobody stated is a gate
    /// that can silently pass; so is a gate stating a unit against a
    /// diagnostic the provider did not report, for which no unit was reported.
    ///
    /// Past the unit rule, a missing diagnostic is a fail-closed verdict
    /// (`Observed = nan`, `Passed = false`) — a gate forge cannot confirm is
    /// never silently passed (GP 6).
    let evaluate
        (diagnostics: Map<string, float>)
        (units: Map<string, string>)
        (spec: GateSpec)
        : Result<GateVerdict, GateUnitMismatch> =
        let reported = Map.tryFind spec.Name units

        if not (ModelExecutionUnits.comparable spec.Unit reported) then
            Error {
                Name = spec.Name
                Unit = spec.Unit
                Reported = reported
            }
        else
            let observed, passed =
                match Map.tryFind spec.Name diagnostics with
                | None -> nan, false
                | Some observed ->
                    let passed =
                        match spec.Direction with
                        | GateDirection.AtLeast -> observed >= spec.Threshold
                        | GateDirection.AtMost -> observed <= spec.Threshold

                    observed, passed

            Ok {
                Name = spec.Name
                Threshold = spec.Threshold
                Unit = spec.Unit
                Direction = spec.Direction
                Observed = observed
                Passed = passed
            }

    /// Evaluate every requested gate, preserving request order. The first
    /// gate (in request order) whose unit is not its diagnostic's refuses the
    /// whole evaluation: an outcome is never recorded carrying a verdict over
    /// a unit mismatch, nor with that gate quietly left out.
    let evaluateAll
        (diagnostics: Map<string, float>)
        (units: Map<string, string>)
        (specs: GateSpec list)
        : Result<GateVerdict list, GateUnitMismatch> =
        let rec go acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | spec :: rest ->
                match evaluate diagnostics units spec with
                | Ok verdict -> go (verdict :: acc) rest
                | Error mismatch -> Error mismatch

        go [] specs

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
            Hash = Sha256.hexOfUtf8 c
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
    /// Phase 451 (interface-plan decision D5) — who asked for this fit.
    /// Read only by compute-budget policy, which can hold agent-initiated
    /// exploration to a tighter ceiling than interactive use. Not part of
    /// the fit's composite identity: the same spec fitted on the same
    /// vintage with the same seed is the same model whoever asked for it,
    /// and folding the submitter into the key would fragment the registry
    /// and defeat memoization.
    SubmitterClass: SubmitterClass
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
    /// Phase 1011 — the unit each diagnostic is stated in (name → unit
    /// text), as the provider reports it. A diagnostic with no entry states
    /// no unit, and a provider that states none returns `Map.empty`. Stating
    /// a unit interprets nothing: it says what a number measures, and it is
    /// what `Gate.evaluate` holds a gate's unit to.
    DiagnosticUnits: Map<string, string>
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
/// failures: no provider for the kind, the provider raised, or a requested
/// gate could not be evaluated because its unit is not its diagnostic's.
[<RequireQualifiedAccess>]
type ModelFitError =
    /// No provider is registered for the request's `ProviderKind`. Terminal
    /// — retrying will not register the provider.
    | ProviderNotFound of kind: string
    /// The provider's `Fit` raised. The message is the exception's; the job
    /// layer treats this as retryable (transient).
    | ProviderFailed of kind: string * message: string
    /// Phase 1011 — a requested gate's unit is not the unit the provider
    /// reports its diagnostic in, so the gate was not evaluated and no
    /// outcome is recorded. Terminal — the same request against the same
    /// provider reports the same units.
    | GateUnitMismatch of GateUnitMismatch

module ModelFitError =
    /// Human-readable one-line description for logs + job-result messages.
    let describe =
        function
        | ModelFitError.ProviderNotFound k -> sprintf "no IModelFitProvider registered for kind '%s'" k
        | ModelFitError.ProviderFailed(k, m) -> sprintf "provider '%s' failed: %s" k m
        | ModelFitError.GateUnitMismatch m ->
            sprintf
                "gate '%s' is stated in %s but its diagnostic is reported in %s"
                m.Name
                (ModelExecutionUnits.describe m.Unit)
                (ModelExecutionUnits.describe m.Reported)

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
/// enqueued (deny early, deny typed). Item-level fit failures are NOT here
/// — they are per-item data reported after execution.
[<RequireQualifiedAccess>]
type FitBatchError =
    /// The batch carries no requests.
    | EmptyBatch
    /// The batch id is null/empty — outcomes would be uncorrelatable.
    | MissingBatchId
    /// Item `index` declares a scope other than the batch's (GP 4).
    | ScopeMismatch of index: int * itemScope: string
    /// Phase 451 — the scope's compute budget refused the batch. Denied
    /// before any item is enqueued, so a refused batch costs nothing and
    /// leaves no partially-scheduled residue: the alternative — enqueue
    /// until the budget runs out mid-batch — would produce a wave whose
    /// membership depended on how much allowance happened to be left,
    /// which is not a result anybody can interpret.
    | BudgetDenied of ComputeBudgetDenial

module FitBatchError =
    let describe =
        function
        | FitBatchError.EmptyBatch -> "fit batch contains no requests"
        | FitBatchError.MissingBatchId -> "fit batch has no batch id"
        | FitBatchError.ScopeMismatch(i, s) -> sprintf "fit batch item %d declares scope '%s', not the batch scope" i s
        | FitBatchError.BudgetDenied d -> ComputeBudgetDenial.describe d

/// Phase 640 — why one item of an accepted batch did not reach the queue.
///
/// Typed rather than a diagnostic string, because this is the per-item
/// denial a submitter most needs to act on programmatically: it arrives on
/// a receipt that otherwise says the batch was taken, and "which of my two
/// hundred fits did not start, and can I retry them?" is a question a
/// string cannot answer without a caller parsing prose forge never
/// promised to keep stable.
///
/// The two cases are the two ways the scheduler can decline an item, and
/// they are kept apart because their remedies differ: a refused
/// *registration* is a property of the request or the deployment and will
/// refuse again identically, whereas a refused *trigger* means the job
/// exists and only its immediate start failed.
[<RequireQualifiedAccess>]
type FitEnqueueRefusal =
    /// `IJobScheduler.Schedule` refused the registration, with its own
    /// typed reason.
    | ScheduleRefused of ScheduleError
    /// The item registered but `TriggerOnce` declined to start it. The
    /// reason is the scheduler's own message — diagnostic, not stable.
    | TriggerRefused of reason: string

module FitEnqueueRefusal =
    /// Human-readable one-line description for logs + job-result messages.
    /// The case, not this string, is the contract.
    let describe =
        function
        | FitEnqueueRefusal.ScheduleRefused e ->
            match e with
            | InvalidCron(expr, reason) -> sprintf "invalid cron '%s': %s" expr reason
            | HandlerNotRegistered name -> sprintf "handler '%s' is not registered" name
            | PrecisionUnsupported(supplied, _) -> sprintf "precision %A unsupported" supplied
            | ScheduleError.StorageFailure m -> sprintf "schedule storage failure: %s" m
        | FitEnqueueRefusal.TriggerRefused reason -> sprintf "trigger failed: %s" reason

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

// ─── Phase 652 — evaluation plans: masked + rolling fold families ───────
//
// The Phase 456 harness evaluates one artifact against one holdout split —
// the degenerate single-fold case. A closed `EvaluationPlan` DU widens that
// to two further fold families: `MaskedObservations` (a declared set of
// excluded observations, excluded from the *evaluation*, with the series
// left intact for providers whose feature construction needs contiguity)
// and `RollingOrigin` (a family of train/test windows advancing through the
// observation axis).
//
// **The discipline line is unchanged (plan D10).** Fold *generation
// parameters* are data on the plan; fold *metric values* are provider
// output. The derivation below reads the observation **count** and the plan's
// own parameters — never a row's value — so it enumerates folds without
// computing anything over the data. Every metric, aggregate or per-fold,
// still comes from the provider.
//
// **No migration.** A stored `EvaluationRun` predating this phase carries no
// plan; it reads as `TailHoldout` through `EvaluationPlan.resolve`, and the
// degenerate family has no separate folds at all — its aggregate row *is*
// its single fold. So an existing run, its stored bytes, and every query
// over it are unchanged.

/// How the masked observation set of a `MaskedObservations` fold family is
/// chosen. A seeded selection is **reproducible by construction**: the same
/// parameters and the same observation count re-derive the identical fold
/// set on every re-run, so a fold family is auditable without storing every
/// ordinal.
[<RequireQualifiedAccess>]
type MaskSelection =
    /// The masked observation ordinals are declared verbatim — one fold,
    /// exactly the ordinals given.
    | Declared of ordinals: int list
    /// `Count` ordinals per fold, drawn by a seeded deterministic draw over
    /// the observation axis.
    | SeededRandom of seed: int64 * count: int
    /// `BlockCount` contiguous blocks of `BlockLength` observations per fold,
    /// block starts drawn by the same seeded deterministic draw — the shape a
    /// time-series consumer masks with, so a masked span stays contiguous.
    | SeededBlocked of seed: int64 * blockLength: int * blockCount: int

/// A `MaskedObservations` plan: `FoldCount` folds, each excluding its own
/// masked observation set from the evaluation. **The series is left intact**
/// — forge hands the provider the whole frame plus the fold's mask, and
/// slices nothing, so a provider whose feature construction needs contiguity
/// keeps it.
type MaskedObservationsPlan = {
    /// How each fold's masked set is chosen.
    Selection: MaskSelection
    /// How many folds the family generates. A `Declared` selection is always
    /// one fold; a seeded selection draws a distinct set per fold index.
    FoldCount: int
}

/// A `RollingOrigin` plan: a family of train/test windows advancing through
/// the observation axis. Windows are half-open observation-ordinal ranges;
/// forge derives them from these parameters and the observation count alone.
type RollingOriginPlan = {
    /// Observations in the first training window.
    InitialTrainSize: int
    /// Observations in each test window.
    TestSize: int
    /// How far the origin advances between folds.
    Step: int
    /// How many folds the family generates.
    FoldCount: int
    /// `true` — the training window grows from ordinal 0 (expanding origin);
    /// `false` — it slides at fixed width (rolling window).
    Expanding: bool
}

/// The closed evaluation-plan family (phase 652), carried on the evaluation
/// run. Closed deliberately: a provider declares which kinds it evaluates by
/// name, and an unrecognised kind is a typed refusal — never a silent
/// tail-holdout fallback.
[<RequireQualifiedAccess>]
type EvaluationPlan =
    /// Today's shape and the degenerate case: the whole holdout vintage
    /// evaluated as one tail split. The plan a stored pre-652 run reads as.
    | TailHoldout
    /// Declared / seeded masked-observation folds.
    | MaskedObservations of MaskedObservationsPlan
    /// A rolling-origin window family.
    | RollingOrigin of RollingOriginPlan

/// What one fold evaluates, as observation ordinals over the holdout frame.
/// A descriptor, not a slice — forge never subsets the frames it hands the
/// provider (plan D10 + the contiguity requirement above).
[<RequireQualifiedAccess>]
type FoldWindow =
    /// The whole holdout frame — the degenerate single fold, and the shape of
    /// the aggregate evaluation under every plan.
    | WholeHoldout
    /// Ordinals excluded from this fold's evaluation, ascending.
    | MaskedOrdinals of ordinals: int list
    /// Half-open train `[trainStart, trainEnd)` and test `[testStart, testEnd)`
    /// observation-ordinal ranges.
    | TrainTestWindow of trainStart: int * trainEnd: int * testStart: int * testEnd: int

module FoldWindow =
    /// Stable one-line descriptor stored on the fold outcome, so a query
    /// surfaces a fold's shape without re-deriving its plan.
    let describe =
        function
        | FoldWindow.WholeHoldout -> "whole-holdout"
        | FoldWindow.MaskedOrdinals ordinals -> sprintf "masked[%s]" (ordinals |> List.map string |> String.concat ",")
        | FoldWindow.TrainTestWindow(trainStart, trainEnd, testStart, testEnd) ->
            sprintf "train[%d,%d) test[%d,%d)" trainStart trainEnd testStart testEnd

/// One fold of an evaluation plan's family — identity, ordinal position, and
/// the window/mask it evaluates.
type EvaluationFold = {
    /// Stable id within the run: `aggregate`, or `fold-<index>`.
    FoldId: string
    /// Zero-based fold index; `-1` for the run's aggregate row.
    Index: int
    /// What this fold evaluates.
    Window: FoldWindow
}

module EvaluationFold =
    /// Stable fold id of the run's aggregate row.
    [<Literal>]
    let AggregateFoldId = "aggregate"

    /// The synthetic fold the run's **aggregate** metric row is computed
    /// under: the whole holdout frame, index `-1`. Every plan's aggregate is
    /// evaluated this way — which is exactly why a `TailHoldout` run is
    /// byte-for-byte what it was before this phase.
    let aggregate: EvaluationFold = {
        FoldId = AggregateFoldId
        Index = -1
        Window = FoldWindow.WholeHoldout
    }

    /// A family fold at `index` over `window`.
    let at (index: int) (window: FoldWindow) : EvaluationFold = {
        FoldId = sprintf "fold-%d" index
        Index = index
        Window = window
    }

module EvaluationPlan =
    /// Stable plan-kind name — the token a provider declares support for and
    /// a refusal names. Recognised by `isKnownKind`; do not rename without a
    /// wire-format bump, since a provider's declaration is these strings.
    let kindName =
        function
        | EvaluationPlan.TailHoldout -> "TailHoldout"
        | EvaluationPlan.MaskedObservations _ -> "MaskedObservations"
        | EvaluationPlan.RollingOrigin _ -> "RollingOrigin"

    /// Every plan-kind name, for a provider declaring blanket support.
    let allKindNames = [ "TailHoldout"; "MaskedObservations"; "RollingOrigin" ]

    /// `true` iff `name` is a known plan-kind token.
    let isKnownKind (name: string) = List.contains name allKindNames

    /// The plan of a stored run: `None` — a record written before this phase
    /// — reads as `TailHoldout`. The whole of the no-migration rule.
    let resolve (stored: EvaluationPlan option) : EvaluationPlan =
        defaultArg stored EvaluationPlan.TailHoldout

    /// A stable 64-bit key for `(salt, ordinal)` — SHA-256, so the draw is
    /// identical on every runtime and platform (`System.Random`'s sequence
    /// is not a contract). No RNG, no wall-clock: the seeded draws below are
    /// pure functions of the plan's own parameters.
    let private drawKey (salt: string) (i: int) : uint64 =
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(sprintf "%s|%d" salt i))
        BitConverter.ToUInt64(hash, 0)

    /// `candidates` in a deterministic seeded order — a total order by
    /// `(drawKey, ordinal)`, so ties break on the ordinal and the result is
    /// stable across runs, machines, and list orderings.
    let private seededOrder (salt: string) (candidates: int list) : int list =
        candidates |> List.sortBy (fun i -> drawKey salt i, i)

    /// The masked ordinals of one fold of a masked-observation family.
    let private maskedFold
        (selection: MaskSelection)
        (observationCount: int)
        (foldIndex: int)
        : Result<int list, string> =
        match selection with
        | MaskSelection.Declared ordinals ->
            let outOfRange = ordinals |> List.filter (fun o -> o < 0 || o >= observationCount)

            if not (List.isEmpty outOfRange) then
                Error(
                    sprintf
                        "declared mask ordinal(s) outside [0, %d): %s"
                        observationCount
                        (outOfRange |> List.map string |> String.concat ", ")
                )
            else
                Ok(ordinals |> List.distinct |> List.sort)
        | MaskSelection.SeededRandom(seed, count) ->
            if count < 1 then
                Error "SeededRandom mask count must be at least 1"
            elif count > observationCount then
                Error(sprintf "SeededRandom mask count %d exceeds the %d observation(s)" count observationCount)
            else
                seededOrder (sprintf "maskrandom|%d|%d" seed foldIndex) [ 0 .. observationCount - 1 ]
                |> List.truncate count
                |> List.sort
                |> Ok
        | MaskSelection.SeededBlocked(seed, blockLength, blockCount) ->
            if blockLength < 1 then
                Error "SeededBlocked block length must be at least 1"
            elif blockCount < 1 then
                Error "SeededBlocked block count must be at least 1"
            elif blockLength > observationCount then
                Error(
                    sprintf "SeededBlocked block length %d exceeds the %d observation(s)" blockLength observationCount
                )
            else
                let starts = [ 0 .. observationCount - blockLength ]

                seededOrder (sprintf "maskblocked|%d|%d" seed foldIndex) starts
                |> List.truncate blockCount
                |> List.collect (fun s -> [ s .. s + blockLength - 1 ])
                |> List.distinct
                |> List.sort
                |> Ok

    /// The fold family a plan generates over `observationCount` holdout
    /// observations — **enumeration, not statistics**: the row count and the
    /// plan's declared parameters are the only inputs.
    ///
    /// `TailHoldout` returns an EMPTY family: its aggregate row *is* its
    /// single fold, which is why a pre-652 run needs no migration and stores
    /// no fold outcome. `Error` is a typed plan-validity refusal, surfaced by
    /// the runner as `EvaluationError.PlanInvalid` — never a silent
    /// truncation of the family.
    let folds (plan: EvaluationPlan) (observationCount: int) : Result<EvaluationFold list, string> =
        match plan with
        | EvaluationPlan.TailHoldout -> Ok []
        | _ when observationCount < 1 -> Error "the holdout frame has no observations to fold over"
        | EvaluationPlan.MaskedObservations masked ->
            if masked.FoldCount < 1 then
                Error "MaskedObservations fold count must be at least 1"
            else
                let foldCount =
                    match masked.Selection with
                    | MaskSelection.Declared _ -> 1
                    | _ -> masked.FoldCount

                let rec build (index: int) (acc: EvaluationFold list) =
                    if index >= foldCount then
                        Ok(List.rev acc)
                    else
                        match maskedFold masked.Selection observationCount index with
                        | Error reason -> Error reason
                        | Ok ordinals ->
                            build (index + 1) (EvaluationFold.at index (FoldWindow.MaskedOrdinals ordinals) :: acc)

                build 0 []
        | EvaluationPlan.RollingOrigin rolling ->
            if rolling.InitialTrainSize < 1 then
                Error "RollingOrigin initial train size must be at least 1"
            elif rolling.TestSize < 1 then
                Error "RollingOrigin test size must be at least 1"
            elif rolling.Step < 1 then
                Error "RollingOrigin step must be at least 1"
            elif rolling.FoldCount < 1 then
                Error "RollingOrigin fold count must be at least 1"
            else
                let lastTestEnd =
                    rolling.InitialTrainSize
                    + (rolling.FoldCount - 1) * rolling.Step
                    + rolling.TestSize

                if lastTestEnd > observationCount then
                    Error(
                        sprintf
                            "RollingOrigin family needs %d observation(s) but the holdout frame has %d"
                            lastTestEnd
                            observationCount
                    )
                else
                    [ 0 .. rolling.FoldCount - 1 ]
                    |> List.map (fun index ->
                        let trainEnd = rolling.InitialTrainSize + index * rolling.Step

                        let trainStart =
                            if rolling.Expanding then
                                0
                            else
                                trainEnd - rolling.InitialTrainSize

                        EvaluationFold.at
                            index
                            (FoldWindow.TrainTestWindow(trainStart, trainEnd, trainEnd, trainEnd + rolling.TestSize)))
                    |> Ok

/// The provider-side metric computation an evaluation run dispatches to —
/// the "provider seam" half of `IModelFitProvider.Evaluate` (task 456.A). A
/// fit provider that supports holdout evaluation implements this interface
/// **on the same object** as its `IModelFitProvider`; predictions + actuals
/// go in, a metric map comes out. Forge never implements it (plan D10) — a
/// provider that does not is a typed `EvaluationUnsupported` refusal, never
/// a forge-computed fallback.
type IModelEvaluationMetrics =
    /// Compute evaluation metrics for a scored holdout: the predictions
    /// frame (the Phase 454 scored vintage) and the actuals frame (the
    /// holdout vintage itself). Returns provider-computed metrics
    /// (name → float); `Error reason` is a typed refusal (e.g. the frames
    /// lack the columns the provider evaluates on).
    ///
    /// **Determinism (plan D4).** A deterministic provider MUST return an
    /// identical metric map for identical frames — no wall-clock, no RNG.
    abstract EvaluateMetrics:
        predictionsSchema: DatasetSchema *
        predictions: DatasetRow list *
        actualsSchema: DatasetSchema *
        actuals: DatasetRow list ->
            Async<Result<Map<string, float>, string>>

/// Everything one fold's metric computation receives (phase 652). The frames
/// are the **whole** predictions + holdout, not a slice: forge subsets
/// nothing, so a provider whose feature construction needs contiguity keeps
/// the series intact and applies `Fold.Window` itself.
type EvaluationFoldContext = {
    /// The plan the run declared — the provider sees which family it is in,
    /// not only the individual fold.
    Plan: EvaluationPlan
    /// The fold to evaluate. `EvaluationFold.aggregate` for the run's
    /// aggregate row (`FoldWindow.WholeHoldout`, index `-1`).
    Fold: EvaluationFold
    /// Schema of the predictions frame (the Phase 454 scored vintage).
    PredictionsSchema: DatasetSchema
    /// The whole predictions frame.
    Predictions: DatasetRow list
    /// Schema of the actuals frame (the holdout vintage itself).
    ActualsSchema: DatasetSchema
    /// The whole actuals frame.
    Actuals: DatasetRow list
}

/// The **plan-aware** provider-side metric computation (phase 652) — the
/// widened half of the evaluation seam. A provider that evaluates fold
/// families implements this alongside (or instead of)
/// `IModelEvaluationMetrics`; the envelope prefers it whenever the provider
/// declares the run's plan kind.
///
/// Forge never implements it (plan D10). A plan kind no provider declares is
/// a typed `EvaluationError.PlanUnsupported` naming the provider and the
/// kind — **never** a silent fallback to tail-holdout evaluation, which
/// would report a rolling-origin or masked run's numbers as if the fold
/// family had been honoured.
type IModelEvaluationPlanMetrics =
    /// The plan kinds this provider evaluates, by `EvaluationPlan.kindName`
    /// (`EvaluationPlan.allKindNames` for blanket support). **Declarative
    /// data** (GP 12 rule 3): the envelope reads it and refuses an
    /// unsupported kind by name *without calling the provider*, so an
    /// unrecognised plan cannot be evaluated by accident.
    abstract SupportedPlanKinds: unit -> string list

    /// Compute provider metrics for one fold of the declared plan.
    /// `Error reason` is a typed refusal (surfaced as `ProviderRefused`).
    ///
    /// **Determinism (plan D4).** A deterministic provider MUST return an
    /// identical metric map for an identical context — no wall-clock, no RNG.
    abstract EvaluateFoldMetrics: context: EvaluationFoldContext -> Async<Result<Map<string, float>, string>>

/// What a model-fit provider can do beyond fitting, declared once by the
/// implementation as `IModelFitProvider.Capabilities`.
///
/// A capability is a field the composition reads, never a type test against
/// the object it holds: a decorator over a provider answers with the inner
/// provider's record (`member _.Capabilities = inner.Capabilities`), so
/// wrapping a provider cannot hide what it declared. An undeclared field is
/// refused by name where it is needed (`EvaluationUnsupported`,
/// `PlanUnsupported`).
type ModelFitProviderCapabilities = {
    /// Holdout evaluation: the provider computes its own metrics for a scored
    /// holdout.
    Metrics: IModelEvaluationMetrics option
    /// Plan-aware evaluation: the provider evaluates the fold families it
    /// names.
    PlanMetrics: IModelEvaluationPlanMetrics option
}

[<RequireQualifiedAccess>]
module ModelFitProviderCapabilities =
    /// A provider that fits and does nothing else.
    let none: ModelFitProviderCapabilities = { Metrics = None; PlanMetrics = None }