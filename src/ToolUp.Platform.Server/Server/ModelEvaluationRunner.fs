// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 456 — model evaluation runner + champion-challenger gate ─────
//
// The evaluation envelope (plan Stage 6): score a holdout vintage through
// the Phase 454 seam, hand predictions + actuals to the provider that
// fitted the artifact (`IModelFitProvider.Evaluate`, dispatching to its
// `IModelEvaluationMetrics`), and store the provider-computed metric map as
// an `EvaluationRun` against the artifact. Comparisons order stored metrics
// (pure float sort — the whole of forge's judgment, plan D10); the
// champion-challenger gate narrows the Phase 453 `Approved` transition
// behind a stored comparison verdict — and never performs it: promotion
// remains the human, Owner/Admin-gated `TransitionStatus`.
//
// **No new storage concept.** Runs, comparisons, and standing registrations
// are data objects over `IDataObjectStore` — the exact `BlobModelRegistry`
// idiom (queryable coordinates mirrored into the metadata sidecar; content
// decoded only for matches). **Nothing here is composed by default
// (GP 13)** — a deployment constructs the runner / gate / job handlers
// explicitly, exactly as it constructs the Phase 454 `ModelScorer`; an
// unconfigured deployment is byte-for-byte unchanged.
//
// **Six portability rules (GP 12).**
// 1. *Identity by value* — artifacts / vintages / runs are keyed by hash
//    strings + value records; no live handles cross the seam.
// 2. *Async at every boundary* — every method returns `Async<_>`.
// 3. *Behaviour as data* — the comparison's metric + direction and the
//    gate's margin are declared values; a refusal is a typed
//    `EvaluationError` / `PromotionError`, never an exception.
// 4. *Stateless between calls* — every call carries its full scope + keys;
//    the runner derives everything from the composed stores and caches
//    nothing across calls.
// 5. *No cross-shard ordering* — runs are independent; the only ordering is
//    a single stored comparison's standings (data, not a promise).
// 6. *Precision at the lower bound* — timestamps are `DateTimeOffset`;
//    identities are exact SHA-256 keys (no approximate matching).
//
// **GP 12 audit of the phase-652 widening** (plans, folds, the plan-aware
// provider seam, per-fold outcomes, declared comparison aggregation):
// 1. *Identity by value* — a plan is a closed DU of primitives; a fold is a
//    string id + int index + an ordinal-valued window; a fold outcome is
//    keyed by a SHA-256 string over `(run, fold)`. Nothing crossing the seam
//    is a handle, a stream, or a cursor into the runner's state.
// 2. *Async at every boundary* — `IModelEvaluationPlanMetrics` and the new
//    `GetFoldOutcomes` query both return `Async<_>`.
// 3. *Behaviour as data* — fold GENERATION is plan parameters; plan support
//    is a declared `SupportedPlanKinds()` list read before the provider is
//    called; comparison aggregation is a declared `FoldAggregation`. Every
//    refusal is a typed `EvaluationError` case, never an exception and never
//    a callback.
// 4. *Stateless between calls* — a fold's provider call carries its plan,
//    its fold, and both whole frames; the provider retains nothing between
//    folds, so a distributed worker may take any fold in any order.
// 5. *No cross-shard ordering* — folds carry an explicit `FoldIndex` and the
//    query sorts by it, so family order is DATA on the records rather than a
//    promise about the store's enumeration.
// 6. *Precision at the lower bound* — fold windows are exact integer
//    observation ordinals (half-open ranges); no fractional split points, no
//    implicit rounding a second implementation could disagree about.

/// A request to evaluate `ArtifactKeyHash` against the `Holdout` vintage
/// (task 456.A). Also the persisted payload for a `_platform.modeleval.run`
/// job — the run references the artifact by composite-key hash, so a
/// scheduled evaluation always reads the artifact's *current* record.
type EvaluationRequest = {
    /// Scope the evaluation runs + writes under (GP 4).
    ScopeId: string
    /// Composite-key hash of the artifact to evaluate (Phase 453).
    ArtifactKeyHash: string
    /// The immutable holdout vintage to evaluate against (Phase 448).
    Holdout: DatasetVersionRef
    /// The evaluation plan to run under (phase 652). `None` is the
    /// degenerate `TailHoldout` plan — the Phase 456 behaviour, and what a
    /// persisted job payload written before this phase deserialises to.
    Plan: EvaluationPlan option
    /// Actor the run is attributed to.
    EvaluatedBy: string
}

/// A request to compare `Entrants` on one holdout vintage under a declared
/// primary metric + direction (task 456.B).
type ComparisonRequest = {
    /// Scope the comparison runs under (GP 4).
    ScopeId: string
    /// Composite-key hashes of the artifacts to compare, in declared order.
    Entrants: string list
    /// The one holdout vintage whose stored evaluations are compared.
    Holdout: DatasetVersionRef
    /// The provider metric to rank on (a name forge looks up, plan D10).
    PrimaryMetric: string
    /// Which way the metric improves.
    Direction: MetricDirection
    /// How each entrant's metric evidence is reduced to the ranked value
    /// (phase 652) — **declared, never defaulted**: a fold family only means
    /// something once the comparer says which number it is judged by.
    /// `FoldAggregation.AggregateMetric` is the Phase 456 ranking and keeps
    /// the pre-652 comparison identity byte-for-byte.
    Aggregation: FoldAggregation
    /// Actor the comparison is attributed to.
    ComparedBy: string
}

