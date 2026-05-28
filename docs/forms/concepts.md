# Concepts

How `ToolUp.Forms` works under the hood. Three primary abstractions: schemas, submissions, workflows. Plus the publishable-survey extension and the Phase 21d action-ledger substrate.

## Schema model

A `FormSchema` is a typed declaration of fields, validators, and choices. The shipped record:

```fsharp
type FormSchemaId = string

type FormVisibility =
    | Internal
    | Publishable

type FormSchema = {
    /// Phase 19 reflection contract — entity primary key.
    Id: FormSchemaId
    /// Phase 19 reflection contract — entity type discriminator.
    Type: string
    /// Phase 19 reflection contract — overwritten by the store.
    Version: int
    DisplayName: string
    Description: string option
    Fields: FieldSchema list
    /// Phase 21b — flip to `Publishable` for share-link distribution.
    Visibility: FormVisibility
}

type FieldSchema = {
    /// Persistence identifier (stable across renames of `DisplayName`).
    Key: string
    DisplayName: string
    Description: string option
    Kind: FieldKind
    Required: bool
    Validators: ValidationRule list
}

type FieldKind =
    | TextField of maxLength: int option
    | NumberField of min: float option * max: float option
    | DateField
    | DateTimeField
    | BoolField
    | ChoiceField of options: string list
    | MultiChoiceField of options: string list
    | FileField of allowedTypes: string list
    | EntityRefField of entityType: string
    | NestedFormField of FormSchemaId

type ValidationRule =
    /// Pattern match. Empty string is always considered valid by Regex
    /// (use `FieldSchema.Required = true` to require presence).
    | Regex of pattern: string * description: string option
    /// Numeric range, applied to `NumberField`. No-op for other kinds.
    | NumberRange of min: float option * max: float option
    /// Length bounds for `TextField` / `ChoiceField` / `MultiChoiceField`.
    | LengthRange of min: int option * max: int option
    /// Custom validator looked up by registered name in the server-side
    /// `CustomValidatorRegistry`.
    | Custom of name: string
```

Construct schemas via the `FormSchema.create` helper which seeds the entity-reflection fields (`Type`, `Version = 1`) and defaults `Visibility = Internal`:

```fsharp
open ToolUp.Forms.FormSchema

let leadCaptureSchema =
    FormSchema.create "lead-capture-v1" "Lead Capture" [
        { Key = "name"
          DisplayName = "Your name"
          Description = None
          Kind = TextField (Some 100)
          Required = true
          Validators = [ LengthRange (Some 2, Some 100) ] }
        { Key = "email"
          DisplayName = "Email"
          Description = None
          Kind = TextField (Some 254)
          Required = true
          Validators = [ Regex (@"^[^@\s]+@[^@\s]+\.[^@\s]+$", Some "valid email address") ] }
    ]
```

### Why name-keyed `Custom`?

Custom validation logic can't cross the Fable serialisation boundary as closures. The `Custom of name: string` form references a named validator registered server-side via `FormsServerApp.withCustomValidator`. The schema crosses the wire as data; the function lookup happens at validation time.

The same pattern applies to workflow guards and actions — name-keyed, not closure-valued. The `Transition.Guard` and `Transition.Action` fields are `string option`.

### Schemas persist as entities

Each `FormSchema` is persisted as a Phase 19 entity via `IEntityStore`. Per-scope overrides are allowed: a deployment can register a global `customer-onboarding` schema at compose time and tenants can call `SaveSchema` at their own team scope to override it.

`DefaultedFormStore` is a decorator (wired automatically by `FormsServerApp.run`) that overlays compose-time-registered schemas as scope-wide fallbacks. When the server looks up a schema by `FormSchemaId`, it first checks the active scope's storage, then falls back to the registered default. Tenants without overrides see the default.

## Submission model

