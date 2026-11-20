// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Sql.SqlDataSource

open System
open System.Data
open System.Data.Common
open System.Globalization
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.Common
open ToolUp.Platform.ProviderExceptions
open ToolUp.Platform.Infra

// Driver exceptions can surface at the query `with` handler wrapped in
// `AggregateException`, so a direct `:? DbException` type test never
// fires — the class the first armed cloud-parity run (2026-08-27)
// proved live in the AWS companions. A wrapped `DbException` would
// escape to `Errors.guard` and lose the site's `SchemaMismatch`
// classification. Match through the wrapper: flatten and take the
// single inner exception a one-Task await carries; a bare exception
// passes through unchanged.
//
// The match is the shared `ToolUp.Platform.ProviderExceptions`
// (`ProviderException` / `Unwrapped`).

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

/// The connection string a source declaring `read_only=true` opens with,
/// for the backends whose provider carries a read-only intent in it:
/// PostgreSQL's `default_transaction_read_only` startup option, SQL
/// Server's `ApplicationIntent=ReadOnly`, SQLite's `Mode=ReadOnly` and
/// ClickHouse's `readonly=2` setting. MySQL and Oracle have none, and are
/// returned unchanged: each call runs in a read-only transaction instead.
/// A SQLite `Mode=Memory` database cannot be opened read-only, and is
/// refused by name rather than opened writable.
let readOnlyConnectionString (backend: SqlBackend) (connectionString: string) : Result<string, IngestionError> =
    let builder = DbConnectionStringBuilder()
    builder.ConnectionString <- connectionString

    let value (key: string) =
        match builder.TryGetValue key with
        | true, value when not (isNull value) && not (String.IsNullOrWhiteSpace(string value)) -> Some(string value)
        | _ -> None

    match backend with
    | PostgreSql ->
        let intent = "-c default_transaction_read_only=on"

        builder["Options"] <-
            match value "Options" with
            | Some options -> $"%s{options} %s{intent}"
            | None -> intent

        Ok builder.ConnectionString
    | SqlServer ->
        builder.Remove "Application Intent" |> ignore
        builder["ApplicationIntent"] <- "ReadOnly"
        Ok builder.ConnectionString
    | Sqlite when
        value "Mode"
        |> Option.exists (fun mode -> mode.Trim().ToLowerInvariant() = "memory")
        ->
        Error(
            SchemaMismatch
                $"ConnectionScope key '%s{SourcePosture.ReadOnlyKey}' cannot be honoured: a SQLite Mode=Memory database cannot be opened read-only. Use a database file, or drop the key"
        )
    | Sqlite ->
        builder["Mode"] <- "ReadOnly"
        Ok builder.ConnectionString
    | ClickHouse ->
        builder["set_readonly"] <- "2"
        Ok builder.ConnectionString
    | MySql
    | Oracle -> Ok connectionString

let private createConnection (backend: SqlBackend) (connectionString: string) : DbConnection =
    match backend with
    | PostgreSql -> new PgConn(connectionString) :> DbConnection
    | MySql -> new MyConn(connectionString) :> DbConnection
    | SqlServer -> new MsConn(connectionString) :> DbConnection
    | Sqlite -> new LiteConn(connectionString) :> DbConnection
    | Oracle -> new OraConn(connectionString) :> DbConnection
    | ClickHouse -> new ChConn(connectionString) :> DbConnection

// ─── Binding a value through each provider's own parameter type ──
//
// The provider type is set from the value's kind, never inferred by the
// provider from a boxed object, and each value is converted to the exact
// provider type here, so no provider guesses at a width or a scale.

