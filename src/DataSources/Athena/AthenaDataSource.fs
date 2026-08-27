// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Athena.AthenaDataSource

open System
open Amazon
open Amazon.Athena
open Amazon.Athena.Model
open Amazon.Runtime
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.Common
open DataManagementTypes

// ─── ToolUp.DataSources.Athena ────────────────────────────────────
//
// `IDataSource` companion for AWS Athena — a query engine over data
// already in S3, addressed through a Glue catalogue.
//
// **Production-ready, not dev-only.** Stateless between calls: the
// client is rebuilt per call from credentials resolved through the
// `ISecretStore` thunk (or the ambient AWS credential chain), so a
// rotated key takes effect without reconstructing the connector
// (portability rule 4).
//
// **The async query model maps onto `IDataSource.Query` directly.**
// `StartQueryExecution` returns immediately with an id;
// `GetQueryExecution` is polled until a terminal state; the result
// set is then paged out of `GetQueryResults`. The polling interval
// and the overall ceiling are both configurable, and the ceiling is
// enforced by the CONNECTOR — an ingestion that outruns it fails
// with `SourceUnreachable` rather than hanging a scheduler slot
// forever.
//
// **S3 staging.** Athena writes every result set to
// `output_location` before the connector reads it back. That bucket
// is the same substrate `src/Storage/AwsS3Storage/` composes over —
// point them at the same bucket with different prefixes and one
// lifecycle policy expires both.
//
// **Query output is RFC 4180 CSV** with a header row, which is also
// what Athena natively stages to S3 — so this connector's wire format
// is a re-emission, not a translation.

/// Parsed, validated view of one Athena source's `ConnectionScope`.
type AthenaSourceSettings = {
    /// AWS region in SDK string form ("eu-west-2").
    Region: string
    /// Glue database whose tables `ListTables` / `GetSchema`
    /// enumerate and that unqualified names in `sql` resolve against.
    Database: string
    /// `s3://bucket/prefix/` Athena stages result sets to. Required
    /// unless the workgroup enforces its own output location.
    OutputLocation: string option
    /// Data catalogue. Defaults to `AwsDataCatalog`.
    Catalog: string
    /// Athena workgroup. `None` uses the account's `primary`.
    WorkGroup: string option
    /// How often to poll `GetQueryExecution`. Defaults to 500 ms.
    PollIntervalMs: int
    /// Ceiling on total wait for a query to reach a terminal state.
    /// Defaults to 300 s.
    QueryTimeoutSeconds: int
}

/// The `DataSourceConfig.Kind` this connector answers to.
[<Literal>]
let Kind = "Athena"

[<Literal>]
let private DefaultCatalog = "AwsDataCatalog"

/// Read and validate one call's `ConnectionScope`. Pure.
let readSettings (scope: Map<string, string>) : Result<AthenaSourceSettings, IngestionError> =
    ConnectionScope.require scope "region"
    |> Result.bind (fun region ->
        ConnectionScope.require scope "database"
        |> Result.bind (fun database ->
            ConnectionScope.optionalInt scope "poll_interval_ms"
            |> Result.bind (fun poll ->
                ConnectionScope.optionalInt scope "query_timeout_seconds"
                |> Result.bind (fun timeout ->
                    let pollMs = defaultArg poll 500
                    let timeoutS = defaultArg timeout 300

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
                            Database = database
                            OutputLocation = ConnectionScope.optional scope "output_location"
                            Catalog = ConnectionScope.optionalOr scope "catalog" DefaultCatalog
                            WorkGroup = ConnectionScope.optional scope "workgroup"
                            PollIntervalMs = pollMs
                            QueryTimeoutSeconds = timeoutS
                        }))))

/// Per-Athena/Hive-type overrides in front of the shared ANSI table.
/// Athena reports Hive type names, which spell several things
/// differently from ANSI.
let private overrides (normalised: string) : ColumnType option =
    match normalised with
    | "boolean" -> Some BooleanColumn
    | "tinyint"
    | "smallint"
    | "bigint" -> Some NumberColumn
    | "binary"
    | "varbinary"
    | "string"
    | "char"
    | "varchar"
    | "json"
    | "uuid" -> Some StringColumn
    | "array"
    | "map"
    | "struct"
    | "row" -> Some StringColumn
    | _ -> None

/// Classify an Athena/Hive native type name down to the SDK's coarse
/// `ColumnType`. The RAW name is stored on `ColumnInfo.DataType`.
let toColumnType (nativeType: string) : ColumnType = TypeMap.classify overrides nativeType