```fsharp
open System

type SubmissionId = string

type FieldValue =
    | TextValue of string
    | NumberValue of float
    | DateValue of DateOnly
    | DateTimeValue of DateTimeOffset
    | BoolValue of bool
    | ChoiceValue of string
    | MultiChoiceValue of string list
    | FileValue of fileId: string
    | EntityRefValue of entityId: string
    | NestedSubmissionValue of SubmissionId

type SubmissionState =
    /// In-progress. `UpdateDraft` accepts edits.
    | Draft
    /// Committed without an attached workflow.
    | Submitted
    /// Workflow-defined state. The string matches a workflow state name.
    | Custom of state: string

type SubmissionAuthor =
    | AuthenticatedUser of userId: string
    | InvitedRespondent of tokenId: string * attributedHandle: string option

type Submission = {
    /// Phase 19 reflection contract — entity primary key.
    Id: SubmissionId
    /// Phase 19 reflection contract — entity type discriminator.
    Type: string
    /// Phase 19 reflection contract — overwritten by the store.
    Version: int
    FormId: FormSchemaId
    /// Schema version at submission time (distinct from the entity's
    /// own `Version` which counts submission revisions).
    SchemaVersion: int
    SubmittedAt: DateTimeOffset
    Author: SubmissionAuthor
    /// Values keyed by `FieldSchema.Key`.
    Values: Map<string, FieldValue>
    State: SubmissionState
    /// Optional workflow binding. `None` for ad-hoc submissions.
    WorkflowId: string option
}
```

`Custom` is the workflow-driven state carrier — when a submission is bound to a workflow, its `State` cycles through `Custom "review"`, `Custom "approved"`, etc., matching the workflow's `From` / `To` state names.

### Indexed lookups

Submissions are indexed by `FormId` / `Author` / `State` + compound `FormId+State`. Queries like "all `Submitted` submissions for schema X" return in <50ms even with thousands of submissions per scope.

`SubmissionAuthor` index values are prefix-tagged: `"u:{userId}"` for authenticated users, `"t:{tokenId}"` for invited respondents. A single index keys both flavours; use `SubmissionAuthor.indexValueForUser` / `indexValueForToken` when constructing predicate queries.

See [`platform/architecture.md`](../platform/architecture.md) for how `IEntityStore` indexes work.

## Error model

```fsharp
type FieldError = {
    /// `FieldSchema.Key` of the offending field. Empty string for
    /// errors that aren't field-specific.
    FieldKey: string
    /// Wire-stable error code (`required`, `regex`, `range`,
    /// `choice-not-allowed`, `custom`, `wrong-type`).
    Code: string
    /// Human-readable message.
    Message: string
}

type FormError =
    | NotFound of resource: string * id: string
    | ValidationFailed of FieldError list
    | TransitionDenied of reason: string
    | StorageFailed of string
    | Unauthorised
    | InvalidTransition of currentState: string * attemptedEvent: string
    | WorkflowNotFound of workflowId: string
    /// Phase 21d — guard predicate threw during evaluation.
    | GuardEvaluationFailed of guard: string * reason: string
    /// Phase 21d — workflow action threw under `FailSubmission` policy.
    /// State write rolled back.
    | ActionFailed of actionName: string * reason: string
    /// Phase 21d — a prior `Pending` ledger entry blocks this attempt
    /// under the `FailSubmission` policy.
    | ActionPendingFromPriorAttempt of submissionId: string * actionName: string
```

A single `match` over `FormError` lets the client UI branch on validation / not-found / permission / workflow failures uniformly.

## Workflow engine

```fsharp
type WorkflowState = string
type WorkflowId = string
type TransitionEvent = string

type Transition = {
    From: WorkflowState
    Event: TransitionEvent
    To: WorkflowState
    /// Registered guard name. `None` = unconditional transition.
    Guard: string option
    /// Registered action name. `None` = pure state change.
    Action: string option
}

type WorkflowDefinition = {
    Id: WorkflowId
    /// State assigned to a submission whose `WorkflowId = Some Id` at
    /// submit time. Must be the `From` state of at least one transition.
    InitialState: WorkflowState
    Transitions: Transition list
}
```

