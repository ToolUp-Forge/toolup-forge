module ToolUp.Platform.HealthCheckResponseWriter

open System
open System.Threading.Tasks
open Newtonsoft.Json
open Fable.Remoting.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Diagnostics.HealthChecks

// ─── Phase 9k JSON response writer ───────────────────────────────────
//
// Custom `HealthCheckOptions.ResponseWriter` for `/health` and `/ready`.
// BCL's default writer emits `text/plain` with the status word only —
// the Phase 9k acceptance criterion ("503 with a JSON body listing the
// failing check") explicitly requires this writer.
//
// Wire format:
//   {
//     "status": "Healthy" | "Degraded" | "Unhealthy",
//     "checks": [
//       {"name": "...", "kind": "Readiness", "status": "...", "message": "..."},
//       ...
//     ]
//   }
//
// The endpoint is unauthenticated, so messages are truncated to 500
// characters to avoid leaking large stack traces, internal hostnames,
// or credentials. The message has already been pre-truncated by the
// adapter (`HealthCheckAggregator.BclHealthCheckAdapter`); the writer
// re-applies the cap defensively in case a custom probe registered a
// long `Description` directly.

type private HealthCheckEntry = {
    name: string
    kind: string
    status: string
    message: string
}

type private HealthCheckResponse = {
    status: string
    checks: HealthCheckEntry list
}

let private jsonSettings =
    let s = JsonSerializerSettings(Formatting = Formatting.Indented)
    s.Converters.Add(FableJsonConverter())
    s

let private statusName (status: HealthStatus) =
    match status with
    | HealthStatus.Healthy -> "Healthy"
    | HealthStatus.Degraded -> "Degraded"
    | HealthStatus.Unhealthy -> "Unhealthy"
    | _ -> string status

let private truncate (max: int) (s: string) =
    if isNull s then ""
    elif s.Length <= max then s
    else s.Substring(0, max)

/// `HealthCheckOptions.ResponseWriter` is `Func<HttpContext, HealthReport, Task>`.
/// Emits the JSON shape documented above. The response status code is
/// set by the BCL default mapping (200 for Healthy/Degraded, 503 for
/// Unhealthy) — the writer does NOT override this.
let writeResponse (ctx: HttpContext) (report: HealthReport) : Task =
    ctx.Response.ContentType <- "application/json; charset=utf-8"

    let entries =
        report.Entries
        |> Seq.map (fun kvp ->
            let kind =
                kvp.Value.Tags
                |> Seq.tryFind (fun t -> t = "Liveness" || t = "Readiness")
                |> Option.defaultValue "Readiness"

            {
                name = kvp.Key
                kind = kind
                status = statusName kvp.Value.Status
                message = truncate 500 kvp.Value.Description
            })
        |> List.ofSeq

    let payload = {
        status = statusName report.Status
        checks = entries
    }

    let body = JsonConvert.SerializeObject(payload, jsonSettings)
    ctx.Response.WriteAsync body