/// The forge-facing evaluation harness (plan Stage 6). Every method is
/// team-scoped (GP 4); every stored outcome is queryable back out — the
/// per-artifact track record is the read surface downstream decision-level
/// validation builds on (task 456.D).
type IModelEvaluationRunner =
    /// Run one holdout evaluation: score the holdout through the Phase 454
    /// seam, dispatch predictions + actuals to the artifact's provider for
    /// metric computation (plan D10), and store the outcome as an
    /// `EvaluationRun`. Idempotent per `(artifact, holdout vintage)` — the
    /// run id is deterministic; re-evaluating the same pair records a fresh
    /// outcome under the same id. Audits `ModelEvaluated` on success (GP 6).
    abstract Evaluate: request: EvaluationRequest -> Async<Result<EvaluationRun, EvaluationError>>

    /// Every stored evaluation of one artifact in the scope, ascending by
    /// `EvaluatedAt` — the out-of-time metric track record (task 456.D).
    /// Empty when the artifact has never been evaluated.
    abstract GetTrackRecord: scopeId: string * artifactKeyHash: string -> Async<EvaluationRun list>

    /// Every stored **per-fold** outcome of one run, in family order (phase
    /// 652). Empty for a `TailHoldout` run — including every run stored
    /// before this phase — because the degenerate family's aggregate row IS
    /// its single fold.
    abstract GetFoldOutcomes: scopeId: string * runId: string -> Async<EvaluationFoldOutcome list>

    /// Compare N artifacts' stored evaluations on one holdout vintage under
    /// the declared primary metric + direction, and store the ordered
    /// verdict (task 456.B). A pure float ordering over provider metrics —
    /// ties and missing metrics are typed outcomes on the stored record.
    abstract Compare: request: ComparisonRequest -> Async<Result<ModelComparison, EvaluationError>>

    /// Fetch one stored comparison by id. `NotFound` when no comparison with
    /// that id exists in the scope.
    abstract GetComparison: scopeId: string * comparisonId: string -> Async<Result<ModelComparison, EvaluationError>>

    /// Register a standing re-evaluation: whenever a new vintage of
    /// `holdoutDatasetId` lands, the re-evaluation sweep evaluates the
    /// artifact against it (task 456.D). **Idempotent**: re-registering an
    /// existing `(artifact, dataset)` pair returns the existing registration
    /// unchanged.
    abstract RegisterReevaluation:
        scopeId: string * artifactKeyHash: string * holdoutDatasetId: string * registeredBy: string ->
            Async<Result<EvaluationRegistration, EvaluationError>>

    /// Every standing re-evaluation registration in the scope.
    abstract ListReevaluationRegistrations: scopeId: string -> Async<EvaluationRegistration list>

