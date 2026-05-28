# Phase 1h — Combinable companion composition roots + guard/action DI

This migration ships three consumer-visible changes:

1. **`WorkflowGuard` / `WorkflowAction` signature change.** Both
   previously took `Submission * AccessContext`; both now take a
   `WorkflowContext` record carrying the submission, access context,
   AND the resolved `IServiceProvider`. Actions can resolve
   `IEntityStore`, `INotificationChannel`, etc. from
   `ctx.Services` per invocation — no more request-side workarounds.
2. **`FormsServerApp` / `AIServerApp` internal refactor onto compose
   transformers.** `FormsServerApp.run` / `AIServerApp.run` keep
   working unchanged; internally each is now `composeX >> ServerApp.run`,
   where `composeForms` / `composeAI` apply every companion
   contribution onto the inner `ServerApp` and return the composed
   result. The old positional `composeWithAI` is gone — anything
   wanting the seam directly uses `composeAI : AIServerApp -> ServerApp`.
3. **Additive companion-set extensions `withForms` / `withAI`.** Stack
   Forms and AI contributions onto one composition root without
   committing to either `FormsServerApp.run` or `AIServerApp.run` as
   the terminal call. Each takes a configurator function over a fresh
   `XServerApp` whose `Base` is the input `ServerApp`, runs the
   relevant `composeX` to merge contributions, and returns a
   `ServerApp` ready for `ServerApp.run` (or another `withX`).

Phase 1h's remaining tasks (the analogous `withRAG` additive
extension, the compose-time conflict validator, the combined-companion
sample refresh) ship in a follow-up. `withRAG` is gated on a
prior refactor that lifts `composeWithRAG` onto `composeAI` instead of
duplicating AI's DI registrations inline (see the existing comment in
`RAGCompose.fs` near the `serviceConfig` block) — landing `withRAG`
before that refactor would force the duplication into the additive
surface.

---

## What changes

### 1. `WorkflowGuard` / `WorkflowAction` shape

**Before:**

```fsharp
type WorkflowGuard = Submission * AccessContext -> Async<Result<unit, string>>
type WorkflowAction = Submission * AccessContext -> Async<unit>
```

**After:**

```fsharp
type WorkflowContext = {
    Submission: Submission
    AccessContext: AccessContext
    Services: System.IServiceProvider
}

type WorkflowGuard = WorkflowContext -> Async<Result<unit, string>>
type WorkflowAction = WorkflowContext -> Async<unit>
```

The engine resolves `Services` from the compose-time DI container and
threads it into the context per `Apply` invocation. Guards / actions
that don't need DI ignore the field; those that need
`IEntityStore` / `INotificationChannel` / `IAuditLog` / etc. resolve
them directly:

```fsharp
let stampJobEntity: WorkflowAction =
    fun ctx -> async {
        let store = ctx.Services.GetService(typeof<IEntityStore>) :?> IEntityStore
        let! _ = store.Save("scope", { Id = ctx.Submission.Id; ... })
        return ()
    }
```

### 2. `WorkflowEngine` constructor

The engine constructor now takes an `IServiceProvider` as a final
parameter; `FormsCompose.fs` already threads the DI factory's `sp`
into the constructor, so deployments using `FormsServerApp.run` pick
this up transparently. Direct constructions (typically tests) need to
pass a provider — an empty
`{ interface IServiceProvider with member _.GetService(_) = null }`
shim is sufficient when guards/actions don't resolve services.

### 3. `composeForms` / `composeAI` internal seams

`ToolUp.Forms.FormsCompose.composeForms : FormsServerApp -> ServerApp`
and `ToolUp.AI.AICompose.composeAI : AIServerApp -> ServerApp` return
the composed `ServerApp` without driving it. Both marked
`[<EditorBrowsable(Never)>]` — consumers continue using
`FormsServerApp.run` / `AIServerApp.run`.

The old positional `composeWithAI` (also `[<EditorBrowsable(Never)>]`)
is removed; nothing inside the SDK or any in-tree sample called it
externally, and the new `composeAI` is the only seam needed for the
Phase 1h additive extensions.

### 4. `withForms` / `withAI` additive extensions

Two new module-level functions sit alongside the existing
`*ServerApp.run` roots:

```fsharp
// ToolUp.Forms.FormsCompose
val withForms : (FormsServerApp -> FormsServerApp) -> ServerApp -> ServerApp

// ToolUp.AI.AICompose
val withAI :
    IAIProviderFactory ->
    IProviderProfile ->
    (AIServerApp -> AIServerApp) ->
    ServerApp ->
    ServerApp
```

