// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SqliteLogStore

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Hosting

// ─── Phase 828 — SqliteLogStore (the self-hosted default) ───────────────
//
// The default `ILogStore`: one local SQLite database file, an FTS5 index
// over the message text, and ordinary B-tree indexes over the columns the
// query filters on (`timestamp`, `level`, `logger`, `scope_id`,
// `correlation_id`) so every filter pushes down to the engine rather than
// being applied after a full scan.
//
// **Production-capable, single-node.** SQLite is a file, not a server:
// this store is durable across restarts and bounded by retention, but it
// belongs to ONE process on ONE host. A multi-instance deployment composes
// a distributed companion against the same `ILogStore` contract (the
// `src/LogStores/Postgres/` directory reserves the first one). That is a
// topology statement, not the "dev-only" marker `InMemoryTimeSeriesStore`
// carries — an air-gapped single-node pilot is exactly what this is for.
//
// **Why the module wraps the type.** `LogStoreMode` has a `SqliteLogStore`
// union case, and a union case shadows a type of the same name in
// expression position. Housing the type inside a module of its own name —
// the `JsonConsoleLogger` precedent — keeps both spellings unambiguous:
// `SqliteLogStore cfg` is the mode, `SqliteLogStore.create cfg` is the
// store.
//
// **Concurrency.** One connection, opened once, guarded by a monitor. The
// writes arrive already serialised (the `LogStoreLogger` decorator drains
// a single queue) and a read is a few milliseconds against indexed
// columns, so a lock costs less than a connection pool and removes
// SQLITE_BUSY from the picture entirely. WAL is enabled so a concurrent
// reader on another connection — an operator with a sqlite3 shell — never
// blocks the writer.

/// The canonical column spelling of a level: the same tokens
/// `LogLevel.tryParse` accepts, so the round-trip needs no second table.
let private levelName =
    function
    | LogLevel.Trace -> "trace"
    | LogLevel.Debug -> "debug"
    | LogLevel.Info -> "info"
    | LogLevel.Warn -> "warn"
    | LogLevel.Error -> "error"

let private toUnixSeconds (t: DateTime) : int64 =
    DateTimeOffset(LogRecord.truncateToSecond t, TimeSpan.Zero).ToUnixTimeSeconds()

let private fromUnixSeconds (s: int64) : DateTime =
    DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime

/// A query bound, in unix seconds, rounded **up**. Stored timestamps are
/// second-aligned, so for any second-aligned `e`: `e >= bound` holds
/// exactly when `unix(e) >= ceil(bound)`, and `e < bound` exactly when
/// `unix(e) < ceil(bound)`. Truncating instead would silently widen a
/// sub-second `SinceUtc` by up to a second and make the SQL disagree with
/// `LogSearchQuery.matches` — the one place a portable contract can drift
/// without any test that uses whole seconds ever noticing.
let private boundUnixSeconds (t: DateTime) : int64 =
    let truncated = LogRecord.truncateToSecond t
    let floorSeconds = DateTimeOffset(truncated, TimeSpan.Zero).ToUnixTimeSeconds()

    let normalised =
        match t.Kind with
        | DateTimeKind.Local -> t.ToUniversalTime()
        | _ -> t

    if normalised.Ticks > truncated.Ticks then
        floorSeconds + 1L
    else
        floorSeconds

/// The FTS5 `MATCH` expression for a needle, or `None` when the needle
/// carries no tokens. Built from `LogSearchQuery.tokenise` — the same
/// tokenisation the portable rule uses — and joined with `AND`, each token
/// double-quoted so a token that happens to be an FTS5 keyword (`OR`,
/// `NEAR`, …) is read as a word rather than as syntax.
let private ftsExpression (needle: string) : string option =
    match LogSearchQuery.tokenise needle with
    | [] -> None
    | tokens ->
        tokens
        |> List.map (fun t -> "\"" + t.Replace("\"", "\"\"") + "\"")
        |> String.concat " AND "
        |> Some

