// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SchemaMigrationJobHandler

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 7b — SchemaMigrationJobHandler ─────────────────────────────
//
// `IJobHandler` binding so long-running `MigrationPlan` executions run
// through `IJobScheduler` and survive restarts (Phase 9b). The handler is
// stateless between invocations (Phase 9c rule 4): every piece of state
// arrives via `JobContext.ScopeId` + the serialised `Payload`; the only
// captured dependency is the (itself stateless) `IUserSchemaStore`.
//
// A malformed payload is `PermanentFailure` (retrying won't fix it); a
// structural refusal from the store (schema gone / invalid) is likewise
// permanent; a storage failure is `TransientFailure` so the scheduler
// retries per the job's `RetryPolicy`.

/// Stable handler name registered with `IJobScheduler.RegisterHandler`.
[<Literal>]
let HandlerName = "_platform.user_schema.migrate"

/// Serialised job payload. Fable-compatible via `FableConverters` (the
/// same converter set the scheduler persists every other payload with).
type SchemaMigrationJobPayload = {
    SchemaId: SchemaId
    Migrations: SchemaMigration list
    ActorUserId: string
}

let private jsonOptions = FableConverters.create ()

/// Serialise a payload for `JobRegistration.Payload` / `ScheduledJobDeclaration.withPayload`.
let serialisePayload (payload: SchemaMigrationJobPayload) : string =
    JsonSerializer.Serialize(payload, jsonOptions)

/// Build the migration job handler over an `IUserSchemaStore`.
let create (store: IUserSchemaStore) : IJobHandler =
    { new IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            let parsed =
                try
                    Ok(JsonSerializer.Deserialize<SchemaMigrationJobPayload>(ctx.Payload, jsonOptions))
                with ex ->
                    Error ex.Message

            match parsed with
            | Error msg -> return PermanentFailure(sprintf "malformed migration payload: %s" msg)
            | Ok payload ->
                let! result =
                    store.ExecuteMigration(ctx.ScopeId, payload.SchemaId, payload.Migrations, payload.ActorUserId)

                match result with
                | Ok _ -> return Success
                | Error(SchemaNotFound _ as e)
                | Error(SchemaVersionNotFound _ as e)
                | Error(InvalidSchema _ as e)
                | Error(MigrationFailed _ as e) -> return PermanentFailure(sprintf "%A" e)
                | Error e -> return TransientFailure(sprintf "%A" e)
        }
    }