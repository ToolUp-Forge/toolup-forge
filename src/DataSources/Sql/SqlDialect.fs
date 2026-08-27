// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Sql

open System

open ToolUp.Platform
open ToolUp.DataSources.Common
open DataManagementTypes

// ─── Sql connector dialects (pure) ────────────────────────────────
//
// Everything in this file is a total function over strings. It has no
// database dependency, so the whole introspection surface — which
// catalogue a backend exposes, how it spells "nullable", how its
// native type names classify — is unit-testable without a server, and
// IS unit-tested in `ToolUp.DataSources.Tests`.
//
// **Why hand-written catalogue SQL rather than a query abstraction.**
// See the linq2db decision in this companion's README. In short: six
// backends collapse to THREE catalogue shapes (ANSI
// `information_schema`, Oracle's `ALL_*` views, SQLite's
// `sqlite_master` + `pragma_table_info`), and three pure string
// builders are cheaper to own, test offline, and reason about than a
// runtime provider registry we would consult for nothing else.

/// The operational backends this one companion spans, selected per
/// data source by the `backend` key in
/// `DataSourceConfig.ConnectionScope`.
type SqlBackend =
    | PostgreSql
    | MySql
    | SqlServer
    | Sqlite
    | Oracle
    | ClickHouse

module SqlDialect =

    /// Accepted `backend` values, in the spelling an operator writes
    /// into `ConnectionScope`. Aliases are deliberate: the same
    /// engine is called different things by different teams, and a
    /// connector that refuses `postgresql` because it wanted
    /// `postgres` is a support ticket, not a safety feature.
    let private aliases = [
        "postgres", PostgreSql
        "postgresql", PostgreSql
        "npgsql", PostgreSql
        "redshift-wire", PostgreSql
        "mysql", MySql
        "mariadb", MySql
        "sqlserver", SqlServer
        "mssql", SqlServer
        "azuresql", SqlServer
        "sqlite", Sqlite
        "oracle", Oracle
        "clickhouse", ClickHouse
    ]

    /// Every accepted `backend` spelling, for error messages and docs.
    let acceptedBackends = aliases |> List.map fst

    /// Canonical, stable name for a backend — used in diagnostics and
    /// in the `Sql` connector's error messages.
    let backendName (backend: SqlBackend) : string =
        match backend with
        | PostgreSql -> "postgres"
        | MySql -> "mysql"
        | SqlServer -> "sqlserver"
        | Sqlite -> "sqlite"
        | Oracle -> "oracle"
        | ClickHouse -> "clickhouse"

    /// Parse the `backend` ConnectionScope value. The failure names
    /// every accepted spelling.
    let parseBackend (raw: string) : Result<SqlBackend, IngestionError> =
        let normalised = (if isNull raw then "" else raw).Trim().ToLowerInvariant()

        match aliases |> List.tryFind (fun (alias, _) -> alias = normalised) with
        | Some(_, backend) -> Ok backend
        | None ->
            let accepted = String.Join(", ", acceptedBackends)
            Error(SchemaMismatch $"ConnectionScope key 'backend' must be one of [%s{accepted}]; got '%s{raw}'")

    /// The schema the connector introspects when `ConnectionScope`
    /// declares none. `None` means "the backend has no useful default
    /// and the catalogue query self-scopes" — MySQL to `DATABASE()`,
    /// Oracle to `USER`, SQLite to the attached file, ClickHouse and
    /// SQL Server to everything outside their system catalogues.
    let defaultSchema (backend: SqlBackend) : string option =
        match backend with
        | PostgreSql -> Some "public"
        | SqlServer -> Some "dbo"
        | MySql
        | Sqlite
        | Oracle
        | ClickHouse -> None

    // ─── Identifier + literal safety ──────────────────────────────
    //
    // Delegated to `ToolUp.DataSources.Common.SqlIdentifier`, which
    // carries the rationale and is shared with the Synapse and
    // Snowflake connectors so all three enforce one rule. Re-exported
    // here because a reader of this dialect file should not have to
    // follow a package boundary to see what "safe" means.

    /// Does this string look like a plain, unqualified SQL identifier?
    let isSafeIdentifier (value: string) : bool = SqlIdentifier.isSafe value

    /// Validate an identifier destined for interpolation.
    let requireIdentifier (label: string) (value: string) : Result<string, IngestionError> =
        SqlIdentifier.require label value

    /// Double single quotes so a validated identifier can be
    /// interpolated into a catalogue query's string literal.
    let quoteLiteral (value: string) : string = SqlIdentifier.quoteLiteral value

    // ─── Catalogue queries ────────────────────────────────────────

    /// SQL listing table names visible to the connection, scoped to
    /// `schema` when the backend supports schema scoping.
    let listTablesSql (backend: SqlBackend) (schema: string option) : Result<string, IngestionError> =
        let ansi (schemaColumn: string) (systemExclusion: string) =
            match schema with
            | Some s ->
                requireIdentifier "schema" s
                |> Result.map (fun s ->
                    $"SELECT table_name FROM information_schema.tables WHERE %s{schemaColumn} = '%s{quoteLiteral s}' ORDER BY table_name")
            | None ->
                Ok $"SELECT table_name FROM information_schema.tables WHERE %s{systemExclusion} ORDER BY table_name"

        match backend with
        | PostgreSql -> ansi "table_schema" "table_schema NOT IN ('pg_catalog', 'information_schema')"
        | SqlServer -> ansi "table_schema" "table_schema NOT IN ('sys', 'INFORMATION_SCHEMA')"
        | MySql -> ansi "table_schema" "table_schema = DATABASE()"
        | ClickHouse -> ansi "table_schema" "table_schema NOT IN ('system', 'INFORMATION_SCHEMA', 'information_schema')"
        | Oracle ->
            match schema with
            | Some s ->
                requireIdentifier "schema" s
                |> Result.map (fun s ->
                    let owner = (quoteLiteral s).ToUpperInvariant()
                    $"SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = '%s{owner}' ORDER BY TABLE_NAME")
            | None -> Ok "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = USER ORDER BY TABLE_NAME"
        | Sqlite ->
            // SQLite has no schema catalogue; a declared `schema` is
            // an operator misconfiguration worth naming rather than
            // silently ignoring.
            match schema with
            | Some s ->
                Error(SchemaMismatch $"sqlite has no schema catalogue; remove ConnectionScope 'schema' ('%s{s}')")
            | None ->
                Ok "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"

    /// SQL describing one table's columns, in ordinal order. Every
    /// shape returns exactly three columns: name, native type, and
    /// the backend's nullability token (interpreted by
    /// `nullableFromToken`).
    let columnsSql (backend: SqlBackend) (schema: string option) (table: string) : Result<string, IngestionError> =
        requireIdentifier "table" table
        |> Result.bind (fun table ->
            let tableLiteral = quoteLiteral table

            let ansi (systemExclusion: string) =
                match schema with
                | Some s ->
                    requireIdentifier "schema" s
                    |> Result.map (fun s ->
                        $"SELECT column_name, data_type, is_nullable FROM information_schema.columns "
                        + $"WHERE table_schema = '%s{quoteLiteral s}' AND table_name = '%s{tableLiteral}' ORDER BY ordinal_position")
                | None ->
                    Ok(
                        $"SELECT column_name, data_type, is_nullable FROM information_schema.columns "
                        + $"WHERE table_name = '%s{tableLiteral}' AND %s{systemExclusion} ORDER BY ordinal_position"
                    )

            match backend with
            | PostgreSql -> ansi "table_schema NOT IN ('pg_catalog', 'information_schema')"
            | SqlServer -> ansi "table_schema NOT IN ('sys', 'INFORMATION_SCHEMA')"
            | MySql -> ansi "table_schema = DATABASE()"
            | ClickHouse -> ansi "table_schema NOT IN ('system', 'INFORMATION_SCHEMA', 'information_schema')"
            | Oracle ->
                let upperTable = tableLiteral.ToUpperInvariant()

                match schema with
                | Some s ->
                    requireIdentifier "schema" s
                    |> Result.map (fun s ->
                        let owner = (quoteLiteral s).ToUpperInvariant()

                        $"SELECT COLUMN_NAME, DATA_TYPE, NULLABLE FROM ALL_TAB_COLUMNS "
                        + $"WHERE OWNER = '%s{owner}' AND TABLE_NAME = '%s{upperTable}' ORDER BY COLUMN_ID")
                | None ->
                    Ok(
                        $"SELECT COLUMN_NAME, DATA_TYPE, NULLABLE FROM ALL_TAB_COLUMNS "
                        + $"WHERE OWNER = USER AND TABLE_NAME = '%s{upperTable}' ORDER BY COLUMN_ID"
                    )
            | Sqlite ->
                match schema with
                | Some s ->
                    Error(SchemaMismatch $"sqlite has no schema catalogue; remove ConnectionScope 'schema' ('%s{s}')")
                | None ->
                    Ok(
                        "SELECT name, type, \"notnull\" FROM pragma_table_info('"
                        + tableLiteral
                        + "') ORDER BY cid"
                    ))

    /// SQL selecting every row of a table — the expansion applied when
    /// `IDataSource.Query` is handed a bare identifier rather than a
    /// statement. Documented per-connector convenience so an admin UI
    /// can offer "ingest this whole table" without composing SQL.
    let selectAllSql (backend: SqlBackend) (schema: string option) (table: string) : Result<string, IngestionError> =
        requireIdentifier "table" table
        |> Result.bind (fun table ->
            let qualify (quoteOpen: string) (quoteClose: string) =
                match schema with
                | Some s ->
                    requireIdentifier "schema" s
                    |> Result.map (fun s ->
                        $"SELECT * FROM %s{quoteOpen}%s{s}%s{quoteClose}.%s{quoteOpen}%s{table}%s{quoteClose}")
                | None -> Ok $"SELECT * FROM %s{quoteOpen}%s{table}%s{quoteClose}"

            match backend with
            | PostgreSql
            | Oracle
            | Sqlite -> qualify "\"" "\""
            | SqlServer -> qualify "[" "]"
            | MySql
            | ClickHouse -> qualify "`" "`")

    /// Interpret a backend's nullability token. ANSI returns
    /// `YES`/`NO`; Oracle `Y`/`N`; ClickHouse's `information_schema`
    /// returns a `UInt8` `0`/`1`; SQLite's `pragma_table_info` returns
    /// `notnull`, whose sense is INVERTED — `1` means NOT nullable.
    let nullableFromToken (backend: SqlBackend) (token: string) : bool =
        let t = (if isNull token then "" else token).Trim().ToLowerInvariant()

        match backend with
        | Sqlite -> not (t = "1" || t = "true")
        | PostgreSql
        | MySql
        | SqlServer
        | ClickHouse
        | Oracle ->
            match t with
            | "yes"
            | "y"
            | "1"
            | "true" -> true
            | _ -> false

    // ─── Native type → coarse ColumnType ──────────────────────────

    /// Per-backend overrides in front of `TypeMap.ansi`. Only the
    /// spellings the ANSI table would classify WRONGLY appear here —
    /// everything a backend spells conventionally falls through.
    let private overridesFor (backend: SqlBackend) (normalised: string) : ColumnType option =
        match backend with
        | PostgreSql ->
            match normalised with
            // `interval` contains no ANSI date token; `oid`/`xid` are
            // integers whose names contain none of the numeric tokens.
            | "interval" -> Some DateColumn
            | "oid"
            | "xid"
            | "cid" -> Some NumberColumn
            | "json"
            | "jsonb"
            | "uuid"
            | "bytea"
            | "inet"
            | "cidr"
            | "macaddr" -> Some StringColumn
            | _ -> None
        | MySql ->
            match normalised with
            // MySQL spells booleans `tinyint(1)`, which normalises to
            // `tinyint` and would otherwise read as a number — but a
            // plain `tinyint` IS a number, so only the year type and
            // the blob family need correcting here.
            | "year" -> Some DateColumn
            | "json"
            | "blob"
            | "tinyblob"
            | "mediumblob"
            | "longblob"
            | "binary"
            | "varbinary" -> Some StringColumn
            | _ -> None
        | SqlServer ->
            match normalised with
            | "uniqueidentifier"
            | "xml"
            | "binary"
            | "varbinary"
            | "image"
            | "hierarchyid"
            | "sql_variant" -> Some StringColumn
            | "rowversion"
            | "timestamp" ->
                // T-SQL `timestamp` is a row version, NOT a date —
                // the single most misleading type name in the family.
                Some StringColumn
            | _ -> None
        | Sqlite ->
            match normalised with
            // SQLite's storage classes are affinities, and `blob` and
            // a declared-but-unknown type both behave as opaque text.
            | "blob" -> Some StringColumn
            | _ -> None
        | Oracle ->
            match normalised with
            | "raw"
            | "long raw"
            | "blob"
            | "clob"
            | "nclob"
            | "rowid"
            | "urowid"
            | "xmltype" -> Some StringColumn
            | _ -> None
        | ClickHouse ->
            match normalised with
            | "uuid"
            | "ipv4"
            | "ipv6"
            | "fixedstring" -> Some StringColumn
            | "enum8"
            | "enum16" -> Some StringColumn
            | _ -> None

    /// ClickHouse type names are WRAPPED — `Nullable(DateTime64(3))`,
    /// `LowCardinality(Nullable(String))`, `Array(UInt8)`. Stripping at
    /// the first `(` (which is what the shared `TypeMap.normalise`
    /// does) would classify every nullable column as the wrapper name,
    /// so peel the transparent wrappers first. `Array(...)` is NOT
    /// peeled: an array of numbers is not a number, and renders in CSV
    /// as text.
    let private unwrapClickHouse (nativeType: string) : string =
        let transparent = [ "nullable"; "lowcardinality"; "simpleaggregatefunction" ]

        let rec peel (value: string) (fuel: int) =
            if fuel <= 0 then
                value
            else
                let trimmed = value.Trim()
                let openParen = trimmed.IndexOf '('

                if openParen > 0 && trimmed.EndsWith(")", StringComparison.Ordinal) then
                    let head = trimmed.Substring(0, openParen).Trim().ToLowerInvariant()

                    if transparent |> List.contains head then
                        let inner = trimmed.Substring(openParen + 1, trimmed.Length - openParen - 2)
                        // SimpleAggregateFunction(sum, UInt64) — the
                        // TYPE is the last argument, not the first.
                        let candidate =
                            if head = "simpleaggregatefunction" then
                                match inner.LastIndexOf ',' with
                                | -1 -> inner
                                | i -> inner.Substring(i + 1)
                            else
                                inner

                        peel candidate (fuel - 1)
                    else
                        trimmed
                else
                    trimmed

        peel (if isNull nativeType then "" else nativeType) 8

    /// Classify a backend's native type name down to the SDK's coarse
    /// four-case `ColumnType`. The RAW name is what the connector
    /// stores on `ColumnInfo.DataType`; this is the projection for
    /// consumers that need to reason uniformly.
    let toColumnType (backend: SqlBackend) (nativeType: string) : ColumnType =
        let subject =
            match backend with
            | ClickHouse -> unwrapClickHouse nativeType
            | PostgreSql
            | MySql
            | SqlServer
            | Sqlite
            | Oracle -> nativeType

        TypeMap.classify (overridesFor backend) subject