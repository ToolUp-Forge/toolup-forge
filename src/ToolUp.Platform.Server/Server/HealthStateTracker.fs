module ToolUp.Platform.HealthStateTracker

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.HealthChecks

// ─── Phase 9p — debounced HealthStateTracker ─────────────────────────
//
// Periodic `BackgroundService` that polls every registered
// `IHealthCheck` once per minute (wall-clock-aligned, matching the
// `JobScheduler` cadence) and emits a `HealthStateChanged` audit
// event when a probe's stable state changes. "Stable state" is
// defined as 3 consecutive observations of the same status — single-
// observation flaps from a load balancer's 1–10 Hz polling don't
// dwarf real audit activity.
//
// **Opt-in via `ServerConfig.HealthStateTracking = true`.** Default
// `false` (GP 13) — deployments that don't enable it pay nothing
// (no DI registration, no tick, no probe runs).
//
// **Single-instance limitation.** The per-probe rolling buffer lives
// in-memory; a second instance running against the same audit-log
// scope would each independently track state and double-emit
// transitions. Flagged for the Phase 9c half 2 distributed companion
// alongside `JobScheduler`, `ClientToolDispatch`, and
// `AICancellationRegistry`.
//
// **Six-rule portability audit:**
//   1. Identity by value      — `ProbeName : string`. No live handles.
//   2. Async                  — `RunOneTick` is `Async<unit>`; the
//                                 BackgroundService base wraps as Task.
//   3. Retry as data          — no built-in retry; the debounce window
//                                 is encoded as a count, not a callback.
//   4. Stateless handlers     — flagged single-instance above. The
//                                 in-memory buffer reconstructs after
//                                 restart from probe runs (3 obs × 60s
//                                 = 3 min to settle).
//   5. No cross-shard ordering — probes are tracked independently;
//                                 transitions emit per-probe.
//   6. Precision lower bound  — tick = `JobPrecision.Minute`, matching
//                                 the SDK's documented floor.

/// Number of consecutive observations of the same status required to
/// flip the tracked stable state. Hardcoded for Phase 9p; a deployment-
/// configurable per-probe override is a follow-up.
let RequiredConsecutive = 3

/// Per-probe state captured between ticks. The rolling buffer holds the
/// last `RequiredConsecutive` observations (oldest first). `StableStatus`
/// is the most recently committed stable state; it's `None` for a probe
/// that hasn't yet seen 3 consecutive identical observations since the
/// tracker started. Public because `runTick` exposes the state map in
/// its signature so tests can drive the algorithm directly without
/// the BackgroundService scheduling overhead.
type ProbeState = {
    Buffer: HealthResult list
    StableStatus: string option
}

let internal emptyState = { Buffer = []; StableStatus = None }

/// Append an observation to the rolling buffer, capped at
/// `RequiredConsecutive`. Older observations drop off the front so the
/// buffer always reflects the trailing window — this is the natural
/// sliding-window shape for a "last N observations" check.
let private appendObservation (buffer: HealthResult list) (obs: HealthResult) : HealthResult list =
    let extended = buffer @ [ obs ]

    if extended.Length <= RequiredConsecutive then
        extended
    else
        extended |> List.skip (extended.Length - RequiredConsecutive)

/// If the buffer holds `RequiredConsecutive` observations all reporting
/// the same status, return that status; otherwise return `None`. The
/// buffer itself decides — `None` means "no stable state observable
/// yet", not "still on the prior stable state".
let private detectStable (buffer: HealthResult list) : string option =
    if buffer.Length < RequiredConsecutive then
        None
    else
        let statuses = buffer |> List.map HealthResult.status

        match statuses with
        | [] -> None
        | first :: rest when rest |> List.forall (fun s -> s = first) -> Some first
        | _ -> None

