// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.VectorStores.Pgvector.PgvectorVectorStore

open System
open System.Globalization
open System.Text
open System.Text.Json
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore

// ─── Phase 507 — PostgreSQL + pgvector IVectorStore ──────────────────
//
// The top rung of the documented RAG scale story:
//
//   InMemoryVectorStore   < ~50k chunks   (flat scan, blob-persisted)
//   HnswVectorStore       < ~1M chunks    (in-process ANN graph)
//   PgvectorVectorStore   external DB     (durable, multi-replica-safe)
//
// Beyond corpus scale, the reason to reach for this companion is that it
// removes the SINGLE-PROCESS ceiling the in-tree stores carry. Both of
// them hold their index in process memory and persist it asynchronously,
// so two replicas of the same deployment each own a private index and a
// chunk ingested on replica A is invisible to replica B until a flush +
// reload cycle. Here the index IS the database: every replica reads and
// writes the same rows, so retrieval is consistent across replicas with
// no per-process index state at all.
//
// **GP 1 — vendor isolation.** The Npgsql dependency lives in this
// companion and never reaches `ToolUp.Platform.*`. Nothing pgvector-shaped
// crosses the `IVectorStore` boundary; a deployment swaps stores by
// changing one composition line.
//
// **GP 4 — structural scope isolation.** Scope is a first-class `scope`
// column and part of the composite primary key `(scope, chunk_id)`.
// Every statement this companion issues except the scope *enumeration*
// binds it, in one of exactly two ways (`Sql.ScopeBinding`): the six
// that read or mutate existing rows carry a `scope = @scope` predicate,
// and the one INSERT — which has no rows to filter yet — carries the
// scope in the row identity it writes plus its `ON CONFLICT (scope,
// chunk_id)` target, so a chunk id colliding across scopes cannot
// overwrite a neighbour's row. There is no statement shape that can
// reach across scopes, so cross-scope leakage is impossible by
// construction rather than by remembering to filter — the SQL twin of
// `HnswVectorStore`'s per-scope graph. `Sql.scopeBoundStatements` is the
// enumerable proof; the test pack asserts the binding on every member,
// with no database present.
//
// **GP 12 — portability.** Identity by value (`chunkId: string`,
// `VectorScope`), async at every boundary, no callbacks, entirely
// stateless between calls (the `NpgsqlDataSource` is a connection pool,
// not per-call state), scope is the shard key with no cross-scope
// ordering promise.
//
// **Fail-loud (507.C).** Connection failure, a missing `vector`
// extension, a missing table under `VerifyOnly`, or a malformed option
// raise a descriptive `PgvectorStoreException` at `create` time — never
// at first query, deep inside a request path. `TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION`
// keeps the same meaning it has for the in-tree stores: with it set, a
// row whose `metadata` JSON cannot be decoded aborts the read instead of
// degrading to empty metadata.
//
// Distributed-readiness: **production-ready / distributed-ready**. Two or
// more replicas may share one database.

// ─── Failure ─────────────────────────────────────────────────────────

/// Raised for every companion-level failure: option validation, the
/// `create`-time connectivity / extension / schema probe, a dimension
/// mismatch at upsert, and (under the refuse-on-corruption toggle) an
/// undecodable metadata row. One exception type so a composing app can
/// catch the companion's failures without matching on Npgsql internals.
exception PgvectorStoreException of message: string

let private fail (message: string) = raise (PgvectorStoreException message)

// ─── Scope key ───────────────────────────────────────────────────────
//
// The `scope` column's value. Same encoding as the in-tree stores' blob
// path segment, so an index exported from `HnswVectorStore` imports here
// without a translation table.

module Scope =
    /// Encode a `VectorScope` as its `scope` column value.
    let toKey (scope: VectorScope) : string =
        match scope with
        | Platform -> "platform"
        | Deployment -> "deployment"
        | Team teamId -> $"team:{teamId}"
        | User userId -> $"user:{userId}"

    /// Decode a `scope` column value. Total: an unrecognised prefix is
    /// read as a team id rather than throwing, matching the in-tree
    /// stores — an unknown scope key is a stale row, not a corrupt one.
    let fromKey (key: string) : VectorScope =
        if key = "platform" then
            Platform
        elif key = "deployment" then
            Deployment
        elif key.StartsWith "user:" then
            User(key.Substring "user:".Length)
        elif key.StartsWith "team:" then
            Team(key.Substring "team:".Length)
        else
            Team key

