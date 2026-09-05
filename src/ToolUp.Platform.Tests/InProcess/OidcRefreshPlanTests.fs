// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcRefreshPlanTests

open Expecto
open ToolUp.Platform
// After `ToolUp.Platform`, deliberately: the OIDC companion's own
// `AuthError` must shadow the platform's `OAuthError`, whose
// `NetworkError` case has the same name and would otherwise win.
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcClient

// ─── Phase 755 — the OIDC pre-expiry refresh POLICY ──────────────────
//
// The timer itself is browser-bound (`setTimeout`, `localStorage`,
// `navigator.onLine`, `document.visibilityState`); everything it
// DECIDES is not. `RefreshPlan` is total functions over an explicitly
// supplied clock, online flag and expiry, so the whole decision
// surface is exercised here from .NET with a fake clock and no browser
// — the same split `classifyStoredTokenWith` made for the stored-token
// classifier, for the same reason.
//
// What is covered:
//   * arming arithmetic — `exp − margin`, the floor, the
//     no-readable-`exp` fallback, and the consumer knobs feeding them;
//   * the woken-background-tab decision, including the `Idle` answer
//     that deliberately does NOT re-arm;
//   * single-flight coalescing;
//   * the failure classification that separates "the issuer refused"
//     (sign-in required) from "we never reached the issuer" (retry).
//
// What is NOT covered here, and honestly so: that `scheduleRefresh`
// wires these answers to real timers and real listeners. That is
// browser behaviour; the Fable compile gate proves it compiles, and
// the wiring is deliberately thin enough to read.

let private policyOf (p: OidcRefreshPolicy) = OidcRefreshPolicy.resolve (Some p)
let private defaultPolicy () = OidcRefreshPolicy.resolve None

/// Fixed clock anchor (~2027); every test rolls `exp` around it rather
/// than reading a real clock, so nothing here rots on a date.
let private now = 1_800_000_000.0