type private PgParam = global.Npgsql.NpgsqlParameter
type private PgType = global.NpgsqlTypes.NpgsqlDbType
type private MyParam = global.MySqlConnector.MySqlParameter
type private MyType = global.MySqlConnector.MySqlDbType
type private MsParam = global.Microsoft.Data.SqlClient.SqlParameter
type private LiteParam = global.Microsoft.Data.Sqlite.SqliteParameter
type private LiteType = global.Microsoft.Data.Sqlite.SqliteType
type private OraParam = global.Oracle.ManagedDataAccess.Client.OracleParameter
type private OraType = global.Oracle.ManagedDataAccess.Client.OracleDbType
type private OraCommand = global.Oracle.ManagedDataAccess.Client.OracleCommand
type private OraDecimal = global.Oracle.ManagedDataAccess.Types.OracleDecimal
type private OraTimestampTz = global.Oracle.ManagedDataAccess.Types.OracleTimeStampTZ
type private ChParam = global.ClickHouse.Client.ADO.Parameters.ClickHouseDbParameter
type private ChDecimal = global.ClickHouse.Client.Numerics.ClickHouseDecimal

/// The value a provider is handed for `value`: `DBNull` for a typed null,
/// and otherwise what `convert` takes the present value to.
let private providerValue (convert: BoundValue -> obj) (value: BoundValue) : obj =
    match value with
    | BoundValue.Null _ -> box DBNull.Value
    | BoundValue.Float _
    | BoundValue.Int _
    | BoundValue.Bool _
    | BoundValue.Text _
    | BoundValue.Timestamp _
    | BoundValue.Categorical _
    | BoundValue.Decimal _ -> convert value

/// The plain .NET value of a bound value; a decimal is its canonical text.
let private plain (value: BoundValue) : obj =
    match value with
    | BoundValue.Float f -> box f
    | BoundValue.Int i -> box i
    | BoundValue.Bool b -> box b
    | BoundValue.Text s
    | BoundValue.Categorical s
    | BoundValue.Decimal s -> box s
    | BoundValue.Timestamp t -> box t
    | BoundValue.Null _ -> box DBNull.Value

