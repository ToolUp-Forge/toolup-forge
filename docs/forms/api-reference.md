# API reference

Public surface of `ToolUp.Forms`. Type signatures are reproduced verbatim from `ToolUp.Forms.Core` and `ToolUp.Forms.Server` so each fenced block compiles against the shipped types.

## `ToolUp.Forms.Core`

### `FormSchema` (`module ToolUp.Forms.FormSchema`)

```fsharp skip=signature
type FormSchemaId = string

type FormVisibility =
    | Internal
    | Publishable

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
    | Regex of pattern: string * description: string option
    | NumberRange of min: float option * max: float option
    | LengthRange of min: int option * max: int option
    | Custom of name: string

type FieldSchema = {
    Key: string
    DisplayName: string
    Description: string option
    Kind: FieldKind
    Required: bool
    Validators: ValidationRule list
}

type FormSchema = {
    /// Entity-store reflection contract — primary key.
    Id: FormSchemaId
    /// Entity-store reflection contract — entity-type discriminator.
    Type: string
    /// Entity-store reflection contract — overwritten by the store.
    Version: int
    DisplayName: string
    Description: string option
    Fields: FieldSchema list
    Visibility: FormVisibility
}

module FormSchema =
    [<Literal>]
    val entityType : string = "FormSchema"

    /// Construct a v1 schema with default `Type` / `Version` fields
    /// and `Visibility = Internal`.
    val create :
        id: FormSchemaId ->
        displayName: string ->
        fields: FieldSchema list ->
        FormSchema

    /// Look up a field by `Key`. `None` if absent.
    val tryFindField : key: string -> schema: FormSchema -> FieldSchema option
```

### `Submission` (`module ToolUp.Forms.FormSubmission`)

```fsharp skip=signature
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
    | Draft
    | Submitted
    | Custom of state: string

module SubmissionState =
    val toIndexValue : SubmissionState -> string
    val fromIndexValue : string -> SubmissionState

type SubmissionAuthor =
    | AuthenticatedUser of userId: string
    | InvitedRespondent of tokenId: string * attributedHandle: string option

module SubmissionAuthor =
    val toIndexValue : SubmissionAuthor -> string
    val indexValueForUser : userId: string -> string
    val indexValueForToken : tokenId: string -> string

type Submission = {
    /// Entity-store reflection contract — server-allocated primary key.
    Id: SubmissionId
    /// Entity-store reflection contract — entity-type discriminator.
    Type: string
    /// Entity-store reflection contract — overwritten on each save.
    Version: int
    FormId: FormSchemaId
    /// Schema version at submission time.
    SchemaVersion: int
    SubmittedAt: DateTimeOffset
    Author: SubmissionAuthor
    Values: Map<string, FieldValue>
    State: SubmissionState
    WorkflowId: string option
}

module Submission =
    [<Literal>]
    val entityType : string = "Submission"

    [<Literal>]
    val indexFormId : string = "FormId"

    [<Literal>]
    val indexAuthor : string = "Author"

    [<Literal>]
    val indexState : string = "State"

    [<Literal>]
    val indexFormIdState : string = "FormId+State"
```

### `FormError` (`module ToolUp.Forms.FormSubmission`)

```fsharp
type FieldError = {
    FieldKey: string
    Code: string
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
    | GuardEvaluationFailed of guard: string * reason: string
    | ActionFailed of actionName: string * reason: string
    | ActionPendingFromPriorAttempt of submissionId: string * actionName: string
```

### `Workflow` (`module ToolUp.Forms.Workflow`)

```fsharp skip=signature
type WorkflowState = string
type WorkflowId = string
type TransitionEvent = string

type Transition = {
    From: WorkflowState
    Event: TransitionEvent
    To: WorkflowState
    /// Registered guard name. `None` = unconditional.
    Guard: string option
    /// Registered action name. `None` = pure state change.
    Action: string option
}

type WorkflowDefinition = {
    Id: WorkflowId
    InitialState: WorkflowState
    Transitions: Transition list
}

/// Per-action policy on exception (consulted by the action ledger).
type ActionFailurePolicy =
    | FailSubmission
    | DeadLetter
    | LogOnly

module WorkflowDefinition =
    val tryFindTransition :
        state: WorkflowState ->
        event: TransitionEvent ->
        def: WorkflowDefinition ->
        Transition option

    val listFromState :
        state: WorkflowState -> def: WorkflowDefinition -> Transition list

    val states : def: WorkflowDefinition -> Set<WorkflowState>
```

