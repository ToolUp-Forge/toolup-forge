// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module internal ToolUp.Platform.RemotingAuditDrainService

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open ToolUp.Platform

// ─── RemotingAuditDrainService (Phase 856.C) ─────────────────────
//
// Drains `RemotingAuditQueue` (Api.fs): the default remoting audit
// emitter enqueues each record a successful audited call produces and
// returns, so the audit write is off the response path; this service
// performs the write. Registered by compose exactly when the queue is
// (see `ComposeRuntimeServices.registerRemotingAuditQueue`), so a
// deployment without it writes inline as it always did.
//
// A write the audit log does not classify itself — `EventStoreAuditLog`
// catches, counts and warns on its own failures, so this is a custom
// `IAuditLog`, or a fault outside the write — is given the audit log's
// own failure shape here: the `toolup.audit.write_failures_total`
// counter tagged with the event type, and the same Warn. Never dropped
// silently.

/// Write one queued record, classifying a failure the log let escape.
let internal writeOne (logger: ILogger) (metrics: unit -> Metrics.IMetricsSink) (work: RemotingAuditWork) : Task =
    task {
        try
            do! work.AuditLog.Record(work.ScopeId, work.Event) |> Async.StartAsTask
        with ex ->
            let eventTypeName = AuditEvent.eventTypeName work.Event

            (metrics ()).Increment(AuditLog.AuditMetrics.WriteFailuresTotal, Map [ "event_type", eventTypeName ])

            logger.Warn(
                sprintf
                    "[AuditLog] write failed scope=%s eventType=%s: %s (queued remoting audit, Phase 856)"
                    work.ScopeId
                    eventTypeName
                    ex.Message
            )
    }
    :> Task

type internal RemotingAuditDrainService
    (queue: RemotingAuditQueue, logger: ILogger, metrics: unit -> Metrics.IMetricsSink) =
    inherit BackgroundService()

    member private _.StopBase(cancellationToken: CancellationToken) = base.StopAsync cancellationToken

    /// Runs until the queue's writer is COMPLETED — not until the host's
    /// stop signal — so every record accepted before shutdown is written.
    override _.ExecuteAsync(_stoppingToken: CancellationToken) =
        task {
            let reader = queue.Reader
            let mutable running = true

            while running do
                let! more = reader.WaitToReadAsync()

                if not more then
                    running <- false
                else
                    let mutable work = Unchecked.defaultof<RemotingAuditWork>

                    while reader.TryRead(&work) do
                        do! writeOne logger metrics work
        }
        :> Task

    /// Close the queue (a late emission then writes inline), let the drain
    /// finish within the host's shutdown timeout, and say so loudly if it
    /// could not.
    override this.StopAsync(cancellationToken: CancellationToken) =
        task {
            queue.Complete()
            do! this.StopBase cancellationToken
            let left = queue.Count

            if left > 0 then
                logger.Error(
                    sprintf
                        "[RemotingAudit] event=undrained_at_shutdown count=%d — the host's shutdown timeout expired before these audit records were written"
                        left,
                    None
                )
        }
        :> Task