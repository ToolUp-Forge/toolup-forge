# ToolUp.Forms

Schema-driven forms, validation, persistence, and a small state-machine workflow primitive for ToolUp.Platform. Companion package — apps that don't import `ToolUp.Forms.Server.props` / `ToolUp.Forms.Client.props` pay zero runtime cost.

**Phase 21 status (2026-05-05): shipped.** 17/17 tests pass; six-rule portability audit clean; strip-imports test verified in both directions.

**Phase 21b status (2026-05-06): shipped.** Adds *Publishable surveys* — a token-gated public-write surface so anonymous respondents can submit forms without an account. Seven slices total (substrate → API → embed entry → admin dashboard → multi-survey overview); see "Publishable surveys" below.

## What this is

CRUD-heavy domains (intake forms, surveys, multi-step wizards, approval chains) duplicate the same render-and-validate-and-persist loop in every module. `ToolUp.Forms` collapses that duplication into:

- **`FormSchema`** — typed declaration of fields, validators, choices. Persists as a versioned Phase 19 entity; per-tenant overrides allowed.
- **`Submission`** — entity-store-backed persistence of completed forms. Indexed by FormId / SubmittedBy / State + compound `(FormId, State)`.
- **`WorkflowDefinition`** — small state machine over named transitions, with name-keyed guards (predicates that can veto a transition) and actions (side-effects after persistence).
- **Feliz `FormRenderer`** — schema-driven typed inputs (no per-field handlers); React.useState for in-flight values; only dispatches upward on submit.

Modules become "register a `FormSchema` + a `WorkflowDefinition`" instead of "write a render-and-validate-and-persist loop."

Shipped surface (commit-stable across Phase 21):

| Concern | File | Module |
|---|---|---|
| Schema types | [`Shared/FormSchema.fs`](Shared/FormSchema.fs) | `FormSchemaId`, `FieldKind`, `ValidationRule`, `FieldSchema`, `FormSchema` |
| Submission types | [`Shared/FormSubmission.fs`](Shared/FormSubmission.fs) | `SubmissionId`, `FieldValue`, `SubmissionState`, `Submission`, `FieldError`, `FormError` |
| Workflow types | [`Shared/Workflow.fs`](Shared/Workflow.fs) | `WorkflowState`, `WorkflowId`, `Transition`, `WorkflowDefinition` |
| Wire contract | [`Shared/FormApi.fs`](Shared/FormApi.fs) | `IFormApi` (ToolUp.Remoting) |
| Server interface | [`Server/IFormStore.fs`](Server/IFormStore.fs) | `IFormStore` — six-rule portable |
| Server interface | [`Server/IWorkflowEngine.fs`](Server/IWorkflowEngine.fs) | `IWorkflowEngine`, `WorkflowGuard`, `WorkflowAction` — six-rule portable |
| Entity registrations | [`Server/EntityRegistrations.fs`](Server/EntityRegistrations.fs) | `formSchemaRegistration`, `submissionRegistration` |
| Default impls | [`Server/FormStore.fs`](Server/FormStore.fs), [`Server/WorkflowEngine.fs`](Server/WorkflowEngine.fs) | over `IEntityStore` (Phase 19) + `IAuditLog` (Phase 9) |
| Validation | [`Server/FormValidator.fs`](Server/FormValidator.fs) | `validate`, `CustomValidator`, `CustomValidatorRegistry` |
| API handler | [`Server/FormApiHandler.fs`](Server/FormApiHandler.fs) | `formApi` ToolUp.Remoting handler with Owner/Admin gating |
| Compose pipeline | [`Server/FormsCompose.fs`](Server/FormsCompose.fs) | `FormsServerApp` record + `run`, `DefaultedFormStore` decorator |
| Client renderer | [`Client/FormRenderer.fs`](Client/FormRenderer.fs) | `FormRenderer` Feliz component |
| Client list | [`Client/FormSubmissionsList.fs`](Client/FormSubmissionsList.fs) | `FormSubmissionsList` table |
| Client badge | [`Client/WorkflowBadge.fs`](Client/WorkflowBadge.fs) | `WorkflowBadge` pill |
| Client proxy | [`Client/FormsClient.fs`](Client/FormsClient.fs) | `proxy: IFormApi` |
| **Phase 21b** — public-form wire | [`Shared/PublicFormApi.fs`](Shared/PublicFormApi.fs) | `IPublicFormApi` (token-gated) |
| **Phase 21b** — public-form handler | [`Server/PublicFormApiHandler.fs`](Server/PublicFormApiHandler.fs) | `publicFormApi` over `IShareTokenStore` |
| **Phase 21b** — preflight validator | [`Server/PublishableFormConfigValidator.fs`](Server/PublishableFormConfigValidator.fs) | warns on Publishable-without-PublicBaseUrl |
| **Phase 21b** — aggregation | [`Shared/AggregationTypes.fs`](Shared/AggregationTypes.fs), [`Server/Aggregations.fs`](Server/Aggregations.fs) | `AggregationSummary`, per-kind helpers |
| **Phase 21b** — analyser stub | [`Server/IFormSubmissionAnalyser.fs`](Server/IFormSubmissionAnalyser.fs) | reserved extension point — no default impl |
| **Phase 21b** — embed entry | [`Client/PublicFormsClient.fs`](Client/PublicFormsClient.fs), [`Client/PublicEmbed.fs`](Client/PublicEmbed.fs) | standalone `/r/{token}` Feliz component |
| **Phase 21b** — admin views | [`Client/SurveyDashboardView.fs`](Client/SurveyDashboardView.fs), [`Client/SurveyListView.fs`](Client/SurveyListView.fs) | per-survey + multi-survey stateless components |

