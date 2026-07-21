module ToolUp.Platform.EntityOutboxRelayService

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open ToolUp.Platform

// ─── EntityOutboxRelayService (Phase 599) ────────────────────────
//
// Periodic drain for staged entity-outbox intents: every minute,
// publish the events of every settled intent whose entity save is
// version-witnessed as committed, and discard intents whose save
// never landed (the semantics live on `OutboxEntityStore.RelayOnce`).
// Registered by compose only when `ServerConfig.EntityOutbox =
// EnabledEntityOutbox` (alongside `EntityStore = EnabledEntityStore`),
// gated as `EntityOutboxRelaySubsystem` on the process-profile
// matrix — deployments that don't opt in pay nothing (GP 13).

let private relayInterval = TimeSpan.FromSeconds 60.0

let private relayBatchSize = 200

type EntityOutboxRelayService(outbox: EntityOutbox.OutboxEntityStore, logger: ILogger) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    do! Task.Delay(relayInterval, stoppingToken)

                    let! published = outbox.RelayOnce relayBatchSize |> Async.StartAsTask

                    if published > 0 then
                        let! remaining = outbox.PendingCount() |> Async.StartAsTask
                        logger.Info $"[EntityOutbox] event=relay_pass published={published} remaining={remaining}"
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error("[EntityOutbox] event=relay_loop_error", Some ex)
        }
        :> Task