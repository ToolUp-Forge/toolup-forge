// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Security.Cryptography

// ─── Phase 456 — model evaluation & champion-challenger core types ──────
//
// Holdout evaluation runs and model comparison as **stored, queryable
// outcomes**, with a promotion gate over the Phase 453 registry statuses
// (statistical-modelling substrate plan, Stage 6) — the Phase 14j
// eval-harness shape generalised from RAG to modelling. The deliberate tail
// of the substrate: it composes Stages 0–5 and adds **no new storage
// concept** (records ride `IDataObjectStore`, exactly as the registry does).
//
// **Plan D10 is the discipline line for every type here.** Providers compute
// metrics; forge stores, compares, and gates — it never computes a
// statistic. The only arithmetic in this file is float *comparison*
// (ordering, ties, margins) — never aggregation, never a formula over data.
//
// **Why server-only, not Core/Shared.** Evaluation identity is
// SHA-256-addressed (`System.Security.Cryptography` is not
// Fable-compilable) and the surfaces compose server-side stores; there is no
// client view in this phase (the phase key-files named a `Server/Shared/`
// folder that does not exist — the nearest correct home is `Server/`,
// mirroring Phase 449/453). The audit payload that crosses into persisted
// `ModuleEvent`s stays in `Core/Shared/AuditTypes.fs`.

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

/// Typed failure of the evaluation *envelope* (task 456.A). A refused or
/// failed evaluation is data, never an exception — distinct cases so the job
/// handler maps each to the right `JobResult` (terminal vs retryable).
[<RequireQualifiedAccess>]
type EvaluationError =
    /// No artifact with the given composite-key hash exists in the scope
    /// (Phase 453). Terminal.
    | ArtifactNotFound
    /// The registry read failed for a non-`NotFound` reason. Retryable.
    | RegistryFailure of reason: string
    /// No `IModelFitProvider` is registered for the artifact's
    /// `ProviderId`. Terminal — retrying will not register the provider.
    | ProviderNotFound of providerId: string
    /// The resolved provider does not implement `IModelEvaluationMetrics`
    /// (plan D10 — forge never computes a fallback metric). Terminal.
    | EvaluationUnsupported of providerId: string
    /// The resolved provider does not declare the run's plan kind (phase
    /// 652). Terminal — and deliberately NOT a tail-holdout fallback: a fold
    /// family the provider cannot honour must not be reported as if it had
    /// been.
    | PlanUnsupported of providerId: string * planKind: string
    /// The declared evaluation plan cannot generate a fold family over this
    /// holdout frame (phase 652) — e.g. a rolling-origin window family
    /// longer than the frame, or a mask ordinal outside it. Terminal: the
    /// same plan and frame will not become valid on retry.
    | PlanInvalid of reason: string
    /// The provider's `EvaluateMetrics` returned a typed refusal. Terminal
    /// — the same frames will not satisfy the provider on retry.
    | ProviderRefused of providerId: string * reason: string
    /// The provider's `EvaluateMetrics` raised. Retryable (transient).
    | ProviderFailed of providerId: string * message: string
    /// The Phase 454 scoring leg failed. Carries the typed `ScoreError`; the
    /// job layer maps terminal/retryable by its case.
    | ScoreFailed of ScoreError
    /// A dataset version (holdout or predictions) could not be read.
    /// Retryable.
    | InputUnavailable of reason: string
    /// A stored evaluation record was not found (`GetComparison` / lookups).
    /// Terminal for the requested id.
    | NotFound
    /// Persisting an evaluation outcome failed. Retryable.
    | StorageFailure of reason: string

