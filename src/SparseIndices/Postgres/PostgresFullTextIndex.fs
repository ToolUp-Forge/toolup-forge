// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.SparseIndices.Postgres.PostgresFullTextIndex

open System
open System.Collections.Generic
open System.Text.Json
open Npgsql
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.ISparseIndex
open ToolUp.RAG.SparseAnalysis

// ─── Phase 893 — PostgreSQL full-text ISparseIndex ───────────────────
//
// The keyword leg of hybrid retrieval, held in the database rather than in
// one process. The in-process `InMemoryBM25Index` keeps its postings in
// process memory and persists them as a blob, so two replicas of one
// deployment each own a private keyword index: a chunk ingested on replica
// A is invisible to replica B's keyword leg, and hybrid retrieval returns
// different results depending on which replica served the turn. Here the
// index IS the database — every replica reads and writes the same rows, so
// the keyword leg is the same on every replica.
//
// **The scoring function is the database's, not BM25.** Rows are ranked
// with `ts_rank(tsv, query, 1)` — term frequency normalised by document
// length, with NO inverse-document-frequency term. Scores are on a
// different scale from the in-process index (roughly [0, 1] rather than
// [0, ~20]) and, more importantly, the ORDER can differ: a rare term and a
// common term weigh the same here. Reciprocal Rank Fusion reads ranks, not
// scores, so the scale difference is invisible to fusion — the order
// difference is not. The companion README records what that does to the
// fused rank and the retrieval-evaluation numbers side by side.
//
// **Query semantics match the in-process index: any term matches.** The
// query is analysed with the same text-search configuration as the rows
// and its lexemes are OR-ed, so a chunk matching one query term is a
// candidate, exactly as a BM25 posting would be. (`plainto_tsquery` alone
// would AND them, which is a different retrieval contract.)
//
// **The analyzer seam maps to a text-search configuration.** Tokenisation
// happens inside the database, so a composed `ISparseAnalyzer` cannot run
// here; the companion maps its id to the configuration that reproduces it
// (`AnalyzerMapping`) and REFUSES at construction when there is none,
// naming the analyzer — never a silent fall-back to a configuration that
// tokenises differently from what the deployment asked for.
//
// **GP 1 — vendor isolation.** Npgsql lives in this companion and never
// reaches `ToolUp.Platform.*`; nothing Postgres-shaped crosses
// `ISparseIndex`.
//
// **GP 4 — structural scope isolation.** Scope is part of the composite
// primary key `(scope, chunk_id)`, and every statement that reads or
// mutates rows carries a `scope = @scope` predicate; the INSERT carries it
// in the row identity and its `ON CONFLICT (scope, chunk_id)` target.
// `Sql.scopeBoundStatements` enumerates them for the test pack.
//
// **GP 12 — portability.** Identity by value, async at every boundary, no
// callbacks, stateless between calls (the data source is a pool), no
// cross-scope ordering promise beyond the total order the in-process index
// also uses.
//
// Distributed-readiness: **production-ready / distributed-ready**. Two or
// more replicas may share one database.

// ─── Failure ─────────────────────────────────────────────────────────

/// Raised for every companion-level failure: option validation, an
/// analyzer with no text-search configuration, and the construction-time
/// connectivity / configuration / schema probe. One exception type so a
/// composing app can catch the companion's failures without matching on
/// Npgsql internals.
exception PostgresFullTextIndexException of message: string

let private fail (message: string) =
    raise (PostgresFullTextIndexException message)

// ─── Scope key ───────────────────────────────────────────────────────

/// The `scope` column encoding — the same as the in-tree stores' blob path
/// segment and the pgvector companion's column, so an operator reads one
/// vocabulary across every store.
module Scope =
    /// Encode a `VectorScope` as its `scope` column value.
    let toKey (scope: VectorScope) : string =
        match scope with
        | Platform -> "platform"
        | Deployment -> "deployment"
        | Team teamId -> $"team:{teamId}"
        | User userId -> $"user:{userId}"

// ─── Metadata codec ──────────────────────────────────────────────────

