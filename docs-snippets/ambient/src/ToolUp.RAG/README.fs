// Ambient context for `src/ToolUp.RAG/README.md`.
//
// The README is a package tour, so its blocks are excerpts from a
// consuming deployment it never shows in full: the server entry point's
// `aiProviderFactory` / `providerProfile` / `config` / `authProvider` /
// `logger` / `blobStorage` / `modules`, the DI collection a custom
// tracer registers into, and the module-side domain shape a
// `VectorisationHandler` reads (`SkuAnalysis`, its `SalesSummary`
// payload and its `DataType`). None of those are SDK types — they are
// what the reader's own program provides.
//
// SDK module opens stay in the BLOCKS. Which module a name lives in is
// part of what a package README teaches, so only the deployment's own
// ceremony is hoisted here.
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.AI
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRagTelemetry
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.Providers
open ToolUp.Platform.VectorisationTypes

[<AutoOpen>]
module RagReadmeAmbient =

    // ─── The consuming deployment's composition root ──────────────

    let aiProviderFactory: IAIProviderFactory = failwith "ambient"

    /// The same factory, under the shorter name the tuning and
    /// auto-vacuum blocks use.
    let factory: IAIProviderFactory = failwith "ambient"

    let providerProfile: IProviderProfile = failwith "ambient"

    /// The embedder a deployment already built — `LocalEmbeddingProvider`
    /// in dev, an API-backed companion in production. The "Wire an
    /// embedding provider" block builds its own and shadows this.
    let embedder: IEmbeddingProvider = failwith "ambient"

    let config: ServerConfig = failwith "ambient"

    let authProvider: IAuthProvider = failwith "ambient"

    let logger: ILogger = failwith "ambient"

    let blobStorage: IBlobStorage = failwith "ambient"

    let modules: ServerModule list = failwith "ambient"

    let moduleAIContexts: ModuleAIContext list = failwith "ambient"

    /// A Prometheus / OTel exporter the deployment supplies. The
    /// interface ships; an exporter does not.
    let customTelemetry: IRagTelemetry = failwith "ambient"

    /// The DI collection a custom `IRetrievalTracer` registers into,
    /// ahead of `RAGServerApp.run`.
    let services: IServiceCollection = failwith "ambient"

    // ─── The chunker's inputs ─────────────────────────────────────

    /// Whatever prose the caller is about to chunk — a page body, a
    /// narrative, a parsed document section.
    let longText: string = failwith "ambient"

    // ─── The module that owns the data type ───────────────────────

    /// One row group of the module's own processed payload. A domain
    /// shape, not an SDK type.
    type SalesRowGroup = {
        Brand: string
        Category: string
        Insights: string
    }

    type SalesSummary = { RowGroups: SalesRowGroup list }

    /// The module's own `DataType.Id` constant, its registered
    /// `DataType`, and the ToolUp.Remoting contract it guards.
    let salesDataTypeId: DataTypeId = failwith "ambient"

    let salesDataType: DataType = failwith "ambient"

    type ISkuAnalysisApi = {
        GetSummary: string -> Async<SalesSummary>
    }

    let skuAnalysisApi: HttpContext -> ISkuAnalysisApi = failwith "ambient"

    /// The module's own reader for the `ProcessedData.Payload` its
    /// `DataType.Process` wrote. Only the module knows that shape.
    let deserialiseSalesSummary (payload: string) : SalesSummary = failwith "ambient"

    /// The handler declared in "Writing a `VectorisationHandler`", as
    /// the composition block one section later reaches it.
    module SkuAnalysis =
        module Server =
            let salesVectorisation: VectorisationHandler = failwith "ambient"