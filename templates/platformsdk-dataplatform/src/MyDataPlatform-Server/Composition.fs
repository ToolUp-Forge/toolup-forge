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
        FactStore = EnabledFactStore
        PublicRendering = EnabledPublicRendering(ContentRoot contentRoot)
        DataIngestion = EnabledDataIngestion
        JobScheduler = InProcessJobScheduler
        EntityStore = EnabledEntityStore
}

/// The application: a RAG app (AI + retrieval + the knowledge base) with the
/// fact tier composed in one call, the trading module, the grounded daily
/// report, and the report pages on the same pipeline. It ends in a
/// `ServerApp`; the caller runs it.
let build (deps: Dependencies) : ServerApp =
    let app =
        RAGServerApp.create deps.AIProviderFactory deps.ProviderProfile deps.Embedder
        |> RAGServerApp.withConfig deps.Config
        |> RAGServerApp.withStorage deps.Storage
        |> RAGServerApp.mapAI (fun ai -> {
            ai with
                Base = ServerApp.withSecretStore deps.Secrets ai.Base
        })
        |> RAGServerApp.withVectorStore (ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.create deps.Storage None)
        |> RAGServerApp.withAuth deps.Auth
        |> RAGServerApp.withFacts FactsCompose.withFactTier
        |> RAGServerApp.mapAI ToolUp.KnowledgeBase.Server.AICompose.register
        |> RAGServerApp.addModules [
            DailyTrade.Server.serverModule ReferenceData.fiscalWeek [ Narratives.DailyReport ]
            // The knowledge base's AI tools name this module id as their source.
            ServerModule.create "ToolUp.KnowledgeBase"
            |> ServerModule.withGuardedApi KnowledgeBase.Server.knowledgeApi
        ]
        |> composeRAG
        |> Narratives.compose
        |> PublishedPages.captureServices

    let app =
        match deps.SignIn with
        | Some signIn -> app |> OidcSsrSignIn.withInteractiveSignIn (new HttpClient()) signIn
        | None -> app

    app |> PublicRenderingCompose.withPublicRendering PublishedPages.install