let tests: Test =
    testList "OidcClient.RefreshPlan" [

        // ─── resolve: the GP 11 guarantee, stated as a test ──────────

        testList "OidcRefreshPolicy.resolve" [
            testCase "None reproduces the margins the timer shipped with"
            <| fun () ->
                let p = defaultPolicy ()
                Expect.isTrue p.Enabled "armed by default — always-on is the decision"
                Expect.equal p.SafetyMarginSeconds 60.0 "shipped margin"
                Expect.equal p.FallbackSeconds 300.0 "shipped fallback cadence"
                Expect.equal p.MinDelaySeconds 5.0 "shipped floor"
                Expect.isTrue p.RefreshOnWake "wake path on by default"

            testCase "an all-None policy resolves identically to no policy at all"
            <| fun () -> Expect.equal (policyOf (OidcRefreshPolicy.none ())) (defaultPolicy ()) ""

            testCase "explicit knobs are honoured"
            <| fun () ->
                let p =
                    policyOf {
                        Enabled = Some false
                        SafetyMarginSeconds = Some 120.0
                        FallbackSeconds = Some 900.0
                        RefreshOnWake = Some false
                    }

                Expect.isFalse p.Enabled ""
                Expect.equal p.SafetyMarginSeconds 120.0 ""
                Expect.equal p.FallbackSeconds 900.0 ""
                Expect.isFalse p.RefreshOnWake ""

            // A `nan` margin propagates through every comparison as
            // `false` and would arm a timer that never fires — the
            // exact failure this phase exists to remove. Rejecting it
            // in favour of the default is the safe reading; a consumer
            // who wants no timer says so with `Enabled = Some false`.
            testCase "non-positive and non-finite numbers fall back to the defaults"
            <| fun () ->
                let bad (v: float) =
                    policyOf {
                        OidcRefreshPolicy.none () with
                            SafetyMarginSeconds = Some v
                            FallbackSeconds = Some v
                    }

                for v in [ 0.0; -30.0; nan; infinity ] do
                    let p = bad v
                    Expect.equal p.SafetyMarginSeconds 60.0 (sprintf "margin rejected %f" v)
                    Expect.equal p.FallbackSeconds 300.0 (sprintf "fallback rejected %f" v)
        ]

        // ─── delaySeconds: the arming arithmetic ─────────────────────

        testList "delaySeconds" [
            testCase "a readable exp schedules the refresh one margin ahead of it"
            <| fun () ->
                let delay = RefreshPlan.delaySeconds (defaultPolicy ()) now (Some(now + 3600.0))
                Expect.equal delay 3540.0 "3600s token, 60s margin"

            testCase "no readable exp falls back to the fixed cadence, less the margin"
            <| fun () ->
                let delay = RefreshPlan.delaySeconds (defaultPolicy ()) now None
                Expect.equal delay 240.0 "300s fallback, 60s margin"

            testCase "a token already inside its margin still yields a positive delay"
            <| fun () ->
                let delay = RefreshPlan.delaySeconds (defaultPolicy ()) now (Some(now + 30.0))
                Expect.equal delay 5.0 "floored, not -30"

            testCase "an already-expired token yields the floor, never a negative delay"
            <| fun () ->
                let delay = RefreshPlan.delaySeconds (defaultPolicy ()) now (Some(now - 600.0))
                Expect.equal delay 5.0 ""

            testCase "a margin wider than the whole token lifetime degrades to the floor"
            <| fun () ->
                let policy =
                    policyOf {
                        OidcRefreshPolicy.none () with
                            SafetyMarginSeconds = Some 7200.0
                    }

                Expect.equal (RefreshPlan.delaySeconds policy now (Some(now + 3600.0))) 5.0 ""

            testCase "the consumer's margin and fallback both feed the arithmetic"
            <| fun () ->
                let policy =
                    policyOf {
                        OidcRefreshPolicy.none () with
                            SafetyMarginSeconds = Some 120.0
                            FallbackSeconds = Some 900.0
                    }

                Expect.equal (RefreshPlan.delaySeconds policy now (Some(now + 3600.0))) 3480.0 "exp path"
                Expect.equal (RefreshPlan.delaySeconds policy now None) 780.0 "fallback path"
        ]

        // ─── onArm ───────────────────────────────────────────────────

        testList "onArm" [
            testCase "arms against a readable expiry"
            <| fun () ->
                let action = RefreshPlan.onArm (defaultPolicy ()) true now (Some(now + 3600.0))
                Expect.equal action (ArmTimer 3540.0) ""

            testCase "no session — nothing to refresh"
            <| fun () -> Expect.equal (RefreshPlan.onArm (defaultPolicy ()) false now (Some(now + 3600.0))) Idle ""

            testCase "the deliberate opt-out arms nothing"
            <| fun () ->
                let policy =
                    policyOf {
                        OidcRefreshPolicy.none () with
                            Enabled = Some false
                    }

                Expect.equal (RefreshPlan.onArm policy true now (Some(now + 3600.0))) Idle ""

            // Arming is free; a request is not. An offline session
            // still arms — the offline question is asked when the
            // timer FIRES.
            testCase "arming does not consult the network"
            <| fun () ->
                let action = RefreshPlan.onArm (defaultPolicy ()) true now None
                Expect.equal action (ArmTimer 240.0) ""
        ]

        // ─── onTimer ─────────────────────────────────────────────────

        testList "onTimer" [
            testCase "online — refresh now"
            <| fun () -> Expect.equal (RefreshPlan.onTimer (defaultPolicy ()) true) RefreshNow ""

            // A refresh with no link cannot succeed, and the failure it
            // WOULD produce is indistinguishable at the call site from
            // an issuer refusing the grant — which is how an offline
            // moment used to end a live session.
            testCase "offline — make no request, look again shortly"
            <| fun () ->
                let policy = defaultPolicy ()
                Expect.equal (RefreshPlan.onTimer policy false) (ArmTimer policy.RetrySeconds) ""
        ]

        // ─── onWake ──────────────────────────────────────────────────

        testList "onWake" [
            // Browsers throttle timers in background tabs, so a tab
            // parked for an hour wakes with a timer that has not fired
            // and a bearer that has already expired.
            testCase "woken past the margin — refresh at once"
            <| fun () ->
                let action = RefreshPlan.onWake (defaultPolicy ()) true true now (Some(now + 30.0))
                Expect.equal action RefreshNow "30s left against a 60s margin"

            testCase "woken with an expired bearer — refresh at once"
            <| fun () ->
                let action = RefreshPlan.onWake (defaultPolicy ()) true true now (Some(now - 600.0))
                Expect.equal action RefreshNow ""

            testCase "exactly on the margin boundary counts as past it"
            <| fun () ->
                let action = RefreshPlan.onWake (defaultPolicy ()) true true now (Some(now + 60.0))
                Expect.equal action RefreshNow ""

            // NOT a re-arm. A re-arm would restart the delay on every
            // tab-focus, and for the no-readable-exp case a user who
            // switches tabs more often than the cadence would push the
            // refresh out forever — silent starvation presenting as
            // the very expired-session bug this phase closes.
            testCase "woken well inside the token's life — leave the armed timer alone"
            <| fun () ->
                let action =
                    RefreshPlan.onWake (defaultPolicy ()) true true now (Some(now + 3600.0))

                Expect.equal action Idle ""

            testCase "woken with no readable exp — leave the armed timer alone"
            <| fun () -> Expect.equal (RefreshPlan.onWake (defaultPolicy ()) true true now None) Idle ""

            testCase "offline — a wake means nothing; the armed timer's own retry covers it"
            <| fun () -> Expect.equal (RefreshPlan.onWake (defaultPolicy ()) false true now (Some(now + 30.0))) Idle ""

            testCase "no session — nothing to refresh"
            <| fun () -> Expect.equal (RefreshPlan.onWake (defaultPolicy ()) true false now (Some(now + 30.0))) Idle ""

            testCase "the wake path can be switched off without disabling the timer"
            <| fun () ->
                let policy =
                    policyOf {
                        OidcRefreshPolicy.none () with
                            RefreshOnWake = Some false
                    }

                Expect.equal (RefreshPlan.onWake policy true true now (Some(now + 30.0))) Idle "no wake refresh"

                Expect.equal
                    (RefreshPlan.onArm policy true now (Some(now + 3600.0)))
                    (ArmTimer 3540.0)
                    "timer still armed"

            testCase "the master opt-out also silences the wake path"
            <| fun () ->
                let policy =
                    policyOf {
                        OidcRefreshPolicy.none () with
                            Enabled = Some false
                    }

                Expect.equal (RefreshPlan.onWake policy true true now (Some(now - 600.0))) Idle ""
        ]

        // ─── admit — single-flight coalescing ────────────────────────

        testList "admit" [
            // Before Phase 755 the timer was single-flight BY
            // CONSTRUCTION — one handle, cancelled before every
            // re-arm. The wake path breaks that: `visibilitychange`
            // and `online` can both land while the armed timer's own
            // refresh is still awaiting the token endpoint. Two
            // concurrent `refresh_token` POSTs against a ROTATING
            // issuer is worse than wasteful — the second presents a
            // token the first has consumed and is refused, which under
            // `outcomeOf` would end a perfectly good session.
            testCase "nothing in flight — every action passes through untouched"
            <| fun () ->
                Expect.equal (RefreshPlan.admit false RefreshNow) RefreshNow ""
                Expect.equal (RefreshPlan.admit false (ArmTimer 42.0)) (ArmTimer 42.0) ""
                Expect.equal (RefreshPlan.admit false Idle) Idle ""

            testCase "a refresh in flight collapses every concurrent trigger"
            <| fun () ->
                Expect.equal (RefreshPlan.admit true RefreshNow) Idle "the second trigger issues no request"
                Expect.equal (RefreshPlan.admit true (ArmTimer 42.0)) Idle ""
                Expect.equal (RefreshPlan.admit true Idle) Idle ""

            testCase "the timer, a tab wake and a reconnect racing together yield exactly one refresh"
            <| fun () ->
                let policy = defaultPolicy ()
                let expiring = Some(now + 30.0)

                // The armed timer fires first and wins.
                let fromTimer = RefreshPlan.onTimer policy true |> RefreshPlan.admit false
                Expect.equal fromTimer RefreshNow "the timer starts the one refresh"

                // Both wake events then land while it is in flight.
                let inFlight = true

                let fromVisibility =
                    RefreshPlan.onWake policy true true now expiring |> RefreshPlan.admit inFlight

                let fromOnline =
                    RefreshPlan.onWake policy true true now expiring |> RefreshPlan.admit inFlight

                Expect.equal fromVisibility Idle "visibilitychange coalesces"
                Expect.equal fromOnline Idle "online coalesces"

                let requests =
                    [ fromTimer; fromVisibility; fromOnline ]
                    |> List.filter (fun a -> a = RefreshNow)
                    |> List.length

                Expect.equal requests 1 "exactly one refresh request under concurrent triggers"
        ]

        // ─── outcomeOf — failure → transition ────────────────────────

        testList "outcomeOf" [
            testCase "success re-arms"
            <| fun () -> Expect.equal (RefreshPlan.outcomeOf (defaultPolicy ()) (Ok())) Rearm ""

            // The distinction the pre-755 code did not draw: it treated
            // EVERY Error as expiry, so one timer tick during a tunnel
            // or a dropped wifi hop ended the session — the
            // dead-session symptom this phase exists to remove,
            // arriving by the other door.
            testCase "a transport failure is a retry, not a sign-out"
            <| fun () ->
                let policy = defaultPolicy ()

                Expect.equal
                    (RefreshPlan.outcomeOf policy (Error(NetworkError "Failed to fetch")))
                    (RetryLater policy.RetrySeconds)
                    ""

            testCase "an issuer that refuses the grant ends the session"
            <| fun () ->
                let policy = defaultPolicy ()

                let refusals = [
                    TokenExchangeFailed "invalid_grant"
                    TokenExchangeFailed "refresh response missing access_token"
                    IdTokenExpired
                    IdTokenSignatureInvalid
                    MalformedIdToken
                ]

                for e in refusals do
                    Expect.equal (RefreshPlan.outcomeOf policy (Error e)) Expire (sprintf "%A ends the session" e)
        ]
    ]