# Getting started with ToolUp.Forms

End-to-end walkthrough: define a schema, render the form, persist a submission, run a workflow.

## Prerequisites

- A working ToolUp Platform app with `IEntityStore` enabled (`ServerConfig.EntityStore = EnabledEntityStore`).
- `IAuditLog` enabled for event emission.

## 1. Add the packages

In your server project's `.fsproj`:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Forms.Server" />
</ItemGroup>
```

In your client project's `.fsproj`:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Forms.Client" />
</ItemGroup>
```

## 2. Declare a schema

In a module's `SharedTypes.fs`:

```fsharp
open ToolUp.Forms.FormSchema

let leadCaptureSchema: FormSchema =
    FormSchema.create "lead-capture-v1" "Lead Capture" [
        { Key = "name"
          DisplayName = "Your name"
          Description = Some "How should we address you?"
          Kind = TextField (Some 100)
          Required = true
          Validators = [ LengthRange (Some 2, Some 100) ] }

        { Key = "email"
          DisplayName = "Email"
          Description = None
          Kind = TextField (Some 254)
          Required = true
          Validators = [
              Regex (@"^[^@\s]+@[^@\s]+\.[^@\s]+$", Some "valid email address")
          ] }

        { Key = "company"
          DisplayName = "Company"
          Description = None
          Kind = TextField (Some 200)
          Required = true
          Validators = [ LengthRange (None, Some 200) ] }

        { Key = "interest"
          DisplayName = "What are you interested in?"
          Description = None
          Kind = ChoiceField [ "Demo"; "Pricing"; "Partnership"; "Other" ]
          Required = true
          Validators = [] }

        { Key = "notes"
          DisplayName = "Anything else?"
          Description = None
          Kind = TextField (Some 2000)
          Required = false
          Validators = [ LengthRange (None, Some 2000) ] }
    ]
```

`FieldKind` shipped variants:

```fsharp skip=fragment
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
| MatrixField of rows: int * cols: int * cell: FieldKind * labels: MatrixFieldLabels option
```

`MatrixField` is a fixed-shape 2D grid of `cell`-typed inputs. Build one with `Matrix.create rows cols cell labels`, which enforces the ≥ 1 bounds and rejects a nested matrix cell; its values flatten into the ordinary `Map<string, FieldValue>` under `Matrix.cellKey`-shaped sub-keys, so nothing about persistence changes.

`ValidationRule` shipped variants:

```fsharp skip=fragment
| Regex of pattern: string * description: string option
| NumberRange of min: float option * max: float option
| LengthRange of min: int option * max: int option
| Custom of name: string
```

There is no `Required` / `Email` / `MinLength` / `MaxLength` / `MinValue` / `MaxValue` rule — `Required` is a separate boolean on `FieldSchema`, "email" is a `Regex` rule with an email pattern, and `MinLength` / `MaxLength` / `MinValue` / `MaxValue` collapse into the bounded `LengthRange` / `NumberRange` rules with `None` for the open-ended side.

## 3. Declare a workflow (optional)

If submissions need workflow state, define a `WorkflowDefinition`:

```fsharp
open ToolUp.Forms.Workflow

let leadWorkflow : WorkflowDefinition = {
    Id = "lead-workflow"
    InitialState = "new"
    Transitions = [
        { From = "new"
          Event = "contact"
          To = "contacted"
          Guard = None
          Action = None }

        { From = "contacted"
          Event = "qualify"
          To = "qualified"
          Guard = None
          Action = None }

        { From = "contacted"
          Event = "mark-lost"
          To = "lost"
          Guard = None
          Action = None }

        { From = "qualified"
          Event = "send-proposal"
          To = "proposal-sent"
          Guard = Some "has-proposal-attached"
          Action = Some "send-proposal-email" }

        { From = "proposal-sent"
          Event = "mark-won"
          To = "won"
          Guard = None
          Action = Some "send-welcome-email" }

        { From = "proposal-sent"
          Event = "mark-lost"
          To = "lost"
          Guard = None
          Action = None }
    ]
}
```

`WorkflowState`, `WorkflowId`, and `TransitionEvent` are type aliases for `string` — workflow authors pick natural names. `Guard` and `Action` are `string option` and resolve server-side at apply time against the registries populated by `withGuard` / `withAction`. Closures don't cross Fable serialisation; name-keyed dispatch is the workaround.

