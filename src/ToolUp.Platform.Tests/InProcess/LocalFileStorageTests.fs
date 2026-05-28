module ToolUp.Platform.Tests.InProcess.LocalFileStorageTests

open System
open System.IO
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

/// Bind the `IBlobStorage` contract test pack to `LocalFileStorage`.
/// Each factory call creates a fresh temp directory so tests don't
/// share filesystem state — GUID-suffixed, parent-side cleanup left
/// to the OS (temp dirs are tiny and Windows / Linux reap /tmp on a
/// schedule; avoids a teardown hook that would fire even on test
/// failure and obscure diagnostics).
let tests =
    let factory () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    IBlobStorageContract.tests "LocalFileStorage" factory