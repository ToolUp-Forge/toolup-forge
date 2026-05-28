module ToolUp.Platform.Tests.InProcess.BlobEntityStoreTests

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Tests.Contracts

/// Bind the IEntityStore contract pack to BlobEntityStore over a
/// LocalFileStorage substrate. Each factory call creates a fresh
/// temp directory + DataObjectStore + EntityRegistry so tests don't
/// share filesystem state.
let tests =
    let factory () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-entity-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let dos = DataObjectStore(blob) :> IDataObjectStore
        let registry = EntityRegistry()
        // Audit log stays None — tests focus on storage behaviour;
        // audit emission is exercised separately when wired up.
        let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, registry, "team-a-" + suffix, "team-b-" + suffix

    IEntityStoreContract.tests "BlobEntityStore (blob-backed)" factory