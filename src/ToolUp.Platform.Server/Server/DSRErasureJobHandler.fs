// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.IDataExporter
open ToolUp.Platform.ErasurePipeline
open ToolUp.Platform.DataSubjectRequestApiHandler

// ─── Phase 9h.A — DSR background erasure handler ────────────────────────
//
// `IJobHandler` for `_platform.datasubject.erasure`. Runs the same
// `executeErase` the synchronous Phase 9h `ConfirmErasure` path runs, but
// on an `IJobScheduler` job so a large descendants-before-ancestors
// erasure walk survives a process restart (`IJobStore` re-dispatches the
// persisted job) and doesn't block an HTTP thread. Erasure produces no
// downloadable artefact, so there is no `IBackgroundExportStore` ticket —
// the job emits `ErasureCompleted` / `ErasureFailed` audit and is done.

type DSRErasureJobHandler(handlers: IErasureHandler list, audit: AuditOnDsr, logger: ILogger) =

    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match DsrJobPayload.parse ctx.Payload with
            | Error e ->
                logger.Error($"DSRErasureJobHandler: malformed payload — {e}", None)
                return PermanentFailure $"malformed DSR erasure payload: {e}"
            | Ok(_ticket, request) ->
                try
                    let! outcome = executeErase handlers request ctx.ScopeId

                    let totalAffected =
                        outcome.PerHandler.Values
                        |> Seq.sumBy (fun r ->
                            match r with
                            | Ok s -> s.RecordsAffected
                            | _ -> 0)

                    let kind =
                        if outcome.OverallSuccess then
                            ErasureCompleted
                        else
                            ErasureFailed

                    do!
                        audit {
                            RequestId = request.Id
                            Kind = kind
                            SubjectUserId = request.SubjectUserId
                            ScopeId = ctx.ScopeId
                            Actor = request.RequestedBy
                            Reason = request.Reason
                            Properties =
                                Map [
                                    "totalAffected", string totalAffected
                                    "handlerCount", string outcome.PerHandler.Count
                                    "overallSuccess", string outcome.OverallSuccess
                                ]
                        }

                    // A partial-failure erasure is recorded (ErasureFailed
                    // audit) but the job itself succeeds — re-running would
                    // re-erase already-erased handlers. Operators triage
                    // off the audit trail, matching the synchronous path.
                    return Success
                with ex ->
                    logger.Warn $"DSRErasureJobHandler: erasure threw for {request.Id} — {ex.Message}"
                    return TransientFailure ex.Message
        }

module DSRErasureJobHandler =
    /// Construct the erasure job handler. `handlers` are the registered
    /// `IErasureHandler`s resolved from DI at compose time.
    let create (handlers: IErasureHandler list) (audit: AuditOnDsr) (logger: ILogger) : IJobHandler =
        DSRErasureJobHandler(handlers, audit, logger) :> IJobHandler