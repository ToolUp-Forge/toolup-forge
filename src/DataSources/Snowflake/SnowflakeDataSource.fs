// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Snowflake

open System
open System.Data
open System.Data.Common
open Snowflake.Data.Client
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.Common
open DataManagementTypes

// ─── ToolUp.DataSources.Snowflake ─────────────────────────────────
//
// `IDataSource` companion for Snowflake, over the official
// `Snowflake.Data` ADO.NET driver.
//
// **Production-ready, not dev-only.** Stateless between calls: every
// method opens a connection, uses it, and disposes it, and the
// credential is re-read through the `ISecretStore` thunk on EVERY
// call so a rotated password or key takes effect without
// reconstructing the connector (portability rule 4).
//
// **Two authentication modes**, chosen by `authenticator` in
// `ConnectionScope`:
//   • `password` (default) — the `ISecretStore` credential is the
//     user's password.
//   • `snowflake_jwt` — key-pair auth. Prefer `private_key_file` in
//     `ConnectionScope` pointing at a mounted secret; the connector
//     then needs no `ISecretStore` credential at all. Passing the PEM
//     itself as the credential also works, with the caveat in the
//     README.
//
// **Per-warehouse / per-schema routing** is `ConnectionScope` data,
// not connector state: `warehouse`, `database`, `schema` and `role`
// are read per call, so two `DataSourceConfig` records pointing at
// different warehouses share one registered connector.
//
// **Identifier case.** Snowflake folds unquoted identifiers to UPPER
// CASE and stores them that way, so the catalogue queries uppercase
// the schema and table literals they compare against. A source that
// genuinely uses lower-case quoted identifiers must say so with
// `case_sensitive_identifiers = true`.
//
// **Query output is RFC 4180 CSV** with a header row.

/// How the connector authenticates to Snowflake.
type SnowflakeAuth =
    /// The `ISecretStore` credential is the account password.
    | PasswordAuth
    /// Key-pair (JWT) auth.
    | KeyPairAuth

/// Parsed, validated view of one Snowflake source's `ConnectionScope`.
type SnowflakeSourceSettings = {
    /// Account identifier, e.g. `xy12345.eu-west-1` or
    /// `myorg-myaccount`.
    Account: string
    /// Login name.
    User: string
    /// Database — required, because `INFORMATION_SCHEMA` is
    /// per-database in Snowflake and the catalogue queries resolve
    /// against it.
    Database: string
    /// Schema `ListTables` / `GetSchema` scope to. Defaults to
    /// `PUBLIC`.
    Schema: string
    /// Virtual warehouse that runs the queries. `None` uses the
    /// user's default; a user with no default cannot run a query at
    /// all, so setting it is strongly recommended.
    Warehouse: string option
    /// Role to assume. `None` uses the user's default.
    Role: string option
    /// Authentication mode.
    Auth: SnowflakeAuth
    /// Path to a PEM private-key file for `snowflake_jwt`. Preferred
    /// over passing the PEM through `ISecretStore`.
    PrivateKeyFile: string option
    /// Treat `schema` / table names as already correctly cased rather
    /// than uppercasing them for the catalogue comparison.
    CaseSensitiveIdentifiers: bool
    /// Per-command timeout. `None` leaves the driver default.
    CommandTimeoutSeconds: int option
}

