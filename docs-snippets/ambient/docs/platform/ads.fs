// Ambient context for `docs/platform/ads.md`.
//
// The view and sink excerpts read from the consumer's own module — its
// Elmish `Model` / `Msg`, the sibling render helpers, the deployment's
// `ClientConfig` value, and the payload shaper a custom analytics sink
// would supply. None of them are SDK surface, so they are declared here
// and the blocks stay about the ads substrate.
open Feliz
open ToolUp.Platform

[<AutoOpen>]
module PageAmbient =

    type Model = { Query: string }

    type Msg = | NoOp

    /// The deployment's own `ClientConfig` value — the component branches
    /// on its `AdPanel` field.
    let config: ClientConfig = failwith "ambient"

    let renderHeader () : ReactElement = failwith "ambient"

    let renderResults (model: Model) (dispatch: Msg -> unit) : ReactElement = failwith "ambient"

    /// Maps an impression / click event onto the analytics vendor's own
    /// request body.
    let toPlausibleShape (event: 'a) : string = failwith "ambient"