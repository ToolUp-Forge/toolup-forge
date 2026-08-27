// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Redshift.RedshiftDataSource

open System
open Amazon
open Amazon.RedshiftDataAPIService
open Amazon.RedshiftDataAPIService.Model
open Amazon.Runtime
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.Common
open DataManagementTypes

// ─── ToolUp.DataSources.Redshift ──────────────────────────────────
//
// `IDataSource` companion for AWS Redshift over the **Redshift Data
// API**, not a JDBC/ODBC wire connection.
//
// **Why the Data API rather than the Postgres wire.** Redshift speaks
// the Postgres wire protocol, so `ToolUp.DataSources.Sql` with
// `backend = redshift-wire` reaches it too — and for a small,
// low-latency query that is the better connector. The Data API is
// what this one exists for: it needs no VPC route from the
// application to the cluster, it authenticates with IAM rather than a
// database password, and it is asynchronous by design — submit,
// poll, page — which is exactly the shape of a scheduled ingestion
// that may run for minutes. A serverless workgroup has no wire
// endpoint reachable from outside its VPC at all.
//
// **Production-ready, not dev-only.** Stateless between calls; the
// client is rebuilt per call from credentials resolved through the
// `ISecretStore` thunk or the AWS default credential chain
// (portability rule 4).
//
// **Two credentials, and they are not the same thing.** The
// `ISecretStore` credential signs the AWS API call. The DATABASE
// credential is named by `secret_arn` (a Secrets Manager ARN) or
// `db_user` (temporary credentials) in `ConnectionScope` — Redshift
// resolves it itself and it never passes through this process.
//
// **Query output is RFC 4180 CSV** with a header row.

/// Parsed, validated view of one Redshift source's `ConnectionScope`.
type RedshiftSourceSettings = {
    /// AWS region in SDK string form ("us-east-1").
    Region: string
    /// Provisioned cluster identifier. Mutually exclusive with
    /// `WorkgroupName`.
    ClusterIdentifier: string option
    /// Redshift Serverless workgroup. Mutually exclusive with
    /// `ClusterIdentifier`.
    WorkgroupName: string option
    /// Database within the cluster / workgroup.
    Database: string
    /// Database user for temporary-credential auth.
    DbUser: string option
    /// Secrets Manager ARN holding the database credential. Redshift
    /// reads it directly; the value never reaches this process.
    SecretArn: string option
    /// Schema `ListTables` / `GetSchema` scope to. Defaults to
    /// `public`.
    Schema: string
    /// How often to poll `DescribeStatement`. Defaults to 500 ms.
    PollIntervalMs: int
    /// Ceiling on total wait for a statement to reach a terminal
    /// state. Defaults to 300 s.
    QueryTimeoutSeconds: int
}

/// The `DataSourceConfig.Kind` this connector answers to.
[<Literal>]
let Kind = "Redshift"

/// Read and validate one call's `ConnectionScope`. Pure.
let readSettings (scope: Map<string, string>) : Result<RedshiftSourceSettings, IngestionError> =
    ConnectionScope.require scope "region"
    |> Result.bind (fun region ->
        ConnectionScope.require scope "database"
        |> Result.bind (fun database ->
            ConnectionScope.optionalInt scope "poll_interval_ms"
            |> Result.bind (fun poll ->
                ConnectionScope.optionalInt scope "query_timeout_seconds"
                |> Result.bind (fun timeout ->
                    let cluster = ConnectionScope.optional scope "cluster_identifier"
                    let workgroup = ConnectionScope.optional scope "workgroup_name"
                    let dbUser = ConnectionScope.optional scope "db_user"
                    let secretArn = ConnectionScope.optional scope "secret_arn"
                    let pollMs = defaultArg poll 500
                    let timeoutS = defaultArg timeout 300

                    match cluster, workgroup with
                    | None, None ->
                        Error(
                            SchemaMismatch
                                "ConnectionScope must set exactly one of 'cluster_identifier' (provisioned) or 'workgroup_name' (serverless)"
                        )
                    | Some _, Some _ ->
                        Error(
                            SchemaMismatch
                                "ConnectionScope sets both 'cluster_identifier' and 'workgroup_name'; exactly one is allowed"
                        )
                    | Some _, None when dbUser.IsNone && secretArn.IsNone ->
                        // A provisioned cluster has no ambient
                        // identity to fall back on: without one of
                        // these the Data API rejects every call with
                        // a message that does not say which key is
                        // missing.
                        Error(
                            SchemaMismatch
                                "a provisioned cluster needs ConnectionScope 'secret_arn' (Secrets Manager) or 'db_user' (temporary credentials)"
                        )
                    | Some _, None
                    | None, Some _ ->
                        if pollMs <= 0 then
                            Error(
                                SchemaMismatch
                                    $"ConnectionScope key 'poll_interval_ms' must be positive; got %d{pollMs}"
                            )
                        elif timeoutS <= 0 then
                            Error(
                                SchemaMismatch
                                    $"ConnectionScope key 'query_timeout_seconds' must be positive; got %d{timeoutS}"
                            )
                        else
                            Ok {
                                Region = region
                                ClusterIdentifier = cluster
                                WorkgroupName = workgroup
                                Database = database
                                DbUser = dbUser
                                SecretArn = secretArn
                                Schema = ConnectionScope.optionalOr scope "schema" "public"
                                PollIntervalMs = pollMs
                                QueryTimeoutSeconds = timeoutS
                            }))))

