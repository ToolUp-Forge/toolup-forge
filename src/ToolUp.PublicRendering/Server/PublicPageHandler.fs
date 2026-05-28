namespace ToolUp.PublicRendering

open Giraffe
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http

/// Catch-all `GET /{slug}` handler. The slug derives from the
/// request path with leading `/` stripped; root path `/` resolves
/// against the page slug `"index"`. When `IPublicContentApi.GetPage`
/// returns `None`, the handler falls through to `next` — the
/// `RedirectMap` handler is expected to own the 301 fall-through
/// before a 404 lands.
///
/// Layout resolution: looks up `PublicPage.Layout` in the registered
/// layout map; falls back to the first-registered layout when the
/// named layout is unknown. A page with no layouts at all returns
/// a 500 ("no layout registered") — compose-time invariant rather
/// than a runtime expectation, surfaced as a hard error so a
/// mis-registered layout map can't masquerade as an empty page.
module PublicPageHandler =
    let private resolveLayout
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (page: PublicPage)
        : (PublicPage -> XmlNode) option =
        match Map.tryFind page.Layout layouts with
        | Some f -> Some f
        | None -> layouts |> Map.toSeq |> Seq.tryHead |> Option.map snd

    let handler (api: IPublicContentApi) (layouts: Map<LayoutName, PublicPage -> XmlNode>) : HttpHandler =
        fun _next (ctx: HttpContext) -> task {
            let rawPath = ctx.Request.Path.Value
            let slug = rawPath.TrimStart('/')
            let slugOrIndex = if slug = "" then "index" else slug
            let! pageOpt = api.GetPage slugOrIndex

            match pageOpt with
            | Some page ->
                match resolveLayout layouts page with
                | Some layout ->
                    let node = layout page
                    let html = RenderView.AsString.htmlDocument node
                    ctx.Response.ContentType <- "text/html; charset=utf-8"
                    return! ctx.WriteStringAsync html
                | None ->
                    ctx.Response.StatusCode <- 500
                    return! ctx.WriteStringAsync "PublicRendering: no layout registered"
            // No page — return `None` so the surrounding `choose`
            // falls through to the SDK's default (which ultimately
            // becomes a 404 once every branch declines).
            | None -> return None
        }