## 4. Wire `FormsServerApp`

In the server composition root:

```fsharp
open ToolUp.Platform.Server
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow
open ToolUp.Forms.FormsCompose

let serverApp =
    FormsServerApp.create ()
    |> FormsServerApp.withConfig {
        ServerConfig.defaults with
            EntityStore = EnabledEntityStore
            AuditLog = EnabledAuditLog
    }
    |> FormsServerApp.withAuth authProvider
    |> FormsServerApp.addModules modules
    |> FormsServerApp.withFormSchema MyModule.SharedTypes.leadCaptureSchema
    |> FormsServerApp.withWorkflow leadWorkflow
    |> FormsServerApp.withGuard
        "has-proposal-attached"
        (fun ctx -> async {
            if ctx.Submission.Values.ContainsKey "proposal_file_id" then
                return Ok()
            else
                return Error "no proposal file attached"
        })
    |> FormsServerApp.withAction
        "send-proposal-email"
        (fun ctx -> async {
            let email =
                match Map.tryFind "email" ctx.Submission.Values with
                | Some(TextValue s) -> s
                | _ -> ""
            // ... ship the email via your notification sink ...
            return ()
        })
    |> FormsServerApp.withActionPolicy "send-proposal-email" DeadLetter

let exitCode = FormsServerApp.run serverApp
```

- **Guards** have signature `WorkflowContext -> Async<Result<unit, string>>`, where the context bundles `Submission`, `AccessContext` and a per-invocation `IServiceProvider` — so a guard resolves what it needs from DI rather than capturing it at compose time. `Ok ()` allows the transition; `Error reason` short-circuits it as `FormError.TransitionDenied reason`. A guard that throws surfaces as `GuardEvaluationFailed (guardName, reason)` so callers can retry transient faults.
- **Actions** have signature `WorkflowContext -> Async<unit>` — the same context guards receive, so an action resolves its collaborators from DI per invocation too. They run after persistence, wrapped in the `IActionLedger` lifecycle so each `(SubmissionId, transitionId, actionName)` triple fires at most once.
- **`withActionPolicy`** sets the per-action `ActionFailurePolicy` (`FailSubmission` / `DeadLetter` / `LogOnly`). Without an explicit policy the engine defaults to `DeadLetter`.

## 5. Render the form on the client

In a module's `ClientView.fs`:

```fsharp
open Feliz
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission

let formView
    (schema: FormSchema)
    (onSubmit: Map<string, FieldValue> -> unit)
    =
    Html.div [
        prop.className "max-w-2xl mx-auto p-6"
        prop.children [
            Html.h2 [ prop.text schema.DisplayName ]

            FormRenderer.FormRenderer schema onSubmit
        ]
    ]
```

`FormRenderer.FormRenderer` is a Feliz component exposed by `ToolUp.Forms.Client`. It uses `React.useState` for in-flight values; only dispatches upward on submit. The user types freely without per-keystroke Elmish dispatch.

Client-side validation mirrors the server's; the server's is authoritative.

## 6. Submit + persist

The `OnSubmit` callback typically dispatches an Elmish `Msg`. The handler calls the API:

```fsharp
open ToolUp.Forms.FormApi
open ToolUp.Forms.FormSubmission

// one arm of the module's `update`
match msg with
| SubmitLead values ->
    let request: SubmitRequest = {
        FormId = MyModule.SharedTypes.leadCaptureSchema.Id
        Values = values
        WorkflowId = Some leadWorkflow.Id
    }

    let cmd =
        Cmd.OfAsync.either
            (fun req -> FormsClient.proxy.Submit req)
            request
            (function
             | Ok submission -> SubmissionSucceeded submission
             | Error err -> SubmissionFailed err)
            (fun ex -> SubmissionFailed (FormError.StorageFailed ex.Message))

    model, cmd
```

`IFormApi.Submit`:
1. Validates server-side via the schema's `ValidationRule`s + any custom validators.
2. Persists the `Submission` to `IEntityStore` with declared indexes (`FormId` / `Author` / `State` / `FormId+State`).
3. Emits `FormSubmitted` event under `_platform.audit`.
4. If `WorkflowId = Some _`, sets `Submission.State = Custom workflow.InitialState`; otherwise sets it to `Submitted`.
5. Returns the persisted `Submission` with server-allocated `Id` and `SubmittedAt`.

