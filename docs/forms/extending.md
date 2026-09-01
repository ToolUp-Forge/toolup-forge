# Extending ToolUp.Forms

How to write custom validators, guards, actions, replace the action ledger, add submission analysers, and write a custom renderer.

## Custom validators

For per-field domain validation that the built-in `ValidationRule`s don't cover. The shipped contract is intentionally small — a custom validator receives the **string form** of a single field's submitted value and returns `Ok ()` to pass or `Error message` to fail.

```fsharp
open ToolUp.Forms.FormValidator

let blockListValidator : CustomValidator =
    fun raw ->
        let blockedDomains = [ "example-bad.com"; "trash-mail.test" ]
        let lower = raw.ToLowerInvariant ()

        if blockedDomains |> List.exists (fun d -> lower.EndsWith ("@" + d)) then
            Error "this email domain is not accepted"
        else
            Ok ()
```

Register on `FormsServerApp`:

```fsharp
open ToolUp.Forms.FormsCompose

let app =
    FormsServerApp.create ()
    // ...
    |> FormsServerApp.withCustomValidator "blocklist-check" blockListValidator
```

Reference from a `FormSchema`:

```fsharp
open ToolUp.Forms.FormSchema

let userOnboardingSchema =
    FormSchema.create "user-onboarding" "User onboarding" [
        { Key = "email"
          DisplayName = "Email"
          Description = None
          Kind = TextField (Some 254)
          Required = true
          Validators = [
              Regex (@"^[^@\s]+@[^@\s]+\.[^@\s]+$", Some "valid email address")
              Custom "blocklist-check"
          ] }
        // ...
    ]
```

The validator name (`"blocklist-check"`) matches between the schema's `Custom name` and the registry registration. Schemas don't reference closures — only names — so the schema crosses the wire as data and the lookup happens server-side.

Missing-by-name custom validators are a soft pass (no error). Renaming a registered validator doesn't break in-flight forms, but it does silently disable the check — keep names stable.

### Cross-field / async / per-submission validation

The per-field `CustomValidator` shape is `string -> Result<unit, string>` — synchronous and field-local. For cross-field rules ("end date must be after start date"), async lookups ("is this email already registered?"), or per-submission constraints that depend on multiple values, layer the check at the route-handler tier in your own module rather than fighting the registry shape. The shipped engine has no built-in cross-field validator surface.

### Multiple custom validators on one field

```fsharp
let emailField: FieldSchema = {
    Key = "email"
    DisplayName = "Email"
    Description = None
    Kind = TextField None
    Required = true
    Validators = [
        Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", Some "valid email address")
        Custom "blocklist-check"
        Custom "domain-allowlist"
    ]
}
```

Run in order. The validator engine accumulates every failure across every rule and every field — no short-circuit, so the UI shows all problems at once.

## Workflow guards

Predicates that veto a transition. Receive a `WorkflowContext`; return `Async<Result<unit, string>>`. `Ok ()` allows the transition; `Error reason` surfaces as `FormError.TransitionDenied reason`.

The SDK's `WorkflowContext` bundles the submission (`Submission`), the caller's `AccessContext`, and a freshly-resolved `IServiceProvider` (`Services`) — so a guard reaches DI per invocation instead of capturing services at compose time:

```fsharp
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.IWorkflowEngine

let hasProposalAttached: WorkflowGuard =
    fun ctx -> async {
        if ctx.Submission.Values.ContainsKey "proposal_file_id" then
            return Ok()
        else
            return Error "no proposal file attached"
    }

let creditCheckPassed: WorkflowGuard =
    fun ctx -> async {
        // Resolve per invocation from ctx.Services rather than capturing.
        let creditApi = ctx.Services.GetService(typeof<ICreditCheckApi>) :?> ICreditCheckApi

        match Map.tryFind "company_name" ctx.Submission.Values with
        | Some(TextValue companyName) ->
            let! score = creditApi.GetCreditScore companyName

            if score >= 600.0 then
                return Ok()
            else
                return Error(sprintf "credit score %g below threshold" score)
        | _ -> return Error "missing company_name field"
    }
```

Register on `FormsServerApp`:

```fsharp
let app =
    FormsServerApp.create ()
    // ...
    |> FormsServerApp.withGuard "has-proposal-attached" hasProposalAttached
    |> FormsServerApp.withGuard "credit-check-passed" creditCheckPassed
```

Reference from a `WorkflowDefinition`:

```fsharp
let approveTransition: ToolUp.Forms.Workflow.Transition = {
    From = "quoted"
    Event = "approve"
    To = "approved"
    Guard = Some "credit-check-passed"
    Action = None
}
```

A failing guard returns `Error (FormError.TransitionDenied reason)` to the caller. A guard that **throws** surfaces as `FormError.GuardEvaluationFailed (guardName, exceptionMessage)` so callers can choose to retry on transient faults vs surface a hard denial to the user.

### Guard chaining

A transition has at most one `Guard`. For multiple checks, compose them into one named guard:

```fsharp
let approvalGuard (proposalGuard: WorkflowGuard) (creditGuard: WorkflowGuard) : WorkflowGuard =
    fun ctx -> async {
        match! proposalGuard ctx with
        | Error r -> return Error r
        | Ok() -> return! creditGuard ctx
    }
```

## Workflow actions

Side-effects fired after a successful transition. Receive a `WorkflowContext`; return `Async<unit>`. The engine wraps every invocation in the `IActionLedger` lifecycle (exactly-once invocation across replays) and applies the per-action `ActionFailurePolicy` registered via `withActionPolicy` to decide what happens on exception. Without an explicit policy the engine defaults to `DeadLetter` — see [concepts.md](concepts.md) "Action ledger and failure policy" for the full table.

```fsharp
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.IWorkflowEngine

// Resolve collaborators from ctx.Services per invocation — the context is
// built fresh for each Apply call, so a transient service is never captured
// across calls.
let sendWelcomeEmail: WorkflowAction =
    fun ctx -> async {
        let notify = ctx.Services.GetService(typeof<IMyEmailService>) :?> IMyEmailService

        let email =
            match Map.tryFind "email" ctx.Submission.Values with
            | Some(TextValue s) -> s
            | _ -> ""

        let name =
            match Map.tryFind "name" ctx.Submission.Values with
            | Some(TextValue s) -> s
            | _ -> "there"

        do! notify.Send email (sprintf "Welcome, %s!" name) (welcomeEmailBody ctx.Submission)
    }

let kickoffOnboardingJob: WorkflowAction =
    fun ctx -> async {
        let jobs = ctx.Services.GetService(typeof<IJobScheduler>) :?> IJobScheduler
        let scopeId = ctx.AccessContext.TeamId |> Option.defaultValue ctx.AccessContext.UserId

        // A `JobId` is a Guid the scheduler MINTED at registration — not a
        // natural key you can compose from a submission id. Find the
        // registered job by its handler name instead.
        let! scoped = jobs.ListJobs scopeId

        match scoped |> List.tryFind (fun job -> job.Handler = "onboarding") with
        | None -> return failwith "no onboarding job is registered in this scope"
        | Some job ->
            // TriggerOnce answers with a Result rather than throwing.
            match! jobs.TriggerOnce(scopeId, job.JobId, ctx.AccessContext.UserId) with
            | Ok() -> return ()
            // Throwing is how an action defers to its registered
            // `ActionFailurePolicy` — see below.
            | Error reason -> return failwith reason
    }
```

`IMyEmailService` is not an SDK interface — email delivery rides `INotificationChannel` / `INotificationSink`, or your own service registered in DI.

Register:

```fsharp
open ToolUp.Forms.Workflow
open ToolUp.Forms.FormsCompose

let app =
    FormsServerApp.create ()
    // ...
    // The actions are registered as-is: each resolves its own collaborators
    // from ctx.Services, so there is nothing to partially apply here.
    |> FormsServerApp.withAction "send-welcome-email" sendWelcomeEmail
    |> FormsServerApp.withAction "kickoff-onboarding" kickoffOnboardingJob
    // Best-effort courtesy mail: a lost send is not worth a dead-letter row.
    |> FormsServerApp.withActionPolicy "send-welcome-email" LogOnly
    // Load-bearing downstream work: abort the transition if it throws.
    |> FormsServerApp.withActionPolicy "kickoff-onboarding" FailSubmission
```

Reference from a `WorkflowDefinition`:

```fsharp
{ From = "submitted"
  Event = "approve"
  To = "approved"
  Guard = None
  Action = Some "send-welcome-email" }
```

### Multiple actions per transition

A transition has at most one `Action`. For multiple effects, compose into a single named action and register it once:

```fsharp
let onboardingActions
    (welcomeEmail: WorkflowAction)
    (kickoffJob: WorkflowAction)
    (createCustomer: WorkflowAction)
    : WorkflowAction
    =
    fun args -> async {
        do! welcomeEmail args
        do! kickoffJob args
        do! createCustomer args
    }
```

Or use jobs / events as the multi-effect substrate — fire one action that publishes an event; multiple subscribers handle the event independently.

## `ActionFailurePolicy` and the action ledger

### Why this matters

Pre-ledger, a workflow action that threw was warn-logged and swallowed. The submission committed; the side effect (email send, webhook fan-out, downstream API) was lost with no metric, no audit, no dead-letter row. Operators only heard about the failure when the customer followed up. Worse, a process restart between state-persist and action-completion re-fired the action on the next `ApplyTransition` — double emails, double payments, double webhook events.

The action ledger introduces two coordinated primitives:

1. **`IActionLedger`** — exactly-once invocation per `(SubmissionId, transitionId, actionName)`. Survives process restarts when wired to a durable backend.
2. **`ActionFailurePolicy`** — per-action policy controlling what happens when the action throws.

### `ActionFailurePolicy` cases

```fsharp
type ActionFailurePolicy =
    /// Abort the transition. State NOT committed.
    | FailSubmission
    /// Default. Commit submission. Persist a `Failed` ledger entry
    /// the operator drains via a retry orchestrator.
    | DeadLetter
    /// Pre-21d behaviour. Warn-log + commit. No dead-letter row.
    | LogOnly
```

Choose by the consequence of losing the side effect:

| Policy | Choose when |
|---|---|
| `DeadLetter` (default) | Most actions — outbound notifications, downstream API fan-out, audit hooks. A retry orchestrator drains the dead-letter ledger asynchronously; the user-facing transition still succeeds. |
| `FailSubmission` | Load-bearing side effects: payment capture, regulatory webhook, legal notice. Committing the transition without the side effect leaves the system in a worse state than not transitioning at all. |
| `LogOnly` | Genuinely best-effort sinks where a retry orchestrator adds no value — fire-and-forget observability beacons. Also the explicit opt-in for pre-21d behaviour. |

Last write wins — calling `withActionPolicy` twice with the same action name keeps the most recent policy.

### Observability surfaces

Regardless of policy, every action invocation emits on three surfaces:

1. **Metric counter** — `forms.workflow.action.outcome{status="succeeded" | "failed" | "skipped_replay" | "skipped_pending"}` on every registered `IMetricsSink`.
2. **Audit row** — `WorkflowActionExecuted{status=...}` to `_platform.audit`. Carries `actionName`, `submissionId`, `transitionId`, actor, timestamp, plus the exception message on failure.
3. **Ledger entry** — `ActionLedgerEntry{Status = Failed reason | Succeeded | Pending}` in the registered `IActionLedger`.

Silent loss is structurally impossible: a failure missing from all three would require all three substrates to fail concurrently. Dead-letter retry orchestrators consume the ledger; operator dashboards consume the metric counter; compliance trails consume the audit log.

### `IActionLedger` contract

```fsharp
type ActionLedgerStatus =
    | Pending
    | Succeeded
    | Failed of reason: string

type ActionLedgerEntry = {
    SubmissionId: SubmissionId
    /// Engine-derived "{from}:{event}:{to}" triple.
    TransitionId: string
    ActionName: string
    Status: ActionLedgerStatus
}

type LedgerError =
    | StorageFailed of string
    | EntryMissing

type IActionLedger =
    abstract Record :
        ActionLedgerEntry -> Async<Result<unit, LedgerError>>

    abstract Lookup :
        SubmissionId * transitionId: string * actionName: string ->
            Async<Result<ActionLedgerEntry option, LedgerError>>

    abstract MarkSucceeded :
        SubmissionId * transitionId: string * actionName: string ->
            Async<Result<unit, LedgerError>>

    abstract MarkFailed :
        SubmissionId * transitionId: string * actionName: string *
        reason: string ->
            Async<Result<unit, LedgerError>>
```

**Identity by value.** Entries are keyed by `(SubmissionId, transitionId, actionName)`. The engine derives `transitionId` per call as `"{From}:{Event}:{To}"` so two transitions firing the same action on the same submission (unusual but legal) get independent ledger rows.

**Idempotent `Record`.** Inserting an entry whose key already exists is a no-op — the existing row is preserved and the engine branches on its `Status` rather than overwriting. This protects against double-`Record` on retry.

