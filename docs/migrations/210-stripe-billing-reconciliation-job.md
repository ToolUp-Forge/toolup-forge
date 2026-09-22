# Phase 210 — Stripe billing reconciliation job

**What changes.** `ToolUp.Stripe.Server` gains an opt-in periodic reconcile of a
deployment's local entitlement view against the payment provider's authoritative
subscription state: `Reconciliation.fs`, carrying `IBillingReconciler`,
`IBillingDivergenceSink`, `ReconcileOutcome`, `ReconcileReaders`, the
`BillingReconciler` implementation, an audit-only default sink, and a cron
`JobRegistration` helper. **No action is required of any consumer.** Everything is
additive; nothing is registered, constructed or scheduled unless you register it,
so a deployment that ignores this phase is byte-for-byte unchanged (GP 13).

**Scope.** `ToolUp.Stripe.Server` (one new file, one doc-comment addition to
`Idempotency.fs`) and `ToolUp.Stripe.Tests`. No wire change, no behaviour change
to the webhook path, no config change.

## Why

Webhooks get missed — a minute of downtime, a dropped idempotency claim, a
signing-secret rotation that straddles a delivery. When one is missed, the local
entitlement view stops matching what the provider believes, and nothing in the
webhook path ever re-derives it: that path only hears about events it received.
The drift is silent in both directions, and both are expensive — a customer who
paid and did not get their tier, or a customer who cancelled and kept it.

The reconcile reads the truth back out of the provider on a schedule and reports
the difference.

## What it does NOT do

It does **not** apply the correction, and it does not know how to. The SDK owns
neither half of the domain data the diff needs:

- the **user↔customer map** — which of your users a provider customer id belongs
  to — is your domain logic; and
- there is **no local entitlement store** in these packages to diff against. The
  local tier is a signed browser cookie (`ToolUp.Stripe.TierToken.Cookie`) or an
  offline capability token, neither of which a background job with no request in
  hand can enumerate.

So you supply both readers and the sink that acts on a divergence. The SDK owns
the mechanism: the read, the diff rule, the idempotency key, the job shape (GP 1).

It also does not use `ITierTokenSink`. That seam takes an `HttpContext` because it
resolves *which user* an event belongs to out of the request, and a cron tick has
no request; a synthesised context would defeat the seam rather than use it. The new
`IBillingDivergenceSink` is the request-free counterpart, and the reconciler never
calls `ITierTokenSink`.

## Copy-paste: turning it on

**1. Supply the two readers and a sink.** `ReconcileReaders` is a record of two
functions; the sink is a one-method interface.

```fsharp skip=fragment
let readers: ReconcileReaders = {
    EnumerateCustomers = fun () -> myUserStore.AllStripeCustomerIds()
    ReadLocalStatus = fun customerId -> myUserStore.EntitlementOf customerId
}

let sink =
    { new IBillingDivergenceSink with
        member _.OnDivergence(outcome) = async {
            match outcome with
            | StripeAhead(customerId, _, ActiveSubscription planId) ->
                do! myUserStore.GrantTier customerId planId
            | LocalAhead(customerId, _, _)
            | StripeCancelled(customerId, _) -> do! myUserStore.RevokeTier customerId
            | InSync _
            | Unreadable _ -> ()
        }
    }
```

`ReadLocalStatus` answers in the neutral `SubscriptionStatus` vocabulary.
`None` means "no entitlement on record", and compares equal with `NoSubscription`.

Start with `AuditOnlyDivergenceSink` instead if you want to *see* your drift before
you automate correcting it — it logs each divergence at warning level and applies
nothing.

**2. Construct the reconciler** over your existing `IPaymentProvider` and webhook
idempotency store:

```fsharp skip=fragment
let reconciler =
    BillingReconciler(stripeBillingProvider, readers, sink, idempotencyStore) :> IBillingReconciler
```

**3. Register the job.** `Reconciliation.jobRegistration` builds the
`JobRegistration`; you register a handler under `Reconciliation.HandlerName` that
calls `ReconcileAll`. The handler stays on your side because `IJobHandler` lives in
`ToolUp.Platform.Server`, which this billing companion deliberately does not depend
on (GP 1) — it is ten lines:

```fsharp skip=fragment
type BillingReconcileJobHandler(reconciler: IBillingReconciler) =
    interface IJobHandler with
        member _.Execute(_ctx: JobContext) = async {
            let! _outcomes = reconciler.ReconcileAll()
            return Success
        }

// at compose:
scheduler.RegisterHandler(Reconciliation.HandlerName, BillingReconcileJobHandler reconciler)

let registration =
    Reconciliation.jobRegistration scopeId "compose" Reconciliation.DefaultCronExpression

let! _jobId = scheduler.Schedule registration
```

The registration is idempotent by `(handler, scope)`, so re-running compose reuses
the existing job rather than accumulating a second cron for the same scope.

## The five outcomes

The diff is over **entitlement**, not over the raw status value: `NoSubscription`
and `Canceled` both grant nothing, so a local view holding neither against a
provider reporting the other is in sync and produces no sink call.

| Outcome | Means | Sink fires |
|---|---|---|
| `InSync` | both sides grant the same thing | no |
| `LocalAhead` | you grant more than the provider backs — the revenue-leak direction | yes |
| `StripeAhead` | the provider grants what you have not reflected, *including a plan change at the same strength* | yes |
| `StripeCancelled` | the provider reports cancelled while you still grant something | yes |
| `Unreadable` | the provider call failed, or one of your readers threw | no |

`Unreadable` is deliberately not a divergence: there is no correction to apply to a
state nobody could read, and retrying is the scheduler's `RetryPolicy` job. One
customer's failure — a throwing reader, a throwing sink, a transport fault — is
captured against that customer and never abandons the rest of the sweep. A failing
*enumerator* does propagate, because an empty sweep would read as "no drift found".

## The idempotency key convention

A correction claims `recon:<customerId>:<status>:<yyyyMMddHH>` through the
`IWebhookIdempotencyStore` you already have, so a divergence is applied once per
customer, per provider status, per UTC hour.

Two things about that shape are load-bearing:

- **The `recon:` prefix.** `TryClaim` is keyed by the provider's event id, and a
  reconcile has none — the state was read, not delivered. The prefix keeps the
  synthesised key out of the `evt_…` id space, so a correction can never consume a
  real event's claim or be consumed by one.
- **The hour bucket.** Without it the key is either unique per tick, which claims
  nothing, or eternal, which would apply a correction once and never again however
  long the divergence persisted. Bucketing means a standing divergence is
  re-applied hourly while a tick storm inside one hour applies once.

`Reconciliation.DefaultCronExpression` is hourly to match. A finer cadence is
admissible — the read is cheap — but the extra ticks classify and report without
re-applying.

## A note for whoever generalises this

Nothing in the reconcile is Stripe-shaped: the read goes through
`IPaymentProvider` and the diff is over the neutral `SubscriptionStatus`.
`IBillingReconciler` lives in the Stripe companion only because Stripe is today's
sole `IPaymentProvider` implementation, and a substrate interface with exactly one
provider is a generalisation nobody has tested. When a second provider lands, this
belongs beside `IPaymentProvider` in the platform core; the only Stripe-specific
dependency left behind is `IWebhookIdempotencyStore`.

## Verification

```powershell
dotnet build src/ToolUp.Stripe.Server/ToolUp.Stripe.Server.fsproj
dotnet src/ToolUp.Stripe.Tests/bin/Debug/net10.0/ToolUp.Stripe.Tests.dll --filter "Billing reconciliation (Phase 210)"
```

The pack stubs the provider at the `HttpMessageHandler` seam rather than faking
`IPaymentProvider`, so it exercises the shipped `/v1/subscriptions` parse as well
as the diff — a mis-parsed response would otherwise have passed as in sync.

## Rollback

Delete the registration. Nothing else in either package changed behaviour, so
removing the `Schedule` call restores the prior state exactly; the types can stay
compiled in at no cost.
