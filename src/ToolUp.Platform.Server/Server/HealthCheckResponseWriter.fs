module ToolUp.Platform.HealthCheckResponseWriter

open System
open System.Threading.Tasks
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
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

// Phase 118 — degraded-capability section. Emitted ONLY when the
// registry is non-empty, so a healthy deployment's `/health` payload
// stays byte-for-byte identical to pre-Phase-118 (GP 13). Local
// lowercase DTO so the nested keys match this endpoint's existing
// lowercase wire style (`status` / `checks`) rather than the Core
// record's PascalCase.
type private DegradedEntry = {
    capability: string
    degradedSince: DateTimeOffset
    reason: string
    impact: string
    remediation: string
}

type private HealthCheckResponseWithDegraded = {
    status: string
    checks: HealthCheckEntry list
    degraded: DegradedEntry list
}

let private jsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

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

    // Phase 118 — fold in the degraded-capability set when present.
    // Resolved best-effort from DI; absent (null) on the rare path where
    // the registry was not composed (e.g. a hand-rolled host). Empty set
    // → emit the original payload shape unchanged (GP 13).
    let degraded =
        match ctx.RequestServices.GetService(typeof<DegradedCapabilities.DegradedCapabilityRegistry>) with
        | :? DegradedCapabilities.DegradedCapabilityRegistry as reg when not reg.IsEmpty ->
            reg.Snapshot()
            |> List.map (fun d -> {
                capability = d.Capability
                degradedSince = d.DegradedSince
                reason = d.Reason
                impact = d.Impact
                remediation = d.Remediation
            })
        | _ -> []

    let body =
        if List.isEmpty degraded then
            JsonSerializer.Serialize(
                {
                    status = statusName report.Status
                    checks = entries
                },
                jsonOptions
            )
        else
            JsonSerializer.Serialize(
                {
                    status = statusName report.Status
                    checks = entries
                    degraded = degraded
                },
                jsonOptions
            )

    ctx.Response.WriteAsync body