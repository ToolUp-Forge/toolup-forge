// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DatasetStoreTests

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tests.Contracts

// ─── Phase 448 — IDatasetStore conformance ──────────────────────────────
//
// Binds the `IDatasetStoreContract` pack to the blob-backed `BlobDatasetStore`
// over a `DataObjectStore` over `LocalFileStorage` — the full default stack.
// Each factory call gets a fresh temp directory so tests never share state; a
// Parquet companion codec would run the same pack via `createWithCodec`.

let tests =
    let factory () : IDatasetStore =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-ds-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let dataObjects = DataObjectStore(blob) :> IDataObjectStore
        BlobDatasetStore.create dataObjects

    IDatasetStoreContract.tests "BlobDatasetStore (blob-backed, JSON-frame codec)" factory