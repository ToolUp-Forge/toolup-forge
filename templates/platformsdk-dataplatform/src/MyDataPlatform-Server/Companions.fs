module MyDataPlatform.Server.Companions

open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI

// ─── The companions the local run selects ─────────────────────────
//
// Each is one line to swap for a production companion: blob storage for a
// cloud object store (`ToolUp.Storage.*`), the encrypted-file secret store
// for a vault (`ToolUp.Secrets.*`). Nothing above this file names an
// implementation.

/// Local-filesystem blob storage under `root`: uploads, fact tables, the
/// vector index and the embedding cache all live here.
let blobStorage (root: string) : IBlobStorage =
    Directory.CreateDirectory root |> ignore
    LocalFileStorage.LocalFileStorage(root) :> IBlobStorage

/// The encrypted-file secret store. It encrypts at rest when
/// `TOOLUP_SECRETS_MASTER_KEY` is set; set it for anything but a throwaway
/// local run.
let secretStore (root: string) : ISecretStore =
    Directory.CreateDirectory root |> ignore

    EncryptedSecretStore.EncryptedSecretStore(
        FileSecretStore.FileSecretStore(baseDir = root) :> ISecretStore,
        EncryptedSecretStore.masterKeyFromEnvironment ()
    )
    :> ISecretStore

/// Each user's or team's AI provider entries, kept in the application's own
/// blob storage.
let providerProfile (storage: IBlobStorage) : IProviderProfile = BlobProviderProfile.create storage

/// Claude, strictly bring-your-own-key: every call resolves the caller's own
/// provider entry, whose key the secret store holds. There is no platform
/// key to fall back on, so an unfunded caller is refused rather than billed
/// to the deployment.
let aiProviderFactory (profile: IProviderProfile) (secrets: ISecretStore) : IAIProviderFactory =
    let claude: AIProviderBuilder = {
        Descriptor = {
            Id = ClaudeAIProvider.ProviderId
            DisplayName = "Claude"
            SupportedModels = ClaudeAIProvider.KnownModels
            DefaultModel = ClaudeAIProvider.DefaultModel
            Capabilities = {
                Streaming = true
                ToolUse = true
                Vision = true
                SupportsPromptCaching = true
                SupportsTriage = true
                TriageModelId = Some ClaudeAIProvider.DefaultModel
                ProviderName = ClaudeAIProvider.ProviderId
                Model = ClaudeAIProvider.DefaultModel
            }
        }
        Build = ClaudeAIProvider.createWithApiKeyAndModel
    }

    DefaultAIProviderFactory.create [ claude ] profile secrets StrictBYOK [] None