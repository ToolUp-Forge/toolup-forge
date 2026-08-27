// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Sql.SqlDataSource

open System
open System.Data
open System.Data.Common
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.Common

// ─── ToolUp.DataSources.Sql ───────────────────────────────────────
//
// One `IDataSource` companion spanning six operational databases —
// PostgreSQL, MySQL / MariaDB, SQL Server (and Azure SQL), SQLite,
// Oracle and ClickHouse — selected per data source by the `backend`
// key in `DataSourceConfig.ConnectionScope`.
//
// **Production-ready, not dev-only.** The connector holds no state
// between calls: every method opens a connection, uses it, and
// disposes it (ADO connection pooling makes that cheap), and the
// credential is re-read through the `ISecretStore` thunk on EVERY
// call so a rotated password takes effect without reconstructing the
// connector. That satisfies portability rule 4 (stateless handlers)
// by construction.
//
// **Cancellation** rides the ambient `Async` cancellation token —
// `DataSourceCallContext` carries no token field, so the connector
// reads `Async.CancellationToken` and threads it into every ADO
// `*Async` call. A cancelled ingestion therefore aborts the in-flight
// statement rather than waiting for it.
//
// **Query output is RFC 4180 CSV** with a header row, UTF-8, no BOM —
// the uniform wire format for this connector family. See
// `ToolUp.DataSources.Common.Csv`.

/// Parsed, validated view of one data source's `ConnectionScope`.
/// Public so a deployment can unit-test its own configuration with
/// `SqlDataSource.readSettings` before wiring it.
type SqlSourceSettings = {
    /// Which engine this source talks to.
    Backend: SqlBackend
    /// ADO connection string for the backend, WITHOUT the password
    /// when the password arrives through `ISecretStore`.
    ConnectionString: string
    /// Schema/owner the catalogue queries scope to. `None` falls back
    /// to `SqlDialect.defaultSchema`, which is itself `None` for the
    /// backends whose catalogue query self-scopes.
    Schema: string option
    /// Per-command timeout. `None` leaves the provider default.
    CommandTimeoutSeconds: int option
}

// Type abbreviations rather than `open`, so the six provider
// namespaces never shadow each other or the `SqlBackend` cases.
type private PgConn = global.Npgsql.NpgsqlConnection
type private MyConn = global.MySqlConnector.MySqlConnection
type private MsConn = global.Microsoft.Data.SqlClient.SqlConnection
type private LiteConn = global.Microsoft.Data.Sqlite.SqliteConnection
type private OraConn = global.Oracle.ManagedDataAccess.Client.OracleConnection
type private ChConn = global.ClickHouse.Client.ADO.ClickHouseConnection

/// The `DataSourceConfig.Kind` this connector answers to. A
/// deployment that prefers per-backend routing registers the same
/// connector again through `createWithKind`.
[<Literal>]
let Kind = "Sql"

/// Read and validate one call's `ConnectionScope`. Pure — no
/// connection is opened, nothing is resolved from `ISecretStore`.
let readSettings (scope: Map<string, string>) : Result<SqlSourceSettings, IngestionError> =
    ConnectionScope.require scope "backend"
    |> Result.bind SqlDialect.parseBackend
    |> Result.bind (fun backend ->
        ConnectionScope.require scope "connection_string"
        |> Result.bind (fun connectionString ->
            ConnectionScope.optionalInt scope "command_timeout_seconds"
            |> Result.bind (fun timeout ->
                let declaredSchema = ConnectionScope.optional scope "schema"

                let schema =
                    match declaredSchema with
                    | Some _ -> declaredSchema
                    | None -> SqlDialect.defaultSchema backend

                match timeout with
                | Some seconds when seconds <= 0 ->
                    Error(
                        SchemaMismatch
                            $"ConnectionScope key 'command_timeout_seconds' must be positive; got %d{seconds}"
                    )
                | Some _
                | None ->
                    Ok {
                        Backend = backend
                        ConnectionString = connectionString
                        Schema = schema
                        CommandTimeoutSeconds = timeout
                    })))

/// Fold a resolved password into a base connection string. Uses the
/// BCL `DbConnectionStringBuilder` so quoting is the provider-neutral
/// ADO rules rather than string concatenation, and never overwrites a
/// password the operator put in the connection string themselves.
let composeConnectionString (baseConnectionString: string) (password: string option) : string =
    match password with
    | None -> baseConnectionString
    | Some value ->
        let builder = DbConnectionStringBuilder()
        builder.ConnectionString <- baseConnectionString

        if builder.ContainsKey "Password" || builder.ContainsKey "pwd" then
            baseConnectionString
        else
            builder["Password"] <- value
            builder.ConnectionString

let private createConnection (backend: SqlBackend) (connectionString: string) : DbConnection =
    match backend with
    | PostgreSql -> new PgConn(connectionString) :> DbConnection
    | MySql -> new MyConn(connectionString) :> DbConnection
    | SqlServer -> new MsConn(connectionString) :> DbConnection
    | Sqlite -> new LiteConn(connectionString) :> DbConnection
    | Oracle -> new OraConn(connectionString) :> DbConnection
    | ClickHouse -> new ChConn(connectionString) :> DbConnection

/// A bare identifier handed to `Query` is expanded to
/// `SELECT * FROM <schema>.<table>` — the documented convenience that
/// lets an admin UI offer "ingest this whole table" without composing
/// SQL. Anything containing whitespace or punctuation is passed
/// through verbatim as a statement.
let resolveStatement (settings: SqlSourceSettings) (sql: string) : Result<string, IngestionError> =
    if SqlDialect.isSafeIdentifier sql then
        SqlDialect.selectAllSql settings.Backend settings.Schema sql
    else
        Ok sql

