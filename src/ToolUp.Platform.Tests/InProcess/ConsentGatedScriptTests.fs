// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ConsentGatedScriptTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Consent
open Components.ConsentGatedScript

// ─── Phase 191 — the shared consent gate ─────────────────────────────
//
// Phases 159 and 163 each wrote their own fail-closed gate at their own
// call site — the ad bundle's script load and the telemetry emission.
// Phase 191 is the seam they now share, and this pack is what says the
// enforcement is the SAME enforcement rather than two things that
// currently agree.
//
// **Why the pack is .NET-side.** `ConsentGated` is deliberately
// transport-free — no React, no DOM, no script element — so the claim
// "a non-consented category makes the load a no-op" is assertable in
// process, exactly as Phase 163's telemetry gate already is
// (`TelemetrySinkTests`). The React wrapper over it is a thin
// `useState` + `useEffect` pair; the two properties of it that cannot
// be reached from .NET (it renders `Html.none` and never calls `load`
// before the grant) are pinned textually below, in the idiom
// `SidebarVisibilityContractTests` uses for the same reason.

// ─── Test providers ──────────────────────────────────────────────────

/// Answers every category with one fixed decision, and counts the
/// questions asked. The count is the point in two cases: the gate must
/// short-circuit on the first category it is refused, and the telemetry
/// path must still ask exactly the one question Phase 163 pinned.
type private CountingConsentProvider(decision: ConsentDecision) =
    let asked = ResizeArray<ConsentCategory>()

    member _.Asked = List.ofSeq asked

    interface IConsentProvider with
        member _.GetCurrentState() = async { return ConsentState.initial }
        member _.RequestConsent(_categories) = async { return ConsentState.initial }

        member _.HasConsented(category) = async {
            asked.Add category
            return decision
        }

        member _.OnStateChanged(_handler) =
            { new IDisposable with
                member _.Dispose() = ()
            }

/// A provider with an explicit `SetState` affordance, so the
/// "granted later, via `OnStateChanged`" arm can be driven without a
/// CMP host page. Mirrors `ConsentProviderTests`' own test provider.
type private MutableConsentProvider() =
    let mutable state = ConsentState.initial
    let subscribers = System.Collections.Generic.Dictionary<int, ConsentState -> unit>()
    let mutable nextId = 0

    let notify () =
        for kvp in Seq.toArray subscribers do
            try
                kvp.Value state
            with _ ->
                ()

    member _.SubscriberCount = subscribers.Count

    member _.SetState(next: ConsentState) =
        if next <> state then
            state <- next
            notify ()

    member this.Grant(category: ConsentCategory) =
        this.SetState {
            state with
                Granted = Set.add category state.Granted
                Denied = Set.remove category state.Denied
        }

    member this.Withdraw(category: ConsentCategory) =
        this.SetState {
            state with
                Granted = Set.remove category state.Granted
                Denied = Set.add category state.Denied
        }

    interface IConsentProvider with
        member _.GetCurrentState() = async { return state }
        member _.RequestConsent(_categories) = async { return state }

        member _.HasConsented(category) = async {
            if Set.contains category state.Granted then return Granted
            elif Set.contains category state.Denied then return Denied
            else return NotYetDecided
        }

        member _.OnStateChanged(handler) =
            let id = nextId
            nextId <- nextId + 1
            subscribers[id] <- handler

            { new IDisposable with
                member _.Dispose() = subscribers.Remove id |> ignore
            }

/// A stand-in for a consumer's own third-party loader — the third call
/// site the seam exists for. It records what it was asked to load, so
/// "nothing loaded" is an assertion about the loader rather than about
/// the absence of an observable effect.
type private RecordingScriptLoader() =
    let loaded = ResizeArray<string>()

    member _.Loaded = List.ofSeq loaded
    member _.EnsureLoaded(src: string) = loaded.Add src

// ─── Source pins (the React half) ────────────────────────────────────

