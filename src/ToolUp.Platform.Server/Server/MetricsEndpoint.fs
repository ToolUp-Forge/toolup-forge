module ToolUp.Platform.MetricsEndpoint

open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform.Metrics

// ─── /metrics endpoint — Phase 9e ────────────────────────────────────
//
// Renders the in-process `PrometheusMetricsSink` registry as
// OpenMetrics text. Mounted by `compose` only when
// `ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint` — the route
// is conditionally added to the Giraffe `choose` list so deployments
// without metrics enabled get a clean 404 rather than an empty 200.
//
// **Per-request DI resolution.** The handler resolves
// `PrometheusMetricsSink` from `HttpContext.RequestServices` rather
// than capturing it at compose time. `compose` builds the routing
// table before resolving the sink (`router` is defined earlier in the
// function than the sink registration), so a closure-captured sink
// would force the order to flip; the DI lookup keeps both regions of
// `compose` independent of each other.
//
// **Auth surface.** This handler is intentionally not gated by
// `AuthEnforcementMiddleware` — Prometheus / Grafana Cloud / OTel
// scrapers don't carry bearer tokens by default and the cost of
// integrating a header on every monitoring stack is disproportionate
// to the threat. Deployments that want authn on `/metrics` gate at the
// network layer (LB allowlist, monitoring-network CIDR). The
// information disclosed is intentional — route templates, tag values,
// traffic patterns. For deployments that don't want that surface
// open, `MetricsEndpoint = NoMetricsEndpoint` is the right answer.
//
// **Content type.** `application/openmetrics-text; version=1.0.0;
// charset=utf-8` is the OpenMetrics 1.0 mandate. Prometheus accepts
// it; OTel collectors with the OpenMetrics receiver also accept it.
// Vanilla Prometheus also accepts `text/plain; version=0.0.4` — we
// emit the OpenMetrics form because it includes `# UNIT` and `# EOF`
// lines that the older format doesn't carry.

[<Literal>]
let private OpenMetricsContentType =
    "application/openmetrics-text; version=1.0.0; charset=utf-8"

let handler: HttpHandler =
    fun next ctx -> task {
        let sink = ctx.RequestServices.GetService<PrometheusMetricsSink>()

        if isNull (box sink) then
            // Defensive — the route is only mounted when the sink is
            // registered, but a deployment that monkey-patches DI
            // could land here with no sink. Prefer a clean 503 over
            // a NullReferenceException.
            ctx.Response.StatusCode <- 503
            do! ctx.Response.WriteAsync "Metrics sink not registered"
            return! next ctx
        else
            ctx.Response.ContentType <- OpenMetricsContentType
            ctx.Response.Headers["Cache-Control"] <- "no-store"
            do! ctx.Response.WriteAsync(sink.Render())
            return! next ctx
    }

let routes: HttpHandler list = [ route "/metrics" >=> handler ]