module EvaluationError =
    /// Human-readable one-line description for logs + job-result messages.
    let describe =
        function
        | EvaluationError.ArtifactNotFound -> "model artifact not found"
        | EvaluationError.RegistryFailure r -> sprintf "model registry read failed: %s" r
        | EvaluationError.ProviderNotFound k -> sprintf "no IModelFitProvider registered for provider '%s'" k
        | EvaluationError.EvaluationUnsupported k ->
            sprintf "provider '%s' does not implement IModelEvaluationMetrics" k
        | EvaluationError.PlanUnsupported(k, planKind) ->
            sprintf "provider '%s' does not declare evaluation plan kind '%s'" k planKind
        | EvaluationError.PlanInvalid r -> sprintf "evaluation plan is invalid for this holdout: %s" r
        | EvaluationError.ProviderRefused(k, r) -> sprintf "provider '%s' refused to evaluate: %s" k r
        | EvaluationError.ProviderFailed(k, m) -> sprintf "provider '%s' failed to evaluate: %s" k m
        | EvaluationError.ScoreFailed e -> sprintf "holdout scoring failed: %s" (ScoreError.describe e)
        | EvaluationError.InputUnavailable r -> sprintf "evaluation input unavailable: %s" r
        | EvaluationError.NotFound -> "evaluation record not found"
        | EvaluationError.StorageFailure r -> sprintf "evaluation storage failure: %s" r

/// One stored holdout-evaluation outcome (task 456.A): which artifact, which
/// holdout vintage, the predictions vintage the scoring leg wrote, and the
/// **provider-computed** metric map stored verbatim (plan D10 — forge stores,
/// never interprets). The per-artifact list of these — one per holdout
/// vintage — is the out-of-time track record (task 456.D).
type EvaluationRun = {
    /// Deterministic identity: SHA-256 over `(artifact, holdout vintage)` —
    /// one evaluation outcome per pair, so a standing re-evaluation is
    /// naturally idempotent. See `EvaluationRun.id`.
    RunId: string
    /// Scope the evaluation ran under (GP 4 — structural isolation).
    ScopeId: string
    /// Composite-key hash of the evaluated artifact (Phase 453, plan D5).
    ArtifactKeyHash: string
    /// Resolved provider `Kind` that computed the metrics.
    ProviderId: string
    /// Resolved provider version.
    ProviderVersion: string
    /// The immutable holdout vintage evaluated (Phase 448).
    Holdout: DatasetVersionRef
    /// The predictions vintage the Phase 454 scoring leg wrote.
    Predictions: DatasetVersionRef
    /// Provider-computed metrics for the **whole** holdout frame — the
    /// aggregate row. Stored verbatim (plan D10) and unchanged by phase 652:
    /// under every plan the aggregate is the provider's evaluation of the
    /// whole frame, exactly as before fold families existed.
    Metrics: Map<string, float>
    /// The evaluation plan the run was executed under (phase 652). `None` on
    /// a record written before that phase — read it through
    /// `EvaluationPlan.resolve`, which yields `TailHoldout`. No migration:
    /// the absent field IS the degenerate plan.
    Plan: EvaluationPlan option
    /// Actor the run is attributed to.
    EvaluatedBy: string
    /// When the run's outcome was recorded.
    EvaluatedAt: DateTimeOffset
}

module EvaluationRun =
    /// Canonical, order-fixed identity string of an evaluation — one outcome
    /// per `(artifact, holdout vintage)` pair. Stable; do not reorder.
    let canonical (artifactKeyHash: string) (holdout: DatasetVersionRef) : string =
        sprintf "eval|artifact=%s|holdout=%s" artifactKeyHash (DatasetVersionRef.key holdout)

    /// Deterministic run id — lowercase SHA-256 hex of `canonical`.
    let id (artifactKeyHash: string) (holdout: DatasetVersionRef) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical artifactKeyHash holdout))
        |> Convert.ToHexStringLower

    /// The run's plan, with a pre-652 record reading as `TailHoldout`.
    let plan (run: EvaluationRun) : EvaluationPlan = EvaluationPlan.resolve run.Plan

