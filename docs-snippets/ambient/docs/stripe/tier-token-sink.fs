// Ambient context for `docs/stripe/tier-token-sink.md`.
//
// The wiring block reads values the page's own program supplies — the
// HMAC signing key, the deployment's `StripeConfig`, its webhook handler,
// and the `BillingTierSink` the first block introduces. Declaring the
// sink here (auto-opened, so the first block's own declaration simply
// shadows it) is what lets the second block read it the way a reader who
// scrolled past it would.
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Stripe.Webhook
open ToolUp.Stripe.Server

[<AutoOpen>]
module PageAmbient =

    /// The HMAC key the deployment mints its tier cookies with.
    let signingKey: byte[] = failwith "ambient"

    let stripeConfig: StripeConfig = failwith "ambient"

    /// The consumer's own webhook handler — the one whose `Ok` gates the sink.
    let onEvent: VerifiedEvent -> HttpContext -> Task<Result<unit, string>> =
        failwith "ambient"

    type BillingTierSink(signingKey: byte[]) =
        interface ITierTokenSink with
            member _.OnBillingEvent (_event: VerifiedEvent) (_ctx: HttpContext) : Task<unit> = failwith "ambient"