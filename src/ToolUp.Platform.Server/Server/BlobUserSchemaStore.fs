// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BlobUserSchemaStore

open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 7b — BlobUserSchemaStore default implementation ────────────
//
// Default `IUserSchemaStore` over the Phase 7 versioned object store
// (`IDataObjectStore`), which is itself blob-backed — so schema
// *definitions* get append-only versioning, per-scope isolation, and
// content dedup "for free", exactly as `BlobEntityStore` does.
//
// Blob layout (logical — the `_platform/user-schemas/{scopeId}/` intent):
//
//   schema definition : objectId = {schemaId},  dataType = "_user-schema"  (Versioned)
//   schema instance   : objectId = {instanceId}, dataType = {schemaId}      (Versioned)
//
// A schema's `dataType` is the reserved `"_user-schema"` tag; instance
// rows of a schema carry the schema's own `SchemaId` as their `dataType`,
// so the two never collide (a schema id equal to the reserved tag is
// rejected at `Save`).
//
// Migration executes over the same object store: `AddField` populates the
// serialised default on every instance's JSON, `RemoveField` /
// `RenameField` reshape it; the type-change cases are schema-only.

let private jsonOptions = FableConverters.create ()

let private serialise (value: 'T) : byte[] =
    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, jsonOptions))

let private deserialise (bytes: byte[]) : UserAuthoredSchema =
    JsonSerializer.Deserialize<UserAuthoredSchema>(Encoding.UTF8.GetString bytes, jsonOptions)

/// Human-readable `AuthoredBy` projection for the audit payload.
let private proposedByString (by: AuthoredBy) : string =
    match by with
    | Human -> "Human"
    | AIWithApproval convId -> sprintf "AIWithApproval:%s" convId

/// Structural validation of a schema before commit. Rejects an empty /
/// path-unsafe / reserved schema id and malformed field lists so a bad
/// schema fails loudly at `Save` rather than corrupting the object layout.
let private validate (schema: UserAuthoredSchema) : Result<unit, UserSchemaError> =
    if System.String.IsNullOrWhiteSpace schema.SchemaId then
        Error(InvalidSchema "schema id must be non-empty")
    elif schema.SchemaId.Contains "/" then
        Error(InvalidSchema "schema id must not contain '/'")
    elif schema.SchemaId = UserAuthoredSchema.dataType then
        Error(InvalidSchema(sprintf "schema id '%s' is reserved" UserAuthoredSchema.dataType))
    elif schema.Fields |> List.exists (fun f -> System.String.IsNullOrWhiteSpace f.Name) then
        Error(InvalidSchema "every field must have a non-empty name")
    else
        let names = schema.Fields |> List.map _.Name

        if (names |> List.distinct |> List.length) <> names.Length then
            Error(InvalidSchema "field names must be unique")
        else
            Ok()

/// Apply a migration plan to a schema's field list (the schema-side of a
/// migration). `AddField` appends; `RemoveField` drops; `RenameField`
/// renames; the type-change cases replace the field's semantic type.
let private applyMigrationsToFields
    (fields: UserSchemaField list)
    (migrations: SchemaMigration list)
    : UserSchemaField list =
    let step (acc: UserSchemaField list) (m: SchemaMigration) : UserSchemaField list =
        match m with
        | AddField(field, _) ->
            if acc |> List.exists (fun f -> f.Name = field.Name) then
                acc
            else
                acc @ [ field ]
        | RemoveField name -> acc |> List.filter (fun f -> f.Name <> name)
        | RenameField(fromName, toName) ->
            acc
            |> List.map (fun f -> if f.Name = fromName then { f with Name = toName } else f)
        | TightenType(fieldName, _, toType)
        | LooseType(fieldName, _, toType) ->
            acc
            |> List.map (fun f -> if f.Name = fieldName then { f with Type = toType } else f)
        | ChangeRef(fieldName, _, toTypeId) ->
            acc
            |> List.map (fun f ->
                if f.Name = fieldName then
                    {
                        f with
                            Type = BIFriendlyType.Ref toTypeId
                    }
                else
                    f)

    migrations |> List.fold step fields

