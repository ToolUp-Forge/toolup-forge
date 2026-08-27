# ToolUp.Stripe.Server

Giraffe / ASP.NET Core wiring for `ToolUp.Stripe.Webhook`. Skeleton at
v0.1.0-alpha — Phase 05 ships the `Routes.stripeWebhook`
`HttpHandler`; Phase 06 ships the `CustomerPortal.openSessionLink`
and `Checkout.createSession` wrappers.

## Planned surface

```fsharp skip=signature
type StripeConfig =
    { WebhookSecret: string
      ApiKey: string }

module Routes =
    val stripeWebhook
        : config:StripeConfig
        -> handler:(VerifiedEvent -> HttpContext -> Task<Result<unit, string>>)
        -> HttpHandler

module CustomerPortal =
    val openSessionLink
        : customerId:string
        -> returnUrl:string
        -> apiKey:string
        -> Task<Result<Uri, string>>

module Checkout =
    val createSession : ...
```
