module ToolUp.Platform.Tests.InProcess.LocalFileStorageTests

open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Testing

/// Bind the `IBlobStorage` contract test pack to `LocalFileStorage`.
/// Each factory call creates a fresh temp directory so tests don't
/// share filesystem state.
let tests =
    let factory () =
        let tempDir = TempDir.forRun "test"
        LocalFileStorage.LocalFileStorage(tempDir.Path) :> IBlobStorage

    IBlobStorageContract.tests "LocalFileStorage" factory