/// One stored **per-fold** metric outcome of a run (phase 652): which fold of
/// which run, the window/mask it evaluated, and the provider-computed metric
/// map for that fold, stored verbatim (plan D10).
///
/// A `TailHoldout` run stores **none** of these — its aggregate row is its
/// single fold — so an existing run's stored footprint is unchanged.
type EvaluationFoldOutcome = {
    /// Deterministic identity: SHA-256 over `(run, fold)`. See
    /// `EvaluationFoldOutcome.id`.
    OutcomeId: string
    /// The run this fold belongs to.
    RunId: string
    /// Scope the run executed under (GP 4).
    ScopeId: string
    /// Composite-key hash of the evaluated artifact — so fold outcomes are
    /// queryable per artifact without joining through the run.
    ArtifactKeyHash: string
    /// Stable fold id within the run (`fold-<index>`).
    FoldId: string
    /// Zero-based fold index — the family's declared order.
    FoldIndex: int
    /// One-line window / mask descriptor (`FoldWindow.describe`), stored so a
    /// query surfaces the fold's shape without re-deriving its plan.
    Descriptor: string
    /// The typed window / mask this fold evaluated.
    Window: FoldWindow
    /// Provider-computed metrics for this fold, stored verbatim (plan D10).
    Metrics: Map<string, float>
    /// When the fold outcome was recorded.
    RecordedAt: DateTimeOffset
}

module EvaluationFoldOutcome =
    /// Canonical, order-fixed identity string of a fold outcome — one
    /// outcome per `(run, fold)` pair, so re-running an evaluation is as
    /// idempotent per fold as it already is per run.
    let canonical (runId: string) (foldId: string) : string =
        sprintf "evalfold|run=%s|fold=%s" runId foldId

    /// Deterministic fold-outcome id — lowercase SHA-256 hex of `canonical`.
    let id (runId: string) (foldId: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical runId foldId))
        |> Convert.ToHexStringLower

/// Which way a metric improves. Declared by the comparer (task 456.B) — the
/// declaration is data; forge applies a float comparison in the declared
/// direction and assigns no meaning to the metric itself (plan D10).
[<RequireQualifiedAccess>]
type MetricDirection =
    /// A larger value ranks better (e.g. a fit score).
    | HigherIsBetter
    /// A smaller value ranks better (e.g. an error measure).
    | LowerIsBetter

module MetricDirection =
    /// Stable case-name string (stored records, audit). Round-trips through
    /// `parse`; do not rename without a wire-format bump.
    let name =
        function
        | MetricDirection.HigherIsBetter -> "HigherIsBetter"
        | MetricDirection.LowerIsBetter -> "LowerIsBetter"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "HigherIsBetter" -> Some MetricDirection.HigherIsBetter
        | "LowerIsBetter" -> Some MetricDirection.LowerIsBetter
        | _ -> None

    /// `true` iff `challenger` beats `champion` in this direction by more
    /// than `margin` (`margin = 0.0` ⇒ strict improvement). A pure float
    /// comparison — the whole of forge's judgment (plan D10). Any NaN
    /// operand fails closed (`false`).
    let beats (direction: MetricDirection) (margin: float) (challenger: float) (champion: float) : bool =
        match direction with
        | MetricDirection.HigherIsBetter -> challenger > champion + margin
        | MetricDirection.LowerIsBetter -> challenger < champion - margin

/// How a comparison reduces an entrant's stored metric evidence to the one
/// float it ranks on (phase 652). **Declared on the comparison, no default**
/// — a fold family only means something once the comparer says which number
/// it is being judged by, and inferring one would be forge assigning
/// meaning it has no standing to assign (plan D10).
///
/// Every case selects or reduces *provider-computed* values. Forge computes
/// no statistic over data here or anywhere: `MeanAcrossFolds` averages
/// numbers the provider already returned, in the same sense that
/// `AggregateMetric` looks one up.
[<RequireQualifiedAccess>]
type FoldAggregation =
    /// Rank on the run's aggregate row — the whole-frame metric. The
    /// pre-phase-652 behaviour, and the only one a `TailHoldout` run can
    /// satisfy (it stores no folds).
    | AggregateMetric
    /// Rank on the arithmetic mean of the metric across the run's stored
    /// fold outcomes.
    | MeanAcrossFolds
    /// Rank on the entrant's WORST fold in the comparison's declared
    /// direction — the conservative reading of a fold family.
    | WorstFold

