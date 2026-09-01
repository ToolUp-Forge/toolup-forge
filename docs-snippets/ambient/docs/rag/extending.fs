// Ambient context for `docs/rag/extending.md`.
//
// The page is a companion-authoring walkthrough, so each block is one
// file out of a deployment: the substrate a `create` function is handed
// (`secretStore`, an `HttpClient` and its key), the DI collection a
// tracer registers into, and the vendor call each stub stands in for
// (`extractedTables`, `rerankedCandidates`). The two page-local products
// a LATER block reads back — the vendor provider from "Writing a new
// `IEmbeddingProvider`" and the tracer from "Writing a new
// `IRetrievalTracer`" — are declared here as well, because each block
// compiles on its own; the blocks that teach them shadow these.
//
// SDK module opens stay in the BLOCKS, deliberately. `IEmbeddingProvider`,
// `IRetrievalTracer` and their siblings each live in their own module
// under `ToolUp.Platform`, so which one to open is part of what the page
// teaches — it is the fact the page was wrong about before Phase 660.B.
// Only the BCL / DI ceremony is hoisted here.
open System.Net.Http
open System.Net.Http.Json
open Microsoft.Extensions.DependencyInjection
open ToolUp.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.ITableExtractor

[<AutoOpen>]
module PageAmbient =

    /// The substrate a companion's `create` is handed at compose.
    let secretStore: ISecretStore = failwith "ambient"

    let httpClient: HttpClient = failwith "ambient"

    let apiKey: string = failwith "ambient"

    /// The rest of the RAG composition root the "Wire into a consumer"
    /// block pipes into.
    let aiProviderFactory: IAIProviderFactory = failwith "ambient"

    let providerProfile: IProviderProfile = failwith "ambient"

    /// The DI collection a tracer registers into, before the RAG
    /// composition runs.
    let services: IServiceCollection = failwith "ambient"

    /// What the vendor call each stub stands in for would return.
    let extractedTables: ExtractedTable list = failwith "ambient"

    let rerankedCandidates: VectorMatch list = failwith "ambient"

    /// The page's own vendor provider, built in "Writing a new
    /// `IEmbeddingProvider`" and consumed by "Wire into a consumer".
    module MyVendorEmbeddingProvider =
        let create (secretStore: ISecretStore) (model: string) : IEmbeddingProvider = failwith "ambient"

    /// The page's own tracer, built in "Writing a new `IRetrievalTracer`"
    /// and registered by the block after it.
    type DatadogRetrievalTracer(httpClient: HttpClient, apiKey: string) =
        interface IRetrievalTracer with
            member _.Trace _ _ = failwith "ambient"
            member _.Miss _ _ = failwith "ambient"