## Why a companion, not core SDK

Forms are a **domain capability**, not platform substrate. Nothing else in the SDK depends on `IFormStore`. Analytics-only deployments never need forms — charging them the binary weight is wrong-by-default. Same rationale as `ToolUp.AI` / `ToolUp.RAG` / `ToolUp.KnowledgeBase` / `ToolUp.Scheduling` not living in `ToolUp.Platform`.

The companion is a *consumer* of substrate (`IEntityStore` Phase 19, `IAuditLog` Phase 9, optionally `INotificationSink` Phase 6f for transition notifications), not substrate itself.

## How to enable

In your server `.fsproj`:

```xml
<Import Project="..\ToolUp.Forms\ToolUp.Forms.Server.props" />
<!-- ...elsewhere... -->
<ProjectReference Include="..\ToolUp.Forms\ToolUp.Forms.fsproj" />
```

In your client `.fsproj`:

```xml
<Import Project="..\ToolUp.Forms\ToolUp.Forms.Client.props" />
```

In your server's composition root:

```fsharp skip=fragment
open ToolUp.Forms.FormsCompose

let bugReportSchema = FormSchema.create "bug-report" "Bug report" [
    { Key = "title"; DisplayName = "Title"; Description = None;
      Kind = TextField (Some 200); Required = true; Validators = [] }
    { Key = "severity"; DisplayName = "Severity"; Description = None;
      Kind = ChoiceField [ "Minor"; "Major"; "Critical" ]; Required = true; Validators = [] }
]

let triageWorkflow = {
    Id = "bug-triage"
    InitialState = "new"
    Transitions = [
        { From = "new"; Event = "triage"; To = "triaged"; Guard = None; Action = None }
        { From = "triaged"; Event = "close"; To = "closed";
          Guard = Some "severity-major-or-critical"; Action = None }
    ]
}

let severityGuard : WorkflowGuard = fun (submission, _ctx) -> async {
    match Map.tryFind "severity" submission.Values with
    | Some (ChoiceValue v) when v = "Major" || v = "Critical" -> return Ok ()
    | _ -> return Error "Only Major or Critical bugs can be closed."
}

let config = { ServerConfig.defaults with
                 Port = 5000; Surfaces = Surfaces.team
                 EntityStore = EnabledEntityStore }   // REQUIRED — Forms rides on Phase 19

[<EntryPoint>]
let main _ =
    FormsServerApp.create ()
    |> FormsServerApp.withConfig config
    |> FormsServerApp.withAuth (StaticJwtAuthProvider(...))
    |> FormsServerApp.withFormSchema bugReportSchema
    |> FormsServerApp.withWorkflow triageWorkflow
    |> FormsServerApp.withGuard "severity-major-or-critical" severityGuard
    |> FormsServerApp.run
```

The Forms API is mounted at `/api/IFormApi/*` once `FormsServerApp.run` boots. Client code calls it via `FormsClient.proxy`.

## How to define a custom validator

