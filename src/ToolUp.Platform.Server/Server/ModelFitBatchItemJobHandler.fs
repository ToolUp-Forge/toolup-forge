// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Batch item execution ───────────────────────────────────────────────
//
// The job bound to `_platform.modelfit.batchitem`: run one batch item's fit
// through the fit envelope, then register the outcome into `IModelRegistry`
// carrying the batch annotations (`batch.id` / `batch.index`) — which is
// what makes a whole wave's outcomes retrievable in one `QueryPage` call.
// Lives in its own file because it needs `IModelRegistry`, which compiles
// after `ModelFitJobHandler.fs`.
//
// Failure semantics mirror the single-item job: a malformed payload, an
// unknown provider or a gate-unit mismatch is terminal; a provider fault is
// retryable; a registration failure after a successful fit is retryable
// (the registry is idempotent under the composite key, so the retry
// re-registers safely — the fit itself re-runs deterministically for a
// deterministic provider). One item's failure is its own job's failure;
// sibling items are separate jobs and never aborted.

/// The batch-item job, bound to `_platform.modelfit.batchitem`.
module ModelFitBatchItemJobHandler =

    /// The batch-item job: fit one item, then register the outcome with the
    /// batch annotations. A fit failure classes as the single-item job's
    /// does; a registration failure after a successful fit is retryable.
    let job
        (providers: ModelFitProviderRegistry)
        (modelRegistry: IModelRegistry)
        (audit: IAuditLog)
        : RunJob<FitBatchItemPayload> =
        RunJob.ofPayload ModelFitBatch.ItemHandlerName (fun _ item -> async {
            match! ModelFitEnvelope.runFit providers audit item.Request with
            | Error e -> return Error(ModelFitJobHandler.failureOf e)
            | Ok outcome ->
                let annotations =
                    Map [
                        FitRequestBatch.BatchIdAnnotationKey, item.BatchId
                        FitRequestBatch.BatchIndexAnnotationKey, string item.Index
                    ]

                let registeredBy = $"_platform.modelfit.batch/{item.BatchId}"

                match! modelRegistry.Register(item.Request.ScopeId, outcome, registeredBy, annotations, "") with
                | Ok _ -> return Ok()
                | Error e ->
                    return
                        Error(
                            RunFailure.Retryable
                                $"registration failed (batch {item.BatchId}): {ModelRegistryError.describe e}"
                        )
        })

    /// Construct the batch-item job handler.
    let create
        (providers: ModelFitProviderRegistry)
        (modelRegistry: IModelRegistry)
        (audit: IAuditLog)
        (logger: ILogger)
        : IJobHandler =
        RunJob.handler logger (job providers modelRegistry audit)