// ─── Vector literal ──────────────────────────────────────────────────
//
// pgvector's text input format is `[1,2,3]`, cast with `::vector`. Going
// through the text form rather than the `Pgvector` NuGet package's
// binary type handler keeps the dependency set to Npgsql alone — one
// vendor package, per the companion-authoring guide's "use the narrow
// surface" rule.

module Vector =
    /// Euclidean magnitude.
    let magnitude (v: float32 array) : float32 =
        let mutable sum = 0.0f

        for x in v do
            sum <- sum + x * x

        sqrt sum

    /// Unit-normalise. A zero vector is returned unchanged — pgvector's
    /// `<=>` yields NaN against a zero vector either way, and silently
    /// substituting a synthetic direction would fabricate a ranking.
    let normalise (v: float32 array) : float32 array =
        let m = magnitude v
        if m = 0.0f then v else Array.map (fun x -> x / m) v

    /// pgvector text literal — `[a,b,c]`, invariant culture, round-trippable.
    let toLiteral (v: float32 array) : string =
        let sb = StringBuilder()
        sb.Append '[' |> ignore

        v
        |> Array.iteri (fun i x ->
            if i > 0 then
                sb.Append ',' |> ignore

            sb.Append(x.ToString("R", CultureInfo.InvariantCulture)) |> ignore)

        sb.Append ']' |> ignore
        sb.ToString()

// ─── Metadata codec ──────────────────────────────────────────────────
//
// `TextChunk.Metadata` is a flat `Map<string, string>`, stored as a
// `jsonb` object so an operator can inspect and index it with ordinary
// SQL (`metadata ->> '_origin' = 'Document'`). A hand-rolled object
// encode/decode rather than the F#-aware converter set, because the
// on-disk shape here is a *query surface* the deployment reads with
// psql, not an opaque round-trip buffer.

module Metadata =
    /// Encode to a flat JSON object.
    let toJson (metadata: Map<string, string>) : string =
        use stream = new IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()

        for KeyValue(k, v) in metadata do
            writer.WriteString(k, v)

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    /// Decode a flat JSON object. `Error` carries a human-readable
    /// reason for the fail-loud path; non-string values are coerced to
    /// their raw text so a row hand-edited in psql stays readable.
    let fromJson (json: string) : Result<Map<string, string>, string> =
        if String.IsNullOrWhiteSpace json then
            Ok Map.empty
        else
            try
                use doc = JsonDocument.Parse json

                if doc.RootElement.ValueKind <> JsonValueKind.Object then
                    Error(sprintf "expected a JSON object, got %O" doc.RootElement.ValueKind)
                else
                    doc.RootElement.EnumerateObject()
                    |> Seq.map (fun p ->
                        let value =
                            if p.Value.ValueKind = JsonValueKind.String then
                                p.Value.GetString()
                            else
                                p.Value.GetRawText()

                        p.Name, value)
                    |> Map.ofSeq
                    |> Ok
            with ex ->
                Error ex.Message

// ─── Options ─────────────────────────────────────────────────────────

/// Approximate-nearest-neighbour index built over the `embedding` column.
/// The default is **no ANN index** — exact search, which is correct at
/// every corpus size and fast below a few hundred thousand rows (GP 11:
/// the default is the conservative behaviour). An ANN index trades exact
/// recall for latency and is the deliberate opt-in at scale.
type PgvectorAnnIndex =
    /// Exact (sequential) cosine scan. Perfect recall.
    | NoAnnIndex
    /// pgvector ≥ 0.5 HNSW index. `m` is the neighbour budget,
    /// `efConstruction` the build-time candidate list.
    | HnswAnnIndex of m: int * efConstruction: int
    /// pgvector IVFFlat index. `lists` ≈ rows / 1000 is the usual
    /// starting point. Must be built AFTER the table holds data.
    | IvfFlatAnnIndex of lists: int

