module ToolUp.Platform.ConsoleLogger

open System

/// Default `ILogger` (+ `ITraceLogger`) implementation — writes to stdout
/// for Debug/Info/Warn/Trace and stderr for Error. Each line is prefixed
/// with a level tag and a UTC timestamp (HH:mm:ss.fff) so logs from multiple
/// processes interleave readably in local dev.
///
/// Replace via DI registration to route logs elsewhere. This implementation
/// is intentionally dependency-free so the SDK stays usable with nothing
/// more than the BCL.
///
/// Filtering (Phase 6h follow-up — Workstream B):
/// - `level` floors `Debug`/`Info`/`Warn`/`Error` calls. `LogLevel.Info`
///   (the default) silences `Debug`; `LogLevel.Warn` also silences `Info`;
///   etc. `Error` is never silenced.
/// - `traceCategories` whitelists `Trace` calls. Empty (the default)
///   silences every Trace call; populated with `["ai.sse"; "platform.sse"]`
///   emits Traces tagged with those categories and no others.
/// - Both knobs default to "behaviour identical to the pre-Workstream-B
///   logger": no Trace output, all other levels emitted.
type ConsoleLogger(level: LogLevel, traceCategories: Set<string>) =
    let timestamp () = DateTime.UtcNow.ToString "HH:mm:ss.fff"
    let floor = LogLevel.rank level

    let allows lvl = LogLevel.rank lvl >= floor

    /// Default constructor — `LogLevel.Info`, no trace categories. Matches
    /// the pre-Workstream-B behaviour exactly so existing call sites
    /// (`ConsoleLogger.ConsoleLogger() :> ILogger`) compile and behave
    /// unchanged.
    new() = ConsoleLogger(LogLevel.Info, Set.empty)

    interface ILogger with
        member _.Debug message =
            if allows LogLevel.Debug then
                Console.Out.WriteLine $"[DBG {timestamp ()}] {message}"

        member _.Info message =
            if allows LogLevel.Info then
                Console.Out.WriteLine $"[INF {timestamp ()}] {message}"

        member _.Warn message =
            if allows LogLevel.Warn then
                Console.Out.WriteLine $"[WRN {timestamp ()}] {message}"

        member _.Error(message, ex) =
            // Error is never silenced — operators always need to see failures.
            match ex with
            | Some e -> Console.Error.WriteLine $"[ERR {timestamp ()}] {message} :: {e}"
            | None -> Console.Error.WriteLine $"[ERR {timestamp ()}] {message}"

    interface ITraceLogger with
        member _.Trace(category, message) =
            // Trace is gated by category (not by level rank) so an operator
            // can leave the floor at Info but still see fine-grained diag for
            // a specific subsystem. Empty whitelist = no Trace output.
            if traceCategories.Contains category then
                Console.Out.WriteLine $"[TRC {timestamp ()}] [{category}] {message}"

// Phase 11.G — env-var-driven default-logger construction. Lifts the
// `TOOLUP_LOG_LEVEL` + `TOOLUP_TRACE_CATEGORIES` parsing out of every
// consumer composition root. The bad-value warning surfaces via
// `eprintfn` — the logger we'd want to use for the warning is being
// constructed in the same call.

let private envVar (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

/// Parse `TOOLUP_LOG_LEVEL` + `TOOLUP_TRACE_CATEGORIES`. Exposed
/// separately so `ServerConfig.fromEnv` can mirror the same resolved
/// values into `ServerConfig.LogLevel` / `ServerConfig.TraceCategories`
/// without re-parsing. Unset / empty env vars resolve to
/// `LogLevel.Info` + empty trace-category set — behaviour identical to
/// the no-arg `ConsoleLogger()` constructor.
let envSettings () : LogLevel * Set<string> =
    let level =
        match envVar "TOOLUP_LOG_LEVEL" with
        | None -> LogLevel.Info
        | Some raw ->
            match LogLevel.tryParse raw with
            | Some lvl -> lvl
            | None ->
                eprintfn $"[WRN] TOOLUP_LOG_LEVEL={raw} not recognised. Using Info."
                LogLevel.Info

    let categories =
        match envVar "TOOLUP_TRACE_CATEGORIES" with
        | None -> Set.empty
        | Some raw ->
            raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s <> "")
            |> Set.ofArray

    level, categories

/// Build the default `ConsoleLogger` from env. Equivalent to
/// `let l, c = envSettings () in ConsoleLogger(l, c) :> ILogger`.
let fromEnv () : ILogger =
    let level, categories = envSettings ()
    ConsoleLogger(level, categories) :> ILogger