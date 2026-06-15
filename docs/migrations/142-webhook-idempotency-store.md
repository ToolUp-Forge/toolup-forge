# Phase 142 — Durable webhook idempotency store (`IWebhookIdempotencyStore`)

**What changes.** The webhook handler's event-id deduplication is a
formal seam — `IWebhookIdempotencyStore` — satisfying the six
portability rules (GP 12). [Phase 140](140-stripe-webhook-handler.md)
shipped an in-process `InMemoryIdempotencyStore` as the default; Phase
142 adds a **production-ready** `DurableIdempotencyStore` backed by
`IBlobStorage` so dedup state survives process restarts and spans
instances. **No action is required for the default** — a single-instance
deployment keeps the in-memory store with no config change.

**Scope.** Server-side composition only. No wire change.

## When you need it

Stripe delivers webhooks **at least once**. With a single instance, the
in-memory LRU is sufficient. With **two or more replicas** (or a process
that restarts), a redelivered event can be processed twice because the
in-memory store's dedup state is per-process and lost on restart. Compose
the durable store to close that gap.

## Diff to apply — compose the durable store

```fsharp
open ToolUp.Stripe.Server

// `blobStorage` is your already-composed IBlobStorage
// (S3 / Azure / GCS / local). The reserved `_platform` container is the
// natural home for deployment-level webhook dedup.
let idem = DurableIdempotencyStore(blobStorage) :> IWebhookIdempotencyStore

let options = WebhookOptions.create () |> WebhookOptions.withStore idem

let webApp =
    POST >=> route "/webhooks/stripe" >=> Routes.stripeWebhookWith options stripeConfig onEvent
```

A custom container: `DurableIdempotencyStore(blobStorage, "_platform")`.

## Portability audit (GP 12) — clean on the interface + both impls

| Rule | `IWebhookIdempotencyStore` |
|---|---|
| 1. Identity by value | event id is a `string` |
| 2. Async at every boundary | `TryClaim: string -> Async<bool>` |
| 3. Retry/supervision as data | bool result, no callbacks |
| 4. Stateless between calls | claim derived from id + backing store |
| 5. No cross-shard ordering | dedup is per-event-id only |
| 6. Precision at lower bound | n/a (no time semantics) |

`InMemoryIdempotencyStore` carries a **dev-only** header marker;
`DurableIdempotencyStore` a **production-ready** marker. Strict
cross-instance exactly-once requires the backing `IBlobStorage` to offer
read-after-write consistency (the shipped cloud providers do); the
residual race is bounded by Stripe's redelivery interval and assumes an
idempotent handler (defence-in-depth).

## Verification

- `dotnet run --project Build.fsproj -- VerifyAll` — the `Stripe` pack's
  `IWebhookIdempotencyStore` conformance suite runs against both
  implementations: first claim wins, replay loses, concurrent claims
  yield exactly one winner, and the durable store dedupes across a
  simulated restart.

## Rollback

Drop the `withStore` line (or use `Routes.stripeWebhook`) to revert to
the in-memory default. No persisted state needs cleanup — stale dedup
blobs are inert.