/// How `create` reconciles the database schema.
type PgvectorSchemaMode =
    /// Issue the idempotent `CREATE EXTENSION` / `CREATE TABLE IF NOT
    /// EXISTS` / `CREATE INDEX IF NOT EXISTS` migration. Needs DDL rights.
    | AutoMigrate
    /// Verify the extension + table are present and refuse to start if
    /// not. For deployments whose application role has no DDL grant —
    /// the schema is provisioned by a migration tool, and the companion's
    /// job is to fail loudly rather than silently query a missing table.
    | VerifyOnly

/// Companion configuration. `Dimensions` is required and has no default:
/// the column is `vector(N)` and N is a property of the deployment's
/// embedding model, not something this companion can guess (GP 9 — never
/// fabricate).
type PgvectorOptions = {
    /// Unqualified table name. Validated as a plain SQL identifier —
    /// identifiers cannot be parameterised, so the guard is the injection
    /// boundary.
    Table: string
    /// Embedding dimensionality. Must match the composed
    /// `IEmbeddingProvider`'s output length.
    Dimensions: int
    /// Schema reconciliation performed at `create` time.
    SchemaMode: PgvectorSchemaMode
    /// ANN index to build (under `AutoMigrate`) / expect. Default `NoAnnIndex`.
    AnnIndex: PgvectorAnnIndex
    /// Per-command timeout in seconds. `0` inherits the data source's.
    CommandTimeoutSeconds: int
}

module PgvectorOptions =
    /// pgvector's own hard ceiling on a `vector` column.
    [<Literal>]
    let MaxDimensions = 16000

    /// Defaults for everything except `Dimensions`, which the caller must
    /// supply.
    let forDimensions (dimensions: int) : PgvectorOptions = {
        Table = "toolup_rag_chunks"
        Dimensions = dimensions
        SchemaMode = AutoMigrate
        AnnIndex = NoAnnIndex
        CommandTimeoutSeconds = 0
    }

    /// A plain unquoted SQL identifier: leading letter or underscore,
    /// then letters / digits / underscores, ≤ 63 bytes (PostgreSQL's
    /// `NAMEDATALEN - 1`). Deliberately narrower than what PostgreSQL
    /// would accept quoted — the table name is interpolated into every
    /// statement, so the narrow set is the injection guard, not a
    /// stylistic preference.
    let isSafeIdentifier (name: string) : bool =
        not (String.IsNullOrWhiteSpace name)
        && name.Length <= 63
        && (Char.IsLetter name[0] || name[0] = '_')
        && name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')
        && name |> Seq.forall (fun c -> int c < 128)

    /// Bounds-check before any I/O, so a fat-fingered option fails at
    /// `create` with a message naming the offending field rather than as
    /// a Postgres syntax error on the first query.
    let validate (o: PgvectorOptions) : Result<unit, string> =
        if not (isSafeIdentifier o.Table) then
            Error(
                sprintf
                    "Table must be a plain SQL identifier (letter or underscore, then letters/digits/underscores, max 63 chars); got '%s'."
                    o.Table
            )
        elif o.Dimensions < 1 || o.Dimensions > MaxDimensions then
            Error(sprintf "Dimensions must be in [1, %d]; got %d." MaxDimensions o.Dimensions)
        elif o.CommandTimeoutSeconds < 0 then
            Error(sprintf "CommandTimeoutSeconds must be >= 0; got %d." o.CommandTimeoutSeconds)
        else
            match o.AnnIndex with
            | NoAnnIndex -> Ok()
            | HnswAnnIndex(m, efConstruction) ->
                if m < 2 || m > 100 then
                    Error(sprintf "AnnIndex HNSW m must be in [2, 100]; got %d." m)
                elif efConstruction < m * 2 then
                    Error(sprintf "AnnIndex HNSW efConstruction must be >= 2*m (%d); got %d." (m * 2) efConstruction)
                else
                    Ok()
            | IvfFlatAnnIndex lists ->
                if lists < 1 then
                    Error(sprintf "AnnIndex IVFFlat lists must be >= 1; got %d." lists)
                else
                    Ok()

// ─── SQL ─────────────────────────────────────────────────────────────
//
// Every statement lives here, built from the validated table name, so
// the scope-isolation guarantee is auditable in one place instead of
// being spread across nine interface members.

