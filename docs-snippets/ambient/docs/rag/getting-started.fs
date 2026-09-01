// Ambient context for `docs/rag/getting-started.md`.
//
// The page enables RAG in one deployment and then walks the surfaces it
// unlocks, so every block is an excerpt from a composition root it never
// shows in full: the substrate a companion's `create` is handed
// (`secretStore`, `blobStorage`, `logger`), the rest of the RAG
// composition (`aiProviderFactory`, `providerProfile`, `serverConfig`,
// `authProvider`, `modules`), the built `IServiceProvider` a running
// deployment resolves out of, and the ids a caller already holds
// (`teamId`, `scopeId`). Two page-local products a LATER block reads
// back — `embedder` from "Wire an embedding provider" and
// `myDataVectorisationHandler` from "Author a custom
// VectorisationHandler" — are declared here as well, because each block
// compiles on its own; the blocks that teach them shadow these.
//
// `processData` / `MyDataEntry` stand in for the module's own domain: a
// vectorisation handler is handed the `ProcessedData` its `DataType.Process`
// produced, and only the module author knows what is inside it.
open Microsoft.Extensions.DependencyInjection
open ProcessedDataTypes
open ToolUp.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EmbeddingProviderEnv
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.Platform.VectorisationTypes
open ToolUp.RAG.IngestionTypes

[<AutoOpen>]
module PageAmbient =

    // ─── Substrate a companion's `create` is handed ───────────────

    let secretStore: ISecretStore = failwith "ambient"

    let blobStorage: IBlobStorage = failwith "ambient"

    let logger: ILogger = failwith "ambient"

    // ─── The rest of the RAG composition root ─────────────────────

    let aiProviderFactory: IAIProviderFactory = failwith "ambient"

    let providerProfile: IProviderProfile = failwith "ambient"

    let serverConfig: ServerConfig = failwith "ambient"

    let authProvider: IAuthProvider = failwith "ambient"

    let modules: ServerModule list = failwith "ambient"

    /// The embedding provider built in "Wire an embedding provider" and
    /// passed to `RAGServerApp.create`; those blocks shadow this.
    let embedder: IEmbeddingProvider = failwith "ambient"

    // ─── A running deployment ─────────────────────────────────────

    /// The built container a background path resolves singletons out of.
    let serviceProvider: IServiceProvider = failwith "ambient"

    /// The event store the retrieval tracer writes through.
    let eventStore: IEventStore = failwith "ambient"

    /// Ids the caller already holds. `scopeId` is the resolved storage
    /// scope — every `IEventStore` read is scope-first.
    let teamId: string = failwith "ambient"

    let scopeId: string = failwith "ambient"

    // ─── The module's own domain ──────────────────────────────────

    /// One record the module's `DataType.Process` produced. Whatever
    /// shape it has is the module author's business, not the SDK's.
    type MyDataEntry = { Description: string; Value: decimal }

    /// The module's own projection of its processed payload — the
    /// vectorisation handler's input, not an SDK call.
    let processData (processed: ProcessedData) : MyDataEntry list = failwith "ambient"

    /// The handler built in "Author a custom `VectorisationHandler`" and
    /// registered by the block after it; that block shadows this.
    let myDataVectorisationHandler: VectorisationHandler = failwith "ambient"