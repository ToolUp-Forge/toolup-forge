namespace ToolUp.Platform.Metrics

open System
open System.Diagnostics
open Microsoft.AspNetCore.Http

// ─── MetricsMiddleware — Phase 9e per-request emission ──────────────
//
// Records the SDK standard request-level metrics on every HTTP
// request: counter, latency histogram, error counter. Sits in the
// pipeline BEFORE `RequestTimingMiddleware` so it observes the same
// span as the slow-request log warner (the two middlewares serve
// different audiences — operator dashboards vs. log lines — and stay
// independently togglable).
//
// **Bypass list.** Long-running endpoints (SSE, /health, /ready)
// emit different metrics: SSE manages its own
// `toolup.sse.active_connections` gauge in `SSEConnectionManager`;
// /health and /ready are too noisy at orchestrator-poll cadence to
// usefully appear in latency histograms. Mirrors the bypass shape in
// `RequestTimingMiddleware.IsLongRunningPath`.
//
// **Tag bucketing.** Caller-controlled values (route, status code)
// are bucketed before emission. `route` becomes `route_class` (the
// path's first two segments); `status` becomes `status_class`
// (1xx/2xx/3xx/4xx/5xx). This bounds cardinality structurally — a
// 5xx tagged with the literal status code "503" produces unbounded
// distinct values across implementations; "5xx" produces five.

/// SDK-owned standard metric names. Centralised so emission sites
/// in middleware, the job scheduler, the SSE connection manager, and
/// `FileManagement` all reference the same string. Module-scoped
/// metrics declared via `ServerModule.withMetrics` are auto-namespaced
/// elsewhere; these are SDK-only.
module StandardMetrics =
    [<Literal>]
    let RequestsTotal = "toolup.requests.total"

    [<Literal>]
    let RequestsLatencyMs = "toolup.requests.latency_ms"

    [<Literal>]
    let ErrorsTotal = "toolup.errors.total"

    [<Literal>]
    let SseActiveConnections = "toolup.sse.active_connections"

    [<Literal>]
    let JobsQueued = "toolup.jobs.queued"

    [<Literal>]
    let JobsRunsTotal = "toolup.jobs.runs.total"

    [<Literal>]
    let StorageBytesRead = "toolup.storage.bytes_read"

    [<Literal>]
    let StorageBytesWritten = "toolup.storage.bytes_written"

    /// SDK standard registrations — registered unconditionally by
    /// `compose` when `MetricsEndpoint = EnabledMetricsEndpoint`.
    let registrations: MetricRegistration list = [
        {
            Module = None
            Definition = {
                Name = RequestsTotal
                Kind = Counter
                Description = "Total HTTP requests received"
                Unit = "1"
                Tags = [ "method"; "route_class"; "status_class" ]
            }
        }
        {
            Module = None
            Definition = {
                Name = RequestsLatencyMs
                Kind = Histogram MetricDefinition.defaultLatencyBucketsMs
                Description = "HTTP request duration in milliseconds"
                Unit = "ms"
                Tags = [ "method"; "route_class"; "status_class" ]
            }
        }
        {
            Module = None
            Definition = {
                Name = ErrorsTotal
                Kind = Counter
                Description = "Total HTTP responses with status >= 400"
                Unit = "1"
                Tags = [ "route_class"; "status_class" ]
            }
        }
        {
            Module = None
            Definition = {
                Name = SseActiveConnections
                Kind = Gauge
                Description = "Currently open Server-Sent Events connections"
                Unit = "1"
                Tags = [ "endpoint" ]
            }
        }
        {
            Module = None
            Definition = {
                Name = JobsQueued
                Kind = Gauge
                Description = "Active scheduled-job count"
                Unit = "1"
                Tags = []
            }
        }
        {
            Module = None
            Definition = {
                Name = JobsRunsTotal
                Kind = Counter
                Description = "Job runs completed since process start"
                Unit = "1"
                Tags = [ "handler"; "outcome" ]
            }
        }
        {
            Module = None
            Definition = {
                Name = StorageBytesRead
                Kind = Counter
                Description = "Bytes read from blob storage"
                Unit = "bytes"
                Tags = [ "container_class" ]
            }
        }
        {
            Module = None
            Definition = {
                Name = StorageBytesWritten
                Kind = Counter
                Description = "Bytes written to blob storage"
                Unit = "bytes"
                Tags = [ "container_class" ]
            }
        }
    ]

module private RouteClassifier =
    /// Bucket the request path to a stable two-segment prefix to
    /// keep cardinality bounded. `/api/_platform/teams/team-abc/members/user-xyz`
    /// → `/api/_platform`. Health / metrics / SSE endpoints carry
    /// their literal path so the operator can distinguish them.
    /// Empty path is reported as `"/"`.
    let classify (path: string) =
        if isNull path || path.Length = 0 then
            "/"
        else
            let trimmed = path.TrimStart('/')

            if trimmed.Length = 0 then
                "/"
            else
                let segments = trimmed.Split('/')

                if segments.Length = 1 then
                    "/" + segments[0]
                else
                    "/" + segments[0] + "/" + segments[1]

    /// Bucket status into 1xx / 2xx / 3xx / 4xx / 5xx. Anything outside
    /// the standard range falls into "other".
    let statusClass (status: int) =
        if status >= 100 && status < 200 then "1xx"
        elif status >= 200 && status < 300 then "2xx"
        elif status >= 300 && status < 400 then "3xx"
        elif status >= 400 && status < 500 then "4xx"
        elif status >= 500 && status < 600 then "5xx"
        else "other"

    /// Endpoints excluded from request-level latency emission. SSE
    /// streams report through `toolup.sse.active_connections`
    /// instead; health probes are too high-cadence to be informative
    /// in a latency histogram.
    let isExcluded (path: string) =
        if isNull path then
            false
        else
            path.StartsWith("/api/notifications", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/ai/events", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/ready", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)

/// Per-request metrics middleware. Registered before
/// `RequestTimingMiddleware` so the timer covers the same downstream
/// pipeline. Best-effort — any exception inside the metrics path is
/// caught and swallowed so a sink failure cannot leak into the
/// primary request.
type MetricsMiddleware(next: RequestDelegate, sink: IMetricsSink) =
    member _.InvokeAsync(ctx: HttpContext) = task {
        let stopwatch = Stopwatch.StartNew()

        try
            do! next.Invoke(ctx)
        finally
            stopwatch.Stop()

            try
                let path = ctx.Request.Path.Value

                if not (RouteClassifier.isExcluded path) then
                    let routeClass = RouteClassifier.classify path
                    let statusClass = RouteClassifier.statusClass ctx.Response.StatusCode
                    let method = ctx.Request.Method
                    let elapsedMs = stopwatch.Elapsed.TotalMilliseconds

                    let baseTags =
                        Map.ofList [ "method", method; "route_class", routeClass; "status_class", statusClass ]

                    sink.Increment(StandardMetrics.RequestsTotal, baseTags)
                    sink.Record(StandardMetrics.RequestsLatencyMs, elapsedMs, baseTags)

                    if ctx.Response.StatusCode >= 400 then
                        let errorTags =
                            Map.ofList [ "route_class", routeClass; "status_class", statusClass ]

                        sink.Increment(StandardMetrics.ErrorsTotal, errorTags)
            with _ ->
                ()
    }