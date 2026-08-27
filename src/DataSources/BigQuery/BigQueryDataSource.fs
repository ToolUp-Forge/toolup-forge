// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.BigQuery.BigQueryDataSource

open System
open Google.Apis.Auth.OAuth2
open Google.Cloud.BigQuery.V2
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources.Common
open DataManagementTypes

// ─── ToolUp.DataSources.BigQuery ──────────────────────────────────
//
// `IDataSource` companion for Google BigQuery.
//
// **Production-ready, not dev-only.** No state is held between calls:
// a `BigQueryClient` is built per call from the credential resolved
// through the `ISecretStore` thunk, so a rotated service-account key
// takes effect without reconstructing the connector (portability rule
// 4). BigQuery clients are cheap; pooling is a profiling-driven
// follow-up, not a correctness concern.
//
// **Cost control is the operator's lever and the connector surfaces
// it.** BigQuery bills per byte SCANNED, and `SELECT *` on a
// partitioned fact table is the classic way to spend a lot of money
// by accident. `maximum_bytes_billed` in `ConnectionScope` sets the
// hard ceiling — a query that would exceed it FAILS instead of
// billing — and the README recommends setting it on every source.
//
// **Query output is RFC 4180 CSV** with a header row (see
// `ToolUp.DataSources.Common.Csv`). `sql` is BigQuery Standard SQL
// unless `use_legacy_sql = true`.

/// Parsed, validated view of one BigQuery source's `ConnectionScope`.
type BigQuerySourceSettings = {
    /// GCP project the queries are BILLED to. Not necessarily the
    /// project owning the data — a cross-project read bills the
    /// querying project.
    ProjectId: string
    /// Dataset that `ListTables` / `GetSchema` enumerate and that
    /// unqualified table names in `Query` resolve against.
    DatasetId: string
    /// Dataset location ("EU", "us-central1"). `None` lets the
    /// service infer it, which fails on a location mismatch — set it
    /// explicitly for a non-US dataset.
    Location: string option
    /// Hard ceiling on bytes scanned per query. A query whose dry-run
    /// estimate exceeds this is REFUSED by the service rather than
    /// billed. `None` means no ceiling.
    MaximumBytesBilled: int64 option
    /// Interpret `sql` as BigQuery Legacy SQL. Defaults to false —
    /// Standard SQL — which is what every modern deployment wants.
    UseLegacySql: bool
    /// Use Google application-default credentials (workload identity,
    /// a metadata server, `GOOGLE_APPLICATION_CREDENTIALS`) instead of
    /// a service-account JSON blob from `ISecretStore`.
    UseDefaultCredentials: bool
}

/// The `DataSourceConfig.Kind` this connector answers to.
[<Literal>]
let Kind = "BigQuery"

/// Read and validate one call's `ConnectionScope`. Pure — no client
/// is built and no credential is resolved.
let readSettings (scope: Map<string, string>) : Result<BigQuerySourceSettings, IngestionError> =
    ConnectionScope.require scope "project_id"
    |> Result.bind (fun projectId ->
        ConnectionScope.require scope "dataset_id"
        |> Result.bind (fun datasetId ->
            ConnectionScope.optionalBool scope "use_legacy_sql"
            |> Result.bind (fun useLegacySql ->
                ConnectionScope.optionalBool scope "use_default_credentials"
                |> Result.bind (fun useDefault ->
                    let maximumBytes =
                        match ConnectionScope.optional scope "maximum_bytes_billed" with
                        | None -> Ok None
                        | Some raw ->
                            match
                                Int64.TryParse(
                                    raw,
                                    Globalization.NumberStyles.Integer,
                                    Globalization.CultureInfo.InvariantCulture
                                )
                            with
                            | true, value when value > 0L -> Ok(Some value)
                            | true, value ->
                                Error(
                                    SchemaMismatch
                                        $"ConnectionScope key 'maximum_bytes_billed' must be positive; got %d{value}"
                                )
                            | false, _ ->
                                Error(
                                    SchemaMismatch
                                        $"ConnectionScope key 'maximum_bytes_billed' is not an integer: '%s{raw}'"
                                )

                    maximumBytes
                    |> Result.map (fun maximumBytes -> {
                        ProjectId = projectId
                        DatasetId = datasetId
                        Location = ConnectionScope.optional scope "location"
                        MaximumBytesBilled = maximumBytes
                        UseLegacySql = defaultArg useLegacySql false
                        UseDefaultCredentials = defaultArg useDefault false
                    })))))

/// Per-BigQuery-type overrides in front of the shared ANSI table.
/// BigQuery's own spellings are mostly ANSI-shaped; what needs saying
/// is that the structured types render as text in CSV, and that
/// `BIGNUMERIC` / `NUMERIC` are numbers despite carrying no ANSI
/// numeric token beyond "numeric" (which `TypeMap.ansi` does match).
let private overrides (normalised: string) : ColumnType option =
    match normalised with
    | "bool"
    | "boolean" -> Some BooleanColumn
    | "bytes"
    | "geography"
    | "json"
    | "record"
    | "struct"
    | "array"
    | "interval" -> Some StringColumn
    | _ -> None