/// The self-hosted SQLite `ILogStore`. Construct through
/// `SqliteLogStore.create`; the constructor opens (and creates, with its
/// parent directory) the database file named by `config.DatabasePath` and
/// applies the schema.
type SqliteLogStore(config: LogStoreConfig) =
    let gate = obj ()

    let connection =
        let path = config.DatabasePath

        let directory = Path.GetDirectoryName(Path.GetFullPath path)

        if not (String.IsNullOrEmpty directory) && not (Directory.Exists directory) then
            Directory.CreateDirectory directory |> ignore

        // `Pooling = false` is load-bearing, not a micro-optimisation: with
        // the default pool, disposing the connection returns the handle to
        // the pool rather than closing the file, so the database stays
        // LOCKED after `Dispose` and a caller that expected to be able to
        // move or delete the file cannot. This store holds exactly one
        // connection for its whole life, so a pool buys nothing anyway.
        let conn =
            new SqliteConnection(SqliteConnectionStringBuilder(DataSource = path, Pooling = false).ToString())

        conn.Open()
        conn

    let exec (sql: string) (bind: SqliteCommand -> unit) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        bind cmd
        cmd.ExecuteNonQuery()

    do
        // WAL keeps an operator's concurrent read from blocking the
        // writer; NORMAL synchronous is the documented WAL pairing —
        // durable across a process crash, and the one failure it admits
        // (an OS-level crash losing the last commits) costs log lines, not
        // system-of-record state.
        exec "PRAGMA journal_mode=WAL;" ignore |> ignore
        exec "PRAGMA synchronous=NORMAL;" ignore |> ignore
        // A second process opening the same file (an operator's sqlite3
        // shell, a sibling silo pointed at the same path) makes SQLITE_BUSY
        // possible however careful this process is with its own writes.
        exec "PRAGMA busy_timeout=5000;" ignore |> ignore

        exec
            """
            CREATE TABLE IF NOT EXISTS log_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp INTEGER NOT NULL,
                level TEXT NOT NULL,
                logger TEXT NOT NULL,
                message TEXT NOT NULL,
                scope_id TEXT NULL,
                correlation_id TEXT NULL,
                error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_log_entries_timestamp ON log_entries(timestamp);
            CREATE INDEX IF NOT EXISTS ix_log_entries_level ON log_entries(level);
            CREATE INDEX IF NOT EXISTS ix_log_entries_logger ON log_entries(logger);
            CREATE INDEX IF NOT EXISTS ix_log_entries_scope ON log_entries(scope_id);
            CREATE INDEX IF NOT EXISTS ix_log_entries_correlation ON log_entries(correlation_id);
            """
            ignore
        |> ignore

        // External-content FTS5 over `message`: the index stores no copy of
        // the text, and the two triggers keep it in step with the base
        // table — including under a bulk `DELETE`, which is what the
        // retention sweep does. The tokeniser is config so a deployment
        // with non-Latin logs can choose its own; the default is the one
        // `LogSearchQuery.tokenise` mirrors.
        exec
            $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS log_entries_fts USING fts5(
                message,
                content='log_entries',
                content_rowid='id',
                tokenize='{config.FtsTokeniser}'
            );
            CREATE TRIGGER IF NOT EXISTS log_entries_fts_ai AFTER INSERT ON log_entries BEGIN
                INSERT INTO log_entries_fts(rowid, message) VALUES (new.id, new.message);
            END;
            CREATE TRIGGER IF NOT EXISTS log_entries_fts_ad AFTER DELETE ON log_entries BEGIN
                INSERT INTO log_entries_fts(log_entries_fts, rowid, message)
                VALUES('delete', old.id, old.message);
            END;
            """
            ignore
        |> ignore

    let bindOptional (cmd: SqliteCommand) (name: string) (value: string option) =
        match value with
        | Some v -> cmd.Parameters.AddWithValue(name, box v) |> ignore
        | None -> cmd.Parameters.AddWithValue(name, box DBNull.Value) |> ignore

    let readOptional (reader: SqliteDataReader) (ordinal: int) =
        if reader.IsDBNull ordinal then
            None
        else
            Some(reader.GetString ordinal)

    let appendCore (entry: LogRecord) =
        exec """
            INSERT INTO log_entries (timestamp, level, logger, message, scope_id, correlation_id, error)
            VALUES (@timestamp, @level, @logger, @message, @scope, @correlation, @error);
            """ (fun cmd ->
            cmd.Parameters.AddWithValue("@timestamp", toUnixSeconds entry.TimestampUtc)
            |> ignore

            cmd.Parameters.AddWithValue("@level", levelName entry.Level) |> ignore
            cmd.Parameters.AddWithValue("@logger", entry.Logger) |> ignore
            cmd.Parameters.AddWithValue("@message", entry.Message) |> ignore
            bindOptional cmd "@scope" entry.ScopeId
            bindOptional cmd "@correlation" entry.CorrelationId
            bindOptional cmd "@error" entry.Error)
        |> ignore

    let searchCore (query: LogSearchQuery) : LogRecord list =
        if query.Limit <= 0 then
            []
        else
            use cmd = connection.CreateCommand()

            let clauses = ResizeArray<string>()
            clauses.Add "timestamp >= @since"
            clauses.Add "timestamp < @until"
            cmd.Parameters.AddWithValue("@since", boundUnixSeconds query.SinceUtc) |> ignore
            cmd.Parameters.AddWithValue("@until", boundUnixSeconds query.UntilUtc) |> ignore

            match query.Levels with
            | [] -> ()
            | levels ->
                let names =
                    levels
                    |> List.distinct
                    |> List.mapi (fun i level ->
                        let p = $"@level{i}"
                        cmd.Parameters.AddWithValue(p, levelName level) |> ignore
                        p)

                clauses.Add $"""level IN ({String.concat ", " names})"""

            match query.ScopeId with
            | Some s ->
                clauses.Add "scope_id = @scopeFilter"
                cmd.Parameters.AddWithValue("@scopeFilter", s) |> ignore
            | None -> ()

            match query.CorrelationId with
            | Some c ->
                clauses.Add "correlation_id = @correlationFilter"
                cmd.Parameters.AddWithValue("@correlationFilter", c) |> ignore
            | None -> ()

            match query.TextMatch |> Option.bind ftsExpression with
            | Some expression ->
                clauses.Add "id IN (SELECT rowid FROM log_entries_fts WHERE log_entries_fts MATCH @needle)"
                cmd.Parameters.AddWithValue("@needle", expression) |> ignore
            | None -> ()

            cmd.Parameters.AddWithValue("@limit", query.Limit) |> ignore

            cmd.CommandText <-
                "SELECT timestamp, level, logger, message, scope_id, correlation_id, error FROM log_entries WHERE "
                + String.concat " AND " clauses
                // `id DESC` is the tie-break rule 5 promises: within one
                // second, reverse append order.
                + " ORDER BY timestamp DESC, id DESC LIMIT @limit;"

            use reader = cmd.ExecuteReader()
            let results = ResizeArray<LogRecord>()

            while reader.Read() do
                let level =
                    reader.GetString 1
                    |> LogLevel.tryParse
                    // A row whose level token is unreadable is a corrupted
                    // row, not a reason to fail the whole search; it reads
                    // back at the level an operator most wants to see.
                    |> Option.defaultValue LogLevel.Error

                results.Add {
                    TimestampUtc = fromUnixSeconds (reader.GetInt64 0)
                    Level = level
                    Logger = reader.GetString 2
                    Message = reader.GetString 3
                    ScopeId = readOptional reader 4
                    CorrelationId = readOptional reader 5
                    Error = readOptional reader 6
                }

            List.ofSeq results

    let pruneCore (olderThanUtc: DateTime) : int =
        exec "DELETE FROM log_entries WHERE timestamp < @bound;" (fun cmd ->
            cmd.Parameters.AddWithValue("@bound", boundUnixSeconds olderThanUtc) |> ignore)

    let trimCore (maxRows: int64) : int =
        if maxRows <= 0L then
            0
        else
            // Delete by the primary key of the oldest surplus rows rather
            // than by a computed timestamp bound: two rows can share a
            // second, and a bound would over- or under-delete whichever way
            // it rounded.
            exec """
                DELETE FROM log_entries WHERE id IN (
                    SELECT id FROM log_entries
                    ORDER BY timestamp ASC, id ASC
                    LIMIT MAX(0, (SELECT COUNT(*) FROM log_entries) - @maxRows)
                );
                """ (fun cmd -> cmd.Parameters.AddWithValue("@maxRows", maxRows) |> ignore)

    /// The configuration this store was opened with — the retention sweep
    /// reads its bounds from here rather than being told them twice.
    member _.Config = config

    interface ILogStore with
        member _.Append(entry: LogRecord) = async {
            let normalised = {
                entry with
                    TimestampUtc = LogRecord.truncateToSecond entry.TimestampUtc
            }

            lock gate (fun () -> appendCore normalised)
        }

        member _.Search(query: LogSearchQuery) = async { return lock gate (fun () -> searchCore query) }

        member _.Prune(olderThanUtc: DateTime) = async { return lock gate (fun () -> pruneCore olderThanUtc) }

    interface ILogStoreRetention with
        member _.TrimToMaxRows(maxRows: int64) = async { return lock gate (fun () -> trimCore maxRows) }

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                connection.Close()
                connection.Dispose())

/// Open (creating if absent) the SQLite-backed `ILogStore` described by
/// `config`. The returned value also implements `ILogStoreRetention` and
/// `IDisposable`.
let create (config: LogStoreConfig) : ILogStore = new SqliteLogStore(config) :> ILogStore

// ─── retention sweep ────────────────────────────────────────────────────

/// How often the retention sweep runs. Hourly: retention is a bound, not a
/// deadline, and an hourly pass keeps the sweep's own cost invisible
/// beside a store taking log lines continuously.
let private sweepInterval = TimeSpan.FromHours 1.0

/// Phase 828 — the hosted service that keeps the log store inside its
/// declared retention bounds. Registered only when
/// `ServerConfig.LogStore` opts in, so a deployment on `NoLogStore` runs
/// no timer at all (GP 13).
///
/// Generic over `ILogStore` rather than over `SqliteLogStore`: the age
/// bound is the interface's own `Prune`, and the row cap is the optional
/// `ILogStoreRetention` capability, skipped when the composed store does
/// not offer one. It lives beside the default store because that is the
/// store whose bounds it was written for; a companion inherits it for
/// free.
type LogStoreRetentionService(store: ILogStore, config: LogStoreConfig, logger: ILogger) =
    inherit BackgroundService()

    /// Run one sweep — the age bound then the row cap — and report how
    /// many entries each leg deleted. Exposed so the contract tests can
    /// drive a sweep without waiting an hour for the timer.
    member _.SweepOnce() = async {
        let! byAge =
            if config.MaxAgeDays <= 0 then
                async { return 0 }
            else
                store.Prune(DateTime.UtcNow.AddDays(-(float config.MaxAgeDays)))

        let! byRows =
            match box store with
            | :? ILogStoreRetention as retention when config.MaxRows > 0L -> retention.TrimToMaxRows config.MaxRows
            | _ -> async { return 0 }

        return byAge, byRows
    }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    do! Task.Delay(sweepInterval, stoppingToken)

                    let! byAge, byRows = this.SweepOnce() |> Async.StartAsTask

                    if byAge > 0 || byRows > 0 then
                        logger.Info $"[LogStore] event=retention_sweep by_age={byAge} by_rows={byRows}"
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error("[LogStore] event=retention_sweep_error", Some ex)
        }
        :> Task