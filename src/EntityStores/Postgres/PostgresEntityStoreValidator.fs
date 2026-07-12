// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.EntityStores.Postgres

open System
open Npgsql
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.ConfigValidation

// ─── Phase 531 — Postgres entity-store ops surface ──────────────────────
//
// An `IHealthCheck` readiness probe (cheap live round-trip) and an
// `IConfigValidator` preflight (connect + schema-presence, with opt-in
// auto-migrate). Both take the already-built `NpgsqlDataSource`; a
// composition registers them as DI singletons alongside the store so the SDK
// health + preflight aggregators pick them up.

/// Readiness probe — a `SELECT 1` round-trip. `Unhealthy` on any connection
/// failure (unreachable host, revoked credential, exhausted pool).
type PostgresEntityStoreHealthCheck(dataSource: NpgsqlDataSource) =
    interface IHealthCheck with
        member _.Name = "entity_store:postgres"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                use cmd = dataSource.CreateCommand "SELECT 1"
                let! _ = cmd.ExecuteScalarAsync() |> Async.AwaitTask
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

/// Startup preflight — connects and checks the entity table is present.
/// `autoMigrate = true` creates the table when absent (and passes); `false`
/// fails the preflight with a message pointing at the schema DDL, so a
/// deployment that manages its own migrations catches a missing table at
/// boot rather than on first write.
type PostgresEntityStoreValidator(dataSource: NpgsqlDataSource, table: string, autoMigrate: bool, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = sprintf "postgres-entity-store (%s)" table
        member _.Timeout = timeout

        member _.Validate() = async {
            try
                use existsCmd = dataSource.CreateCommand "SELECT to_regclass(@t) IS NOT NULL"
                existsCmd.Parameters.AddWithValue("t", table) |> ignore
                let! existsObj = existsCmd.ExecuteScalarAsync() |> Async.AwaitTask

                let present =
                    match existsObj with
                    | :? bool as b -> b
                    | _ -> false

                if present then
                    return ValidationResult.Ok
                elif autoMigrate then
                    do! PostgresEntityStore.ensureSchema dataSource table
                    return ValidationResult.Ok
                else
                    return
                        ValidationResult.Error(
                            sprintf
                                "entity table '%s' is absent and auto-migrate is disabled — create it (see the companion README) or enable auto-migrate"
                                table
                        )
            with ex ->
                return ValidationResult.Error(sprintf "Postgres entity-store preflight failed: %s" ex.Message)
        }

[<RequireQualifiedAccess>]
module PostgresEntityStoreOps =
    /// The readiness health probe for a Postgres entity store.
    let healthCheck (dataSource: NpgsqlDataSource) : IHealthCheck =
        PostgresEntityStoreHealthCheck(dataSource) :> IHealthCheck

    /// The startup preflight validator. `autoMigrate` creates the table when
    /// absent (recommended for single-owner deployments); disable it when
    /// migrations are managed externally.
    let validator (dataSource: NpgsqlDataSource) (table: string) (autoMigrate: bool) : IConfigValidator =
        PostgresEntityStoreValidator(dataSource, table, autoMigrate) :> IConfigValidator