### `IActionLedger` (`module ToolUp.Forms.IActionLedger`)

```fsharp skip=signature
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
        entry: ActionLedgerEntry -> Async<Result<unit, LedgerError>>

    abstract Lookup :
        submissionId: SubmissionId *
        transitionId: string *
        actionName: string ->
            Async<Result<ActionLedgerEntry option, LedgerError>>

    abstract MarkSucceeded :
        submissionId: SubmissionId *
        transitionId: string *
        actionName: string ->
            Async<Result<unit, LedgerError>>

    abstract MarkFailed :
        submissionId: SubmissionId *
        transitionId: string *
        actionName: string *
        reason: string ->
            Async<Result<unit, LedgerError>>

module ActionLedger =
    /// "{From}:{Event}:{To}"
    val transitionId : t: Transition -> string
```

### `IFormApi` (`module ToolUp.Forms.FormApi`)

The authenticated ToolUp.Remoting wire contract. Mounted by `FormsServerApp.run` at `/api/IFormApi/<MethodName>`.

```fsharp skip=signature
open System
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Forms.AggregationTypes

type SubmitRequest = {
    FormId: FormSchemaId
    Values: Map<string, FieldValue>
    /// `None` => state becomes `Submitted`.
    /// `Some workflowId` => state becomes `Custom workflow.InitialState`.
    WorkflowId: WorkflowId option
}

type UpdateDraftRequest = {
    SubmissionId: SubmissionId
    Values: Map<string, FieldValue>
}

type ApplyTransitionRequest = {
    SubmissionId: SubmissionId
    Event: TransitionEvent
}

/// IssueTokens recipient (opaque handle — third-party survey provider shape).
type IssueRecipient = {
    Handle: string
    DisplayName: string option
}

type IssueTokensRequest = {
    SchemaId: FormSchemaId
    Recipients: IssueRecipient list
    /// None = store-default lifetime (typically 30 days).
    ExpiresAt: DateTimeOffset option
    /// Outer None = store default (typically Some 1).
    /// Some None = unlimited. Some (Some n) = at most n uses.
    UseLimit: int option option
}

type IssuedToken = {
    Handle: string
    TokenId: string
    /// Fully composed embed URL: "{PublicBaseUrl}/r/{token}".
    Url: string
    ExpiresAt: DateTimeOffset
}

/// DispatchInvitationsByEmail recipient (real email).
type EmailInvitation = {
    Email: string
    DisplayName: string option
}

type DispatchInvitationsRequest = {
    SchemaId: FormSchemaId
    Invitations: EmailInvitation list
    ExpiresAt: DateTimeOffset option
    UseLimit: int option option
    Subject: string
    /// Body template. {{link}} (required) and {{displayName}} (optional)
    /// are substituted per recipient.
    BodyTemplate: string
}

type DispatchedInvitation = {
    Email: string
    TokenId: string option
    Url: string option
    Error: string option
}

type DispatchSummary = {
    Recipients: DispatchedInvitation list
    SuccessCount: int
}

/// Derived survey lifecycle status.
type SurveyStatus =
    | Draft
    | Active
    | Closed

type SurveyOverviewRow = {
    Schema: FormSchema
    SubmissionCount: int
    InvitedCount: int
    ResponseRate: float option
    Status: SurveyStatus
}

type CloseSurveyMode =
    | HideSchema
    | RevokeAllTokens
    | HideSchemaAndRevoke

type CloseSurveyRequest = {
    SchemaId: FormSchemaId
    Mode: CloseSurveyMode
}

type CloseSurveyResult = {
    SchemaId: FormSchemaId
    NewVisibility: FormVisibility
    TokensRevoked: int
}

type IFormApi = {
    // Schema management (Owner/Admin)
    SaveSchema : FormSchema -> Async<Result<FormSchema, FormError>>
    GetSchema : FormSchemaId * int option -> Async<FormSchema option>
    ListSchemas : unit -> Async<FormSchema list>
    DeleteSchema : FormSchemaId -> Async<Result<unit, FormError>>

    // Submission lifecycle (any authenticated)
    Submit : SubmitRequest -> Async<Result<Submission, FormError>>
    UpdateDraft : UpdateDraftRequest -> Async<Result<Submission, FormError>>
    GetSubmission : SubmissionId -> Async<Submission option>
    ListSubmissions :
        EntityQuery<Submission> -> Async<Result<Submission list, FormError>>

    // Workflow operations
    ApplyTransition :
        ApplyTransitionRequest -> Async<Result<Submission, FormError>>
    ListPossibleTransitions : SubmissionId -> Async<Transition list>

    // Publishable surveys — share-link issuance (Owner/Admin)
    IssueTokens :
        IssueTokensRequest -> Async<Result<IssuedToken list, FormError>>

    // Publishable surveys — aggregation dashboard (Owner/Admin)
    GetAggregations :
        FormSchemaId -> Async<Result<AggregationSummary, FormError>>

    // Publishable surveys — optional email-dispatch convenience
    DispatchInvitationsByEmail :
        DispatchInvitationsRequest -> Async<Result<DispatchSummary, FormError>>

    // Publishable surveys — multi-survey admin
    ListSchemasOverview : unit -> Async<SurveyOverviewRow list>
    CloseSurvey :
        CloseSurveyRequest -> Async<Result<CloseSurveyResult, FormError>>

    // Analyser-output cache control (Owner/Admin)
    RebuildAnalyserOutputs : FormSchemaId -> Async<Result<int, FormError>>
}

/// Route shape: "/api/{typeName}/{methodName}"
val routeBuilder : typeName: string -> methodName: string -> string
```

