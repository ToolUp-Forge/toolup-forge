// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Compose.ComposeObservability

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.Config

// ─── compose phase: observability substrate ─────────────────────────────
//
// Phase 828 — the first member of the observability compose family: the
// self-hosted log store (`ILogStore`), the logger decorator that feeds it,
// and its retention sweep. The other observability subsystems (metrics
// sink, activity sink, health-state tracker, alert rules) were composed
// inline in `compose` or under `ComposeRuntimeServices` before this file
// existed and are deliberately left where they are — moving them would be
// an unrelated refactor of files sibling sessions are editing.
//
// The log store is composed in three separate steps because the logger
// decorator has to exist BEFORE the logger is handed to the rest of
// `compose`, while the DI registrations happen with the other stores much
// later:
//
//   1. `createLogStore`  — opens the store (or `None` on the default).
//   2. `decorateLogger`  — wraps the resolved `ILogger` around it.
//   3. `registerLogStore` — DI singleton + the retention sweep.
//
// Every step is a no-op under `LogStoreMode.NoLogStore`, which is the
// default: no file, no decorator, no hosted service, no DI entry (GP 13),
// and a boot path byte-for-byte what it was (GP 11).

/// Open the configured log store, or `None` when the deployment has not
/// opted in. Returns the store beside the config it was opened with, since
/// the retention sweep needs both and re-deriving the config would let the
/// two drift.
let createLogStore (config: ServerConfig) : (ILogStore * LogStoreConfig) option =
    match config.Observability.LogStore with
    | NoLogStore -> None
    | SqliteLogStore storeConfig -> Some(SqliteLogStore.create storeConfig, storeConfig)

/// Wrap `logger` so every line it receives is also recorded in the store.
/// Returns `logger` unchanged when no store was composed — the identity
/// that makes the opt-out free.
let decorateLogger (store: (ILogStore * LogStoreConfig) option) (logger: ILogger) : ILogger =
    match store with
    | None -> logger
    | Some(logStore, _) -> LogStoreLogger.create logger logStore

/// Register the composed store and its retention sweep. `TryAddSingleton`,
/// so a consumer that registered its own `ILogStore` companion before
/// calling `compose` keeps it — the same courtesy every other store
/// registration extends.
///
/// Process-local: retention bounds the store file THIS process opened, so
/// a `WebOnly` silo that skipped the sweep would grow its own file without
/// limit. A serverless host keeps no long-running tick alive and runs none.
let registerLogStore
    (services: IServiceCollection)
    (config: ServerConfig)
    (store: (ILogStore * LogStoreConfig) option)
    (resolvedLogger: ILogger)
    : unit =
    match store with
    | None -> ()
    | Some(logStore, storeConfig) ->
        services.TryAddSingleton<ILogStore>(logStore)

        Hosting.register
            services
            (BackgroundUnit.create "log-store-retention" ProcessLocal (fun _ ->
                new SqliteLogStore.LogStoreRetentionService(logStore, storeConfig, resolvedLogger)
                :> Microsoft.Extensions.Hosting.IHostedService))
        |> ignore

/// Phase 829 — register the metrics-history flusher: the
/// `BackgroundService` that samples the live metric registry into
/// `ITimeSeriesStore` every `FlushSeconds` and sweeps points past
/// `RetentionDays` daily. A no-op on `NoMetricsHistory` (the default) —
/// no hosted service, no read of the sink, no point appended (GP 13).
///
/// Both of its dependencies are resolved from the `IServiceProvider` per
/// tick rather than captured here, so a companion `ITimeSeriesStore`
/// registered after this point is still seen (the same reason
/// `AlertRuleEngine` resolves its metric tap per tick). That is also why
/// this registration does not require them: the flusher names an absent
/// sink or store once at `Warn` rather than compose refusing a
/// deployment whose store arrives later.
///
/// Process-local, for the log store's reason: each process samples its OWN
/// in-memory metric accumulators, so a silo that skipped the flush would
/// simply lose its own metrics rather than let a peer cover for it.
let registerMetricsHistory (services: IServiceCollection) (config: ServerConfig) (resolvedLogger: ILogger) : unit =
    match config.Observability.MetricsHistory with
    | NoMetricsHistory -> ()
    | EnabledMetricsHistory historyConfig ->
        Hosting.register
            services
            (BackgroundUnit.create "metrics-history-flusher" ProcessLocal (fun sp ->
                new MetricsHistoryFlusher.MetricsHistoryFlusherService(sp, historyConfig, resolvedLogger)
                :> Microsoft.Extensions.Hosting.IHostedService))
        |> ignore

/// Phase 9x — register the alert-rule status board the alerts endpoint
/// reads and the engine writes after every tick. Only when at least one
/// rule is declared — the same condition that mounts the endpoint and
/// hosts the engine — so a deployment with no rules resolves nothing
/// (GP 13). `TryAddSingleton`, so a consumer's own board survives.
///
/// Deliberately NOT gated on the `ProcessProfile` matrix the engine is:
/// a `WebOnly` silo still serves the endpoint, and an empty board there
/// is what makes it answer `NotEvaluated` honestly rather than 500.
let registerObservabilityReadback (services: IServiceCollection) (config: ServerConfig) : unit =
    if not (List.isEmpty config.Observability.AlertRules) then
        services.TryAddSingleton<AlertRuleEngine.AlertRuleStatusBoard>(AlertRuleEngine.AlertRuleStatusBoard())