module FoldAggregation =
    /// Stable case-name string (stored records, comparison identity).
    /// Round-trips through `parse`; do not rename without a wire-format bump.
    let name =
        function
        | FoldAggregation.AggregateMetric -> "AggregateMetric"
        | FoldAggregation.MeanAcrossFolds -> "MeanAcrossFolds"
        | FoldAggregation.WorstFold -> "WorstFold"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "AggregateMetric" -> Some FoldAggregation.AggregateMetric
        | "MeanAcrossFolds" -> Some FoldAggregation.MeanAcrossFolds
        | "WorstFold" -> Some FoldAggregation.WorstFold
        | _ -> None

    /// The aggregation of a stored comparison: `None` — a record written
    /// before phase 652 — reads as `AggregateMetric`, which is exactly what
    /// it ranked on. No migration.
    let resolveStored (stored: FoldAggregation option) : FoldAggregation =
        defaultArg stored FoldAggregation.AggregateMetric

    /// Reduce one entrant's evidence — its run's aggregate metric and the
    /// same metric across its stored fold outcomes, one `float option` per
    /// fold in family order — to the single float the comparison ranks on.
    /// `None` means "not rankable", which `ModelComparison.order` surfaces as
    /// a typed missing-metric outcome, never a silent ordering position.
    ///
    /// A missing fold value, a NaN, or an empty family yields `None` for the
    /// fold aggregations: an aggregation must never launder partial or
    /// unusable evidence into a rankable number — a mean over whichever
    /// folds happened to report is not the mean the comparer declared.
    let resolve
        (aggregation: FoldAggregation)
        (direction: MetricDirection)
        (aggregate: float option)
        (foldMetrics: float option list)
        : float option =
        let usable (v: float) = not (Double.IsNaN v)

        let complete =
            if
                List.isEmpty foldMetrics
                || foldMetrics |> List.exists (fun v -> v |> Option.exists usable |> not)
            then
                None
            else
                Some(foldMetrics |> List.map Option.get)

        match aggregation with
        | FoldAggregation.AggregateMetric -> aggregate |> Option.filter usable
        | FoldAggregation.MeanAcrossFolds -> complete |> Option.map (fun vs -> List.sum vs / float (List.length vs))
        | FoldAggregation.WorstFold ->
            complete
            |> Option.map (fun vs ->
                match direction with
                | MetricDirection.HigherIsBetter -> List.min vs
                | MetricDirection.LowerIsBetter -> List.max vs)

/// One ranked entrant in a comparison: an artifact and the primary-metric
/// value its evaluation reported. Only entrants whose metric exists (and is
/// not NaN) are ranked — a missing metric is a typed outcome on the
/// comparison, never a silent ordering position (task 456.B).
type ComparisonStanding = {
    ArtifactKeyHash: string
    Metric: float
}

/// The typed verdict of a comparison (task 456.B). Ties and empty fields are
/// explicit cases, not arbitrary orderings.
[<RequireQualifiedAccess>]
type ComparisonResult =
    /// Exactly one entrant holds the best metric value.
    | DecisiveWinner of artifactKeyHash: string
    /// Two or more entrants share the exact best metric value — a typed tie,
    /// surfaced for human judgment (plan D10), never broken by forge.
    | TiedAtBest of artifactKeyHashes: string list
    /// No entrant had a usable primary metric — nothing to compare.
    | NoComparableMetrics

module ComparisonResult =
    /// Human-readable one-line description for logs + audit reasons.
    let describe =
        function
        | ComparisonResult.DecisiveWinner a -> sprintf "decisive winner: %s" a
        | ComparisonResult.TiedAtBest tied -> sprintf "tied at best: %s" (String.concat ", " tied)
        | ComparisonResult.NoComparableMetrics -> "no comparable metrics"

