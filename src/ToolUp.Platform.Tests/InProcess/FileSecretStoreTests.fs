module ToolUp.Platform.Tests.InProcess.FileSecretStoreTests

open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Config
open ToolUp.Platform.Testing

/// Bind the `ISecretStore` contract test pack to `FileSecretStore`.
/// Each factory call creates a unique temp directory and constructs a
/// store targeting it — the optional `baseDir` ctor param lets tests
/// avoid cwd manipulation and run in parallel without collisions. The
/// factory has no teardown hook, so each directory outlives its case;
/// the family name attributes it in the census.
let tests =
    let factory () =
        let dir = TempDir.create "secret-test"
        FileSecretStore.FileSecretStore(baseDir = dir.Path) :> ISecretStore

    ISecretStoreContract.tests "FileSecretStore" factory