/// `TextChunk.Metadata` as a flat `jsonb` object, so an operator can query
/// it with ordinary SQL.
module Metadata =
    /// Encode to a flat JSON object.
    let toJson (metadata: Map<string, string>) : string =
        let dict = Dictionary<string, string>()

        for KeyValue(k, v) in metadata do
            dict[k] <- v

        JsonSerializer.Serialize dict

    /// Decode a flat JSON object. Non-string values are read as their raw
    /// text, so a row hand-edited in psql stays readable. `Error` carries
    /// the reason.
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

// ─── Analyzer → text-search configuration ────────────────────────────

/// Maps an `ISparseAnalyzer.Id` to the PostgreSQL text-search configuration
/// that tokenises the same way. Deliberately narrow: a mapping is offered
/// only where the configuration does what the analyzer does, because a
/// configuration that tokenises differently would make this companion's
/// keyword leg silently disagree with the one the deployment composed.
module AnalyzerMapping =
    /// `SparseAnalysis.identity` (word runs, lower-cased, no language
    /// processing) → `simple` (the default parser, lower-cased, no
    /// dictionary). The two agree on ordinary prose; they can differ on
    /// tokens the default parser treats specially (a hyphenated word is
    /// indexed whole and in parts), which only ever adds matches.
    [<Literal>]
    let IdentityConfiguration = "simple"

    /// The Snowball companion's English analyzer with its built-in (Porter2)
    /// stemmer and stop-word removal → `english`, whose dictionary is the
    /// Snowball English stemmer with an English stop list.
    [<Literal>]
    let EnglishConfiguration = "english"

    /// The configuration that reproduces `analyzerId`, or `None` when there
    /// is none. Recognised: `identity`, and the Snowball English family
    /// (`snowball+en+porter2+stop+…`, any folding and minimum-length
    /// setting). Everything else — a CJK n-gram analyzer, a Snowball
    /// analyzer without stemming or with a custom stemmer, a hand-written
    /// analyzer — has no equivalent configuration.
    let tryMap (analyzerId: string) : string option =
        if isNull analyzerId then
            None
        elif analyzerId = IdentityAnalyzerId then
            Some IdentityConfiguration
        else
            match analyzerId.Split '+' |> List.ofArray with
            | "snowball" :: "en" :: "porter2" :: "stop" :: _ -> Some EnglishConfiguration
            | _ -> None

// ─── Options ─────────────────────────────────────────────────────────

/// How construction reconciles the database schema.
type FullTextSchemaMode =
    /// Issue the idempotent `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF
    /// NOT EXISTS` migration (serialised across processes by an advisory
    /// lock), and re-analyse rows written under a different configuration.
    /// Needs DDL rights.
    | AutoMigrate
    /// Verify the table is present and every row was analysed with the
    /// resolved configuration; refuse to start otherwise. For a role with no
    /// DDL grant, whose schema a migration tool provisions.
    | VerifyOnly

/// Companion configuration. Start from `PostgresFullTextOptions.defaults`.
type PostgresFullTextOptions = {
    /// Unqualified table name. Validated as a plain SQL identifier —
    /// identifiers cannot be parameterised, so the guard is the injection
    /// boundary.
    Table: string
    /// Schema reconciliation performed at construction.
    SchemaMode: FullTextSchemaMode
    /// An explicit text-search configuration (e.g. `french`). `None`
    /// (default) derives it from the composed analyzer through
    /// `AnalyzerMapping.tryMap` and refuses when there is none. `Some cfg`
    /// is the operator's deliberate choice: it is used on both the index
    /// and the query side, so the two still agree, and the analyzer is not
    /// consulted.
    TextSearchConfiguration: string option
    /// Per-command timeout in seconds. `0` inherits the data source's.
    CommandTimeoutSeconds: int
}