**Six-rule portability.** The interface satisfies all six rules: identity by string, async at every boundary, retry/failure expressed as the `LedgerError` data record, stateless between calls (grain deactivation is safe), no cross-key ordering promised, no sub-second timing claims. Distributed-ready by construction.

### Default — `InMemoryActionLedger`

`FormsServerApp.run` auto-defaults to `InMemoryActionLedger` if `withActionLedger` is not called. This is correct for dev and single-process deployments — survives in-process retries within the same process, but **does not survive a process restart**. The compose step prints an out-of-band warning to `stderr` in non-`Anonymous` modes so deployments running workflow-bearing forms in production see a clear signal they need a durable ledger.

### Wiring a durable `IActionLedger`

Skeleton for a SQL-backed implementation:

```fsharp
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.IActionLedger

type PostgresActionLedger(connectionString: string) =
    interface IActionLedger with
        member _.Record entry = async {
            // INSERT ... ON CONFLICT DO NOTHING for idempotency.
            // Return Ok () on insert-or-retain;
            // Error (StorageFailed msg) on backing-store failure.
            return Ok ()
        }

        member _.Lookup (submissionId, transitionId, actionName) = async {
            // SELECT ... WHERE submission_id = $1 AND transition_id = $2 AND action_name = $3
            // Return Ok None if no row; Ok (Some entry) otherwise.
            return Ok None
        }

        member _.MarkSucceeded (submissionId, transitionId, actionName) = async {
            // UPDATE ... SET status = 'succeeded' WHERE ...
            // Return Error EntryMissing if 0 rows affected.
            return Ok ()
        }

        member _.MarkFailed (submissionId, transitionId, actionName, reason) = async {
            // UPDATE ... SET status = 'failed', reason = $4 WHERE ...
            // Return Error EntryMissing if 0 rows affected.
            return Ok ()
        }
```

Register on `FormsServerApp`:

```fsharp
open ToolUp.Forms.FormsCompose

let app =
    FormsServerApp.create ()
    // ...
    |> FormsServerApp.withActionLedger (PostgresActionLedger connStr :> IActionLedger)
```

### Conformance — `IActionLedgerContract`

Bind your implementation to the eight-test `IActionLedgerContract` pack in `ToolUp.Forms.Tests/Contracts/IActionLedgerContract.fs`. The pack covers:

- `Record` is idempotent (re-`Record`ing returns the existing row).
- `Lookup` returns `None` for unknown keys and `Some` for `Record`ed keys.
- `MarkSucceeded` / `MarkFailed` update status correctly.
- `MarkSucceeded` / `MarkFailed` against an unknown key return `EntryMissing`.
- Distinct `(submissionId, transitionId, actionName)` keys are independent.
- Concurrent `Record` calls converge to one row per key.

Passing the pack is the conformance bar — the engine relies on these lifecycle invariants, so any drop-in must satisfy them.

## Replacing the renderer

`FormRenderer` is generic; sometimes you need custom UX (multi-step wizards, mobile-specific layouts, branded look-and-feel). Write your own renderer against the same `FormSchema`:

```fsharp
open Feliz
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission

// `let rec … and …`: the renderer and its per-field helper are mutually
// visible, so the field cases can be written after the form that uses them.
let rec render
    (props:
        {| Schema : FormSchema
           InitialValues : Map<string, FieldValue>
           OnSubmit : Map<string, FieldValue> -> unit |})
    =
    let values, setValues = React.useState props.InitialValues

    Html.form [
        prop.onSubmit (fun e ->
            e.preventDefault()
            props.OnSubmit values)
        prop.children [
            for field in props.Schema.Fields do
                renderField field values setValues

            Html.button [
                prop.type'.submit
                prop.text "Submit"
            ]
        ]
    ]

// The return annotation is load-bearing: without it the `for … do` above is
// read as a statement, `renderField` infers as `unit`, and every arm below
// then conflicts with it.
and renderField (field: FieldSchema) values setValues : ReactElement =
    // Render per-FieldKind.
    match field.Kind with
    | TextField _ -> Html.text "..."
    | NumberField _ -> Html.text "..."
    | DateField -> Html.text "..."
    | BoolField -> Html.text "..."
    | ChoiceField _ -> Html.text "..."
    | _ -> Html.text "..."
```

The submission API still validates server-side via the schema's `ValidationRule`s. Your custom renderer's UX matters for input quality; the server's validation is the authority.

