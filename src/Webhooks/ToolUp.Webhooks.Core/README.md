# ToolUp.Webhooks.Core

Vendor-neutral **inbound-webhook** verification substrate — the generalisation of
`ToolUp.Stripe.Webhook`'s signature-verify pattern into a scheme-driven verifier any
integration can configure (Stripe, GitHub, Shopify, generic SaaS).

Pure F# + BCL crypto only — no ASP.NET Core, no vendor SDK (GP 1).

## What's here

- **`WebhookScheme`** — the verification scheme expressed as data: signature header,
  encoding (hex/base64), signed-payload shape (`BodyOnly` / `TimestampDotBody`),
  timestamp source (none / separate header / Stripe-style embedded), freshness window,
  and an optional dedup-id header. Presets: `WebhookScheme.stripeStyle`,
  `WebhookScheme.gitHubStyle`.
- **`WebhookVerifier.verify`** — recomputes HMAC-SHA256, constant-time compares, enforces
  the freshness window, returns `Result<VerifiedWebhook, WebhookError>` (fail-closed).
- **`IInboundWebhookHandler`** — `Kind` + `Handle : VerifiedWebhook -> Async<WebhookAck>`.
  Stateless between invocations (the six portability rules).
- **`IWebhookDedupStore`** — `TryClaim(kind, eventId)`; `true` first time, `false` on
  redelivery.

The ASP.NET Core / Giraffe route + registry + in-memory dedup store live in
`ToolUp.Webhooks.Server`.

## Example

```fsharp
open ToolUp.Webhooks

let result =
    WebhookVerifier.verify WebhookScheme.gitHubStyle "push" secret body (fun h ->
        match req.Headers.TryGetValue h with
        | true, v -> Some(string v)
        | _ -> None)
```
