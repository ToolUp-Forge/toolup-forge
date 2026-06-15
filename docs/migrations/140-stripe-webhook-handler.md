# Phase 140 — `Routes.stripeWebhook` production handler (consumer wiring)

**What changes.** `ToolUp.Stripe.Server` graduates from a skeleton (the
`Routes.placeholderVersion` string) to a real Giraffe webhook handler.
`Routes.stripeWebhook` reads the raw body + `Stripe-Signature` header,
verifies it through `WebhookSigner`, deduplicates on the Stripe event
id, invokes your handler with the typed `VerifiedEvent`, and maps the
outcome to HTTP status. This is **additive** — the package was a
placeholder, so there is no prior behaviour to preserve.

**Scope.** Server-side wiring only. No wire/protocol change. A
deployment that does not mount the handler pays nothing (GP 13).

## Diff to apply — mount the handler

```fsharp
open Giraffe
open ToolUp.Stripe.Webhook
open ToolUp.Stripe.Server

let stripeConfig: StripeConfig = {
    WebhookSecret = secretStore.WebhookSecret // the whsec_… value
    ApiKey = secretStore.ApiKey               // sk_…  (used by Phase 141)
}

// Your handler: route on the typed event, persist, return Ok/Error.
let onEvent (verified: VerifiedEvent) (ctx: HttpContext) : Task<Result<unit, string>> =
    task {
        match verified.Event with
        | SubscriptionUpdated p -> // … update your user's plan …
            return Ok()
        | _ -> return Ok() // ignore the long tail
    }

let webApp =
    choose [
        POST >=> route "/webhooks/stripe" >=> Routes.stripeWebhook stripeConfig onEvent
        // … your other routes …
    ]
```

**Status mapping (handled for you):**

| Outcome | Status |
|---|---|
| handler returns `Ok` (or a replayed event id) | `200` |
| malformed header / signature mismatch / body parse error | `400` |
| timestamp outside the 5-minute freshness window | `408` |
| handler returns `Error` | `500` (logged; no secret material) |

**Idempotency.** The default is an in-process `InMemoryIdempotencyStore`
— correct for a single instance. A multi-instance deployment supplies a
durable store via `WebhookOptions` (see
[`142-webhook-idempotency-store.md`](142-webhook-idempotency-store.md)):

```fsharp
let options = WebhookOptions.create () |> WebhookOptions.withStore myDurableStore
Routes.stripeWebhookWith options stripeConfig onEvent
```

## Verification

- `POST` a correctly-signed event → `200`, handler invoked once.
- Tamper the body → `400`, handler not invoked.
- Replay the same event id → `200` both times, handler invoked once.
- A handler returning `Error` → `500`.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `Stripe` pack
  (`StripeWebhookHandlerTests`, TestHost-driven) covers all four.

## Rollback

Remove the route entry. Nothing else in the deployment references the
handler, so removal is byte-for-byte reversion to the pre-mount state.
