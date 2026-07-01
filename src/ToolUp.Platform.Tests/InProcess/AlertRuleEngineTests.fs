module ToolUp.Platform.Tests.InProcess.AlertRuleEngineTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.AlertRuleEngine

// ─── Phase 178 — AlertRuleEngine tests ───────────────────────────────
//
// Covers the debounce/re-arm state machine, source→condition matching
// (metric scalar + health probe), the two delivery paths (ViaChannel →
// SystemMessage on the target scope; ViaSink → matching transactional
// kind), and the GP 13 default-off contract. The `BackgroundService`
// scheduling loop itself is not exercised — the testable surface is the
// pure `runTick`, which the production service calls inline; passing a
// scripted `now` per tick models time passing without a real wall-clock
// wait. End-to-end sink routing (transactional envelope → INotificationSink)
// is `TransactionalDispatcherTests`' domain — here we assert the engine's
// delivery *boundary*: it publishes the notification kind that the
// `DispatchingNotificationChannel` decorator routes to the matching sink.

let private baseTime = DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc)

let private capturing () =
    let captured = List<string * Notification>()
    let publish (scopeId: string) (n: Notification) = async { captured.Add((scopeId, n)) }
    captured, publish

/// Drive a metric rule across a scripted `(offsetMinutes, value option)`
/// series. `value = None` models an absent metric series. Returns the
/// captured `(scopeId, notification)` publishes in order.
let private driveMetric (rule: AlertRule) (script: (float * float option) list) = async {
    let captured, publish = capturing ()
    let states = ConcurrentDictionary<string, RuleState>()
    let current = ref None
    let readMetric _ _ = current.Value
    let readProbe _ = async { return None }

    for offsetMin, value in script do
        current.Value <- value
        do! runTick readMetric readProbe publish [ rule ] states (baseTime.AddMinutes offsetMin)

    return captured |> List.ofSeq
}

/// Drive a health-probe rule across a scripted `(offsetMinutes, result
/// option)` series. `result = None` models an unregistered probe.
let private driveProbe (rule: AlertRule) (script: (float * HealthResult option) list) = async {
    let captured, publish = capturing ()
    let states = ConcurrentDictionary<string, RuleState>()
    let current = ref None
    let readMetric _ _ = None
    let readProbe _ = async { return current.Value }

    for offsetMin, result in script do
        current.Value <- result
        do! runTick readMetric readProbe publish [ rule ] states (baseTime.AddMinutes offsetMin)

    return captured |> List.ofSeq
}

let private metricRule condition forDuration delivery = {
    Name = "cpu-high"
    Source = Metric("cpu.load", Map.empty)
    Condition = condition
    ForDuration = forDuration
    Severity = SystemMessageLevel.Warning
    DeliverVia = delivery
}

let private stubChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }
    }

