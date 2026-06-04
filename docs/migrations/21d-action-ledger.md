# Phase 21d — Workflow action durability for Forms (consumer migration)

**What changes.** `IWorkflowEngine.Apply` gains exactly-once side-effect semantics. Action exceptions no longer silently warn-log; a process restart between state-persist and action-completion no longer re-fires the action on recovery. Three new `FormError` variants surface the new failure modes (`GuardEvaluationFailed`, `ActionFailed`, `ActionPendingFromPriorAttempt`). A new substrate seam — `IActionLedger` — is required by the engine and auto-defaulted to an in-memory impl at compose time.

**Scope.** Forge SDK ships the engine rewrite, the `IActionLedger` interface + in-memory default, the `withActionLedger` / `withActionPolicy` builders on `FormsServerApp`, a new `IActionLedgerContract` portability pack, and durability tests. Consumer adoption is opt-in per deployment.

## Diff to apply (consumer side)

### 1. Audit every registered action and decide its `ActionFailurePolicy`

The engine treats actions without an explicit policy as `DeadLetter` (the safe new default — commits the submission, persists a `Failed` ledger entry, emits the action-outcome metric + `WorkflowActionExecuted` audit row).

If your action's pre-21d posture was "best effort, never roll back the transition, swallow exceptions silently", set it to `LogOnly` explicitly:

```fsharp
FormsServerApp.create ()
|> FormsServerApp.withAction "send-acknowledgement-email" sendAcknowledgementEmail
|> FormsServerApp.withActionPolicy "send-acknowledgement-email" LogOnly  // ← preserve pre-21d posture
```

If the action's downstream effect is load-bearing (payment capture, regulatory webhook, legal notice — committing the transition without the side effect leaves the system in a worse state), set it to `FailSubmission`:

```fsharp
|> FormsServerApp.withAction "capture-payment" capturePayment
|> FormsServerApp.withActionPolicy "capture-payment" FailSubmission
```

For most actions — outbound notifications, downstream API fan-out, audit hooks — the `DeadLetter` default is correct; you can omit the `withActionPolicy` call entirely.

### 2. Distributed deployments: wire a durable `IActionLedger`

In single-process / dev deployments the SDK auto-defaults to `InMemoryActionLedger` (warns on startup for non-`Anonymous` modes). In distributed deployments you MUST wire a durable ledger so the replay-safety property survives a process restart:

```fsharp
let ledger : IActionLedger = MyDurableActionLedger(entityStore) :> IActionLedger

FormsServerApp.create ()
|> FormsServerApp.withActionLedger ledger
|> ...
```

The `IActionLedgerContract` portability pack (`ToolUp.Forms.Tests/Contracts/IActionLedgerContract.fs`) validates any impl against the same conformance bar as the default. A reference distributed impl backed by `IEntityStore` is out of scope for 21d; a follow-on phase ships it if/when a pinned consumer triggers the need.

### 3. Surface the new `FormError` variants in your UI

The ToolUp.Remoting wire contract automatically picks up the new DU cases — no error-mapping middleware change is required. Client-side `match` statements over `FormError` are not exhaustive any more, however; add cases so the compiler stops flagging warnings:

```fsharp
match err with
| FormError.NotFound _              -> ...
| FormError.ValidationFailed errors -> ...
| FormError.TransitionDenied reason -> ...
| FormError.StorageFailed s         -> ...
| FormError.Unauthorised            -> ...
| FormError.InvalidTransition _     -> ...
| FormError.WorkflowNotFound _      -> ...
| FormError.GuardEvaluationFailed (guard, reason) ->
    // NEW: guard predicate threw — distinct from TransitionDenied
    // (which is a clean operator-defined denial). Suitable retry
    // path on transient faults.
    ...
| FormError.ActionFailed (actionName, reason) ->
    // NEW: action under FailSubmission policy threw; the
    // transition has been rolled back; submission state is
    // unchanged.
    ...
| FormError.ActionPendingFromPriorAttempt (submissionId, actionName) ->
    // NEW: a prior attempt persisted state but never completed the
    // action; the FailSubmission policy aborted retry to avoid
    // double-firing. Manual operator intervention or a dead-letter
    // drain is the usual remediation.
    ...
```

### 4. Wire the new metric + audit observability (optional but recommended)

The engine emits `toolup.forms.workflow.action.outcome` (Counter, tagged `actionName` + `status` ∈ `succeeded | failed | skipped_replay | skipped_pending`). It's pre-registered by `FormsCompose.run`, so it flows through both the in-process `PrometheusMetricsSink` and the `OtelMetricsSink` companion the moment the deployment scrapes `/metrics`. Dashboards filtering on `status=failed` surface dead-letter actions in real time.

The `WorkflowActionExecuted` audit row carries the same status vocabulary so cross-reference between metric + audit + ledger row is exact.

## Verification steps

1. **`dotnet build` clean** on the consumer's sln. Adding the new `FormError` cases is the most likely break point — fix any non-exhaustive matches.
2. **Round-trip a real workflow action through the new ledger**: register an action that throws, observe `WorkflowActionExecuted` with `Status = "failed"` in the audit trail, observe the `forms.workflow.action.outcome{status="failed"}` counter incrementing, observe a `Failed` ledger entry via your durable ledger admin path.
3. **`dotnet run --project Build.fsproj -- Verify`** clean in forge — the new `WorkflowEngineDurabilityTests` (6 scenarios) and the `IActionLedgerContract` pack (8 tests) must be green.
4. **Replay test (distributed deployments only)**: stop the consumer process between state-persist and action-completion (the engine writes a `Pending` ledger entry just before the action call — use a debugger breakpoint or an artificial sleep in the action body to widen the window). Restart. Confirm the action is NOT re-invoked on next `Apply` (replay path) under `Succeeded` ledger state, or that it surfaces `ActionPendingFromPriorAttempt` under `FailSubmission` policy.

## Rollback

The engine rewrite is not strictly additive — the `WorkflowEngine` constructor takes new mandatory parameters (`IActionLedger`, `IMetricsSink`, `actionPolicies`). Consumers cannot revert without pinning the pre-21d forge package version.

If a regression surfaces post-adoption, revert the consumer's adoption PR and pin `ToolUp.Forms.Server` to the pre-21d release. The SDK does NOT ship a feature flag to disable the ledger: the durability guarantee is the new posture and the type system enforces it. Bug reports against the engine are higher priority than disabling the substrate.
