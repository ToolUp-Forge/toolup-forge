module ToolUp.Stripe.Server.Tests.SmokeTests

open Expecto
open ToolUp.Stripe.Server

/// Smoke suite — references the real Server surface so the assembly
/// loads at runtime. End-to-end TestHost-driven coverage of
/// `Routes.stripeWebhook` lives in `StripeWebhookHandlerTests`.
[<Tests>]
let tests =
    testList "Server smoke" [
        test "default webhook options expose an idempotency store" {
            // Touch the real surface so this isn't trivially-passing
            // dead code: the default store must claim a fresh id once.
            let store = (WebhookOptions.create ()).Store

            let won = store.TryClaim "evt_smoke" |> Async.RunSynchronously

            Expect.isTrue won "first claim wins"
        }
    ]