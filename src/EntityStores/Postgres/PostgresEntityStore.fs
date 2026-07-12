// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.EntityStores.Postgres

open System
open System.Collections.Concurrent
open System.Text.Json
open Microsoft.FSharp.Reflection
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 531 — PostgreSQL / JSONB IEntityStore ────────────────────────
//
// A distributed-ready `IEntityStore` over an `NpgsqlDataSource`, binding the
// same `IEntityStoreContract` pack as the in-tree `BlobEntityStore`. Entities
// are stored one row per version in a single `toolup_entities` table with a
// `jsonb` payload; declared `EntityRegistration` indexes become JSONB
// expression indexes, and the Phase 19a query layer's predicates translate to
// SQL `WHERE` clauses over `payload ->> 'field'` — so range and negation
// predicates execute in the engine, not as client-side complement scans.
//
// **GP 1** — the Npgsql dependency is isolated to this companion; it never
// reaches `ToolUp.Platform.*`. **GP 2** — PostgreSQL + Npgsql are free
// (no paid default). **GP 4** — every statement carries `scope_id`; scope
// isolation is a structural predicate, not a post-filter. **GP 12** —
// stateless between calls (the data source is a connection pool), identity by
// value, distributed-ready.
//
// Serialisation uses the same `FableConverters` STJ option set as
// `BlobEntityStore`, so an entity written by one backend deserialises under
// the other (F# records / DUs / Option round-trip losslessly).
//
// **Index-name ⇒ JSONB key.** JSONB pushdown addresses a field by name
// (`payload ->> 'Owner'`), so a single-field index's `Name` must equal the
// record field it indexes (the common case — `withIndex "Owner" _.Owner`).
// `BlobEntityStore`'s opaque `Extract : 'T -> string` closure allows
// name≠field and compound keys; those do not push down here. See the
// companion README's "Divergences" section.

/// Serialiser option set — must match `BlobEntityStore` byte-for-byte.
module private Json =
    let options = FableConverters.create ()

    let serialise (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let deserialise<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options)

module private Reflection =
    /// Replace the `Version` field on an immutable record — same reflection
    /// rebuild `BlobEntityStore` uses so the persisted payload carries the
    /// store-assigned version.
    let withVersion<'T> (entity: 'T) (newVersion: int) : 'T =
        let t = typeof<'T>

        if not (FSharpType.IsRecord t) then
            failwith "withVersion: not a record"

        let fields = FSharpType.GetRecordFields t
        let values = FSharpValue.GetRecordFields entity

        let updated =
            Array.zip fields values
            |> Array.map (fun (f, v) -> if f.Name = "Version" then box newVersion else v)

        FSharpValue.MakeRecord(t, updated) :?> 'T

/// Translate a Phase 19a `Predicate` to a parameterised SQL boolean over the
/// `jsonb` payload. `addParam value` appends a query parameter and returns its
/// `@pN` placeholder — so both values and JSONB keys are parameterised (no
/// string interpolation of caller data into SQL).
module private Sql =
    let rec translate (addParam: obj -> string) (predicate: Predicate) : string =
        let field f = addParam (box (f: string))

        let compare (f: string) (op: string) (v: string) =
            sprintf "(payload ->> %s) %s %s" (field f) op (addParam (box v))

        match predicate with
        | Eq(f, v) -> compare f "=" v
        // Ne is the complement of Eq (every entity carries the field, so
        // IS DISTINCT FROM matches "value present and not equal").
        | Ne(f, v) -> sprintf "(payload ->> %s) IS DISTINCT FROM %s" (field f) (addParam (box v))
        | Gt(f, v) -> compare f ">" v
        | Gte(f, v) -> compare f ">=" v
        | Lt(f, v) -> compare f "<" v
        | Lte(f, v) -> compare f "<=" v
        | In(f, values) ->
            match values with
            | [] -> "FALSE"
            | _ ->
                let placeholders =
                    values |> List.map (fun v -> addParam (box v)) |> String.concat ", "

                sprintf "(payload ->> %s) IN (%s)" (field f) placeholders
        | And(a, b) -> sprintf "(%s AND %s)" (translate addParam a) (translate addParam b)
        | Or(a, b) -> sprintf "(%s OR %s)" (translate addParam a) (translate addParam b)
        | Not p -> sprintf "(NOT %s)" (translate addParam p)

