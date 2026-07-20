module ToolUp.Platform.AuditFallbackReplayService

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open ToolUp.Platform

// ─── AuditFallbackReplayService (Phase 9t) ───────────────────────
//
// Periodic drain for the `DegradeToFile` audit spill: every minute,
// replay accumulated fallback records into the live `IEventStore`
// (oldest first, batch-bounded, halt-on-store-failure — the
// semantics live on `AuditFallbackStore.ReplayOnce`). Registered by
// compose only when `ServerConfig.AuditFailurePolicy = DegradeToFile`
// and `AuditLog = EnabledAuditLog`, gated by
// `ProcessProfileGate` (`AuditFallbackReplaySubsystem`) like every
// other background subsystem — a deployment on any other policy
// pays nothing (GP 13).
//
// Replayed events flow through the DI-registered decorated event
// store, so webhook fan-out and `OnEvent` job triggers fire for the
// recovered records exactly as they would have live — late, but not
// lost.

let private replayInterval = TimeSpan.FromSeconds 60.0

let private replayBatchSize = 200

type AuditFallbackReplayService
    (fallbackStore: AuditFallbackStore.AuditFallbackStore, eventStore: IEventStore, logger: ILogger) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    do! Task.Delay(replayInterval, stoppingToken)

                    let! replayed = fallbackStore.ReplayOnce(eventStore, replayBatchSize) |> Async.StartAsTask

                    if replayed > 0 then
                        let remaining = fallbackStore.PendingCount()

                        logger.Info
                            $"[AuditFallback] event=replayed count={replayed} remaining={remaining} root={fallbackStore.Root}"
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error("[AuditFallback] event=replay_loop_error", Some ex)
        }
        :> Task