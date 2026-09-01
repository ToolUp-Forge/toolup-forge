// Ambient context for `docs/knowledge-base/extending.md`.
//
// The page teaches how a deployment REPLACES or extends the KB
// companion, so nearly every block is an excerpt from a program it
// never shows in full: the composition root's `aiProviderFactory` /
// `providerProfile` / `embedder` and the `services` collection, the
// replacement module's own Elmish `Model` / `Msg` and page views, and
// the deployment-owned helpers the page names in passing (the EPUB
// parser, the Slack poster, the `ChainedObserver` the page explicitly
// says is five lines you write yourself).
//
// Two of the names here are the reader's own companions rather than
// shipped ones — `CamelotTableExtractor` and `MyConfluenceKb`. They are
// declared for the same reason the rest are: the page presents them as
// something the deployment supplies.
open Feliz
open Microsoft.Extensions.DependencyInjection
open ToolUp.Elmish
open ToolUp.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.ITableExtractor
open ToolUp.Platform.Providers
open ToolUp.Platform.VectorisationTypes
open ToolUp.RAG.IngestionTypes
open ToolUp.RAG.RAGCompose
open ProcessedDataTypes

[<AutoOpen>]
module PageAmbient =

    // ─── The deployment's composition root ────────────────────────

    let aiProviderFactory: IAIProviderFactory = failwith "ambient"

    let providerProfile: IProviderProfile = failwith "ambient"

    let embedder: IEmbeddingProvider = failwith "ambient"

    /// The DI collection the OCR / table-extractor companions register
    /// into, before `composeWithRAG` probes for them.
    let services: IServiceCollection = failwith "ambient"

    /// Substrate the built-in KB observer is constructed over.
    let blobStorage: IBlobStorage = failwith "ambient"

    let notifications: INotificationChannel = failwith "ambient"

    let logger: ILogger = failwith "ambient"

    // ─── The modules the page composes ────────────────────────────

    /// The deployment's KB replacement, as a server module. The
    /// "Replacing the AI Context page" block declares a client-side
    /// binding of the same name and shadows this one.
    let myKnowledgeBaseModule: ServerModule = failwith "ambient"

    /// The built-in KB module and the parallel EPUB module of the
    /// "Wire alongside KB" example.
    let kbModule: ServerModule = failwith "ambient"

    let epubModule: ServerModule = failwith "ambient"

    /// The replacement's observer, built in the first block.
    let myIngestionStatusObserver: IIngestionStatusObserver = failwith "ambient"

    // ─── Client-side composition ──────────────────────────────────

    let clientConfig: ClientConfig = failwith "ambient"

    let modules: ErasedModule list = failwith "ambient"

    /// The deployment's own KB module, registered exactly the way any
    /// `ClientModule.register` does.
    module MyConfluenceKb =
        let register () : ErasedModule = failwith "ambient"

    // ─── The replacement module's own Elmish state ────────────────

    type Model = { Documents: string list }

    type Msg =
        | NoOp
        | IngestionStatusReceived of IngestionStatusUpdate

    let init () : Model * Cmd<Msg> = failwith "ambient"

    let update (msg: Msg) (model: Model) : Model * Cmd<Msg> = failwith "ambient"

    let documentsView (model: Model) (dispatch: Msg -> unit) : PageContent = failwith "ambient"

    let notesView (model: Model) (dispatch: Msg -> unit) : PageContent = failwith "ambient"

    /// Parses the `CustomNotification` payload published under
    /// `SharedTypes.IngestionStatusNotificationKey`.
    let decodeIngestionStatusUpdate (payloadJson: string) : IngestionStatusUpdate option = failwith "ambient"

    // ─── The parallel EPUB module ─────────────────────────────────

    type EpubChapter = { Title: string; Content: string }

    type EpubBook = { Chapters: EpubChapter list }

    let parseEpub (contents: string) : EpubBook = failwith "ambient"

    let serialiseEpub (book: EpubBook) : string = failwith "ambient"

    let deserialiseEpub (payload: string) : EpubBook = failwith "ambient"

    // ─── Table extraction ─────────────────────────────────────────

    /// The reader's own table-extraction companion — the interface is
    /// shipped, an implementation of it is not.
    let pythonSidecar: obj = failwith "ambient"

    type CamelotExtractor(sidecar: obj) =
        interface ITableExtractor with
            member _.Name = failwith "ambient"
            member _.ExtractTables documentBytes mimeType = failwith "ambient"

    module CamelotTableExtractor =
        let create (sidecar: obj) : CamelotExtractor = failwith "ambient"

    // ─── Custom ingestion observers ───────────────────────────────

    let slackWebhookUrl: string = failwith "ambient"

    let postToSlack (webhookUrl: string) (text: string) : Async<unit> = failwith "ambient"

    /// The Slack observer written one block earlier; the page's own
    /// block declares it and shadows this one.
    type SlackOnFailureObserver(webhookUrl: string) =
        interface IIngestionStatusObserver with
            member _.OnChunkIndexed(job) = failwith "ambient"
            member _.OnChunkFailed(job, error) = failwith "ambient"

    /// The five-line fan-out the page says you write yourself. Not
    /// shipped by the SDK — declared here so the composing block
    /// resolves.
    type ChainedObserver(observers: IIngestionStatusObserver list) =
        interface IIngestionStatusObserver with
            member _.OnChunkIndexed(job) = failwith "ambient"
            member _.OnChunkFailed(job, error) = failwith "ambient"

    // ─── Cross-module knowledge surfacing ─────────────────────────

    /// The producing module's own analysis result and its projection
    /// into the narrative shape the commit broker takes.
    type Analysis = { Title: string; Body: string }

    let narrativeOf (analysis: Analysis) : ToolUp.Platform.Narrative.NarrativeDocument = failwith "ambient"