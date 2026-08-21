// Ambient context for `docs/platform/modules.md`.
//
// The page teaches how a CONSUMER's module is written, so most of its
// blocks are excerpts of one: they read the module's own Elmish
// `Model` / `Msg`, its shared-tier query contract, and — in the
// conformance / envelope sections — whole illustrative modules
// (`Orders`, `Inventory`, `Reports`, `MyModule`) the reader is
// assumed to have beside them. None of these is SDK surface; they are
// what the page tells a module author to write.
open ToolUp.Platform

[<AutoOpen>]
module PageAmbient =

    /// The module's own Elmish state. The page's `ClientModel.fs`
    /// block declares these too and shadows them.
    type Model = { Text: string }

    type Msg =
        | NoOp
        | Submit of string

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

    // ─── The packaged-module repo's own `Build.fs` ────────────────

    /// `main`'s argv, as the module repo's Build.fs `[<EntryPoint>]`
    /// receives it.
    let args: string array = [||]

    let config: ToolUp.Platform.Build.BuildConfig = failwith "ambient"