module Sql =
    /// Named parameter carrying the scope key on every scope-bound
    /// statement.
    [<Literal>]
    let ScopeParameter = "@scope"

    /// The predicate that makes scope isolation structural.
    [<Literal>]
    let ScopePredicate = "scope = @scope"

    let private annIndexDdl (o: PgvectorOptions) : string list =
        match o.AnnIndex with
        | NoAnnIndex -> []
        | HnswAnnIndex(m, efConstruction) -> [
            sprintf
                "CREATE INDEX IF NOT EXISTS %s_embedding_hnsw_idx ON %s USING hnsw (embedding vector_cosine_ops) WITH (m = %d, ef_construction = %d);"
                o.Table
                o.Table
                m
                efConstruction
          ]
        | IvfFlatAnnIndex lists -> [
            sprintf
                "CREATE INDEX IF NOT EXISTS %s_embedding_ivfflat_idx ON %s USING ivfflat (embedding vector_cosine_ops) WITH (lists = %d);"
                o.Table
                o.Table
                lists
          ]

    /// The idempotent schema migration, ordered. Excludes `CREATE
    /// EXTENSION`, which is issued separately because it needs elevated
    /// rights and has its own fallback probe.
    let migration (o: PgvectorOptions) : string list =
        [
            sprintf
                """CREATE TABLE IF NOT EXISTS %s (
    scope      text        NOT NULL,
    chunk_id   text        NOT NULL,
    content    text        NOT NULL,
    metadata   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    embedding  vector(%d)  NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT %s_pkey PRIMARY KEY (scope, chunk_id)
);"""
                o.Table
                o.Dimensions
                o.Table
            // Partial index over live rows — the shape `Search` and the
            // default `ListChunks` both take.
            sprintf
                "CREATE INDEX IF NOT EXISTS %s_scope_live_idx ON %s (scope) WHERE deleted_at IS NULL;"
                o.Table
                o.Table
            // Tombstone sweep for `Vacuum`.
            sprintf "CREATE INDEX IF NOT EXISTS %s_scope_deleted_idx ON %s (scope, deleted_at);" o.Table o.Table
        ]
        @ annIndexDdl o

    /// Extension bootstrap (`AutoMigrate` only).
    [<Literal>]
    let CreateExtension = "CREATE EXTENSION IF NOT EXISTS vector;"

    /// Probe used when `CREATE EXTENSION` is refused for lack of rights,
    /// and as the whole extension check under `VerifyOnly`.
    [<Literal>]
    let ExtensionPresent = "SELECT 1 FROM pg_extension WHERE extname = 'vector';"

    /// Table-presence probe for `VerifyOnly`.
    let tableRegclass = "SELECT to_regclass(@table);"

    /// Upsert on the composite key. Re-upserting clears any tombstone —
    /// the new content supersedes the old (the `IVectorStore` contract).
    let upsert (o: PgvectorOptions) =
        sprintf
            """INSERT INTO %s (scope, chunk_id, content, metadata, embedding, deleted_at)
VALUES (@scope, @chunk_id, @content, @metadata::jsonb, @embedding::vector, NULL)
ON CONFLICT (scope, chunk_id) DO UPDATE
SET content = EXCLUDED.content,
    metadata = EXCLUDED.metadata,
    embedding = EXCLUDED.embedding,
    deleted_at = NULL;"""
            o.Table

    /// Per-scope KNN. `<=>` is pgvector's cosine distance, so
    /// `1 - distance` is the cosine similarity the other stores report.
    /// Tie-broken on `chunk_id` inside the scope; the caller applies the
    /// cross-scope total order.
    let search (o: PgvectorOptions) =
        sprintf
            """SELECT chunk_id, content, metadata, 1 - (embedding <=> @embedding::vector) AS score
FROM %s
WHERE scope = @scope AND deleted_at IS NULL
ORDER BY embedding <=> @embedding::vector, chunk_id
LIMIT @top_k;"""
            o.Table

    /// Administrative enumeration. `@include_deleted` keeps the statement
    /// shape constant so the plan is cached across both call shapes.
    let listChunks (o: PgvectorOptions) =
        sprintf
            """SELECT chunk_id, content, metadata, deleted_at
FROM %s
WHERE scope = @scope AND (@include_deleted OR deleted_at IS NULL)
ORDER BY chunk_id;"""
            o.Table

    /// Tombstone write. Already-tombstoned rows keep their original
    /// timestamp, so a repeated delete cannot extend the retention window.
    let deleteChunk (o: PgvectorOptions) =
        sprintf
            """UPDATE %s
SET deleted_at = @deleted_at
WHERE scope = @scope AND chunk_id = @chunk_id AND deleted_at IS NULL;"""
            o.Table

    let restoreChunk (o: PgvectorOptions) =
        sprintf
            """UPDATE %s
SET deleted_at = NULL
WHERE scope = @scope AND chunk_id = @chunk_id AND deleted_at IS NOT NULL;"""
            o.Table

    let vacuum (o: PgvectorOptions) =
        sprintf
            """DELETE FROM %s
WHERE scope = @scope AND deleted_at IS NOT NULL AND deleted_at < @older_than;"""
            o.Table

    let deleteByScope (o: PgvectorOptions) =
        sprintf "DELETE FROM %s WHERE scope = @scope;" o.Table

    /// The one statement with no scope predicate — it exists precisely to
    /// enumerate scopes, and is exempt by construction, not by omission.
    let listScopes (o: PgvectorOptions) =
        sprintf "SELECT DISTINCT scope FROM %s ORDER BY scope;" o.Table

    /// How a statement binds the scope column. Two shapes, because an
    /// INSERT has no rows to filter yet — its isolation is carried by
    /// the row IDENTITY rather than by a predicate. Distinguishing them
    /// keeps the guarantee checkable instead of approximately true.
    type ScopeBinding =
        /// Reads or mutates existing rows, filtered by `scope = @scope`.
        | ScopePredicated
        /// Writes a row whose composite identity `(scope, chunk_id)`
        /// binds the scope column to `@scope`. Nothing outside the
        /// caller's scope is reachable, including on conflict.
        | ScopeKeyed

    /// The composite-key conflict target — the scope half of a written
    /// row's identity.
    [<Literal>]
    let ScopeConflictTarget = "ON CONFLICT (scope, chunk_id)"

    /// Every statement that reads or writes chunk rows, paired with the
    /// interface member it serves and the way it binds scope. The
    /// scope-isolation guarantee (GP 4) is that EVERY member of this
    /// list carries one of the two bindings and none carries neither;
    /// the test pack asserts exactly that, so a future statement added
    /// without a scope binding fails the gate rather than shipping a
    /// leak. `ListScopes` is absent by design — it exists to enumerate
    /// scopes, so a scope predicate would make it useless.
    let scopeBoundStatements (o: PgvectorOptions) : (string * ScopeBinding * string) list = [
        "Upsert", ScopeKeyed, upsert o
        "Search", ScopePredicated, search o
        "ListChunks", ScopePredicated, listChunks o
        "DeleteChunk", ScopePredicated, deleteChunk o
        "RestoreChunk", ScopePredicated, restoreChunk o
        "Vacuum", ScopePredicated, vacuum o
        "DeleteByScope", ScopePredicated, deleteByScope o
    ]

