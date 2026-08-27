# Migration — derivative-pipeline dead-letter queue + retry observability

**What changed.** `ToolUp.AssetStore` gains an opt-in dead-letter + retry-observability
surface over the async job-backed derivative pipeline. New public surface, all additive:

| Addition | Purpose |
|---|---|
| `DerivativeDeadLetterRecord` | persisted record of a derivation that exhausted its retry budget |
| `DerivativeFailedNotification` | terminal-failure payload, published on its own notification key |
| `DerivativeObservability` (+ `DerivativeObservability.disabled`) | the handler's posture; `disabled` is the prior behaviour |
| `DerivativeJobs.DerivativeFailedNotificationKey` | `"AssetStore.DerivativeFailed"` |
| `DerivativeJobs.RetryMetric` / `DerivativeJobs.FailedMetric` | `"assetstore.derivative.retry"` / `"assetstore.derivative.failed"` |
| `DerivativeJobs.deadLetterKey` / `DerivativeJobs.readDeadLetter` | the sweep surface |
| `DerivativeDlqOptions` (+ `DerivativeDlqOptions.defaults`) | compose-time options record |
| `AssetStoreServerAppModule.withDerivativeDlq` | the opt-in builder |
| a seventh-argument `DerivativeJobHandler` constructor | takes the posture |

**Why.** The async pipeline already recorded a terminal status once a derivation exhausted
its retry budget, so the request path answered a typed error rather than an eternal
`DerivationPending`. What it did not do was leave anything an operator could sweep: the
failure lived only in the per-`(hash, name)` status blob that the next successful derivation
clears, no counter moved, and the only notification rode the *ready* key with
`Outcome = "Failed"` — which a subscriber filtering on that key could not distinguish from
a completion without parsing the payload. A poison asset therefore failed quietly and left
no durable trace.

## Do I need to do anything?

**No.** The whole surface is behind one compose call. Without
`AssetStoreServerAppModule.withDerivativeDlq`, `AssetCompose.run` constructs the handler
through the unchanged six-argument constructor, which supplies
`DerivativeObservability.disabled`: no dead-letter blob is written, no failure notification
is published, and no `IMetricsSink` is resolved from DI (GP 11 / GP 13). An existing
deployment upgrades with no config edit and behaves identically.

**If you construct `DerivativeJobHandler` yourself** (a custom composition root, a test
double), nothing changes — the six-argument form is kept as an explicit secondary
constructor rather than folded into one widened form, so the existing call site compiles
untouched.

**If you pattern-match `DerivativeStatus`**, nothing changes. `StatusFailed` is not new; it
shipped with the async pipeline and already carried the error, attempt count, and
last-attempt timestamp this phase's dead-letter record repeats.

## Opting in

```fsharp
open ToolUp.AssetStore
open ToolUp.AssetStore.AssetCompose

AssetStoreServerApp.create ()
|> AssetStoreServerApp.withConfig config
|> AssetStoreServerApp.withAsyncDerivation AsyncDerivationOptions.defaults
|> AssetStoreServerApp.withDerivativeDlq DerivativeDlqOptions.defaults
|> AssetStoreServerApp.run
```

`DerivativeDlqOptions.defaults` turns on all three surfaces — record, notification,
counters — and inherits the retry budget already declared by `withAsyncDerivation`. The
three gate independently, so a deployment that wants the alerting but not the persisted
record can say so:

```fsharp
|> AssetStoreServerApp.withDerivativeDlq {
    DerivativeDlqOptions.defaults with
        RecordDeadLetters = false
   }
```

To widen the budget and name a destination for a companion to route on, override the
policy — the override reaches both the handler's exhaustion test and the registration the
coordinator stamps, so the two cannot disagree about how many attempts there are:

```fsharp
|> AssetStoreServerApp.withDerivativeDlq {
    DerivativeDlqOptions.defaults with
        RetryPolicy =
            Some {
                JobRetryPolicy.defaults with
                    MaxAttempts = 5
                    DeadLetterDestination = Some "assets-dlq"
            }
   }
```

`withDerivativeDlq` observes the async pipeline, so it needs
`AssetStoreServerAppModule.withAsyncDerivation` alongside it. Composed without one, it logs
a warning at startup naming the omission and is ignored — there is no background derivation
to observe.

## What the opt-in produces

**A dead-letter record** at `assets/derivative-dlq/{hash}/{name}.json`, deliberately outside
the `assets/derivative-status/` prefix: a completed derivation clears its status blob, and a
record an operator has not swept yet must survive that. Read it back with
`DerivativeJobs.readDeadLetter`; every field is by value, so a sweep tool re-drives the
derivation from the record alone (GP 12 rule 1). `Destination` carries
`JobRetryPolicy.DeadLetterDestination` verbatim — the SDK never routes to it, it passes the
operator's string through for a companion to interpret (GP 12 rule 3).

**A failure notification** under `DerivativeJobs.DerivativeFailedNotificationKey`, alongside
(not instead of) the existing ready-key publish, so an existing subscriber is unaffected.
Its `DeadLettered` flag tells a subscriber whether a sweep will find a record.

**Two counters** on the deployment's registered `IMetricsSink`, tagged
`derivative = <name>`: `DerivativeJobs.RetryMetric` once per retryable attempt that did
*not* exhaust the budget (the leading indicator, visible while derivations are still
recovering on their own) and `DerivativeJobs.FailedMetric` once per terminal failure. A
permanent classification — a decode failure, a profile that no longer declares the
derivative — dead-letters on the first attempt and increments only the failure counter.
Sink faults are logged and swallowed: recording a failure never changes a job's outcome.

## Verification

- `dotnet build ToolUp.Forge.sln`
- `dotnet run --project Build.fsproj -- VerifyAll`
- Test pack: `src/ToolUp.AssetStore.Tests/InProcess/DerivativeDlqTests.fs` (`AssetStore` in
  the `VerifyAll` pack list, wired by this phase). Every assertion about the new surface has
  a negative twin on the un-opted-in handler — same poison payload, same exhausted budget,
  nothing written, published, or counted beyond what the pipeline already did.

## Rollback

Delete the `withDerivativeDlq` line. The handler reverts to
`DerivativeObservability.disabled` on the next start. Dead-letter records already written
are inert data under `assets/derivative-dlq/` — nothing reads them unless an operator does,
and removing them is a blob delete.
