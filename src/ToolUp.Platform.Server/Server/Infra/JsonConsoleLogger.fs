module ToolUp.Platform.JsonConsoleLogger

open System
open System.Text.Json.Nodes

/// Phase 9e.1 — JSON-structured `ILogger` (+ `ITraceLogger`) for log
/// aggregation. Production deployments pipe stdout/stderr into
/// centralised aggregation (Elasticsearch / CloudWatch Logs / Datadog
/// Logs / Loki); those parsers expect one self-describing JSON object
/// per line, not the plain `[INF hh:mm:ss.fff] msg` form `ConsoleLogger`
/// emits.
///
/// One JSON object per line: `timestamp` (ISO-8601 UTC), `level`,
/// `logger`, `message`, `context` (the ambient `LoggerScope`
/// correlation map — `request_id` / `scope_id` / `trace_id` / any
/// pushed keys), and `error` (the exception, only on `Error` with an
/// exn). Debug/Info/Warn/Trace go to stdout, Error to stderr — same
/// stream split as `ConsoleLogger`.
///
/// Opt-in: `compose` constructs this instead of `ConsoleLogger` only
/// when `TOOLUP_LOG_FORMAT=json`; the plain logger stays the zero-config
/// default (GP 13 — advanced behaviour is opt-in; GP 11 — default path
/// unchanged). Dependency-free: `System.Text.Json` is in the shared
/// framework, no NuGet (GP 2).
///
/// Filtering mirrors `ConsoleLogger` exactly:
/// - `level` floors Debug/Info/Warn; Error is never silenced.
/// - `traceCategories` whitelists Trace; empty (default) silences Trace.
type JsonConsoleLogger(level: LogLevel, traceCategories: Set<string>, ?loggerName: string) =
    let name = defaultArg loggerName "toolup"
    let floor = LogLevel.rank level
    let allows lvl = LogLevel.rank lvl >= floor

    let emit (out: IO.TextWriter) (levelTag: string) (message: string) (ex: exn option) =
        let o = JsonObject()
        o["timestamp"] <- JsonValue.Create(DateTime.UtcNow.ToString("o", Globalization.CultureInfo.InvariantCulture))
        o["level"] <- JsonValue.Create levelTag
        o["logger"] <- JsonValue.Create name
        o["message"] <- JsonValue.Create message

        let ctx = JsonObject()

        for KeyValue(k, v) in LoggerScope.current () do
            ctx[k] <- JsonValue.Create v

        o["context"] <- ctx

        match ex with
        | Some e -> o["error"] <- JsonValue.Create(e.ToString())
        | None -> ()

        out.WriteLine(o.ToJsonString())

    /// Default constructor — `LogLevel.Info`, no trace categories,
    /// logger name `"toolup"`. Matches `ConsoleLogger`'s default knobs.
    new() = JsonConsoleLogger(LogLevel.Info, Set.empty)

    interface ILogger with
        member _.Debug message =
            if allows LogLevel.Debug then
                emit Console.Out "debug" message None

        member _.Info message =
            if allows LogLevel.Info then
                emit Console.Out "info" message None

        member _.Warn message =
            if allows LogLevel.Warn then
                emit Console.Out "warn" message None

        member _.Error(message, ex) =
            // Error is never silenced — operators always need failures.
            emit Console.Error "error" message ex

    interface ITraceLogger with
        member _.Trace(category, message) =
            // Gated by category (not level rank), as in ConsoleLogger.
            // The category rides the `context` map under
            // `trace_category` so JSON consumers can filter on it the
            // same way they would the plain logger's `[category]` tag.
            if traceCategories.Contains category then
                use _ = LoggerScope.push [ "trace_category", category ]
                emit Console.Out "trace" message None