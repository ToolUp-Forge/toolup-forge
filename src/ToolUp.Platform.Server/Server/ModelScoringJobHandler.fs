// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Batch scoring job ──────────────────────────────────────────────────
//
// `ModelScorer` (in `IModelScorer.fs`) is the small-frame synchronous path
// for interactive scoring. This file fuses the same envelope onto
// `IJobScheduler` for scheduled / batch scoring: a `ScoreJobRequest` is the
// persisted job payload, and the `ModelScoringJobHandler` job reconstructs the
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
    /// Composite-key hash of the artifact to score with.
    ArtifactKeyHash: string
    /// The immutable input vintage to score.
    Input: DatasetVersionRef
    /// Dataset id the predictions land under.
    OutputDatasetId: string
    /// Actor recorded as the output vintage's `CreatedBy`.
    ScoredBy: string
}

/// The batch-scoring job, bound to `_platform.modelscore.run`: parse a
/// `ScoreJobRequest`, fetch the governed artifact from `IModelRegistry`, and
/// run it through the scorer.
module ModelScoringJobHandler =
    /// Reserved scheduler handler name for the batch-scoring job.
    [<Literal>]
    let HandlerName = "_platform.modelscore.run"

    /// A scoring failure as the scheduler acts on it: a missing provider, an
    /// unapproved artifact and an input-schema mismatch are terminal; an
    /// unavailable input, a storage fault and a provider fault may clear.
    let failureOf (error: ScoreError) : RunFailure =
        match error with
        | ScoreError.ProviderNotFound _
        | ScoreError.NotApproved _
        | ScoreError.InputSchemaMismatch _ -> RunFailure.Terminal(ScoreError.describe error)
        | ScoreError.InputUnavailable _
        | ScoreError.StorageFailure _
        | ScoreError.ProviderFailed _ -> RunFailure.Retryable(ScoreError.describe error)

    /// The batch-scoring job. An artifact the scope does not hold is
    /// terminal; any other registry failure may clear.
    let job (registry: IModelRegistry) (scorer: IModelScorer) : RunJob<ScoreJobRequest> =
        RunJob.ofPayload HandlerName (fun _ request -> async {
            match! registry.Get(request.ScopeId, request.ArtifactKeyHash) with
            | Error ModelRegistryError.NotFound ->
                return Error(RunFailure.Terminal $"model artifact '{request.ArtifactKeyHash}' not found in scope")
            | Error e -> return Error(RunFailure.Retryable(ModelRegistryError.describe e))
            | Ok artifact ->
                let scoreRequest: ScoreRequest = {
                    ScopeId = request.ScopeId
                    Artifact = artifact
                    Input = request.Input
                    OutputDatasetId = request.OutputDatasetId
                    ScoredBy = request.ScoredBy
                }

                match! scorer.Score scoreRequest with
                | Ok _ -> return Ok()
                | Error e -> return Error(failureOf e)
        })

    /// Construct the batch-scoring job handler.
    let create (registry: IModelRegistry) (scorer: IModelScorer) (logger: ILogger) : IJobHandler =
        RunJob.handler logger (job registry scorer)