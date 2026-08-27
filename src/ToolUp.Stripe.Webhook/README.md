# ToolUp.Stripe.Webhook

Pure-F# webhook signature verification + typed event envelope. Zero
ASP.NET Core / Giraffe / `Stripe.net` dependencies — server-side
wiring is in the sibling `ToolUp.Stripe.Server` package.

## Surface (v0.1.0-alpha)

```fsharp skip=signature
type VerifiedEvent =
    { Body: string
      Timestamp: int64 }

type WebhookError =
    | MalformedHeader
    | TimestampDrift of seconds:int64
    | SignatureMismatch
    | BodyParseError of string
    | UnknownEventType of string

module WebhookSigner =
    /// Verify a Stripe-Signature header against `secret` over `body`.
    /// Uses DateTimeOffset.UtcNow internally; for tests / replay,
    /// see `verifyWith`.
    val verify     : secret:string -> body:string -> header:string -> Result<VerifiedEvent, WebhookError>
    val verifyWith : now:DateTimeOffset -> secret:string -> body:string -> header:string -> Result<VerifiedEvent, WebhookError>
```

The typed `StripeEvent` DU (CustomerCreated, SubscriptionCreated, …,
Unknown of rawJson) lands at Phase 04. v0.1.0-alpha returns the
raw body in `VerifiedEvent.Body` and lets the caller route from there.

## Signature algorithm

Per [Stripe's webhook signature documentation](https://docs.stripe.com/webhooks/signatures):

1. Parse the `Stripe-Signature` header: comma-separated `key=value`
   pairs; `t=<unix>` is the timestamp; `v1=<hex>` is the HMAC-SHA256
   over `"<timestamp>.<body>"`.
2. Recompute `HMAC-SHA256(timestamp + "." + body, secret)`,
   hex-encoded lower-case.
3. Constant-time compare against the `v1=` field.
4. Reject when `abs(now - timestamp) > 300` (5-minute drift window).
