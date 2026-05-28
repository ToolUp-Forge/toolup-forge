namespace ToolUp.Platform

open System
open System.Diagnostics
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Request timing middleware ───────────────────────────────────────
//
// `RequestTimingMiddleware` wraps the downstream pipeline in a
// `Stopwatch` and emits a `Warn`-level log entry whenever the
// elapsed time exceeds `ServerConfig.SlowRequestThreshold`. The
// threshold is configurable per deployment so production can tighten
// it to surface latency regressions earlier and dev can relax it
// without code changes.
//
// **Why log, not audit.** A slow request is operational telemetry,
// not a state change. `IAuditLog` is for compliance-relevant events;
// log lines are for operators looking at dashboards. Routing this
// through `IAuditLog` would dwarf real audit volume at scale.
//
// The middleware is best-effort: any exception in the timing path is
// caught and swallowed so a logger failure cannot leak into the
// primary request. The stopwatch is started before the `next`
// invocation and stopped after it returns (success or thrown), so
// timings include downstream middleware and handler work but exclude
// scope-resolution and auth gates that ran before this middleware.

type RequestTimingMiddleware(next: RequestDelegate, config: ServerConfig, logger: ILogger) =
    /// Routes that are long-lived by design and would always trip the
    /// slow-request threshold. `/api/notifications` and `/api/ai/events`
    /// are SSE streams held open until client disconnect; `/health` and
    /// `/ready` are load-balancer probes. Mirrors the bypass list in
    /// `RateLimiting.fs`.
    static member private IsLongRunningPath(path: string) =
        path.StartsWith("/api/notifications", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/ai/events", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/ready", StringComparison.OrdinalIgnoreCase)

    /// Resolve the slow-request threshold for `path`: longest-prefix
    /// match in `SlowRequestThresholdOverrides` wins, falling back to
    /// the global `SlowRequestThreshold`. Case-insensitive prefix match,
    /// matching the HTTP path conventions used elsewhere in the SDK.
    static member private ResolveThreshold (config: ServerConfig) (path: string) =
        if Map.isEmpty config.SlowRequestThresholdOverrides then
            config.SlowRequestThreshold
        else
            let bestMatch =
                config.SlowRequestThresholdOverrides
                |> Map.toSeq
                |> Seq.filter (fun (prefix, _) -> path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                |> Seq.sortByDescending (fun (prefix, _) -> prefix.Length)
                |> Seq.tryHead

            match bestMatch with
            | Some(_, threshold) -> threshold
            | None -> config.SlowRequestThreshold

    member _.InvokeAsync(ctx: HttpContext) = task {
        let stopwatch = Stopwatch.StartNew()

        try
            do! next.Invoke(ctx)
        finally
            stopwatch.Stop()

            try
                let route =
                    if isNull ctx.Request.Path.Value then
                        ""
                    else
                        ctx.Request.Path.Value

                let threshold = RequestTimingMiddleware.ResolveThreshold config route

                if
                    stopwatch.Elapsed > threshold
                    && not (RequestTimingMiddleware.IsLongRunningPath route)
                then
                    let method = ctx.Request.Method
                    let status = ctx.Response.StatusCode
                    let elapsedMs = int64 stopwatch.Elapsed.TotalMilliseconds

                    logger.Warn($"[RequestTiming] slow request: {method} {route} status={status} elapsedMs={elapsedMs}")
            with _ ->
                ()
    }