/// Apply one observation to a probe's state. Returns the new state
/// plus an optional `(prev, next, message)` tuple when the stable
/// status changed — the caller emits an audit event if the tuple is
/// `Some`.
let private apply (state: ProbeState) (obs: HealthResult) : ProbeState * (string * string * string) option =
    let buffer = appendObservation state.Buffer obs
    let stable = detectStable buffer

    match stable, state.StableStatus with
    | Some newStable, Some oldStable when newStable <> oldStable ->
        let message = HealthResult.message obs

        {
            Buffer = buffer
            StableStatus = Some newStable
        },
        Some(oldStable, newStable, message)
    | Some newStable, None ->
        // First-ever stable state — by convention we synthesise a
        // transition from "Healthy" so the trail records the *change*
        // even when the very first three observations were Unhealthy.
        // Operators reading the audit log want to see the moment the
        // probe stopped being healthy, not the moment the tracker
        // first noticed it was already unhealthy.
        let message = HealthResult.message obs

        let priorAssumed = "Healthy"

        if newStable = priorAssumed then
            // Already healthy — no change vs the assumed baseline,
            // nothing to record. Establish stable state silently.
            {
                Buffer = buffer
                StableStatus = Some newStable
            },
            None
        else
            {
                Buffer = buffer
                StableStatus = Some newStable
            },
            Some(priorAssumed, newStable, message)
    | _ -> { state with Buffer = buffer }, None

/// Single tick: run every probe in parallel via `HealthCheckRunner`,
/// fold each observation into the per-probe state map under a lock,
/// and emit `HealthStateChanged` audit events for transitions.
///
/// Pure plumbing — testable directly without scheduling. The
/// production `BackgroundService` calls this on its wall-clock-aligned
/// schedule; tests call it inline with scripted probes + a fake
/// audit log.
let runTick
    (probes: IHealthCheck list)
    (states: ConcurrentDictionary<string, ProbeState>)
    (auditLog: IAuditLog)
    (now: unit -> DateTime)
    : Async<unit> =
    async {
        if probes.IsEmpty then
            return ()
        else
            let! runs = probes |> List.map HealthCheckRunner.runOne |> Async.Parallel

            // Convert the runner's `Status` string back into a
            // `HealthResult` so the state machine compares structured
            // values. The runner already classifies timeouts as
            // `Degraded` and exceptions as `Unhealthy`, matching the
            // `HealthResult` mapping `BclHealthCheckAdapter` uses.
            let toResult (run: HealthCheckRunner.ProbeRun) : HealthResult =
                match run.Status with
                | "Healthy" -> Healthy
                | "Degraded" -> Degraded run.Message
                | _ -> Unhealthy run.Message

            for run in runs do
                let observation = toResult run

                let priorState =
                    states.TryGetValue run.Name
                    |> function
                        | true, s -> s
                        | false, _ -> emptyState

                let nextState, transition = apply priorState observation
                states[run.Name] <- nextState

                match transition with
                | None -> ()
                | Some(fromStatus, toStatus, message) ->
                    let payload = {
                        ProbeName = run.Name
                        FromStatus = fromStatus
                        ToStatus = toStatus
                        Message = message
                        ObservedAt = now ()
                    }

                    // Emit under the reserved `_platform` scope —
                    // probes are deployment-wide, not team-scoped.
                    // Mirrors the scope choice used by Phase 9b's
                    // job lifecycle events.
                    do! auditLog.Record("_platform", HealthStateChanged payload)
    }

/// `BackgroundService` host for the periodic tracker. The tick body is
/// `runTick`, which tests can call directly without the scheduling
/// overhead. The probes list is resolved per-tick via the captured
/// `IServiceProvider` so companions that register after the tracker
/// is constructed (rare — typically all probe registrations land
/// before `app.Build()`, but the contract is "all `IHealthCheck`s
/// known at compose-end") still get polled.
type HealthStateTrackerService(serviceProvider: IServiceProvider, auditLog: IAuditLog, logger: ILogger) =
    inherit BackgroundService()

    let states = ConcurrentDictionary<string, ProbeState>()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // Wall-clock-aligned tick — same shape as `JobScheduler`'s
            // `ExecuteAsync`. Operators staring at log timestamps trust
            // aligned-to-minute more than offset-from-startup ticks.
            while not stoppingToken.IsCancellationRequested do
                let now = DateTime.UtcNow

                let nextTick =
                    DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes 1.0

                let delay = nextTick - now

                try
                    if delay > TimeSpan.Zero then
                        do! Task.Delay(delay, stoppingToken)

                    let probes = serviceProvider.GetServices<IHealthCheck>() |> List.ofSeq

                    do! runTick probes states auditLog (fun () -> DateTime.UtcNow) |> Async.StartAsTask :> Task
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error($"[HealthStateTracker] event=tick_wrapper_error nextTick={nextTick:o}", Some ex)
        }
        :> Task