/// The catalogue queries, as pure functions over strings — unit-
/// testable without a Snowflake account, and unit-tested in
/// `ToolUp.DataSources.Tests`.
module SnowflakeCatalogue =

    /// Apply Snowflake's unquoted-identifier folding unless the
    /// source declared its identifiers are already correctly cased.
    let foldCase (caseSensitive: bool) (value: string) : string =
        if caseSensitive then value else value.ToUpperInvariant()

    /// SQL listing the tables and views in `schema`, within the
    /// connection's current database.
    let listTablesSql (caseSensitive: bool) (schema: string) : Result<string, IngestionError> =
        SqlIdentifier.require "schema" schema
        |> Result.map (fun schema ->
            let literal = SqlIdentifier.quoteLiteral (foldCase caseSensitive schema)
            $"SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '%s{literal}' ORDER BY TABLE_NAME")

    /// SQL describing one table's columns in ordinal order.
    let columnsSql (caseSensitive: bool) (schema: string) (table: string) : Result<string, IngestionError> =
        SqlIdentifier.require "schema" schema
        |> Result.bind (fun schema ->
            SqlIdentifier.require "table" table
            |> Result.map (fun table ->
                let schemaLiteral = SqlIdentifier.quoteLiteral (foldCase caseSensitive schema)
                let tableLiteral = SqlIdentifier.quoteLiteral (foldCase caseSensitive table)

                $"SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS "
                + $"WHERE TABLE_SCHEMA = '%s{schemaLiteral}' AND TABLE_NAME = '%s{tableLiteral}' "
                + "ORDER BY ORDINAL_POSITION"))

    /// SQL selecting a whole table — the expansion applied when
    /// `Query` is handed a bare identifier.
    let selectAllSql (schema: string) (table: string) : Result<string, IngestionError> =
        SqlIdentifier.require "schema" schema
        |> Result.bind (fun schema ->
            SqlIdentifier.require "table" table
            |> Result.map (fun table -> $"SELECT * FROM \"%s{schema}\".\"%s{table}\""))

    /// `INFORMATION_SCHEMA` reports nullability as `YES` / `NO`.
    let nullableFromToken (token: string) : bool =
        (if isNull token then "" else token).Trim().ToUpperInvariant() = "YES"

    /// Per-Snowflake-type overrides in front of the shared ANSI table.
    let private overrides (normalised: string) : ColumnType option =
        match normalised with
        | "boolean" -> Some BooleanColumn
        // The semi-structured family renders as its JSON text in CSV.
        | "variant"
        | "object"
        | "array"
        | "geography"
        | "geometry"
        | "binary"
        | "varbinary" -> Some StringColumn
        // Snowflake's TIMESTAMP_* variants all carry the `timestamp`
        // token, which the ANSI table already reads as a date; the
        // two that do not are named here.
        | "date"
        | "time" -> Some DateColumn
        | _ -> None

    /// Classify a Snowflake native type name down to the SDK's coarse
    /// `ColumnType`. The RAW name is stored on `ColumnInfo.DataType`.
    let toColumnType (nativeType: string) : ColumnType = TypeMap.classify overrides nativeType

