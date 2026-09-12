// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AIAllowlistDiagnosticsTests

// ─── Phase 47 — AI action-denial observability ───────────────────────
//
// Two units under test, both reachable without an HTTP host:
//
//   * `AIAllowlistDiagnosticsHandler.computeRollup` — the pure
//     aggregation behind `/dev/ai-allowlist` AND behind the
//     production-safe `IAIDenialRollupProbe` seam the health-monitor
//     admin panel reads. Testing the shared function rather than
//     either surface is the point: the two cannot report different
//     numbers, because there is one function.
//
//   * `AIDenialRateMonitor` — the sustained-rate alert. The acceptance
//     criterion is "a simulated denial burst crosses the threshold and
//     emits the Owner/Admin `SystemMessage`", so the burst is simulated
//     against a real `INotificationChannel` capture and the published
//     notification is asserted, not just the monitor's boolean.
//
// The clock is injected everywhere. A wall-clock test of a
// rolling-window aggregate is a time bomb (it passes until the machine
// is slow, or until a date rolls); every timestamp here is constructed.

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.AI

// ─── Fixtures ────────────────────────────────────────────────────────

let private baseTime = DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc)

let private denial
    (toolName: string)
    (activeModule: string option)
    (reason: string)
    (scopeId: string)
    (at: DateTime)
    : AIAllowlistDiagnosticsHandler.DenialEventPayload * string * DateTime =
    {
        ToolName = toolName
        Reason = reason
        ActiveModule = activeModule
        ActivePage = None
        TaskId = Guid.Empty
        ConversationId = Guid.Empty
    },
    scopeId,
    at

/// Capturing `INotificationChannel`. Only `Publish` is exercised; the
/// subscription half of the interface is not reached by the monitor.
type private CapturingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.Published = published |> Seq.toList

    interface INotificationChannel with
        member _.Publish(scopeId, notification) =
            published.Enqueue(scopeId, notification)
            async { return () }

        member _.Subscribe(_scopeId, _handler) = async { return Guid.NewGuid() }

        member _.Unsubscribe _ = async { return () }

/// A channel whose `Publish` always throws — the wedged-transport leg.
type private ThrowingChannel() =
    interface INotificationChannel with
        member _.Publish(_scopeId, _notification) = async { return failwith "transport is down" }

        member _.Subscribe(_scopeId, _handler) = async { return Guid.NewGuid() }

        member _.Unsubscribe _ = async { return () }

// ─── Rollup ──────────────────────────────────────────────────────────