## Submission analysers (extension stub)

`IFormSubmissionAnalyser` is the extension point for richer analysis of submission corpora (sentiment, clustering, key-theme extraction). The default impl ships nothing; consumer companion packages plug in. `IAnalyserCache` memoises results across calls — wire via `FormsServerApp.withAnalyserCache` or let the compose step auto-construct a `ResultStoreAnalyserCache` when an `IResultStore` is in DI.

Skeleton for a custom analyser:

`IAIProvider` lives in `ToolUp.Platform.AI`, so a server-side analyser that calls a model needs that open:

```fsharp
open ToolUp.Platform.AI
open ToolUp.Forms.AggregationTypes
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.IFormSubmissionAnalyser

type ClaudeBasedAnalyser(aiProvider: IAIProvider) =
    interface IFormSubmissionAnalyser with
        // Echoed onto every produced AnalyserOutput and used as the
        // deduplication key, so it must be unique in the deployment.
        member _.Name = "sentiment-claude"

        // The engine calls this once PER FIELD, so the analyser decides per
        // FieldKind whether it has anything to say — `None` for a field it
        // does not apply to, which is never treated as a failure.
        member _.Analyse(schema, field, submissions) = async {
            match field.Kind with
            | TextField(Some maxLength) when maxLength < 500 -> return None
            | TextField _ ->
                let texts =
                    submissions
                    |> List.choose (fun s ->
                        match Map.tryFind field.Key s.Values with
                        | Some(TextValue text) -> Some text
                        | _ -> None)

                // Run sentiment + clustering via the LLM ...
                let! payload = summariseSentiment aiProvider texts

                return
                    Some {
                        AnalyserName = "sentiment-claude"
                        FieldKey = field.Key
                        Payload = payload
                    }
            | _ -> return None
        }
```