type private SqlDataSourceImpl(kind: string, secretStore: ISecretStore option) =

    let prepare (ctx: DataSourceCallContext) : Async<Result<SqlSourceSettings * DbConnection, IngestionError>> = async {
        match readSettings ctx.Config.ConnectionScope with
        | Error err -> return Error err
        | Ok settings ->
            // The credential is OPTIONAL for this connector — a
            // deployment may carry the whole connection string
            // (including integrated security or a password) in
            // `ConnectionScope`, in which case there is no secret to
            // resolve and a missing one is not an error.
            let! password = Credentials.resolveOptional secretStore ctx
            let connectionString = composeConnectionString settings.ConnectionString password
            let connection = createConnection settings.Backend connectionString
            let! ct = Async.CancellationToken

            try
                do! connection.OpenAsync ct |> Async.AwaitTask
                return Ok(settings, connection)
            with ex ->
                connection.Dispose()
                return Error(Errors.classify $"%s{kind} (%s{SqlDialect.backendName settings.Backend}) connect" ex)
    }

    let newCommand (settings: SqlSourceSettings) (connection: DbConnection) (sql: string) =
        let command = connection.CreateCommand()
        command.CommandText <- sql
        command.CommandType <- CommandType.Text

        match settings.CommandTimeoutSeconds with
        | Some seconds -> command.CommandTimeout <- seconds
        | None -> ()

        command

    /// Run a catalogue query, projecting each row with `project`.
    let readCatalogue
        (ctx: DataSourceCallContext)
        (context: string)
        (buildSql: SqlSourceSettings -> Result<string, IngestionError>)
        (project: DbDataReader -> 'T)
        : Async<Result<'T list, IngestionError>> =
        Errors.guard context (fun () -> async {
            match! prepare ctx with
            | Error err -> return Error err
            | Ok(settings, connection) ->
                use connection = connection

                match buildSql settings with
                | Error err -> return Error err
                | Ok sql ->
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
        member _.Kind = kind

        member _.Connect(ctx) =
            Errors.guard $"%s{kind} Connect" (fun () -> async {
                match! prepare ctx with
                | Error err -> return Error err
                | Ok(_, connection) ->
                    use _ = connection
                    return Ok()
            })

        member _.ListTables(ctx) =
            readCatalogue
                ctx
                $"%s{kind} ListTables"
                (fun settings -> SqlDialect.listTablesSql settings.Backend settings.Schema)
                (fun reader -> reader.GetValue 0 |> Csv.renderValue)

        member _.GetSchema(ctx, table) = async {
            let! rows =
                readCatalogue
                    ctx
                    $"%s{kind} GetSchema"
                    (fun settings -> SqlDialect.columnsSql settings.Backend settings.Schema table)
                    (fun reader ->
                        let name = reader.GetValue 0 |> Csv.renderValue
                        let nativeType = reader.GetValue 1 |> Csv.renderValue
                        let nullToken = reader.GetValue 2 |> Csv.renderValue
                        name, nativeType, nullToken)

            match rows with
            | Error err -> return Error err
            | Ok rows ->
                // The backend is needed to interpret the nullability
                // token, and re-reading settings here is cheaper than
                // threading it out of the catalogue helper.
                match readSettings ctx.Config.ConnectionScope with
                | Error err -> return Error err
                | Ok settings ->
                    let columns =
                        rows
                        |> List.map (fun (name, nativeType, nullToken) ->
                            TypeMap.column name nativeType (SqlDialect.nullableFromToken settings.Backend nullToken))

                    return Ok(TypeMap.schema table columns)
        }

        member _.Query(ctx, sql) =
            Errors.guard $"%s{kind} Query" (fun () -> async {
                match! prepare ctx with
                | Error err -> return Error err
                | Ok(settings, connection) ->
                    use connection = connection

                    match resolveStatement settings sql with
                    | Error err -> return Error err
                    | Ok statement ->
                        let! ct = Async.CancellationToken
                        use command = newCommand settings connection statement

                        try
                            use! reader = command.ExecuteReaderAsync ct |> Async.AwaitTask
                            let! bytes = Csv.ofReader reader
                            return Ok bytes
                        with :? DbException as ex ->
                            // The connection opened, so the source
                            // is reachable — a failure HERE is the
                            // statement or the schema, which is
                            // the operator's to fix.
                            return
                                Error(
                                    SchemaMismatch
                                        $"%s{kind} (%s{SqlDialect.backendName settings.Backend}) query failed: %s{ex.Message}"
                                )
            })

/// Build the connector with an `ISecretStore` for the credential
/// thunk. The store is consulted per call, so a rotated password
/// takes effect without reconstructing the connector.
let create (secretStore: ISecretStore) : IDataSource =
    SqlDataSourceImpl(Kind, Some secretStore) :> IDataSource

/// Build the connector for a deployment whose `ConnectionScope`
/// connection string already carries every credential (integrated
/// security, a managed identity, a local SQLite file). The ingestor's
/// pre-resolved `DataSourceCallContext.Credential` is still honoured
/// when present.
let createWithoutSecrets () : IDataSource =
    SqlDataSourceImpl(Kind, None) :> IDataSource

/// Register the same connector under a deployment-chosen `Kind`, so a
/// deployment that prefers `DataSourceConfig.Kind = "Postgres"` over
/// `"Sql"` + a `backend` key can route that way instead. The
/// `backend` key is still what selects the engine.
let createWithKind (kind: string) (secretStore: ISecretStore option) : IDataSource =
    SqlDataSourceImpl(kind, secretStore) :> IDataSource