/// The provider parameter for one binding on `backend`.
let private providerParameter (backend: SqlBackend) (binding: ProviderBinding) : DbParameter =
    let kind = BoundValue.kind binding.Value

    let parameter: DbParameter =
        match backend with
        | PostgreSql ->
            let p = PgParam()

            // A decimal travels as its text into `CAST($n AS numeric)`
            // (`SqlBinding.translate`); timestamptz takes a UTC offset only.
            p.NpgsqlDbType <-
                match kind with
                | DatasetDType.Float -> PgType.Double
                | DatasetDType.Int -> PgType.Bigint
                | DatasetDType.Bool -> PgType.Boolean
                | DatasetDType.Text
                | DatasetDType.Categorical
                | DatasetDType.Decimal -> PgType.Text
                | DatasetDType.Timestamp -> PgType.TimestampTz

            p.Value <-
                binding.Value
                |> providerValue (fun v ->
                    match v with
                    | BoundValue.Timestamp t -> box (t.ToUniversalTime())
                    | _ -> plain v)

            p
        | MySql ->
            let p = MyParam()

            p.MySqlDbType <-
                match kind with
                | DatasetDType.Float -> MyType.Double
                | DatasetDType.Int -> MyType.Int64
                | DatasetDType.Bool -> MyType.Bool
                | DatasetDType.Text
                | DatasetDType.Categorical
                | DatasetDType.Decimal -> MyType.VarChar
                | DatasetDType.Timestamp -> MyType.DateTime

            // MySQL's DATETIME holds no offset: the instant is bound in UTC.
            p.Value <-
                binding.Value
                |> providerValue (fun v ->
                    match v with
                    | BoundValue.Timestamp t -> box t.UtcDateTime
                    | _ -> plain v)

            p
        | SqlServer ->
            let p = MsParam()

            p.SqlDbType <-
                match kind with
                | DatasetDType.Float -> SqlDbType.Float
                | DatasetDType.Int -> SqlDbType.BigInt
                | DatasetDType.Bool -> SqlDbType.Bit
                | DatasetDType.Text
                | DatasetDType.Categorical -> SqlDbType.NVarChar
                | DatasetDType.Timestamp -> SqlDbType.DateTimeOffset
                | DatasetDType.Decimal -> SqlDbType.Decimal

            // `SqlDecimal` holds 38 digits, where `System.Decimal` holds 28.
            match binding.Value with
            | BoundValue.Decimal digits ->
                let exact = SqlTypes.SqlDecimal.Parse digits
                p.Precision <- exact.Precision
                p.Scale <- exact.Scale
                p.SqlValue <- exact
            | _ -> p.Value <- providerValue plain binding.Value

            p
        | Sqlite ->
            let p = LiteParam()

            // SQLite has no exact numeric and no timestamp: the decimal's
            // digits and the timestamp's ISO 8601 text are bound as TEXT,
            // which is how the provider stores a `System.Decimal` itself.
            p.SqliteType <-
                match kind with
                | DatasetDType.Float -> LiteType.Real
                | DatasetDType.Int
                | DatasetDType.Bool -> LiteType.Integer
                | DatasetDType.Text
                | DatasetDType.Categorical
                | DatasetDType.Decimal
                | DatasetDType.Timestamp -> LiteType.Text

            p.Value <-
                binding.Value
                |> providerValue (fun v ->
                    match v with
                    | BoundValue.Bool b -> box (if b then 1L else 0L)
                    | BoundValue.Timestamp t -> box (t.ToString("O", CultureInfo.InvariantCulture))
                    | _ -> plain v)

            p
        | Oracle ->
            let p = new OraParam()

            p.OracleDbType <-
                match kind with
                | DatasetDType.Float -> OraType.BinaryDouble
                | DatasetDType.Int -> OraType.Int64
                | DatasetDType.Bool -> OraType.Boolean
                | DatasetDType.Text
                | DatasetDType.Categorical -> OraType.NVarchar2
                | DatasetDType.Timestamp -> OraType.TimeStampTZ
                | DatasetDType.Decimal -> OraType.Decimal

            // `OracleDecimal` holds 38 significant digits.
            p.Value <-
                binding.Value
                |> providerValue (fun v ->
                    match v with
                    | BoundValue.Decimal digits -> box (OraDecimal digits)
                    | BoundValue.Timestamp t -> box (OraTimestampTz(t.UtcDateTime, "+00:00"))
                    | _ -> plain v)

            p
        | ClickHouse ->
            let p = ChParam()
            // The type the marker names (`{name:Type}`).
            p.ClickHouseType <- SqlBinding.clickHouseType binding.Value

            p.Value <-
                binding.Value
                |> providerValue (fun v ->
                    match v with
                    | BoundValue.Decimal digits -> box (ChDecimal.Parse(digits, CultureInfo.InvariantCulture))
                    | BoundValue.Timestamp t -> box t.UtcDateTime
                    | _ -> plain v)

            p

    parameter.ParameterName <- binding.Name |> Option.defaultValue ""
    parameter

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

/// An open connection with the source's settings and posture, and the
/// read-only transaction its calls run in on a backend whose read-only
/// means is one. Disposing it rolls that transaction back.
type private Opened = {
    Settings: SqlSourceSettings
    Posture: SourcePosture
    Connection: DbConnection
    Transaction: DbTransaction option
} with

    interface IDisposable with
        member this.Dispose() =
            this.Transaction |> Option.iter _.Dispose()
            this.Connection.Dispose()