### Apply flow

When `IFormApi.ApplyTransition` is called with `{ SubmissionId; Event }`:

1. Server reads the current `Submission` from `IFormStore`.
2. Server resolves the workflow via `Submission.WorkflowId`.
3. Server filters `Transitions` to those matching `(From = current state, Event = requested)`.
4. If no transition matches, returns `Error (InvalidTransition (currentState, attemptedEvent))`.
5. If a `Guard` is declared, resolves the named guard from the registry and runs it with `(Submission * AccessContext)` inside a `try/with`. A guard that returns `Error reason` surfaces as `TransitionDenied reason`; a guard that throws surfaces as `GuardEvaluationFailed (guard, reason)` so callers can choose to retry on transient faults vs surface a hard denial.
6. If the transition declares an `Action`, consult the action ledger (`IActionLedger.Lookup`). A `Succeeded` prior entry short-circuits the apply with `skipped_replay` observability; a `Pending` prior entry takes the per-action `ActionFailurePolicy` branch (`FailSubmission` aborts with `ActionPendingFromPriorAttempt`, `DeadLetter` and `LogOnly` proceed).
7. Updates `Submission.State` to `Custom To` (atomically with `IFormStore.SaveSubmission`).
8. Emits `WorkflowTransitioned` audit event.
9. If an `Action` is declared and the ledger said "proceed", writes a `Pending` ledger entry **before** invoking the action (replay-safety hinges on this ordering). Runs the action inside `try/with`. On success → `MarkSucceeded` + `WorkflowActionExecuted{status="succeeded"}` audit + counter increment. On exception → `MarkFailed` + `WorkflowActionExecuted{status="failed"}` audit + counter increment + apply the per-action `ActionFailurePolicy`.
10. Returns the updated `Submission`, or `Error (ActionFailed (actionName, reason))` when the policy is `FailSubmission` and the state write has been rolled back.

The workflow engine is intentionally simple — no parallel branches, no compensation logic, no long-running waiting. For more sophisticated workflows, use a proper workflow engine (Temporal, Camunda); Forms covers the 90% case of CRUD-with-state plus exactly-once side-effect semantics for the common "transition + notify" pattern.

### Action ledger and failure policy

Two production defects motivated the action-ledger substrate (Phase 21d):

1. **Silent loss.** A pre-ledger action that threw was warn-logged and swallowed. The submission committed; the side effect (email send, webhook fan-out, downstream API) was lost with no metric, no audit, no dead-letter row. Operators only heard about the failure when the customer followed up.
2. **Replay duplication.** A process restart between state-persist and action-completion re-fired the action on next `ApplyTransition`. Customers received two acknowledgement emails; payment captures double-debited; webhook subscribers got duplicate events.

The ledger turns action invocation into an exactly-once primitive per `(SubmissionId, transitionId, actionName)`. Combined with the per-action `ActionFailurePolicy` registered via `FormsServerApp.withActionPolicy`, the engine surfaces every failure on three independent observability surfaces — metric, audit row, dead-letter ledger entry — so silent loss is structurally impossible.

```fsharp
type ActionFailurePolicy =
    /// Abort the transition. State NOT committed.
    | FailSubmission
    /// (Default.) Commit submission. Persist `Failed` ledger entry.
    /// Operator drains via dead-letter retry orchestrator.
    | DeadLetter
    /// Pre-21d behaviour. Warn-log + commit. No dead-letter row.
    | LogOnly
```

| Policy            | Behaviour on action exception                                                                                                                  | Choose when                                                                                                          |
| ----------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `DeadLetter`      | (Default.) Commit submission. Persist `Failed` ledger entry. Emit `forms.workflow.action.outcome{status="failed"}` + `WorkflowActionExecuted{status="failed"}`. | Most actions — outbound notifications, downstream API fan-out, audit hooks. A retry orchestrator drains the dead-letter ledger. |
| `FailSubmission`  | Roll back the state write. Surface `FormError.ActionFailed (actionName, reason)`.                                                              | Load-bearing side effects: payment capture, regulatory webhook, legal notice. Committing the transition without the side effect would leave the system in a worse state. |
| `LogOnly`         | Warn-log the exception. Commit submission. Same metric + audit row as `DeadLetter` minus the dead-letter retry intent.                         | Genuinely best-effort sinks where a retry orchestrator adds no value — fire-and-forget observability beacons. Also the explicit opt-in for pre-21d behaviour. |

