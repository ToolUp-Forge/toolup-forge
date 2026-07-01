// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AlertRuleEngine

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Metrics

// ─── Phase 178 — alert-rule / threshold engine ──────────────────────
//
// A `BackgroundService` that turns the emit-only observability floor
// operator-facing: it evaluates each declared `AlertRule` against the
// signals the SDK already emits and *delivers* breaches through the
// existing notification family. Today the SDK emits metrics
// (`IMetricsSink`, Phase 9e) and exposes health (`IHealthCheck`, Phase
// 9k) but nothing in-substrate says "queue depth > N for M minutes →
// tell someone". This closes that gap without a store — the lightweight
// threshold-to-notification seam (the trigger-gated Phase 9x self-hosted
// observability subset ships the query/store substrate instead).
//
// **The metric-read seam.** The engine reads current metric values via
// `PrometheusMetricsSink.TryRead` (the concrete SDK-default sink,
// resolved from DI). `IMetricsSink` itself stays WRITE-ONLY — its
// hot-path rule-2 exemption forbids an `Async<_>` / read method on the
// emission interface, so the read lives on the concrete sink, not on
// the portable contract. When metrics are disabled (`NoOpMetricsSink`,
// no `PrometheusMetricsSink` in DI) metric reads return `None` and
// metric rules never fire (health-probe rules still work). Documented
// tap; no interface widening.
//
// **Debounce + re-arm.** Per-rule the engine tracks the timestamp a
// breach began; a rule fires exactly once when the breach has persisted
// for `ForDuration`, and re-arms only after the signal recovers
// (mirrors Prometheus `for:`). Transient sub-window blips never fire.
//
// **Single-instance limitation.** The per-rule breach window is
// in-memory; a second instance evaluating the same rules would each
// track state independently and could double-fire. Same class as
// `HealthStateTracker` / `JobScheduler` — flagged for the distributed
// companion. Gated to the `AllInOne` / `WorkerOnly` process profiles via
// `ProcessProfileGate` at the compose registration site.
//
// **Six-rule portability audit:**
//   1. Identity by value      — rules keyed by `AlertRule.Name`; no
//                                 live handles cross the surface.
//   2. Async                  — delivery is `INotificationChannel.Publish`
//                                 (`Async<unit>`); `runTick` is `Async`.
//   3. Retry as data          — no retry here; delivery retry/back-off
//                                 lives in the notification impls.
//   4. Stateless handlers     — the pure `runTick` takes the readers +
//                                 state map as parameters (testable
//                                 inline); the only mutable is the
//                                 bounded per-rule breach window (hot-
//                                 path-adjacent, mirrors the metrics-sink
//                                 exemption).
//   5. No cross-shard ordering — rules evaluate independently per tick.
//   6. Precision lower bound  — tick = `JobPrecision.Minute`, matching
//                                 the SDK's documented floor and the
//                                 `INotificationChannel` precision
//                                 contract.

/// Per-rule state carried between ticks. `BreachingSince` is when the
/// current uninterrupted breach began (`None` when not currently
/// breaching); `Fired` is `true` once the rule has delivered for the
/// current breach episode and resets on recovery. Public so `runTick`
/// exposes the state map in its signature and tests can drive the
/// algorithm directly without the scheduling overhead.
type RuleState = {
    BreachingSince: DateTime option
    Fired: bool
}

/// Starting state for a rule the engine hasn't observed yet — not
/// breaching, not fired.
let initialState = { BreachingSince = None; Fired = false }

/// Does a scalar (counter / gauge) reading satisfy a numeric condition?
/// Probe conditions are inert against a metric source — they never
/// breach (documented; the rule is a source/condition mismatch).
let scalarBreached (condition: ThresholdCondition) (value: float) : bool =
    match condition with
    | GreaterThan t -> value > t
    | LessThan t -> value < t
    | Equals t -> value = t
    | ProbeUnhealthy
    | ProbeDegraded -> false

