module ToolUp.Platform.Tests.InProcess.ConsentProviderTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Consent
open ToolUp.Platform.Tests.Contracts

// ─── Phase 59 — IConsentProvider in-process contract binding ─────────
//
// Two test lists exported:
//   * `tests` — drives the portable `IConsentProviderContract` pack
//     against `NoOpConsentProvider` (the SDK's default-on impl).
//   * `subscriptionFiringTests` — covers the OnStateChanged firing
//     contract that's unreachable through the portable interface
//     alone, using a small test-only mutable provider that exposes
//     a setState affordance. Bound in the same module so the
//     mutable-impl plumbing stays test-side and out of the SDK
//     surface.
//
// FundingChoicesConsentProvider's CMP-event handling is Fable-only
// (`JS.setTimeout`, `window.__tcfapi`, `addEventListener`) and not
// reached from the .NET runner. Browser-side parity is exercised
// by the Fable consumer's smoke tests.

// ─── Test-only mutable provider ──────────────────────────────────────
//
// Implements `IConsentProvider` with an explicit `setState`
// affordance so the firing-side assertions can drive a state change
// without depending on a CMP host page. Mirrors the subscription
// machinery of `NoOpConsentProvider` (dictionary-keyed handlers,
// try/with around notify) so any subscription-firing defect would
// reproduce against either provider.

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

    member _.SetState(next: ConsentState) =
        if next <> state then
            state <- next
            notify ()

    interface IConsentProvider with
        member _.GetCurrentState() = async { return state }

        member _.RequestConsent(_categories: ConsentCategory list) = async { return state }

        member _.HasConsented(category: ConsentCategory) = async {
            if Set.contains category state.Granted then return Granted
            elif Set.contains category state.Denied then return Denied
            else return NotYetDecided
        }

        member _.OnStateChanged(handler: ConsentState -> unit) =
            let id = nextId
            nextId <- nextId + 1
            subscribers[id] <- handler

            { new IDisposable with
                member _.Dispose() = subscribers.Remove(id) |> ignore
            }

// ─── Portable contract binding ───────────────────────────────────────

let tests =
    let factory () =
        NoOpConsentProvider() :> IConsentProvider

    IConsentProviderContract.tests "NoOpConsentProvider" factory

// ─── OnStateChanged firing contract ──────────────────────────────────

let private granting categories : ConsentState = {
    Granted = Set.ofList (Necessary :: categories)
    Denied = Set.empty
    LastUpdatedAt = DateTimeOffset.UtcNow
    ConsentVersion = 1
}

let subscriptionFiringTests =
    testList "MutableConsentProvider — OnStateChanged firing" [
        testCaseAsync "subscribed handler fires when state changes"
        <| async {
            let provider = MutableConsentProvider()
            let mutable fired = 0

            use _sub =
                (provider :> IConsentProvider).OnStateChanged(fun _ -> fired <- fired + 1)

            provider.SetState(granting [ Analytics ])
            Expect.equal fired 1 "handler fires exactly once per state change"
        }

        testCaseAsync "SetState to an equal state does not fire handlers"
        <| async {
            let provider = MutableConsentProvider()
            let mutable fired = 0

            use _sub =
                (provider :> IConsentProvider).OnStateChanged(fun _ -> fired <- fired + 1)
            // Initial state is already `ConsentState.initial`; setting
            // it back to itself must not provoke a notification.
            provider.SetState ConsentState.initial
            Expect.equal fired 0 "no-op SetState must not fire handlers"
        }

        testCaseAsync "handlers receive the new state, not a stale snapshot"
        <| async {
            let provider = MutableConsentProvider()
            let mutable observed: ConsentState option = None

            use _sub =
                (provider :> IConsentProvider).OnStateChanged(fun s -> observed <- Some s)

            let next = granting [ Marketing; Personalisation ]
            provider.SetState next

            match observed with
            | Some s -> Expect.equal s next "handler receives the post-mutation state"
            | None -> failtest "handler did not fire"
        }

        testCaseAsync "disposed subscription stops receiving notifications"
        <| async {
            let provider = MutableConsentProvider()
            let mutable fired = 0
            let sub = (provider :> IConsentProvider).OnStateChanged(fun _ -> fired <- fired + 1)
            provider.SetState(granting [ Analytics ])
            Expect.equal fired 1 "fired once before dispose"
            sub.Dispose()
            provider.SetState(granting [ Analytics; Marketing ])
            Expect.equal fired 1 "no additional firing after dispose"
        }

        testCaseAsync "multiple subscribers each fire independently"
        <| async {
            let provider = MutableConsentProvider()
            let mutable fired1 = 0
            let mutable fired2 = 0

            use _sub1 =
                (provider :> IConsentProvider).OnStateChanged(fun _ -> fired1 <- fired1 + 1)

            use _sub2 =
                (provider :> IConsentProvider).OnStateChanged(fun _ -> fired2 <- fired2 + 1)

            provider.SetState(granting [ Functional ])
            Expect.equal fired1 1 "first subscriber fired"
            Expect.equal fired2 1 "second subscriber fired"
        }

        testCaseAsync "subscriber exception does not break sibling subscribers"
        <| async {
            let provider = MutableConsentProvider()
            let mutable fired = 0

            use _bad =
                (provider :> IConsentProvider).OnStateChanged(fun _ -> raise (InvalidOperationException "synthetic"))

            use _good =
                (provider :> IConsentProvider).OnStateChanged(fun _ -> fired <- fired + 1)

            provider.SetState(granting [ ThirdPartyEmbeds ])

            Expect.equal fired 1 "well-behaved subscriber must still fire when a sibling throws"
        }
    ]