The `IActionLedger` contract is six-rule portable. The default `InMemoryActionLedger` is correct for dev / single-process deployments; distributed deployments wire a durable ledger via `FormsServerApp.withActionLedger`. The `IActionLedgerContract` portability pack (`ToolUp.Forms.Tests/Contracts/IActionLedgerContract.fs`) validates any impl against the same conformance bar.

### Why guards and actions are name-keyed

Same reason as `Custom` validators: closures don't cross Fable serialisation. The schema and the workflow definition cross the wire as data; the function lookup happens server-side at execution time.

Workflow definitions / guards / actions are **constructor parameters on `WorkflowEngine`** (immutable for process lifetime), not interface methods. This keeps the portability-rule-2 (async at every boundary) clean — the engine itself has async methods, but the registries are dictionary lookups.

## Validation chain

When `IFormApi.Submit` is called, validation runs:

1. **Schema lookup** — `IFormStore.GetSchema` (with scope fallback through `DefaultedFormStore`).
2. **Field-by-field** — for each field in the schema, run three passes:
   - `Required` — value must be present.
   - Coarse type check — present value must match the `FieldKind` shape (`TextField` → `TextValue`, `NumberField` → `NumberValue`, etc.).
   - Each `ValidationRule` in order: `Regex` (on `TextValue`), `NumberRange` (on `NumberValue`), `LengthRange` (on `TextValue` / `ChoiceField` / `MultiChoiceField`), `Custom` (look up by name in `CustomValidatorRegistry`, run).
3. **Collect errors** — `FieldError list` accumulated across every field. No short-circuit; the UI shows them all at once.
4. **Return** — `Ok submission` if all clear, `Error (FormError.ValidationFailed errors)` otherwise.

Client-side validation in `FormRenderer` mirrors the same rules for UX feedback; server-side is authoritative.

`Custom` validators are looked up by name in the registry populated via `FormsServerApp.withCustomValidator`. Missing-by-name custom validators are a soft pass (no error) so renaming a validator doesn't break in-flight forms.

## Publishable surveys (Phase 21b)

A `FormSchema` with `Visibility = Publishable` is distributable to anonymous respondents via signed share-link tokens. The flow:

```
admin issues N tokens
   │
   ▼  IFormApi.IssueTokens { SchemaId; Recipients; ExpiresAt; UseLimit }
   ▼  IShareTokenStore.Issue × N
   │      │
   │      ▼  HMAC-SHA256-sign { ResourceKind = "forms.publishable"
   │                          ; ResourceId = SchemaId
   │                          ; TokenId = newGuid
   │                          ; AttributedHandle = opaque option
   │                          ; ExpiresAt
   │                          ; UseLimit }
   │      ▼  persist claim to IBlobStorage
   │
   ▼  result: IssuedToken list (each carries Url = /r/{token})
   ▼  admin distributes (email / SMS / panel provider)
   │
   ▼  respondent visits /r/{token}
   ▼  PublicEmbed Feliz component renders form
   ▼  respondent submits
   │
   ▼  POST /api/public/forms/SubmitWithToken { Token; Values }
   ▼  IPublicFormApi.SubmitWithToken handler:
   │      ▼  IShareTokenStore.Validate token
   │           ├─ signature check (HMAC-SHA256)
   │           ├─ ResourceKind = "forms.publishable" check
   │           ├─ ResourceId resolves to a Publishable schema
   │           ├─ ExpiresAt not past
   │           └─ UseLimit not exhausted (atomic decrement)
   │      ▼  FormValidator.validate
   │      ▼  IFormStore.SaveSubmission { Author = InvitedRespondent (tokenId, handle); ... }
   │      ▼  emit FormSubmitted audit event
   │
   ▼  respondent sees success message; token use-count incremented
```

