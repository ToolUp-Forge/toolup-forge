module ToolUp.Platform.Tests.InProcess.FileSecretStoreTests

open System
open System.IO
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

/// Bind the `ISecretStore` contract test pack to `FileSecretStore`.
/// Each factory call creates a unique temp directory and constructs a
/// store targeting it — the optional `baseDir` ctor param lets tests
/// avoid cwd manipulation and run in parallel without collisions.
let tests =
    let factory () =
        let dir =
            Path.Combine(Path.GetTempPath(), "toolup-secret-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory dir |> ignore
        FileSecretStore.FileSecretStore(baseDir = dir) :> ISecretStore

    ISecretStoreContract.tests "FileSecretStore" factory