let private clientSource (relative: string list) =
    let assemblyDir =
        IO.Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location)

    // …/src/ToolUp.Platform.Tests/bin/Debug/net10.0 → the repo root
    let repoRoot =
        IO.Path.GetFullPath(IO.Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    let path =
        IO.Path.Combine(repoRoot :: "src" :: "ToolUp.Platform.Client" :: relative |> Array.ofList)

    Expect.isTrue (IO.File.Exists path) (sprintf "expected the client source at %s" path)

    // Comments quote both the retired hand-written gate and the phrases
    // asserted below, so a raw search would match the prose explaining
    // the code instead of the code.
    (IO.File.ReadAllText path).Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
    |> String.concat "\n"

// ─── The pack ────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 191 — the shared consent gate" [

        // ── run: the one-shot effect ─────────────────────────────────

        testCaseAsync "a category that is not granted makes the effect a no-op"
        <| async {
            for decision in [ NotYetDecided; Denied ] do
                let mutable ran = false

                do!
                    ConsentGated.run (CountingConsentProvider decision :> IConsentProvider) [ Marketing ] (fun () -> async {
                        ran <- true
                    })

                Expect.isFalse ran (sprintf "a %A category must not reach the effect" decision)
        }

        testCaseAsync "every required category granted runs the effect exactly once"
        <| async {
            let mutable runs = 0

            do!
                ConsentGated.run
                    (CountingConsentProvider Granted :> IConsentProvider)
                    [ Analytics; Marketing ]
                    (fun () -> async { runs <- runs + 1 })

            Expect.equal runs 1 "a fully-consented effect runs, and runs once"
        }

        testCaseAsync "the gate short-circuits on the first refusal"
        <| async {
            let provider = CountingConsentProvider Denied
            let mutable ran = false

            do!
                ConsentGated.run (provider :> IConsentProvider) [ Analytics; Marketing; ThirdPartyEmbeds ] (fun () -> async {
                    ran <- true
                })

            Expect.isFalse ran "a denied category closes the gate"
            Expect.equal provider.Asked [ Analytics ] "the refusal ends the questioning — the rest is not asked"
        }

        // Phase 163 pinned this shape in prose ("the helper asks exactly
        // one question"). Routing `trackVia` through the seam must not
        // have turned one question into several.
        testCaseAsync "a single-category gate asks exactly one question"
        <| async {
            let provider = CountingConsentProvider Granted
            let mutable ran = false

            do! ConsentGated.run (provider :> IConsentProvider) [ Analytics ] (fun () -> async { ran <- true })

            Expect.isTrue ran "granted analytics consent runs the effect"
            Expect.equal provider.Asked [ Analytics ] "one category, one question"
        }

        testCaseAsync "a provider that throws fails closed rather than propagating"
        <| async {
            let throwing =
                { new IConsentProvider with
                    member _.GetCurrentState() = async { return ConsentState.initial }
                    member _.RequestConsent(_categories) = async { return ConsentState.initial }
                    member _.HasConsented(_category) = async { return failwith "CMP unavailable" }

                    member _.OnStateChanged(_handler) =
                        { new IDisposable with
                            member _.Dispose() = ()
                        }
                }

            let mutable ran = false

            do! ConsentGated.run throwing [ Analytics ] (fun () -> async { ran <- true })

            Expect.isFalse ran "a broken CMP must not open the gate"
        }

        testCaseAsync "an effect that throws never escapes the gate"
        <| async {
            // The gated side effect is a third-party script load; it must
            // not be able to break the surface that declared it.
            do!
                ConsentGated.run (CountingConsentProvider Granted :> IConsentProvider) [ Analytics ] (fun () -> async {
                    return failwith "the loader blew up"
                })
        }

        testCaseAsync "an empty category list permits — the ConsentState.hasAll [] semantics"
        <| async {
            let mutable ran = false

            do!
                ConsentGated.run (CountingConsentProvider Denied :> IConsentProvider) [] (fun () -> async {
                    ran <- true
                })

            Expect.isTrue ran "an effect declaring no category is not consent-categorised"
            Expect.isTrue (ConsentState.hasAll [] ConsentState.initial) "and the render-side gate says the same"
        }

        // ── the shipped defaults ─────────────────────────────────────

        // The default `ConsentProvider = NoConsentProvider` resolves to
        // `NoOpConsentProvider`, which grants `Necessary` and nothing
        // else. A deployment that has wired no CMP therefore loads no
        // gated script at all — opt-in, not default-open (GP 13).
        testCaseAsync "the default NoOpConsentProvider loads nothing"
        <| async {
            let provider = NoOpConsentProvider() :> IConsentProvider
            let loader = RecordingScriptLoader()

            for category in [ Functional; Analytics; Marketing; Personalisation; ThirdPartyEmbeds ] do
                do!
                    ConsentGated.run provider [ category ] (fun () -> async {
                        loader.EnsureLoaded(sprintf "%A.js" category)
                    })

            Expect.isEmpty loader.Loaded "no CMP wired means no gated script loads"

            let! necessary = ConsentGated.permits provider [ Necessary ]
            Expect.isTrue necessary "and the one category it does grant is still granted"
        }

        // ── watch: the mounted surface ───────────────────────────────

        test "watch reports the provider's current answer on subscription" {
            let observed = ResizeArray<bool>()

            use _subscription =
                ConsentGated.watch (NoOpConsentProvider() :> IConsentProvider) [ Analytics ] observed.Add

            Expect.equal (List.ofSeq observed) [ false ] "the initial read fires once, closed"
        }

        test "a later Granted state change activates the load via OnStateChanged" {
            let provider = MutableConsentProvider()
            let loader = RecordingScriptLoader()

            use _subscription =
                ConsentGated.watch (provider :> IConsentProvider) [ Analytics ] (fun granted ->
                    if granted then
                        loader.EnsureLoaded "analytics.js")

            Expect.isEmpty loader.Loaded "nothing loads before the grant"

            provider.Grant Analytics

            Expect.equal loader.Loaded [ "analytics.js" ] "the grant is what loads it"
        }

        test "withdrawing consent closes the gate again" {
            let provider = MutableConsentProvider()
            provider.Grant Marketing

            let observed = ResizeArray<bool>()

            use _subscription =
                ConsentGated.watch (provider :> IConsentProvider) [ Marketing ] observed.Add

            provider.Withdraw Marketing

            Expect.equal (List.ofSeq observed) [ true; false ] "a withdrawal is a transition the surface must see"
        }

        test "disposing the watch unsubscribes" {
            let provider = MutableConsentProvider()
            let observed = ResizeArray<bool>()

            let subscription =
                ConsentGated.watch (provider :> IConsentProvider) [ Analytics ] observed.Add

            subscription.Dispose()
            provider.Grant Analytics

            Expect.equal (List.ofSeq observed) [ false ] "a disposed watch sees nothing further"
            Expect.equal provider.SubscriberCount 0 "and the provider is left holding no handler"
        }

        // ── the third call site ──────────────────────────────────────

        // The seam's reason for existing: a consumer's own third-party
        // embed — registered by the consumer, unknown to the SDK — is
        // gated by exactly the same machinery as the ad bundle and the
        // telemetry beacon, with nothing hand-written at its call site.
        test "a third, consumer-registered script obeys the same gate" {
            let provider = MutableConsentProvider()
            let loader = RecordingScriptLoader()

            use _subscription =
                ConsentGated.watch (provider :> IConsentProvider) [ ThirdPartyEmbeds; Functional ] (fun granted ->
                    if granted then
                        loader.EnsureLoaded "https://example.test/embed.js")

            provider.Grant ThirdPartyEmbeds
            Expect.isEmpty loader.Loaded "a partially-granted requirement is not consent"

            provider.Grant Functional
            Expect.equal loader.Loaded [ "https://example.test/embed.js" ] "the last required grant loads it"
        }

        // ── the two call sites the phase refactored ──────────────────

        testCaseAsync "the telemetry path is this gate — suppressed without consent, one question with it"
        <| async {
            let denied = CountingConsentProvider Denied
            let sent = ResizeArray<TelemetryEvent>()

            do!
                Telemetry.trackVia (denied :> IConsentProvider) (fun ev -> async { sent.Add ev }) {
                    Event = "page_view"
                    Properties = Map.empty
                }

            Expect.isEmpty sent "Phase 163's fail-closed behaviour is unchanged by the refactor"
            Expect.equal denied.Asked [ Analytics ] "and it still asks exactly the one question"

            let granted = CountingConsentProvider Granted

            do!
                Telemetry.trackVia (granted :> IConsentProvider) (fun ev -> async { sent.Add ev }) {
                    Event = "page_view"
                    Properties = Map.empty
                }

            Expect.equal sent.Count 1 "a consented event still dispatches"
            Expect.equal granted.Asked [ Analytics ] "one category, one question"
        }

        // The ad path's gate is a React effect, so its wiring is pinned
        // textually: that it goes through the seam, and that the
        // `NoAdPanel` default returns before any of it is reached.
        test "the ad path is wired to the seam, and NoAdPanel returns before any of it" {
            let code = clientSource [ "Components"; "AdSlot.fs" ]

            Expect.stringContains
                code
                "ConsentGatedScript.ConsentGated.watch"
                "AdSlot tracks consent through the shared seam, not its own subscription"

            Expect.isFalse
                (code.Contains "provider.OnStateChanged")
                "the hand-written subscription is gone — one gate, not two"

            Expect.stringContains
                code
                "| NoAdPanel -> Html.none"
                "the NoAdPanel default renders nothing and reaches no consent or script work"

            let noAdPanelAt = code.IndexOf "| NoAdPanel -> Html.none"
            let ensureLoadedAt = code.IndexOf "AdScriptLoader.ensureLoaded"

            Expect.isGreaterThan
                ensureLoadedAt
                noAdPanelAt
                "the script load sits inside the EnabledAdPanel arm, after the default has returned"
        }

        test "the component renders nothing and loads nothing until the gate opens" {
            let code = clientSource [ "Components"; "ConsentGatedScript.fs" ]

            Expect.stringContains
                code
                "if granted then children else Html.none"
                "pre-consent the component is Html.none"

            Expect.isTrue
                (Text.RegularExpressions.Regex.IsMatch(code, @"if granted then\s+load \(\)"))
                "and `load` is reached only when granted"

            Expect.equal
                (Text.RegularExpressions.Regex.Matches(code, @"load \(\)").Count)
                1
                "there is exactly one call site for the consumer's loader, and it is that one"
        }
    ]