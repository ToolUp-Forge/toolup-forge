module MyDataPlatform.Server.Composition

open System.Net.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI
open ToolUp.Facts
open ToolUp.RAG.RAGCompose
open ToolUp.PublicRendering
open ToolUp.AuthProviders

/// Everything the composition is handed rather than choosing itself, so the
/// end-to-end test composes exactly the same application around a scripted
/// model and a temporary directory.
type Dependencies = {
    Config: ServerConfig
    Storage: IBlobStorage
    Secrets: ISecretStore
    ProviderProfile: IProviderProfile
    AIProviderFactory: IAIProviderFactory
    Embedder: IEmbeddingProvider
    Auth: IAuthProvider
    /// Interactive sign-in for the gated pages, when the deployment uses an
    /// OIDC provider: a browser following a link with no credential is sent
    /// to sign in rather than answered 401.
    SignIn: OidcSsrSignIn.OidcSsrSignInConfig option
}

/// The server's settings over `config`: a team deployment with the fact
/// tier, published pages, data ingestion, the in-process job scheduler and
/// the entity store on.
let configure (config: ServerConfig) (contentRoot: string) : ServerConfig = {
    config with
        Surfaces = Surfaces.team
        Stores = {
            config.Stores with
                FactStore = FeatureMode.Enabled
                EntityStore = FeatureMode.Enabled
        }
        PublicRendering = EnabledPublicRendering(ContentRoot contentRoot)
        DataIngestion = FeatureMode.Enabled
        JobScheduler = InProcessJobScheduler
}

/// The application: a RAG app (AI + retrieval + the knowledge base) with the
/// fact tier composed in one call, the trading module, the grounded daily
/// report, and the report pages on the same pipeline. It ends in a
/// `ServerApp`; the caller runs it.
let build (deps: Dependencies) : ServerApp =
    let app =
        ServerApp.empty
        |> ServerApp.withConfig deps.Config
        |> ServerApp.withStorage deps.Storage
        |> ServerApp.withSecretStore deps.Secrets
        |> ServerApp.withAuth deps.Auth
        |> ToolUp.KnowledgeBase.Server.AICompose.register
        |> ServerApp.addModules [
            DailyTrade.Server.serverModule ReferenceData.fiscalWeek [ Narratives.DailyReport ]
            // The knowledge base's AI tools name this module id as their source.
            ServerModule.create "ToolUp.KnowledgeBase"
            |> ServerModule.withGuardedApi KnowledgeBase.Server.knowledgeApi
        ]
        |> withRAG (
            RagOptions.create deps.AIProviderFactory deps.ProviderProfile deps.Embedder
            |> RagOptions.withVectorStore (ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.create deps.Storage None)
            |> RagOptions.withFacts FactsCompose.withFactTier
        )
        |> Narratives.compose
        |> PublishedPages.captureServices

    let app =
        match deps.SignIn with
        | Some signIn -> app |> OidcSsrSignIn.withInteractiveSignIn (new HttpClient()) signIn
        | None -> app

    app
    |> PublicRenderingCompose.withPublicRendering (
        PublicRenderingOptions.create () |> PublishedPages.install deps.Config
    )