let private rollupTests =
    testList "the /dev/ai-allowlist denial rollup" [
        test "counts all-time and in-window separately" {
            let rows = [
                denial "ui.setField" (Some "budget") "not on the allowlist" "team-a" (baseTime.AddMinutes -90.0)
                denial "ui.setField" (Some "budget") "not on the allowlist" "team-a" (baseTime.AddMinutes -30.0)
                denial "ui.click" (Some "budget") "not on the allowlist" "team-a" (baseTime.AddMinutes -5.0)
            ]

            let rollup =
                AIAllowlistDiagnosticsHandler.computeRollup "team-a" baseTime (TimeSpan.FromMinutes 60.0) rows

            Expect.equal rollup.TotalDenialsAllTime 3 "every row is counted all-time"

            Expect.equal
                rollup.TotalDenialsInWindow
                2
                "the row 90 minutes old is outside the 60-minute window — an aged-out burst must not read as current"
        }

        test "rolls up by tool, by module and by scope, ordered by count" {
            let rows = [
                denial "ui.setField" (Some "budget") "denied" "team-a" (baseTime.AddMinutes -1.0)
                denial "ui.setField" (Some "budget") "denied" "team-a" (baseTime.AddMinutes -2.0)
                denial "ui.setField" (Some "forecast") "denied" "team-a" (baseTime.AddMinutes -3.0)
                denial "ui.click" (Some "forecast") "denied" "team-a" (baseTime.AddMinutes -4.0)
            ]

            let rollup =
                AIAllowlistDiagnosticsHandler.computeRollup "team-a" baseTime (TimeSpan.FromMinutes 60.0) rows

            Expect.equal
                (rollup.ByToolName |> List.map (fun g -> g.Key, g.Count))
                [ "ui.setField", 3; "ui.click", 1 ]
                "by-tool is descending by count"

            Expect.equal
                (rollup.ByActiveModule |> List.sumBy _.Count)
                4
                "every in-window row lands in exactly one module bucket"

            Expect.equal
                (rollup.ByScopeId |> List.map (fun g -> g.Key, g.Count))
                [ "team-a", 4 ]
                "the read is caller-scope only, so the scope axis reports the one scope read"
        }

        test "top (tool, module) pairs are the cut neither single axis gives" {
            let rows = [
                denial "ui.setField" (Some "budget") "denied" "team-a" (baseTime.AddMinutes -1.0)
                denial "ui.setField" (Some "budget") "denied" "team-a" (baseTime.AddMinutes -2.0)
                denial "ui.setField" (Some "forecast") "denied" "team-a" (baseTime.AddMinutes -3.0)
            ]

            let rollup =
                AIAllowlistDiagnosticsHandler.computeRollup "team-a" baseTime (TimeSpan.FromMinutes 60.0) rows

            let top = rollup.TopToolModulePairs |> List.head

            Expect.equal top.ToolName "ui.setField" "the hottest pair's tool"
            Expect.equal top.ActiveModule "budget" "the hottest pair's module"
            Expect.equal top.Count 2 "the hottest pair's count"
        }

        test "an absent ActiveModule groups under a real label, never null" {
            let rows = [ denial "ui.setField" None "denied" "team-a" (baseTime.AddMinutes -1.0) ]

            let rollup =
                AIAllowlistDiagnosticsHandler.computeRollup "team-a" baseTime (TimeSpan.FromMinutes 60.0) rows

            Expect.equal
                (rollup.ByActiveModule |> List.map _.Key)
                [ AIAllowlistDiagnosticsHandler.NoModuleLabel ]
                "a turn with no active module must not produce a null grouping key"

            Expect.equal
                (rollup.RecentDenials |> List.map _.ActiveModule)
                [ AIAllowlistDiagnosticsHandler.NoModuleLabel ]
                "and the recent list uses the same label"
        }

        test "the per-minute rate is the number an alert threshold is sized against" {
            let rows =
                [ 1..30 ]
                |> List.map (fun i ->
                    denial "ui.setField" (Some "budget") "denied" "team-a" (baseTime.AddMinutes(float -i)))

            let rollup =
                AIAllowlistDiagnosticsHandler.computeRollup "team-a" baseTime (TimeSpan.FromMinutes 60.0) rows

            Expect.floatClose Accuracy.high rollup.DenialsPerMinute 0.5 "30 denials over a 60-minute window is 0.5/min"
        }

        test "recent denials are newest-first and capped" {
            let rows =
                [ 1..40 ]
                |> List.map (fun i ->
                    denial $"ui.tool{i}" (Some "budget") "denied" "team-a" (baseTime.AddMinutes(float -i)))

            let rollup =
                AIAllowlistDiagnosticsHandler.computeRollup "team-a" baseTime (TimeSpan.FromMinutes 60.0) rows

            Expect.equal rollup.RecentDenials.Length 20 "the recent list is bounded"

            Expect.equal
                (rollup.RecentDenials |> List.head).ToolName
                "ui.tool1"
                "newest first — an operator reads the most recent refusal at the top"
        }

        test "an empty window reports zero rather than dividing by nothing" {
            let rollup =
                AIAllowlistDiagnosticsHandler.computeRollup "team-a" baseTime (TimeSpan.FromMinutes 60.0) []

            Expect.equal rollup.TotalDenialsInWindow 0 "no rows"
            Expect.floatClose Accuracy.high rollup.DenialsPerMinute 0.0 "and no rate"
            Expect.isEmpty rollup.RecentDenials "and nothing recent"
        }
    ]

// ─── Reason sanitisation ─────────────────────────────────────────────

let private sanitisationTests =
    testList "denial-reason sanitisation" [
        test "control characters are stripped and whitespace collapsed" {
            let raw = "denied:\r\n\tfield  'amount' not allowed"
            let cleaned = AIAllowlistDiagnosticsHandler.sanitiseReason raw

            Expect.isFalse (cleaned |> Seq.exists Char.IsControl) "no control character survives"

            Expect.equal
                cleaned
                "denied: field 'amount' not allowed"
                "runs of whitespace collapse to one space so a console/log view cannot be smuggled a newline"
        }

        test "an over-long reason is truncated with an ellipsis" {
            let raw = String.replicate 400 "x"
            let cleaned = AIAllowlistDiagnosticsHandler.sanitiseReason raw

            Expect.isTrue (cleaned.Length <= 201) "the rendered reason is bounded"
            Expect.stringEnds cleaned "…" "and the truncation is visible rather than silent"
        }

        test "an absent reason reads as absent, not as a blank cell" {
            Expect.equal
                (AIAllowlistDiagnosticsHandler.sanitiseReason "   ")
                "(no reason recorded)"
                "a whitespace-only reason must not render as an empty cell that looks like a UI bug"
        }
    ]

// ─── Rate monitor ────────────────────────────────────────────────────

let private policy: AIDenialRateMonitor.AIDenialRateAlertPolicy = {
    Threshold = 5
    Window = TimeSpan.FromMinutes 5.0
    Cooldown = TimeSpan.FromMinutes 30.0
    AlertScopeId = None
}

/// Mutable clock the monitor reads; each test drives it explicitly.
type private TestClock(start: DateTime) =
    let mutable now = start
    member _.Now = now
    member _.Advance(span: TimeSpan) = now <- now + span
    member this.Read = fun () -> this.Now