### Token wire format

Tokens are opaque bearer strings. Validation checks the HMAC signature first (cheap; constant-time comparison), then payload semantics. Forged tokens fail at the signature check; valid tokens proceed to claim-state lookup. The `IShareTokenStore` substrate owns the exact wire format — see `docs/platform/share-tokens.md` for the canonical encoding.

The `ResourceKind` string for the forms surface is `"forms.publishable"` (exposed as `PublicFormApi.ResourceKind`). The validator rejects tokens whose `ResourceKind` doesn't match — guards against a token issued for one resource being re-used to access another.

### Anonymous-route registration

The `/api/public/forms/*` prefix is registered as an anonymous route via `ServerApp.withAnonymousRoute PublicFormApi.AnonymousRoutePrefix`. `FormsServerApp.run` calls this automatically; `AuthEnforcementMiddleware` skips its 401 gate for `/api/public/forms/*`. The `/r/{token}` browser-facing route is owned by the consumer app (typically a static HTML host loading the `PublicEmbed` Feliz bundle).

### Cross-cutting validation chain

`PublishableFormConfigValidator` is an `IConfigValidator` that runs at preflight. Severity is **mode-aware** (Phase 21e):

| Condition | `Anonymous` / `AuthenticatedEphemeral` | `Individual` / `Team` / `MultiTeam` |
|---|---|---|
| `Publishable` schema + `ShareTokenStore = NoShareTokenStore` | `Warning` | **`Error`** (boot fails) |
| `Publishable` schema + `PublicBaseUrl = None` or whitespace | `Warning` | **`Error`** (boot fails) |
| No Publishable schemas | `Ok` | `Ok` |

The escape hatch `ServerConfig.AcceptUnsignedPublishable = true` (env `TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE=1`) downgrades the persistent-mode `Error` back to `Warning` for the staging-shape-in-production-mode edge case — explicit operator opt-in for the dry-run scenario where Publishable schemas are registered but the share-link surface is not yet wired.

The `Error` exists because a misconfigured production deployment that boots cleanly with only a `Warning` ships a public surface with no signed-token gate, no use-limit enforcement, and no revocation. Persistent-data deployments must wire `EnabledShareTokenStore` + a valid `PublicBaseUrl` or explicitly opt out via the escape hatch.

The `INotificationSink` checks emit `Warning` regardless of mode (deployments dispatching via the creator's own MTA legitimately skip the sink).

### Per-token rate limit (Phase 21e)

Tokens issued via `IFormApi.IssueTokens` carry an optional `ShareTokenRateLimit` field:

```fsharp
type ShareTokenRateLimit = {
    MaxUses: int           // requests admitted per window; must be >= 1
    Window: TimeSpan       // rolling window; must be >= 1 second
}
```

`None` (the default) means no per-window rate limit — only the static `UseLimit` cap applies. Set `Some { MaxUses = 5; Window = TimeSpan.FromMinutes 1.0 }` to cap a leaked token's blast radius at 5 submissions/minute. `MaxUses < 1` or `Window < 1s` is rejected at issue-time (`ShareTokenError.StorageFailed`).

`IPublicFormApi.SubmitWithToken` consults the registered `IShareTokenRateLimiter` before any validation or persistence side-effect; rejection returns `FormError.RateLimited`. The SDK default is `InMemoryShareTokenRateLimiter` (single-instance only); distributed deployments wire a Redis / `IRateLimitStore`-backed companion via `FormsServerApp.withShareTokenRateLimiter` so per-token windows are shared across replicas.

### Public-embed message collapsing (Phase 21e)

`PublicEmbed.describeError` renders the same user-visible string — `"This link is no longer valid. Please ask the survey owner for a new link."` — for every token-rejection case (`Unauthorised`, `NotFound("token", "expired" / "revoked" / "use-limit-exceeded")`, `RateLimited`). Server-side audit log still distinguishes the cases so operator observability is unchanged; the collapse prevents a token-guessing attacker using the embed as a token-state oracle.

### Distribution flows

Three flows out of the box:

1. **Platform-dispatched email** — `IFormApi.DispatchInvitationsByEmail` accepts a list of `{ Email; DisplayName? }` and ships invitations via the registered `INotificationSink`. Uses `TransactionalEmail` notification kind. Requires an email channel companion (`ToolUp.NotificationChannels.Email.Smtp` or `Email.SendGrid`).
2. **Creator's own MTA** — `IFormApi.IssueTokens` returns `IssuedToken list` (each with a fully-composed `Url`); the admin (or the creator's UI) sends them via whatever email tool they prefer. The platform never touches the recipient list.
3. **Third-party survey provider with opaque handles** — same `IssueTokens`, but with opaque `Handle` values (`Handle = "panel-123"`). The platform stores the handle but never sees the underlying real email / panel id. Useful for compliance scenarios where the platform must be unable to correlate respondents to identities.