let private stubLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let tests =
    testList "AlertRuleEngine" [
        testCaseAsync "Sub-window breach does not fire (debounce)"
        <| async {
            // Breaching from minute 0-2 but ForDuration is 5 minutes —
            // the window never closes, so nothing fires.
            let rule =
                metricRule (GreaterThan 80.0) (TimeSpan.FromMinutes 5.0) [ ViaChannel "team-1" ]

            let! captured = driveMetric rule [ 0.0, Some 90.0; 1.0, Some 90.0; 2.0, Some 90.0 ]

            Expect.isEmpty captured "a breach shorter than ForDuration must not fire"
        }

        testCaseAsync "Persistent breach fires exactly once, then re-arms only after recovery"
        <| async {
            let rule =
                metricRule (GreaterThan 80.0) (TimeSpan.FromMinutes 5.0) [ ViaChannel "team-1" ]

            let! captured =
                driveMetric rule [
                    0.0, Some 90.0 // breach begins
                    2.0, Some 90.0 // elapsed 2 < 5 — no fire
                    6.0, Some 90.0 // elapsed 6 >= 5 — FIRE #1
                    7.0, Some 90.0 // still breaching, already fired — no fire
                    8.0, Some 40.0 // recovered — re-arm
                    9.0, Some 90.0 // breach begins again
                    11.0, Some 90.0 // elapsed 2 < 5 — no fire
                    15.0, Some 90.0 // elapsed 6 >= 5 — FIRE #2
                ]

            Expect.equal captured.Length 2 "one fire per sustained episode: once per breach, re-armed after recovery"

            Expect.all
                captured
                (fun (scopeId, n) ->
                    scopeId = "team-1"
                    && (match n with
                        | SystemMessage(SystemMessageLevel.Warning, _) -> true
                        | _ -> false))
                "each fire is a Warning SystemMessage on the rule's scope"
        }

        testCaseAsync "No recovery between two breach spikes means still exactly one fire"
        <| async {
            // A single uninterrupted breach across a long window fires
            // once, never re-arming while it stays breaching.
            let rule =
                metricRule (GreaterThan 80.0) (TimeSpan.FromMinutes 3.0) [ ViaChannel "team-1" ]

            let! captured =
                driveMetric rule [
                    0.0, Some 90.0
                    4.0, Some 90.0 // FIRE
                    8.0, Some 90.0
                    12.0, Some 90.0
                    16.0, Some 90.0
                ]

            Expect.equal captured.Length 1 "a sustained breach fires exactly once until it recovers"
        }

        testCaseAsync "Absent metric series holds state (neither fires nor re-arms)"
        <| async {
            let rule =
                metricRule (GreaterThan 80.0) (TimeSpan.FromMinutes 5.0) [ ViaChannel "team-1" ]

            // Breach starts, then a gap where the series can't be read
            // (None), then the window closes while still breaching. The
            // None ticks must not reset the breach window.
            let! captured =
                driveMetric rule [
                    0.0, Some 90.0 // breach begins (since = 0)
                    2.0, None // no observation — hold
                    3.0, None // hold
                    6.0, Some 90.0 // elapsed 6 >= 5 — FIRE (window survived the gap)
                ]

            Expect.equal captured.Length 1 "an absent reading holds the breach window rather than re-arming"
        }

        testCaseAsync "LessThan condition fires on a value below the bound"
        <| async {
            let rule = metricRule (LessThan 10.0) TimeSpan.Zero [ ViaChannel "team-1" ]

            let! captured = driveMetric rule [ 0.0, Some 3.0 ]

            Expect.equal captured.Length 1 "LessThan fires when the reading drops below the bound"
        }

        testCaseAsync "ProbeUnhealthy fires when the named probe goes unhealthy"
        <| async {
            let rule = {
                Name = "redis-down"
                Source = HealthProbe "redis"
                Condition = ProbeUnhealthy
                ForDuration = TimeSpan.Zero
                Severity = SystemMessageLevel.Error
                DeliverVia = [ ViaChannel "_platform" ]
            }

            let! captured = driveProbe rule [ 0.0, Some Healthy; 1.0, Some(Unhealthy "connection refused") ]

            Expect.equal captured.Length 1 "an Unhealthy probe fires the rule exactly once"

            let scopeId, notification = captured.Head
            Expect.equal scopeId "_platform" "probe alert lands on the declared scope"

            match notification with
            | SystemMessage(SystemMessageLevel.Error, _) -> ()
            | other -> failtestf "expected an Error SystemMessage, got %A" other
        }

        testCaseAsync "ProbeUnhealthy does not fire on a merely Degraded probe"
        <| async {
            let rule = {
                Name = "redis-degraded"
                Source = HealthProbe "redis"
                Condition = ProbeUnhealthy
                ForDuration = TimeSpan.Zero
                Severity = SystemMessageLevel.Warning
                DeliverVia = [ ViaChannel "_platform" ]
            }

            let! captured = driveProbe rule [ 0.0, Some(Degraded "slow"); 1.0, Some(Degraded "slow") ]

            Expect.isEmpty captured "Degraded does not satisfy the ProbeUnhealthy condition"
        }

        testCaseAsync "ProbeDegraded fires on Degraded (the 'at least degraded' rung)"
        <| async {
            let rule = {
                Name = "redis-degraded"
                Source = HealthProbe "redis"
                Condition = ProbeDegraded
                ForDuration = TimeSpan.Zero
                Severity = SystemMessageLevel.Warning
                DeliverVia = [ ViaChannel "_platform" ]
            }

            let! captured = driveProbe rule [ 0.0, Some(Degraded "slow") ]

            Expect.equal captured.Length 1 "ProbeDegraded fires on a Degraded reading"
        }

        testCaseAsync "ViaChannel delivers a SystemMessage on the target scope"
        <| async {
            let rule = metricRule (GreaterThan 80.0) TimeSpan.Zero [ ViaChannel "team-42" ]

            let! captured = driveMetric rule [ 0.0, Some 99.0 ]

            Expect.equal captured.Length 1 "one delivery"
            let scopeId, notification = captured.Head
            Expect.equal scopeId "team-42" "delivered on the rule's declared channel scope"

            match notification with
            | SystemMessage(SystemMessageLevel.Warning, text) ->
                Expect.stringContains text "cpu-high" "body names the firing rule"
            | other -> failtestf "expected a SystemMessage, got %A" other
        }

        testCaseAsync "ViaSink publishes the transactional kind matching the sink"
        <| async {
            // The engine's delivery boundary: a ViaSink target publishes
            // the matching transactional notification under the reserved
            // _platform scope. The DispatchingNotificationChannel decorator
            // then routes it to the registered INotificationSink of that
            // kind (covered end-to-end by TransactionalDispatcherTests).
            let emailRule =
                metricRule (GreaterThan 80.0) TimeSpan.Zero [ ViaSink NotificationKind.SinkKind.Email ]

            let smsRule =
                metricRule (GreaterThan 80.0) TimeSpan.Zero [ ViaSink NotificationKind.SinkKind.Sms ]

            let pushRule =
                metricRule (GreaterThan 80.0) TimeSpan.Zero [
                    ViaSink(NotificationKind.SinkKind.Push NotificationKind.PushVariant.WebPush)
                ]

            let! email = driveMetric emailRule [ 0.0, Some 99.0 ]
            let! sms = driveMetric smsRule [ 0.0, Some 99.0 ]
            let! push = driveMetric pushRule [ 0.0, Some 99.0 ]

            let kindOf captured =
                captured
                |> List.map (fun (scope, n) -> scope, NotificationKind.ofNotification n)

            Expect.equal
                (kindOf email)
                [ NotificationKind.PlatformReservedScope, NotificationKind.TransactionalEmail ]
                "Email sink → TransactionalEmail on _platform"

            Expect.equal
                (kindOf sms)
                [ NotificationKind.PlatformReservedScope, NotificationKind.TransactionalSms ]
                "Sms sink → TransactionalSms on _platform"

            Expect.equal
                (kindOf push)
                [ NotificationKind.PlatformReservedScope, NotificationKind.MobilePush ]
                "Push sink → MobilePush on _platform"
        }

        testCaseAsync "Multiple DeliverVia targets each receive the firing"
        <| async {
            let rule =
                metricRule (GreaterThan 80.0) TimeSpan.Zero [
                    ViaChannel "team-1"
                    ViaSink NotificationKind.SinkKind.Email
                ]

            let! captured = driveMetric rule [ 0.0, Some 99.0 ]

            Expect.equal captured.Length 2 "both delivery targets fire"
        }

        testCase "GP 13: an empty AlertRules set registers no BackgroundService"
        <| fun _ ->
            // Faithful to the real gate: call the compose registration
            // helper with an empty vs non-empty rule set against the
            // default (AllInOne / KestrelHost) profile.
            let hosted (rules: AlertRule list) =
                let services = ServiceCollection()

                ComposeRuntimeServices.registerAlertRuleEngine
                    services
                    {
                        ServerConfig.defaults with
                            AlertRules = rules
                    }
                    stubChannel
                    stubLogger

                let sp = services.BuildServiceProvider()
                sp.GetServices<IHostedService>() |> Seq.toList

            Expect.isEmpty (hosted []) "no engine hosted when AlertRules is empty"

            let withRule =
                hosted [ metricRule (GreaterThan 80.0) TimeSpan.Zero [ ViaChannel "team-1" ] ]

            Expect.equal withRule.Length 1 "exactly one engine hosted when a rule is declared"
            Expect.isTrue (withRule.Head :? AlertRuleEngineService) "the hosted service is the AlertRuleEngineService"
    ]