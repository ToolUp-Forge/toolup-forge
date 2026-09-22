// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ComposeObservability

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform

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
    match config.LogStore with
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
/// The sweep is gated only on `ServerlessHost`, where no long-running tick
/// can be kept alive. It is deliberately NOT gated on the `ProcessProfile`
/// matrix that `ProcessProfileGate` applies to the outbound dispatchers:
/// those drain a SHARED durable queue, so one silo may do the work for
/// all. Retention bounds the store file THIS process opened, so a
/// `WebOnly` silo that skipped the sweep would grow its own file without
/// limit — the opposite of what the gate is for.
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

        match config.ServerlessHost with
        | ServerlessHost -> ()
        | KestrelHost ->
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun _ ->
                SqliteLogStore.LogStoreRetentionService(logStore, storeConfig, resolvedLogger)
                :> Microsoft.Extensions.Hosting.IHostedService)
            |> ignore