/// A stored, ordered comparison of N artifacts on one holdout vintage under
/// one declared primary metric + direction (task 456.B). The standings are a
/// pure float ordering over the entrants' stored evaluation metrics; every
/// entrant that could not be ranked (no evaluation, metric absent, or NaN)
/// is listed in `MissingMetric` — a typed outcome, never a silent ordering.
type ModelComparison = {
    /// Deterministic identity over `(scope, entrants, holdout, metric,
    /// direction)`. See `ModelComparison.id`.
    ComparisonId: string
    /// Scope the comparison ran under (GP 4).
    ScopeId: string
    /// The compared artifacts' composite-key hashes, in declared order.
    Entrants: string list
    /// The one holdout vintage every entrant's evaluation read.
    Holdout: DatasetVersionRef
    /// The provider metric the comparison ranks on. A name forge looks up —
    /// never a statistic forge computes (plan D10).
    PrimaryMetric: string
    /// Which way `PrimaryMetric` improves.
    Direction: MetricDirection
    /// How each entrant's metric evidence was reduced to the ranked value
    /// (phase 652). `None` on a record written before that phase — read it
    /// through `FoldAggregation.resolveStored`, which yields
    /// `AggregateMetric`, exactly what such a record ranked on.
    Aggregation: FoldAggregation option
    /// Ranked entrants, best first. Entrants tied on the exact metric value
    /// keep their declared order (a stable sort) — and the tie is surfaced
    /// in `Result`, never silently broken.
    Standings: ComparisonStanding list
    /// Entrants that could not be ranked (no stored evaluation for the
    /// vintage, primary metric absent, or NaN), in declared order.
    MissingMetric: string list
    /// The typed verdict.
    Result: ComparisonResult
    /// Actor the comparison is attributed to.
    ComparedBy: string
    /// When the comparison was recorded.
    ComparedAt: DateTimeOffset
}

module ModelComparison =
    /// Canonical, order-fixed identity string of a comparison. Entrant order
    /// is part of the identity (it is the declared tie-preserving order).
    let canonical
        (scopeId: string)
        (entrants: string list)
        (holdout: DatasetVersionRef)
        (primaryMetric: string)
        (direction: MetricDirection)
        : string =
        sprintf
            "compare|scope=%s|entrants=%s|holdout=%s|metric=%s|direction=%s"
            scopeId
            (String.concat "," entrants)
            (DatasetVersionRef.key holdout)
            primaryMetric
            (MetricDirection.name direction)

    /// Deterministic comparison id — lowercase SHA-256 hex of `canonical`.
    let id
        (scopeId: string)
        (entrants: string list)
        (holdout: DatasetVersionRef)
        (primaryMetric: string)
        (direction: MetricDirection)
        : string =
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical scopeId entrants holdout primaryMetric direction))
        |> Convert.ToHexStringLower

    /// Canonical identity string including the declared fold aggregation
    /// (phase 652). `AggregateMetric` appends nothing, so a comparison
    /// declaring it keeps **exactly** the pre-652 identity — an existing
    /// stored comparison stays reachable at the id it was written under.
    /// Any other aggregation ranks differently and therefore gets its own
    /// id rather than overwriting a differently-ranked verdict.
    let canonicalFor
        (scopeId: string)
        (entrants: string list)
        (holdout: DatasetVersionRef)
        (primaryMetric: string)
        (direction: MetricDirection)
        (aggregation: FoldAggregation)
        : string =
        let baseKey = canonical scopeId entrants holdout primaryMetric direction

        match aggregation with
        | FoldAggregation.AggregateMetric -> baseKey
        | other -> sprintf "%s|aggregation=%s" baseKey (FoldAggregation.name other)

    /// Deterministic comparison id including the declared fold aggregation —
    /// lowercase SHA-256 hex of `canonicalFor`.
    let idFor
        (scopeId: string)
        (entrants: string list)
        (holdout: DatasetVersionRef)
        (primaryMetric: string)
        (direction: MetricDirection)
        (aggregation: FoldAggregation)
        : string =
        SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalFor scopeId entrants holdout primaryMetric direction aggregation)
        )
        |> Convert.ToHexStringLower

    /// Order entrants by their primary-metric value (task 456.B): a pure
    /// float sort in the declared direction (plan D10 — the only judgment is
    /// `>`/`<`). Entrants with `None` or NaN metrics land in the missing
    /// list, in declared order — a typed outcome, never a silent ordering.
    /// The sort is stable, so exact ties keep declared order and are
    /// surfaced as `TiedAtBest`.
    let order
        (direction: MetricDirection)
        (entrants: (string * float option) list)
        : ComparisonStanding list * string list * ComparisonResult =
        let ranked, missing =
            entrants
            |> List.partition (fun (_, metric) ->
                match metric with
                | Some v -> not (Double.IsNaN v)
                | None -> false)

        let standings =
            ranked
            |> List.map (fun (key, metric) -> {
                ArtifactKeyHash = key
                Metric = Option.get metric
            })
            |> match direction with
               | MetricDirection.HigherIsBetter -> List.sortByDescending _.Metric
               | MetricDirection.LowerIsBetter -> List.sortBy _.Metric

        let missingKeys = missing |> List.map fst

        let result =
            match standings with
            | [] -> ComparisonResult.NoComparableMetrics
            | best :: _ ->
                let tied =
                    standings
                    |> List.filter (fun s -> s.Metric = best.Metric)
                    |> List.map _.ArtifactKeyHash

                match tied with
                | [ winner ] -> ComparisonResult.DecisiveWinner winner
                | tiedKeys -> ComparisonResult.TiedAtBest tiedKeys

        standings, missingKeys, result

