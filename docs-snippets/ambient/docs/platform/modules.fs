// Ambient context for `docs/platform/modules.md`.
//
// The page teaches how a CONSUMER's module is written, so most of its
// blocks are excerpts of one: they read the module's own Elmish
// `Model` / `Msg` / `init` / `update` / `view`, its shared-tier query
// contract, the registrations its composition root assembles, and — in
// the conformance / envelope sections — whole illustrative modules
// (`Orders`, `Inventory`, `Reports`, `MyModule`) the reader is
// assumed to have beside them. None of these is SDK surface; they are
// what the page tells a module author to write.
//
// `open Expecto` is here for the one block that is an excerpt of the
// module's OWN test file (the packaged-layout conformance test); it is
// what that file's surrounding program supplies.
open Microsoft.AspNetCore.Http
open Expecto
open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

[<AutoOpen>]
module PageAmbient =

    /// The module's own Elmish state. The page's `ClientModel.fs`
    /// block declares these too and shadows them.
    type Model = { Text: string }

    type Msg =
        | NoOp
        | Submit of string

    /// The module's own MVU trio and its view, as `ClientModel.fs` /
    /// `ClientView.fs` declare them — read by every registration
    /// excerpt on the page.
    let init: unit -> Model * Cmd<Msg> = failwith "ambient"

    let update: Msg -> Model -> Model * Cmd<Msg> = failwith "ambient"

    let view: Model -> (Msg -> unit) -> ReactElement * ReactElement = failwith "ambient"

    /// The per-pane renderers the multi-page example composes into its
    /// `PageContent` cases.
    let leftPanel: Model -> (Msg -> unit) -> ReactElement = failwith "ambient"

    let rightPanel: Model -> (Msg -> unit) -> ReactElement = failwith "ambient"

    let analysisGrid: Model -> (Msg -> unit) -> ReactElement = failwith "ambient"

    // ─── The Hello World module's own server-side pieces ──────────
    //
    // The page's `SharedTypes.fs` block declares `HelloApi` and
    // shadows this; the registration excerpt three sections later
    // reads the factory and the facets the composition root built.

    type HelloApi = { DoThing: string -> Async<string> }

    let helloApiFactory: HttpContext -> HelloApi = failwith "ambient"

    let helloDataType: DataType = failwith "ambient"

    let helloConfigSchema: ModuleConfigSchema = failwith "ambient"

    let helloTool: AIToolDefinition = failwith "ambient"

    let helloToolExecutor: HttpContext -> string -> Async<string> = failwith "ambient"

    /// The AI-tool pair of the `withAITools` section. The page's own
    /// `AIToolDefinition` block declares `myTool` and shadows it.
    let myTool: AIToolDefinition = failwith "ambient"

    let myToolExecutor: HttpContext -> string -> Async<string> = failwith "ambient"

    // ─── Illustrative modules, in the four-file shape the page
    //     documents ───────────────────────────────────────────────

    // The cross-module-query example's request / response records.
    // The page declares them inside `Reports.SharedTypes`; they sit at
    // this level here so the provider block's record construction
    // resolves its labels without qualifying them, which is what a
    // reader compiling inside the module would also get.
    type LatestReq = { DatasetId: string; Top: int }

    type LatestResp = { Label: string; Score: decimal }

    /// The providing module of the cross-module-query example. Its
    /// shared tier declares the contract once; both ends reference it.
    module Reports =

        module SharedTypes =
            let latest: ModuleQueryContract<LatestReq, LatestResp> = failwith "ambient"

    module Orders =

        module Server =
            let serverModule: ServerModule = failwith "ambient"

    module Inventory =

        module Server =
            let serverModule: ServerModule = failwith "ambient"

    module MyModule =

        module SharedTypes =
            type Order = { Id: string; Total: decimal }

        module Server =
            let serverModule: ServerModule = failwith "ambient"

        module ClientView =
            let register () : ErasedModule = failwith "ambient"

    /// An audit sink the deployment composed, read by the
    /// `HostEnvelope` excerpt.
    let mySink: IAuditSink = failwith "ambient"

    /// The composition the `HostEnvelope` section derives from. The
    /// page's `HostEnvelope.describe` block declares `modules` / `app`
    /// itself and shadows these; the four excerpts after it read them.
    let modules: ServerModule list = failwith "ambient"

    let app: ServerApp = failwith "ambient"

    let envelope: HostEnvelope = failwith "ambient"

    /// A stamp pinned beside a previously-generated artefact, read by
    /// the staleness excerpt.
    let pinnedStamp: HostEnvelopeStamp = failwith "ambient"

    // ─── The packaged-module repo's own `Build.fs` ────────────────

    /// `main`'s argv, as the module repo's Build.fs `[<EntryPoint>]`
    /// receives it.
    let args: string array = [||]

    let config: ToolUp.Platform.Build.BuildConfig = failwith "ambient"

    /// The layout options the packaged module's `Build.fs` declares.
    /// The page's `Build.fs` block declares `layout` and shadows this;
    /// the test-binding excerpt after it reads it.
    let layout: PackagedModuleCheckOptions = failwith "ambient"