let private monitorTests =
    testList "the sustained action-denial rate monitor" [
        test "a burst crossing the threshold trips exactly once" {
            let clock = TestClock baseTime
            let monitor = AIDenialRateMonitor.AIDenialRateMonitor(policy, clock.Read)

            let trips = [ 1..10 ] |> List.map (fun _ -> monitor.Observe "team-a")

            Expect.equal
                (trips |> List.filter id |> List.length)
                1
                "ten denials in one burst are ONE incident — a per-denial alert would be the denial-of-service"

            Expect.equal
                (trips |> List.findIndex id)
                4
                "and it trips on the fifth denial, the one that reaches the threshold"
        }

        test "the cooldown expires and a fresh campaign alerts again" {
            let clock = TestClock baseTime
            let monitor = AIDenialRateMonitor.AIDenialRateMonitor(policy, clock.Read)

            for _ in 1..5 do
                monitor.Observe "team-a" |> ignore

            clock.Advance(TimeSpan.FromMinutes 31.0)

            let trips = [ 1..5 ] |> List.map (fun _ -> monitor.Observe "team-a")

            Expect.isTrue
                (trips |> List.exists id)
                "past the cooldown a second campaign is a second incident and must page again"
        }

        test "denials that age out of the window do not accumulate into a false alert" {
            let clock = TestClock baseTime
            let monitor = AIDenialRateMonitor.AIDenialRateMonitor(policy, clock.Read)

            // Four denials, then a gap longer than the window, then four
            // more. Eight denials total, never five inside one window.
            for _ in 1..4 do
                monitor.Observe "team-a" |> ignore

            clock.Advance(TimeSpan.FromMinutes 6.0)

            let trips = [ 1..4 ] |> List.map (fun _ -> monitor.Observe "team-a")

            Expect.isFalse
                (trips |> List.exists id)
                "a trickle of denials over hours is not a campaign; only the trailing window counts"
        }

        test "scopes are counted independently" {
            let clock = TestClock baseTime
            let monitor = AIDenialRateMonitor.AIDenialRateMonitor(policy, clock.Read)

            let trips =
                [ 1..4 ]
                |> List.collect (fun _ -> [ monitor.Observe "team-a"; monitor.Observe "team-b" ])

            Expect.isFalse
                (trips |> List.exists id)
                "four denials each across two scopes is not five in either — cross-scope aggregation would page on noise"
        }

        test "a crossing burst publishes one Owner/Admin SystemMessage" {
            let clock = TestClock baseTime
            let monitor = AIDenialRateMonitor.AIDenialRateMonitor(policy, clock.Read)
            let channel = CapturingChannel()

            for _ in 1..10 do
                AIDenialRateMonitor.observeAndAlert
                    (Some monitor)
                    (Some(channel :> INotificationChannel))
                    "team-a"
                    "ui.setField"
                |> Async.RunSynchronously

            Expect.equal channel.Published.Length 1 "one notification for one campaign"

            let scopeId, notification = channel.Published |> List.head

            Expect.equal scopeId "team-a" "published to the scope the denials occurred in by default"

            match notification with
            | Notification.SystemMessage(level, text) ->
                Expect.equal level SystemMessageLevel.Warning "a refused-action campaign is a warning, not info"
                Expect.stringContains text "team-a" "the alert names the scope"
                Expect.stringContains text "ui.setField" "and the tool that tripped it"
            | other -> failtestf "expected a SystemMessage notification, got %A" other
        }

        test "AlertScopeId redirects the alert to a fixed operator channel" {
            let clock = TestClock baseTime

            let monitor =
                AIDenialRateMonitor.AIDenialRateMonitor(
                    {
                        policy with
                            AlertScopeId = Some "_platform"
                    },
                    clock.Read
                )

            let channel = CapturingChannel()

            for _ in 1..5 do
                AIDenialRateMonitor.observeAndAlert
                    (Some monitor)
                    (Some(channel :> INotificationChannel))
                    "team-a"
                    "ui.setField"
                |> Async.RunSynchronously

            let scopeId, _ = channel.Published |> List.head

            Expect.equal
                scopeId
                "_platform"
                "a multi-tenant deployment must be able to route the alert away from the tenant being probed"

            Expect.stringContains
                (match channel.Published |> List.head |> snd with
                 | Notification.SystemMessage(_, text) -> text
                 | other -> string other)
                "team-a"
                "while still naming the scope that tripped it"
        }

        test "no policy declared means nothing is published" {
            let channel = CapturingChannel()

            for _ in 1..50 do
                AIDenialRateMonitor.observeAndAlert None (Some(channel :> INotificationChannel)) "team-a" "ui.setField"
                |> Async.RunSynchronously

            Expect.isEmpty channel.Published "a deployment that never opted in pays nothing and hears nothing (GP 13)"
        }

        test "a wedged notification channel never surfaces to the caller" {
            let clock = TestClock baseTime
            let monitor = AIDenialRateMonitor.AIDenialRateMonitor(policy, clock.Read)

            // Would throw if the publish failure escaped. The refusal is
            // already enforced and audited by the time this runs, so a
            // dropped alert must not become a failed conversation.
            for _ in 1..10 do
                AIDenialRateMonitor.observeAndAlert
                    (Some monitor)
                    (Some(ThrowingChannel() :> INotificationChannel))
                    "team-a"
                    "ui.setField"
                |> Async.RunSynchronously

            Expect.isTrue true "publish failures are swallowed"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 47 — AI action-denial observability" [ rollupTests; sanitisationTests; monitorTests ]