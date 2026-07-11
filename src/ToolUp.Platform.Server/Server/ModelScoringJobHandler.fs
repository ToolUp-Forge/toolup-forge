// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 454 — batch scoring job + IJobHandler ────────────────────────
//
// `ModelScorer` (in `IModelScorer.fs`) is the small-frame synchronous path
// for interactive scoring. This file fuses the same envelope onto
// `IJobScheduler` for scheduled / batch scoring: a `ScoreJobRequest` is the
// persisted job payload, and `ModelScoringJobHandler` reconstructs the
// governed artifact from `IModelRegistry` before delegating to the scorer.
//
// The payload references the artifact by its composite-key hash rather than
// embedding the whole `ModelArtifact`, so a scheduled score always reads the
// artifact's *current* lifecycle status at run time — a batch job scheduled
// while an artifact was `Fitted` correctly refuses under an approved-only
// policy if the artifact was retired before the job ran, and picks up an
// `Approved` promotion that landed after scheduling.

/// The persisted payload for a `_platform.modelscore.run` job: which artifact
/// (by composite-key hash), which input vintage, where the predictions land,
/// and who to attribute. The handler fetches the artifact from
/// `IModelRegistry` and hands a `ScoreRequest` to the scorer.
type ScoreJobRequest = {
    /// Scope the score runs + writes under (GP 4).
    ScopeId: string
    /// Composite-key hash of the artifact to score with (Phase 453).
    ArtifactKeyHash: string
    /// The immutable input vintage to score (Phase 448).
    Input: DatasetVersionRef
    /// Dataset id the predictions land under.
    OutputDatasetId: string
    /// Actor recorded as the output vintage's `CreatedBy`.
    ScoredBy: string
}

module ModelScoringEnvelope =
    /// STJ options with the full F# converter set — matches the
    /// Fable-compatible wire shape used everywhere else in the SDK, so a
    /// persisted `ScoreJobRequest` round-trips faithfully.
    let private jsonOptions = FableConverters.create ()

    /// Serialise a `ScoreJobRequest` to the persisted job-payload string.
    let serialiseRequest (request: ScoreJobRequest) : string =
        JsonSerializer.Serialize(request, jsonOptions)

    /// Parse a persisted job payload back into a `ScoreJobRequest`. `Error`
    /// carries the failure detail; a malformed payload is a `PermanentFailure`
    /// (it will not recover on retry).
    let tryParseRequest (payload: string) : Result<ScoreJobRequest, string> =
        try
            Ok(JsonSerializer.Deserialize<ScoreJobRequest>(payload, jsonOptions))
        with ex ->
            Error ex.Message

/// `IJobHandler` bound to `_platform.modelscore.run`. Deserialises a
/// `ScoreJobRequest`, fetches the governed artifact from `IModelRegistry`,
/// and runs it through the scorer. Failure mapping:
///   * malformed payload / artifact not found / terminal `ScoreError`
///     (`ProviderNotFound` / `NotApproved` / `InputSchemaMismatch`) →
///     `PermanentFailure` (no retry recovers it);
///   * registry / input / storage / provider faults
///     (`InputUnavailable` / `StorageFailure` / `ProviderFailed`) →
///     `TransientFailure` (a later attempt may succeed).
type ModelScoringJobHandler(registry: IModelRegistry, scorer: IModelScorer, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match ModelScoringEnvelope.tryParseRequest ctx.Payload with
            | Error e ->
                logger.Error($"ModelScoringJobHandler: malformed ScoreJobRequest payload — {e}", None)
                return PermanentFailure $"malformed ScoreJobRequest payload: {e}"
            | Ok request ->
                match! registry.Get(request.ScopeId, request.ArtifactKeyHash) with
                | Error ModelRegistryError.NotFound ->
                    logger.Warn
                        $"ModelScoringJobHandler: no artifact '{request.ArtifactKeyHash}' in scope '{request.ScopeId}'"

                    return PermanentFailure $"model artifact '{request.ArtifactKeyHash}' not found in scope"
                | Error e ->
                    logger.Warn $"ModelScoringJobHandler: registry read failed — {ModelRegistryError.describe e}"
                    return TransientFailure(ModelRegistryError.describe e)
                | Ok artifact ->
                    let scoreRequest: ScoreRequest = {
                        ScopeId = request.ScopeId
                        Artifact = artifact
                        Input = request.Input
                        OutputDatasetId = request.OutputDatasetId
                        ScoredBy = request.ScoredBy
                    }

                    match! scorer.Score scoreRequest with
                    | Ok _ -> return Success
                    | Error(ScoreError.ProviderNotFound _ as e)
                    | Error(ScoreError.NotApproved _ as e)
                    | Error(ScoreError.InputSchemaMismatch _ as e) ->
                        logger.Warn $"ModelScoringJobHandler: terminal refusal — {ScoreError.describe e}"
                        return PermanentFailure(ScoreError.describe e)
                    | Error(ScoreError.InputUnavailable _ as e)
                    | Error(ScoreError.StorageFailure _ as e)
                    | Error(ScoreError.ProviderFailed _ as e) ->
                        logger.Warn $"ModelScoringJobHandler: transient failure — {ScoreError.describe e}"
                        return TransientFailure(ScoreError.describe e)
        }

module ModelScoringJobHandler =
    /// Reserved scheduler handler name for the batch-scoring job.
    [<Literal>]
    let HandlerName = "_platform.modelscore.run"

    /// Construct the batch-scoring job handler.
    let create (registry: IModelRegistry) (scorer: IModelScorer) (logger: ILogger) : IJobHandler =
        ModelScoringJobHandler(registry, scorer, logger) :> IJobHandler