/// Does a health reading satisfy a probe condition? `ProbeDegraded` is
/// the "at least degraded" ladder rung (fires on `Degraded` OR
/// `Unhealthy`); `ProbeUnhealthy` fires only on `Unhealthy`. Numeric
/// conditions are inert against a probe source.
let probeBreached (condition: ThresholdCondition) (result: HealthResult) : bool =
    match condition with
    | ProbeUnhealthy ->
        match result with
        | Unhealthy _ -> true
        | _ -> false
    | ProbeDegraded ->
        match result with
        | Degraded _
        | Unhealthy _ -> true
        | _ -> false
    | GreaterThan _
    | LessThan _
    | Equals _ -> false

/// Advance one rule's state for a single tick given whether it is
/// breaching *this* tick. `breaching = None` means "no observation this
/// tick" (metric series absent / probe unregistered) — the state is held
/// unchanged so absence of data neither fires nor spuriously re-arms.
/// `Some false` recovers (re-arms). `Some true` starts / continues the
/// breach window and fires exactly once when it has persisted for
/// `ForDuration`. Returns the new state and whether to deliver now.
let advance (rule: AlertRule) (state: RuleState) (breaching: bool option) (now: DateTime) : RuleState * bool =
    match breaching with
    | None -> state, false
    | Some false -> { BreachingSince = None; Fired = false }, false
    | Some true ->
        let since = state.BreachingSince |> Option.defaultValue now
        let elapsed = now - since

        if not state.Fired && elapsed >= rule.ForDuration then
            {
                BreachingSince = Some since
                Fired = true
            },
            true
        else
            {
                state with
                    BreachingSince = Some since
            },
            false

/// Human-readable one-line description of a firing rule, used as the
/// notification body / SMS text / push body and email body.
let describeBreach (rule: AlertRule) : string =
    let signal =
        match rule.Source with
        | Metric(name, tags) when Map.isEmpty tags -> name
        | Metric(name, tags) ->
            let rendered =
                tags
                |> Map.toList
                |> List.map (fun (k, v) -> sprintf "%s=%s" k v)
                |> String.concat ","

            sprintf "%s{%s}" name rendered
        | HealthProbe probe -> sprintf "probe:%s" probe

    let cond =
        match rule.Condition with
        | GreaterThan t -> sprintf "> %g" t
        | LessThan t -> sprintf "< %g" t
        | Equals t -> sprintf "= %g" t
        | ProbeUnhealthy -> "unhealthy"
        | ProbeDegraded -> "degraded"

    sprintf "[alert:%s] %s %s (sustained %g min)" rule.Name signal cond rule.ForDuration.TotalMinutes

/// Build the `(scopeId, Notification)` pairs a firing rule publishes —
/// one per `DeliverVia` target. `ViaChannel` publishes a `SystemMessage`
/// at the rule's `Severity` under the target scope. `ViaSink` publishes
/// the transactional notification matching the sink kind under the
/// reserved `_platform` scope; the `DispatchingNotificationChannel`
/// decorator routes it to the registered sink. `ViaSink` envelopes carry
/// no recipients — recipient targeting for engine-driven alerts (e.g.
/// resolving platform admins) is a documented follow-up; the seam this
/// phase ships is "route the alert to the sink kind".
let buildNotifications (rule: AlertRule) : (string * Notification) list =
    let body = describeBreach rule
    let subject = sprintf "[Alert] %s" rule.Name
    let correlationId = Some(sprintf "alert:%s" rule.Name)

    rule.DeliverVia
    |> List.map (fun delivery ->
        match delivery with
        | ViaChannel scopeId -> scopeId, SystemMessage(rule.Severity, body)
        | ViaSink sinkKind ->
            let notification =
                match sinkKind with
                | NotificationKind.SinkKind.Email ->
                    TransactionalEmail {
                        RecipientUserIds = []
                        Content = InlineEmail(subject, body, None)
                        CorrelationId = correlationId
                    }
                | NotificationKind.SinkKind.Sms ->
                    TransactionalSms {
                        RecipientUserIds = []
                        Body = body
                        CorrelationId = correlationId
                    }
                | NotificationKind.SinkKind.Push _ ->
                    MobilePush {
                        RecipientUserIds = []
                        Title = subject
                        Body = body
                        DeepLink = None
                        CorrelationId = correlationId
                    }

            NotificationKind.PlatformReservedScope, notification)

