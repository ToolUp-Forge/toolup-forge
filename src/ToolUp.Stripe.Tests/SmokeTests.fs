module ToolUp.Stripe.Server.Tests.SmokeTests

open Expecto

/// Placeholder smoke suite — the Server package is a skeleton at
/// v0.1.0-alpha. End-to-end TestServer-driven tests of
/// `Routes.stripeWebhook` land at Phase 05; Customer-Portal +
/// Checkout smoke tests at Phase 06.
[<Tests>]
let tests =
    testList "Server skeleton" [
        test "package loads" {
            // Reference a known internal symbol so the assembly
            // actually loads at runtime — keeps this suite from
            // being trivially-passing dead code.
            Expect.equal ToolUp.Stripe.Server.Routes.placeholderVersion "0.1.0-alpha" "version surface"
        }
    ]