// ─── Store ───────────────────────────────────────────────────────────

/// PostgreSQL + pgvector `IVectorStore`. Durable and multi-replica-safe:
/// the index is the database, so replicas share one view of the corpus
/// with no per-process state to reconcile.
///
/// Construct via `PgvectorVectorStore.create` / `createWithDataSource`,
/// which perform the `create`-time connectivity + schema probe. The
/// constructor itself is deliberately I/O-free so the probe's failure
/// mode is a single, descriptive exception from one place.
type PgvectorVectorStore(dataSource: NpgsqlDataSource, options: PgvectorOptions, ownsDataSource: bool, ?logger: ILogger)
    =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    // Same toggle, same meaning as the in-tree stores: with it set, an
    // undecodable row aborts the read instead of degrading to empty
    // metadata. A compliance deployment would rather stop than answer
    // from a corpus it cannot fully read.
    let refuseOnCorruption =
        match Environment.GetEnvironmentVariable ConfigKeys.Names.ragRefuseOnIndexCorruption with
        | "1"
        | "true"
        | "TRUE" -> true
        | _ -> false

    let sqlUpsert = Sql.upsert options
    let sqlSearch = Sql.search options
    let sqlListChunks = Sql.listChunks options
    let sqlDeleteChunk = Sql.deleteChunk options
    let sqlRestoreChunk = Sql.restoreChunk options
    let sqlVacuum = Sql.vacuum options
    let sqlDeleteByScope = Sql.deleteByScope options
    let sqlListScopes = Sql.listScopes options

    let newCommand (sql: string) =
        let cmd = dataSource.CreateCommand sql

        if options.CommandTimeoutSeconds > 0 then
            cmd.CommandTimeout <- options.CommandTimeoutSeconds

        cmd

    /// Decode a `metadata` column, honouring the fail-loud toggle.
    let decodeMetadata (scopeKey: string) (chunkId: string) (json: string) : Map<string, string> =
        match Metadata.fromJson json with
        | Ok m -> m
        | Error reason ->
            if refuseOnCorruption then
                fail (
                    sprintf
                        "[PgvectorVectorStore] Refusing to read chunk '%s' in scope '%s' from table '%s': its metadata column is not a decodable JSON object (%s). TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION is set — repair or delete the row, then retry (unset the variable to fall back to the default empty-metadata behaviour)."
                        chunkId
                        scopeKey
                        options.Table
                        reason
                )

            log.Warn
                $"[PgvectorVectorStore] Undecodable metadata on chunk '{chunkId}' in scope '{scopeKey}': {reason} — reading it as empty."

            Map.empty

    /// Project the `deleted_at` column back onto the metadata map, so
    /// `ListChunks includeDeleted = true` surfaces `_deletedAt` exactly
    /// as the in-tree stores do. The column is the source of truth (it
    /// is what `Vacuum` predicates on); the metadata key is its
    /// contract-visible projection.
    let withTombstone (deletedAt: DateTime option) (metadata: Map<string, string>) =
        match deletedAt with
        | Some ts ->
            metadata
            |> Map.add
                ChunkMetadata.DeletedAtKey
                (DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToString "o")
        | None -> metadata |> Map.remove ChunkMetadata.DeletedAtKey

    let readerDeletedAt (reader: Data.Common.DbDataReader) (ordinal: int) =
        if reader.IsDBNull ordinal then
            None
        else
            Some(reader.GetFieldValue<DateTime> ordinal)

    /// The configuration this store was built with — read by the health
    /// probe and useful in diagnostics.
    member _.Options = options

    interface IVectorStore with

        member _.Upsert scope chunkId vector chunk = async {
            if vector.Length <> options.Dimensions then
                fail (
                    sprintf
                        "[PgvectorVectorStore] Chunk '%s' carries a %d-dimension vector but table '%s' is vector(%d). The column dimension is fixed at migration time — re-embed the corpus with the composed provider, or compose a separate store per embedding model."
                        chunkId
                        vector.Length
                        options.Table
                        options.Dimensions
                )

            let scopeKey = Scope.toKey scope
            // The tombstone lives in the column; strip any caller-supplied
            // copy so the two can never disagree.
            let metadata = chunk.Metadata |> Map.remove ChunkMetadata.DeletedAtKey

            use cmd = newCommand sqlUpsert
            cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore
            cmd.Parameters.AddWithValue("chunk_id", chunkId) |> ignore
            cmd.Parameters.AddWithValue("content", chunk.Content) |> ignore
            cmd.Parameters.AddWithValue("metadata", Metadata.toJson metadata) |> ignore

            cmd.Parameters.AddWithValue("embedding", Vector.toLiteral (Vector.normalise vector))
            |> ignore

            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.Search scopes query topK = async {
            if topK <= 0 || List.isEmpty scopes then
                return []
            else
                let queryLiteral = Vector.toLiteral (Vector.normalise query)
                let matches = ResizeArray<VectorMatch>()

                // One scope-parameterised query per requested scope. A
                // single `scope = ANY(@scopes)` query would be fewer
                // round-trips but would put the isolation guarantee inside
                // an array parameter; per-scope keeps `Sql.search`'s
                // predicate the only shape that exists (GP 4).
                for scope in scopes do
                    let scopeKey = Scope.toKey scope
                    use cmd = newCommand sqlSearch
                    cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore
                    cmd.Parameters.AddWithValue("embedding", queryLiteral) |> ignore
                    cmd.Parameters.AddWithValue("top_k", topK) |> ignore

                    use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                    let mutable go = true

                    while go do
                        let! has = reader.ReadAsync() |> Async.AwaitTask

                        if has then
                            let chunkId = reader.GetString 0

                            matches.Add {
                                ChunkId = chunkId
                                Content = reader.GetString 1
                                Score = reader.GetDouble 3
                                Scope = scope
                                Metadata = decodeMetadata scopeKey chunkId (reader.GetString 2)
                            }
                        else
                            go <- false

                // Same total order as every in-tree store: score
                // descending, ties broken on `(Scope, ChunkId)` so a
                // repeated query is byte-identical run to run — the
                // deterministic-ordering contract the eval gate rests on.
                return
                    matches
                    |> Seq.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                    |> Seq.truncate topK
                    |> Seq.toList
        }

        member _.ListChunks scope includeDeleted = async {
            let scopeKey = Scope.toKey scope
            use cmd = newCommand sqlListChunks
            cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore
            cmd.Parameters.AddWithValue("include_deleted", includeDeleted) |> ignore

            use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
            let acc = ResizeArray<string * TextChunk>()
            let mutable go = true

            while go do
                let! has = reader.ReadAsync() |> Async.AwaitTask

                if has then
                    let chunkId = reader.GetString 0
                    let metadata = decodeMetadata scopeKey chunkId (reader.GetString 2)

                    acc.Add(
                        chunkId,
                        {
                            Content = reader.GetString 1
                            Metadata = withTombstone (readerDeletedAt reader 3) metadata
                        }
                    )
                else
                    go <- false

            return List.ofSeq acc
        }

        member _.DeleteChunk scope chunkId = async {
            use cmd = newCommand sqlDeleteChunk
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            cmd.Parameters.AddWithValue("chunk_id", chunkId) |> ignore

            cmd.Parameters.AddWithValue("deleted_at", DateTimeOffset.UtcNow.UtcDateTime)
            |> ignore

            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.RestoreChunk scope chunkId = async {
            use cmd = newCommand sqlRestoreChunk
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            cmd.Parameters.AddWithValue("chunk_id", chunkId) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.Vacuum scope olderThan = async {
            use cmd = newCommand sqlVacuum
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            cmd.Parameters.AddWithValue("older_than", olderThan.UtcDateTime) |> ignore
            let! purged = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return purged
        }

        member _.DeleteByScope scope = async {
            use cmd = newCommand sqlDeleteByScope
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.ListScopes() = async {
            use cmd = newCommand sqlListScopes
            use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
            let acc = ResizeArray<VectorScope>()
            let mutable go = true

            while go do
                let! has = reader.ReadAsync() |> Async.AwaitTask

                if has then
                    acc.Add(Scope.fromKey (reader.GetString 0))
                else
                    go <- false

            return List.ofSeq acc
        }

        member this.Erase(scope, subjectUserId, policy, dryRun) =
            ToolUp.Platform.IVectorStore.eraseSubject (this :> IVectorStore) scope subjectUserId policy dryRun

    interface IDisposable with
        member _.Dispose() =
            if ownsDataSource then
                dataSource.Dispose()

// ─── create-time probe (507.C) ───────────────────────────────────────

/// Connectivity + extension + schema reconciliation, run once at
/// construction. Every failure is a descriptive `PgvectorStoreException`
/// naming the operator action — never a deferred failure on the first
/// retrieval of a live request.
let private probeAndMigrate (dataSource: NpgsqlDataSource) (options: PgvectorOptions) : Async<unit> = async {
    // 1. Connectivity. A store that cannot reach its database must not
    //    be composed at all.
    try
        use cmd = dataSource.CreateCommand "SELECT 1;"
        let! _ = cmd.ExecuteScalarAsync() |> Async.AwaitTask
        ()
    with ex ->
        fail (
            sprintf
                "[PgvectorVectorStore] Cannot reach the configured PostgreSQL database: %s. The store is not composed — check the connection string, network reachability and credentials."
                ex.Message
        )

    // 2. The `vector` extension. Under AutoMigrate try to create it (the
    //    common single-role dev/CI case); if the role lacks the grant,
    //    fall through to the presence probe rather than reporting a
    //    permission error the operator cannot act on directly.
    let extensionPresent () = async {
        use cmd = dataSource.CreateCommand Sql.ExtensionPresent
        let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask
        return not (isNull result || result = box DBNull.Value)
    }

    match options.SchemaMode with
    | AutoMigrate ->
        let mutable created = false

        try
            use cmd = dataSource.CreateCommand Sql.CreateExtension
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            created <- true
        with _ ->
            created <- false

        if not created then
            let! present = extensionPresent ()

            if not present then
                fail
                    "[PgvectorVectorStore] The `vector` extension is not installed and this role may not create it. Run `CREATE EXTENSION vector;` as a superuser in the target database (pgvector must be installed on the server), then restart."
    | VerifyOnly ->
        let! present = extensionPresent ()

        if not present then
            fail
                "[PgvectorVectorStore] The `vector` extension is not installed in the target database. SchemaMode = VerifyOnly, so this companion will not create it — run `CREATE EXTENSION vector;` as a superuser, then restart."

    // 3. Schema.
    match options.SchemaMode with
    | AutoMigrate ->
        for statement in Sql.migration options do
            try
                use cmd = dataSource.CreateCommand statement
                let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                ()
            with ex ->
                fail (
                    sprintf
                        "[PgvectorVectorStore] Schema migration failed on `%s`: %s. Either grant this role DDL rights on the target schema, or provision the table out of band and compose with SchemaMode = VerifyOnly."
                        (statement.Split '\n' |> Array.head)
                        ex.Message
                )
    | VerifyOnly ->
        use cmd = dataSource.CreateCommand Sql.tableRegclass
        cmd.Parameters.AddWithValue("table", options.Table) |> ignore
        let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask

        if isNull result || result = box DBNull.Value then
            fail (
                sprintf
                    "[PgvectorVectorStore] Table '%s' does not exist in the target database and SchemaMode = VerifyOnly. Provision it with the DDL in the companion README, or compose with SchemaMode = AutoMigrate."
                    options.Table
            )
}

let private validateOrFail (options: PgvectorOptions) =
    match PgvectorOptions.validate options with
    | Ok() -> ()
    | Error message -> fail (sprintf "[PgvectorVectorStore] Invalid PgvectorOptions — %s" message)

/// Build a store over a data source the CALLER owns (a shared pool, or a
/// data source configured with TLS / logging the deployment supplies).
/// Disposing the store leaves the data source open.
///
/// Options are validated and the database probed before the store is
/// returned, so a `create` that returns has a store that works.
let createWithDataSource
    (dataSource: NpgsqlDataSource)
    (options: PgvectorOptions)
    (logger: ILogger option)
    : IVectorStore =
    validateOrFail options
    probeAndMigrate dataSource options |> Async.RunSynchronously

    match logger with
    | Some l -> new PgvectorVectorStore(dataSource, options, false, l) :> IVectorStore
    | None -> new PgvectorVectorStore(dataSource, options, false) :> IVectorStore

/// Build a store from a connection string. The store owns the resulting
/// `NpgsqlDataSource` and disposes it with itself.
///
/// ```
/// let store =
///     PgvectorVectorStore.create connectionString (PgvectorOptions.forDimensions 1536) (Some logger)
/// ```
let create (connectionString: string) (options: PgvectorOptions) (logger: ILogger option) : IVectorStore =
    validateOrFail options

    if String.IsNullOrWhiteSpace connectionString then
        fail
            "[PgvectorVectorStore] The connection string is empty. Supply it from ISecretStore / configuration at compose time."

    let dataSource =
        try
            NpgsqlDataSource.Create connectionString
        with ex ->
            fail (sprintf "[PgvectorVectorStore] The connection string could not be parsed: %s" ex.Message)

    try
        probeAndMigrate dataSource options |> Async.RunSynchronously
    with _ ->
        dataSource.Dispose()
        reraise ()

    match logger with
    | Some l -> new PgvectorVectorStore(dataSource, options, true, l) :> IVectorStore
    | None -> new PgvectorVectorStore(dataSource, options, true) :> IVectorStore