/// One evaluation tick over every rule. `readMetric` returns the current
/// scalar for a `(name, tags)` metric series (`None` if absent);
/// `readProbe` runs the named health probe (`None` if unregistered);
/// `publish` is the notification-channel publish. Pure plumbing —
/// testable directly with fake readers, no scheduling. The production
/// `BackgroundService` calls this on its wall-clock-aligned minute
/// schedule.
let runTick
    (readMetric: string -> Map<string, string> -> float option)
    (readProbe: string -> Async<HealthResult option>)
    (publish: string -> Notification -> Async<unit>)
    (rules: AlertRule list)
    (states: ConcurrentDictionary<string, RuleState>)
    (now: DateTime)
    : Async<unit> =
    async {
        for rule in rules do
            let! breaching = async {
                match rule.Source with
                | Metric(name, tags) ->
                    match readMetric name tags with
                    | Some value -> return Some(scalarBreached rule.Condition value)
                    | None -> return None
                | HealthProbe probeName ->
                    let! result = readProbe probeName

                    match result with
                    | Some health -> return Some(probeBreached rule.Condition health)
                    | None -> return None
            }

            let prior =
                match states.TryGetValue rule.Name with
                | true, s -> s
                | false, _ -> initialState

            let next, fire = advance rule prior breaching now
            states[rule.Name] <- next

            if fire then
                for scopeId, notification in buildNotifications rule do
                    do! publish scopeId notification
    }

/// `BackgroundService` host for the periodic engine. Resolves the
/// metric-read tap (`PrometheusMetricsSink`, nullable when metrics are
/// disabled) and the `IHealthCheck` set per tick from the captured
/// `IServiceProvider` so companion probes that register up to
/// end-of-compose are seen. The tick body is `runTick`, which tests call
/// directly without the scheduling overhead.
type AlertRuleEngineService
    (serviceProvider: IServiceProvider, channel: INotificationChannel, rules: AlertRule list, logger: ILogger) =
    inherit BackgroundService()

    let states = ConcurrentDictionary<string, RuleState>()

    /// Metric read via the concrete default sink. `null` (metrics
    /// disabled) ⇒ `None` ⇒ metric rules never fire, health rules still
    /// do.
    let readMetric (name: string) (tags: Map<string, string>) : float option =
        match serviceProvider.GetService typeof<PrometheusMetricsSink> with
        | :? PrometheusMetricsSink as sink -> sink.TryRead(name, tags)
        | _ -> None

    /// Run the named probe. `None` when no probe with that name is
    /// registered. A throwing probe is treated as `Unhealthy` (the same
    /// classification `HealthCheckRunner` applies).
    let readProbe (probeName: string) : Async<HealthResult option> = async {
        let probe =
            serviceProvider.GetServices<IHealthCheck>()
            |> Seq.tryFind (fun p -> p.Name = probeName)

        match probe with
        | None -> return None
        | Some p ->
            try
                let! result = p.Check()
                return Some result
            with ex ->
                logger.Warn(
                    sprintf "[AlertRuleEngine] probe '%s' threw: %s: %s" probeName (ex.GetType().Name) ex.Message
                )

                return Some(Unhealthy(ex.Message))
    }

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // Wall-clock-aligned minute tick — same shape as
            // `HealthStateTracker` / `JobScheduler`. Operators trust
            // aligned-to-minute timestamps over offset-from-startup.
            while not stoppingToken.IsCancellationRequested do
                let now = DateTime.UtcNow

                let nextTick =
                    DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes 1.0

                let delay = nextTick - now

                try
                    if delay > TimeSpan.Zero then
                        do! Task.Delay(delay, stoppingToken)

                    let publish scopeId notification = channel.Publish(scopeId, notification)

                    do!
                        runTick readMetric readProbe publish rules states DateTime.UtcNow
                        |> Async.StartAsTask
                        :> Task
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error($"[AlertRuleEngine] event=tick_wrapper_error nextTick={nextTick:o}", Some ex)
        }
        :> Task