/// Opt-in champion/challenger promotion policy (task 456.C). Default off by
/// construction (GP 13): the gate is a separate service a governed
/// deployment composes — a deployment that never constructs it is
/// byte-for-byte unchanged, and Phase 453 transitions work exactly as
/// before.
type ChampionChallengerPolicy = {
    /// How much the challenger must beat the champion by on the comparison's
    /// declared primary metric, in the declared direction. `0.0` = any
    /// strict improvement.
    Margin: float
}

module ChampionChallengerPolicy =
    /// Any strict improvement over the champion permits promotion.
    let strictImprovement: ChampionChallengerPolicy = { Margin = 0.0 }

    /// The challenger must beat the champion by at least `margin`.
    let withMargin (margin: float) : ChampionChallengerPolicy = { Margin = margin }

/// The typed verdict of the promotion gate (task 456.C). Every non-beating
/// case is explicit — the gate fails closed on anything it cannot compare —
/// and no case promotes anything: promotion remains the human Owner/Admin
/// `TransitionStatus` (Phase 453), which the gate merely narrows.
[<RequireQualifiedAccess>]
type PromotionVerdict =
    /// The challenger beat the current champion by at least the policy
    /// margin — the gate permits the (still human, still role-gated)
    /// promotion.
    | BeatsChampion of championKeyHash: string * challengerMetric: float * championMetric: float
    /// The challenger did not beat the current champion. Refused.
    | DoesNotBeatChampion of championKeyHash: string * challengerMetric: float * championMetric: float
    /// No entrant of the comparison is currently `Approved` — there is no
    /// champion to defend, so the policy narrows nothing.
    | NoChampion
    /// The challenger is not an entrant of the comparison. Refused (fail
    /// closed — the gate only reasons over the stored verdict).
    | ChallengerNotCompared
    /// The challenger has no usable primary metric in the comparison.
    /// Refused (fail closed).
    | ChallengerMetricMissing
    /// The current champion has no usable primary metric in the comparison —
    /// the gate cannot prove the challenger beats it. Refused (fail closed).
    | ChampionMetricMissing of championKeyHash: string

