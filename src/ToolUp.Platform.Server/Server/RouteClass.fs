namespace ToolUp.Platform.Infra

open System
open Microsoft.AspNetCore.Http

// ─── The route class — one classification per request ────────────────
//
// A request's path is classified ONCE, by `RouteClassifier.classify` over
// one table of `(prefix, RouteClass)` rows, and stamped on the request by
// `RequestIdMiddleware` (the first platform middleware). Every middleware
// that used to keep its own path-prefix list (metrics exclusion, the
// slow-request exemption, the rate limiter's bypass and SSE window, the SSE
// surface carve-out, the reader refusal class, the surface gate's and the
// scope resolver's `/api` / `/dev` / page predicates) reads the stamp, so a
// request's class cannot disagree with itself across them.
//
// Compiled in the Infra part assembly because it is the earliest one every
// reader references.

/// What a request is, by its path.
[<RequireQualifiedAccess>]
type RouteClass =
    /// An `/api` route that is not a server-sent-event stream.
    | Api
    /// A `/dev` diagnostics route.
    | Dev
    /// A load-balancer probe (`/health`, `/ready`, and anything under them).
    | Health
    /// The metrics scrape endpoint (`/metrics`).
    | Metrics
    /// One of the SDK's server-sent-event streams (`/api/notifications`,
    /// `/api/ai/events`): long-lived, under `/api`.
    | Sse
    /// Everything else: a page route, the SPA shell, a static asset.
    | Page

module RouteClass =
    /// The stable lower-case name, for logs and the pins.
    let name (routeClass: RouteClass) : string =
        match routeClass with
        | RouteClass.Api -> "api"
        | RouteClass.Dev -> "dev"
        | RouteClass.Health -> "health"
        | RouteClass.Metrics -> "metrics"
        | RouteClass.Sse -> "sse"
        | RouteClass.Page -> "page"

    /// `true` for a route under `/api`: an API call or one of its SSE
    /// streams. The surface gate and the scope resolver scope themselves to
    /// this, exactly as `PathString.StartsWithSegments "/api"` did.
    let isApiSurface (routeClass: RouteClass) : bool =
        match routeClass with
        | RouteClass.Api
        | RouteClass.Sse -> true
        | RouteClass.Dev
        | RouteClass.Health
        | RouteClass.Metrics
        | RouteClass.Page -> false

module RouteClassifier =
    /// The one path-prefix table. Most specific first: the first row whose
    /// prefix is a whole-segment prefix of the path (case-insensitive) wins,
    /// and a path no row matches is a `Page`.
    let table: (string * RouteClass) list = [
        "/api/notifications", RouteClass.Sse
        "/api/ai/events", RouteClass.Sse
        "/api", RouteClass.Api
        "/dev", RouteClass.Dev
        "/health", RouteClass.Health
        "/ready", RouteClass.Health
        "/metrics", RouteClass.Metrics
    ]

    /// `path` is `prefix` or lies under it, by whole segments and ignoring
    /// case — the rule `PathString.StartsWithSegments` applies.
    let private underSegment (path: string) (prefix: string) =
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length = prefix.Length || path[prefix.Length] = '/')

    /// Classify a request path through the table.
    let classify (path: string) : RouteClass =
        match path with
        | null -> RouteClass.Page
        | p ->
            table
            |> List.tryFind (fun (prefix, _) -> underSegment p prefix)
            |> Option.map snd
            |> Option.defaultValue RouteClass.Page

    /// The table's prefixes for one class, in table order.
    let prefixesOf (routeClass: RouteClass) : string list =
        table |> List.filter (fun (_, c) -> c = routeClass) |> List.map fst

    /// The `HttpContext.Items` key `RequestIdMiddleware` stamps the class
    /// under, once per request. Read it through `ofItems` / `ofRequest`.
    [<Literal>]
    let ItemsKey = "ToolUp.RouteClass"

    /// Stamp `routeClass` on a request's items.
    let stamp (items: Collections.Generic.IDictionary<obj, obj>) (routeClass: RouteClass) : unit =
        items[box ItemsKey] <- box routeClass

    /// The class stamped on a request's items, or `None`.
    let ofItems (items: Collections.Generic.IDictionary<obj, obj>) : RouteClass option =
        match items.TryGetValue(box ItemsKey) with
        | true, (:? RouteClass as routeClass) -> Some routeClass
        | _ -> None

    /// The request's class: the stamp, or — for a request that did not pass
    /// `RequestIdMiddleware` (a middleware driven on its own, a host that
    /// mounts it elsewhere) — the same table read of its path.
    let ofRequest (ctx: HttpContext) : RouteClass =
        match ofItems ctx.Items with
        | Some routeClass -> routeClass
        | None -> classify ctx.Request.Path.Value