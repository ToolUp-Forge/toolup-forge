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

    /// Phase 10g — OAuth 1.0a per-call request-signing counters. Emitted
    /// by a connector's `IOAuth1aFlow.Sign` implementation, tagged by
    /// `provider` (the flow `Name`).
    [<Literal>]
    let OAuth1aSigningCallsTotal = "toolup.oauth1a.signing_calls_total"

    [<Literal>]
    let OAuth1aSigningFailuresTotal = "toolup.oauth1a.signing_failures_total"

    /// The request / job / storage / audit series this file owns.
    /// Spliced into `registrations` below.
    let private coreRegistrations: MetricRegistration list = [
        {
            Module = None
            Definition = {
                Name = OAuth1aSigningCallsTotal
                Kind = Counter
                Description = "Total OAuth 1.0a request-signing calls"
                Unit = "1"
                Tags = [ "provider" ]
            }
        }
        {
            Module = None
            Definition = {
                Name = OAuth1aSigningFailuresTotal
                Kind = Counter
                Description = "Total OAuth 1.0a request-signing failures"
                Unit = "1"
                Tags = [ "provider" ]
            }
        }
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
        // Phase 114 — audit-write failure counter. Incremented by
        // `EventStoreAuditLog.Record` when an audit event fails to
        // persist (event-store write threw). Promotes the formerly
        // Warn-only loss signal to an alertable metric so silent audit
        // loss is dashboard-visible. The literal is defined in
        // `AuditLog.AuditMetrics` (compiles before this file).
        {
            Module = None
            Definition = {
                Name = ToolUp.Platform.AuditLog.AuditMetrics.WriteFailuresTotal
                Kind = Counter
                Description = "Audit events that failed to persist (event-store write threw)"
                Unit = "1"
                Tags = [ "event_type" ]
            }
        }
    ]

    /// SDK standard registrations — registered unconditionally by
    /// `compose` when `MetricsEndpoint = EnabledMetricsEndpoint`.
    ///
    /// Phase 740 — the edge-purge outcome counters are declared beside
    /// their emission in `IEdgeCache.fs` (which compiles before this
    /// file) so each metric's tag allowlist sits next to the code that
    /// emits those tags, and spliced in here so a deployment with a
    /// metrics endpoint has them registered without composing anything
    /// — an unregistered series is silently dropped by the sink.
    let registrations: MetricRegistration list =
        coreRegistrations @ ToolUp.Platform.EdgePurgeMetrics.registrations

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

// ─── Component-id telemetry correlation — Phase 283 ─────────────────
//
// Threads the stable Phase 279 `ComponentId` into metric emission as an
// additional *tag* dimension (`component_id`) alongside the name-based
// auto-namespacing that module metrics already carry. A module keeps its
// name-derived namespace (`toolup.{name}.{metric}`) — byte-for-byte
// unchanged for a deployment that never emits the id tag (GP 11) — and a
// deployment that opts in gains a rename-stable grouping key: rename the
// display `Name` and the metric namespace moves, but the `component_id`
// tag (when an explicit id was declared via `ServerModule.withComponentId`)
// stays put, so telemetry correlates across the rename.
//
// Zero cost when unused (GP 13): the helpers are pure `string` / `Map`
// projections a caller pays for only when it merges the tag or permits the
// dimension. The hot-path emission in `MetricsMiddleware.InvokeAsync` above
// is untouched — it allocates no id dimension.
module ComponentCorrelation =
    open ToolUp.Platform

    /// The standard correlation tag key. One stable wire string that every
    /// emission site and every dashboard references, so an id-keyed
    /// grouping survives a display-name rename.
    [<Literal>]
    let ComponentIdTag = "component_id"

    /// The `(key, value)` correlation tag carrying a component's stable id.
    let componentIdTag (componentId: ComponentId) : string * string = ComponentIdTag, componentId.Value

    /// Merge the `component_id` correlation dimension into a tag map.
    /// Additive — every existing tag is preserved; a caller that never
    /// merges pays nothing (GP 13).
    let withComponentId (componentId: ComponentId) (tags: Map<string, string>) : Map<string, string> =
        let k, v = componentIdTag componentId
        Map.add k v tags

    /// Permit the `component_id` dimension on a metric's tag allowlist so
    /// the sink accepts — rather than silently drops — the id when it is
    /// emitted. Idempotent + order-preserving: the key is appended once, at
    /// the end, only when absent, so a definition already permitting it (or
    /// one re-augmented) is returned unchanged. Rendered output stays
    /// byte-identical until an emission actually carries the tag (GP 11).
    let permitComponentIdDimension (def: MetricDefinition) : MetricDefinition =
        if List.contains ComponentIdTag def.Tags then
            def
        else
            {
                def with
                    Tags = def.Tags @ [ ComponentIdTag ]
            }

    /// The stable audit `SourceModule` label for a component. A module that
    /// wants its audit trail to survive a display-name rename records audit
    /// under this label (the stable id string) rather than its mutable
    /// display `Name`, so historical and post-rename events share one
    /// correlatable source. Opt-in — a module that keeps recording under its
    /// `Name` is byte-for-byte unchanged (GP 11).
    let auditSourceLabel (componentId: ComponentId) : string = componentId.Value