/// The read-only means that needs an open connection. MySQL and Oracle
/// run each call in a read-only transaction. SQL Server's
/// `ApplicationIntent` only routes, so the database the connection reached
/// must itself be read-only (a readable secondary, a read scale-out
/// replica, a `READ_ONLY` database); a writable one is refused by name at
/// connect, because SQL Server has no read-only session to fall back on.
let private openReadOnly
    (backend: SqlBackend)
    (connection: DbConnection)
    (ct: Threading.CancellationToken)
    : Async<Result<DbTransaction option, IngestionError>> =
    async {
        match backend with
        | MySql ->
            let! transaction =
                (connection :?> MyConn).BeginTransactionAsync(IsolationLevel.Unspecified, true, ct).AsTask()
                |> Async.AwaitTask

            return Ok(Some(transaction :> DbTransaction))
        | Oracle ->
            let transaction = connection.BeginTransaction()
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            command.CommandText <- "SET TRANSACTION READ ONLY"
            do! command.ExecuteNonQueryAsync ct |> Async.AwaitTask |> Async.Ignore
            return Ok(Some transaction)
        | SqlServer ->
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Updateability'))"
            let! updateability = command.ExecuteScalarAsync ct |> Async.AwaitTask

            match string updateability with
            | "READ_ONLY" -> return Ok None
            | other ->
                return
                    Error(
                        SchemaMismatch
                            $"ConnectionScope key '%s{SourcePosture.ReadOnlyKey}' cannot be honoured: SQL Server offers no read-only session, and the database this connection reached is %s{other}. Point the connection at a readable secondary or a READ_ONLY database (ApplicationIntent=ReadOnly is set), or drop the key and connect with a login that cannot write"
                    )
        | PostgreSql
        | Sqlite
        | ClickHouse -> return Ok None
    }

/// Whether the database refused `ex`'s statement because the session or
/// the database is read-only.
let private isWriteRefusal (ex: DbException) : bool =
    match ex with
    | :? global.Microsoft.Data.Sqlite.SqliteException as e -> e.SqliteErrorCode = 8 // SQLITE_READONLY
    | :? global.Microsoft.Data.SqlClient.SqlException as e -> e.Number = 3906
    | :? global.Oracle.ManagedDataAccess.Client.OracleException as e -> e.Number = 1456
    | :? global.ClickHouse.Client.ClickHouseServerException as e -> e.ErrorCode = 164 // READONLY
    | :? global.MySqlConnector.MySqlException as e -> e.Number = 1792
    // PostgreSQL's read_only_sql_transaction, which MySQL reports too.
    | _ -> ex.SqlState = "25006"

