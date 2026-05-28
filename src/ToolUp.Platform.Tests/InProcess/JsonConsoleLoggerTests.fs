module ToolUp.Platform.Tests.InProcess.JsonConsoleLoggerTests

open System
open System.Text.Json
open Expecto
open ToolUp.Platform

// ─── JsonConsoleLogger + LoggerScope — Phase 9e.1 ───────────────────
//
// `JsonConsoleLogger` writes to the process `Console` (same design as
// `ConsoleLogger`). Tests redirect `Console.Out`/`Console.Error` for
// the duration of the call; the list is `testSequenced` so the two
// redirects don't race each other, and assertions locate the emitted
// line by a unique sentinel in `message` so a stray write from another
// parallel suite can't break the parse.

let private capture (f: unit -> unit) : string * string =
    let outW = new IO.StringWriter()
    let errW = new IO.StringWriter()
    let prevOut = Console.Out
    let prevErr = Console.Error
    Console.SetOut outW
    Console.SetError errW

    try
        f ()
    finally
        Console.SetOut prevOut
        Console.SetError prevErr

    outW.ToString(), errW.ToString()

/// Find the single JSON object line whose `message` equals `sentinel`.
let private lineFor (sentinel: string) (block: string) : JsonElement =
    let parsed =
        block.Split('\n')
        |> Array.map _.Trim()
        |> Array.filter (fun l -> l.StartsWith "{" && l.EndsWith "}")
        |> Array.choose (fun l ->
            try
                Some(JsonDocument.Parse(l).RootElement)
            with _ ->
                None)
        |> Array.filter (fun e ->
            match e.TryGetProperty "message" with
            | true, m -> m.GetString() = sentinel
            | _ -> false)

    Expect.equal parsed.Length 1 $"exactly one JSON line carried the sentinel '{sentinel}'"
    parsed[0]

let tests =
    testSequenced
    <| testList "JsonConsoleLogger + LoggerScope" [

        // ─── LoggerScope ───────────────────────────────────────

        testCase "current () is empty before anything is pushed"
        <| fun _ -> Expect.isEmpty (LoggerScope.current ()) "no ambient context by default"

        testCase "push merges keys and Dispose restores the prior context (LIFO)"
        <| fun _ ->
            use _outer = LoggerScope.push [ LoggerScope.RequestId, "r1" ]
            Expect.equal (LoggerScope.current () |> Map.tryFind LoggerScope.RequestId) (Some "r1") "outer key visible"

            (use _inner =
                LoggerScope.push [ LoggerScope.ScopeId, "s1"; LoggerScope.RequestId, "r2" ]

             Expect.equal (LoggerScope.current () |> Map.tryFind LoggerScope.ScopeId) (Some "s1") "inner key visible"
             Expect.equal (LoggerScope.current () |> Map.tryFind LoggerScope.RequestId) (Some "r2") "inner overwrites")

            Expect.equal
                (LoggerScope.current () |> Map.tryFind LoggerScope.RequestId)
                (Some "r1")
                "prior context restored after inner scope disposed"

            Expect.isNone (LoggerScope.current () |> Map.tryFind LoggerScope.ScopeId) "inner-only key gone"

        testCase "withScope restores the prior context even when f throws"
        <| fun _ ->
            try
                LoggerScope.withScope [ LoggerScope.TraceId, "t1" ] (fun () -> failwith "boom")
            with _ ->
                ()

            Expect.isEmpty (LoggerScope.current ()) "context unwound after exception"

        testCase "ambient context flows across async continuations"
        <| fun _ ->
            use _ = LoggerScope.push [ LoggerScope.RequestId, "async-r" ]

            let observed =
                async {
                    do! Async.Sleep 1
                    return LoggerScope.current () |> Map.tryFind LoggerScope.RequestId
                }
                |> Async.RunSynchronously

            Expect.equal observed (Some "async-r") "AsyncLocal carries the id past the await"

        // ─── JsonConsoleLogger shape ───────────────────────────

        testCase "Info emits one well-formed JSON object with the expected fields"
        <| fun _ ->
            let logger = JsonConsoleLogger.JsonConsoleLogger() :> ILogger
            let out, _ = capture (fun () -> logger.Info "shape-sentinel")
            let e = lineFor "shape-sentinel" out

            Expect.equal (e.GetProperty("level").GetString()) "info" "level tag"
            Expect.equal (e.GetProperty("logger").GetString()) "toolup" "default logger name"
            Expect.equal (e.GetProperty("message").GetString()) "shape-sentinel" "message round-trips"
            Expect.equal (e.GetProperty("context").ValueKind) JsonValueKind.Object "context is always an object"

            let ts = e.GetProperty("timestamp").GetString()

            Expect.isTrue (fst (DateTimeOffset.TryParse ts)) "timestamp parses as a date-time (ISO-8601)"

        testCase "context carries the ambient LoggerScope correlation ids"
        <| fun _ ->
            let logger = JsonConsoleLogger.JsonConsoleLogger() :> ILogger

            let out, _ =
                capture (fun () ->
                    use _ = LoggerScope.push [ LoggerScope.RequestId, "req-42" ]
                    logger.Info "ctx-sentinel")

            let ctx = (lineFor "ctx-sentinel" out).GetProperty("context")
            Expect.equal (ctx.GetProperty("request_id").GetString()) "req-42" "pushed request_id appears in context"

        // ─── JsonConsoleLogger filtering ───────────────────────

        testCase "level floor silences below the floor; Error always emits to stderr"
        <| fun _ ->
            let logger =
                JsonConsoleLogger.JsonConsoleLogger(LogLevel.Warn, Set.empty) :> ILogger

            let out, err =
                capture (fun () ->
                    logger.Debug "dbg-sentinel"
                    logger.Info "inf-sentinel"
                    logger.Warn "wrn-sentinel"
                    logger.Error("err-sentinel", Some(exn "kaboom")))

            Expect.isFalse (out.Contains "dbg-sentinel") "Debug silenced under Warn floor"
            Expect.isFalse (out.Contains "inf-sentinel") "Info silenced under Warn floor"
            let w = lineFor "wrn-sentinel" out
            Expect.equal (w.GetProperty("level").GetString()) "warn" "Warn emitted at the floor"

            let e = lineFor "err-sentinel" err
            Expect.equal (e.GetProperty("level").GetString()) "error" "Error never silenced, goes to stderr"

            Expect.stringContains
                (e.GetProperty("error").GetString())
                "kaboom"
                "exception rendered into the error field"

        testCase "Trace is gated by category and rides the context map"
        <| fun _ ->
            let logger =
                JsonConsoleLogger.JsonConsoleLogger(LogLevel.Info, Set.ofList [ "ai.sse" ])

            let traceL = logger :> ITraceLogger

            let out, _ =
                capture (fun () ->
                    traceL.Trace("other", "trace-suppressed-sentinel")
                    traceL.Trace("ai.sse", "trace-emitted-sentinel"))

            Expect.isFalse (out.Contains "trace-suppressed-sentinel") "non-whitelisted category silenced"
            let t = lineFor "trace-emitted-sentinel" out
            Expect.equal (t.GetProperty("level").GetString()) "trace" "whitelisted Trace emitted"

            Expect.equal
                (t.GetProperty("context").GetProperty("trace_category").GetString())
                "ai.sse"
                "trace category carried in context"
    ]