module PromotionVerdict =
    /// Whether the verdict permits the promotion to proceed to the human
    /// Owner/Admin transition. Only a beaten champion or no champion at all
    /// do; every uncomparable case fails closed.
    let permitsPromotion =
        function
        | PromotionVerdict.BeatsChampion _
        | PromotionVerdict.NoChampion -> true
        | PromotionVerdict.DoesNotBeatChampion _
        | PromotionVerdict.ChallengerNotCompared
        | PromotionVerdict.ChallengerMetricMissing
        | PromotionVerdict.ChampionMetricMissing _ -> false

    /// Human-readable one-line description for audit reasons + logs.
    let describe =
        function
        | PromotionVerdict.BeatsChampion(champion, challengerM, championM) ->
            sprintf "challenger (%g) beats champion %s (%g)" challengerM champion championM
        | PromotionVerdict.DoesNotBeatChampion(champion, challengerM, championM) ->
            sprintf "challenger (%g) does not beat champion %s (%g)" challengerM champion championM
        | PromotionVerdict.NoChampion -> "no current champion among the compared artifacts"
        | PromotionVerdict.ChallengerNotCompared -> "challenger is not an entrant of the comparison"
        | PromotionVerdict.ChallengerMetricMissing -> "challenger has no usable primary metric in the comparison"
        | PromotionVerdict.ChampionMetricMissing champion ->
            sprintf "champion %s has no usable primary metric in the comparison" champion

module PromotionGate =
    /// The metric a comparison ranked for one entrant — `None` when the
    /// entrant landed in `MissingMetric` (or is absent entirely).
    let private metricOf (comparison: ModelComparison) (keyHash: string) : float option =
        comparison.Standings
        |> List.tryFind (fun s -> s.ArtifactKeyHash = keyHash)
        |> Option.map _.Metric

    /// Check whether `challenger` may be promoted over `champion` given the
    /// stored comparison (task 456.C). Pure — a float comparison in the
    /// comparison's declared direction with the policy margin (plan D10);
    /// every uncomparable case is a typed fail-closed verdict. `champion` is
    /// `None` when no compared artifact is currently `Approved`.
    let check
        (policy: ChampionChallengerPolicy)
        (comparison: ModelComparison)
        (champion: string option)
        (challenger: string)
        : PromotionVerdict =
        if not (List.contains challenger comparison.Entrants) then
            PromotionVerdict.ChallengerNotCompared
        else
            match metricOf comparison challenger with
            | None -> PromotionVerdict.ChallengerMetricMissing
            | Some challengerMetric ->
                match champion with
                | None -> PromotionVerdict.NoChampion
                | Some championKey ->
                    match metricOf comparison championKey with
                    | None -> PromotionVerdict.ChampionMetricMissing championKey
                    | Some championMetric ->
                        if MetricDirection.beats comparison.Direction policy.Margin challengerMetric championMetric then
                            PromotionVerdict.BeatsChampion(championKey, challengerMetric, championMetric)
                        else
                            PromotionVerdict.DoesNotBeatChampion(championKey, challengerMetric, championMetric)

/// A standing declaration that an artifact should be re-evaluated whenever a
/// new vintage of a holdout dataset lands (task 456.D). The registration
/// names the dataset by *id*, not version — the re-evaluation job resolves
/// the latest vintage each sweep and evaluates it exactly once (the
/// deterministic `EvaluationRun.id` makes the sweep idempotent).
type EvaluationRegistration = {
    /// Deterministic identity over `(artifact, holdout dataset id)`. See
    /// `EvaluationRegistration.id`.
    RegistrationId: string
    /// Scope the registration lives under (GP 4).
    ScopeId: string
    /// Composite-key hash of the artifact to keep evaluating.
    ArtifactKeyHash: string
    /// The holdout dataset whose new vintages trigger re-evaluation.
    HoldoutDatasetId: string
    /// Actor who registered the standing evaluation.
    RegisteredBy: string
    /// When the registration was recorded.
    RegisteredAt: DateTimeOffset
}

