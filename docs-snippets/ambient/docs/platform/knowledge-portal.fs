// Ambient context for `docs/platform/knowledge-portal.md`.
//
// Every block on this page is an excerpt from one composition root: the
// retrieval pipeline and knowledge-base client the deployment already
// holds, the team the portal's scopes are pinned to, and the four
// surfaces the page builds up section by section and then composes in its
// last block. Only `open` lines carry between blocks, so the values the
// closing block reads are declared here; a block that declares its own
// `answers` / `faq` shadows this one, which is why they sit in an
// auto-opened module.
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.PublicRendering
open ToolUp.PublicRendering.PublicRenderingCompose

[<AutoOpen>]
module PageAmbient =

    /// Resolved from DI by the deployment's composition root.
    let pipeline: IRetrievalPipeline = failwith "ambient"

    /// The team whose knowledge the portal serves.
    let teamId: string = failwith "ambient"

    /// The deployment's own knowledge-base client. `ToolUp.PublicRendering`
    /// never references a knowledge-base companion (GP 1), so the page reads
    /// whatever surface the deployment already holds rather than an SDK type.
    let kb
        : {|
              GetSuggestedQuestions: string option -> Async<string list>
          |} =
        failwith "ambient"

    let config: ServerConfig = failwith "ambient"

    // `XmlNode` is declared in the `HtmlElements` module, not directly in
    // the `Giraffe.ViewEngine` namespace — an `open` reaches it because that
    // module is auto-opened, but a qualified name has to name it. Qualified
    // deliberately: a top-level `open Giraffe.ViewEngine` here would put the
    // whole element DSL in scope for every block on the page and so hide a
    // genuinely-missing `open` in the markdown.
    let pageLayout: PublicPage -> Giraffe.ViewEngine.HtmlElements.XmlNode =
        failwith "ambient"

    let answers: IContentSource = failwith "ambient"

    let docs: IContentSource = failwith "ambient"

    let faq: IContentSource = failwith "ambient"

    let searchConfig: SemanticSearchConfig = failwith "ambient"