### `IPublicFormApi` (`module ToolUp.Forms.PublicFormApi`)

Anonymous, token-gated public surface for publishable surveys. Mounted at `/api/public/forms/<MethodName>`.

```fsharp skip=signature
[<Literal>]
val ResourceKind : string = "forms.publishable"

[<Literal>]
val AnonymousRoutePrefix : string = "/api/public/forms/"

type SubmitWithTokenRequest = {
    Token: string
    Values: Map<string, FieldValue>
}

type IPublicFormApi = {
    /// Resolve the schema referenced by a valid token. Returns ONLY
    /// the schema — never historical submissions, aggregations, or
    /// the recipient list.
    GetSchemaByToken : string -> Async<Result<FormSchema, FormError>>

    /// Submit field values against the form referenced by a valid
    /// token. On success: persisted with
    /// `Author = InvitedRespondent (tokenId, attributedHandle)`;
    /// `UsedCount` is bumped; `FormSubmitted` audit event emitted.
    /// `ValidationFailed` does NOT bump the use count.
    SubmitWithToken :
        SubmitWithTokenRequest -> Async<Result<Submission, FormError>>
}

/// Route shape: "/api/public/forms/{methodName}"
val routeBuilder : typeName: string -> methodName: string -> string
```

### `AggregationTypes` (`module ToolUp.Forms.AggregationTypes`)

```fsharp
/// Sketch — full record lives in ToolUp.Forms.AggregationTypes.
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

## `ToolUp.Forms.Server`

### `IFormStore` (`module ToolUp.Forms.IFormStore`)

```fsharp
open ToolUp.Platform.EntityQueryTypes