module SnowflakeDataSource =

    /// The `DataSourceConfig.Kind` this connector answers to.
    [<Literal>]
    let Kind = "Snowflake"

    /// Parse the `authenticator` ConnectionScope value.
    let parseAuth (raw: string option) : Result<SnowflakeAuth, IngestionError> =
        match raw with
        | None -> Ok PasswordAuth
        | Some value ->
            match value.Trim().ToLowerInvariant() with
            | "password"
            | "snowflake" -> Ok PasswordAuth
            | "snowflake_jwt"
            | "keypair"
            | "key-pair" -> Ok KeyPairAuth
            | other ->
                Error(
                    SchemaMismatch
                        $"ConnectionScope key 'authenticator' must be one of [password, snowflake_jwt]; got '%s{other}'"
                )

    /// Read and validate one call's `ConnectionScope`. Pure.
    let readSettings (scope: Map<string, string>) : Result<SnowflakeSourceSettings, IngestionError> =
        ConnectionScope.require scope "account"
        |> Result.bind (fun account ->
            ConnectionScope.require scope "user"
            |> Result.bind (fun user ->
                ConnectionScope.require scope "database"
                |> Result.bind (fun database ->
                    parseAuth (ConnectionScope.optional scope "authenticator")
                    |> Result.bind (fun auth ->
                        ConnectionScope.optionalBool scope "case_sensitive_identifiers"
                        |> Result.bind (fun caseSensitive ->
                            ConnectionScope.optionalInt scope "command_timeout_seconds"
                            |> Result.map (fun commandTimeout -> {
                                Account = account
                                User = user
                                Database = database
                                Schema = ConnectionScope.optionalOr scope "schema" "PUBLIC"
                                Warehouse = ConnectionScope.optional scope "warehouse"
                                Role = ConnectionScope.optional scope "role"
                                Auth = auth
                                PrivateKeyFile = ConnectionScope.optional scope "private_key_file"
                                CaseSensitiveIdentifiers = defaultArg caseSensitive false
                                CommandTimeoutSeconds = commandTimeout
                            }))))))

    /// Build the Snowflake connection string. Public so a deployment
    /// can unit-test its own configuration.
    ///
    /// The driver parses a plain `key=value;` list. Values containing
    /// `;` would break that parse, so the builder REJECTS them rather
    /// than emitting a string the driver would mis-split — a password
    /// containing a semicolon is a real thing, and silently truncating
    /// it would present as an authentication failure nobody could
    /// explain.
    let composeConnectionString
        (settings: SnowflakeSourceSettings)
        (credential: string option)
        : Result<string, IngestionError> =
        let parts = ResizeArray<string * string>()
        parts.Add("account", settings.Account)
        parts.Add("user", settings.User)
        parts.Add("db", settings.Database)
        parts.Add("schema", settings.Schema)
        settings.Warehouse |> Option.iter (fun value -> parts.Add("warehouse", value))
        settings.Role |> Option.iter (fun value -> parts.Add("role", value))

        match settings.Auth, settings.PrivateKeyFile, credential with
        | PasswordAuth, _, Some password -> parts.Add("password", password)
        | PasswordAuth, _, None -> ()
        | KeyPairAuth, Some file, _ ->
            parts.Add("authenticator", "snowflake_jwt")
            parts.Add("private_key_file", file)
        | KeyPairAuth, None, Some pem ->
            parts.Add("authenticator", "snowflake_jwt")
            parts.Add("private_key", pem)
        | KeyPairAuth, None, None -> ()

        let offending =
            parts
            |> Seq.filter (fun (_, value) -> not (isNull value) && value.Contains ';')
            |> Seq.map fst
            |> List.ofSeq

        match offending with
        | [] ->
            Ok(
                String.Join(";", parts |> Seq.map (fun (key, value) -> $"%s{key}=%s{value}"))
                + ";"
            )
        | keys ->
            let named = String.Join(", ", keys)

            Error(
                SchemaMismatch $"Snowflake connection values must not contain ';' (the driver splits on it): %s{named}"
            )

    /// A bare identifier handed to `Query` is expanded to
    /// `SELECT * FROM "schema"."table"`. Anything else is passed
    /// through verbatim.
    let resolveStatement (settings: SnowflakeSourceSettings) (sql: string) : Result<string, IngestionError> =
        if SqlIdentifier.isSafe sql then
            SnowflakeCatalogue.selectAllSql
                (SnowflakeCatalogue.foldCase settings.CaseSensitiveIdentifiers settings.Schema)
                (SnowflakeCatalogue.foldCase settings.CaseSensitiveIdentifiers sql)
        else
            Ok sql

    type private SnowflakeDataSourceImpl(secretStore: ISecretStore option) =

        let openConnection (ctx: DataSourceCallContext) (settings: SnowflakeSourceSettings) = async {
            let! ct = Async.CancellationToken
            // The credential is OPTIONAL: key-pair auth with a
            // `private_key_file` needs no stored secret at all.
            let! credential = Credentials.resolveOptional secretStore ctx

            match settings.Auth, settings.PrivateKeyFile, credential with
            | PasswordAuth, _, None -> return Error(CredentialMissing ctx.Config.CredentialKey)
            | KeyPairAuth, None, None -> return Error(CredentialMissing ctx.Config.CredentialKey)
            | PasswordAuth, _, Some _
            | KeyPairAuth, Some _, _
            | KeyPairAuth, None, Some _ ->
                match composeConnectionString settings credential with
                | Error err -> return Error err
                | Ok connectionString ->
                    let connection = new SnowflakeDbConnection()
                    connection.ConnectionString <- connectionString

                    try
                        do! connection.OpenAsync ct |> Async.AwaitTask
                        return Ok(connection :> DbConnection)
                    with ex ->
                        connection.Dispose()
                        return Error(Errors.classify "Snowflake connect" ex)
        }

        let newCommand (settings: SnowflakeSourceSettings) (connection: DbConnection) (sql: string) =
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
            (buildSql: SnowflakeSourceSettings -> Result<string, IngestionError>)
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
                Errors.guard "Snowflake Connect" (fun () -> async {
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
                    "Snowflake ListTables"
                    (fun settings -> SnowflakeCatalogue.listTablesSql settings.CaseSensitiveIdentifiers settings.Schema)
                    (fun reader -> reader.GetValue 0 |> Csv.renderValue)

            member _.GetSchema(ctx, table) = async {
                let! rows =
                    readCatalogue
                        ctx
                        "Snowflake GetSchema"
                        (fun settings ->
                            SnowflakeCatalogue.columnsSql settings.CaseSensitiveIdentifiers settings.Schema table)
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
                            TypeMap.column name nativeType (SnowflakeCatalogue.nullableFromToken nullToken))
                        |> TypeMap.schema table)
            }

            member _.Query(ctx, sql) =
                Errors.guard "Snowflake Query" (fun () -> async {
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
                                with :? DbException as ex ->
                                    return Error(SchemaMismatch $"Snowflake query failed: %s{ex.Message}")
                })

    /// Build the connector with an `ISecretStore`. Under
    /// `authenticator = password` the credential is the password;
    /// under `snowflake_jwt` without a `private_key_file` it is the
    /// PEM. Either is re-read per call, so rotation takes effect
    /// without reconstructing the connector.
    let create (secretStore: ISecretStore) : IDataSource =
        SnowflakeDataSourceImpl(Some secretStore) :> IDataSource

    /// Build the connector for a deployment using key-pair auth with
    /// a mounted `private_key_file`, where there is no stored secret
    /// to resolve.
    let createWithKeyFile () : IDataSource =
        SnowflakeDataSourceImpl(None) :> IDataSource