/// Defaults and validation for `PostgresFullTextOptions`.
module PostgresFullTextOptions =
    /// Table `toolup_rag_fulltext`, `AutoMigrate`, configuration derived
    /// from the analyzer, the data source's command timeout.
    let defaults: PostgresFullTextOptions = {
        Table = "toolup_rag_fulltext"
        SchemaMode = AutoMigrate
        TextSearchConfiguration = None
        CommandTimeoutSeconds = 0
    }

    /// A plain unquoted SQL identifier: leading letter or underscore, then
    /// ASCII letters / digits / underscores, at most 63 bytes. The table
    /// name and configuration name are interpolated or cast, so the narrow
    /// set is the injection guard.
    let isSafeIdentifier (name: string) : bool =
        not (String.IsNullOrWhiteSpace name)
        && name.Length <= 63
        && (Char.IsLetter name[0] || name[0] = '_')
        && name |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')
        && name |> Seq.forall (fun c -> int c < 128)

    /// Bounds-check before any I/O, naming the offending field.
    let validate (o: PostgresFullTextOptions) : Result<unit, string> =
        if not (isSafeIdentifier o.Table) then
            Error(
                sprintf
                    "Table must be a plain SQL identifier (letter or underscore, then letters/digits/underscores, max 63 chars); got '%s'."
                    o.Table
            )
        elif o.CommandTimeoutSeconds < 0 then
            Error(sprintf "CommandTimeoutSeconds must be >= 0; got %d." o.CommandTimeoutSeconds)
        else
            match o.TextSearchConfiguration with
            | Some cfg when not (isSafeIdentifier cfg) ->
                Error(sprintf "TextSearchConfiguration must be a plain SQL identifier; got '%s'." cfg)
            | _ -> Ok()

    /// The configuration this index runs with: the explicit one, else the
    /// analyzer's mapping. `Error` names the analyzer.
    let resolveConfiguration (o: PostgresFullTextOptions) (analyzer: ISparseAnalyzer) : Result<string, string> =
        match o.TextSearchConfiguration with
        | Some cfg -> Ok cfg
        | None ->
            match AnalyzerMapping.tryMap analyzer.Id with
            | Some cfg -> Ok cfg
            | None ->
                Error(
                    sprintf
                        "The composed sparse analyzer '%s' has no equivalent PostgreSQL text-search configuration. This index tokenises inside the database, so it cannot run that analyzer; composing it anyway would make the keyword leg tokenise differently from what the deployment asked for. Compose an analyzer that maps (identity → simple, the Snowball English analyzer → english), set PostgresFullTextOptions.TextSearchConfiguration to a configuration you have chosen deliberately, or keep the in-process index for this analyzer."
                        analyzer.Id
                )

// ─── SQL ─────────────────────────────────────────────────────────────