Custom predicates ride a server-side registry keyed by name (closures don't cross Fable serialization, so the Shared layer carries names only).

```fsharp skip=fragment
let isStaffEmail : CustomValidator = fun raw ->
    if raw.EndsWith "@example.com" then Ok ()
    else Error "Email must be on the example.com domain."

// In the compose root:
|> FormsServerApp.withCustomValidator "staff-email" isStaffEmail

// In the schema:
{ Key = "email"; DisplayName = "Work email"; Description = None;
  Kind = TextField None; Required = true;
  Validators = [ Custom "staff-email" ] }
```

## How to define a workflow guard / action

Same name-keyed pattern as custom validators. Guards veto transitions; actions run after persistence (best-effort, failures logged not rolled back).

```fsharp skip=fragment
let notifyApprover : WorkflowAction = fun (submission, _ctx) -> async {
    // ...send email / SMS / push notification...
    do! someTransactionalSink.Send (...)
}

|> FormsServerApp.withAction "notify-approver" notifyApprover
```

## Publishable surveys (Phase 21b)

A `FormSchema` with `Visibility = Publishable` is a *survey* — distributable to anonymous respondents via signed share-link tokens. The substrate handles invitation issuance, embed-page rendering, response aggregation, and the multi-survey admin loop. Apps that don't issue tokens leave `Visibility = Internal` (the default) and pay nothing for the public-form surface.

**End-to-end flow:**

1. **Author the schema** — flip `Visibility = Publishable` on a `FormSchema` and `SaveSchema`. Per-tenant configurable so a deployment can carry a global default schema (`FormsServerApp.withFormSchema`) and let teams override it.

2. **Issue invitations** — `IFormApi.IssueTokens` (Owner/Admin gated) takes a list of opaque recipient handles and returns `(handle, tokenId, url, expiresAt)` rows. Three real-world distribution flows:
   - **Platform dispatches email** — `IFormApi.DispatchInvitationsByEmail` (slice 6, optional). Requires a transactional sink (`ServerApp.withTransactionalSink`); writes ephemeral `respondent:{tokenId}` address-book entries so PII never crosses the notification wire.
   - **Creator dispatches via own MTA** — call `IssueTokens` and ship the returned URLs through your own mailing system. Platform sends nothing.
   - **Third-party survey provider with no panel-id sharing** — hash panel ids client-side, call `IssueTokens` with the hashes as opaque handles, hand the resulting CSV to the third-party provider. Platform never sees real emails or panel ids.

3. **Respondents submit** — embed URL is `{PublicBaseUrl}/r/{token}`. The host application's `Client.fs` checks `ToolUp.Forms.PublicEmbed.isEmbedUrl` and renders the standalone `PublicEmbed` component instead of the authenticated app shell. No sidebar, no auth UI; just a branded header (`ClientConfig.AppName`) and the form.

4. **Creator monitors progress** — `IFormApi.GetAggregations schemaId` returns response counts, response rate, per-question roll-ups (mean/median/stddev for numeric, vote counts for choice, sample for text), and the recipient progress table. `SurveyDashboardView` is the matching stateless Feliz component.

5. **Multi-survey admin** — `IFormApi.ListSchemasOverview` returns every survey in scope with status (`Draft` / `Active` / `Closed`) and metric summaries in one call. `SurveyListView` is the matching stateless Feliz component. `IFormApi.CloseSurvey` closes a survey by flipping `Visibility` to `Internal`, bulk-revoking outstanding tokens, or both.

**Required substrate:**
- `ServerConfig.ShareTokenStore = EnabledShareTokenStore` — the SDK's HMAC-signed token primitive (Phase 21b slice 1)
- `ServerConfig.PublicBaseUrl = Some "https://your-host.example.com"` — for `IssueTokens` URL composition
- `ServerConfig.AnonymousRoutePrefixes` includes `/api/public/forms/` — `FormsCompose` registers this automatically
- Optional: `ServerApp.withTransactionalSink (...)` if `DispatchInvitationsByEmail` is wanted

The `PublishableFormConfigValidator` runs at startup and emits a `Warning` if `Publishable` schemas are registered but `PublicBaseUrl` is unset or `ShareTokenStore` is disabled.

**Security posture:** every token is HMAC-SHA256-signed against a key auto-resolved from `ISecretStore` under `_platform/share_token_signing_key`. The persisted `ShareTokenClaim` is the source of truth; a valid signature alone isn't sufficient (revocation is server-side). `PublicFormApiHandler` re-checks `Visibility = Publishable` on every request, so flipping a schema back to `Internal` instantly invalidates every outstanding token without per-token bookkeeping. Per-token rate limiting partitions on `sha256(token)[..16]` so one respondent can't saturate the public endpoint.

The `IFormSubmissionAnalyser` extension point is reserved for sentiment / NLP companions but the SDK ships no default implementation — a planned analyser companion + persistent results is a tracked follow-up.

## What's not in v1

See `TECHNICAL_GUIDE.md` "Departures from the design spec" for the full list. Headlines:

- Drag-drop form designer UI (Phase 26 follow-up)
- Conditional field visibility ("show field B only if field A == X")
- Computed fields (`field C = field A + field B`)
- Server-side rendering / static-export (`HTML emit`)
- Multi-step wizards (model as separate forms with workflow transitions between them)
- AI-backed sentiment / NLP analyser companion (Phase 26 — extension point ships in Phase 21b, no default impl)
- Persistent analyser outputs (Phase 26 — currently computed on-demand by `GetAggregations`)

## See also

- [`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md) — design decisions, six-rule portability audit verdict, strip-imports test record, departures from spec
- [`src/ToolUp.Forms.Tests/`](../ToolUp.Forms.Tests/) — `IFormStoreContract` (9), `IWorkflowEngineContract` (7), worked-example test (1)