/// Apply the value-affecting migrations to a single stored instance's
/// JSON object. `AddField` writes the serialised default when the field
/// is absent; `RemoveField` / `RenameField` reshape; the type-change
/// cases leave the value untouched (schema-only).
let private applyMigrationsToInstance (migrations: SchemaMigration list) (json: string) : string =
    let node =
        try
            JsonNode.Parse json
        with _ ->
            null

    match node with
    | :? JsonObject as obj ->
        for m in migrations do
            match m with
            | AddField(field, defaultValue) ->
                if not (obj.ContainsKey field.Name) then
                    obj[field.Name] <- JsonValue.Create defaultValue
            | RemoveField name -> obj.Remove name |> ignore
            | RenameField(fromName, toName) ->
                if obj.ContainsKey fromName then
                    let existing = obj[fromName]

                    let cloned = if isNull existing then null else existing.DeepClone()

                    obj.Remove fromName |> ignore
                    obj[toName] <- cloned
            | TightenType _
            | LooseType _
            | ChangeRef _ -> ()

        obj.ToJsonString()
    // Not a JSON object (empty / malformed instance) — leave it verbatim
    // so a migration never destroys data it cannot safely reshape.
    | _ -> json

/// Default `IUserSchemaStore`. `auditLog` is optional so a deployment
/// without an audit log pays nothing (though the SDK always registers at
/// least `NoOpAuditLog`).
type BlobUserSchemaStore(dataObjectStore: IDataObjectStore, auditLog: IAuditLog option) =

    let emit (scopeId: string) (evt: AuditEvent) : Async<unit> = async {
        match auditLog with
        | Some log ->
            try
                do! log.Record(scopeId, evt)
            with _ ->
                ()
        | None -> ()
    }

    interface IUserSchemaStore with

        member _.Save(scopeId, schema, actorUserId) = async {
            match validate schema with
            | Error e -> return Error e
            | Ok() ->
                let objectId = schema.SchemaId
                let! existing = dataObjectStore.ListVersions(scopeId, objectId)

                let newVersion =
                    if existing.IsEmpty then
                        1
                    else
                        (existing |> List.map _.Version |> List.max) + 1

                let committed = {
                    schema with
                        Version = newVersion
                        Owner = scopeId
                }

                let! saveResult =
                    dataObjectStore.Save(
                        scopeId,
                        objectId,
                        serialise committed,
                        UserAuthoredSchema.dataType,
                        actorUserId,
                        Map.empty,
                        VersioningPolicy.Versioned
                    )

                match saveResult with
                | Error err ->
                    return Error(UserSchemaStorageFailure(sprintf "IDataObjectStore.Save failed: %s" (string err)))
                | Ok _ ->
                    do!
                        emit
                            scopeId
                            (SchemaChanged {
                                UserId = actorUserId
                                ScopeId = scopeId
                                SchemaId = committed.SchemaId
                                Version = newVersion
                                VersionLabel = committed.VersionLabel
                                ChangeKind = (if newVersion = 1 then "Created" else "Updated")
                                EvolvedFrom = committed.EvolvedFrom
                                MigrationsApplied = 0
                                InstancesMigrated = 0
                            })

                    match committed.ProposedBy with
                    | AIWithApproval convId ->
                        do!
                            emit
                                scopeId
                                (SchemaApproved {
                                    UserId = actorUserId
                                    ScopeId = scopeId
                                    SchemaId = committed.SchemaId
                                    Version = newVersion
                                    VersionLabel = committed.VersionLabel
                                    ProposedBy = proposedByString committed.ProposedBy
                                    ConversationId = Some convId
                                })
                    | Human -> ()

                    return Ok committed
        }

        member _.Get(scopeId, schemaId) = async {
            let! result = dataObjectStore.Get(scopeId, schemaId)

            match result with
            | Ok(_, bytes) -> return Ok(deserialise bytes)
            | Error DataObjectError.NotFound -> return Error(SchemaNotFound schemaId)
            | Error err ->
                return Error(UserSchemaStorageFailure(sprintf "IDataObjectStore.Get failed: %s" (string err)))
        }

        member _.GetVersion(scopeId, schemaId, version) = async {
            let! result = dataObjectStore.GetVersion(scopeId, schemaId, version)

            match result with
            | Ok(_, bytes) -> return Ok(deserialise bytes)
            | Error DataObjectError.NotFound -> return Error(SchemaNotFound schemaId)
            | Error(DataObjectError.VersionNotFound _) -> return Error(SchemaVersionNotFound(schemaId, version))
            | Error err ->
                return Error(UserSchemaStorageFailure(sprintf "IDataObjectStore.GetVersion failed: %s" (string err)))
        }

        member _.List(scopeId) = async {
            let! all = dataObjectStore.ListObjects scopeId

            let schemaObjects =
                all |> List.filter (fun o -> o.DataType = UserAuthoredSchema.dataType)

            let! loaded =
                schemaObjects
                |> List.map (fun o -> async {
                    let! r = dataObjectStore.Get(scopeId, o.ObjectId)

                    return
                        match r with
                        | Ok(_, bytes) -> Some(deserialise bytes)
                        | Error _ -> None
                })
                |> Async.Parallel

            return loaded |> Array.choose id |> Array.toList
        }

        member _.History(scopeId, schemaId) = async {
            let! versions = dataObjectStore.ListVersions(scopeId, schemaId)

            let! loaded =
                versions
                |> List.sortBy _.Version
                |> List.map (fun v -> async {
                    let! r = dataObjectStore.GetVersion(scopeId, schemaId, v.Version)

                    return
                        match r with
                        | Ok(_, bytes) -> Some(deserialise bytes)
                        | Error _ -> None
                })
                |> Async.Parallel

            return loaded |> Array.choose id |> Array.toList
        }

        member _.Delete(scopeId, schemaId, actorUserId) = async {
            let! existing = dataObjectStore.ListVersions(scopeId, schemaId)
            let! deleteResult = dataObjectStore.Delete(scopeId, schemaId)

            match deleteResult with
            | Ok() ->
                if not existing.IsEmpty then
                    do!
                        emit
                            scopeId
                            (SchemaChanged {
                                UserId = actorUserId
                                ScopeId = scopeId
                                SchemaId = schemaId
                                Version = 0
                                VersionLabel = ""
                                ChangeKind = "Deleted"
                                EvolvedFrom = None
                                MigrationsApplied = 0
                                InstancesMigrated = 0
                            })

                return Ok()
            | Error err ->
                return Error(UserSchemaStorageFailure(sprintf "IDataObjectStore.Delete failed: %s" (string err)))
        }

        member this.ExecuteMigration(scopeId, schemaId, migrations, actorUserId) = async {
            let store = this :> IUserSchemaStore
            let! current = store.Get(scopeId, schemaId)

            match current with
            | Error e -> return Error e
            | Ok schema ->
                let fromVersion = schema.Version

                // 1. Evolve the schema and commit a new version.
                let evolved = {
                    schema with
                        Fields = applyMigrationsToFields schema.Fields migrations
                        EvolvedFrom = Some schemaId
                        MigrationPlan = migrations
                }

                let! saved = store.Save(scopeId, evolved, actorUserId)

                match saved with
                | Error e -> return Error(MigrationFailed(sprintf "schema commit failed: %A" e))
                | Ok committed ->
                    // 2. Transform every stored instance of this schema.
                    let! all = dataObjectStore.ListObjects scopeId

                    let instances = all |> List.filter (fun o -> o.DataType = schemaId)

                    let! outcomes =
                        instances
                        |> List.map (fun o -> async {
                            let! r = dataObjectStore.Get(scopeId, o.ObjectId)

                            match r with
                            | Ok(_, bytes) ->
                                let migrated = applyMigrationsToInstance migrations (Encoding.UTF8.GetString bytes)

                                let! saveR =
                                    dataObjectStore.Save(
                                        scopeId,
                                        o.ObjectId,
                                        Encoding.UTF8.GetBytes migrated,
                                        schemaId,
                                        actorUserId,
                                        o.Metadata,
                                        VersioningPolicy.Versioned
                                    )

                                return
                                    (match saveR with
                                     | Ok _ -> true
                                     | Error _ -> false)
                            | Error _ -> return false
                        })
                        |> Async.Parallel

                    let migratedCount = outcomes |> Array.filter id |> Array.length

                    do!
                        emit
                            scopeId
                            (SchemaChanged {
                                UserId = actorUserId
                                ScopeId = scopeId
                                SchemaId = schemaId
                                Version = committed.Version
                                VersionLabel = committed.VersionLabel
                                ChangeKind = "Migrated"
                                EvolvedFrom = Some schemaId
                                MigrationsApplied = List.length migrations
                                InstancesMigrated = migratedCount
                            })

                    return
                        Ok {
                            SchemaId = schemaId
                            FromVersion = fromVersion
                            ToVersion = committed.Version
                            InstancesMigrated = migratedCount
                            AppliedMigrations = List.length migrations
                        }
        }