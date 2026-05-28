# ToolUp.Forms — Technical Guide

Internals, design decisions, and the deferred set for the Forms Companion. Read [`README.md`](README.md) first for the overview.

## Six-rule portability audit (GP-12 / Phase 9c)

Both Forms server interfaces — `IFormStore` and `IWorkflowEngine`
— are audited against the six portability rules described in
[`CLAUDE.md`](../../CLAUDE.md) ("Six portability rules for
distributed implementations"). Verdict for each interface is
captured verbatim in its docstring; this section consolidates the
audit for cross-reference.

| Rule | `IFormStore` | `IWorkflowEngine` |
|---|---|---|
| **1. Identity by value** | ✓ `FormSchemaId` / `SubmissionId` are `string`; `scopeId: string`; no live handles. | ✓ `WorkflowId` / `SubmissionId` / `WorkflowState` / `TransitionEvent` are all `string`. |
| **2. Async at every boundary** | ✓ Every method returns `Async<_>`. No fire-and-forget shapes. | ✓ Every method returns `Async<_>`. The compose-time-immutable workflow registry is constructor parameter, not interface method. |
| **3. Retry / supervision as data** | ✓ Failure flows through `FormError` DU. No callback parameters; no supervision-strategy objects. Retries are caller-side. | ✓ Failure flows through `FormError` DU (`InvalidTransition` / `TransitionDenied` / `WorkflowNotFound`). |
| **4. Stateless between calls** | ✓ Every call derives its result from parameters + `IEntityStore`. A grain that deactivates between two calls behaves identically. | ✓ `Apply` reads current state from `IFormStore`, computes the transition, persists, then runs the action — no in-memory state assumptions across calls. Workflow registrations are compose-time constants, not Rule-4 in-memory state. |
| **5. No cross-shard ordering** | ✓ Submission versioning is monotonically increasing within `(SubmissionId, scopeId)`; across submissions or scopes no ordering is promised. | ✓ Transitions on different submissions are independent. Per-submission ordering is enforced by `IFormStore.SaveSubmission` versioning. |
| **6. Precision at lower bound** | N/A (no time semantics in the interface). | N/A (no scheduling primitives). |

A future distributed companion (Akka.NET / Orleans grain layer)
implementing either interface binds the same `IFormStoreContract`
(9 tests) or `IWorkflowEngineContract` (7 tests) test pack against
its own factory, mirroring the `IBookingSchedulerContract` pattern
from `src/ToolUp.Scheduling.Tests/`.

**Companion-internal types reviewed for portability:**
- `WorkflowGuard = Submission * AccessContext -> Async<Result<unit, string>>` and `WorkflowAction = Submission * AccessContext -> Async<unit>` — closures, not portable across process boundaries on their own. Resolved by name from a server-side registry; the registry's keys (strings) cross boundaries, the closures don't. Same pattern as `ValidationRule.Custom` in the Shared layer.
- `CustomValidatorRegistry = Map<string, string -> Result<unit, string>>` — closures, same name-keyed lookup discipline as guards / actions.

## Opt-in wiring

The companion ships built but inert: a consumer with neither
`ToolUp.Forms.Server.props` nor `ToolUp.Forms.Client.props` imported
gets no Forms surface in their build output. Wiring in is two lines
per tier — `<Import Project="...ToolUp.Forms.Server.props" />` (or
the Client analogue) plus a `<ProjectReference>` to the respective
`.fsproj`. Removing those two lines yields a clean build with the
Forms surface absent from the Fable output (no `ToolUp.Forms/*.js`
emitted) and no `FormsCompose` surface on the server. The pattern
mirrors Scheduling.

**Note on `ShareTokenTypes`** — this shared type ships in
`ToolUp.Platform.Core/Shared/ShareTokenTypes.fs` regardless of Forms
wiring. That's by design — the share-token primitive is structured
as SDK substrate so future shareable-dashboard / magic-login
consumers reuse the same wire shape without renegotiating it. The
bundle cost is a small types-only file (~22 lines of compiled JS);
the implementation (`BlobShareTokenStore`) is server-only and never
reaches the Fable bundle.

## Publishable surveys

The companion ships an opt-in *survey* surface on top of the
`FormSchema` substrate. A survey is just a `FormSchema` with
`Visibility = Publishable` plus the share-token machinery to
distribute it to anonymous respondents. The architecture is
deliberately additive — `Internal` schemas (the default) get
nothing; only `Publishable` schemas activate the public-write
surface.

### Architecture

```
                     ┌──────────────────────────────────────────┐
                     │ IFormApi (authenticated, /api/IFormApi/) │
                     │  • SaveSchema (schema.Visibility ↑)      │
                     │  • IssueTokens / DispatchByEmail         │
                     │  • CloseSurvey / ListSchemasOverview     │
                     │  • GetAggregations                       │
                     └──────────────────────────────────────────┘
                                  │ writes
                                  ▼
       ┌──────────────────┐    ┌──────────────────────┐    ┌────────────────┐
       │ IFormStore       │    │ IShareTokenStore     │    │ IAuditLog      │
       │  (FormSchema +   │    │  (token issue/       │    │ (FormSubmitted,│
       │   Submission via │    │   validate/revoke)   │    │  ShareToken*)  │
       │   IEntityStore)  │    │                      │    │                │
       └──────────────────┘    └──────────────────────┘    └────────────────┘
                ▲                       ▲
                │ reads                 │ validates
                │                       │
       ┌──────────────────────────────────────┐
       │ IPublicFormApi (anonymous,           │
       │  /api/public/forms/)                 │
       │  • GetSchemaByToken                  │
       │  • SubmitWithToken                   │
       └──────────────────────────────────────┘
                ▲
                │ /r/{token} → PublicEmbed → proxy
                │
       ┌──────────────────┐
       │ Browser (no auth │
       │  cookie / no JWT │
       │  — the token IS  │
       │  the auth)       │
       └──────────────────┘
```

### Token wire format + validation chain

The wire format is `{tokenId}.{base64url(payloadJson)}.{base64url(hmac)}` (see `src/ToolUp.Platform.Server/Server/ShareTokenStore.fs`). Validation runs in this order on every public-handler call:

1. **Signature check** — HMAC-SHA256 against the platform signing key (auto-resolved from `ISecretStore` under `_platform/share_token_signing_key`). Tampered tokens fail here without a storage hit.
2. **Persisted claim lookup** — if signature passes, fetch the row at `_platform/share-tokens/{scopeId}/{tokenId}.json`. The persisted record is the source of truth — a valid signature alone isn't sufficient.
3. **Cross-check** — embedded `(ScopeId, ResourceKind, ResourceId)` from the signed payload must match the persisted claim. A spliced wire string with a different scope or resource fails as `InvalidSignature`.
4. **Lifecycle gates** — `ExpiresAt > now`, not `Revoked`, `UsedCount < UseLimit`. Each fails with a distinct `ShareTokenError` so server logs distinguish causes; consumer-facing errors collapse to a single "this link is no longer valid" message (see `PublicEmbed.describeError`).
5. **Resource-kind binding** — `claim.ResourceKind` must equal `PublicFormApi.ResourceKind = "forms.publishable"`. A token issued for a different resource (a future shareable dashboard, magic-login link, etc.) cannot be replayed against the public form surface.
6. **Schema-visibility re-check** — `PublicFormApiHandler.resolveTokenAndSchema` reads the *latest* schema version and rejects when `Visibility ≠ Publishable`. This is the load-bearing check for `CloseSurvey HideSchema`: flipping `Visibility = Internal` instantly invalidates every outstanding token without per-token bookkeeping.

### Submission identity model (`SubmissionAuthor`)

To accommodate both authenticated and token-invited respondents, `Submission.SubmittedBy` is a typed DU:

```fsharp
type SubmissionAuthor =
    | AuthenticatedUser of userId: string
    | InvitedRespondent of tokenId: string * attributedHandle: string option
```

Every internal `IFormApi.Submit` call writes `AuthenticatedUser userId`; every public `IPublicFormApi.SubmitWithToken` call writes `InvitedRespondent (tokenId, claim.AttributedHandle)`. The entity-store `Author` index uses the prefix-tagged value `"u:{userId}"` or `"t:{tokenId}"` so a single `Eq("Author", ...)` query targets either flavour. Helper functions (`SubmissionAuthor.indexValueForUser`, `SubmissionAuthor.indexValueForToken`) build the correctly-tagged value at the query callsite.

The audit trail of an invited-respondent submission carries `UserId = "respondent:{tokenId}"` — never the raw email or handle. PII stays in the address book (`_platform/contacts/{scopeId}/respondent_{tokenId}.json`) and never crosses the audit / event-store layers.

### Distribution flexibility (`IssueTokens` vs `DispatchInvitationsByEmail`)

The substrate supports three real-world distribution flows out of the box. The decision to split issuance from dispatch is intentional — many real deployments can't or won't let the platform send their email.

| Flow | API method | What the platform sends |
|---|---|---|
| Platform-driven email | `DispatchInvitationsByEmail` (slice 6, optional) | `TransactionalEmail` notifications via `INotificationChannel`; sink resolves `respondent:{tokenId}` → email at delivery time |
| Creator's own MTA | `IssueTokens` | Nothing — caller gets `(handle, url)` rows and ships via own system |
| Third-party survey provider | `IssueTokens` (with hashed handles) | Nothing — caller hashes panel ids client-side, hands the CSV to the third-party provider, platform never sees real identifiers |

The email path uses ephemeral synthesised `respondent:{tokenId}` userIds and the existing `BlobBackedNotificationAddressBook` write helper. The no-PII-on-wire principle is preserved — envelopes carry only the synthesised id; the email lives in the address book and is resolved at sink time.

### Multi-survey UX

`IFormApi.ListSchemasOverview` is the load-bearing primitive. Each schema in scope rolls up to a `SurveyOverviewRow` carrying the schema, submission count, invited count, response rate, and a derived `SurveyStatus`:

| Status | Visibility | Has tokens? | Meaning |
|---|---|---|---|
| `Draft` | `Internal` | No | Authored but not distributed |
| `Active` | `Publishable` | Any | Currently accepting submissions |
| `Closed` | `Internal` | Yes | Was distributed; now disabled |

Operators close a survey via `IFormApi.CloseSurvey` with one of three modes:
- `HideSchema` — flips `Visibility = Internal` (cheaper; reversible by flipping back to `Publishable`)
- `RevokeAllTokens` — bulk-revokes every outstanding token, leaves visibility unchanged
- `HideSchemaAndRevoke` — both (the "I'm fully done" default)

The SDK ships only the primitives; navigation between surveys (sidebar entries, drawers, route changes) is app-level — `SurveyListView` and `SurveyDashboardView` are stateless components that hosts wire into their own admin module per Guiding Principle 9 (SDK never names a domain module).

### Compose-time wiring summary

For the public-survey surface, `FormsCompose.run` performs three
additional setup steps beyond the baseline schema-storage wiring:

1. Registers `/api/public/forms/` as an anonymous route via `ServerApp.withAnonymousRoute` so `AuthEnforcementMiddleware` lets unauthenticated submissions through
2. Mounts `PublicFormApiHandler` as a Fable.Remoting handler with the `PublicFormApi.routeBuilder` (separate from the authenticated handler's default routing)
3. Auto-registers `PublishableFormConfigValidator` so deployments hear about misconfigured visibility / `PublicBaseUrl` at startup, not at first respondent click

The handler factory resolves `IShareTokenStore`, `INotificationChannel`, and `IBlobStorage` per-request (all option-typed — `None` causes the relevant API methods to return clear errors rather than null-ref). `ServerConfig.PublicBaseUrl` is captured outside the per-request lambda so URL composition doesn't pay a config-lookup per call.

## Departures from the textbook validator/workflow shape

A few shapes ship differently from the textbook "predicates as closures" pattern, for Fable-compat or YAGNI reasons:

- **`ValidationRule.Custom`** is `string` (registered name) not a
  closure `string -> Result<unit, string>`. Closures don't cross
  Fable's serialization; predicates live in a server-side
  `CustomValidatorRegistry` resolved at validation time.
- **`Transition.Guard` / `Transition.Action`** are `string` (registered
  names) not closures. Same Fable reason. Engine constructor takes
  the resolved `Map<name, predicate>` from the compose root.
- **`IWorkflowEngine.RegisterWorkflow`** is NOT on the interface.
  Workflow definitions are constructor parameters on
  `WorkflowEngine` (immutable for process lifetime). Sync
  registration would break Phase 9c rule 2 (async at every
  boundary); making it async would imply persistence which it
  doesn't do.
- **Reminder fusion / transactional notifications on workflow
  transitions** — opt-in via `withTransactionalSink`; concrete
  dispatch deferred. Mirrors Scheduling's `withReminders` no-op
  shipping pattern.
- **`FormsServerApp.withCustomValidator` / `withGuard` / `withAction`**
  added to the compose root (not in the spec) so callers register
  predicates without subclassing.
- **`DefaultedFormStore` decorator** on `IFormStore` overlays
  compose-time-registered schemas as scope-wide fallbacks. Not
  spec'd; needed because schemas are useful to ship as code-defined
  constants without forcing every scope to call `SaveSchema` first.