/// Per-Redshift-type overrides in front of the shared ANSI table.
/// Redshift is Postgres-derived, so most spellings are ANSI; what
/// needs correcting is the SUPER / HLLSKETCH family and the
/// no-numeric-token integer aliases.
let private overrides (normalised: string) : ColumnType option =
    match normalised with
    | "bool"
    | "boolean" -> Some BooleanColumn
    | "super"
    | "hllsketch"
    | "varbyte"
    | "geometry"
    | "geography" -> Some StringColumn
    | "interval" -> Some DateColumn
    | _ -> None

/// Classify a Redshift native type name down to the SDK's coarse
/// `ColumnType`. The RAW name is stored on `ColumnInfo.DataType`.
let toColumnType (nativeType: string) : ColumnType = TypeMap.classify overrides nativeType

/// Build AWS credentials from a JSON credential blob. Deliberately
/// duplicated rather than shared with the Athena companion —
/// `ToolUp.DataSources.Common` is vendor-free by design (GP 1), and
/// two independently-consumable packages must not depend on each
/// other for fifteen lines.
let parseAwsCredentials (label: string) (json: string) : Result<AWSCredentials, IngestionError> =
    CredentialJson.parseObject label json
    |> Result.bind (fun fields ->
        let accessKey =
            CredentialJson.tryFind fields [ "accessKeyId"; "aws_access_key_id"; "access_key_id" ]

        let secretKey =
            CredentialJson.tryFind fields [ "secretAccessKey"; "aws_secret_access_key"; "secret_access_key" ]

        let sessionToken =
            CredentialJson.tryFind fields [ "sessionToken"; "aws_session_token"; "session_token" ]

        match accessKey, secretKey with
        | Some accessKey, Some secretKey ->
            match sessionToken with
            | Some token -> Ok(SessionAWSCredentials(accessKey, secretKey, token) :> AWSCredentials)
            | None -> Ok(BasicAWSCredentials(accessKey, secretKey) :> AWSCredentials)
        | _ ->
            Error(
                SchemaMismatch
                    $"credential '%s{label}' must carry accessKeyId + secretAccessKey (sessionToken optional); leave the credential unset to use the AWS default credential chain"
            ))

/// Render one Data API `Field` as a CSV cell. The Data API returns a
/// tagged union with one populated arm per value; the boxed probe
/// below is deliberately tolerant of whether the SDK models the value
/// arms as `T` or `Nullable<T>`, because boxing a `Nullable` with no
/// value yields `null` either way.
let renderField (field: Field) : string =
    let isNullArm =
        match box field.IsNull with
        | :? bool as flag -> flag
        | _ -> false

    if isNullArm then
        ""
    else
        let arms: obj list = [
            box field.StringValue
            box field.LongValue
            box field.DoubleValue
            box field.BooleanValue
        ]

        match arms |> List.tryFind (isNull >> not) with
        | Some value -> Csv.renderValue value
        | None ->
            match field.BlobValue with
            | null -> ""
            | blob -> Convert.ToBase64String(blob.ToArray())

