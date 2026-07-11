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
    /// Provider-computed metrics, stored verbatim (plan D10).
    Metrics: Map<string, float>
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