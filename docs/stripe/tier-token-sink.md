# Composing `ToolUp.Stripe.Server` with `ToolUp.Stripe.TierToken`

The webhook handler (`ToolUp.Stripe.Server`) owns HTTP + signature
verification; the tier-claim cookie machinery (`ToolUp.Stripe.TierToken`)
owns the cookie crypto. Neither package depends on the other — they
compose in **your** code through the `ITierTokenSink` seam. On a
tier-changing billing event (a subscription created / updated / deleted,
or a completed checkout), the handler invokes your sink after your
handler succeeds and before the `200`, so you can re-stamp the user's
tier cookie.

## The sink

`ITierTokenSink.OnBillingEvent` receives the verified event + the
`HttpContext`. You resolve which user the event maps to (your domain
logic) and issue the cookie:

```fsharp
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Stripe.Webhook
open ToolUp.Stripe.Server
open ToolUp.Stripe.TierToken

type BillingTierSink(signingKey: byte[]) =
    let cookieConfig: CookieConfig = {
        CookieName = "tier"
        InsecureCookiesEnvVar = Some "MYAPP_INSECURE_COOKIES" // dev only
    }

    interface ITierTokenSink with
        member _.OnBillingEvent (event: VerifiedEvent) (ctx: HttpContext) : Task<unit> =
            task {
                // Map the Stripe event to your tier. The handler already
                // filters to tier-changing events, so this only sees them.
                let tier =
                    match event.Event with
                    | SubscriptionDeleted _ -> Tier.Free
                    | SubscriptionCreated p
                    | SubscriptionUpdated p when p.Status = "active" -> Tier.Personal
                    | _ -> Tier.Free

                // Re-issue the HMAC tier cookie on the response.
                Cookie.issue cookieConfig ctx tier 3600 signingKey |> ignore
            }
```

## Wiring it into the handler

```fsharp
open Giraffe

let sink = BillingTierSink(signingKey) :> ITierTokenSink

let options =
    WebhookOptions.create ()
    |> WebhookOptions.withTierTokenSink sink
    // optionally also: |> WebhookOptions.withStore durableStore

let webApp: HttpHandler =
    POST >=> route "/webhooks/stripe" >=> Routes.stripeWebhookWith options stripeConfig onEvent
```

## What the seam guarantees

- The sink fires **only** after your handler returns `Ok`, and **only**
  on a tier-changing event (`TierTokenSink.isTierChanging`).
- It never fires on a verification failure, a handler error, or a
  replayed event.
- `ToolUp.Stripe.Server` carries **no** reference to
  `ToolUp.Stripe.TierToken` — the two packages meet only here, in your
  composition code (GP 1). A deployment that supplies no sink behaves
  exactly as it did before (GP 11 / GP 13).