/// PostgreSQL-backed `IEntityStore`. `dataSource` is built by the deployment
/// (via `NpgsqlDataSource.Create connString`, the connection string resolved
/// from `ISecretStore` at compose — see `PostgresEntityStore.create`);
/// `registry` is the shared `EntityRegistry` (declared indexes + type
/// validation); `table` defaults to `toolup_entities`.
type PostgresEntityStore
    (dataSource: NpgsqlDataSource, registry: EntityStore.EntityRegistry, ?table: string, ?auditLog: IAuditLog) =
    let table = defaultArg table "toolup_entities"

    // Best-effort expression-index creation, once per entity type.
    let ensuredIndexTypes = ConcurrentDictionary<string, bool>()

    let addValues (cmd: NpgsqlCommand) (values: ResizeArray<obj>) =
        values
        |> Seq.iteri (fun i v -> cmd.Parameters.AddWithValue(sprintf "p%d" i, v) |> ignore)

    let readString (reader: System.Data.Common.DbDataReader) (ordinal: int) = reader.GetString ordinal

    /// Create a JSONB expression index per declared single-field index of the
    /// entity type (best-effort; a name that isn't a plain identifier is
    /// skipped — its key can't be safely embedded in DDL, and the query still
    /// works via a sequential scan). Runs once per type. Takes the plain
    /// `(indexName, isCompound)` projection so it stays non-generic (the caller
    /// erases `EntityIndex<'T>` to its name/compound flag).
    let ensureIndexes (entityType: string) (indexes: (string * bool) list) = async {
        if ensuredIndexTypes.TryAdd(entityType, true) then
            let isPlain (s: string) =
                s.Length > 0 && s |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')

            for indexName, isCompound in indexes do
                if (not isCompound) && isPlain indexName && isPlain entityType then
                    try
                        let idxName = sprintf "tue_%s_%s" entityType indexName

                        let ddl =
                            sprintf
                                "CREATE INDEX IF NOT EXISTS %s ON %s ((payload ->> '%s')) WHERE entity_type = '%s'"
                                idxName
                                table
                                indexName
                                entityType

                        use cmd = dataSource.CreateCommand ddl
                        let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                        ()
                    with _ ->
                        () // best-effort — an index failure never faults a Save
    }

    let recordAudit (scopeId: string) (event: AuditEvent) = async {
        match auditLog with
        | Some log ->
            try
                do! log.Record(scopeId, event)
            with _ ->
                ()
        | None -> ()
    }

    interface IEntityStore with
        member _.Save<'T>(scopeId: string, entity: 'T) = async {
            match tryGetEntityFields entity with
            | Error msg -> return Error(InvalidEntityShape msg)
            | Ok core ->
                match registry.TryGet<'T>(core.Type) with
                | None -> return Error(EntityError.UnknownEntityType core.Type)
                | Some reg ->
                    if reg.EntityType <> core.Type then
                        return
                            Error(
                                InvalidEntityShape(
                                    sprintf
                                        "Entity Type field '%s' doesn't match registered type '%s'"
                                        core.Type
                                        reg.EntityType
                                )
                            )
                    else
                        try
                            do! ensureIndexes core.Type (reg.Indexes |> List.map (fun i -> i.Name, i.IsCompound))

                            use maxCmd =
                                dataSource.CreateCommand(
                                    sprintf
                                        "SELECT COALESCE(MAX(version), 0) FROM %s WHERE scope_id = @s AND entity_type = @t AND entity_id = @i"
                                        table
                                )

                            maxCmd.Parameters.AddWithValue("s", scopeId) |> ignore
                            maxCmd.Parameters.AddWithValue("t", core.Type) |> ignore
                            maxCmd.Parameters.AddWithValue("i", core.Id) |> ignore
                            let! maxObj = maxCmd.ExecuteScalarAsync() |> Async.AwaitTask

                            let maxVersion =
                                match maxObj with
                                | :? int as i -> i
                                | :? int64 as l -> int l
                                | _ -> 0

                            let newVersion = maxVersion + 1
                            let json = Json.serialise (Reflection.withVersion entity newVersion)

                            use insCmd =
                                dataSource.CreateCommand(
                                    sprintf
                                        "INSERT INTO %s (scope_id, entity_type, entity_id, version, payload) VALUES (@s, @t, @i, @v, @p::jsonb)"
                                        table
                                )

                            insCmd.Parameters.AddWithValue("s", scopeId) |> ignore
                            insCmd.Parameters.AddWithValue("t", core.Type) |> ignore
                            insCmd.Parameters.AddWithValue("i", core.Id) |> ignore
                            insCmd.Parameters.AddWithValue("v", newVersion) |> ignore
                            insCmd.Parameters.AddWithValue("p", json) |> ignore
                            let! _ = insCmd.ExecuteNonQueryAsync() |> Async.AwaitTask

                            let event =
                                if newVersion = 1 then
                                    EntityCreated {
                                        UserId = "system"
                                        EntityType = core.Type
                                        EntityId = core.Id
                                        Version = newVersion
                                    }
                                else
                                    EntityUpdated {
                                        UserId = "system"
                                        EntityType = core.Type
                                        EntityId = core.Id
                                        Version = newVersion
                                    }

                            do! recordAudit scopeId event

                            return
                                Ok(
                                    {
                                        Id = core.Id
                                        Type = core.Type
                                        Version = newVersion
                                    }
                                    : EntityRef<'T>
                                )
                        with ex ->
                            return Error(StorageFailure(sprintf "PostgresEntityStore.Save failed: %s" ex.Message))
        }

        member _.Get<'T>(scopeId: string, entityType: string, entityId: EntityId) = async {
            try
                use cmd =
                    dataSource.CreateCommand(
                        sprintf
                            "SELECT payload::text FROM %s WHERE scope_id = @s AND entity_type = @t AND entity_id = @i ORDER BY version DESC LIMIT 1"
                            table
                    )

                cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                cmd.Parameters.AddWithValue("t", entityType) |> ignore
                cmd.Parameters.AddWithValue("i", entityId) |> ignore
                let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask

                match result with
                | null -> return Error(NotFound(entityType, entityId))
                | :? string as json -> return Ok(Json.deserialise<'T> json)
                | other -> return Ok(Json.deserialise<'T> (string other))
            with ex ->
                return Error(StorageFailure(sprintf "PostgresEntityStore.Get failed: %s" ex.Message))
        }

        member _.GetVersion<'T>(scopeId: string, entityType: string, entityId: EntityId, version: int) = async {
            try
                use cmd =
                    dataSource.CreateCommand(
                        sprintf
                            "SELECT payload::text FROM %s WHERE scope_id = @s AND entity_type = @t AND entity_id = @i AND version = @v"
                            table
                    )

                cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                cmd.Parameters.AddWithValue("t", entityType) |> ignore
                cmd.Parameters.AddWithValue("i", entityId) |> ignore
                cmd.Parameters.AddWithValue("v", version) |> ignore
                let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask

                match result with
                | null -> return Error(NotFound(entityType, entityId))
                | :? string as json -> return Ok(Json.deserialise<'T> json)
                | other -> return Ok(Json.deserialise<'T> (string other))
            with ex ->
                return Error(StorageFailure(sprintf "PostgresEntityStore.GetVersion failed: %s" ex.Message))
        }

        member _.ListVersions<'T>(scopeId: string, entityType: string, entityId: EntityId) = async {
            try
                use cmd =
                    dataSource.CreateCommand(
                        sprintf
                            "SELECT version FROM %s WHERE scope_id = @s AND entity_type = @t AND entity_id = @i ORDER BY version ASC"
                            table
                    )

                cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                cmd.Parameters.AddWithValue("t", entityType) |> ignore
                cmd.Parameters.AddWithValue("i", entityId) |> ignore
                use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                let acc = ResizeArray<EntityRef<'T>>()
                let mutable go = true

                while go do
                    let! has = reader.ReadAsync() |> Async.AwaitTask

                    if has then
                        acc.Add {
                            Id = entityId
                            Type = entityType
                            Version = reader.GetInt32 0
                        }
                    else
                        go <- false

                return List.ofSeq acc
            with _ ->
                return []
        }

        member _.Delete(scopeId: string, entityType: string, entityId: EntityId) = async {
            if not (registry.Knows entityType) then
                return Error(EntityError.UnknownEntityType entityType)
            else
                try
                    // Read the head version first so the audit records what was removed.
                    use headCmd =
                        dataSource.CreateCommand(
                            sprintf
                                "SELECT COALESCE(MAX(version), 0) FROM %s WHERE scope_id = @s AND entity_type = @t AND entity_id = @i"
                                table
                        )

                    headCmd.Parameters.AddWithValue("s", scopeId) |> ignore
                    headCmd.Parameters.AddWithValue("t", entityType) |> ignore
                    headCmd.Parameters.AddWithValue("i", entityId) |> ignore
                    let! headObj = headCmd.ExecuteScalarAsync() |> Async.AwaitTask

                    let headVersion =
                        match headObj with
                        | :? int as i -> i
                        | :? int64 as l -> int l
                        | _ -> 0

                    // Hard-delete every version — a subsequent Get returns NotFound
                    // (divergence: BlobEntityStore leaves historical versions in its
                    // underlying object store; here history lives in the same table).
                    use cmd =
                        dataSource.CreateCommand(
                            sprintf "DELETE FROM %s WHERE scope_id = @s AND entity_type = @t AND entity_id = @i" table
                        )

                    cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                    cmd.Parameters.AddWithValue("t", entityType) |> ignore
                    cmd.Parameters.AddWithValue("i", entityId) |> ignore
                    let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask

                    if headVersion > 0 then
                        do!
                            recordAudit
                                scopeId
                                (EntityDeleted {
                                    UserId = "system"
                                    EntityType = entityType
                                    EntityId = entityId
                                    Version = headVersion
                                })

                    return Ok()
                with ex ->
                    return Error(StorageFailure(sprintf "PostgresEntityStore.Delete failed: %s" ex.Message))
        }

        member _.FindByIndex<'T>(scopeId: string, entityType: string, indexName: string, value: string) = async {
            match registry.TryGet<'T>(entityType) with
            | None -> return Error(EntityError.UnknownEntityType entityType)
            | Some reg ->
                match EntityRegistration.tryFindIndex indexName reg with
                | None -> return Error(InvalidIndex indexName)
                | Some _ ->
                    try
                        // Head-version rows whose current indexed value equals `value`.
                        use cmd =
                            dataSource.CreateCommand(
                                sprintf
                                    "SELECT entity_id, version FROM %s t WHERE scope_id = @s AND entity_type = @t AND version = (SELECT MAX(version) FROM %s h WHERE h.scope_id = t.scope_id AND h.entity_type = t.entity_type AND h.entity_id = t.entity_id) AND (payload ->> @idx) = @val"
                                    table
                                    table
                            )

                        cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                        cmd.Parameters.AddWithValue("t", entityType) |> ignore
                        cmd.Parameters.AddWithValue("idx", indexName) |> ignore
                        cmd.Parameters.AddWithValue("val", value) |> ignore
                        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                        let acc = ResizeArray<EntityRef<'T>>()
                        let mutable go = true

                        while go do
                            let! has = reader.ReadAsync() |> Async.AwaitTask

                            if has then
                                acc.Add {
                                    Id = readString reader 0
                                    Type = entityType
                                    Version = reader.GetInt32 1
                                }
                            else
                                go <- false

                        return Ok(List.ofSeq acc)
                    with ex ->
                        return Error(StorageFailure(sprintf "PostgresEntityStore.FindByIndex failed: %s" ex.Message))
        }

        member _.Count(scopeId: string, entityType: string) = async {
            try
                use cmd =
                    dataSource.CreateCommand(
                        sprintf
                            "SELECT COUNT(DISTINCT entity_id) FROM %s WHERE scope_id = @s AND entity_type = @t"
                            table
                    )

                cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                cmd.Parameters.AddWithValue("t", entityType) |> ignore
                let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask

                return
                    match result with
                    | :? int as i -> i
                    | :? int64 as l -> int l
                    | _ -> 0
            with _ ->
                return 0
        }

        member _.ListAll<'T>(scopeId: string, entityType: string, skip: int, take: int) = async {
            if not (registry.Knows entityType) then
                return []
            else
                try
                    use cmd =
                        dataSource.CreateCommand(
                            sprintf
                                "SELECT entity_id, MAX(version) FROM %s WHERE scope_id = @s AND entity_type = @t GROUP BY entity_id ORDER BY entity_id OFFSET @skip LIMIT @take"
                                table
                        )

                    cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                    cmd.Parameters.AddWithValue("t", entityType) |> ignore
                    cmd.Parameters.AddWithValue("skip", int64 (max 0 skip)) |> ignore
                    cmd.Parameters.AddWithValue("take", int64 take) |> ignore
                    use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                    let acc = ResizeArray<EntityRef<'T>>()
                    let mutable go = true

                    while go do
                        let! has = reader.ReadAsync() |> Async.AwaitTask

                        if has then
                            acc.Add {
                                Id = readString reader 0
                                Type = entityType
                                Version = reader.GetInt32 1
                            }
                        else
                            go <- false

                    return List.ofSeq acc
                with _ ->
                    return []
        }

        member _.Query<'T>(scopeId: string, query: EntityQuery<'T>) = async {
            match registry.TryGet<'T>(query.EntityType) with
            | None -> return Error(EntityError.UnknownEntityType query.EntityType)
            | Some reg ->
                match EntityQuery.validate query reg with
                | Error(IndexValidationError.NonIndexedField f) -> return Error(InvalidIndex f)
                | Error(IndexValidationError.NonIndexedSortField f) -> return Error(InvalidIndex f)
                | Error(IndexValidationError.UnknownEntityType t) -> return Error(EntityError.UnknownEntityType t)
                | Ok validated ->
                    try
                        let values = ResizeArray<obj>()

                        let addParam (v: obj) =
                            values.Add v
                            sprintf "@p%d" (values.Count - 1)

                        let scopeP = addParam (box scopeId)
                        let typeP = addParam (box query.EntityType)

                        let whereClause =
                            match validated.Where with
                            | None -> "TRUE"
                            | Some p -> Sql.translate addParam p

                        let orderClause =
                            match validated.OrderBy with
                            | Some(f, dir) ->
                                let d =
                                    match dir with
                                    | Ascending -> "ASC"
                                    | Descending -> "DESC"

                                sprintf "ORDER BY (payload ->> %s) %s, entity_id" (addParam (box f)) d
                            | None -> "ORDER BY entity_id"

                        let skipP = addParam (box (int64 validated.Skip))
                        let takeP = addParam (box (int64 validated.Take))

                        let sql =
                            sprintf
                                "WITH heads AS (SELECT DISTINCT ON (entity_id) entity_id, payload FROM %s WHERE scope_id = %s AND entity_type = %s ORDER BY entity_id, version DESC) SELECT payload::text FROM heads WHERE %s %s OFFSET %s LIMIT %s"
                                table
                                scopeP
                                typeP
                                whereClause
                                orderClause
                                skipP
                                takeP

                        use cmd = dataSource.CreateCommand sql
                        addValues cmd values
                        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                        let acc = ResizeArray<'T>()
                        let mutable go = true

                        while go do
                            let! has = reader.ReadAsync() |> Async.AwaitTask

                            if has then
                                acc.Add(Json.deserialise<'T> (readString reader 0))
                            else
                                go <- false

                        return Ok(List.ofSeq acc)
                    with ex ->
                        return Error(StorageFailure(sprintf "PostgresEntityStore.Query failed: %s" ex.Message))
        }

/// Companion capability posture — a distributed-ready store that writes to a
/// database and reads external state (GP 12).
module PostgresEntityStore =
    let capability: CompanionCapability = CompanionCapability.distributedEffecting

    [<Literal>]
    let DefaultTable = "toolup_entities"

    /// DDL for the entity table. One row per version; the primary key gives
    /// head-version lookups (`MAX(version)` per `(scope, type, id)`) a covering
    /// index for free. Declared `EntityRegistration` indexes become JSONB
    /// expression indexes lazily on first Save of each type.
    let schemaDdl (table: string) : string =
        sprintf
            "CREATE TABLE IF NOT EXISTS %s (scope_id text NOT NULL, entity_type text NOT NULL, entity_id text NOT NULL, version integer NOT NULL, payload jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (scope_id, entity_type, entity_id, version))"
            table

    /// Idempotently create the entity table (auto-migrate). Safe to call on
    /// every boot — `CREATE TABLE IF NOT EXISTS`.
    let ensureSchema (dataSource: NpgsqlDataSource) (table: string) : Async<unit> = async {
        use cmd = dataSource.CreateCommand(schemaDdl table)
        let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
        ()
    }

    /// Construct a Postgres-backed `IEntityStore` over `dataSource`, ensuring
    /// the default `toolup_entities` schema exists (auto-migrate). `registry`
    /// is the shared `EntityRegistry`. In a composition the connection string
    /// is resolved from `ISecretStore` before building the `NpgsqlDataSource`
    /// (never read from env vars in the companion — companion-authoring guide).
    let create (dataSource: NpgsqlDataSource) (registry: EntityStore.EntityRegistry) : IEntityStore =
        ensureSchema dataSource DefaultTable |> Async.RunSynchronously
        PostgresEntityStore(dataSource, registry) :> IEntityStore

    /// As `create`, with a custom table name.
    let createWithTable
        (dataSource: NpgsqlDataSource)
        (registry: EntityStore.EntityRegistry)
        (table: string)
        : IEntityStore =
        ensureSchema dataSource table |> Async.RunSynchronously
        PostgresEntityStore(dataSource, registry, table) :> IEntityStore

    /// As `create`, additionally wiring an `IAuditLog` for entity lifecycle
    /// events (`EntityCreated` / `EntityUpdated` / `EntityDeleted`).
    let createWithAudit
        (dataSource: NpgsqlDataSource)
        (registry: EntityStore.EntityRegistry)
        (auditLog: IAuditLog)
        : IEntityStore =
        ensureSchema dataSource DefaultTable |> Async.RunSynchronously
        PostgresEntityStore(dataSource, registry, DefaultTable, auditLog) :> IEntityStore