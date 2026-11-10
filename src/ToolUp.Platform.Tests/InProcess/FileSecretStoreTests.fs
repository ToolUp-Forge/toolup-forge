module ToolUp.Platform.Tests.InProcess.FileSecretStoreTests

open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Config
open ToolUp.Platform.Testing

/// Bind the `ISecretStore` contract test pack to `FileSecretStore`.
/// Each factory call creates a unique temp directory and constructs a
/// store targeting it — the optional `baseDir` ctor param lets tests
/// avoid cwd manipulation and run in parallel without collisions.
let tests =
    let factory () =
        let dir = TempDir.forRun "secret-test"
        FileSecretStore.FileSecretStore(baseDir = dir.Path) :> ISecretStore

    ISecretStoreContract.tests "FileSecretStore" factory