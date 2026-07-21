// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 599 — batch item execution ───────────────────────────────────
//
// The `IJobHandler` bound to `_platform.modelfit.batchitem`: run one batch
// item's fit through the unchanged Phase 449 envelope, then register the
// outcome into `IModelRegistry` carrying the batch annotations
// (`batch.id` / `batch.index`) — which is what makes a whole wave's
// outcomes retrievable in one `QueryPage` call (bulk outcome retrieval).
// Lives in its own file because it needs `IModelRegistry`, which compiles
// after `ModelFitJobHandler.fs`.
//
// Failure semantics mirror the single-item handler: malformed payload /
// unknown provider → `PermanentFailure`; provider raised →
// `TransientFailure`; a registration failure after a successful fit is
// `TransientFailure` (the registry is idempotent under the composite key,
// so the retry re-registers safely — the fit itself re-runs
// deterministically for a deterministic provider). One item's failure is
// its own job's failure; sibling items are separate jobs and never
// aborted.

/// `IJobHandler` bound to `_platform.modelfit.batchitem` (Phase 599).
type ModelFitBatchItemJobHandler
    (providers: ModelFitProviderRegistry, modelRegistry: IModelRegistry, audit: IAuditLog, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match ModelFitBatch.tryParseItem ctx.Payload with
            | Error e ->
                logger.Error($"ModelFitBatchItemJobHandler: malformed FitBatchItemPayload — {e}", None)
                return PermanentFailure $"malformed FitBatchItemPayload: {e}"
            | Ok item ->
                match! ModelFitEnvelope.runFit providers audit item.Request with
                | Error(ModelFitError.ProviderNotFound k) ->
                    logger.Warn $"ModelFitBatchItemJobHandler: no provider for kind '{k}' (batch {item.BatchId})"
                    return PermanentFailure(ModelFitError.describe (ModelFitError.ProviderNotFound k))
                | Error(ModelFitError.ProviderFailed(k, m)) ->
                    logger.Warn $"ModelFitBatchItemJobHandler: provider '{k}' failed (batch {item.BatchId}) — {m}"
                    return TransientFailure(ModelFitError.describe (ModelFitError.ProviderFailed(k, m)))
                | Ok outcome ->
                    let annotations =
                        Map [
                            FitRequestBatch.BatchIdAnnotationKey, item.BatchId
                            FitRequestBatch.BatchIndexAnnotationKey, string item.Index
                        ]

                    let registeredBy = $"_platform.modelfit.batch/{item.BatchId}"

                    match! modelRegistry.Register(item.Request.ScopeId, outcome, registeredBy, annotations, "") with
                    | Ok _ -> return Success
                    | Error e ->
                        logger.Warn
                            $"ModelFitBatchItemJobHandler: registration failed (batch {item.BatchId}) — {ModelRegistryError.describe e}"
                        // Registration is idempotent under the composite key;
                        // the deterministic fit re-runs safely on retry.
                        return TransientFailure(ModelRegistryError.describe e)
        }

module ModelFitBatchItemJobHandler =
    /// Construct the batch-item job handler.
    let create
        (providers: ModelFitProviderRegistry)
        (modelRegistry: IModelRegistry)
        (audit: IAuditLog)
        (logger: ILogger)
        : IJobHandler =
        ModelFitBatchItemJobHandler(providers, modelRegistry, audit, logger) :> IJobHandler