type private SqlDataSourceImpl(kind: string, secretStore: ISecretStore option) =

    /// Refuse statement text, before anything is sent, on a source whose
    /// policy is `bound-only`. A bare identifier is always admitted: it is
    /// expanded to a whole-table select by the connector itself.
    let admitText (context: string) (ctx: DataSourceCallContext) (text: string) : Result<unit, IngestionError> =
        if SqlDialect.isSafeIdentifier text then
            Ok()
        else
            SourcePosture.read ctx.Config.ConnectionScope
            |> Result.bind (fun posture ->
                match posture.StatementPolicy with
                | StatementPolicy.BoundOnly -> Error(SourcePosture.rawTextRefused context ctx.Config.Id)
                | StatementPolicy.RawAllowed -> Ok())

    let prepare (ctx: DataSourceCallContext) : Async<Result<Opened, IngestionError>> = async {
        let read =
            readSettings ctx.Config.ConnectionScope
            |> Result.bind (fun settings ->
                SourcePosture.read ctx.Config.ConnectionScope
                |> Result.map (fun posture -> settings, posture))

        match read with
        | Error err -> return Error err
        | Ok(settings, posture) ->
            // The credential is OPTIONAL for this connector — a
            // deployment may carry the whole connection string
            // (including integrated security or a password) in
            // `ConnectionScope`, in which case there is no secret to
            // resolve and a missing one is not an error.
            let! password = Credentials.resolveOptional secretStore ctx
            let composed = composeConnectionString settings.ConnectionString password

            let connectionString =
                if posture.ReadOnly then
                    readOnlyConnectionString settings.Backend composed
                else
                    Ok composed

            match connectionString with
            | Error err -> return Error err
            | Ok connectionString ->
                let connection = createConnection settings.Backend connectionString
                let! ct = Async.CancellationToken

                try
                    do! connection.OpenAsync ct |> Async.AwaitTask

                    let! transaction =
                        if posture.ReadOnly then
                            openReadOnly settings.Backend connection ct
                        else
                            async { return Ok None }

                    match transaction with
                    | Error err ->
                        connection.Dispose()
                        return Error err
                    | Ok transaction ->
                        return
                            Ok {
                                Settings = settings
                                Posture = posture
                                Connection = connection
                                Transaction = transaction
                            }
                with ex ->
                    connection.Dispose()
                    return Error(Errors.classify $"%s{kind} (%s{SqlDialect.backendName settings.Backend}) connect" ex)
    }

    let newCommand (opened: Opened) (sql: string) =
        let command = opened.Connection.CreateCommand()
        command.CommandText <- sql
        command.CommandType <- CommandType.Text

        match opened.Transaction with
        | Some transaction -> command.Transaction <- transaction
        | None -> ()

        match opened.Settings.CommandTimeoutSeconds with
        | Some seconds -> command.CommandTimeout <- seconds
        | None -> ()

        command

    /// A command running `statement`, each value bound through the
    /// provider's own parameter. `timeout` overrides the source's default.
    let newBoundCommand (opened: Opened) (timeout: TimeSpan option) (statement: BackendStatement) =
        let command = newCommand opened statement.Text

        match command with
        | :? OraCommand as oracle -> oracle.BindByName <- true
        | _ -> ()

        match timeout with
        | Some span -> command.CommandTimeout <- max 1 (int (Math.Ceiling span.TotalSeconds))
        | None -> ()

        for binding in statement.Bindings do
            command.Parameters.Add(providerParameter opened.Settings.Backend binding)
            |> ignore

        command

    /// Run a catalogue query, projecting each row with `project`.
    let readCatalogue
        (ctx: DataSourceCallContext)
        (context: string)
        (buildStatement: SqlSourceSettings -> Result<BoundStatement, IngestionError>)
        (project: DbDataReader -> 'T)
        : Async<Result<'T list, IngestionError>> =
        Errors.guard context (fun () -> async {
            match! prepare ctx with
            | Error err -> return Error err
            | Ok opened ->
                use opened = opened

                // The catalogue statements are the connector's own, so a
                // translation refusal is a defect in them, never the caller's.
                let translated =
                    buildStatement opened.Settings
                    |> Result.bind (fun statement ->
                        SqlBinding.translate opened.Settings.Backend statement
                        |> Result.mapError (fun refusal ->
                            UnexpectedFailure $"%s{context}: %s{BoundStatementError.describe refusal}"))

                match translated with
                | Error err -> return Error err
                | Ok statement ->
                    let! ct = Async.CancellationToken
                    use command = newBoundCommand opened None statement
                    use! reader = command.ExecuteReaderAsync ct |> Async.AwaitTask

                    let rows = ResizeArray<'T>()
                    let mutable go = true

                    while go do
                        let! hasRow = reader.ReadAsync ct |> Async.AwaitTask

                        if hasRow then rows.Add(project reader) else go <- false

                    return Ok(List.ofSeq rows)
        })

    /// The catalogue's description of a bare table name.
    let catalogueSchema (ctx: DataSourceCallContext) (table: string) = async {
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


    /// The schema of a statement's result set, read without
    /// fetching rows (`CommandBehavior.SchemaOnly`). A column whose
    /// nullability the provider cannot say is recorded as nullable.
    let describeStatement (ctx: DataSourceCallContext) (statement: string) =
        Errors.guard $"%s{kind} GetSchema" (fun () -> async {
            match! prepare ctx with
            | Error err -> return Error err
            | Ok opened ->
                use opened = opened
                let! ct = Async.CancellationToken
                use command = newCommand opened statement

                try
                    use! reader = command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct) |> Async.AwaitTask

                    let columns =
                        reader.GetColumnSchema()
                        |> Seq.map (fun column ->
                            let nativeType =
                                if isNull column.DataTypeName then
                                    ""
                                else
                                    column.DataTypeName

                            let nullable =
                                if column.AllowDBNull.HasValue then
                                    column.AllowDBNull.Value
                                else
                                    true

                            TypeMap.column column.ColumnName nativeType nullable)

                    return Ok(TypeMap.schema statement columns)
                with ProviderException(ex: DbException) ->
                    return
                        Error(
                            SchemaMismatch
                                $"%s{kind} (%s{SqlDialect.backendName opened.Settings.Backend}) could not describe the statement: %s{ex.Message}"
                        )
        })

    /// Run `command` and drain its result set into CSV. The connection
    /// opened, so the source is reachable: a provider failure here is the
    /// statement or the schema, which is the operator's to fix, and a write
    /// a read-only source's database refused is named as that.
    let readCsv (ctx: DataSourceCallContext) (opened: Opened) (command: DbCommand) = async {
        let! ct = Async.CancellationToken
        let context = $"%s{kind} (%s{SqlDialect.backendName opened.Settings.Backend})"

        try
            use! reader = command.ExecuteReaderAsync ct |> Async.AwaitTask
            let! bytes = Csv.ofReader reader
            return Ok bytes
        with ProviderException(ex: DbException) ->
            if opened.Posture.ReadOnly && isWriteRefusal ex then
                return Error(SourcePosture.writeRefused context ctx.Config.Id ex.Message)
            else
                return Error(SchemaMismatch $"%s{context} query failed: %s{ex.Message}")
    }

    /// Run a bound statement. The kinds the source's backend binds, the
    /// markers and the one-statement rule are checked before any connection
    /// opens; the values are bound through the provider's own parameters.
    let queryBound (ctx: DataSourceCallContext) (statement: BoundStatement) = async {
        match readSettings ctx.Config.ConnectionScope with
        | Error err -> return Error(BoundStatementError.Failed err)
        | Ok settings ->
            let translated =
                BoundStatement.check (SqlBinding.bindableKinds settings.Backend) statement
                |> Result.mapError BoundStatementError.Refused
                |> Result.bind (fun () -> SqlBinding.translate settings.Backend statement)

            match translated with
            | Error refusal -> return Error refusal
            | Ok backendStatement ->
                let! result =
                    Errors.guard $"%s{kind} QueryBound" (fun () -> async {
                        match! prepare ctx with
                        | Error err -> return Error err
                        | Ok opened ->
                            use opened = opened
                            use command = newBoundCommand opened statement.Timeout backendStatement
                            return! readCsv ctx opened command
                    })

                return result |> Result.mapError BoundStatementError.Failed
    }

    /// Every kind a bound value can carry: which of them a source binds is
    /// its backend's (`SqlBinding.bindableKinds`), read per call, and the
    /// rest are refused by name.
    let binds =
        { new IBindsStatements with
            member _.BindableKinds = Set.ofList BoundValue.kinds
            member _.QueryBound(ctx, statement) = queryBound ctx statement
        }

    // `Query` emits RFC 4180 CSV; declared, not implied.
    interface IDeclaresPayloadFormat with
        member _.PayloadFormat = PayloadFormat.Csv

    // OPTIONAL: `ConnectionScope` may carry the whole connection string
    // (integrated security or an inline password), and then there is no
    // secret to resolve.
    interface IDeclaresCredentialNeed with
        member _.CredentialNeed = CredentialNeed.Optional

    interface IDataSource with
        member _.Kind = kind

        // A query against a SQL database has no location a delivery can watch,
        // and it emits only the CSV it declares; it binds a statement's values.
        member _.Capabilities = {
            ConnectorCapabilities.none with
                Binds = Some binds
        }

        member _.Connect(ctx) =
            Errors.guard $"%s{kind} Connect" (fun () -> async {
                match! prepare ctx with
                | Error err -> return Error err
                | Ok opened ->
                    use _ = opened
                    return Ok()
            })

        member _.ListTables(ctx) =
            readCatalogue
                ctx
                $"%s{kind} ListTables"
                (fun settings -> SqlDialect.listTablesSql settings.Backend settings.Schema)
                (fun reader -> reader.GetValue 0 |> Csv.renderValue)

        // A statement-shaped "table" (what `Query` passes
        // through verbatim) is described by its result set, read with
        // `CommandBehavior.SchemaOnly`, so a statement ingested under a
        // caller label keeps its schema record and drift detection. A bare
        // identifier reads the catalogue exactly as before. A `bound-only`
        // source describes no statement text.
        member _.GetSchema(ctx, table) =
            if not (SqlDialect.isSafeIdentifier table) then
                match admitText $"%s{kind} GetSchema" ctx table with
                | Error err -> async { return Error err }
                | Ok() -> describeStatement ctx table
            else
                catalogueSchema ctx table

        member _.Query(ctx, sql) =
            match admitText $"%s{kind} Query" ctx sql with
            | Error err -> async { return Error err }
            | Ok() ->
                Errors.guard $"%s{kind} Query" (fun () -> async {
                    match! prepare ctx with
                    | Error err -> return Error err
                    | Ok opened ->
                        use opened = opened

                        match resolveStatement opened.Settings sql with
                        | Error err -> return Error err
                        | Ok statement ->
                            use command = newCommand opened statement
                            return! readCsv ctx opened command
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

/// The posture of each configured SQL source, read at start-up: what it
/// may be asked (`statement_policy`) and whether it opens read-only
/// (`read_only`). Pure: it reads the configurations it is given and
/// reaches no database.
[<RequireQualifiedAccess>]
module SourcePosturePreflight =

    /// The validator's name in the preflight log and summaries.
    [<Literal>]
    let ValidatorName = "sql-source-posture"

    let private message (err: IngestionError) =
        match err with
        | CredentialMissing key -> $"credential '%s{key}' is missing"
        | SourceUnreachable message
        | SchemaMismatch message
        | IngestionError.StorageFailure message
        | UnexpectedFailure message -> message

    /// Whether the preflight warns about `source`: it admits raw statement
    /// text and may write, or its posture keys do not read.
    let flagged (source: DataSourceConfig) : bool =
        match SourcePosture.read source.ConnectionScope with
        | Ok posture -> SourcePosture.admitsRawAndMayWrite posture
        | Error _ -> true

    /// One line per source: its id, its policy and its intent.
    let lines (sources: DataSourceConfig list) : string list =
        sources
        |> List.map (fun source ->
            match SourcePosture.read source.ConnectionScope with
            | Ok posture when SourcePosture.admitsRawAndMayWrite posture ->
                $"source '%s{source.Id}': %s{SourcePosture.describe posture} (admits raw statement text and may write)"
            | Ok posture -> $"source '%s{source.Id}': %s{SourcePosture.describe posture}"
            | Error err -> $"source '%s{source.Id}': posture unreadable: %s{message err}")

    /// `Ok` when no source is flagged, and a `Warning` naming each flagged
    /// one otherwise. Never `Error`: a posture is the deployment's to choose.
    let result (sources: DataSourceConfig list) : ConfigValidation.ValidationResult =
        match sources |> List.filter flagged with
        | [] -> ConfigValidation.ValidationResult.Ok
        | open' ->
            let named =
                open' |> List.map (fun source -> $"'%s{source.Id}'") |> String.concat ", "

            ConfigValidation.ValidationResult.Warning
                $"%d{open'.Length} of %d{sources.Length} SQL source(s) admit raw statement text and may write, or declare an unreadable posture: %s{named}. Set %s{SourcePosture.StatementPolicyKey}=bound-only or %s{SourcePosture.ReadOnlyKey}=true in a source's ConnectionScope to close it"

/// The preflight validator over a deployment's SQL sources. It hands
/// `report` one line per source (the startup logger at Info is the usual
/// target) and warns about each source that admits raw statement text and
/// may write. A deployment with no sources reports nothing and passes.
/// Structural class: it reads only the configurations it was given, so it
/// runs even when `SkipPreflight` is set, and it never refuses a start.
type SourcePosturePreflight(sources: DataSourceConfig list, report: string -> unit) =

    interface ConfigValidation.IConfigValidator with
        member _.Name = SourcePosturePreflight.ValidatorName
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            match sources with
            | [] -> return ConfigValidation.ValidationResult.Ok
            | _ ->
                let lines = SourcePosturePreflight.lines sources
                report (String.concat "\n  " ($"SQL source posture, %d{sources.Length} source(s):" :: lines))
                return SourcePosturePreflight.result sources
        }

    interface ConfigValidation.IStructuralClassValidator