The configurator receives a fresh `XServerApp` whose `Base` is the
input `ServerApp`; it should call only the companion's own helpers
(`FormsServerApp.withFormSchema` / `AIServerApp.withAIConfig` / …),
not the delegating `withConfig` / `withAuth` / … helpers (which would
overwrite the outer pipeline's existing config — set base config on
the outer pipeline before calling `withX`).

Composes left-to-right; the canonical Forms + AI pipeline becomes:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth auth
|> ServerApp.withStorage storage
|> FormsCompose.withForms (fun f ->
    f
    |> FormsServerApp.withFormSchema mySchema
    |> FormsServerApp.withWorkflow myWorkflow
    |> FormsServerApp.withAction "stampJob" stampJobAction)
|> AICompose.withAI factory providerProfile (fun ai ->
    ai
    |> AIServerApp.withAIConfig assistant
    |> AIServerApp.withModuleAIContexts contexts)
|> ServerApp.run
```

A workflow `Action` registered on the Forms half resolves
`IEntityStore` (or any other DI service) from `ctx.Services` — the
same provider seen by every other handler in the app, including the
AI agent loop. The pre-Phase-1h "actions could not reach DI" gap
disappears for combined deployments.

Calling `withAI` (or `withForms`) twice on the same pipeline composes
the companion twice: AI's metric registrations would re-append and
fail at sink construction (clear "duplicate metric name" error);
Forms's entity registrations would re-register and `compose` would
fail with a duplicate-name error. The Phase 1h conflict validator
(landing in a follow-up) gives a clearer diagnostic before either
failure mode surfaces. Until it lands, the existing duplicate-
detection paths still catch the misuse, just less helpfully.

---

## Diff to apply (out-of-tree consumers)

### Workflow guards (per-guard)

```diff
-let approveGuard: WorkflowGuard =
-    fun (submission, accessContext) -> async {
-        if accessContext.UserId = submission.Author then
-            return Ok ()
-        else
-            return Error "Only the submitter can approve."
-    }
+let approveGuard: WorkflowGuard =
+    fun ctx -> async {
+        if ctx.AccessContext.UserId = ctx.Submission.Author then
+            return Ok ()
+        else
+            return Error "Only the submitter can approve."
+    }
```

### Workflow actions (per-action)

```diff
-let notifySubmitter: WorkflowAction =
-    fun (submission, accessContext) -> async {
-        // request-side workaround — actions could not reach DI
-        do! sendNotificationOutOfBand submission accessContext
-    }
+let notifySubmitter: WorkflowAction =
+    fun ctx -> async {
+        let channel =
+            ctx.Services.GetService(typeof<INotificationChannel>) :?> INotificationChannel
+        do! channel.Publish(ctx.Submission.Author, payloadFor ctx)
+    }
```

Actions that don't need DI just ignore `ctx.Services`.

### Direct `WorkflowEngine` constructions (typically test code only)

```diff
 let engine =
     WorkflowEngine(
         formStore,
         auditLog,
         ledger,
         metricsSink,
         warn,
         workflows,
         guards,
         actions,
-        actionPolicies
+        actionPolicies,
+        services
     )
```

Tests can construct a null-returning provider:

```fsharp
let services: IServiceProvider =
    { new IServiceProvider with
        member _.GetService(_) = null }
```

Production code using `FormsServerApp.run` requires **no change** —
the DI factory threads the provider in automatically.

---

## Verification steps

1. **`dotnet build`** the consumer's solution — the guard/action
   signature is checked at compile time. Lambda call sites that still
   destructure `(submission, accessContext)` will fail to type-check
   with a clear "expected `WorkflowContext`" message.
2. **Workflow guard / action smoke test.** Apply a transition through
   `IWorkflowEngine.Apply` against a registered workflow whose guard
   reads from the new `ctx.Submission` field and whose action resolves
   a DI service. Confirm:
   - Guard return-`Ok` proceeds to action; return-`Error reason`
     surfaces `FormError.TransitionDenied`.
   - Action `ctx.Services.GetService(typeof<IEntityStore>)` returns
     the deployment's registered store (non-null in any non-test
     deployment).
3. **Existing single-superset deployments** — `FormsServerApp.run` /
   `AIServerApp.run` / `RAGServerApp.run` still compile and boot
   without change.

---

## Rollback

The signature change is mechanical; revert involves:

1. Restoring the tuple-shape `type WorkflowGuard = Submission *
   AccessContext -> _` and the matching `WorkflowAction`.
2. Removing the `services: IServiceProvider` constructor parameter
   from `WorkflowEngine`.
3. Removing the `WorkflowContext` type.
4. Reverting consumer guards / actions back to the tuple
   destructuring shape.

The `composeForms` seam is internal; its removal is purely a
collapse of `composeForms >> ServerApp.run` back into one `run` body.
