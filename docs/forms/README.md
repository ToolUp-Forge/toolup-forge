# ToolUp.Forms

Schema-driven forms, validation, persistence, and a small state-machine workflow primitive for ToolUp Platform. Collapses CRUD-heavy intake / survey / approval flows into "register a `FormSchema` + a `WorkflowDefinition`."

Plus: **publishable surveys** — token-gated public-write surface where anonymous respondents submit forms without an account.

## When to use this companion

- **Intake forms** — new-customer onboarding, support requests, lead capture.
- **Surveys** — internal or external; multi-question with required / optional fields, choice fields, free text.
- **Approval workflows** — quote → accepted → scheduled → invoiced → paid; state machine with guards on transitions.
- **Wizard flows** — multi-step submissions with partial-save (`Draft` state).
- **Publishable surveys** — distributing a form via share-link tokens; anonymous respondents; aggregation dashboard.

## When NOT to use this companion

- **Free-form rich content** — Forms is structured input; for prose / Markdown, use file uploads + `ToolUp.KnowledgeBase`.
- **Highly bespoke UX** — Forms ships a generic `FormRenderer`; complex multi-column / multi-tab layouts need custom UI. Use the `IFormStore` + `IWorkflowEngine` interfaces directly with your own renderer.
- **Single-shot frictionless lead-capture** — for one-field email collection on a marketing page, hand-rolled HTML is lighter than instantiating a full `FormSchema`.

## What's in the box

Three packages:

| Package | What it is |
|---|---|
| `ToolUp.Forms.Core` | Shared types: `FormSchema` / `FieldKind` / `ValidationRule` / `Submission` / `FieldValue` / `FormError` / `WorkflowDefinition` / `IFormApi` ToolUp.Remoting contract + `IPublicFormApi` for publishable surveys + `IActionLedger` (Phase 21d). |
| `ToolUp.Forms.Server` | `IFormStore` + default `FormStore` (over `IEntityStore`), `IWorkflowEngine` + `WorkflowEngine`, `FormValidator`, `formApi` handler, `publicFormApi` handler (Phase 21b), `PublishableFormConfigValidator`, `InMemoryActionLedger` default + `IActionLedger` substrate (Phase 21d), `FormsCompose`. |
| `ToolUp.Forms.Client` | `FormRenderer` (Feliz schema-driven inputs), `WorkflowBadge` (state pill), `FormSubmissionsList` (table), `PublicEmbed` (`/r/{token}` standalone respondent UI), `SurveyDashboardView` + `SurveyListView` (admin), `FormsClient.proxy` (ToolUp.Remoting proxy). |

## Required substrate

- **`IEntityStore`** (`ToolUp.Platform.Server`) — typed persistence + indexed lookup. Forms persists every `FormSchema` and `Submission` as entities with declared indexes.
- **`IAuditLog`** (`ToolUp.Platform.Server`) — emits `FormSubmitted` / `FormSubmissionUpdated` / `WorkflowTransitioned` / `WorkflowActionExecuted` events.

Optional:
- **`INotificationChannel` / `INotificationSink`** (`ToolUp.Platform.Server`) — for transactional notifications from workflow actions (e.g. "email customer on Invoiced state").
- **`IShareTokenStore`** (`ToolUp.Platform.Server`) — required for publishable surveys. HMAC-SHA256-signed tokens, blob-backed claim store.
- **`IActionLedger`** (`ToolUp.Forms.Core`) — for distributed deployments. The compose step auto-defaults to `InMemoryActionLedger`; wire a durable ledger via `FormsServerApp.withActionLedger` when the deployment must survive a process restart between state-persist and action-completion.

## Quick start

Add the packages:

```xml
<PackageReference Include="ToolUp.Forms.Server" />
<PackageReference Include="ToolUp.Forms.Client" />
```

Wire the server composition root:

