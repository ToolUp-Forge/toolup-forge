// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Synapse

open System
open System.Data
open System.Data.Common
open System.Data.SqlTypes
open Azure.Core
open Azure.Identity
open Microsoft.Data.SqlClient
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.Common
open DataManagementTypes
open ToolUp.Platform.ProviderExceptions
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose

// ─── ToolUp.DataSources.Synapse ───────────────────────────────────
//
// `IDataSource` companion for Azure Synapse Analytics, over the
// Synapse SQL endpoint (serverless or dedicated) using
// `Microsoft.Data.SqlClient`.
//
// **Production-ready, not dev-only.** Stateless between calls: every
// method opens a connection, uses it, and disposes it (SqlClient
// pools underneath), and the credential is re-read through the
// `ISecretStore` thunk on EVERY call so a rotated password or a
// re-minted AAD token takes effect without reconstructing the
// connector (portability rule 4).
//
// **Three authentication modes**, chosen by `auth` in
// `ConnectionScope`:
//   • `sql` (default) — SQL login. `user` in `ConnectionScope`, the
//     password through `ISecretStore`.
//   • `aad-token` — the `ISecretStore` credential IS an AAD access
//     token for `https://database.windows.net/`. The deployment mints
//     and rotates it; the connector just presents it.
//   • `aad-default` — `DefaultAzureCredential` mints a token per
//     call (managed identity in Azure, developer credentials
//     locally). No stored secret at all, which is the mode a managed
//     -identity deployment wants.
//
// **Query output is RFC 4180 CSV** with a header row.
//
// **It binds what it is given** (`ConnectorCapabilities.Binds`): a bound
// statement's `@name` markers are T-SQL's own, so its text runs as written
// and each value travels beside it as a typed `SqlParameter`
// (`SynapseBinding`), never rendered into the text.

/// How the connector authenticates to the Synapse SQL endpoint.
type SynapseAuth =
    /// SQL login: `user` from `ConnectionScope`, password from
    /// `ISecretStore`.
    | SqlLogin
    /// The `ISecretStore` credential is a ready-minted AAD access
    /// token.
    | AadToken
    /// `DefaultAzureCredential` mints a token per call.
    | AadDefault

/// Parsed, validated view of one Synapse source's `ConnectionScope`.
type SynapseSourceSettings = {
    /// Fully-qualified SQL endpoint, e.g.
    /// `myws-ondemand.sql.azuresynapse.net`.
    Server: string
    /// Database (a serverless SQL pool database, or a dedicated pool).
    Database: string
    /// TDS port. Defaults to 1433.
    Port: int
    /// Schema `ListTables` / `GetSchema` scope to. Defaults to `dbo`.
    Schema: string
    /// Authentication mode.
    Auth: SynapseAuth
    /// SQL login name. Required for `SqlLogin`, ignored otherwise.
    User: string option
    /// Connection-open timeout. Defaults to 30 s — Synapse
    /// SERVERLESS pools cold-start, so a shorter one is a false
    /// negative waiting to happen.
    ConnectTimeoutSeconds: int
    /// Per-command timeout. `None` leaves the provider default.
    CommandTimeoutSeconds: int option
}

