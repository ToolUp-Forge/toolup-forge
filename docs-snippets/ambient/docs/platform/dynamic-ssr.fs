// Ambient context for `docs/platform/dynamic-ssr.md`.
//
// The page teaches request-time content sources, so nearly every block is
// an excerpt from a composition root it never shows in full: the
// deployment's `config` and `pageLayout`, the two sources registered in
// "Registering sources", and the page-local backend queries a data-bound
// source calls (`loadCampaign` / `loadProcessed`). Declared here so the
// blocks compile exactly as a reader would copy them, with no
// `open`-ceremony added to the markdown.
open Giraffe.ViewEngine
open ToolUp.PublicRendering

[<AutoOpen>]
module PageAmbient =

    /// One row of a campaign's channel breakdown — the page's own domain
    /// shape, not an SDK type.
    type CampaignChannel = {
        Name: string
        Spend: decimal
        Cpa: decimal
    }

    /// What `loadCampaign` returns: already-formatted display strings plus
    /// the raw signed delta fractions the metric / threshold projectors take.
    type Campaign = {
        Name: string
        SpendDisplay: string
        SpendDelta: float
        ConvDisplay: string
        ConvDelta: float
        SpendVsTargetLabel: string
        Channels: CampaignChannel list
    }

    /// The deployment's own `ServerConfig` and first-registered layout.
    let config: ServerConfig = failwith "ambient"

    let pageLayout: PublicPage -> XmlNode = failwith "ambient"

    /// The page a layout / component-registry example is rendering.
    let page: PublicPage = failwith "ambient"

    /// The two sources built in the "Writing a content source" blocks.
    let statusPage: IContentSource = failwith "ambient"

    let clientPages: IContentSource = failwith "ambient"

    /// The page-local backend queries a data-bound source calls.
    let loadCampaign (ctx: AccessContext) (client: string) : Async<Campaign option> = failwith "ambient"

    let loadProcessed (ctx: AccessContext) (slug: string) : Async<ProcessedDataTypes.ProcessedData option> =
        failwith "ambient"

    /// The external block renderer the `Component` seam example calls.
    let renderPriceWidget (sku: string option) : XmlNode = failwith "ambient"

    /// The programmatic-render (Phase 155) example's own substrate.
    let cache: IRenderCache = failwith "ambient"

    let contentVersion: string = failwith "ambient"

    let lastModified: DateTimeOffset = failwith "ambient"

    let renderExpensiveReportHtml (tenant: string) (quarter: string) : string = failwith "ambient"