/// Classify a BigQuery native type name down to the SDK's coarse
/// `ColumnType`. The RAW BigQuery type is what the connector stores on
/// `ColumnInfo.DataType`.
let toColumnType (nativeType: string) : ColumnType = TypeMap.classify overrides nativeType

/// A BigQuery field is nullable unless its mode is `REQUIRED`.
/// `REPEATED` fields are arrays — present, but each element may be
/// absent, so the SDK's coarse `Nullable` reads true.
let nullableFromMode (mode: string) : bool =
    let m = (if isNull mode then "" else mode).Trim().ToUpperInvariant()
    m <> "REQUIRED"

type private BigQueryDataSourceImpl(secretStore: ISecretStore option) =

    let buildClient (ctx: DataSourceCallContext) (settings: BigQuerySourceSettings) = async {
        if settings.UseDefaultCredentials then
            let! credential = GoogleCredential.GetApplicationDefaultAsync() |> Async.AwaitTask
            let! client = BigQueryClient.CreateAsync(settings.ProjectId, credential) |> Async.AwaitTask
            return Ok client
        else
            match! Credentials.resolve secretStore ctx with
            | Error err -> return Error err
            | Ok json ->
                let credential =
                    try
                        Ok(GoogleCredential.FromJson json)
                    with ex ->
                        Error(
                            SchemaMismatch
                                $"BigQuery credential '%s{ctx.Config.CredentialKey}' is not a valid service-account JSON blob: %s{ex.Message}"
                        )

                match credential with
                | Error err -> return Error err
                | Ok credential ->
                    let! client = BigQueryClient.CreateAsync(settings.ProjectId, credential) |> Async.AwaitTask
                    return Ok client
    }

    let withClient
        (ctx: DataSourceCallContext)
        (context: string)
        (body: BigQuerySourceSettings -> BigQueryClient -> Async<Result<'T, IngestionError>>)
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

    interface IDataSource with
        member _.Kind = Kind

        member this.Connect(ctx) =
            withClient ctx "BigQuery Connect" (fun settings client -> async {
                // Cheapest possible authenticated probe: one dataset
                // metadata fetch. Scans no bytes, so it bills nothing.
                let! _ = client.GetDatasetAsync(settings.DatasetId) |> Async.AwaitTask
                return Ok()
            })

        member _.ListTables(ctx) =
            withClient ctx "BigQuery ListTables" (fun settings client -> async {
                let tables =
                    client.ListTables settings.DatasetId
                    |> Seq.map (fun table -> table.Reference.TableId)
                    |> List.ofSeq

                return Ok tables
            })

        member _.GetSchema(ctx, table) =
            withClient ctx "BigQuery GetSchema" (fun settings client -> async {
                let! bqTable = client.GetTableAsync(settings.DatasetId, table) |> Async.AwaitTask

                let columns =
                    match bqTable.Schema with
                    | null -> []
                    | schema ->
                        match schema.Fields with
                        | null -> []
                        | fields ->
                            fields
                            |> Seq.map (fun field ->
                                TypeMap.column field.Name field.Type (nullableFromMode field.Mode))
                            |> List.ofSeq

                return Ok(TypeMap.schema table columns)
            })

        member _.Query(ctx, sql) =
            withClient ctx "BigQuery Query" (fun settings client -> async {
                let! ct = Async.CancellationToken

                let options = QueryOptions()
                options.UseLegacySql <- Nullable settings.UseLegacySql
                options.DefaultDataset <- client.GetDatasetReference(settings.ProjectId, settings.DatasetId)

                match settings.MaximumBytesBilled with
                | Some ceiling -> options.MaximumBytesBilled <- Nullable ceiling
                | None -> ()

                let! results = client.ExecuteQueryAsync(sql, null, options, null, ct) |> Async.AwaitTask

                let header =
                    match results.Schema with
                    | null -> []
                    | schema ->
                        match schema.Fields with
                        | null -> []
                        | fields -> fields |> Seq.map _.Name |> List.ofSeq

                let rows =
                    results
                    |> Seq.map (fun row -> header |> List.map (fun name -> Csv.renderValue row[name]) |> Seq.ofList)

                return Ok(Csv.toBytes header rows)
            })

/// Build the connector with an `ISecretStore` holding the
/// service-account JSON under each source's `CredentialKey`. The
/// store is consulted per call, so a rotated key takes effect without
/// reconstructing the connector.
let create (secretStore: ISecretStore) : IDataSource =
    BigQueryDataSourceImpl(Some secretStore) :> IDataSource

/// Build the connector for a deployment running under Google
/// application-default credentials (workload identity on GKE, the GCE
/// metadata server, `GOOGLE_APPLICATION_CREDENTIALS`). Every source
/// wired to this instance must set
/// `use_default_credentials = true` in its `ConnectionScope`.
let createWithDefaultCredentials () : IDataSource =
    BigQueryDataSourceImpl(None) :> IDataSource