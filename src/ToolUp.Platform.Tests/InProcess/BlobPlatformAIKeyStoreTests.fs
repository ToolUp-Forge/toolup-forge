module ToolUp.Platform.Tests.InProcess.BlobPlatformAIKeyStoreTests

// Phase 70 — BlobPlatformAIKeyStore over an in-memory ISecretStore.
// Binds the IPlatformAIKeyStoreContract pack to the default impl.

open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.AI
open ToolUp.AI.BlobPlatformAIKeyStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Testing

let tests =
    let factory () =
        let secretStore = Fakes.TestSecretStore() :> ISecretStore
        create secretStore

    IPlatformAIKeyStoreContract.tests "BlobPlatformAIKeyStore over in-memory ISecretStore" factory