/// Every statement the index issues, as a function of the options. Kept in
/// one place so the scope-binding property can be read (and tested) from a
/// single list.
module Sql =
    /// The table, the GIN index over the text-search column, and nothing
    /// else. `ts_config` records the configuration each row was analysed
    /// with, so a configuration change is detected rather than served.
    let createSchema (o: PostgresFullTextOptions) =
        $"""CREATE TABLE IF NOT EXISTS {o.Table} (
    scope text NOT NULL,
    chunk_id text NOT NULL,
    content text NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{{}}'::jsonb,
    ts_config text NOT NULL,
    tsv tsvector NOT NULL,
    PRIMARY KEY (scope, chunk_id)
);
CREATE INDEX IF NOT EXISTS {o.Table}_tsv_gin ON {o.Table} USING gin (tsv);"""

    /// Serialises concurrent migrations from several processes: `CREATE …
    /// IF NOT EXISTS` is not race-free on its own.
    let migrationLock = "SELECT pg_advisory_xact_lock(hashtext(@lock_key));"

    /// Whether the table exists.
    let tablePresent = "SELECT to_regclass(@table) IS NOT NULL;"

    /// Whether a text-search configuration exists.
    let configurationPresent =
        "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_ts_config WHERE cfgname = @cfg);"

    /// Rows analysed under a different configuration.
    let staleRowCount (o: PostgresFullTextOptions) =
        $"SELECT count(*) FROM {o.Table} WHERE ts_config <> @cfg;"

    /// Re-analyse rows analysed under a different configuration.
    let reanalyse (o: PostgresFullTextOptions) =
        $"UPDATE {o.Table} SET tsv = to_tsvector(@cfg::regconfig, content), ts_config = @cfg WHERE ts_config <> @cfg;"

    /// Insert or replace one chunk. Scope is bound by the row identity and
    /// the conflict target.
    let upsert (o: PostgresFullTextOptions) =
        $"""INSERT INTO {o.Table} (scope, chunk_id, content, metadata, ts_config, tsv)
VALUES (@scope, @chunk_id, @content, @metadata::jsonb, @cfg, to_tsvector(@cfg::regconfig, @content))
ON CONFLICT (scope, chunk_id) DO UPDATE
SET content = EXCLUDED.content, metadata = EXCLUDED.metadata, ts_config = EXCLUDED.ts_config, tsv = EXCLUDED.tsv;"""

    /// The query's lexemes OR-ed into one `tsquery` — any term matches, as
    /// in the in-process index. Each lexeme is quoted for the `tsquery`
    /// input syntax (quote and backslash escaped) so no lexeme can inject an
    /// operator.
    let private orQuery =
        "(SELECT coalesce(string_agg('''' || replace(replace(lex, '\\', '\\\\'), '''', '''''') || '''', ' | '), '')::tsquery "
        + "FROM unnest(tsvector_to_array(to_tsvector(@cfg::regconfig, @query))) AS lex)"

    /// Search one scope. Equal scores order by chunk id, byte order — the
    /// in-process index's tie-break within a scope.
    let search (o: PostgresFullTextOptions) =
        $"""SELECT chunk_id, content, metadata::text, ts_rank(tsv, q.query, 1)::float8 AS score
FROM {o.Table}, {orQuery} AS q(query)
WHERE scope = @scope AND tsv @@ q.query
ORDER BY score DESC, chunk_id COLLATE "C"
LIMIT @top_k;"""

    /// Delete every chunk in one scope.
    let deleteByScope (o: PostgresFullTextOptions) =
        $"DELETE FROM {o.Table} WHERE scope = @scope;"

    /// Delete one chunk.
    let deleteChunk (o: PostgresFullTextOptions) =
        $"DELETE FROM {o.Table} WHERE scope = @scope AND chunk_id = @chunk_id;"

    /// The subject-match predicate `IVectorStore.eraseSubject` uses: the
    /// subject id appears in the content or in any metadata value.
    /// `strpos` is a byte-exact, case-sensitive match, as `String.Contains`
    /// is in the in-process index.
    let private subjectMatch =
        "(strpos(content, @subject) > 0 OR EXISTS (SELECT 1 FROM jsonb_each_text(metadata) AS m WHERE strpos(m.value, @subject) > 0))"

    /// Count a subject's chunks in one scope.
    let countSubject (o: PostgresFullTextOptions) =
        $"SELECT count(*) FROM {o.Table} WHERE scope = @scope AND {subjectMatch};"

    /// Delete a subject's chunks in one scope.
    let eraseSubject (o: PostgresFullTextOptions) =
        $"DELETE FROM {o.Table} WHERE scope = @scope AND {subjectMatch};"

    /// Readiness probe — reaches the table without reading a scope.
    let probe (o: PostgresFullTextOptions) = $"SELECT 1 FROM {o.Table} LIMIT 1;"

    /// Every statement that reads or mutates chunk rows, named by the
    /// interface member it serves. Each carries `scope = @scope` (or, for
    /// the insert, the scope in its row identity and conflict target).
    let scopeBoundStatements (o: PostgresFullTextOptions) : (string * string) list = [
        "Upsert", upsert o
        "Search", search o
        "DeleteByScope", deleteByScope o
        "DeleteChunk", deleteChunk o
        "Erase (count)", countSubject o
        "Erase (delete)", eraseSubject o
    ]

// ─── The index ───────────────────────────────────────────────────────