/// The catalogue queries, as pure functions over strings — so the
/// whole introspection surface is unit-testable without a Synapse
/// workspace, and IS unit-tested in `ToolUp.DataSources.Tests`.
module SynapseCatalogue =

    /// T-SQL listing the base tables and views in `schema`.
    /// Synapse serverless surfaces external tables and views as
    /// `INFORMATION_SCHEMA` rows like any other, so one query covers
    /// dedicated pools, serverless views over the lake, and external
    /// tables alike.
    let listTablesSql (schema: string) : Result<string, IngestionError> =
        SqlIdentifier.require "schema" schema
        |> Result.map (fun schema ->
            $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '%s{SqlIdentifier.quoteLiteral schema}' ORDER BY TABLE_NAME")

    /// T-SQL describing one table's columns in ordinal order.
    let columnsSql (schema: string) (table: string) : Result<string, IngestionError> =
        SqlIdentifier.require "schema" schema
        |> Result.bind (fun schema ->
            SqlIdentifier.require "table" table
            |> Result.map (fun table ->
                $"SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS "
                + $"WHERE TABLE_SCHEMA = '%s{SqlIdentifier.quoteLiteral schema}' AND TABLE_NAME = '%s{SqlIdentifier.quoteLiteral table}' "
                + "ORDER BY ORDINAL_POSITION"))

    /// T-SQL describing a STATEMENT's first result set without
    /// running it. The statement is bound as the `@tsql` parameter, never
    /// interpolated; the result names each column's `name`,
    /// `system_type_name` and `is_nullable`.
    [<Literal>]
    let describeStatementSql = "EXEC sp_describe_first_result_set @tsql = @tsql"

    /// T-SQL selecting a whole table — the expansion applied when
    /// `Query` is handed a bare identifier.
    let selectAllSql (schema: string) (table: string) : Result<string, IngestionError> =
        SqlIdentifier.require "schema" schema
        |> Result.bind (fun schema ->
            SqlIdentifier.require "table" table
            |> Result.map (fun table -> $"SELECT * FROM [%s{schema}].[%s{table}]"))

    /// `INFORMATION_SCHEMA` reports nullability as `YES` / `NO`.
    let nullableFromToken (token: string) : bool =
        (if isNull token then "" else token).Trim().ToUpperInvariant() = "YES"

    /// Per-T-SQL-type overrides in front of the shared ANSI table.
    let private overrides (normalised: string) : ColumnType option =
        match normalised with
        | "bit" -> Some BooleanColumn
        | "uniqueidentifier"
        | "xml"
        | "binary"
        | "varbinary"
        | "image"
        | "hierarchyid"
        | "sql_variant" -> Some StringColumn
        // T-SQL `timestamp` is a ROW VERSION, not a date — the single
        // most misleading type name in the family, and the ANSI table
        // would classify it as `DateColumn`.
        | "rowversion"
        | "timestamp" -> Some StringColumn
        | _ -> None

    /// Classify a T-SQL native type name down to the SDK's coarse
    /// `ColumnType`. The RAW name is stored on `ColumnInfo.DataType`.
    let toColumnType (nativeType: string) : ColumnType = TypeMap.classify overrides nativeType

/// Binding a statement's values through SqlClient's own parameters. A bound
/// statement names each parameter `@name`, which is T-SQL's own marker, so
/// the text runs exactly as written and each value travels beside it as a
/// typed `SqlParameter`, which SqlClient sends through `sp_executesql`: a
/// value never becomes part of the text. Pure up to the command, so the
/// request the connector sends is unit-tested without a workspace.
module SynapseBinding =

    /// The kinds the connector binds: every kind a bound value can carry.
    /// A decimal binds exactly as `DECIMAL(p, s)`, up to 38 digits.
    let bindableKinds: Set<DatasetDType> = Set.ofList BoundValue.kinds

    let private isNameStart (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c = '_'

    let private isNameChar (c: char) = isNameStart c || (c >= '0' && c <= '9')

    /// The `@name` markers `text` uses, in text order, a repeat named again.
    /// The text is read as T-SQL reads it: a marker inside a string literal,
    /// a quoted identifier (`"..."`, `[...]`) or a comment (`--`, nesting
    /// `/* */`) is not a marker, and `@@name` is a system function. Refused,
    /// naming why: a text that ends inside a literal, an identifier or a
    /// comment, and a second statement after a `;`. T-SQL also runs a batch
    /// whose statements no separator divides, which no lexical reading sees;
    /// a bound value can never add one.
    let markers (text: string) : Result<string list, string> =
        let n = text.Length
        let found = ResizeArray<string>()
        let mutable i = 0
        let mutable separated = false
        let mutable error: string option = None

        let at k = if k < n then text[k] else ' '

        let nameEnd start =
            let mutable j = start

            while isNameChar (at j) do
                j <- j + 1

            j

        // A quoted run opened at `i`, closed by `close`, a doubled `close`
        // standing for itself.
        let quoted (close: char) (what: string) =
            let mutable j = i + 1
            let mutable stop = -1

            while stop < 0 && j < n do
                if text[j] <> close then j <- j + 1
                elif at (j + 1) = close then j <- j + 2
                else stop <- j + 1

            if stop < 0 then
                error <- Some $"the text ends inside a %s{what}"
            else
                i <- stop

        let blockComment () =
            let mutable depth = 1
            let mutable j = i + 2

            while depth > 0 && j < n do
                if text[j] = '*' && at (j + 1) = '/' then
                    depth <- depth - 1
                    j <- j + 2
                elif text[j] = '/' && at (j + 1) = '*' then
                    depth <- depth + 1
                    j <- j + 2
                else
                    j <- j + 1

            if depth > 0 then
                error <- Some "the text ends inside a comment"
            else
                i <- j

        while error.IsNone && i < n do
            let c = text[i]
            let next = at (i + 1)

            if Char.IsWhiteSpace c then
                i <- i + 1
            elif c = ';' then
                separated <- true
                i <- i + 1
            elif c = '-' && next = '-' then
                let stop = text.IndexOf("\n", i, StringComparison.Ordinal)
                i <- if stop < 0 then n else stop
            elif c = '/' && next = '*' then
                blockComment ()
            elif separated then
                error <- Some "the text holds more than one statement; the bound path runs one statement per call"
            else
                match c with
                | '\'' -> quoted '\'' "string literal"
                | '"' -> quoted '"' "quoted identifier"
                | '[' -> quoted ']' "quoted identifier"
                | '@' when next = '@' -> i <- nameEnd (i + 2)
                | '@' when isNameStart next ->
                    let stop = nameEnd (i + 1)
                    found.Add(text.Substring(i + 1, stop - i - 1))
                    i <- stop
                | _ -> i <- i + 1

        match error with
        | Some reason -> Error reason
        | None -> Ok(List.ofSeq found)

    /// The digits a canonical decimal holds, before the point (none for a
    /// lone `0`) and after it; at least one.
    let private precision (digits: string) =
        let body = digits.TrimStart '-'

        let integerPart, fractionDigits =
            match body.IndexOf '.' with
            | -1 -> body, 0
            | point -> body.Substring(0, point), body.Length - point - 1

        max 1 ((if integerPart = "0" then 0 else integerPart.Length) + fractionDigits)

    /// The provider parameter for `name`: its `SqlDbType` set from the
    /// value's kind, never inferred from a boxed object, and the exact
    /// decimal as a `SqlDecimal`, which holds 38 digits where
    /// `System.Decimal` holds 28.
    let private parameter (name: string) (value: BoundValue) : SqlParameter =
        let p = SqlParameter()
        p.ParameterName <- BoundStatement.marker name

        p.SqlDbType <-
            match BoundValue.kind value with
            | DatasetDType.Float -> SqlDbType.Float
            | DatasetDType.Int -> SqlDbType.BigInt
            | DatasetDType.Bool -> SqlDbType.Bit
            | DatasetDType.Text
            | DatasetDType.Categorical -> SqlDbType.NVarChar
            | DatasetDType.Timestamp -> SqlDbType.DateTimeOffset
            | DatasetDType.Decimal -> SqlDbType.Decimal

        match value with
        | BoundValue.Float f -> p.Value <- box f
        | BoundValue.Int i -> p.Value <- box i
        | BoundValue.Bool b -> p.Value <- box b
        | BoundValue.Text s
        | BoundValue.Categorical s -> p.Value <- box s
        | BoundValue.Timestamp t -> p.Value <- box t
        | BoundValue.Decimal digits ->
            let exact = SqlDecimal.Parse digits
            p.Precision <- exact.Precision
            p.Scale <- exact.Scale
            p.SqlValue <- exact
        | BoundValue.Null _ -> p.Value <- box DBNull.Value

        p

    let private refusedText (reason: string) =
        BoundStatementError.Failed(SchemaMismatch $"Synapse refused the bound statement: %s{reason}")

    /// The command `statement` runs as, before any connection: its text
    /// exactly as written, each distinct parameter once as a typed
    /// `SqlParameter` in marker order, and the statement's timeout. Refused
    /// before any request exists: what `BoundStatement.check` refuses against
    /// `bindableKinds`, a text `markers` refuses, a marker no parameter
    /// supplies, a parameter the text does not use, and a decimal beyond
    /// `DECIMAL(38, s)`. The caller disposes the command.
    let command (statement: BoundStatement) : Result<SqlCommand, BoundStatementError> =
        BoundStatement.check bindableKinds statement
        |> Result.mapError BoundStatementError.Refused
        |> Result.bind (fun () -> markers statement.Text |> Result.mapError refusedText)
        |> Result.bind (fun markers ->
            let used = List.distinct markers

            let supplied =
                statement.Parameters |> List.map (fun p -> p.Name, p.Value) |> Map.ofList

            let beyond =
                statement.Parameters
                |> List.tryPick (fun p ->
                    match p.Value with
                    | BoundValue.Decimal digits when precision digits > 38 -> Some p.Name
                    | _ -> None)

            match used |> List.tryFind (supplied.ContainsKey >> not) with
            | Some missing -> Error(BoundStatementError.Refused(BindingRefusal.MissingParameter missing))
            | None ->
                match statement.Parameters |> List.tryFind (fun p -> not (List.contains p.Name used)) with
                | Some unused -> Error(BoundStatementError.Refused(BindingRefusal.UnusedParameter unused.Name))
                | None ->
                    match beyond with
                    | Some name ->
                        Error(
                            refusedText $"parameter '%s{name}' is a decimal beyond DECIMAL(38, s), at most 38 digits"
                        )
                    | None ->
                        let command = new SqlCommand(statement.Text)
                        command.CommandType <- CommandType.Text

                        match statement.Timeout with
                        | Some span -> command.CommandTimeout <- max 1 (int (Math.Ceiling span.TotalSeconds))
                        | None -> ()

                        for name in used do
                            command.Parameters.Add(parameter name supplied[name]) |> ignore

                        Ok command)

module SynapseDataSource =

    /// The `DataSourceConfig.Kind` this connector answers to.
    [<Literal>]
    let Kind = "Synapse"

    /// AAD scope the connector requests a token for under
    /// `aad-default`. Azure SQL and Synapse share it.
    [<Literal>]
    let private SqlScope = "https://database.windows.net/.default"

    /// Parse the `auth` ConnectionScope value.
    let parseAuth (raw: string option) : Result<SynapseAuth, IngestionError> =
        match raw with
        | None -> Ok SqlLogin
        | Some value ->
            match value.Trim().ToLowerInvariant() with
            | "sql"
            | "sql-login" -> Ok SqlLogin
            | "aad-token"
            | "aad_token" -> Ok AadToken
            | "aad-default"
            | "aad_default"
            | "managed-identity" -> Ok AadDefault
            | other ->
                Error(
                    SchemaMismatch
                        $"ConnectionScope key 'auth' must be one of [sql, aad-token, aad-default]; got '%s{other}'"
                )

    /// Read and validate one call's `ConnectionScope`. Pure — no
    /// connection is opened and no credential is resolved.
    let readSettings (scope: Map<string, string>) : Result<SynapseSourceSettings, IngestionError> =
        ConnectionScope.require scope "server"
        |> Result.bind (fun server ->
            ConnectionScope.require scope "database"
            |> Result.bind (fun database ->
                parseAuth (ConnectionScope.optional scope "auth")
                |> Result.bind (fun auth ->
                    ConnectionScope.optionalInt scope "port"
                    |> Result.bind (fun port ->
                        ConnectionScope.optionalInt scope "connect_timeout_seconds"
                        |> Result.bind (fun connectTimeout ->
                            ConnectionScope.optionalInt scope "command_timeout_seconds"
                            |> Result.bind (fun commandTimeout ->
                                let user = ConnectionScope.optional scope "user"

                                match auth, user with
                                | SqlLogin, None ->
                                    Error(SchemaMismatch "auth = sql requires ConnectionScope key 'user'")
                                | SqlLogin, Some _
                                | AadToken, _
                                | AadDefault, _ ->
                                    Ok {
                                        Server = server
                                        Database = database
                                        Port = defaultArg port 1433
                                        Schema = ConnectionScope.optionalOr scope "schema" "dbo"
                                        Auth = auth
                                        User = user
                                        ConnectTimeoutSeconds = defaultArg connectTimeout 30
                                        CommandTimeoutSeconds = commandTimeout
                                    }))))))

    /// Build the TDS connection string. Public so a deployment can
    /// unit-test its own configuration; the password is folded in by
    /// the caller only under `SqlLogin`.
    let composeConnectionString (settings: SynapseSourceSettings) (password: string option) : string =
        let builder = SqlConnectionStringBuilder()
        builder.DataSource <- $"%s{settings.Server},%d{settings.Port}"
        builder.InitialCatalog <- settings.Database
        builder.ConnectTimeout <- settings.ConnectTimeoutSeconds
        // Synapse endpoints are public and always TLS; SqlClient 4+
        // defaults `Encrypt` to true, but stating it means a future
        // provider default cannot silently downgrade the transport.
        builder.Encrypt <- true

        match settings.Auth, settings.User, password with
        | SqlLogin, Some user, Some password ->
            builder.UserID <- user
            builder.Password <- password
        | SqlLogin, Some user, None -> builder.UserID <- user
        | SqlLogin, None, _
        | AadToken, _, _
        | AadDefault, _, _ -> ()

        builder.ConnectionString

    /// A bare identifier handed to `Query` is expanded to
    /// `SELECT * FROM [schema].[table]`. Anything else is passed
    /// through verbatim as T-SQL.
    let resolveStatement (settings: SynapseSourceSettings) (sql: string) : Result<string, IngestionError> =
        if SqlIdentifier.isSafe sql then
            SynapseCatalogue.selectAllSql settings.Schema sql
        else
            Ok sql

    type private SynapseDataSourceImpl(secretStore: ISecretStore option) =

        let openConnection (ctx: DataSourceCallContext) (settings: SynapseSourceSettings) = async {
            let! ct = Async.CancellationToken

            let! credential = async {
                match settings.Auth with
                | SqlLogin
                | AadToken ->
                    let! resolved = Credentials.resolve secretStore ctx
                    return resolved |> Result.map Some
                | AadDefault -> return Ok None
            }

            match credential with
            | Error err -> return Error err
            | Ok credential ->
                let password =
                    match settings.Auth with
                    | SqlLogin -> credential
                    | AadToken
                    | AadDefault -> None

                let connection = new SqlConnection(composeConnectionString settings password)

                try
                    match settings.Auth, credential with
                    | AadToken, Some token -> connection.AccessToken <- token
                    | AadDefault, _ ->
                        let azureCredential = DefaultAzureCredential()
                        let request = TokenRequestContext([| SqlScope |])
                        let! token = azureCredential.GetTokenAsync(request, ct).AsTask() |> Async.AwaitTask
                        connection.AccessToken <- token.Token
                    | AadToken, None
                    | SqlLogin, _ -> ()

                    do! connection.OpenAsync ct |> Async.AwaitTask
                    return Ok connection
                with ex ->
                    connection.Dispose()
                    return Error(Errors.classify "Synapse connect" ex)
        }

        /// Set the source's command timeout on `command`, when it declares one.
        let applyTimeout (settings: SynapseSourceSettings) (command: SqlCommand) =
            match settings.CommandTimeoutSeconds with
            | Some seconds -> command.CommandTimeout <- seconds
            | None -> ()

        let newCommand (settings: SynapseSourceSettings) (connection: SqlConnection) (sql: string) =
            let command = connection.CreateCommand()
            command.CommandText <- sql
            command.CommandType <- CommandType.Text
            applyTimeout settings command
            command

        /// Run `command` and read its result as CSV. The connection opened,
        /// so the endpoint is reachable: a failure here is the statement or
        /// the schema. A driver exception can arrive wrapped in an
        /// `AggregateException`, so it is matched through `ProviderException`.
        let readCsv (command: SqlCommand) : Async<Result<byte[], IngestionError>> = async {
            let! ct = Async.CancellationToken

            try
                use! reader = command.ExecuteReaderAsync ct |> Async.AwaitTask
                let! bytes = Csv.ofReader reader
                return Ok bytes
            with ProviderException(ex: DbException) ->
                return Error(SchemaMismatch $"Synapse query failed: %s{ex.Message}")
        }

        /// Run a bound statement. Everything `SynapseBinding.command` refuses
        /// is refused before any connection opens; the values travel as
        /// typed parameters, and the statement's timeout wins over the
        /// source's.
        let queryBound (ctx: DataSourceCallContext) (statement: BoundStatement) = async {
            match readSettings ctx.Config.ConnectionScope with
            | Error err -> return Error(BoundStatementError.Failed err)
            | Ok settings ->
                match SynapseBinding.command statement with
                | Error refusal -> return Error refusal
                | Ok command ->
                    use command = command

                    if statement.Timeout.IsNone then
                        applyTimeout settings command

                    let! result =
                        Errors.guard "Synapse QueryBound" (fun () -> async {
                            match! openConnection ctx settings with
                            | Error err -> return Error err
                            | Ok connection ->
                                use connection = connection
                                command.Connection <- connection
                                return! readCsv command
                        })

                    return result |> Result.mapError BoundStatementError.Failed
        }

        let binds =
            { new IBindsStatements with
                member _.BindableKinds = SynapseBinding.bindableKinds
                member _.QueryBound(ctx, statement) = queryBound ctx statement
            }

        let readCatalogue
            (ctx: DataSourceCallContext)
            (context: string)
            (buildSql: SynapseSourceSettings -> Result<string, IngestionError>)
            (configure: SqlCommand -> unit)
            (project: DbDataReader -> 'T)
            : Async<Result<'T list, IngestionError>> =
            Errors.guard context (fun () -> async {
                match readSettings ctx.Config.ConnectionScope with
                | Error err -> return Error err
                | Ok settings ->
                    match buildSql settings with
                    | Error err -> return Error err
                    | Ok sql ->
                        match! openConnection ctx settings with
                        | Error err -> return Error err
                        | Ok connection ->
                            use connection = connection
                            let! ct = Async.CancellationToken
                            use command = newCommand settings connection sql
                            configure command
                            use! reader = command.ExecuteReaderAsync ct |> Async.AwaitTask

                            let rows = ResizeArray<'T>()
                            let mutable go = true

                            while go do
                                let! hasRow = reader.ReadAsync ct |> Async.AwaitTask

                                if hasRow then rows.Add(project reader) else go <- false

                            return Ok(List.ofSeq rows)
            })

        // `Query` emits RFC 4180 CSV; declared, not implied.
        interface IDeclaresPayloadFormat with
            member _.PayloadFormat = PayloadFormat.Csv

        // OPTIONAL: `sql-login` and `aad-token` read the stored secret (and
        // refuse its absence themselves); `aad-default` takes
        // `DefaultAzureCredential` and consults none.
        interface IDeclaresCredentialNeed with
            member _.CredentialNeed = CredentialNeed.Optional

        interface IDataSource with
            member _.Kind = Kind

            // A query against Synapse has no location a delivery can watch,
            // and it emits only the CSV it declares; it binds a statement's
            // values.
            member _.Capabilities = {
                ConnectorCapabilities.none with
                    Binds = Some binds
            }

            member _.Connect(ctx) =
                Errors.guard "Synapse Connect" (fun () -> async {
                    match readSettings ctx.Config.ConnectionScope with
                    | Error err -> return Error err
                    | Ok settings ->
                        match! openConnection ctx settings with
                        | Error err -> return Error err
                        | Ok connection ->
                            use _ = connection
                            return Ok()
                })

            member _.ListTables(ctx) =
                readCatalogue
                    ctx
                    "Synapse ListTables"
                    (fun settings -> SynapseCatalogue.listTablesSql settings.Schema)
                    ignore
                    (fun reader -> reader.GetValue 0 |> Csv.renderValue)

            // A statement-shaped "table" (what `Query` passes
            // through verbatim) is described by its first result set through
            // `sp_describe_first_result_set`, the statement bound as a
            // parameter and never executed, so a statement ingested under a
            // caller label keeps its schema record and drift detection. A
            // bare identifier reads `INFORMATION_SCHEMA` exactly as before.
            member _.GetSchema(ctx, table) = async {
                let statementShaped = not (SqlIdentifier.isSafe table)

                let! rows =
                    readCatalogue
                        ctx
                        "Synapse GetSchema"
                        (fun settings ->
                            if statementShaped then
                                Ok SynapseCatalogue.describeStatementSql
                            else
                                SynapseCatalogue.columnsSql settings.Schema table)
                        (fun command ->
                            if statementShaped then
                                let parameter = command.CreateParameter()
                                parameter.ParameterName <- "@tsql"
                                parameter.DbType <- DbType.String
                                parameter.Size <- -1
                                parameter.Value <- table
                                command.Parameters.Add parameter |> ignore)
                        (fun reader ->
                            if statementShaped then
                                let text (column: string) =
                                    reader.GetValue(reader.GetOrdinal column) |> Csv.renderValue

                                let nullable =
                                    match reader.GetValue(reader.GetOrdinal "is_nullable") with
                                    | :? bool as flag -> flag
                                    | _ -> true

                                TypeMap.column (text "name") (text "system_type_name") nullable
                            else
                                let name = reader.GetValue 0 |> Csv.renderValue
                                let nativeType = reader.GetValue 1 |> Csv.renderValue
                                let nullToken = reader.GetValue 2 |> Csv.renderValue
                                TypeMap.column name nativeType (SynapseCatalogue.nullableFromToken nullToken))

                return rows |> Result.map (TypeMap.schema table)
            }

            member _.Query(ctx, sql) =
                Errors.guard "Synapse Query" (fun () -> async {
                    match readSettings ctx.Config.ConnectionScope with
                    | Error err -> return Error err
                    | Ok settings ->
                        match resolveStatement settings sql with
                        | Error err -> return Error err
                        | Ok statement ->
                            match! openConnection ctx settings with
                            | Error err -> return Error err
                            | Ok connection ->
                                use connection = connection
                                use command = newCommand settings connection statement
                                return! readCsv command
                })

    /// Build the connector with an `ISecretStore`. Under `auth = sql`
    /// the credential is the password; under `auth = aad-token` it is
    /// a ready-minted AAD access token. Either is re-read per call, so
    /// rotation takes effect without reconstructing the connector.
    let create (secretStore: ISecretStore) : IDataSource =
        SynapseDataSourceImpl(Some secretStore) :> IDataSource

    /// Build the connector for a deployment authenticating entirely
    /// through `DefaultAzureCredential`. Every source wired to this
    /// instance must set `auth = aad-default`.
    let createWithDefaultCredentials () : IDataSource =
        SynapseDataSourceImpl(None) :> IDataSource