## 7. List + manage submissions

```fsharp
let submissionsView (model: Model) dispatch =
    FormSubmissionsList.FormSubmissionsList model.Submissions
```

The list view shows one row per submission, formatted per schema. Workflow-aware rows show a `WorkflowBadge` (current state) + per-transition action buttons (filtered by what's permitted from the current state).

Transition handler:

```fsharp
// another arm of the same `update`
match msg with
| TransitionWorkflow(submissionId, eventName) ->
    let request: ApplyTransitionRequest = {
        SubmissionId = submissionId
        Event = eventName
    }

    let cmd =
        Cmd.OfAsync.perform
            (fun req -> FormsClient.proxy.ApplyTransition req)
            request
            (function
             | Ok submission -> TransitionSucceeded submission
             | Error err -> TransitionFailed err)

    model, cmd
```

The server runs the guard (if any), persists the new state, fires the action under the ledger, emits a `WorkflowTransitioned` audit event. A guard returning `Error reason` surfaces as `FormError.TransitionDenied reason`; a guard exception surfaces as `GuardEvaluationFailed`.

## 8. Optional — publishable surveys

For anonymous-respondent surveys (e.g. a customer-satisfaction NPS survey distributed via email):

```fsharp
open ToolUp.Forms.FormSchema

let npsSchema =
    { FormSchema.create "nps-q3-2026" "Q3 NPS" [
        { Key = "score"
          DisplayName = "On a scale of 0–10, how likely are you to recommend us?"
          Description = None
          Kind = NumberField (Some 0.0, Some 10.0)
          Required = true
          Validators = [ NumberRange (Some 0.0, Some 10.0) ] }

        { Key = "why"
          DisplayName = "Briefly — why?"
          Description = None
          Kind = TextField (Some 1000)
          Required = false
          Validators = [ LengthRange (None, Some 1000) ] }
      ]
      with
        Visibility = Publishable }
```

Wire `IShareTokenStore`:

```fsharp
open ToolUp.Platform.Server
open ToolUp.Forms.FormsCompose

let serverApp =
    FormsServerApp.create ()
    |> FormsServerApp.withConfig {
        ServerConfig.defaults with
            EntityStore = EnabledEntityStore
            ShareTokenStore = EnabledShareTokenStore
            PublicBaseUrl = Some "https://my-app.example.com"
    }
    |> FormsServerApp.withFormSchema npsSchema

let exitCode = FormsServerApp.run serverApp
```

Issue tokens:

```fsharp
open System
open ToolUp.Forms.FormApi

async {
    let! result =
        formsApi.IssueTokens {
            SchemaId = npsSchema.Id
            Recipients = [ for i in 1..100 -> { Handle = $"panel-{i}"; DisplayName = None } ]
            ExpiresAt = Some(DateTimeOffset.UtcNow.AddDays 30.0)
            UseLimit = Some(Some 1)
        }

    // result : Result<IssuedToken list, FormError>
    // Each IssuedToken has .Url = "{PublicBaseUrl}/r/{token}".
    return result
}
```

Distribute the URLs by email / SMS / panel provider. Anonymous respondents visit `/r/{token}` and submit the form via the `PublicEmbed` Feliz component (no app shell, no auth required).

Server-side `IPublicFormApi.SubmitWithToken` validates the token (signed, `ResourceKind = "forms.publishable"`, schema is `Publishable`, not expired, use-limit not exhausted), validates the form values, persists with `Author = InvitedRespondent (tokenId, attributedHandle)`, returns `Ok submission`.

See [concepts.md](concepts.md) for the full publishable-surveys flow.

## Next steps

- [concepts.md](concepts.md) — schema model, validation chain, workflow engine, action ledger, publishable surveys, entity-store layer.
- [api-reference.md](api-reference.md) — full public surface.
- [extending.md](extending.md) — custom validators / guards / actions, registering an `IActionLedger`, custom analysers, custom renderer.

---

_Documentation as-of forge `32ef4aa`._
