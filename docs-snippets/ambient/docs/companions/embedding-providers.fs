// Ambient context for `docs/companions/embedding-providers.md`.
//
// The page is a cross-cutting tour of the shipped embedding-provider
// companions, so nearly every block is an excerpt from a composition
// root it never shows in full: the `aiProviderFactory` /
// `providerProfile` every `RAGServerApp.create` is threaded, the
// substrate a companion's `create` / `fromEnv` takes (`secretStore` for
// the API-keyed one, `blobStorage` for the persistent local one), the
// `logger` the env-var resolver announces through, the built
// `serviceProvider` the re-embedding queue is resolved from, and the
// Redis connection string / team id a worked example is about.
// Declared here so the blocks compile exactly as a reader would copy
// them, with no `open`-ceremony added to the markdown.
//
// `ToolUp.Platform.EmbeddingProviderEnv` is opened here rather than in
// the markdown for one specific reason: the "Selecting the companion
// from configuration" block writes `EmbeddingProviderResolver` values
// as bare record literals (`{ Name = "local"; Resolve = ... }`), and
// those labels have to be in scope for the literal to resolve. The
// call itself stays spelled `EmbeddingProviderEnv.fromEnv`, which is
// how it reads in a real composition root — `ToolUp.Platform` is open
// there by definition.
open ToolUp.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EmbeddingProviderEnv
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.RAG.RAGCompose

[<AutoOpen>]
module PageAmbient =

    // ─── The deployment's composition root ────────────────────────

    let aiProviderFactory: IAIProviderFactory = failwith "ambient"

    let providerProfile: IProviderProfile = failwith "ambient"

    /// The resolved embedder. Every "Setup" block on the page rebinds
    /// this name with the companion it is teaching; the later wiring
    /// blocks read it as already-resolved.
    let embedder: IEmbeddingProvider = failwith "ambient"

    // ─── Substrate the companions are constructed over ────────────

    /// Where an API-keyed companion reads its key from, per call.
    let secretStore: ISecretStore = failwith "ambient"

    /// Backs `LocalEmbeddingProvider.fromEnv` when the deployment wants
    /// the persistent (IDF-state-carrying) local provider.
    let blobStorage: IBlobStorage = failwith "ambient"

    /// What `EmbeddingProviderEnv.fromEnv` announces the selected
    /// companion through at startup.
    let logger: ILogger = failwith "ambient"

    /// The BUILT provider — `GetRequiredService<ReembeddingQueue>` is a
    /// resolution, not a registration, so this is `IServiceProvider`
    /// rather than the compose-time `IServiceCollection`.
    let serviceProvider: IServiceProvider = failwith "ambient"

    /// The Redis endpoint the distributed embedding cache connects to,
    /// and the team whose scope a model swap re-embeds.
    let connectionString: string = failwith "ambient"

    let teamId: string = failwith "ambient"

    // ─── The page's own hypothetical companion ────────────────────

    /// "Writing a new provider" declares `module MyVendor.EmbeddingProvider`
    /// — a file-level dotted module header, which a generated snippet file
    /// cannot carry (it already declares its own module), so that block
    /// stays `skip=fragment`. The "Wire" block right after it calls into
    /// the module that block would have produced, so the entry point is
    /// declared here: page-local, never an SDK name.
    module MyVendor =

        module EmbeddingProvider =

            let create (secrets: ISecretStore) (model: string) : IEmbeddingProvider = failwith "ambient"