type IFormStore =
    abstract SaveSchema :
        scopeId: string * schema: FormSchema ->
            Async<Result<FormSchema, FormError>>

    abstract GetSchema :
        scopeId: string *
        schemaId: FormSchemaId *
        version: int option ->
            Async<Result<FormSchema, FormError>>

    abstract ListSchemas : scopeId: string -> Async<FormSchema list>

    abstract DeleteSchema :
        scopeId: string * schemaId: FormSchemaId ->
            Async<Result<unit, FormError>>

    abstract SaveSubmission :
        scopeId: string * submission: Submission ->
            Async<Result<Submission, FormError>>

    abstract GetSubmission :
        scopeId: string * submissionId: SubmissionId ->
            Async<Result<Submission, FormError>>

    abstract ListSubmissions :
        scopeId: string * query: EntityQuery<Submission> ->
            Async<Result<Submission list, FormError>>

    abstract DeleteSubmission :
        scopeId: string * submissionId: SubmissionId ->
            Async<Result<unit, FormError>>
```

Default impl: `FormStore` over `IEntityStore`, optionally wrapped by `DefaultedFormStore` (overlays compose-time defaults).

### `IWorkflowEngine` (`module ToolUp.Forms.IWorkflowEngine`)

```fsharp
open ToolUp.Platform

/// Guard predicate keyed by name in the engine's guard registry.
type WorkflowGuard =
    Submission * AccessContext -> Async<Result<unit, string>>

/// Action keyed by name in the engine's action registry.
type WorkflowAction =
    Submission * AccessContext -> Async<unit>

type IWorkflowEngine =
    /// Apply a transition event to a submission. Failure modes (in
    /// order of detection): WorkflowNotFound; InvalidTransition;
    /// TransitionDenied; GuardEvaluationFailed; ActionFailed;
    /// ActionPendingFromPriorAttempt; StorageFailed.
    abstract Apply :
        scopeId: string *
        submissionId: SubmissionId *
        event: TransitionEvent *
        accessContext: AccessContext ->
            Async<Result<Submission, FormError>>

    /// Enumerate transitions whose `From` matches the submission's
    /// current state. Empty when the submission has no workflow or
    /// is terminal.
    abstract ListPossibleTransitions :
        scopeId: string * submissionId: SubmissionId ->
            Async<Transition list>
```

Default impl: `WorkflowEngine`. Constructor parameters (immutable for process lifetime):

```fsharp skip=fragment
new WorkflowEngine(
    formStore: IFormStore,
    auditLog: IAuditLog,
    ledger: IActionLedger,
    metricsSink: IMetricsSink,
    warn: WorkflowWarn,                      // string -> unit
    workflows: Map<WorkflowId, WorkflowDefinition>,
    guards: Map<string, WorkflowGuard>,
    actions: Map<string, WorkflowAction>,
    actionPolicies: Map<string, ActionFailurePolicy>
)
```

### `FormValidator` (`module ToolUp.Forms.FormValidator`)

```fsharp skip=signature
/// Server-side custom validator. Receives the raw string form of a
/// field value; returns `Ok ()` to pass or `Error message` to fail.
type CustomValidator = string -> Result<unit, string>

type CustomValidatorRegistry = Map<string, CustomValidator>

val emptyRegistry : CustomValidatorRegistry

module FormValidator =
    /// Validate every field. Returns Ok () on clean pass; Error
    /// (FieldError list) accumulates every failure across every field.
    val validate :
        schema: FormSchema ->
        values: Map<string, FieldValue> ->
        registry: CustomValidatorRegistry ->
            Result<unit, FieldError list>
```

### `IActionLedger` impls

- **`InMemoryActionLedger`** (`module ToolUp.Forms.InMemoryActionLedger`) — process-local thread-safe dictionary. Auto-defaulted by `FormsServerApp.run` when no explicit ledger is wired. Correct for dev / single-process deployments; does NOT survive a process restart.
- **Distributed impls** — supply via `FormsServerApp.withActionLedger`; conformance against the `IActionLedgerContract` test pack is the bar.

### `FormsServerApp` (`module ToolUp.Forms.FormsCompose`)

`FormsServerApp` wraps a base `ServerApp` and adds the form-specific compose helpers.

```fsharp skip=signature
type FormsServerApp = {
    Base: ServerApp
    Schemas: Map<FormSchemaId, FormSchema>
    Workflows: Map<WorkflowId, WorkflowDefinition>
    CustomValidators: CustomValidatorRegistry
    Guards: Map<string, WorkflowGuard>
    Actions: Map<string, WorkflowAction>
    AnalyserCache: IAnalyserCache option
    ActionLedger: IActionLedger option
    ActionPolicies: Map<string, ActionFailurePolicy>
}

