module ToolUp.Platform.SmokeTestHandler

open System
open System.Diagnostics
open Newtonsoft.Json
open Fable.Remoting.Json
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.SmokeTests

// ─── Smoke-test endpoint handler (Phase 9o) ──────────────────────────
//
// Single route:
//   GET /api/_internal/smoke
//
// Resolves every `ISmokeTest` registered via DI, runs them in
// parallel, aggregates outcomes, returns 200 if every test `Pass`
// or 503 with per-test detail otherwise. Emits one audit row to
// `IEventStore` per invocation under
// `SourceModule = SmokeTest.AuditSourceModule`
// ("_platform.diagnostics").
//
// Token gating: closed to anyone without the deploy script's
// shared secret. Two failure modes both surface as 401 — env var
// unset, and header missing / mismatched. Constant-time
// comparison defeats timing side-channels.
//
// Release-build live, not `#if DEBUG`-gated. Mounted in release
// builds when `ServerConfig.SmokeTest = EnabledSmokeTest` AND the
// token is configured — exactly the deploy-pipeline contract.
//
// Audit emission: writes directly to `IEventStore` rather than
// through `IAuditLog.Record` because `IAuditLog` uses a single
// `SourceModule = "_platform.audit"` label, and the smoke audit
// row belongs under `"_platform.diagnostics"`.

[<Literal>]
let private SmokeTokenEnvVar = "TOOLUP_SMOKE_TOKEN"

[<Literal>]
let private SmokeTokenHeader = "X-Smoke-Token"

let private readEnvToken () =
    match Environment.GetEnvironmentVariable SmokeTokenEnvVar with
    | null
    | "" -> None
    | value -> Some value

let private readHeader (ctx: HttpContext) (name: string) : string option =
    match ctx.Request.Headers.TryGetValue name with
    | true, values when values.Count > 0 ->
        let v = values[0]
        if String.IsNullOrEmpty v then None else Some v
    | _ -> None

/// Constant-time string comparison. Prevents timing side channels
/// against the smoke token.
let private constantTimeEquals (a: string) (b: string) : bool =
    if a.Length <> b.Length then
        false
    else
        let mutable result = 0

        for i in 0 .. a.Length - 1 do
            result <- result ||| (int a[i] ^^^ int b[i])

        result = 0

let private resolveGate (ctx: HttpContext) : Result<unit, int * string> =
    match readEnvToken () with
    | None ->
        Error(
            401,
            sprintf
                "smoke: %s env var is not configured. Set %s before pointing the deploy pipeline at /api/_internal/smoke."
                SmokeTokenEnvVar
                SmokeTokenEnvVar
        )
    | Some envToken ->
        match readHeader ctx SmokeTokenHeader with
        | None -> Error(401, sprintf "smoke: missing %s header" SmokeTokenHeader)
        | Some headerToken ->
            if constantTimeEquals envToken headerToken then
                Ok()
            else
                Error(401, "smoke: invalid token")

// ─── Per-test outcome wire shape ─────────────────────────────────────

type private TestEntry = {
    name: string
    status: string
    message: string
    elapsedMs: int64
}

type private SmokeResponse = {
    status: string
    tests: TestEntry list
}

// ─── Audit payload wire shape ────────────────────────────────────────

type private SmokeAuditEntry = {
    Name: string
    Status: string
    ElapsedMs: int64
}

type private SmokeAuditPayload = {
    OccurredAt: DateTime
    Status: string
    Tests: SmokeAuditEntry list
}

let private auditJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private responseJsonSettings =
    let s = JsonSerializerSettings(Formatting = Formatting.Indented)
    s.Converters.Add(FableJsonConverter())
    s

let private truncate (max: int) (s: string) =
    if isNull s then ""
    elif s.Length <= max then s
    else s.Substring(0, max)

let private runOne (test: ISmokeTest) : Async<TestEntry> = async {
    let stopwatch = Stopwatch.StartNew()

    try
        let! result = test.RunOnce()
        stopwatch.Stop()

        return {
            name = test.Name
            status = SmokeResult.status result
            message = truncate 500 (SmokeResult.message result)
            elapsedMs = stopwatch.ElapsedMilliseconds
        }
    with ex ->
        stopwatch.Stop()

        return {
            name = test.Name
            status = "Fail"
            message = "probe threw: " + truncate 500 ex.Message
            elapsedMs = stopwatch.ElapsedMilliseconds
        }
}

let private emitAudit (eventStore: IEventStore) (logger: ILogger) (response: SmokeResponse) : Async<unit> = async {
    try
        let payload: SmokeAuditPayload = {
            OccurredAt = DateTime.UtcNow
            Status = response.status
            Tests =
                response.tests
                |> List.map (fun t -> {
                    Name = t.name
                    Status = t.status
                    ElapsedMs = t.elapsedMs
                })
        }

        let evt =
            Events.create
                "_platform"
                SmokeTest.AuditSourceModule
                SmokeTest.AuditEventType
                (JsonConvert.SerializeObject(payload, auditJsonSettings))

        do! eventStore.Write evt
    with ex ->
        logger.Warn(sprintf "[SmokeTestHandler] audit write failed: %s" ex.Message)
}

/// `GET /api/_internal/smoke`. Token-gated; aggregates every
/// registered `ISmokeTest`; 200 if all `Pass`, 503 with per-test
/// detail otherwise. Emits one audit row per invocation under
/// `_platform.diagnostics`.
let smokeHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        match resolveGate ctx with
        | Error(statusCode, message) ->
            ctx.Response.StatusCode <- statusCode
            ctx.Response.ContentType <- "text/plain; charset=utf-8"
            return! ctx.WriteTextAsync message
        | Ok() ->
            let tests = ctx.RequestServices.GetServices<ISmokeTest>() |> Seq.toList

            let! entries = tests |> List.map runOne |> Async.Parallel |> Async.StartImmediateAsTask

            let entriesList = entries |> Array.toList

            let allPass = entriesList |> List.forall (fun t -> t.status = "Pass")

            let response = {
                status = (if allPass then "Pass" else "Fail")
                tests = entriesList
            }

            let eventStore = ctx.RequestServices.GetService(typeof<IEventStore>) :?> IEventStore

            let logger =
                match ctx.RequestServices.GetService(typeof<ILogger>) with
                | :? ILogger as l -> l
                | _ -> ConsoleLogger.ConsoleLogger() :> ILogger

            do! emitAudit eventStore logger response |> Async.StartImmediateAsTask

            ctx.Response.StatusCode <- (if allPass then 200 else 503)
            ctx.Response.ContentType <- "application/json; charset=utf-8"

            let body = JsonConvert.SerializeObject(response, responseJsonSettings)
            return! ctx.WriteTextAsync body
    }

/// Routes table. Mounted by `compose` only when
/// `ServerConfig.SmokeTest = EnabledSmokeTest`.
let routes: HttpHandler list = [ GET >=> route "/api/_internal/smoke" >=> smokeHandler ]