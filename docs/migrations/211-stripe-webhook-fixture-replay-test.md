# Phase 211 — Stripe webhook-fixture replay test pack

**What changes.** `ToolUp.Stripe.Tests` gains a curated corpus of real-shaped
Stripe webhook event payloads under `src/ToolUp.Stripe.Tests/fixtures/` and a
`WebhookFixtureReplayTests` pack that replays each one through the shipped
webhook wire. **No action is required of any consumer**: this is a test-project
addition only — no public type, member or behaviour moved, and the
`ToolUp.Stripe.*` packages are byte-for-byte unchanged (GP 13).

**Scope.** `ToolUp.Stripe.Tests` only. No wire change, no API change, no
config change.

## Why a fixture corpus, when the handler already has unit tests

`StripeEventTests` and `StripeWebhookHandlerTests` build their bodies by hand,
one line of JSON each. Those bodies are written by the same hand that wrote the
decoder, so they agree with it by construction. A real provider payload does
not: it carries the full event envelope (`object`, `api_version`, `livemode`,
`pending_webhooks`, `request`), deeply nested `data.object` sub-objects, a
`previous_attributes` sibling on update events, and a long tail of fields the
typed catalogue does not model. The decoder is deliberately *lenient* about all
of that, and leniency is exactly the property a hand-built one-liner cannot
exercise — it never has anything to be lenient about.

Two of the assertions only mean something over a real shape:

- `customer.subscription.updated` carries **both** a current `status` and a
  `previous_attributes.status`. Reading the wrong one inverts the tier decision
  and looks green against a one-line body that has only one status.
- `invoice.payment_failed` carries `amount_due = 2000` beside `amount_paid = 0`.
  Reading the wrong slot bills nothing and, again, looks green.

## What the pack asserts, per fixture

Eight fixtures — the seven catalogued event types plus an uncatalogued tail
(`radar.early_fraud_warning.created`) — each run through four tests:

| Test | Asserts |
|---|---|
| replays through the shipped handler | `StripeEvent` discrimination + payload fields, the verified body is the exact bytes signed, `TierTokenSink.isTierChanging` classification, the composed sink fires only on a tier-changing event, `Ok` → 200 |
| redelivery is idempotent through the store | a second, **freshly signed** delivery of the same event id reaches the store, loses the claim, short-circuits to 200, and applies the consumer handler exactly once |
| a tampered signature is rejected before any handler effect | `WebhookSigner.verify` reports `SignatureMismatch`, the route returns 400, and neither the handler, the sink nor the idempotency store was touched |
| a handler `Error` maps to 500 and fires no sink | `Error` → 500 with no tier-token effect |

A ninth test asserts that the set of fixtures **on disk** equals the set of
declared cases, so a fixture added later cannot sit unasserted.

The signatures are synthesised at test time from the test secret, so **the
fixture JSON carries no secret** and nothing in the corpus is account-specific.

## Copying the pattern into your own deployment

The pack is a worked example of contract-testing a provider webhook end to end.
The parts worth lifting:

1. **Instrument the seams, not the status code.** The pack wraps
   `IWebhookIdempotencyStore` in a counting decorator and registers a recording
   `ITierTokenSink`, so "applied once" is asserted at the seam rather than
   inferred from two `200`s.
2. **Sign each delivery afresh.** A redelivery in production carries a new
   timestamp and therefore a different signature; re-posting identical bytes
   would let a signature-keyed dedup pass a test that an id-keyed one is
   supposed to earn.
3. **Assert the negative paths take effect nowhere.** A tampered signature must
   leave the store untouched, not merely return 400 — otherwise a forged event
   can still burn an event id.

```fsharp skip=fragment
// The composition under test: the shipped handler over instrumented seams.
let options =
    WebhookOptions.create ()
    |> WebhookOptions.withStore (countingStore :> IWebhookIdempotencyStore)
    |> WebhookOptions.withTierTokenSink (recordingSink :> ITierTokenSink)

let webApp = Routes.stripeWebhookWith options stripeConfig onEvent
```

## Adding a fixture

Drop the JSON beside the others and add its declared case; nothing else is
wired by hand, because the Expecto runner auto-discovers and the fsproj copies
the directory by wildcard:

```xml
<None Include="fixtures\**\*.json" CopyToOutputDirectory="PreserveNewest" />
```

The corpus test fails until the new file has a declared case, which is the
point of it.

## Verification

```text
dotnet build src/ToolUp.Stripe.Tests/ToolUp.Stripe.Tests.fsproj
dotnet src/ToolUp.Stripe.Tests/bin/Debug/net10.0/ToolUp.Stripe.Tests.dll
```

33 cases are added to the pack. The pack is already in the canonical
`VerifyAll` set, so no registration step and no floor bump is involved.

## Rollback

Delete `src/ToolUp.Stripe.Tests/WebhookFixtureReplayTests.fs`,
`src/ToolUp.Stripe.Tests/fixtures/` and their fsproj entries. The shared
`WebhookTestHarness.fs` (the signing / TestServer / POST helpers lifted out of
`StripeWebhookHandlerTests` so two files could share one copy) can stay or be
folded back; nothing outside the test project depends on either.
