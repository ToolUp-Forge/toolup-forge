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

/// Phase 1027 — the relay drain both outboxes run on: every minute, one
/// `relayOnce` pass, then the remaining count when it moved anything. Log
/// lines are tagged `[<label>]`. The entity outbox (Phase 599) and the
/// lineage outbox (Phase 1027) are its two hosts, and both are gated as
/// `EntityOutboxRelaySubsystem`.
type OutboxRelayService(label: string, relayOnce: int -> Async<int>, pendingCount: unit -> Async<int>, logger: ILogger)
    =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    do! Task.Delay(relayInterval, stoppingToken)

                    let! published = relayOnce relayBatchSize |> Async.StartAsTask

                    if published > 0 then
                        let! remaining = pendingCount () |> Async.StartAsTask
                        logger.Info $"[{label}] event=relay_pass published={published} remaining={remaining}"
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error($"[{label}] event=relay_loop_error", Some ex)
        }
        :> Task

/// The Phase 599 entity-outbox drain.
type EntityOutboxRelayService(outbox: EntityOutbox.OutboxEntityStore, logger: ILogger) =
    inherit OutboxRelayService("EntityOutbox", outbox.RelayOnce, outbox.PendingCount, logger)

    // Kept so the Phase 599 type's public surface is unchanged by the move
    // of the drain loop into `OutboxRelayService`.
    override _.ExecuteAsync(stoppingToken: CancellationToken) = base.ExecuteAsync stoppingToken