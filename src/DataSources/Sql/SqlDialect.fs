// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Sql

open System
open System.Text

open ToolUp.Platform
open ToolUp.Platform.Infra
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

    // ─── Identifier safety ────────────────────────────────────────
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

    // ─── Catalogue queries ────────────────────────────────────────
    //
    // The schema and table names a catalogue query compares against are
    // VALUES, so they are bound (`@schema`, `@table`) and never rendered
    // into the text; `SqlBinding.translate` rewrites the markers to each
    // backend's form. The identifier check stays in front of them, so a name
    // the catalogue could never hold is refused before any connection opens.

    let private bindText (name: string) (value: string) (statement: BoundStatement) =
        BoundStatement.bind name (BoundValue.Text value) statement

    let private noSchemaCatalogue (schema: string) =
        Error(SchemaMismatch $"sqlite has no schema catalogue; remove ConnectionScope 'schema' ('%s{schema}')")

    /// The statement listing table names visible to the connection, scoped
    /// to `schema` (bound as `@schema`) when the backend supports schema
    /// scoping.
    let listTablesSql (backend: SqlBackend) (schema: string option) : Result<BoundStatement, IngestionError> =
        let ansi (systemExclusion: string) =
            match schema with
            | Some s ->
                requireIdentifier "schema" s
                |> Result.map (fun s ->
                    BoundStatement.ofText
                        "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema ORDER BY table_name"
                    |> bindText "schema" s)
            | None ->
                Ok(
                    BoundStatement.ofText
                        $"SELECT table_name FROM information_schema.tables WHERE %s{systemExclusion} ORDER BY table_name"
                )

        match backend with
        | PostgreSql -> ansi "table_schema NOT IN ('pg_catalog', 'information_schema')"
        | SqlServer -> ansi "table_schema NOT IN ('sys', 'INFORMATION_SCHEMA')"
        | MySql -> ansi "table_schema = DATABASE()"
        | ClickHouse -> ansi "table_schema NOT IN ('system', 'INFORMATION_SCHEMA', 'information_schema')"
        | Oracle ->
            match schema with
            | Some s ->
                requireIdentifier "schema" s
                |> Result.map (fun s ->
                    BoundStatement.ofText "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = @schema ORDER BY TABLE_NAME"
                    |> bindText "schema" (s.ToUpperInvariant()))
            | None ->
                Ok(BoundStatement.ofText "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = USER ORDER BY TABLE_NAME")
        | Sqlite ->
            // SQLite has no schema catalogue; a declared `schema` is
            // an operator misconfiguration worth naming rather than
            // silently ignoring.
            match schema with
            | Some s -> noSchemaCatalogue s
            | None ->
                Ok(
                    BoundStatement.ofText
                        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
                )

    /// The statement describing one table's columns (bound as `@table`), in
    /// ordinal order. Every shape returns exactly three columns: name,
    /// native type, and the backend's nullability token (interpreted by
    /// `nullableFromToken`).
    let columnsSql
        (backend: SqlBackend)
        (schema: string option)
        (table: string)
        : Result<BoundStatement, IngestionError> =
        requireIdentifier "table" table
        |> Result.bind (fun table ->
            let ansi (systemExclusion: string) =
                match schema with
                | Some s ->
                    requireIdentifier "schema" s
                    |> Result.map (fun s ->
                        BoundStatement.ofText (
                            "SELECT column_name, data_type, is_nullable FROM information_schema.columns "
                            + "WHERE table_schema = @schema AND table_name = @table ORDER BY ordinal_position"
                        )
                        |> bindText "schema" s
                        |> bindText "table" table)
                | None ->
                    Ok(
                        BoundStatement.ofText (
                            "SELECT column_name, data_type, is_nullable FROM information_schema.columns "
                            + $"WHERE table_name = @table AND %s{systemExclusion} ORDER BY ordinal_position"
                        )
                        |> bindText "table" table
                    )

            match backend with
            | PostgreSql -> ansi "table_schema NOT IN ('pg_catalog', 'information_schema')"
            | SqlServer -> ansi "table_schema NOT IN ('sys', 'INFORMATION_SCHEMA')"
            | MySql -> ansi "table_schema = DATABASE()"
            | ClickHouse -> ansi "table_schema NOT IN ('system', 'INFORMATION_SCHEMA', 'information_schema')"
            | Oracle ->
                // Oracle folds unquoted identifiers to upper case and stores
                // them that way, so the compared values are upper-cased.
                let upperTable = table.ToUpperInvariant()

                match schema with
                | Some s ->
                    requireIdentifier "schema" s
                    |> Result.map (fun s ->
                        BoundStatement.ofText (
                            "SELECT COLUMN_NAME, DATA_TYPE, NULLABLE FROM ALL_TAB_COLUMNS "
                            + "WHERE OWNER = @schema AND TABLE_NAME = @table ORDER BY COLUMN_ID"
                        )
                        |> bindText "schema" (s.ToUpperInvariant())
                        |> bindText "table" upperTable)
                | None ->
                    Ok(
                        BoundStatement.ofText (
                            "SELECT COLUMN_NAME, DATA_TYPE, NULLABLE FROM ALL_TAB_COLUMNS "
                            + "WHERE OWNER = USER AND TABLE_NAME = @table ORDER BY COLUMN_ID"
                        )
                        |> bindText "table" upperTable
                    )
            | Sqlite ->
                match schema with
                | Some s -> noSchemaCatalogue s
                | None ->
                    Ok(
                        BoundStatement.ofText
                            "SELECT name, type, \"notnull\" FROM pragma_table_info(@table) ORDER BY cid"
                        |> bindText "table" table
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
// ─── Bound statements, per backend (pure) ─────────────────────────
//
// A bound statement names each parameter `@name`; each backend's provider
// takes its values another way. `SqlBinding.translate` rewrites the markers
// to the backend's form (named `@name`, positional `$n`, prefixed `:bN`, or
// ClickHouse's typed `{name:Type}`) and lists the values in the order the
// provider binds them, so a value never becomes part of the text.
//
// **The scanner reads the text the way the backend will.** A marker inside a
// string literal, a quoted identifier or a comment is left alone, and so is
// a statement separator. Where the backend's own reading of a quoted run
// depends on a session setting (a backslash inside a quote on MySQL, under
// `sql_mode`, or on PostgreSQL outside an `E'...'` string, under
// `standard_conforming_strings`), the scanner cannot know which reading
// applies, so the statement is refused rather than guessed at. So is a
// separator followed by another statement: the bound path runs one
// statement per call. And so is a marker in the backend's own syntax (`$1`,
// `:name`, `?`), which would bind outside the statement's parameter list.

/// One piece of a scanned statement: text passed to the provider as it
/// stands, or a `@name` marker.
[<RequireQualifiedAccess>]
type StatementPiece =
    | Verbatim of text: string
    | Marker of name: string

/// One value as the provider binds it: the name the provider binds it
/// by (`None` for a positional marker, bound in list order) and the value.
type ProviderBinding = {
    Name: string option
    Value: BoundValue
}

/// A bound statement in one backend's form: the text the provider runs and
/// its values, each distinct parameter once, in marker order.
type BackendStatement = {
    Text: string
    Bindings: ProviderBinding list
}

module SqlBinding =

    /// The kinds each backend binds. MySQL does not bind the exact decimal:
    /// its `DECIMAL` holds at most 30 fractional digits, so some canonical
    /// decimals have no exact MySQL value, and a decimal bound as text is
    /// compared through a double. Oracle does not bind the boolean: before
    /// 23ai it has no SQL boolean type.
    let bindableKinds (backend: SqlBackend) : Set<DatasetDType> =
        let all = Set.ofList BoundValue.kinds

        match backend with
        | MySql -> all.Remove DatasetDType.Decimal
        | Oracle -> all.Remove DatasetDType.Bool
        | PostgreSql
        | SqlServer
        | Sqlite
        | ClickHouse -> all

    let private isNameStart (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c = '_'

    let private isNameChar (c: char) = isNameStart c || (c >= '0' && c <= '9')

    /// A character that continues an identifier, for the "does this `$` or
    /// `E` start a token" questions.
    let private isWordChar (c: char) = isNameChar c || c = '$' || c > '\u007f'

    /// Whether a backslash inside a quoted run is read one way only, whatever
    /// the session's settings.
    let private backslashFixed (backend: SqlBackend) =
        match backend with
        | MySql
        | PostgreSql -> false
        | SqlServer
        | Sqlite
        | Oracle
        | ClickHouse -> true

    /// Whether a backslash escapes the next character inside a quoted run.
    let private backslashEscapes (backend: SqlBackend) =
        match backend with
        | ClickHouse -> true
        | MySql
        | PostgreSql
        | SqlServer
        | Sqlite
        | Oracle -> false

    let private nestsBlockComments (backend: SqlBackend) =
        match backend with
        | PostgreSql
        | SqlServer -> true
        | MySql
        | Sqlite
        | Oracle
        | ClickHouse -> false

    /// Split `text` into verbatim runs and `@name` markers, reading literals,
    /// quoted identifiers and comments as `backend` reads them. The error
    /// names what was refused.
    let scan (backend: SqlBackend) (text: string) : Result<StatementPiece list, string> =
        let n = text.Length
        let pieces = ResizeArray<StatementPiece>()
        let run = StringBuilder()
        let mutable i = 0
        let mutable separated = false
        let mutable error: string option = None

        let at k = if k < n then text[k] else '\000'

        let flush () =
            if run.Length > 0 then
                pieces.Add(StatementPiece.Verbatim(run.ToString()))
                run.Clear() |> ignore

        let refuse (reason: string) =
            if error.IsNone then
                error <- Some reason

        // Any token but whitespace, a comment or a separator: after a
        // separator it is a second statement.
        let significant () =
            if separated then
                refuse "the text holds more than one statement; the bound path runs one statement per call"

        let copyTo (stop: int) =
            run.Append(text, i, stop - i) |> ignore
            i <- stop

        // A quoted run opened at `i`, closed by `close`, a doubled `close`
        // standing for itself. `escapes`: a backslash escapes the next char.
        let quoted (close: char) (doubled: bool) (escapes: bool) (what: string) =
            let mutable j = i + 1
            let mutable stop = -1
            let mutable ambiguous = false

            while stop < 0 && not ambiguous && j < n do
                let c = text[j]

                if c = '\\' && escapes then
                    j <- j + 2
                elif c = '\\' && not (backslashFixed backend) then
                    refuse (
                        $"a backslash inside a %s{what} is read two ways by %s{SqlDialect.backendName backend}, "
                        + "depending on a session setting; bind the value instead"
                    )

                    ambiguous <- true
                elif c = close then
                    if doubled && at (j + 1) = close then
                        j <- j + 2
                    else
                        stop <- j + 1
                else
                    j <- j + 1

            if stop >= 0 then
                copyTo stop
            elif not ambiguous then
                refuse $"the text ends inside a %s{what}"

        let lineComment () =
            let stop = text.IndexOf('\n', i)
            copyTo (if stop < 0 then n else stop)

        let blockComment () =
            let nests = nestsBlockComments backend
            let mutable depth = 1
            let mutable j = i + 2

            while depth > 0 && j < n do
                if text[j] = '*' && at (j + 1) = '/' then
                    depth <- depth - 1
                    j <- j + 2
                elif nests && text[j] = '/' && at (j + 1) = '*' then
                    depth <- depth + 1
                    j <- j + 2
                else
                    j <- j + 1

            if depth > 0 then
                refuse "the text ends inside a comment"
            else
                copyTo j

        // `$tag$ ... $tag$`, where the tag is empty or a name. `None` when
        // the `$` at `i` does not open one.
        let dollarTag () =
            if i > 0 && isWordChar text[i - 1] then
                None
            else
                let mutable j = i + 1

                if isNameStart (at j) then
                    j <- j + 1

                    while isNameChar (at j) do
                        j <- j + 1

                if at j = '$' then
                    Some(text.Substring(i, j - i + 1))
                else
                    None

        let isLineCommentStart () =
            match backend with
            // MySQL reads `--` as a comment only before whitespace or a
            // control character; `1--1` is arithmetic.
            | MySql ->
                let c = at (i + 2)
                Char.IsWhiteSpace c || Char.IsControl c
            | PostgreSql
            | SqlServer
            | Sqlite
            | Oracle
            | ClickHouse -> true

        let hashComments =
            match backend with
            | MySql
            | ClickHouse -> true
            | PostgreSql
            | SqlServer
            | Sqlite
            | Oracle -> false

        while error.IsNone && i < n do
            let c = text[i]
            let next = at (i + 1)

            if Char.IsWhiteSpace c then
                copyTo (i + 1)
            elif c = ';' then
                separated <- true
                copyTo (i + 1)
            elif c = '-' && next = '-' && isLineCommentStart () then
                lineComment ()
            elif c = '#' && hashComments then
                lineComment ()
            // MySQL runs the body of `/*! ... */` as code, so it is scanned
            // as code; its delimiters pass through as text.
            elif c = '/' && next = '*' && not (backend = MySql && at (i + 2) = '!') then
                blockComment ()
            else
                significant ()

                match c with
                | '\'' ->
                    // PostgreSQL's `E'...'` always reads a backslash as an escape.
                    let escapeString =
                        backend = PostgreSql
                        && i > 0
                        && (text[i - 1] = 'E' || text[i - 1] = 'e')
                        && (i < 2 || not (isWordChar text[i - 2]))

                    quoted '\'' true (escapeString || backslashEscapes backend) "string literal"
                | '"' -> quoted '"' true (backslashEscapes backend) "quoted identifier"
                | '`' when backend = MySql || backend = ClickHouse || backend = Sqlite ->
                    quoted '`' true (backslashEscapes backend) "quoted identifier"
                | '[' when backend = SqlServer || backend = Sqlite ->
                    quoted ']' (backend = SqlServer) false "quoted identifier"
                | '$' when backend = PostgreSql && Char.IsDigit next ->
                    refuse "the text uses a positional marker ('$n'); name every parameter as @name"
                | '$' when backend = Sqlite && isNameStart next ->
                    refuse "the text uses a '$name' marker; name every parameter as @name"
                | '$' when backend = PostgreSql || backend = ClickHouse ->
                    match dollarTag () with
                    | Some tag ->
                        let close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal)

                        if close < 0 then
                            refuse "the text ends inside a dollar-quoted string"
                        else
                            copyTo (close + tag.Length)
                    | None -> copyTo (i + 1)
                | ':' when (backend = Oracle || backend = Sqlite) && isNameStart next ->
                    refuse "the text uses a ':name' marker; name every parameter as @name"
                | '?' when backend = MySql || backend = Sqlite ->
                    refuse "the text uses a positional marker ('?'); name every parameter as @name"
                | '@' when next = '@' ->
                    // `@@name` is a system variable, never a marker.
                    let mutable j = i + 2

                    while isNameChar (at j) do
                        j <- j + 1

                    copyTo j
                | '@' when isNameStart next ->
                    let mutable j = i + 1

                    while isNameChar (at j) do
                        j <- j + 1

                    flush ()
                    pieces.Add(StatementPiece.Marker(text.Substring(i + 1, j - i - 1)))
                    i <- j
                | _ -> copyTo (i + 1)

        match error with
        | Some reason -> Error reason
        | None ->
            flush ()
            Ok(List.ofSeq pieces)

    /// Digits before the point (none for a lone `0`), and after it, of a
    /// canonical decimal.
    let private decimalShape (digits: string) =
        let body = digits.TrimStart '-'

        let integerPart, fractionPart =
            match body.IndexOf '.' with
            | -1 -> body, ""
            | point -> body.Substring(0, point), body.Substring(point + 1)

        let integerDigits = if integerPart = "0" then 0 else integerPart.Length
        integerDigits, fractionPart

    /// What `backend` holds exactly, when the canonical decimal `digits` is
    /// beyond it; `None` when it can carry them.
    let private beyondCapacity (backend: SqlBackend) (digits: string) : string option =
        let integerDigits, fraction = decimalShape digits
        let scale = fraction.Length
        let precision = max 1 (integerDigits + scale)

        // Significant digits: a fraction below one drops its leading zeros.
        let significant =
            if integerDigits > 0 then
                precision
            else
                max 1 (fraction.TrimStart('0').Length)

        match backend with
        | SqlServer when precision > 38 -> Some "DECIMAL(38, s), at most 38 digits"
        | Oracle when significant > 38 || scale > 127 -> Some "NUMBER, at most 38 significant digits"
        | ClickHouse when precision > 76 -> Some "Decimal(76, s), at most 76 digits"
        | PostgreSql when integerDigits > 131072 || scale > 16383 ->
            Some "numeric, at most 131072 digits before the point and 16383 after"
        | SqlServer
        | Oracle
        | ClickHouse
        | PostgreSql
        | Sqlite
        | MySql -> None

    /// The ClickHouse type a value is bound as, which its marker names.
    let clickHouseType (value: BoundValue) : string =
        let ofKind (kind: DatasetDType) =
            match kind with
            | DatasetDType.Float -> "Float64"
            | DatasetDType.Int -> "Int64"
            | DatasetDType.Bool -> "Bool"
            | DatasetDType.Text
            | DatasetDType.Categorical -> "String"
            | DatasetDType.Timestamp -> "DateTime64(7, 'UTC')"
            | DatasetDType.Decimal -> "Decimal(76, 38)"

        match value with
        | BoundValue.Decimal digits ->
            let integerDigits, fraction = decimalShape digits
            $"Decimal(%d{max 1 (integerDigits + fraction.Length)}, %d{fraction.Length})"
        | BoundValue.Null kind -> $"Nullable(%s{ofKind kind})"
        | BoundValue.Float _
        | BoundValue.Int _
        | BoundValue.Bool _
        | BoundValue.Text _
        | BoundValue.Timestamp _
        | BoundValue.Categorical _ -> ofKind (BoundValue.kind value)

    /// The marker for the parameter `name`, the `ordinal`-th distinct one in
    /// the text, in `backend`'s form.
    let private markerFor (backend: SqlBackend) (ordinal: int) (name: string) (value: BoundValue) =
        match backend with
        | SqlServer
        | MySql
        | Sqlite -> "@" + name
        // PostgreSQL takes the exact decimal as its text and casts it, so the
        // digits never pass through a host decimal.
        | PostgreSql when BoundValue.kind value = DatasetDType.Decimal -> $"CAST($%d{ordinal} AS numeric)"
        | PostgreSql -> $"$%d{ordinal}"
        | Oracle -> $":b%d{ordinal}"
        | ClickHouse -> $"{{%s{name}:%s{clickHouseType value}}}"

    let private providerName (backend: SqlBackend) (ordinal: int) (name: string) =
        match backend with
        | PostgreSql -> None
        | SqlServer
        | MySql
        | Sqlite -> Some("@" + name)
        | Oracle -> Some $"b%d{ordinal}"
        | ClickHouse -> Some name

    let private refusedText (backend: SqlBackend) (reason: string) =
        BoundStatementError.Failed(
            SchemaMismatch $"%s{SqlDialect.backendName backend} refused the bound statement: %s{reason}"
        )

    /// `statement` in `backend`'s form. Refuses a text the scanner refuses,
    /// a marker no parameter supplies, a parameter the text does not use, and
    /// a decimal the backend cannot carry exactly. The kinds are
    /// `BoundStatement.check`'s, against `bindableKinds`.
    let translate (backend: SqlBackend) (statement: BoundStatement) : Result<BackendStatement, BoundStatementError> =
        match scan backend statement.Text with
        | Error reason -> Error(refusedText backend reason)
        | Ok pieces ->
            let used =
                pieces
                |> List.choose (fun piece ->
                    match piece with
                    | StatementPiece.Marker name -> Some name
                    | StatementPiece.Verbatim _ -> None)
                |> List.distinct

            let supplied =
                statement.Parameters |> List.map (fun p -> p.Name, p.Value) |> Map.ofList

            let beyond =
                statement.Parameters
                |> List.tryPick (fun p ->
                    match p.Value with
                    | BoundValue.Decimal digits ->
                        beyondCapacity backend digits |> Option.map (fun limit -> p.Name, limit)
                    | _ -> None)

            match used |> List.tryFind (supplied.ContainsKey >> not) with
            | Some missing -> Error(BoundStatementError.Refused(BindingRefusal.MissingParameter missing))
            | None ->
                match statement.Parameters |> List.tryFind (fun p -> not (List.contains p.Name used)) with
                | Some unused -> Error(BoundStatementError.Refused(BindingRefusal.UnusedParameter unused.Name))
                | None ->
                    match beyond with
                    | Some(name, limit) ->
                        Error(refusedText backend $"parameter '%s{name}' is a decimal beyond %s{limit}")
                    | None ->
                        let ordinal name = List.findIndex ((=) name) used + 1

                        let text =
                            pieces
                            |> List.map (fun piece ->
                                match piece with
                                | StatementPiece.Verbatim run -> run
                                | StatementPiece.Marker name -> markerFor backend (ordinal name) name supplied[name])
                            |> String.concat ""

                        Ok {
                            Text = text
                            Bindings =
                                used
                                |> List.mapi (fun index name -> {
                                    Name = providerName backend (index + 1) name
                                    Value = supplied[name]
                                })
                        }