# 1081 — `BudgetPosture`, the task registry as a DI singleton, and the compute-budget twins retired

**Class:** breaking (the twins); additive (the posture and the registry). Rides 0.42.0. The codemod rewrites the
mechanical renames and lists every other site: `dotnet run --project Build.fsproj -- Codemod <dir> --phase 1081`.

## A budget that cannot read decides by its declared posture

The AI token and spend budgets each read two things before they decide: the scope's policy from the config store and
the consumption window from the telemetry stream. When either read fails, the deployment's `BudgetPosture` decides:

```fsharp skip=signature
type BudgetPosture =
    | FailOpen   // admit the call (the default: what every deployment did before)
    | FailClosed // refuse it with a BudgetDenial naming the read
```

- **Nobody changes config to keep today's behaviour.** `AIOptions.BudgetPosture` defaults to `FailOpen`, which admits
  exactly the calls an unreadable read admitted before.
- **To refuse on a store outage:** `AIOptions.create factory profile |> AIOptions.withBudgetPosture BudgetPosture.FailClosed`.
  The refusal is a `BudgetDenial` whose `Dimension` is `unreadable-policy` or `unreadable-window`
  (`BudgetWindowUnreadable.dimension`, `BudgetWindowUnreadable.isDenial`); quota and spend are zero because neither is
  known. The provider decorators surface it as the usual `PermanentClient(429)` with its own message.
- **Every failed read is an event.** One `BudgetWindowUnreadable` record is written under
  `BudgetWindowUnreadable.SourceModule` (`_platform.ai.budget_unreadable`) per failed check, whichever posture is in
  force. It names the scope, the caller, the budget's domain, the read that failed and the posture that decided.
- **The `/dev/inspect` panels** show the posture and the last unreadable time beside the cached figures.
- **`ReadPolicy` returns a `Result`.** `AIBudgetEnforcer.ReadPolicy` and `AISpendEnforcer.ReadPolicy` answer
  `Async<Result<policy, string>>`; a config-store failure is an `Error`, never the unbounded policy.
- **The posture rides the window caches.** `AIBudgetWindowCache(ttlSeconds, posture)` and
  `AISpendWindowCache(ttlSeconds, posture)` carry it (the one-argument and no-argument constructors mean `FailOpen`),
  because the cache is the per-deployment singleton the enforcer and the panel share. The enforcers' constructors are
  unchanged, and they record through `AIBudgetEnforcer.unreadableRecorder` over the event store they are given.

## `AITaskStatusRegistry.Shared` is gone

`AICompose` creates one `AITaskStatusRegistry` per composed server, registers it as a DI singleton (the other four AI
registries already were) and hands the same instance to the assistant handler. Two servers composed in one process
keep two registries.

- **A consumer that read `AITaskStatusRegistry.Shared`** resolves it from DI:
  `services.GetService(typeof<AITaskStatusRegistry>) :?> AITaskStatusRegistry`.
- **`aiAssistantApi` takes the registry** after the SSE manager:
  `aiAssistantApi config contexts sseManager taskRegistry ctx`. A test that stands the handler up by hand passes one
  (`AITaskStatusRegistry()`), shared across the requests that must see each other's tasks.
- `AITaskStatusRegistry()` is the default bounds (10 000 tasks, terminal ones kept an hour); the two-argument
  constructor is unchanged.

## The compute budget speaks the budget seam's types

`ComputeBudgetPeriod`, `ComputeBudgetDenial`, `ComputeBudgetUsage` and their four adapters are deleted from
`ToolUp.Platform.Core`. Each was a subset of, or isomorphic to, the seam type it projected onto, so no behaviour moves:

| Removed | Use |
|---|---|
| `ComputeBudgetPeriod` (`Perpetual` / `Daily` / `Monthly`) | `BudgetPeriod` — the same cases, labels and keys, plus `Hourly` |
| `ComputeBudgetPeriod.key` / `.label` | `BudgetPeriod.key` / `.label` |
| `ComputeBudgetPeriod.toBudgetPeriod p` | `p` |
| `ComputeBudgetDenial` | `BudgetDenial` in the `compute-budget` domain (`ComputeBudget.Domain`); the class is `ClassLabel` |
| `ComputeBudgetDenial.describe` | `ComputeBudgetPolicy.describe` — the same wording |
| `ComputeBudgetDenial.toBudgetDenial d` / `ofBudgetDenial d` | `d` |
| `ComputeBudgetUsage` | `BudgetUsage` in the `compute-budget` domain |
| `ComputeBudgetUsage.empty scope period` | `BudgetUsage.empty ComputeBudget.Domain scope period` |
| `ComputeBudgetUsage.toBudgetUsage u` / `ofBudgetUsage u` | `u` |
| `ComputeBudgetJson.serialiseUsage` | `BudgetUsageJson.serialise` |

So `ComputeBudget.Period` is a `BudgetPeriod`, `ComputeBudgetPolicy.claims` / `admit` take a `BudgetUsage` and
`admit` refuses with a `BudgetDenial`, `IComputeBudgetStore.ReadUsage` / `Admit` speak `BudgetUsage` and
`BudgetDenial`, and `ModelExecutionRefusal.BudgetDenied` and `FitBatchError.BudgetDenied` carry a `BudgetDenial`.

**What the codemod cannot finish** (it reports each site):

- a record literal that built a twin needs `Domain = ComputeBudget.Domain`, and a denial's `SubmitterClass` is
  `ClassLabel`;
- a `match` over a compute budget's period now has a fourth case, `Hourly`;
- a stored budget whose window is `"hourly"` now reads as hourly (before, any unknown label read as monthly; compute
  never wrote one).

## The bytes that did not move, and the one that did

- **The audit row is frozen.** `ComputeBudgetDeniedPayload.Denial` keeps the shape archives, SIEM rules and the audit
  wire corpus hold: it is `ComputeBudgetDeniedFigures` (the denial without its domain, the class as
  `SubmitterClass`), built with `ComputeBudgetDeniedFigures.ofDenial denial`.
- **The model-execution conformance corpus is unchanged**: its `budgetDenied` document carries quota, spend and unit,
  and it round-trips byte for byte.
- **The platform's own remoting bytes for `BudgetDenied` move.** The generated codecs encode a `BudgetDenial`, which
  adds `Domain` and names the class `ClassLabel`. The client and server packages are one lockstep version, so a
  deployment upgrades both together; a federated peer on an older version reads a `BudgetDenied` refusal's quota and
  spend but not its class until it upgrades.

## Verification

`dotnet build` is clean against the release; the codemod's `--check` reports nothing left to rewrite; a deployment
that sets nothing behaves exactly as before.

## Rollback

Pin the previous release. Nothing persisted changes shape: the compute ledger rows were already the seam's, the stored
budget's period labels are the ones the seam writes, and the audit row is frozen.
