module ToolUp.Platform.HealthCheckRunner

open System
open System.Threading
open ToolUp.Platform.HealthChecks

// ─── Phase 9p — shared probe-runner ──────────────────────────────────
//
// Single per-probe runner that respects the declared `Timeout`,
// captures elapsed time, and translates exceptions / cancellations
// into a structured result. Reused by:
//
//   - `DevDiagnosticsHandler.buildHealthChecks` (the dev endpoint —
//     was the original site of this code, now extracted)
//   - `HealthMonitorApiHandler.healthMonitorApi` (Phase 9p's
//     production-safe Owner/Admin surface)
//   - `HealthStateTracker` (Phase 9p's debounced audit-emitter)
//
// Keeping the three call sites pinned to one function eliminates the
// risk that a fix to one diverges from the others — the prior shape
// (the body inlined into `DevDiagnosticsHandler`) was the kind of
// duplication that quietly drifts.

/// Outcome of one probe run. The shape carries enough detail to
/// populate either the dev report or a Fable.Remoting wire DTO without
/// re-running the probe.
type ProbeRun = {
    Name: string
    Kind: HealthKind
    TimeoutMs: int
    Status: string
    Message: string
    ElapsedMs: int64
}

let private truncate (max: int) (s: string) =
    if isNull s then ""
    elif s.Length <= max then s
    else s.Substring(0, max)

/// Run one probe with its declared timeout. Translation rules mirror
/// the BCL adapter (`HealthCheckAggregator.BclHealthCheckAdapter`):
///
///   - Healthy / Degraded / Unhealthy → corresponding status string
///   - `OperationCanceledException` (probe exceeded its own timeout) →
///     `Status = "Degraded"`, message describes the timeout. NOT
///     escalated to `Unhealthy` — a slow dependency may be recovering
///     and we don't want a single slow probe to flip an admin's row to
///     red.
///   - Any other exception → `Status = "Unhealthy"` with the
///     `"probe threw: "` prefix so operators can distinguish a clean
///     `Unhealthy` signal from a probe-implementation bug. Message
///     truncated to 500 chars to bound the wire payload.
let runOne (check: IHealthCheck) : Async<ProbeRun> = async {
    let stopwatch = System.Diagnostics.Stopwatch.StartNew()
    let timeoutMs = int check.Timeout.TotalMilliseconds

    try
        try
            use cts = new CancellationTokenSource(check.Timeout)
            let probeTask = Async.StartImmediateAsTask(check.Check(), cts.Token)
            let! result = probeTask |> Async.AwaitTask
            stopwatch.Stop()

            return {
                Name = check.Name
                Kind = check.Kind
                TimeoutMs = timeoutMs
                Status = HealthResult.status result
                Message = HealthResult.message result
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }
        with
        | :? OperationCanceledException ->
            stopwatch.Stop()

            return {
                Name = check.Name
                Kind = check.Kind
                TimeoutMs = timeoutMs
                Status = "Degraded"
                Message = sprintf "probe exceeded %dms" timeoutMs
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }
        | ex ->
            stopwatch.Stop()

            return {
                Name = check.Name
                Kind = check.Kind
                TimeoutMs = timeoutMs
                Status = "Unhealthy"
                Message = "probe threw: " + truncate 500 ex.Message
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }
    finally
        stopwatch.Stop()
}