module FormsServerApp =
    val create : unit -> FormsServerApp

    // Delegating helpers (every `ServerApp.with*`).
    val withConfig : ServerConfig -> FormsServerApp -> FormsServerApp
    val withAuth : IAuthProvider -> FormsServerApp -> FormsServerApp
    val withLogger : ILogger -> FormsServerApp -> FormsServerApp
    val withStorage : IBlobStorage -> FormsServerApp -> FormsServerApp
    val withNotifications : INotificationChannel -> FormsServerApp -> FormsServerApp
    val withTransactionalSink : INotificationSink -> FormsServerApp -> FormsServerApp
    val withHealthCheck : IHealthCheck -> FormsServerApp -> FormsServerApp
    val withConfigValidator : IConfigValidator -> FormsServerApp -> FormsServerApp
    val withEncryptedBlobStorage : IBlobEncryptionKeyResolver -> FormsServerApp -> FormsServerApp
    val withEntity<'T> : EntityRegistration<'T> -> FormsServerApp -> FormsServerApp
    val withPreMiddleware :
        (IApplicationBuilder -> IApplicationBuilder) -> FormsServerApp -> FormsServerApp
    val withPostMiddleware :
        (IApplicationBuilder -> IApplicationBuilder) -> FormsServerApp -> FormsServerApp
    val addModule : ServerModule -> FormsServerApp -> FormsServerApp
    val addModules : ServerModule list -> FormsServerApp -> FormsServerApp

    // Forms-specific helpers.
    /// Register a compose-time default schema available in every
    /// scope. Boot-time rejection of duplicate `FieldSchema.Key`.
    val withFormSchema : FormSchema -> FormsServerApp -> FormsServerApp

    val withWorkflow : WorkflowDefinition -> FormsServerApp -> FormsServerApp

    val withCustomValidator :
        name: string -> CustomValidator -> FormsServerApp -> FormsServerApp

    val withGuard :
        name: string -> WorkflowGuard -> FormsServerApp -> FormsServerApp

    val withAction :
        name: string -> WorkflowAction -> FormsServerApp -> FormsServerApp

    val withAnalyserCache : IAnalyserCache -> FormsServerApp -> FormsServerApp

    val withActionLedger : IActionLedger -> FormsServerApp -> FormsServerApp
    val withActionPolicy :
        actionName: string ->
        policy: ActionFailurePolicy ->
        FormsServerApp ->
        FormsServerApp

    val run : FormsServerApp -> int
```

### `DefaultedFormStore`

Decorator over `IFormStore` that overlays compose-time-registered schemas as scope-wide fallbacks. The lookup order on `GetSchema`:

1. Inner `IFormStore` — if a schema exists with the requested `FormSchemaId` in scope, return it.
2. Compose-time registered defaults — if the inner store returned `NotFound`, fall back to the matching default schema (provided its version is requested or no version was specified).

Tenants get override capability for free; deployments without per-tenant overrides see the compose-time default. `SaveSchema` always writes to the inner store. Wired automatically by `FormsServerApp.run`.

### `PublishableFormConfigValidator`

`IConfigValidator` impl. Emits `Warning` when a Publishable schema lacks `PublicBaseUrl` / `ShareTokenStore` / `INotificationSink`. Registered automatically by `FormsServerApp.run`. See [concepts.md](concepts.md).

### `IFormSubmissionAnalyser` (extension stub)

```fsharp
/// Sketch — exact record lives in ToolUp.Forms.IFormSubmissionAnalyser.
type IFormSubmissionAnalyser =
    abstract Analyse :
        scopeId: string ->
        schema: FormSchema ->
        submissions: Submission list ->
            Async<AnalysisResult>
```

No default impl ships. Consumers register custom analysers in the DI container; `FormsServerApp.run` resolves the list per-request and composes them.

## `ToolUp.Forms.Client`

### `FormRenderer`

```fsharp skip=fragment
FormRenderer.render
    {| Schema : FormSchema
       InitialValues : Map<string, FieldValue>
       OnSubmit : Map<string, FieldValue> -> unit |}
