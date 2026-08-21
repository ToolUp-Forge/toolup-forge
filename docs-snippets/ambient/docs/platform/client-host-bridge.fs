// Ambient context for `docs/platform/client-host-bridge.md`.
//
// The seam is renderer-neutral, so the page's example is written against
// an unnamed external tree language: its page value (`TreePage`), a
// payload one of its typed actions fetches (`Figures`), and its renderer
// (`MyTreeRuntime.render`). None of those are SDK surface — the adapter
// for language X supplies them — and neither are the module's own `init`
// / `update`, which the page shows a reader already holding. Declared
// here so the block compiles as written; a block that declares its own
// `Model` / `Msg` shadows nothing here, and one that declared its own
// `TreePage` would shadow rather than collide, which is why these sit in
// an auto-opened module.
open Feliz
open ToolUp.Elmish

[<AutoOpen>]
module PageAmbient =

    /// The external language's typed page value, held in the module's model.
    type TreePage = { Slug: string }

    /// A payload one of the tree's typed actions fetches over remoting.
    type Figures = { Rows: int }

    /// Stand-in for the external runtime. Any language whose renderer
    /// produces a `ReactElement` hosts through the seam; the handler bag
    /// is whatever that language's adapter accepts.
    module MyTreeRuntime =
        let render (page: TreePage) (handlers: 'Handlers) : ReactElement = failwith "ambient"

    let init () : 'Model * Cmd<'Msg> = failwith "ambient"

    let update (msg: 'Msg) (model: 'Model) : 'Model * Cmd<'Msg> = failwith "ambient"