Register the analyser in the DI container (the SDK's standard `services.AddSingleton<IFormSubmissionAnalyser>` pattern); `FormsServerApp.run` resolves `GetServices<IFormSubmissionAnalyser>()` per request and composes the registered analysers. Multiple analysers (one for sentiment, one for topic clustering) compose cleanly without a fan-out wrapper.

## Multi-scope schema overrides

`DefaultedFormStore` (the decorator wired by default) makes compose-time-registered schemas available in every scope without a per-scope `SaveSchema` call. A scope can override the default by calling `IFormApi.SaveSchema` with the same `FormSchemaId` — the inner `IFormStore` takes precedence over the registered default.

The lookup order on `GetSchema`:
1. Inner store's per-scope copy.
2. Compose-time registered default (only when the inner store returns `NotFound`).

Scopes without overrides see the default; scopes with overrides see their version. Every `SaveSchema` write goes to the inner store unchanged — defaults are read-fallbacks, never written through.

For deployments that **don't** want per-scope overrides, skip the default decorator by wiring your own `IFormStore` directly into the SDK's DI container before `FormsServerApp.run` resolves it. (Without a per-scope override the decorator's behaviour is identical to the inner store, so this is rarely worth the complexity.)

## Schema versioning

`FormSchema.Version` is overwritten by `IFormStore.SaveSchema` — version 1 on first save, monotonically increasing on subsequent saves. Submissions persist `SchemaVersion` (the schema version at submit time) so historical submissions stay readable even after the schema has evolved.

`IFormStore.GetSchema` takes `version: int option`:
- `version = None` returns the latest.
- `version = Some n` returns that specific historical version.

`FormRenderer` always renders against the latest schema. For displaying historical submissions against their original schema, fetch the versioned schema via `IFormApi.GetSchema (formId, Some submission.SchemaVersion)`.

## Replacing `IFormStore` or `IWorkflowEngine`

Both interfaces are six-rule portable. Drop-in alternatives mirror the same shape:

```fsharp
open ToolUp.Forms.IFormStore

type PostgresFormStore(connectionString: string) =
    interface IFormStore with
        member _.GetSchema (scopeId, schemaId, version) = async { return Ok Unchecked.defaultof<_> }
        // ... every other abstract method ...
        member _.SaveSchema (scopeId, schema) = async { return Ok schema }
        member _.ListSchemas scopeId = async { return [] }
        member _.DeleteSchema (scopeId, schemaId) = async { return Ok () }
        member _.SaveSubmission (scopeId, submission) = async { return Ok submission }
        member _.GetSubmission (scopeId, submissionId) = async { return Ok Unchecked.defaultof<_> }
        member _.ListSubmissions (scopeId, query) = async { return Ok [] }
        member _.DeleteSubmission (scopeId, submissionId) = async { return Ok () }
```

Wire via the SDK's DI registration (replacing the default `FormStore` factory). Conformance: bind to `IFormStoreContract` (in `ToolUp.Forms.Tests`) — the same pack passes for the shipped `FormStore` and any drop-in.

The default `WorkflowEngine` constructor accepts `IFormStore`, `IAuditLog`, `IActionLedger`, `IMetricsSink`, a warn callback, plus the workflows / guards / actions / action-policies maps. Replacing it means mirroring that constructor shape so `FormsServerApp.run` can pass through.

## `IShareTokenRateLimiter`

Per-token rate-limit gate consulted by `IPublicFormApi.SubmitWithToken` before any validation or persistence side-effect. The default implementation (`InMemoryShareTokenRateLimiter`) is single-instance only; multi-replica deployments wire a distributed companion so per-token windows are shared across nodes.

### Interface

```fsharp
type IShareTokenRateLimiter =
    /// Consult the rolling window for (scopeId, tokenId). Returns
    /// Ok () when admission is granted (and bookkeeps the admission
    /// against the window); returns Error ShareTokenError.RateLimited
    /// when the caller has consumed its rate.MaxUses budget inside
    /// the rolling rate.Window. Tokens whose claim has no RateLimit
    /// configured skip this call entirely.
    abstract Admit:
        scopeId: string * tokenId: string * rate: ShareTokenRateLimit ->
            Async<Result<unit, ShareTokenError>>

    /// The limiter's partition guarantee, declared as data rather than
    /// inferred from the runtime type (GP 12). `false` ⇒ window state is
    /// per-process, so a multi-replica deployment grants N × MaxUses per
    /// window and the declared limit is silently the PER-REPLICA one.
    /// `true` ⇒ the window is shared across replicas.
    /// `ShareTokenRateLimiterDistributionValidator` reads this at startup
    /// to refuse / warn on the scale-out misconfiguration.
    abstract IsDistributed: bool
```

The interface is six-rule-portable by construction — identity by value, async at boundary, retry as data (the DU `Result` return), stateless handlers (window state lives in the implementation, not the caller), per-key ordering only, and precision documented at the 1-second lower bound enforced by `ShareTokenTypes.validateRateLimit`.

### Wiring a custom limiter

```fsharp
let myRedisLimiter: IShareTokenRateLimiter =
    MyCompany.RedisShareTokenRateLimiter(redisConnString) :> _

FormsServerApp.create ()
|> FormsServerApp.withShareTokenRateLimiter myRedisLimiter
// ...rest of pipeline
|> FormsServerApp.run
```

Without `withShareTokenRateLimiter`, the compose step auto-builds a fresh `InMemoryShareTokenRateLimiter` per process — fine for dev / single-replica deployments.

### Conformance — `IShareTokenRateLimiterContract`

`ToolUp.Forms.Tests/Contracts/IShareTokenRateLimiterContract.fs` ships a framework-agnostic test pack. Any drop-in implementation MUST pass it:

```fsharp
open ToolUp.Forms.Tests.Contracts

let myLimiterTests =
    IShareTokenRateLimiterContract.tests
        "MyCompany.RedisShareTokenRateLimiter"
        true   // expectedDistributed — the pack asserts the declared posture
        (fun () -> MyCompany.RedisShareTokenRateLimiter(testConnString) :> IShareTokenRateLimiter)
```

Six checks covering admission inside budget, exact-budget boundary, beyond-budget rejection, per-token isolation, per-scope isolation, and sliding-window forward-motion.

## Companion conventions

For deeper customisation (custom field kinds, new ValidationRule cases, etc.), the right shape is "fork the package source" — the source is fully visible in the `fable/` directory of the `.Core` / `.Client` nupkgs. Read existing code, copy what you need, modify in your own copy.

For shallow customisation (validators / guards / actions / analysers / ledger), use the extension points above.

`ToolUp.Forms` is intentionally small. The interfaces are stable; the wire format is committed; the extension points are well-defined. For the 90% case of CRUD-with-state + schema-driven UX, the SDK ships everything needed. For the 10% case of bespoke shapes, customisation through the extension points + the per-scope override mechanism handles most needs; the rest is custom modules built directly against the interfaces.

---

_Documentation as-of forge `32ef4aa`._
