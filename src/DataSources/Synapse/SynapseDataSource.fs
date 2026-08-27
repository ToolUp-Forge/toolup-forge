// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Synapse

open System
open System.Data
open System.Data.Common
open Azure.Core
open Azure.Identity
open Microsoft.Data.SqlClient
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.Common
open DataManagementTypes

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

module SynapseDataSource =

    /// The `DataSourceConfig.Kind` this connector answers to.
    [<Literal>]
    let Kind = "Synapse"

    /// AAD scope the connector requests a token for under
    /// `aad-default`. Azure SQL and Synapse share it.
    [<Literal>]
    let private SqlScope = "https://database.windows.net/.default"

    /// Driver exceptions can surface at the query `with` handler
    /// wrapped in `AggregateException`, so a direct `:? DbException`
    /// type test never fires — the class the first armed cloud-parity
    /// run (2026-08-27) proved live in the AWS companions. A wrapped
    /// `DbException` would escape to `Errors.guard` and lose the site's
    /// `SchemaMismatch` classification. Match through the wrapper:
    /// flatten and take the single inner exception a one-Task await
    /// carries; a bare exception passes through unchanged.
    let private (|Unwrapped|) (ex: exn) =
        match ex with
        | :? AggregateException as aggregate ->
            match Seq.tryHead (aggregate.Flatten().InnerExceptions) with
            | Some inner -> inner
            | None -> ex
        | _ -> ex

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
                    return Ok(connection :> DbConnection)
                with ex ->
                    connection.Dispose()
                    return Error(Errors.classify "Synapse connect" ex)
        }

        let newCommand (settings: SynapseSourceSettings) (connection: DbConnection) (sql: string) =
            let command = connection.CreateCommand()
            command.CommandText <- sql
            command.CommandType <- CommandType.Text

            match settings.CommandTimeoutSeconds with
            | Some seconds -> command.CommandTimeout <- seconds
            | None -> ()

            command

        let readCatalogue
            (ctx: DataSourceCallContext)
            (context: string)
            (buildSql: SynapseSourceSettings -> Result<string, IngestionError>)
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
                            use! reader = command.ExecuteReaderAsync ct |> Async.AwaitTask

                            let rows = ResizeArray<'T>()
                            let mutable go = true

                            while go do
                                let! hasRow = reader.ReadAsync ct |> Async.AwaitTask

                                if hasRow then rows.Add(project reader) else go <- false

                            return Ok(List.ofSeq rows)
            })

        interface IDataSource with
            member _.Kind = Kind

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
                    (fun reader -> reader.GetValue 0 |> Csv.renderValue)

            member _.GetSchema(ctx, table) = async {
                let! rows =
                    readCatalogue
                        ctx
                        "Synapse GetSchema"
                        (fun settings -> SynapseCatalogue.columnsSql settings.Schema table)
                        (fun reader ->
                            let name = reader.GetValue 0 |> Csv.renderValue
                            let nativeType = reader.GetValue 1 |> Csv.renderValue
                            let nullToken = reader.GetValue 2 |> Csv.renderValue
                            name, nativeType, nullToken)

                return
                    rows
                    |> Result.map (fun rows ->
                        rows
                        |> List.map (fun (name, nativeType, nullToken) ->
                            TypeMap.column name nativeType (SynapseCatalogue.nullableFromToken nullToken))
                        |> TypeMap.schema table)
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
                                let! ct = Async.CancellationToken
                                use command = newCommand settings connection statement

                                try
                                    use! reader = command.ExecuteReaderAsync ct |> Async.AwaitTask
                                    let! bytes = Csv.ofReader reader
                                    return Ok bytes
                                with Unwrapped(:? DbException as ex) ->
                                    // The connection opened, so
                                    // the endpoint is reachable —
                                    // a failure HERE is the
                                    // statement or the schema.
                                    return Error(SchemaMismatch $"Synapse query failed: %s{ex.Message}")
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