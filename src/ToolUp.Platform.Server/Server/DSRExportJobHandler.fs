// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.IDataExporter
open ToolUp.Platform.ErasurePipeline
open ToolUp.Platform.DataSubjectRequestApi
open ToolUp.Platform.DataSubjectRequestApiHandler

// ─── Phase 9h.A — DSR background export handler ─────────────────────────
//
// The synchronous Phase 9h `RequestExport` assembles the whole envelope
// on the HTTP thread. For large tenants that crosses the HTTP timeout;
// this handler runs the same `executeExport` on an `IJobScheduler` job
// and writes the result into an `IBackgroundExportStore` ticket the
// client polls.
//
// `DsrJobs` (handler names) + `DsrJobPayload` (serialise / parse) moved
// up to `DataSubjectRequestApiHandler.fs` in Phase 9h.A so the async
// `RequestExportAsync` API path can schedule jobs; they resolve here via
// the `open DataSubjectRequestApiHandler` above.

/// Publish a background-export progress signal (`Preparing` → `Ready` /
/// `Failed` / `Cancelled`) under `DsrNotifications.ExportProgressKey`.
/// No-op when no `INotificationChannel` is composed. Wrapped in a module
/// because a namespace cannot hold a bare `let`.
[<AutoOpen>]
module private DsrExportProgress =
    let publishProgress
        (channel: INotificationChannel option)
        (scopeId: string)
        (ticket: string)
        (status: ExportStatus)
        : Async<unit> =
        async {
            match channel with
            | None -> return ()
            | Some ch ->
                let json = $"{{\"ticket\":\"{ticket}\",\"status\":\"{ExportStatus.name status}\"}}"

                try
                    do! ch.Publish(scopeId, CustomNotification(DsrNotifications.ExportProgressKey, json))
                with _ ->
                    // Best-effort — a notification failure must never fail the
                    // export job (the ticket status remains the source of truth).
                    return ()
        }

/// `IJobHandler` for `_platform.datasubject.export`. Resolves the
/// registered exporters at construction (DI), runs `executeExport` on
/// the job, assembles the canonical envelope, and `Complete`s the
/// ticket. On failure the ticket flips to `Failed` and the job returns
/// `TransientFailure` so `IJobScheduler` retries per the job's
/// `JobRetryPolicy`.
///
/// `notifyChannel` (optional) publishes coarse progress milestones; a
/// ticket cancelled mid-run (`CancelExport`) is observed before
/// `Complete`, so the job skips writing the envelope and the partial
/// state TTL-expires (acceptance: cancellation leaves the system
/// consistent).
type DSRExportJobHandler
    (
        store: IBackgroundExportStore,
        exporters: IDataExporter list,
        audit: AuditOnDsr,
        notifyChannel: INotificationChannel option,
        logger: ILogger
    ) =

    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match DsrJobPayload.parse ctx.Payload with
            | Error e ->
                logger.Error($"DSRExportJobHandler: malformed payload — {e}", None)
                // A malformed payload won't fix itself on retry.
                return PermanentFailure $"malformed DSR export payload: {e}"
            | Ok(ticket, request) ->
                try
                    let! segments = executeExport exporters request ctx.ScopeId
                    let envelope = serialiseSegments segments

                    // Honour a cancel that landed while the export ran —
                    // don't overwrite a Cancelled ticket with Ready.
                    let! current = store.GetStatus ticket

                    match current with
                    | ExportStatus.Cancelled ->
                        logger.Info $"DSRExportJobHandler: ticket {ticket} cancelled mid-run — skipping Complete."
                        return Success
                    | _ ->
                        do! store.Complete(ticket, envelope)
                        do! publishProgress notifyChannel ctx.ScopeId ticket (ExportStatus.Ready(int64 envelope.Length))

                        do!
                            audit {
                                RequestId = request.Id
                                Kind = ExportCompleted
                                SubjectUserId = request.SubjectUserId
                                ScopeId = ctx.ScopeId
                                Actor = request.RequestedBy
                                Reason = request.Reason
                                Properties =
                                    Map [
                                        "ticket", ticket
                                        "segmentCount", string segments.Length
                                        "totalBytes", string envelope.Length
                                    ]
                            }

                        return Success
                with ex ->
                    do! store.Fail(ticket, ex.Message)
                    do! publishProgress notifyChannel ctx.ScopeId ticket (ExportStatus.Failed ex.Message)
                    logger.Warn $"DSRExportJobHandler: export failed for {request.Id} — {ex.Message}"
                    return TransientFailure ex.Message
        }

module DSRExportJobHandler =
    /// Construct the export job handler with progress notifications.
    /// `exporters` are the registered `IDataExporter`s resolved from DI
    /// at compose time; `notifyChannel` publishes coarse progress
    /// milestones (pass `None` to disable).
    let createWithNotifications
        (store: IBackgroundExportStore)
        (exporters: IDataExporter list)
        (audit: AuditOnDsr)
        (notifyChannel: INotificationChannel option)
        (logger: ILogger)
        : IJobHandler =
        DSRExportJobHandler(store, exporters, audit, notifyChannel, logger) :> IJobHandler

    /// Construct the export job handler without progress notifications.
    let create
        (store: IBackgroundExportStore)
        (exporters: IDataExporter list)
        (audit: AuditOnDsr)
        (logger: ILogger)
        : IJobHandler =
        createWithNotifications store exporters audit None logger