### Survey overview + aggregations

Admins query `IFormApi.GetAggregations` to get per-question response counts + summaries:

```fsharp
// Shape sketch — exact fields live in ToolUp.Forms.AggregationTypes.
type FieldAggregation =
    | ChoiceCounts of Map<string, int>
    | NumericSummary of {| Mean: float; Min: float; Max: float; StdDev: float |}
    | TextSamples of string list

type AggregationSummary = {
    SchemaId: FormSchemaId
    TotalResponses: int
    PerField: Map<string, FieldAggregation>
}
```

`SurveyDashboardView` (Feliz) consumes this for the admin UI. `SurveyListView` shows the multi-survey overview (one row per Publishable schema with response count + last response time) — backed by `IFormApi.ListSchemasOverview`.

`IFormSubmissionAnalyser` is the extension stub for richer analysis (sentiment, clustering, thematic analysis). The default impl ships nothing; consumer companion packages plug in custom analysers; Phase 21c added `IAnalyserCache` for memoisation across calls.

## Audit + observability

Audit events under `_platform.audit`:

- `FormSubmitted` — submission created (authenticated or invited respondent).
- `FormSubmissionUpdated` — submission edited (Draft → Submitted, or in-place edit by admin).
- `WorkflowTransitioned` — workflow state change.
- `WorkflowActionExecuted` — workflow-action invocation outcome (`succeeded` / `failed` / `skipped_replay` / `skipped_pending`). Emitted once per `ApplyTransition` that names an action; cross-references the metric counter + the action-ledger row.

Each event carries actor (`AuthenticatedUser` or `InvitedRespondent`), schema id, submission id, and a server-side timestamp. Replicated by the audit-sink subsystem; available for compliance trails.

## Strip-imports verifiability

Forms ships as a pure companion: deployments that don't reference `ToolUp.Forms.Server` / `ToolUp.Forms.Client` pay zero runtime cost. The strip-imports test verifies in both directions:

- With imports: forms work as described.
- Without imports: the app builds and runs identically to a Forms-less deployment; no orphan types, no dangling registrations.

This is the conformance bar for companion-shape correctness.

## Six-rule portability audit

`IFormStore`, `IWorkflowEngine`, `IShareTokenStore`, `IActionLedger` all satisfy the six portability rules. Custom validators / guards / actions are name-keyed (rule 1: identity by value). Every async at every boundary (rule 2). Failure flows as data records, not callbacks (rule 3). Handlers are stateless between invocations (rule 4). No cross-shard ordering promises (rule 5). No sub-second timing claims (rule 6).

Conformance test packs: `IFormStoreContract`, `IWorkflowEngineContract`, `IShareTokenStoreContract`, `IActionLedgerContract` — each binds to the shipped default and is reusable against any drop-in implementation.

---

_Documentation as-of forge `32ef4aa`._
