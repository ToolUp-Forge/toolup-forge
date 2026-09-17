module ToolUp.Platform.Tests.InProcess.BlobEntityStoreTests

open System
open System.IO
open Expecto
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

    /// Phase 806 — the same substrate, composed WITH the audit log the
    /// actor pack hands it.
    let auditedFactory (auditLog: IAuditLog) =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-entity-audit-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let dos = DataObjectStore(blob) :> IDataObjectStore
        let registry = EntityRegistry()
        let store = BlobEntityStore(dos, blob, registry, Some auditLog) :> IEntityStore
        store, registry, "team-audit-" + Guid.NewGuid().ToString("N").Substring(0, 8)

    testList "BlobEntityStore" [
        IEntityStoreContract.tests "BlobEntityStore (blob-backed)" factory
        IEntityStoreContract.auditTests "BlobEntityStore (blob-backed)" auditedFactory
    ]