/// Storage plumbing for the evaluation records — the `BlobModelRegistry`
/// idiom over `IDataObjectStore` (no new storage concept, plan Stage 6):
/// queryable coordinates mirrored into the metadata sidecar, content decoded
/// only for matches. A private module (not class-local bindings) because the
/// helpers are generic over the stored record type.
module private ModelEvaluationStorage =

    [<Literal>]
    let RunDataType = "toolup.modelevaluation"

    [<Literal>]
    let ComparisonDataType = "toolup.modelcomparison"

    /// Per-fold outcomes (phase 652) — their own data type, so the
    /// aggregate-row query surface (`GetTrackRecord`) is untouched by their
    /// arrival and an existing consumer sees exactly what it saw before.
    [<Literal>]
    let FoldDataType = "toolup.modelevaluationfold"

    [<Literal>]
    let FoldRunKey = "evaluationfold.run"

    [<Literal>]
    let RegistrationDataType = "toolup.modelevalregistration"

    [<Literal>]
    let ArtifactKey = "evaluation.artifact"

    [<Literal>]
    let HoldoutKey = "evaluation.holdout"

    [<Literal>]
    let RegistrationArtifactKey = "registration.artifact"

    [<Literal>]
    let RegistrationDatasetKey = "registration.datasetId"

    let jsonOptions = FableConverters.create ()

    let mapDataObjectError (e: DataObjectError) : EvaluationError =
        match e with
        | DataObjectError.NotFound -> EvaluationError.NotFound
        | DataObjectError.VersionNotFound v -> EvaluationError.StorageFailure $"record version {v} not found"
        | DataObjectError.DeleteForbidden -> EvaluationError.StorageFailure "record delete forbidden"
        | DataObjectError.PolicyMismatch _ -> EvaluationError.StorageFailure "record versioning policy mismatch"
        | DataObjectError.StorageFailure m -> EvaluationError.StorageFailure m

    /// Decode a stored record of type `'T`; an unreadable blob is a typed
    /// storage failure, never a throw.
    let decode<'T> (bytes: byte[]) : Result<'T, EvaluationError> =
        try
            Ok(JsonSerializer.Deserialize<'T>(bytes, jsonOptions))
        with ex ->
            Error(EvaluationError.StorageFailure $"stored evaluation record is unreadable: {ex.Message}")

    /// Persist a record under `objectId` with the given sidecar. `Versioned`
    /// — a re-run of the same deterministic id appends a fresh version;
    /// reads return the latest.
    let save
        (dataObjects: IDataObjectStore)
        (scopeId: string)
        (objectId: string)
        (dataType: string)
        (createdBy: string)
        (metadata: Map<string, string>)
        (record: 'T)
        : Async<Result<unit, EvaluationError>> =
        async {
            let content = JsonSerializer.SerializeToUtf8Bytes(record, jsonOptions)

            match! dataObjects.Save(scopeId, objectId, content, dataType, createdBy, metadata, Versioned) with
            | Ok _ -> return Ok()
            | Error e -> return Error(mapDataObjectError e)
        }

    /// Materialise every record of `dataType` in the scope whose metadata
    /// sidecar satisfies `sidecarMatches` (the `BlobModelRegistry.queryBy`
    /// idiom). Undecodable matches are skipped.
    let queryBy<'T>
        (dataObjects: IDataObjectStore)
        (scopeId: string)
        (dataType: string)
        (sidecarMatches: Map<string, string> -> bool)
        : Async<'T list> =
        async {
            let! objs = dataObjects.ListObjects scopeId

            let candidates =
                objs
                |> List.filter (fun d -> d.DataType = dataType && sidecarMatches d.Metadata)

            let results = ResizeArray<'T>()

            for dobj in candidates do
                match! dataObjects.GetContent(scopeId, dobj.ContentHash) with
                | Ok bytes ->
                    match decode<'T> bytes with
                    | Ok record -> results.Add record
                    | Error _ -> ()
                | Error _ -> ()

            return List.ofSeq results
        }

/// Default `IModelEvaluationRunner` over the composed modelling substrate.
/// Providers are resolved from the Phase 449 fit-provider registry by the
/// artifact's `ProviderId` — the provider that fitted an artifact evaluates
/// it. Construct via `ModelEvaluationRunner.create`.
type ModelEvaluationRunner
    (
        fitProviders: ModelFitProviderRegistry,
        registry: IModelRegistry,
        scorer: IModelScorer,
        datasets: IDatasetStore,
        dataObjects: IDataObjectStore,
        audit: IAuditLog
    ) =

    /// Read every row of a vintage, paging in fixed windows (the Phase 454
    /// `ModelScorer` idiom). `Error` lifts a `DatasetError` to
    /// `EvaluationError.InputUnavailable`.
    let readAllRows (input: DatasetVersionRef) : Async<Result<DatasetRow list, EvaluationError>> =
        let pageSize = 1000

        let rec loop (offset: int64) (acc: DatasetRow list) : Async<Result<DatasetRow list, EvaluationError>> = async {
            let query: DatasetPageQuery = {
                Offset = offset
                Limit = pageSize
                Filters = []
            }

            match! datasets.ReadPage(input.ScopeId, input.DatasetId, input.Version, query) with
            | Error e -> return Error(EvaluationError.InputUnavailable(DatasetError.describe e))
            | Ok page ->
                let acc = acc @ page.Rows
                let read = offset + int64 (List.length page.Rows)

                if List.isEmpty page.Rows || read >= page.TotalRows then
                    return Ok acc
                else
                    return! loop read acc
        }

        loop 0L []

    /// Read a vintage's schema + all rows. `Error` is `InputUnavailable`.
    let readFrame (input: DatasetVersionRef) : Async<Result<DatasetSchema * DatasetRow list, EvaluationError>> = async {
        match! datasets.GetVersion(input.ScopeId, input.DatasetId, input.Version) with
        | Error e -> return Error(EvaluationError.InputUnavailable(DatasetError.describe e))
        | Ok version ->
            match! readAllRows input with
            | Error e -> return Error e
            | Ok rows -> return Ok(version.Schema, rows)
    }

    /// The stored evaluation for one `(artifact, holdout vintage)` pair —
    /// `None` when the pair has never been evaluated (or the record is
    /// unreadable). The comparison's per-entrant metric source.
    let tryGetRun (scopeId: string) (artifactKeyHash: string) (holdout: DatasetVersionRef) = async {
        match! dataObjects.Get(scopeId, EvaluationRun.id artifactKeyHash holdout) with
        | Ok(_, bytes) ->
            match ModelEvaluationStorage.decode<EvaluationRun> bytes with
            | Ok run -> return Some run
            | Error _ -> return None
        | Error _ -> return None
    }

    /// Every stored per-fold outcome of one run, in family order.
    let foldOutcomesOf (scopeId: string) (runId: string) = async {
        let! outcomes =
            ModelEvaluationStorage.queryBy<EvaluationFoldOutcome>
                dataObjects
                scopeId
                ModelEvaluationStorage.FoldDataType
                (fun m -> Map.tryFind ModelEvaluationStorage.FoldRunKey m = Some runId)

        return outcomes |> List.sortBy _.FoldIndex
    }

    /// Phase 652 — run the declared plan: derive its fold family, evaluate
    /// the **aggregate** row and every fold through the provider, persist the
    /// per-fold outcomes, and return the aggregate metric map (the run's
    /// `Metrics`, unchanged in meaning from Phase 456).
    ///
    /// Three properties are load-bearing here:
    ///
    /// * *Fold generation is enumeration, not statistics.* The family is
    ///   derived from the observation COUNT and the plan's own declared
    ///   parameters; no row value is read (plan D10).
    /// * *Forge slices nothing.* Every fold's provider call receives the
    ///   WHOLE predictions + holdout frames plus the fold's window/mask
    ///   descriptor, so a provider whose feature construction needs
    ///   contiguity keeps the series intact.
    /// * *A `TailHoldout` run is byte-for-byte what it was.* Its family is
    ///   empty, so it makes exactly one provider call and stores exactly one
    ///   record — the aggregate row.
    let evaluatePlanFolds
        (provider: IModelFitProvider)
        (request: EvaluationRequest)
        (runId: string)
        (plan: EvaluationPlan)
        (predictionsSchema: DatasetSchema, predictions: DatasetRow list)
        (actualsSchema: DatasetSchema, actuals: DatasetRow list)
        : Async<Result<Map<string, float>, EvaluationError>> =
        async {
            let evaluateFold (fold: EvaluationFold) = async {
                try
                    return!
                        provider.EvaluateFold {
                            Plan = plan
                            Fold = fold
                            PredictionsSchema = predictionsSchema
                            Predictions = predictions
                            ActualsSchema = actualsSchema
                            Actuals = actuals
                        }
                with ex ->
                    return Error(EvaluationError.ProviderFailed(provider.Kind, ex.Message))
            }

            match EvaluationPlan.folds plan (List.length actuals) with
            | Error reason -> return Error(EvaluationError.PlanInvalid reason)
            | Ok folds ->
                match! evaluateFold EvaluationFold.aggregate with
                | Error e -> return Error e
                | Ok aggregateMetrics ->
                    // A fold refusal fails the WHOLE run: a partially
                    // evaluated family would read as a complete one at the
                    // comparison, which is the silent-wrong-number failure
                    // this phase exists to make impossible.
                    let mutable failure: EvaluationError option = None

                    for fold in folds do
                        if Option.isNone failure then
                            match! evaluateFold fold with
                            | Error e -> failure <- Some e
                            | Ok foldMetrics ->
                                let outcome: EvaluationFoldOutcome = {
                                    OutcomeId = EvaluationFoldOutcome.id runId fold.FoldId
                                    RunId = runId
                                    ScopeId = request.ScopeId
                                    ArtifactKeyHash = request.ArtifactKeyHash
                                    FoldId = fold.FoldId
                                    FoldIndex = fold.Index
                                    Descriptor = FoldWindow.describe fold.Window
                                    Window = fold.Window
                                    Metrics = foldMetrics
                                    RecordedAt = DateTimeOffset.UtcNow
                                }

                                let foldSidecar =
                                    Map [
                                        ModelEvaluationStorage.FoldRunKey, runId
                                        ModelEvaluationStorage.ArtifactKey, request.ArtifactKeyHash
                                    ]

                                match!
                                    ModelEvaluationStorage.save
                                        dataObjects
                                        request.ScopeId
                                        outcome.OutcomeId
                                        ModelEvaluationStorage.FoldDataType
                                        request.EvaluatedBy
                                        foldSidecar
                                        outcome
                                with
                                | Error e -> failure <- Some e
                                | Ok() -> ()

                    match failure with
                    | Some e -> return Error e
                    | None -> return Ok aggregateMetrics
        }

    interface IModelEvaluationRunner with
        member _.Evaluate(request: EvaluationRequest) : Async<Result<EvaluationRun, EvaluationError>> = async {
            match! registry.Get(request.ScopeId, request.ArtifactKeyHash) with
            | Error ModelRegistryError.NotFound -> return Error EvaluationError.ArtifactNotFound
            | Error e -> return Error(EvaluationError.RegistryFailure(ModelRegistryError.describe e))
            | Ok artifact ->
                let providerId = artifact.CompositeKey.ProviderId

                match fitProviders.TryResolve providerId with
                | None -> return Error(EvaluationError.ProviderNotFound providerId)
                | Some provider ->
                    let runId = EvaluationRun.id request.ArtifactKeyHash request.Holdout

                    // The predictions vintage lands under a run-derived
                    // dataset id, so re-evaluating the same pair appends
                    // versions of one predictions dataset rather than
                    // scattering new ids.
                    let scoreRequest: ScoreRequest = {
                        ScopeId = request.ScopeId
                        Artifact = artifact
                        Input = request.Holdout
                        OutputDatasetId = "_eval-" + runId.Substring(0, 16)
                        ScoredBy = request.EvaluatedBy
                    }

                    match! scorer.Score scoreRequest with
                    | Error e -> return Error(EvaluationError.ScoreFailed e)
                    | Ok predictionsRef ->
                        match! readFrame predictionsRef with
                        | Error e -> return Error e
                        | Ok(predictionsSchema, predictions) ->
                            match! readFrame request.Holdout with
                            | Error e -> return Error e
                            | Ok(actualsSchema, actuals) ->
                                let plan = EvaluationPlan.resolve request.Plan

                                match!
                                    evaluatePlanFolds
                                        provider
                                        request
                                        runId
                                        plan
                                        (predictionsSchema, predictions)
                                        (actualsSchema, actuals)
                                with
                                | Error e -> return Error e
                                | Ok metrics ->
                                    let run: EvaluationRun = {
                                        RunId = runId
                                        ScopeId = request.ScopeId
                                        ArtifactKeyHash = request.ArtifactKeyHash
                                        ProviderId = providerId
                                        ProviderVersion = artifact.CompositeKey.ProviderVersion
                                        Holdout = request.Holdout
                                        Predictions = predictionsRef
                                        Metrics = metrics
                                        Plan = Some plan
                                        EvaluatedBy = request.EvaluatedBy
                                        EvaluatedAt = DateTimeOffset.UtcNow
                                    }

                                    let sidecar =
                                        Map [
                                            ModelEvaluationStorage.ArtifactKey, request.ArtifactKeyHash
                                            ModelEvaluationStorage.HoldoutKey, DatasetVersionRef.key request.Holdout
                                        ]

                                    match!
                                        ModelEvaluationStorage.save
                                            dataObjects
                                            request.ScopeId
                                            runId
                                            ModelEvaluationStorage.RunDataType
                                            request.EvaluatedBy
                                            sidecar
                                            run
                                    with
                                    | Error e -> return Error e
                                    | Ok() ->
                                        do!
                                            audit.Record(
                                                request.ScopeId,
                                                ModelEvaluated {
                                                    CompositeKeyHash = request.ArtifactKeyHash
                                                    ProviderId = providerId
                                                    ProviderVersion = artifact.CompositeKey.ProviderVersion
                                                    HoldoutVersion = DatasetVersionRef.key request.Holdout
                                                    PredictionsVersion = DatasetVersionRef.key predictionsRef
                                                    MetricCount = Map.count metrics
                                                    ScopeId = request.ScopeId
                                                }
                                            )

                                        return Ok run
        }

        member _.GetTrackRecord(scopeId, artifactKeyHash) = async {
            let! runs =
                ModelEvaluationStorage.queryBy<EvaluationRun>
                    dataObjects
                    scopeId
                    ModelEvaluationStorage.RunDataType
                    (fun m -> Map.tryFind ModelEvaluationStorage.ArtifactKey m = Some artifactKeyHash)

            return runs |> List.sortBy _.EvaluatedAt
        }

        member _.GetFoldOutcomes(scopeId, runId) = foldOutcomesOf scopeId runId

        member _.Compare(request: ComparisonRequest) : Async<Result<ModelComparison, EvaluationError>> = async {
            let metrics = ResizeArray<string * float option>()

            for entrant in request.Entrants do
                let! run = tryGetRun request.ScopeId entrant request.Holdout

                let aggregate =
                    run |> Option.bind (fun r -> Map.tryFind request.PrimaryMetric r.Metrics)

                // Phase 652 — the fold evidence is read only when the
                // declared aggregation ranks on it. `AggregateMetric` (the
                // Phase 456 ranking) touches no fold store at all, so an
                // existing comparison does exactly the reads it always did.
                let! foldMetrics =
                    match request.Aggregation, run with
                    | FoldAggregation.AggregateMetric, _
                    | _, None -> async { return [] }
                    | _, Some r -> async {
                        let! outcomes = foldOutcomesOf request.ScopeId r.RunId
                        return outcomes |> List.map (fun o -> Map.tryFind request.PrimaryMetric o.Metrics)
                      }

                metrics.Add(
                    entrant,
                    FoldAggregation.resolve request.Aggregation request.Direction aggregate foldMetrics
                )

            let standings, missing, result =
                ModelComparison.order request.Direction (List.ofSeq metrics)

            let comparisonId =
                ModelComparison.idFor
                    request.ScopeId
                    request.Entrants
                    request.Holdout
                    request.PrimaryMetric
                    request.Direction
                    request.Aggregation

            let comparison: ModelComparison = {
                ComparisonId = comparisonId
                ScopeId = request.ScopeId
                Entrants = request.Entrants
                Holdout = request.Holdout
                PrimaryMetric = request.PrimaryMetric
                Direction = request.Direction
                Aggregation = Some request.Aggregation
                Standings = standings
                MissingMetric = missing
                Result = result
                ComparedBy = request.ComparedBy
                ComparedAt = DateTimeOffset.UtcNow
            }

            let sidecar =
                Map [ ModelEvaluationStorage.HoldoutKey, DatasetVersionRef.key request.Holdout ]

            match!
                ModelEvaluationStorage.save
                    dataObjects
                    request.ScopeId
                    comparisonId
                    ModelEvaluationStorage.ComparisonDataType
                    request.ComparedBy
                    sidecar
                    comparison
            with
            | Error e -> return Error e
            | Ok() -> return Ok comparison
        }

        member _.GetComparison(scopeId, comparisonId) = async {
            match! dataObjects.Get(scopeId, comparisonId) with
            | Error DataObjectError.NotFound -> return Error EvaluationError.NotFound
            | Error e -> return Error(ModelEvaluationStorage.mapDataObjectError e)
            | Ok(_, bytes) -> return ModelEvaluationStorage.decode<ModelComparison> bytes
        }

        member _.RegisterReevaluation(scopeId, artifactKeyHash, holdoutDatasetId, registeredBy) = async {
            let registrationId = EvaluationRegistration.id artifactKeyHash holdoutDatasetId

            // Idempotent: an existing (artifact, dataset) registration is
            // returned unchanged — no new version, nothing re-declared.
            match! dataObjects.Get(scopeId, registrationId) with
            | Ok(_, bytes) -> return ModelEvaluationStorage.decode<EvaluationRegistration> bytes
            | Error DataObjectError.NotFound ->
                let registration: EvaluationRegistration = {
                    RegistrationId = registrationId
                    ScopeId = scopeId
                    ArtifactKeyHash = artifactKeyHash
                    HoldoutDatasetId = holdoutDatasetId
                    RegisteredBy = registeredBy
                    RegisteredAt = DateTimeOffset.UtcNow
                }

                let sidecar =
                    Map [
                        ModelEvaluationStorage.RegistrationArtifactKey, artifactKeyHash
                        ModelEvaluationStorage.RegistrationDatasetKey, holdoutDatasetId
                    ]

                match!
                    ModelEvaluationStorage.save
                        dataObjects
                        scopeId
                        registrationId
                        ModelEvaluationStorage.RegistrationDataType
                        registeredBy
                        sidecar
                        registration
                with
                | Error e -> return Error e
                | Ok() -> return Ok registration
            | Error e -> return Error(ModelEvaluationStorage.mapDataObjectError e)
        }

        member _.ListReevaluationRegistrations(scopeId) =
            ModelEvaluationStorage.queryBy<EvaluationRegistration>
                dataObjects
                scopeId
                ModelEvaluationStorage.RegistrationDataType
                (fun _ -> true)

module ModelEvaluationRunner =
    /// Construct the default evaluation runner over the composed modelling
    /// substrate (Phase 449 providers, Phase 453 registry, Phase 454
    /// scorer, Phase 448 datasets). Nothing is registered by default —
    /// deployments compose this explicitly (GP 13).
    let create
        (fitProviders: ModelFitProviderRegistry)
        (registry: IModelRegistry)
        (scorer: IModelScorer)
        (datasets: IDatasetStore)
        (dataObjects: IDataObjectStore)
        (audit: IAuditLog)
        : IModelEvaluationRunner =
        ModelEvaluationRunner(fitProviders, registry, scorer, datasets, dataObjects, audit) :> IModelEvaluationRunner

/// Typed failure of a gated promotion (task 456.C). A refused promotion is
/// data, never an exception — and never a silent fallback to the ungated
/// transition.
[<RequireQualifiedAccess>]
type PromotionError =
    /// No stored comparison with the given id exists in the scope.
    | ComparisonNotFound
    /// The gate's verdict does not permit promotion — carries the typed
    /// verdict (fail-closed cases included).
    | GateRefused of PromotionVerdict
    /// Reading an entrant's registry record failed.
    | RegistryReadFailed of reason: string
    /// The Phase 453 transition itself was refused (role gate, illegal
    /// edge) or failed — the human gate is intact underneath this policy.
    | TransitionFailed of ModelRegistryError
    /// Reading the stored comparison failed.
    | StorageFailure of reason: string

module PromotionError =
    /// Human-readable one-line description for logs.
    let describe =
        function
        | PromotionError.ComparisonNotFound -> "comparison not found"
        | PromotionError.GateRefused v -> sprintf "promotion gate refused: %s" (PromotionVerdict.describe v)
        | PromotionError.RegistryReadFailed r -> sprintf "registry read failed: %s" r
        | PromotionError.TransitionFailed e -> sprintf "artifact transition failed: %s" (ModelRegistryError.describe e)
        | PromotionError.StorageFailure r -> sprintf "promotion storage failure: %s" r

/// The opt-in champion/challenger promotion gate (task 456.C): a challenger
/// may transition to `Approved` only with a stored comparison verdict
/// beating the current champion on the declared metric by the policy margin.
/// **The gate narrows; it never promotes** — the transition it permits is
/// still the human-initiated, Owner/Admin-gated Phase 453
/// `TransitionStatus`, and the gate is a separate service a governed
/// deployment composes explicitly (GP 13: deployments without it are
/// unchanged, and nothing anywhere calls it automatically).
type ChampionChallengerGate
    (registry: IModelRegistry, runner: IModelEvaluationRunner, audit: IAuditLog, policy: ChampionChallengerPolicy) =

    /// The current champion among the comparison's entrants: the
    /// best-standing entrant (per the comparison's own stored ordering —
    /// pure data, no fresh judgment) whose registry status is currently
    /// `Approved`, falling back to any `Approved` entrant in the missing
    /// list (whose metriclessness the gate then fails closed on). `None`
    /// when no entrant is `Approved`.
    let findChampion (scopeId: string) (comparison: ModelComparison) (challenger: string) = async {
        let ordered =
            (comparison.Standings |> List.map _.ArtifactKeyHash) @ comparison.MissingMetric
            |> List.filter (fun key -> key <> challenger)

        let mutable champion = None
        let mutable failure = None

        for key in ordered do
            if Option.isNone champion && Option.isNone failure then
                match! registry.Get(scopeId, key) with
                | Ok artifact when artifact.Status = ModelArtifactStatus.Approved -> champion <- Some key
                | Ok _ -> ()
                // A vanished entrant cannot be the champion; skip it.
                | Error ModelRegistryError.NotFound -> ()
                | Error e -> failure <- Some(ModelRegistryError.describe e)

        match failure with
        | Some reason -> return Error(PromotionError.RegistryReadFailed reason)
        | None -> return Ok champion
    }

    /// Attempt the gated promotion of `challengerKeyHash` to `Approved`
    /// under the stored comparison `comparisonId`. The gate checks the
    /// stored verdict (pure float comparison + policy margin, plan D10);
    /// only a permitted verdict reaches the Phase 453 transition — which
    /// still enforces the Owner/Admin role itself, so a policy pass never
    /// bypasses the human gate. A refusal is typed and audited as a denied
    /// transition (GP 6).
    member _.PromoteChallenger
        (scopeId: string, comparisonId: string, challengerKeyHash: string, callerRole: TeamRole, actorUserId: string)
        : Async<Result<ModelArtifact, PromotionError>> =
        async {
            match! runner.GetComparison(scopeId, comparisonId) with
            | Error EvaluationError.NotFound -> return Error PromotionError.ComparisonNotFound
            | Error e -> return Error(PromotionError.StorageFailure(EvaluationError.describe e))
            | Ok comparison ->
                match! findChampion scopeId comparison challengerKeyHash with
                | Error e -> return Error e
                | Ok champion ->
                    let verdict = PromotionGate.check policy comparison champion challengerKeyHash

                    if PromotionVerdict.permitsPromotion verdict then
                        match!
                            registry.TransitionStatus(
                                scopeId,
                                challengerKeyHash,
                                ModelArtifactStatus.Approved,
                                callerRole,
                                actorUserId
                            )
                        with
                        | Ok artifact -> return Ok artifact
                        | Error e -> return Error(PromotionError.TransitionFailed e)
                    else
                        // The refusal is itself audit-worthy — a denied
                        // promotion, in the Phase 453 denied-transition
                        // shape (no new audit vocabulary needed).
                        do!
                            audit.Record(
                                scopeId,
                                ModelArtifactTransitionDenied {
                                    CompositeKeyHash = challengerKeyHash
                                    AttemptedStatus = ModelArtifactStatus.name ModelArtifactStatus.Approved
                                    ActorUserId = actorUserId
                                    Reason = "champion-challenger gate: " + PromotionVerdict.describe verdict
                                    ScopeId = scopeId
                                }
                            )

                        return Error(PromotionError.GateRefused verdict)
        }

module ChampionChallengerGate =
    /// Construct the opt-in promotion gate. Deployments that never construct
    /// it keep ungated (but still role-gated) Phase 453 transitions (GP 13).
    let create
        (registry: IModelRegistry)
        (runner: IModelEvaluationRunner)
        (audit: IAuditLog)
        (policy: ChampionChallengerPolicy)
        : ChampionChallengerGate =
        ChampionChallengerGate(registry, runner, audit, policy)

module ModelEvaluationEnvelope =
    /// STJ options with the full F# converter set — the SDK-standard wire
    /// shape, so persisted job payloads round-trip faithfully.
    let private jsonOptions = FableConverters.create ()

    /// Serialise an `EvaluationRequest` to the persisted job-payload string.
    let serialiseRequest (request: EvaluationRequest) : string =
        JsonSerializer.Serialize(request, jsonOptions)

    /// Parse a persisted job payload back into an `EvaluationRequest`.
    /// `Error` carries the failure detail; a malformed payload is a
    /// `PermanentFailure` (it will not recover on retry).
    let tryParseRequest (payload: string) : Result<EvaluationRequest, string> =
        try
            Ok(JsonSerializer.Deserialize<EvaluationRequest>(payload, jsonOptions))
        with ex ->
            Error ex.Message

    /// Whether an `EvaluationError` is terminal (no retry recovers it) —
    /// the job handlers' `PermanentFailure` / `TransientFailure` split.
    let isTerminal (error: EvaluationError) : bool =
        match error with
        | EvaluationError.ArtifactNotFound
        | EvaluationError.ProviderNotFound _
        | EvaluationError.EvaluationUnsupported _
        | EvaluationError.ProviderRefused _
        // Phase 652 — neither an undeclared plan kind nor an invalid plan
        // becomes valid on retry: the plan is data, and the holdout frame is
        // an immutable vintage.
        | EvaluationError.PlanUnsupported _
        | EvaluationError.PlanInvalid _
        | EvaluationError.NotFound -> true
        | EvaluationError.ScoreFailed(ScoreError.ProviderNotFound _)
        | EvaluationError.ScoreFailed(ScoreError.NotApproved _)
        | EvaluationError.ScoreFailed(ScoreError.InputSchemaMismatch _) -> true
        | EvaluationError.ScoreFailed _
        | EvaluationError.RegistryFailure _
        | EvaluationError.ProviderFailed _
        | EvaluationError.InputUnavailable _
        | EvaluationError.StorageFailure _ -> false

/// `IJobHandler` bound to `_platform.modeleval.run` (task 456.A — runs are
/// jobs). Deserialises an `EvaluationRequest` and runs it through the
/// evaluation runner. Terminal refusals map to `PermanentFailure`;
/// registry / input / storage / provider faults map to `TransientFailure`.
type ModelEvaluationJobHandler(runner: IModelEvaluationRunner, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match ModelEvaluationEnvelope.tryParseRequest ctx.Payload with
            | Error e ->
                logger.Error($"ModelEvaluationJobHandler: malformed EvaluationRequest payload — {e}", None)
                return PermanentFailure $"malformed EvaluationRequest payload: {e}"
            | Ok request ->
                match! runner.Evaluate request with
                | Ok _ -> return Success
                | Error e when ModelEvaluationEnvelope.isTerminal e ->
                    logger.Warn $"ModelEvaluationJobHandler: terminal refusal — {EvaluationError.describe e}"
                    return PermanentFailure(EvaluationError.describe e)
                | Error e ->
                    logger.Warn $"ModelEvaluationJobHandler: transient failure — {EvaluationError.describe e}"
                    return TransientFailure(EvaluationError.describe e)
        }

module ModelEvaluationJobHandler =
    /// Reserved scheduler handler name for the evaluation job.
    [<Literal>]
    let HandlerName = "_platform.modeleval.run"

    /// Construct the evaluation job handler.
    let create (runner: IModelEvaluationRunner) (logger: ILogger) : IJobHandler =
        ModelEvaluationJobHandler(runner, logger) :> IJobHandler

/// The persisted payload for a `_platform.modeleval.revintage` sweep: which
/// scope to sweep and who the resulting runs are attributed to.
type ReevaluationSweepRequest = {
    /// Scope whose standing registrations are swept (GP 4).
    ScopeId: string
    /// Actor the sweep's evaluation runs are attributed to.
    EvaluatedBy: string
}

module ReevaluationSweepEnvelope =
    let private jsonOptions = FableConverters.create ()

    /// Serialise a `ReevaluationSweepRequest` to the persisted job payload.
    let serialiseRequest (request: ReevaluationSweepRequest) : string =
        JsonSerializer.Serialize(request, jsonOptions)

    /// Parse a persisted sweep payload. `Error` detail for the terminal
    /// malformed-payload case.
    let tryParseRequest (payload: string) : Result<ReevaluationSweepRequest, string> =
        try
            Ok(JsonSerializer.Deserialize<ReevaluationSweepRequest>(payload, jsonOptions))
        with ex ->
            Error ex.Message

/// `IJobHandler` bound to `_platform.modeleval.revintage` — the standing
/// re-evaluation sweep (task 456.D). For each standing registration in the
/// scope: resolve the holdout dataset's latest vintage and, if the artifact
/// has not yet been evaluated against it, run the evaluation — so the
/// out-of-time track record accumulates per artifact with no bespoke code.
/// Idempotent by construction (the deterministic `EvaluationRun.id` makes an
/// already-evaluated vintage a skip), so a deployment schedules this as an
/// ordinary recurring job at whatever cadence vintages land.
///
/// Failure posture: a registration whose evaluation fails *terminally*
/// (e.g. its provider was uncomposed) is logged and skipped — one broken
/// registration must not wedge the standing sweep; a *transient* failure
/// fails the sweep `TransientFailure`, so the scheduler retries and the
/// idempotent skip re-runs only what is still missing.
type VintageReevaluationJobHandler(runner: IModelEvaluationRunner, datasets: IDatasetStore, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match ReevaluationSweepEnvelope.tryParseRequest ctx.Payload with
            | Error e ->
                logger.Error($"VintageReevaluationJobHandler: malformed ReevaluationSweepRequest — {e}", None)
                return PermanentFailure $"malformed ReevaluationSweepRequest payload: {e}"
            | Ok request ->
                let! registrations = runner.ListReevaluationRegistrations request.ScopeId

                let mutable evaluated = 0
                let mutable transientFailures: string list = []

                for registration in registrations do
                    match! datasets.GetLatest(request.ScopeId, registration.HoldoutDatasetId) with
                    | Error DatasetError.NotFound ->
                        // No vintage has landed yet — the registration is
                        // simply waiting; nothing to do this sweep.
                        ()
                    | Error e ->
                        transientFailures <-
                            sprintf
                                "dataset '%s' unreadable: %s"
                                registration.HoldoutDatasetId
                                (DatasetError.describe e)
                            :: transientFailures
                    | Ok latest ->
                        let latestRef: DatasetVersionRef = {
                            ScopeId = request.ScopeId
                            DatasetId = registration.HoldoutDatasetId
                            Version = latest.Version
                        }

                        let! trackRecord = runner.GetTrackRecord(request.ScopeId, registration.ArtifactKeyHash)

                        let alreadyEvaluated =
                            trackRecord |> List.exists (fun run -> run.Holdout = latestRef)

                        if not alreadyEvaluated then
                            let evaluation: EvaluationRequest = {
                                ScopeId = request.ScopeId
                                ArtifactKeyHash = registration.ArtifactKeyHash
                                Holdout = latestRef
                                // A standing registration declares an
                                // artifact + dataset, not a fold family, so
                                // the sweep runs the degenerate plan — the
                                // Phase 456 behaviour, unchanged by phase
                                // 652.
                                Plan = None
                                EvaluatedBy = request.EvaluatedBy
                            }

                            match! runner.Evaluate evaluation with
                            | Ok _ -> evaluated <- evaluated + 1
                            | Error e when ModelEvaluationEnvelope.isTerminal e ->
                                logger.Warn(
                                    sprintf
                                        "VintageReevaluationJobHandler: registration for artifact '%s' skipped (terminal): %s"
                                        registration.ArtifactKeyHash
                                        (EvaluationError.describe e)
                                )
                            | Error e -> transientFailures <- EvaluationError.describe e :: transientFailures

                if List.isEmpty transientFailures then
                    logger.Info(
                        sprintf
                            "VintageReevaluationJobHandler: swept %d registration(s), %d new evaluation(s)"
                            registrations.Length
                            evaluated
                    )

                    return Success
                else
                    return
                        TransientFailure(
                            sprintf
                                "%d registration(s) failed transiently: %s"
                                transientFailures.Length
                                (String.concat "; " (List.rev transientFailures))
                        )
        }

module VintageReevaluationJobHandler =
    /// Reserved scheduler handler name for the standing re-evaluation sweep.
    [<Literal>]
    let HandlerName = "_platform.modeleval.revintage"

    /// Construct the standing re-evaluation sweep handler.
    let create (runner: IModelEvaluationRunner) (datasets: IDatasetStore) (logger: ILogger) : IJobHandler =
        VintageReevaluationJobHandler(runner, datasets, logger) :> IJobHandler