/// Build AWS credentials from a JSON credential blob. `None` when no
/// credential is configured, which means "fall through to the AWS
/// default credential chain" — env vars, the shared credentials file,
/// an instance profile, an IRSA/ECS task role. That is how most AWS
/// deployments want it, so an absent credential is NOT an error here.
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

type private AthenaDataSourceImpl(secretStore: ISecretStore option) =

    let buildClient (ctx: DataSourceCallContext) (settings: AthenaSourceSettings) = async {
        let config = AmazonAthenaConfig()
        config.RegionEndpoint <- RegionEndpoint.GetBySystemName settings.Region

        match! Credentials.resolveOptional secretStore ctx with
        | None -> return Ok(new AmazonAthenaClient(config))
        | Some json ->
            match parseAwsCredentials ctx.Config.CredentialKey json with
            | Error err -> return Error err
            | Ok credentials -> return Ok(new AmazonAthenaClient(credentials, config))
    }

    let withClient
        (ctx: DataSourceCallContext)
        (context: string)
        (body: AthenaSourceSettings -> AmazonAthenaClient -> Async<Result<'T, IngestionError>>)
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

    /// Start a query, poll to a terminal state, and return its
    /// execution id — or the failure reason Athena gave.
    let runToCompletion
        (settings: AthenaSourceSettings)
        (client: AmazonAthenaClient)
        (sql: string)
        : Async<Result<string, IngestionError>> =
        async {
            let! ct = Async.CancellationToken

            let request = StartQueryExecutionRequest()
            request.QueryString <- sql
            let context = QueryExecutionContext()
            context.Database <- settings.Database
            context.Catalog <- settings.Catalog
            request.QueryExecutionContext <- context

            match settings.OutputLocation with
            | Some location ->
                let resultConfiguration = ResultConfiguration()
                resultConfiguration.OutputLocation <- location
                request.ResultConfiguration <- resultConfiguration
            | None -> ()

            match settings.WorkGroup with
            | Some workGroup -> request.WorkGroup <- workGroup
            | None -> ()

            let! started = client.StartQueryExecutionAsync(request, ct) |> Async.AwaitTask
            let executionId = started.QueryExecutionId

            let deadline = DateTime.UtcNow.AddSeconds(float settings.QueryTimeoutSeconds)
            let mutable outcome = None

            while outcome.IsNone do
                let poll = GetQueryExecutionRequest()
                poll.QueryExecutionId <- executionId
                let! current = client.GetQueryExecutionAsync(poll, ct) |> Async.AwaitTask
                let state = current.QueryExecution.Status.State

                if state = QueryExecutionState.SUCCEEDED then
                    outcome <- Some(Ok executionId)
                elif state = QueryExecutionState.FAILED then
                    let reason =
                        match current.QueryExecution.Status.StateChangeReason with
                        | null -> "(no reason reported)"
                        | reason -> reason

                    outcome <- Some(Error(SchemaMismatch $"Athena query %s{executionId} FAILED: %s{reason}"))
                elif state = QueryExecutionState.CANCELLED then
                    outcome <- Some(Error(SourceUnreachable $"Athena query %s{executionId} was CANCELLED"))
                elif DateTime.UtcNow > deadline then
                    // Best effort: stop billing for a query nobody is
                    // going to read. A failure to cancel must not mask
                    // the timeout that caused it.
                    try
                        let stop = StopQueryExecutionRequest()
                        stop.QueryExecutionId <- executionId
                        let! _ = client.StopQueryExecutionAsync(stop, ct) |> Async.AwaitTask
                        ()
                    with _ ->
                        ()

                    outcome <-
                        Some(
                            Error(
                                SourceUnreachable
                                    $"Athena query %s{executionId} did not finish within query_timeout_seconds (%d{settings.QueryTimeoutSeconds}s); it was asked to stop"
                            )
                        )
                else
                    do! Async.Sleep settings.PollIntervalMs

            return
                outcome
                |> Option.defaultValue (Error(UnexpectedFailure "Athena poll loop exited without an outcome"))
        }

    /// Page the whole result set out of `GetQueryResults` as CSV.
    let readResults (client: AmazonAthenaClient) (executionId: string) : Async<byte[]> = async {
        let! ct = Async.CancellationToken

        let cellOf (datum: Datum) =
            match datum.VarCharValue with
            | null -> ""
            | value -> value

        let mutable header: string list = []
        let rows = ResizeArray<string list>()
        let mutable nextToken: string = null
        let mutable firstPage = true
        let mutable go = true

        while go do
            let request = GetQueryResultsRequest()
            request.QueryExecutionId <- executionId

            if not (isNull nextToken) then
                request.NextToken <- nextToken

            let! page = client.GetQueryResultsAsync(request, ct) |> Async.AwaitTask

            if firstPage then
                header <-
                    match page.ResultSet.ResultSetMetadata with
                    | null -> []
                    | metadata ->
                        match metadata.ColumnInfo with
                        | null -> []
                        | columns -> columns |> Seq.map _.Name |> List.ofSeq

            let pageRows =
                match page.ResultSet.Rows with
                | null -> []
                | rows ->
                    rows
                    |> Seq.map (fun row -> row.Data |> Seq.map cellOf |> List.ofSeq)
                    |> List.ofSeq

            // Athena repeats the column names as the first row of the
            // FIRST page for SELECT statements, but not for DDL. Drop
            // it only when it actually is the header — comparing is
            // cheaper than being wrong in either direction.
            let bodyRows =
                match firstPage, pageRows with
                | true, first :: rest when first = header -> rest
                | true, rows -> rows
                | false, rows -> rows

            rows.AddRange bodyRows
            firstPage <- false
            nextToken <- page.NextToken
            go <- not (String.IsNullOrEmpty nextToken)

        return Csv.toBytes header (rows |> Seq.map Seq.ofList)
    }

    interface IDataSource with
        member _.Kind = Kind

        member _.Connect(ctx) =
            withClient ctx "Athena Connect" (fun settings client -> async {
                let! ct = Async.CancellationToken
                // Cheapest authenticated probe: one Glue database
                // metadata read. Starts no query, so it stages nothing
                // to S3 and scans nothing.
                let request = GetDatabaseRequest()
                request.CatalogName <- settings.Catalog
                request.DatabaseName <- settings.Database
                let! _ = client.GetDatabaseAsync(request, ct) |> Async.AwaitTask
                return Ok()
            })

        member _.ListTables(ctx) =
            withClient ctx "Athena ListTables" (fun settings client -> async {
                let! ct = Async.CancellationToken
                let names = ResizeArray<string>()
                let mutable nextToken: string = null
                let mutable go = true

                while go do
                    let request = ListTableMetadataRequest()
                    request.CatalogName <- settings.Catalog
                    request.DatabaseName <- settings.Database

                    if not (isNull nextToken) then
                        request.NextToken <- nextToken

                    let! page = client.ListTableMetadataAsync(request, ct) |> Async.AwaitTask

                    match page.TableMetadataList with
                    | null -> ()
                    | tables -> names.AddRange(tables |> Seq.map _.Name)

                    nextToken <- page.NextToken
                    go <- not (String.IsNullOrEmpty nextToken)

                return Ok(List.ofSeq names)
            })

        member _.GetSchema(ctx, table) =
            withClient ctx "Athena GetSchema" (fun settings client -> async {
                let! ct = Async.CancellationToken
                let request = GetTableMetadataRequest()
                request.CatalogName <- settings.Catalog
                request.DatabaseName <- settings.Database
                request.TableName <- table
                let! response = client.GetTableMetadataAsync(request, ct) |> Async.AwaitTask

                let describe (columns: Collections.Generic.List<Column>) =
                    match columns with
                    | null -> []
                    | columns ->
                        columns
                        // Athena's catalogue carries no nullability
                        // flag — Hive columns are nullable unless a
                        // partition key, which never is.
                        |> Seq.map (fun column -> TypeMap.column column.Name column.Type true)
                        |> List.ofSeq

                let partitions =
                    match response.TableMetadata.PartitionKeys with
                    | null -> []
                    | keys ->
                        keys
                        |> Seq.map (fun key -> TypeMap.column key.Name key.Type false)
                        |> List.ofSeq

                return Ok(TypeMap.schema table (describe response.TableMetadata.Columns @ partitions))
            })

        member _.Query(ctx, sql) =
            withClient ctx "Athena Query" (fun settings client -> async {
                match! runToCompletion settings client sql with
                | Error err -> return Error err
                | Ok executionId ->
                    let! bytes = readResults client executionId
                    return Ok bytes
            })

/// Build the connector with an `ISecretStore`. A source whose
/// `CredentialKey` resolves to a JSON access-key blob uses it; a
/// source with no stored credential falls through to the AWS default
/// credential chain, which is what an instance-profile or IRSA
/// deployment wants.
let create (secretStore: ISecretStore) : IDataSource =
    AthenaDataSourceImpl(Some secretStore) :> IDataSource

/// Build the connector for a deployment that authenticates entirely
/// through the AWS default credential chain.
let createWithDefaultCredentials () : IDataSource =
    AthenaDataSourceImpl(None) :> IDataSource