```

Feliz component. Uses `React.useState` for in-flight values. Dispatches `OnSubmit` only on user submit (Enter or button click).

### `WorkflowBadge`

```fsharp skip=fragment
WorkflowBadge.render
    {| State : SubmissionState
       Workflow : WorkflowDefinition |}
```

Renders a state pill with hover for the available-transitions list.

### `FormSubmissionsList`

```fsharp skip=fragment
FormSubmissionsList.render
    {| Submissions : Submission list
       Schema : FormSchema
       Workflow : WorkflowDefinition option
       OnTransition : SubmissionId -> TransitionEvent -> unit |}
```

Table of submissions with optional per-row transition buttons (filtered by available transitions from current state).

### `PublicEmbed`

```fsharp skip=fragment
PublicEmbed.render
    {| Token : string         // from URL parameter
       BaseUrl : string |}
```

Standalone Feliz component. Renders the form with no app shell — meant to be embedded in a minimal `index.html` at `/r/{token}`.

### `SurveyDashboardView` + `SurveyListView`

```fsharp skip=fragment
SurveyDashboardView.render
    {| SchemaId : FormSchemaId
       Aggregations : AggregationSummary |}

SurveyListView.render
    {| Surveys : SurveyOverviewRow list
       OnSelect : FormSchemaId -> unit |}
```

Apps wire these into their own admin module.

### ToolUp.Remoting proxies

```fsharp skip=signature
val FormsClient.proxy : IFormApi
val PublicFormsClient.proxy : IPublicFormApi
```

Use directly in Elmish commands:

```fsharp
Cmd.OfAsync.either (fun req -> FormsClient.proxy.Submit req) request onSuccess onFailure
```

## Audit events emitted to `_platform.audit`

- `FormSubmitted` — submission created (authenticated or invited-respondent).
- `FormSubmissionUpdated` — submission edited.
- `WorkflowTransitioned` — workflow state change.
- `WorkflowActionExecuted` — workflow-action invocation outcome (`succeeded` / `failed` / `skipped_replay` / `skipped_pending`).
- `ShareTokensIssued` — issued via `IssueTokens` or `DispatchInvitationsByEmail`.
- `SurveyClosed` — `CloseSurvey` invoked.

Each event carries actor (`AuthenticatedUser` or `InvitedRespondent`), schema id, submission id, and a server-side timestamp.

## HTTP endpoints

Auto-injected by `FormsServerApp.run`:

- `POST /api/IFormApi/*` — every `IFormApi` method.
- `POST /api/public/forms/*` — every `IPublicFormApi` method (anonymous route — exempt from auth middleware).

The browser-facing `/r/{token}` route for `PublicEmbed` is owned by the consumer app's static host; the SDK does not register a Giraffe handler for it.

## Configuration knobs

- `ServerConfig.EntityStore = EnabledEntityStore` — required substrate.
- `ServerConfig.AuditLogMode = EnabledAuditLog` — required for audit emission.
- `ServerConfig.ShareTokenStore = EnabledShareTokenStore { Secret = ... }` — required for `Publishable` schemas.
- `ServerConfig.PublicBaseUrl = Some "https://my-app.example.com"` — required for `IssueTokens` URL composition.
- `ServerConfig.AnonymousRoutePrefixes` — `/api/public/forms/` is appended automatically by `FormsServerApp.run`.

## Conformance test packs

`ToolUp.Forms.Tests` ships:
- `IFormStoreContract` — schema CRUD + submission CRUD + indexed queries.
- `IWorkflowEngineContract` — transition logic, guard semantics, action firing, audit emission.
- `IActionLedgerContract` — ledger lifecycle invariants.
- `IShareTokenStoreContract` (in `ToolUp.Platform.Tests`) — token issue / validate / use-limit / revoke / scope-isolation / cross-resource-splice rejection.

External implementations bind to the same packs to validate against the conformance bar.

---

_Documentation as-of forge `32ef4aa`._
