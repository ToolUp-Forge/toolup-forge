namespace ToolUp.PublicRendering

open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives

/// `redirects.csv`-driven 301-by-default URL remap. Builds a Giraffe
/// `HttpHandler` that issues a redirect when the incoming path
/// matches a `Redirect.From`. Query strings on the incoming request
/// are preserved across the redirect (critical for SEO continuity:
/// `/old-page?utm=src` → `/new-page?utm=src`, not `/new-page`).
///
/// Order: first matching entry wins. Mount this ahead of
/// `PublicPageHandler` in the route chain so a redirect short-
/// circuits before a 404 falls out the other side.
module RedirectMap =
    let private appendQueryString (destination: string) (incomingQuery: string) : string =
        if System.String.IsNullOrEmpty incomingQuery then
            destination
        elif destination.Contains '?' then
            // Destination already carries a query — append incoming
            // params after `&`. Incoming query string includes the
            // leading `?` which we strip.
            destination + "&" + incomingQuery.TrimStart('?')
        else
            // Incoming query string already includes the `?`; append
            // verbatim.
            destination + incomingQuery

    /// Build the redirect handler from a list of `Redirect` entries.
    /// An empty list yields a handler that always falls through to
    /// the next handler in the chain.
    let handler (redirects: Redirect list) : HttpHandler =
        let map = redirects |> List.map (fun r -> r.From, r) |> Map.ofList

        fun _next (ctx: HttpContext) -> task {
            let path = ctx.Request.Path.Value

            match Map.tryFind path map with
            | Some r ->
                let target = appendQueryString r.To ctx.Request.QueryString.Value
                ctx.Response.StatusCode <- r.StatusCode
                ctx.Response.Headers.Location <- StringValues target
                return Some ctx
            // No match — return `None` so the surrounding `choose`
            // tries the next handler. Returning `next ctx` would halt
            // the choose with an empty 200 (the no-op finisher path),
            // shadowing the page handler.
            | None -> return None
        }