module EvaluationRegistration =
    /// Canonical, order-fixed identity string of a registration — one
    /// standing registration per `(artifact, holdout dataset)` pair.
    let canonical (artifactKeyHash: string) (holdoutDatasetId: string) : string =
        sprintf "evalreg|artifact=%s|dataset=%s" artifactKeyHash holdoutDatasetId

    /// Deterministic registration id — lowercase SHA-256 hex of `canonical`.
    let id (artifactKeyHash: string) (holdoutDatasetId: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical artifactKeyHash holdoutDatasetId))
        |> Convert.ToHexStringLower

/// `IModelFitProvider.Evaluate` — the evaluation extension member on the
/// provider seam (task 456.A). Dispatches to the provider's own
/// `IModelEvaluationMetrics` implementation when it declares one; a provider
/// that does not is a typed `EvaluationUnsupported` refusal. The member
/// itself computes nothing (plan D10 — metric math is the provider's; forge
/// only routes) and is additive: no shipped `IModelFitProvider`
/// implementation changes shape.
[<AutoOpen>]
module ModelFitProviderEvaluationExtensions =
    type IModelFitProvider with
        /// Compute provider-owned evaluation metrics for a scored holdout —
        /// predictions + actuals in, metric map out. `Error` is the typed
        /// envelope refusal (`EvaluationUnsupported` / `ProviderRefused`);
        /// a raising provider surfaces as `ProviderFailed` at the runner.
        member this.Evaluate
            (
                predictionsSchema: DatasetSchema,
                predictions: DatasetRow list,
                actualsSchema: DatasetSchema,
                actuals: DatasetRow list
            ) : Async<Result<Map<string, float>, EvaluationError>> =
            match this with
            | :? IModelEvaluationMetrics as metrics -> async {
                match! metrics.EvaluateMetrics(predictionsSchema, predictions, actualsSchema, actuals) with
                | Ok computed -> return Ok computed
                | Error reason -> return Error(EvaluationError.ProviderRefused(this.Kind, reason))
              }
            | _ -> async { return Error(EvaluationError.EvaluationUnsupported this.Kind) }

        /// `IModelFitProvider.EvaluateFold` — the **plan-aware** evaluation
        /// dispatch (phase 652). Routes one fold of one plan to the right
        /// provider surface, and computes nothing itself (plan D10):
        ///
        /// 1. a provider implementing `IModelEvaluationPlanMetrics` that
        ///    **declares this plan kind** gets the fold context;
        /// 2. otherwise, a `TailHoldout` plan falls to the Phase 456
        ///    `Evaluate` path — byte-for-byte the behaviour every existing
        ///    provider and every existing stored run already has;
        /// 3. otherwise the plan kind is refused BY NAME
        ///    (`PlanUnsupported`), or the provider is refused for evaluating
        ///    at all (`EvaluationUnsupported`).
        ///
        /// Step 3 is the point of the phase: there is no path on which an
        /// unrecognised fold family is quietly evaluated as a tail holdout.
        member this.EvaluateFold(context: EvaluationFoldContext) : Async<Result<Map<string, float>, EvaluationError>> =
            let planKind = EvaluationPlan.kindName context.Plan

            let declaresPlan =
                match this with
                | :? IModelEvaluationPlanMetrics as planned -> List.contains planKind (planned.SupportedPlanKinds())
                | _ -> false

            match this with
            | :? IModelEvaluationPlanMetrics as planned when declaresPlan -> async {
                match! planned.EvaluateFoldMetrics context with
                | Ok computed -> return Ok computed
                | Error reason -> return Error(EvaluationError.ProviderRefused(this.Kind, reason))
              }
            | _ ->
                match context.Plan with
                | EvaluationPlan.TailHoldout ->
                    this.Evaluate(
                        context.PredictionsSchema,
                        context.Predictions,
                        context.ActualsSchema,
                        context.Actuals
                    )
                | _ ->
                    match this with
                    | :? IModelEvaluationMetrics
                    | :? IModelEvaluationPlanMetrics -> async {
                        return Error(EvaluationError.PlanUnsupported(this.Kind, planKind))
                      }
                    | _ -> async { return Error(EvaluationError.EvaluationUnsupported this.Kind) }