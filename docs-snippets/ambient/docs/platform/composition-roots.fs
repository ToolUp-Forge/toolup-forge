// Ambient context for `docs/platform/composition-roots.md`.
//
// The page documents the SHAPE of a composition root, so its blocks
// are excerpts of one: they read the deployment's own `Wiring.fs`
// sidecar (the file the page's "What goes in `Wiring.fs`" section
// describes) and the substrate bindings the earlier steps produced.
// Declared here so the excerpts compile as written, with every SDK
// type in the signatures still checked by the gate.
open ToolUp.Platform

[<AutoOpen>]
module PageAmbient =

    /// The deployment's own `Wiring.fs` sidecar. Nothing here is SDK
    /// surface — it is what the page tells a consumer to write.
    module Wiring =

        let secretStoreResolvers: SecretStore.CloudSecretStoreResolver list = []

        let blobStorageResolvers: BlobStorageEnv.CloudBlobStorageResolver list = []

        let notificationResolvers: NotificationChannel.NotificationChannelResolver list = []

        let slowRequestOverrides: Map<string, TimeSpan> = Map.empty

        let providerProfile: ToolUp.Platform.Providers.IProviderProfile = failwith "ambient"

        let aiProviderFactory
            (secretStore: ToolUp.Platform.Secrets.ISecretStore)
            (profile: ToolUp.Platform.Providers.IProviderProfile)
            (blobStorage: ToolUp.Platform.BlobStorage.IBlobStorage)
            : ToolUp.AI.IAIProviderFactory =
            failwith "ambient"

        let allModules: ServerModule list = []

        // The client project's own Wiring.fs sibling contributes these
        // two; they are a different file from the server's, which is
        // why the module list is separately named here.
        let handlers: ClientHandlerRegistry = failwith "ambient"

        let clientModules: ErasedModule list = []

    // ─── Bindings the page's step 1–4 produce, read by the later
    //     "Combining companions on one pipeline" excerpt ────────────

    let logger: ILogger = failwith "ambient"

    let blobStorage: ToolUp.Platform.BlobStorage.IBlobStorage = failwith "ambient"

    let config: ServerConfig = failwith "ambient"

    let factory: ToolUp.AI.IAIProviderFactory = failwith "ambient"

    let providerProfile: ToolUp.Platform.Providers.IProviderProfile = failwith "ambient"

    let aiAssistantConfig: ToolUp.AI.SystemPromptBuilder.AIAssistantServerConfig =
        failwith "ambient"

    let contexts: ToolUp.AI.ModuleAIContext list = []

    let mySchema: ToolUp.Forms.FormSchema.FormSchema = failwith "ambient"

    let myWorkflow: ToolUp.Forms.Workflow.WorkflowDefinition = failwith "ambient"

    let stampJobAction: ToolUp.Forms.IWorkflowEngine.WorkflowAction = failwith "ambient"