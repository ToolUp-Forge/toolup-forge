module ToolUp.Platform.Tests.Contracts.IConsentProviderContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Consent

// ─── IConsentProvider contract pack ──────────────────────────────────
//
// Parametrised tests for any `IConsentProvider` implementation. Each
// test asks the factory for a fresh provider so concurrent runs
// against shared substrate state cannot interfere.
//
// Coverage targets the **portable** surface — assertions every
// implementation must satisfy, reachable through the interface alone:
//   * `GetCurrentState` idempotency across two adjacent reads.
//   * `Necessary` always-granted invariant — the documented default.
//   * `HasConsented` agreement with `GetCurrentState`'s sets across
//     every `ConsentCategory` case.
//   * `RequestConsent` completion + post-condition consistency with
//     `GetCurrentState`.
//   * `OnStateChanged` returns a non-null `IDisposable`; dispose is
//     idempotent; multiple subscribers coexist without interference.
//
// **Out of scope here** (live in per-impl tests, not the portable
// contract):
//   * State-change subscription *firing* — the portable interface
//     has no `setState` affordance, so provoking a change requires
//     impl-side test scaffolding. The InProcess binding exercises
//     this via a small mutable test provider on top of the same pack.
//   * `FundingChoicesConsentProvider` CMP-event handling — lives
//     behind `JS.setTimeout` + `window.__tcfapi`, exercised only by
//     the Fable consumer (out of scope for the .NET runner).
//   * localStorage persistence across page reloads — browser concern;
//     the future `ConsentStateStore` companion will validate it.

let private allCategories = [
    Necessary
    Functional
    Analytics
    Marketing
    Personalisation
    ThirdPartyEmbeds
]

let tests (name: string) (factory: unit -> IConsentProvider) =

    testList $"{name} — IConsentProvider contract" [

        // ─── GetCurrentState shape ────────────────────────────────

        testCaseAsync "GetCurrentState includes Necessary in Granted"
        <| async {
            let provider = factory ()
            let! state = provider.GetCurrentState()

            Expect.isTrue
                (Set.contains Necessary state.Granted)
                "every provider must grant Necessary by default — it is the strictly-required category"
        }

        testCaseAsync "GetCurrentState — Granted and Denied are disjoint"
        <| async {
            let provider = factory ()
            let! state = provider.GetCurrentState()

            let overlap = Set.intersect state.Granted state.Denied

            Expect.isTrue (Set.isEmpty overlap) "no category may appear in both Granted and Denied"
        }

        testCaseAsync "GetCurrentState is idempotent across adjacent calls"
        <| async {
            let provider = factory ()
            let! first = provider.GetCurrentState()
            let! second = provider.GetCurrentState()

            Expect.equal first second "GetCurrentState must return equal snapshots when no state change occurs"
        }

        // ─── HasConsented agreement ───────────────────────────────

        testCaseAsync "HasConsented(Necessary) is Granted"
        <| async {
            let provider = factory ()
            let! decision = provider.HasConsented Necessary
            Expect.equal decision Granted "Necessary is granted by every provider"
        }

        testCaseAsync "HasConsented agrees with GetCurrentState for every category"
        <| async {
            let provider = factory ()
            let! state = provider.GetCurrentState()

            for category in allCategories do
                let! decision = provider.HasConsented category

                let expected =
                    if Set.contains category state.Granted then Granted
                    elif Set.contains category state.Denied then Denied
                    else NotYetDecided

                Expect.equal
                    decision
                    expected
                    (sprintf "HasConsented(%A) must match GetCurrentState's classification" category)
        }

        // ─── RequestConsent post-condition ────────────────────────

        testCaseAsync "RequestConsent completes and matches subsequent GetCurrentState"
        <| async {
            let provider = factory ()
            let! requested = provider.RequestConsent [ Analytics; Marketing ]
            let! current = provider.GetCurrentState()
            // Provider-specific behaviour varies: NoOp returns
            // unchanged state, FundingChoices surfaces whatever the
            // user lands on after the CMP banner. Either way, the
            // value returned from RequestConsent must reflect the
            // same view as a subsequent GetCurrentState.
            Expect.equal requested current "RequestConsent return value must agree with subsequent GetCurrentState"
        }

        // ─── OnStateChanged subscription handle ───────────────────

        testCaseAsync "OnStateChanged returns a non-null IDisposable"
        <| async {
            let provider = factory ()
            let subscription = provider.OnStateChanged(fun _ -> ())

            Expect.isNotNull (box subscription) "subscription handle must not be null"

            subscription.Dispose()
        }

        testCaseAsync "OnStateChanged subscription dispose is idempotent"
        <| async {
            let provider = factory ()
            let subscription = provider.OnStateChanged(fun _ -> ())
            subscription.Dispose()
            // Second dispose must not throw — handles may outlive
            // their provider in Elmish component teardown sequences.
            subscription.Dispose()
        }

        testCaseAsync "Multiple subscriptions coexist without interference"
        <| async {
            let provider = factory ()
            let mutable fired1 = 0
            let mutable fired2 = 0
            let sub1 = provider.OnStateChanged(fun _ -> fired1 <- fired1 + 1)
            let sub2 = provider.OnStateChanged(fun _ -> fired2 <- fired2 + 1)

            // Without driving a state change through impl-specific
            // scaffolding (out of scope here), neither handler fires.
            // The assertion is that two coexisting subscriptions
            // construct + dispose cleanly without throwing or
            // interacting pathologically.
            sub1.Dispose()
            sub2.Dispose()

            Expect.equal fired1 0 "no state change → first handler did not fire"
            Expect.equal fired2 0 "no state change → second handler did not fire"
        }
    ]