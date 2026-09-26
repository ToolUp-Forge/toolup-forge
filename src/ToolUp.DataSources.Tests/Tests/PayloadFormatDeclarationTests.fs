// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.PayloadFormatDeclarationTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts
open ToolUp.DataSources.Athena
open ToolUp.DataSources.BigQuery
open ToolUp.DataSources.Redshift
open ToolUp.DataSources.Snowflake
open ToolUp.DataSources.Sql
open ToolUp.DataSources.Synapse
open ToolUp.DataSources.Csv
open ToolUp.DataSources.Excel
open ToolUp.DataSources.Parquet
open ToolUp.DataSources.Tests.Support

// ─── Phase 834 — every connector here declares its payload format ───
//
// Binds the `IDeclaresPayloadFormat` pack to each connector this project
// ships tests for. The declaration is a constant of the connector, so the
// factories construct without credentials, a network or a seeded file —
// the same constructors the connectors' own "instance Kind" tests use.
// All nine render their result sets as RFC 4180 CSV.

let tests =
    let blobs () =
        FakeBlobStorage.InMemoryBlobStorage() :> BlobStorage.IBlobStorage

    testList "IDeclaresPayloadFormat bindings" [
        IDeclaresPayloadFormatContract.tests "AthenaDataSource" PayloadFormat.Csv (fun () ->
            AthenaDataSource.createWithDefaultCredentials ())
        IDeclaresPayloadFormatContract.tests "BigQueryDataSource" PayloadFormat.Csv (fun () ->
            BigQueryDataSource.createWithDefaultCredentials ())
        IDeclaresPayloadFormatContract.tests "RedshiftDataSource" PayloadFormat.Csv (fun () ->
            RedshiftDataSource.createWithDefaultCredentials ())
        IDeclaresPayloadFormatContract.tests "SnowflakeDataSource" PayloadFormat.Csv (fun () ->
            SnowflakeDataSource.createWithKeyFile ())
        IDeclaresPayloadFormatContract.tests "SqlDataSource" PayloadFormat.Csv (fun () ->
            SqlDataSource.createWithoutSecrets ())
        IDeclaresPayloadFormatContract.tests "SynapseDataSource" PayloadFormat.Csv (fun () ->
            SynapseDataSource.createWithDefaultCredentials ())
        IDeclaresPayloadFormatContract.tests "CsvDataSource" PayloadFormat.Csv (fun () ->
            CsvDataSource.create (blobs ()))
        IDeclaresPayloadFormatContract.tests "ExcelDataSource" PayloadFormat.Csv (fun () ->
            ExcelDataSource.create (blobs ()))
        IDeclaresPayloadFormatContract.tests "ParquetDataSource" PayloadFormat.Csv (fun () ->
            ParquetDataSource.create (blobs ()))
    ]