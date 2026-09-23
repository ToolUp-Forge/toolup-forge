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

/// The watched signal rendered as one token — `name`, `name{k=v,…}`
/// (tags in key order) or `probe:<name>`. The notification body and the
/// Phase 9x Alerts tab both render a rule's signal through this, so the
/// two never disagree about what a rule watches.
let describeSignal (source: AlertSource) : string =
    match source with
    | Metric(name, tags) when Map.isEmpty tags -> name
    | Metric(name, tags) ->
        let rendered =
            tags
            |> Map.toList
            |> List.map (fun (k, v) -> sprintf "%s=%s" k v)
            |> String.concat ","

        sprintf "%s{%s}" name rendered
    | HealthProbe probe -> sprintf "probe:%s" probe

/// The breach condition rendered as one token — `> 5`, `unhealthy`, …
/// Shared by the notification body and the Phase 9x Alerts tab.
let describeCondition (condition: ThresholdCondition) : string =
    match condition with
    | GreaterThan t -> sprintf "> %g" t
    | LessThan t -> sprintf "< %g" t
    | Equals t -> sprintf "= %g" t
    | ProbeUnhealthy -> "unhealthy"
    | ProbeDegraded -> "degraded"

/// Human-readable one-line description of a firing rule, used as the
/// notification body / SMS text / push body and email body.
let describeBreach (rule: AlertRule) : string =
    sprintf
        "[alert:%s] %s %s (sustained %g min)"
        rule.Name
        (describeSignal rule.Source)
        (describeCondition rule.Condition)
        rule.ForDuration.TotalMinutes

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
                        Recipients = []
                        Content = InlineEmail(subject, body, None)
                        CorrelationId = correlationId
                    }
                | NotificationKind.SinkKind.Sms ->
                    TransactionalSms {
                        Recipients = []
                        Body = body
                        CorrelationId = correlationId
                    }
                | NotificationKind.SinkKind.Push _ ->
                    MobilePush {
                        Recipients = []
                        Title = subject
                        Body = body
                        DeepLink = None
                        CorrelationId = correlationId
                    }
                // Phase 827 — a free-form body, like the SMS arm. An
                // alert has no approved template to name, so once
                // recipient targeting lands every recipient outside the
                // 24-hour window is refused server-side, audited.
                | NotificationKind.SinkKind.WhatsApp ->
                    TransactionalWhatsApp {
                        Recipients = []
                        TemplateName = None
                        TemplateLanguage = None
                        TemplateParameters = []
                        Body = Some body
                        Metadata = Map.empty
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

// ─── Phase 9x — the rule-state read surface ───────────────────────────
//
// The engine's per-rule `RuleState` map is private to the running
// service, so nothing could answer "which rules are firing" short of
// waiting for a notification. The Observability admin module's Alerts
// tab needs exactly that, so each tick now also records an
// `AlertRuleObservation` per rule on a DI singleton the handler reads.
// The evaluation itself is untouched: `runTickObserved` wraps `runTick`
// rather than changing it, so the fire / re-arm semantics (and every
// Phase 178 test of them) stay what they were.
//
// Like the breach window it mirrors, the board is in-memory and
// per-process — a silo that does not host the engine reports every rule
// as `NotEvaluated`, which is the honest answer from that process.

/// The latest `AlertRuleObservation` per rule name, written by the
/// engine after every tick and read by the Phase 9x alerts endpoint.
/// Registered as a singleton only when the deployment declares at least
/// one rule (GP 13).
type AlertRuleStatusBoard() =
    let observations = ConcurrentDictionary<string, AlertRuleObservation>()

    /// Record the observation for `ruleName`, replacing the previous one.
    member _.Record(ruleName: string, observation: AlertRuleObservation) : unit = observations[ruleName] <- observation

    /// The latest observation for `ruleName`, or `None` when no tick has
    /// recorded one.
    member _.TryGet(ruleName: string) : AlertRuleObservation option =
        match observations.TryGetValue ruleName with
        | true, observation -> Some observation
        | false, _ -> None

    /// The latest observation for `ruleName`, or
    /// `AlertRuleObservation.notEvaluated` when no tick has recorded one.
    member this.Get(ruleName: string) : AlertRuleObservation =
        this.TryGet ruleName |> Option.defaultValue AlertRuleObservation.notEvaluated

/// Derive a rule's observation from one tick. `hadData` is whether the
/// tick could read the rule's signal; `before` / `after` are the rule's
/// `RuleState` either side of `advance`. Pure — the whole decision
/// surface of the board, tested without a tick.
///
/// A fire is `Fired` going `false → true`; a clear is `Fired` going
/// `true → false` (a recovery re-arms the rule). A tick with no data
/// reports `NoData` — the engine holds the rule's state unchanged then,
/// so the last fire / clear instants carry over untouched.
let observe
    (previous: AlertRuleObservation)
    (hadData: bool)
    (before: RuleState)
    (after: RuleState)
    (now: DateTime)
    : AlertRuleObservation =
    let state =
        if not hadData then AlertRuleState.NoData
        elif after.Fired then AlertRuleState.Firing
        elif after.BreachingSince.IsSome then AlertRuleState.Pending
        else AlertRuleState.Clear

    {
        State = state
        BreachingSinceUtc = after.BreachingSince
        LastFiredUtc =
            if after.Fired && not before.Fired then
                Some now
            else
                previous.LastFiredUtc
        LastClearedUtc =
            if before.Fired && not after.Fired then
                Some now
            else
                previous.LastClearedUtc
        LastEvaluatedUtc = Some now
    }

/// `runTick`, then record every rule's observation on `board`. The two
/// readers are wrapped only to note whether each signal answered —
/// `runTick` itself, and so the evaluation, is unchanged. Signals are
/// keyed by `describeSignal`, so two rules watching one series share
/// one read outcome.
let runTickObserved
    (readMetric: string -> Map<string, string> -> float option)
    (readProbe: string -> Async<HealthResult option>)
    (publish: string -> Notification -> Async<unit>)
    (rules: AlertRule list)
    (states: ConcurrentDictionary<string, RuleState>)
    (board: AlertRuleStatusBoard)
    (now: DateTime)
    : Async<unit> =
    async {
        let answered = ConcurrentDictionary<string, bool>()

        let observedMetric (name: string) (tags: Map<string, string>) =
            let value = readMetric name tags
            answered[describeSignal (Metric(name, tags))] <- value.IsSome
            value

        let observedProbe (probeName: string) = async {
            let! result = readProbe probeName
            answered[describeSignal (HealthProbe probeName)] <- result.IsSome
            return result
        }

        let stateOf (ruleName: string) =
            match states.TryGetValue ruleName with
            | true, s -> s
            | false, _ -> initialState

        let before = rules |> List.map (fun rule -> stateOf rule.Name)

        do! runTick observedMetric observedProbe publish rules states now

        for rule, prior in List.zip rules before do
            let hadData =
                match answered.TryGetValue(describeSignal rule.Source) with
                | true, value -> value
                | false, _ -> false

            board.Record(rule.Name, observe (board.Get rule.Name) hadData prior (stateOf rule.Name) now)
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

                    // Phase 9x — record per-rule observations when the
                    // status board is composed (it is whenever rules are
                    // declared); resolved per tick like the readers.
                    let tick =
                        match serviceProvider.GetService typeof<AlertRuleStatusBoard> with
                        | :? AlertRuleStatusBoard as board ->
                            runTickObserved readMetric readProbe publish rules states board DateTime.UtcNow
                        | _ -> runTick readMetric readProbe publish rules states DateTime.UtcNow

                    do! tick |> Async.StartAsTask :> Task
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error($"[AlertRuleEngine] event=tick_wrapper_error nextTick={nextTick:o}", Some ex)
        }
        :> Task