/// `ISparseIndex` over a PostgreSQL table with a `tsvector` column and a
/// GIN index. Construct through `create` / `createWithDataSource` (or
/// `factory` for `RAGServerApp.withAnalyzedSparseIndex`), which validate the
/// options, resolve the text-search configuration and probe the database
/// before returning.
type PostgresFullTextIndex
    (
        dataSource: NpgsqlDataSource,
        options: PostgresFullTextOptions,
        configuration: string,
        ownsDataSource: bool,
        logger: ILogger option
    ) =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    let sqlUpsert = Sql.upsert options
    let sqlSearch = Sql.search options
    let sqlDeleteByScope = Sql.deleteByScope options
    let sqlDeleteChunk = Sql.deleteChunk options
    let sqlCountSubject = Sql.countSubject options
    let sqlEraseSubject = Sql.eraseSubject options
    let sqlProbe = Sql.probe options

    let newCommand (sql: string) =
        let cmd = dataSource.CreateCommand sql

        if options.CommandTimeoutSeconds > 0 then
            cmd.CommandTimeout <- options.CommandTimeoutSeconds

        cmd

    let decodeMetadata (scopeKey: string) (chunkId: string) (json: string) =
        match Metadata.fromJson json with
        | Ok m -> m
        | Error reason ->
            log.Warn(
                sprintf
                    "[PostgresFullTextIndex] Chunk '%s' in scope '%s' has undecodable metadata (%s); serving it with empty metadata."
                    chunkId
                    scopeKey
                    reason
            )

            Map.empty

    let searchScope (scope: VectorScope) (query: string) (topK: int) = async {
        let scopeKey = Scope.toKey scope
        use cmd = newCommand sqlSearch
        cmd.Parameters.AddWithValue("cfg", configuration) |> ignore
        cmd.Parameters.AddWithValue("query", query) |> ignore
        cmd.Parameters.AddWithValue("scope", scopeKey) |> ignore
        cmd.Parameters.AddWithValue("top_k", topK) |> ignore
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
        let rows = ResizeArray<VectorMatch>()
        let mutable go = true

        while go do
            let! more = reader.ReadAsync() |> Async.AwaitTask

            if more then
                let chunkId = reader.GetString 0

                rows.Add {
                    ChunkId = chunkId
                    Content = reader.GetString 1
                    Metadata = decodeMetadata scopeKey chunkId (reader.GetString 2)
                    Score = reader.GetDouble 3
                    Scope = scope
                }
            else
                go <- false

        return List.ofSeq rows
    }

    let scalarInt64 (sql: string) (bind: NpgsqlCommand -> unit) = async {
        use cmd = newCommand sql
        bind cmd
        let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask
        return Convert.ToInt64 result
    }

    /// The companion options this index was built with.
    member _.Options = options

    /// The PostgreSQL text-search configuration both sides run with.
    member _.TextSearchConfiguration = configuration

    /// A cheap reachability round-trip against the table; the readiness
    /// probe runs it.
    member _.Probe() : Async<unit> = async {
        use cmd = newCommand sqlProbe
        let! _ = cmd.ExecuteScalarAsync() |> Async.AwaitTask
        return ()
    }

    interface ISparseIndex with

        member _.Upsert scope chunkId chunk = async {
            use cmd = newCommand sqlUpsert
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            cmd.Parameters.AddWithValue("chunk_id", chunkId) |> ignore
            cmd.Parameters.AddWithValue("content", chunk.Content) |> ignore

            cmd.Parameters.AddWithValue("metadata", Metadata.toJson chunk.Metadata)
            |> ignore

            cmd.Parameters.AddWithValue("cfg", configuration) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.Search scopes query topK = async {
            if topK <= 0 || List.isEmpty scopes || String.IsNullOrWhiteSpace query then
                return []
            else
                // One scope-parameterised statement per scope, so the only
                // statement shape that exists binds one scope (GP 4). The
                // merge applies the in-process index's total order —
                // score descending, then (Scope, ChunkId) — so equal scores
                // come back in the same order from either index.
                let! perScope =
                    scopes
                    |> List.distinct
                    |> List.map (fun scope -> searchScope scope query topK)
                    |> Async.Sequential

                return
                    perScope
                    |> Array.toList
                    |> List.concat
                    |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                    |> List.truncate topK
        }

        member _.DeleteByScope scope = async {
            use cmd = newCommand sqlDeleteByScope
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.DeleteChunk scope chunkId = async {
            use cmd = newCommand sqlDeleteChunk
            cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
            cmd.Parameters.AddWithValue("chunk_id", chunkId) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            return ()
        }

        member _.Erase(scope, subjectUserId, policy, dryRun) = async {
            // Same matching contract as the in-process index. A lexical
            // index has no tombstone tier, so every policy is a hard delete.
            ignore policy

            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "sparse-index"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op"
                    }
            else
                try
                    let bind (cmd: NpgsqlCommand) =
                        cmd.Parameters.AddWithValue("scope", Scope.toKey scope) |> ignore
                        cmd.Parameters.AddWithValue("subject", subjectUserId) |> ignore

                    if dryRun then
                        let! count = scalarInt64 sqlCountSubject bind

                        return
                            Result.Ok {
                                HandlerName = "sparse-index"
                                RecordsAffected = int count
                                Note = Some(sprintf "%d chunk(s) would be erased in scope" count)
                            }
                    else
                        use cmd = newCommand sqlEraseSubject
                        bind cmd
                        let! deleted = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask

                        return
                            Result.Ok {
                                HandlerName = "sparse-index"
                                RecordsAffected = deleted
                                Note =
                                    Some(
                                        sprintf "%d chunk(s) hard-deleted (lexical index has no tombstone tier)" deleted
                                    )
                            }
                with ex ->
                    return Result.Error(StoreUnreachable("sparse-index", ex.Message))
        }

    interface IDisposable with
        member _.Dispose() =
            if ownsDataSource then
                dataSource.Dispose()

// ─── construction-time probe ─────────────────────────────────────────

let private scalarBool (dataSource: NpgsqlDataSource) (sql: string) (bind: NpgsqlCommand -> unit) = async {
    use cmd = dataSource.CreateCommand sql
    bind cmd
    let! result = cmd.ExecuteScalarAsync() |> Async.AwaitTask
    return (not (isNull result)) && result <> box DBNull.Value && unbox<bool> result
}

let private probeAndMigrate
    (dataSource: NpgsqlDataSource)
    (options: PostgresFullTextOptions)
    (configuration: string)
    (log: ILogger)
    : Async<unit> =
    async {
        // 1. Connectivity.
        try
            use cmd = dataSource.CreateCommand "SELECT 1;"
            let! _ = cmd.ExecuteScalarAsync() |> Async.AwaitTask
            ()
        with ex ->
            fail (
                sprintf
                    "[PostgresFullTextIndex] Cannot reach the configured PostgreSQL database: %s. The index is not composed — check the connection string, network reachability and credentials."
                    ex.Message
            )

        // 2. The configuration exists in this database.
        let! configured =
            scalarBool dataSource Sql.configurationPresent (fun cmd ->
                cmd.Parameters.AddWithValue("cfg", configuration) |> ignore)

        if not configured then
            fail (
                sprintf
                    "[PostgresFullTextIndex] The text-search configuration '%s' does not exist in this database (pg_ts_config). Install it, or choose another via PostgresFullTextOptions.TextSearchConfiguration."
                    configuration
            )

        // 3. Schema.
        match options.SchemaMode with
        | AutoMigrate ->
            use conn = dataSource.CreateConnection()
            do! conn.OpenAsync() |> Async.AwaitTask
            use tx = conn.BeginTransaction()

            use lockCmd = new NpgsqlCommand(Sql.migrationLock, conn, tx)

            lockCmd.Parameters.AddWithValue("lock_key", "toolup-fulltext:" + options.Table)
            |> ignore

            let! _ = lockCmd.ExecuteNonQueryAsync() |> Async.AwaitTask

            use ddl = new NpgsqlCommand(Sql.createSchema options, conn, tx)
            let! _ = ddl.ExecuteNonQueryAsync() |> Async.AwaitTask

            // Rows analysed under another configuration are re-analysed,
            // so a configuration change never leaves the rows in one
            // vocabulary and the queries in another.
            use re = new NpgsqlCommand(Sql.reanalyse options, conn, tx)
            re.Parameters.AddWithValue("cfg", configuration) |> ignore
            let! reanalysed = re.ExecuteNonQueryAsync() |> Async.AwaitTask

            do! tx.CommitAsync() |> Async.AwaitTask

            if reanalysed > 0 then
                log.Warn(
                    sprintf
                        "[PostgresFullTextIndex] Re-analysed %d row(s) of '%s' under text-search configuration '%s' (they were written under another configuration)."
                        reanalysed
                        options.Table
                        configuration
                )
        | VerifyOnly ->
            let! present =
                scalarBool dataSource Sql.tablePresent (fun cmd ->
                    cmd.Parameters.AddWithValue("table", options.Table) |> ignore)

            if not present then
                fail (
                    sprintf
                        "[PostgresFullTextIndex] Table '%s' does not exist and SchemaMode = VerifyOnly. Provision it (the companion README gives the DDL) or use AutoMigrate with a role that holds the DDL grant."
                        options.Table
                )

            use cmd = dataSource.CreateCommand(Sql.staleRowCount options)
            cmd.Parameters.AddWithValue("cfg", configuration) |> ignore
            let! stale = cmd.ExecuteScalarAsync() |> Async.AwaitTask
            let stale = Convert.ToInt64 stale

            if stale > 0L then
                fail (
                    sprintf
                        "[PostgresFullTextIndex] %d row(s) of '%s' were analysed under a text-search configuration other than '%s', and SchemaMode = VerifyOnly cannot re-analyse them. Re-analyse them with the companion README's UPDATE, or start once under AutoMigrate."
                        stale
                        options.Table
                        configuration
                )
    }

let private validateOrFail (options: PostgresFullTextOptions) =
    match PostgresFullTextOptions.validate options with
    | Ok() -> ()
    | Error message -> fail (sprintf "[PostgresFullTextIndex] Invalid PostgresFullTextOptions — %s" message)

let private resolveOrFail (options: PostgresFullTextOptions) (analyzer: ISparseAnalyzer) =
    match PostgresFullTextOptions.resolveConfiguration options analyzer with
    | Ok cfg -> cfg
    | Error message -> fail ("[PostgresFullTextIndex] " + message)

let private build
    (dataSource: NpgsqlDataSource)
    (ownsDataSource: bool)
    (options: PostgresFullTextOptions)
    (analyzer: ISparseAnalyzer)
    (logger: ILogger option)
    : ISparseIndex =
    let configuration = resolveOrFail options analyzer

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    probeAndMigrate dataSource options configuration log |> Async.RunSynchronously

    new PostgresFullTextIndex(dataSource, options, configuration, ownsDataSource, logger) :> ISparseIndex

/// Build an index over a data source the CALLER owns. Disposing the index
/// leaves the data source open. The options are validated, the analyzer
/// mapped to its text-search configuration (refusing, and naming the
/// analyzer, when there is none) and the database probed before the index
/// is returned — every failure is a `PostgresFullTextIndexException`.
let createWithDataSource
    (dataSource: NpgsqlDataSource)
    (options: PostgresFullTextOptions)
    (analyzer: ISparseAnalyzer)
    (logger: ILogger option)
    : ISparseIndex =
    validateOrFail options
    build dataSource false options analyzer logger

/// Build an index from a connection string. The index owns the resulting
/// data source and disposes it with itself.
///
/// ```
/// let index =
///     PostgresFullTextIndex.create connectionString PostgresFullTextOptions.defaults SparseAnalysis.identity (Some logger)
/// ```
let create
    (connectionString: string)
    (options: PostgresFullTextOptions)
    (analyzer: ISparseAnalyzer)
    (logger: ILogger option)
    : ISparseIndex =
    validateOrFail options
    // The analyzer is resolved before any I/O, so an unmappable analyzer is
    // refused without a database in reach.
    resolveOrFail options analyzer |> ignore

    if String.IsNullOrWhiteSpace connectionString then
        fail
            "[PostgresFullTextIndex] The connection string is empty. Supply it from ISecretStore / configuration at compose time."

    let dataSource =
        try
            NpgsqlDataSource.Create connectionString
        with ex ->
            fail (sprintf "[PostgresFullTextIndex] The connection string could not be parsed: %s" ex.Message)

    try
        build dataSource true options analyzer logger
    with _ ->
        dataSource.Dispose()
        reraise ()

/// The composition-time factory for `RAGServerApp.withAnalyzedSparseIndex`:
/// the composition passes the analyzer it composed (the identity analyzer
/// when none), and the index is built for it — or refused, naming it.
let factory
    (connectionString: string)
    (options: PostgresFullTextOptions)
    (logger: ILogger option)
    : ISparseAnalyzer -> ISparseIndex =
    fun analyzer -> create connectionString options analyzer logger

// ─── Health ──────────────────────────────────────────────────────────

/// Readiness probe over a PostgreSQL full-text index: a pooled round-trip
/// against the table. A replica that cannot reach its keyword index should
/// leave rotation rather than serve dense-only answers it believes are
/// hybrid.
type PostgresFullTextIndexHealthCheck(index: ISparseIndex) =
    interface IHealthCheck with
        member _.Name = "sparse_index:postgres"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 2.0

        member _.Check() = async {
            match box index with
            | :? PostgresFullTextIndex as pg ->
                try
                    do! pg.Probe()
                    return Healthy
                with ex ->
                    return Unhealthy ex.Message
            | _ -> return Unhealthy "not a PostgresFullTextIndex — nothing to probe"
        }

/// The readiness probe for `index`.
let health (index: ISparseIndex) : IHealthCheck =
    PostgresFullTextIndexHealthCheck(index) :> IHealthCheck