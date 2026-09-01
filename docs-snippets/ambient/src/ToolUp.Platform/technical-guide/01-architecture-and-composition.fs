// Ambient context for `src/ToolUp.Platform/technical-guide/01-architecture-and-composition.md`.
//
// The chapter teaches composition, so almost every block is an excerpt
// from a composition root the page never shows in full: the module's
// own `ClientModel.fs` / `ClientView.fs` (the "Stage 1" registration),
// the deployment's `config` / `authProvider` / `blobStorage` /
// `modules` (the `ServerApp` ladder), the substrate values `compose`
// has already resolved by the time it reaches DI registration, and the
// per-deployment allowlist the `withPreMiddleware` example gates on.
// All of them are the READER's program, not SDK surface, so they stand
// in here and the markdown a reader copies grows no `open`-ceremony.
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open ToolUp.Elmish
open Feliz
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.VectorisationTypes
open ToolUp.Platform.Providers
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.FlagEvaluator
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.RAG.RAGCompose

[<AutoOpen>]
module PageAmbient =

    // ── "Type Erasure for Module Composition" — the module's own tier ──

    /// The module's `ClientModel.fs`: the Elmish `Model` / `Msg` and the
    /// `init` / `update` its registration names.
    module MyModel =

        type Model = { Value: string }

        type Msg = ValueChanged of string

        let init () : Model * Cmd<Msg> = failwith "ambient"

        let update (msg: Msg) (model: Model) : Model * Cmd<Msg> = failwith "ambient"

    /// The module's own single-page view, from `ClientView.fs`. Single-page
    /// views return the `(left, right)` tuple the shell wraps as a
    /// `SplitPanel`.
    let view (model: MyModel.Model) (dispatch: MyModel.Msg -> unit) : ReactElement * ReactElement = failwith "ambient"

    /// The file-manager summary display the module contributes.
    let myDataTypeDisplay: DataTypeDisplay = failwith "ambient"

    // ── "Server Composition" — the deployment's own substrate ──────────

    /// The deployment's `ServerConfig` and the substrate it hands the
    /// `ServerApp` ladder. Every block that starts `ServerApp.empty`
    /// reads these; the CSP / allowlist worked examples shadow `config`
    /// with their own literal, which is why they are declared here
    /// auto-opened rather than flat.
    let config: ServerConfig = failwith "ambient"

    let authProvider: IAuthProvider = failwith "ambient"

    let blobStorage: IBlobStorage = failwith "ambient"

    let modules: ServerModule list = failwith "ambient"

    /// An already-built base the `AIServerApp.createFrom` block layers on.
    let baseServerApp: ServerApp = failwith "ambient"

    /// The AI / RAG substrate the two upper tiers take at construction.
    let aiProviderFactory: IAIProviderFactory = failwith "ambient"

    let providerProfile: IProviderProfile = failwith "ambient"

    let embeddingProvider: IEmbeddingProvider = failwith "ambient"

    // ── The `ServerModule` worked example ─────────────────────────────

    /// One module's ToolUp.Remoting contract — the reader's own record of
    /// functions, not an SDK type.
    type SkuAnalysisApi = { GetSkus: unit -> Async<string list> }

    /// What the composition root passes to `ServerModule.withGuardedApi`:
    /// a per-request factory the SDK wraps in `makePermissionGuardedApi`.
    let apiFactory: HttpContext -> SkuAnalysisApi = failwith "ambient"

    let salesDataType: DataType = failwith "ambient"

    let embeddingHandler: VectorisationHandler = failwith "ambient"

    let configSchema: ModuleConfigSchema = failwith "ambient"

    // ── "Post-SAFE composition pipeline" — what `compose` has resolved ──
    //
    // The DI excerpt is a slice out of the middle of `compose`, by which
    // point every one of these is a local it resolved earlier. They are
    // SDK-typed on purpose (README rule 2): the whole value of checking
    // that block is that `ILogger`, `IEventStore`, `SSEConnectionManager`
    // and the rest stay under the gate.

    let serverPort: string = failwith "ambient"

    let resolvedLogger: ILogger = failwith "ambient"

    let dataTypes: DataType list = failwith "ambient"

    let resolvedBlobStorage: IBlobStorage = failwith "ambient"

    let eventStore: IEventStore = failwith "ambient"

    let auth: IAuthProvider = failwith "ambient"

    let secretStore: ISecretStore = failwith "ambient"

    let sseConnectionManager: SSEConnectionManager = failwith "ambient"

    let resolvedNotificationChannel: INotificationChannel = failwith "ambient"

    let narrativeStore: INarrativeStore = failwith "ambient"

    let featureFlagStore: IFeatureFlagStore = failwith "ambient"

    let flagEvaluator: FlagEvaluator = failwith "ambient"

    let moduleQueryBus: IModuleQueryBus = failwith "ambient"

    let teamStoreOpt: TeamStore option = failwith "ambient"

    let extensions: ComposeExtensions = failwith "ambient"

    /// The scoped `AccessContext` factory, whose body reads the
    /// pre-resolved user / scope / permissions / subject that
    /// `ScopeResolutionMiddleware` stamped onto `HttpContext.Items`.
    let accessContextFor (sp: System.IServiceProvider) : AccessContext = failwith "ambient"

    // ── The `withPreMiddleware` worked example ────────────────────────

    /// The deployment's own allowlist table, keyed by team slug.
    module Allowlists =

        let byTeam: Map<string, string list> = failwith "ambient"