```fsharp
open ToolUp.Platform.Server
open ToolUp.Forms.FormSchema
open ToolUp.Forms.Workflow
open ToolUp.Forms.FormsCompose

let onboardingSchema =
    FormSchema.create "customer-onboarding" "Customer Onboarding" [
        { Key = "company_name"
          DisplayName = "Company name"
          Description = None
          Kind = TextField (Some 200)
          Required = true
          Validators = [ LengthRange (Some 2, Some 200) ] }
        { Key = "tier"
          DisplayName = "Tier"
          Description = None
          Kind = ChoiceField [ "Starter"; "Growth"; "Enterprise" ]
          Required = true
          Validators = [] }
    ]

let onboardingWorkflow: WorkflowDefinition = {
    Id = "onboarding"
    InitialState = "submitted"
    Transitions = [
        { From = "submitted"; Event = "review"; To = "reviewing"; Guard = None; Action = None }
        { From = "reviewing"; Event = "approve"; To = "approved"; Guard = None; Action = Some "send-welcome-email" }
        { From = "reviewing"; Event = "reject";  To = "rejected"; Guard = None; Action = None }
    ]
}

FormsServerApp.create ()
|> FormsServerApp.withConfig serverConfig
|> FormsServerApp.withAuth authProvider
|> FormsServerApp.addModules modules
|> FormsServerApp.withFormSchema onboardingSchema
|> FormsServerApp.withWorkflow onboardingWorkflow
|> FormsServerApp.run
```

Wire a client view (consumer-side):

```fsharp
open Feliz
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission

// `FormRenderer.render` is exposed by `ToolUp.Forms.Client`; signature
// shown here for clarity. Use directly in your module's `ClientView.fs`.
let onboardingView (schema: FormSchema) (dispatch: Map<string, FieldValue> -> unit) =
    FormRenderer.render
        {| Schema = schema
           InitialValues = Map.empty
           OnSubmit = dispatch |}
```

That's it. Validation runs server-side on submit; the workflow engine moves through transitions; the audit log records every state change.

## Phase 21b — publishable surveys

A `FormSchema` with `Visibility = Publishable` is a survey distributable to anonymous respondents via signed share-link tokens. Three distribution flows out of the box:

1. **Platform-dispatched email** — `IFormApi.DispatchInvitationsByEmail` ships invitations via your registered `INotificationSink` (Phase 6f).
2. **Creator's own MTA** — `IFormApi.IssueTokens` returns token URLs; the creator sends them via their own tool.
3. **Third-party survey provider with opaque handles** — `IssueTokens` with opaque `Handle` values; the platform never sees real emails / panel ids.

The respondent visits `/r/{token}`; the `PublicEmbed` Feliz component (no app shell) renders the form; submission goes through `IPublicFormApi.SubmitWithToken` (token-gated). See [concepts.md](concepts.md) for the wire-format + cross-cutting validation chain.

## Phase 21d — exactly-once workflow actions

Workflow actions (`Transition.Action = Some "send-welcome-email"`) used to be fire-and-forget — an exception inside an action was warn-logged and swallowed, and a process restart between state-persist and action-completion re-fired the action on the next apply attempt. The Phase 21d action ledger turns invocation into an exactly-once primitive keyed by `(SubmissionId, transitionId, actionName)`. Combined with a per-action `ActionFailurePolicy` (`FailSubmission` / `DeadLetter` / `LogOnly`), the engine surfaces failures on three independent observability surfaces (metric, audit row, dead-letter ledger entry) so silent loss is structurally impossible. See [concepts.md](concepts.md) and [extending.md](extending.md) for details.

## Concepts

See [concepts.md](concepts.md) for the schema / submission / workflow model, the validation chain, publishable surveys + share-tokens, the action ledger, and the entity-store layer.

## API reference

See [api-reference.md](api-reference.md) for the full public surface.

## Extending

See [extending.md](extending.md) for writing custom validators / guards / actions, registering an `IActionLedger`, custom submission analysers, and replacing the renderer.

---

_Documentation as-of forge `32ef4aa`._