type private RedshiftDataSourceImpl(secretStore: ISecretStore option) =

    let buildClient (ctx: DataSourceCallContext) (settings: RedshiftSourceSettings) = async {
        let config = AmazonRedshiftDataAPIServiceConfig()
        config.RegionEndpoint <- RegionEndpoint.GetBySystemName settings.Region

        match! Credentials.resolveOptional secretStore ctx with
        | None -> return Ok(new AmazonRedshiftDataAPIServiceClient(config))
        | Some json ->
            match parseAwsCredentials ctx.Config.CredentialKey json with
            | Error err -> return Error err
            | Ok credentials -> return Ok(new AmazonRedshiftDataAPIServiceClient(credentials, config))
    }

    let withClient
        (ctx: DataSourceCallContext)
        (context: string)
        (body: RedshiftSourceSettings -> AmazonRedshiftDataAPIServiceClient -> Async<Result<'T, IngestionError>>)
        : Async<Result<'T, IngestionError>> =
        Errors.guard context (fun () -> async {
            match readSettings ctx.Config.ConnectionScope with
            | Error err -> return Error err
            | Ok settings ->
                match! buildClient ctx settings with
                | Error err -> return Error err
                | Ok client ->
                    use client = client
                    return! body settings client
        })

    // The Data API repeats the same five routing fields on every
    // request type, and they are plain settable properties rather
    // than a shared base — so each apply* below is the same
    // assignment list against a different request type.
    let applyListDatabases (settings: RedshiftSourceSettings) (request: ListDatabasesRequest) =
        request.Database <- settings.Database

        settings.ClusterIdentifier
        |> Option.iter (fun v -> request.ClusterIdentifier <- v)

        settings.WorkgroupName |> Option.iter (fun v -> request.WorkgroupName <- v)
        settings.DbUser |> Option.iter (fun v -> request.DbUser <- v)
        settings.SecretArn |> Option.iter (fun v -> request.SecretArn <- v)

    let applyListTables (settings: RedshiftSourceSettings) (request: ListTablesRequest) =
        request.Database <- settings.Database
        request.SchemaPattern <- settings.Schema

        settings.ClusterIdentifier
        |> Option.iter (fun v -> request.ClusterIdentifier <- v)

        settings.WorkgroupName |> Option.iter (fun v -> request.WorkgroupName <- v)
        settings.DbUser |> Option.iter (fun v -> request.DbUser <- v)
        settings.SecretArn |> Option.iter (fun v -> request.SecretArn <- v)

    let applyDescribeTable (settings: RedshiftSourceSettings) (table: string) (request: DescribeTableRequest) =
        request.Database <- settings.Database
        request.Schema <- settings.Schema
        request.Table <- table

        settings.ClusterIdentifier
        |> Option.iter (fun v -> request.ClusterIdentifier <- v)

        settings.WorkgroupName |> Option.iter (fun v -> request.WorkgroupName <- v)
        settings.DbUser |> Option.iter (fun v -> request.DbUser <- v)
        settings.SecretArn |> Option.iter (fun v -> request.SecretArn <- v)

    let applyExecute (settings: RedshiftSourceSettings) (sql: string) (request: ExecuteStatementRequest) =
        request.Database <- settings.Database
        request.Sql <- sql

        settings.ClusterIdentifier
        |> Option.iter (fun v -> request.ClusterIdentifier <- v)

        settings.WorkgroupName |> Option.iter (fun v -> request.WorkgroupName <- v)
        settings.DbUser |> Option.iter (fun v -> request.DbUser <- v)
        settings.SecretArn |> Option.iter (fun v -> request.SecretArn <- v)

    let runToCompletion
        (settings: RedshiftSourceSettings)
        (client: AmazonRedshiftDataAPIServiceClient)
        (sql: string)
        : Async<Result<string, IngestionError>> =
        async {
            let! ct = Async.CancellationToken

            let request = ExecuteStatementRequest()
            applyExecute settings sql request
            let! started = client.ExecuteStatementAsync(request, ct) |> Async.AwaitTask
            let statementId = started.Id

            let deadline = DateTime.UtcNow.AddSeconds(float settings.QueryTimeoutSeconds)
            let mutable outcome = None

            while outcome.IsNone do
                let describe = DescribeStatementRequest()
                describe.Id <- statementId
                let! current = client.DescribeStatementAsync(describe, ct) |> Async.AwaitTask

                let status =
                    match box current.Status with
                    | null -> ""
                    | status -> (string status).ToUpperInvariant()

                if status = "FINISHED" then
                    outcome <- Some(Ok statementId)
                elif status = "FAILED" then
                    let reason =
                        match current.Error with
                        | null -> "(no reason reported)"
                        | reason -> reason

                    outcome <- Some(Error(SchemaMismatch $"Redshift statement %s{statementId} FAILED: %s{reason}"))
                elif status = "ABORTED" then
                    outcome <- Some(Error(SourceUnreachable $"Redshift statement %s{statementId} was ABORTED"))
                elif DateTime.UtcNow > deadline then
                    try
                        let cancel = CancelStatementRequest()
                        cancel.Id <- statementId
                        let! _ = client.CancelStatementAsync(cancel, ct) |> Async.AwaitTask
                        ()
                    with _ ->
                        ()

                    outcome <-
                        Some(
                            Error(
                                SourceUnreachable
                                    $"Redshift statement %s{statementId} did not finish within query_timeout_seconds (%d{settings.QueryTimeoutSeconds}s); it was asked to cancel"
                            )
                        )
                else
                    do! Async.Sleep settings.PollIntervalMs

            return
                outcome
                |> Option.defaultValue (Error(UnexpectedFailure "Redshift poll loop exited without an outcome"))
        }

    let readResults (client: AmazonRedshiftDataAPIServiceClient) (statementId: string) : Async<byte[]> = async {
        let! ct = Async.CancellationToken

        let mutable header: string list = []
        let rows = ResizeArray<string list>()
        let mutable nextToken: string = null
        let mutable firstPage = true
        let mutable go = true

        while go do
            let request = GetStatementResultRequest()
            request.Id <- statementId

            if not (isNull nextToken) then
                request.NextToken <- nextToken

            let! page = client.GetStatementResultAsync(request, ct) |> Async.AwaitTask

            if firstPage then
                header <-
                    match page.ColumnMetadata with
                    | null -> []
                    | columns ->
                        columns
                        |> Seq.map (fun column ->
                            match column.Label with
                            | null -> column.Name
                            | label when String.IsNullOrWhiteSpace label -> column.Name
                            | label -> label)
                        |> List.ofSeq

            match page.Records with
            | null -> ()
            | records ->
                records
                |> Seq.iter (fun record -> rows.Add(record |> Seq.map renderField |> List.ofSeq))

            firstPage <- false
            nextToken <- page.NextToken
            go <- not (String.IsNullOrEmpty nextToken)

        return Csv.toBytes header (rows |> Seq.map Seq.ofList)
    }

    interface IDataSource with
        member _.Kind = Kind

        member _.Connect(ctx) =
            withClient ctx "Redshift Connect" (fun settings client -> async {
                let! ct = Async.CancellationToken
                // Cheapest authenticated probe: enumerate databases.
                // Starts no statement, so it consumes no query slot.
                let request = ListDatabasesRequest()
                applyListDatabases settings request
                let! _ = client.ListDatabasesAsync(request, ct) |> Async.AwaitTask
                return Ok()
            })

        member _.ListTables(ctx) =
            withClient ctx "Redshift ListTables" (fun settings client -> async {
                let! ct = Async.CancellationToken
                let names = ResizeArray<string>()
                let mutable nextToken: string = null
                let mutable go = true

                while go do
                    let request = ListTablesRequest()
                    applyListTables settings request

                    if not (isNull nextToken) then
                        request.NextToken <- nextToken

                    let! page = client.ListTablesAsync(request, ct) |> Async.AwaitTask

                    match page.Tables with
                    | null -> ()
                    | tables -> names.AddRange(tables |> Seq.map _.Name)

                    nextToken <- page.NextToken
                    go <- not (String.IsNullOrEmpty nextToken)

                return Ok(List.ofSeq names)
            })

        member _.GetSchema(ctx, table) =
            withClient ctx "Redshift GetSchema" (fun settings client -> async {
                let! ct = Async.CancellationToken
                let columns = ResizeArray<ColumnInfo>()
                let mutable nextToken: string = null
                let mutable go = true

                while go do
                    let request = DescribeTableRequest()
                    applyDescribeTable settings table request

                    if not (isNull nextToken) then
                        request.NextToken <- nextToken

                    let! page = client.DescribeTableAsync(request, ct) |> Async.AwaitTask

                    match page.ColumnList with
                    | null -> ()
                    | list ->
                        list
                        |> Seq.iter (fun column ->
                            // The Data API reports nullability as an
                            // int (1 = nullable) and models it as
                            // `int?` in SDK v4; box-probe so either
                            // shape reads correctly.
                            let nullable =
                                match box column.Nullable with
                                | :? int as flag -> flag <> 0
                                | :? bool as flag -> flag
                                | _ -> true

                            columns.Add(TypeMap.column column.Name column.TypeName nullable))

                    nextToken <- page.NextToken
                    go <- not (String.IsNullOrEmpty nextToken)

                return Ok(TypeMap.schema table (List.ofSeq columns))
            })

        member _.Query(ctx, sql) =
            withClient ctx "Redshift Query" (fun settings client -> async {
                match! runToCompletion settings client sql with
                | Error err -> return Error err
                | Ok statementId ->
                    let! bytes = readResults client statementId
                    return Ok bytes
            })

/// Build the connector with an `ISecretStore`. The credential signs
/// the AWS API call; the DATABASE credential is named by
/// `secret_arn` / `db_user` in each source's `ConnectionScope`.
let create (secretStore: ISecretStore) : IDataSource =
    RedshiftDataSourceImpl(Some secretStore) :> IDataSource

/// Build the connector for a deployment that authenticates entirely
/// through the AWS default credential chain (instance profile, IRSA,
/// ECS task role).
let createWithDefaultCredentials () : IDataSource =
    RedshiftDataSourceImpl(None) :> IDataSource