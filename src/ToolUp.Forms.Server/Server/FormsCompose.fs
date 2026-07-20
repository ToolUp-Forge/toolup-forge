module ToolUp.Forms.FormsCompose

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.Server
open ToolUp.Platform.IEntityStore
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow
open ToolUp.Forms.FormApi
open ToolUp.Forms.FormValidator
open ToolUp.Forms.IFormStore
open ToolUp.Forms.IWorkflowEngine
open ToolUp.Forms.IActionLedger
open ToolUp.Forms.FormApiHandler
open ToolUp.Forms.PublicFormApi
open ToolUp.Forms.PublicFormApiHandler

// ─── Phase 66 Stream B.6 — Forms public-submit surface declaration ──
//
// `PublicFormApiHandler` serves `IPublicFormApi`'s two-method
// Fable.Remoting surface at `/api/public/forms/<MethodName>` (per
// `PublicFormApi.routeBuilder`). Both routes admit only `ClaimBearer`
// subjects — the share-token claim IS the identity, validated per
// request by `IShareTokenStore`. They are surfaced to
// `SurfaceEnforcementMiddleware` via per-route exact-match
// `SurfaceRequirement.claimBearerOnly` declarations on a synthetic
// `ServerModule` composed by `composeForms` below.
//
// Keeping the declarations companion-owned (rather than pushing them
// into the SDK's `sdkBuiltInRouteOverrides` list, which today carries
// only the `TeamApi` team-CRUD overrides) means the Forms-companion
// concern of "which subjects may submit publishable forms" stays
// inside the Forms companion — a downstream consumer using
// `withForms` picks up the declaration automatically, and the SDK's
// central override list does not grow per companion.
//
// Anonymous subjects hitting either route get `401
// authentication_required`; user / team-member subjects hitting them
// without a share token get `403 claim_bearer_not_admitted`.

let private publicFormApiRouteSurfaceRequirements: ((string * string) * SurfaceRequirement) list = [
    ("POST", PublicFormApi.routeBuilder "IPublicFormApi" "GetSchemaByToken"), SurfaceRequirement.claimBearerOnly
    ("POST", PublicFormApi.routeBuilder "IPublicFormApi" "SubmitWithToken"), SurfaceRequirement.claimBearerOnly
]

/// Synthetic `ServerModule` carrying only the Forms public-submit
/// `SurfaceRequirement.claimBearerOnly` per-route declarations. The
/// module exposes no handlers, no datatypes, no AI tools, no jobs —
/// its single duty is to add the two exact `(method, path)` entries
/// to `ServerApp.RouteSurfaceOverrides` via the standard `addModule`
/// fan-out. `composeForms` registers it so every consumer using
/// `FormsServerApp.run` or `withForms` picks up the declaration
/// without having to declare it themselves.
let private formsPublicSubmitSurfaceModule: ServerModule =
    publicFormApiRouteSurfaceRequirements
    |> List.fold
        (fun m ((httpMethod, path), req) -> ServerModule.withRouteSurfaceRequirement httpMethod path req m)
        (ServerModule.create "ToolUp.Forms.PublicSubmit")

// ─── Phase 21 — FormsServerApp composition root ─────────────────────
//
// `FormsServerApp` mirrors `AIServerApp` / `RAGServerApp` /
// `SchedulingServerApp` shape: it wraps a base `ServerApp` and adds a
// `with*` helper for every `ServerApp.with*` so consumers write a
// single fluent pipeline.
//
// **Required substrate**: `IEntityStore` + `IAuditLog`. Like
// `SchedulingServerApp`, the compose step does not enforce
// `ServerConfig.EntityStore = EnabledEntityStore` — if the consumer
// forgets, the DI lookup at first-call time throws and the SDK's
// error handler propagates as `500`. Documented requirement.
//
// **Two entity registrations** flow through:
//   * `FormSchema` — no extra indexes (lookups are by Id).
//   * `Submission` — indexes on `FormId` / `SubmittedBy` / `State`,
//     plus compound `FormId+State`.
//
// **API handler**: a single `IFormApi` mounted via
// `makeApi (fun ctx -> formApi ... ctx)`. The handler resolves
// `scopeId` / `userId` / role from `HttpContext.Items` (populated by
// `ScopeResolutionMiddleware` and `ITeamStore`).
//
// **Compose-time-registered defaults**: `withFormSchema` /
// `withWorkflow` / `withCustomValidator` / `withGuard` / `withAction`
// collect maps populated at compose time and immutable for process
// lifetime. The schema map is overlaid on the underlying `IFormStore`
// via `DefaultedFormStore` so registered defaults are available in
// every scope without anyone calling `SaveSchema` first; per-scope
// `SaveSchema` writes still take precedence.

/// Decorator that overlays compose-time-registered default schemas
/// on top of an underlying `IFormStore`. `SaveSchema` always writes
/// to the inner store; `GetSchema` / `ListSchemas` fall back to
/// (or merge with) the registered defaults when the inner store has
/// no per-scope override.
type DefaultedFormStore(inner: IFormStore, defaults: Map<FormSchemaId, FormSchema>) =
    interface IFormStore with
        member _.SaveSchema(scopeId, schema) = inner.SaveSchema(scopeId, schema)

        member _.GetSchema(scopeId, schemaId, version) = async {
            let! r = inner.GetSchema(scopeId, schemaId, version)

            match r with
            | Ok _ -> return r
            | Error(FormError.NotFound _) ->
                match Map.tryFind schemaId defaults with
                | Some defaultSchema ->
                    match version with
                    | None -> return Ok defaultSchema
                    | Some v when v = defaultSchema.Version -> return Ok defaultSchema
                    | Some _ -> return r
                | None -> return r
            | Error _ -> return r
        }

        member _.ListSchemas(scopeId) = async {
            let! persisted = inner.ListSchemas scopeId
            let persistedIds = persisted |> List.map _.Id |> Set.ofList

            let extras =
                defaults
                |> Map.toList
                |> List.map snd
                |> List.filter (fun s -> not (Set.contains s.Id persistedIds))

            return persisted @ extras |> List.sortBy _.Id
        }

        member _.DeleteSchema(scopeId, schemaId) = inner.DeleteSchema(scopeId, schemaId)

        member _.SaveSubmission(scopeId, submission) =
            inner.SaveSubmission(scopeId, submission)

        member _.GetSubmission(scopeId, submissionId) =
            inner.GetSubmission(scopeId, submissionId)

        member _.ListSubmissions(scopeId, query) = inner.ListSubmissions(scopeId, query)

        member _.DeleteSubmission(scopeId, submissionId) =
            inner.DeleteSubmission(scopeId, submissionId)

/// Record form of compose arguments. Wraps a base `ServerApp` and
/// carries the compose-time-registered maps.
type FormsServerApp = {
    Base: ServerApp
    Schemas: Map<FormSchemaId, FormSchema>
    Workflows: Map<WorkflowId, WorkflowDefinition>
    CustomValidators: CustomValidatorRegistry
    Guards: Map<string, WorkflowGuard>
    Actions: Map<string, WorkflowAction>
    /// Phase 21c — optional `IAnalyserCache` wired by
    /// `withAnalyserCache`. When `None` (the default), every
    /// `GetAggregations` re-runs the analyser pipeline — preserves
    /// the pre-Phase-21c behaviour byte-for-byte. When `Some`, the
    /// handler reads through the cache and writes back on miss.
    AnalyserCache: ToolUp.Forms.AnalyserCache.IAnalyserCache option
    /// Phase 21d — optional `IActionLedger` wired by `withActionLedger`.
    /// When `None`, the compose step auto-defaults to a fresh
    /// `InMemoryActionLedger` per process — survives in-process
    /// retries but does not survive a process restart, and emits a
    /// preflight warning in non-`Anonymous` modes (durable
    /// deployments must wire a distributed ledger).
    ActionLedger: IActionLedger option
    /// Phase 21d — per-action `ActionFailurePolicy` overrides keyed
    /// by registered action name. Actions without an explicit
    /// policy use the engine default (`DeadLetter`). Registered via
    /// `withActionPolicy`.
    ActionPolicies: Map<string, ActionFailurePolicy>
    /// Phase 21e — optional `IShareTokenRateLimiter` wired by
    /// `withShareTokenRateLimiter`. `None` (the default) auto-builds
    /// a fresh `InMemoryShareTokenRateLimiter` at compose time
    /// (single-instance only — see the impl's module header).
    /// Distributed deployments wire a Redis / `IRateLimitStore`-backed
    /// companion here.
    ShareTokenRateLimiter: IShareTokenRateLimiter option
}

module FormsServerApp =

    let create () : FormsServerApp = {
        Base = ServerApp.empty
        Schemas = Map.empty
        Workflows = Map.empty
        CustomValidators = FormValidator.emptyRegistry
        Guards = Map.empty
        Actions = Map.empty
        AnalyserCache = None
        ActionLedger = None
        ActionPolicies = Map.empty
        ShareTokenRateLimiter = None
    }

    /// Phase 1h composition seam — lift an existing `ServerApp` into a
    /// `FormsServerApp` so the additive `FormsCompose.withForms`
    /// extension can stack Forms contributions onto whatever the input
    /// `ServerApp` already carries. The input `ServerApp` becomes the
    /// `Base` field; all Forms-specific maps initialise empty (the
    /// configurator passed to `withForms` populates them via
    /// `withFormSchema` / `withWorkflow` / etc.).
    let createFrom (baseApp: ServerApp) : FormsServerApp = {
        Base = baseApp
        Schemas = Map.empty
        Workflows = Map.empty
        CustomValidators = FormValidator.emptyRegistry
        Guards = Map.empty
        Actions = Map.empty
        AnalyserCache = None
        ActionLedger = None
        ActionPolicies = Map.empty
        ShareTokenRateLimiter = None
    }

    /// Phase 21c — opt into persistent analyser-output caching.
    /// The cache reads through on every `GetAggregations`; cache
    /// misses fall through to the live analyser pipeline and write
    /// the result back. Schema-version bumps invalidate naturally
    /// (the key includes `SchemaVersion`); a new submission for an
    /// existing schema clears the cache for that schema, so the
    /// next read re-runs.
    ///
    /// Deployments that skip this builder retain the default
    /// "every GetAggregations re-runs the analyser" behaviour
    /// byte-for-byte.
    let withAnalyserCache (cache: ToolUp.Forms.AnalyserCache.IAnalyserCache) (app: FormsServerApp) : FormsServerApp = {
        app with
            AnalyserCache = Some cache
    }

    // ─── Delegating helpers (mirror every `ServerApp.with*`) ─────

    let withConfig (c: ServerConfig) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withConfig c app.Base
    }

    let withAuth (a: IAuthProvider) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withAuth a app.Base
    }

    let withLogger (l: ILogger) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withLogger l app.Base
    }

    let withStorage (s: IBlobStorage) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withStorage s app.Base
    }

    let withNotifications (n: INotificationChannel) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withNotifications n app.Base
    }

    let withTransactionalSink (sink: INotificationSink) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withTransactionalSink sink app.Base
    }

    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withHealthCheck check app.Base
    }

    let withConfigValidator (validator: ConfigValidation.IConfigValidator) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withConfigValidator validator app.Base
    }

    let withEncryptedBlobStorage (resolver: IBlobEncryptionKeyResolver) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withEncryptedBlobStorage resolver app.Base
    }

    let withEntity<'T> (registration: EntityTypes.EntityRegistration<'T>) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withEntity registration app.Base
    }

    /// Phase 9b.B — declare a composition-root-owned background job.
    /// Delegates to `ServerApp.withJobHandler`. See that helper's
    /// docstring for the full contract.
    let withJobHandler
        (handlerName: string, handler: IJobHandler, trigger: Trigger)
        (app: FormsServerApp)
        : FormsServerApp =
        {
            app with
                Base = ServerApp.withJobHandler (handlerName, handler, trigger) app.Base
        }

    /// Phase 9b.B — declare a composition-root-owned background job
    /// with full control over every `JobRegistration` knob. Delegates
    /// to `ServerApp.withScheduledJob`.
    let withScheduledJob (declaration: ScheduledJobDeclaration) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withScheduledJob declaration app.Base
    }

    /// Phase 9b.A — opt into back-fill of `OnEvent` jobs on detected
    /// scheduler tick drift. Delegates to `ServerApp.withBackfillMissedTicks`.
    let withBackfillMissedTicks (enabled: bool) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withBackfillMissedTicks enabled app.Base
    }

    /// Phase 598 — opt into the event-trigger catch-up watermark.
    /// Delegates to `ServerApp.withEventTriggerCatchUp`.
    let withEventTriggerCatchUp (enabled: bool) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withEventTriggerCatchUp enabled app.Base
    }

    /// Phase 9t — audit-write failure policy. Delegates to
    /// `ServerApp.withAuditFailurePolicy`.
    let withAuditFailurePolicy (policy: AuditFailurePolicy) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withAuditFailurePolicy policy app.Base
    }

    let withPreMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withPreMiddleware f app.Base
    }

    let withPostMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.withPostMiddleware f app.Base
    }

    let addModule (m: ServerModule) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.addModule m app.Base
    }

    let addModules (modules: ServerModule list) (app: FormsServerApp) : FormsServerApp = {
        app with
            Base = ServerApp.addModules modules app.Base
    }

    // ─── Forms-specific helpers ──────────────────────────────────

    /// Register a default form schema available in every scope
    /// without anyone calling `SaveSchema` first. Per-scope
    /// `SaveSchema` writes take precedence over the default.
    ///
    /// **Boot-time validation.** Schemas with two or more
    /// `FieldSchema` entries sharing the same `Key` are rejected
    /// loudly. With duplicates the submission map (keyed by `Key`)
    /// silently coalesces one field's value over the other and
    /// validators / renderers behave non-deterministically by field
    /// order. Failing at compose time surfaces the defect at
    /// startup, before any submission can land against the broken
    /// schema. The exception names the offending key(s) and the
    /// field positions (0-based) so the operator can locate the
    /// rogue entries without grep'ing.
    let withFormSchema (schema: FormSchema) (app: FormsServerApp) : FormsServerApp =
        let duplicates =
            schema.Fields
            |> List.mapi (fun i f -> f.Key, i)
            |> List.groupBy fst
            |> List.choose (fun (key, occurrences) ->
                match occurrences with
                | _ :: _ :: _ -> Some(key, occurrences |> List.map snd)
                | _ -> None)

        if not (List.isEmpty duplicates) then
            let details =
                duplicates
                |> List.map (fun (key, positions) ->
                    let positionList = positions |> List.map string |> String.concat ", "
                    sprintf "'%s' at field positions [%s]" key positionList)
                |> String.concat "; "

            failwithf
                "ToolUp.Forms.FormsCompose.withFormSchema: schema '%s' has duplicate field Key(s): %s. Each FieldSchema.Key must be unique within a schema."
                schema.Id
                details

        {
            app with
                Schemas = Map.add schema.Id schema app.Schemas
        }

    /// Register a workflow definition. Workflows are immutable for
    /// process lifetime; submissions reference workflows by `Id`.
    let withWorkflow (workflow: WorkflowDefinition) (app: FormsServerApp) : FormsServerApp = {
        app with
            Workflows = Map.add workflow.Id workflow app.Workflows
    }

    /// Register a server-side custom validator predicate. Forms
    /// authors reference it by name from `ValidationRule.Custom`.
    let withCustomValidator (name: string) (predicate: CustomValidator) (app: FormsServerApp) : FormsServerApp = {
        app with
            CustomValidators = Map.add name predicate app.CustomValidators
    }

    /// Register a workflow guard. Workflows reference it by name
    /// from `Transition.Guard`. Guard returns `Error reason` to
    /// short-circuit a transition with `FormError.TransitionDenied`.
    let withGuard (name: string) (guard: WorkflowGuard) (app: FormsServerApp) : FormsServerApp = {
        app with
            Guards = Map.add name guard app.Guards
    }

    /// Register a workflow action. Workflows reference it by name
    /// from `Transition.Action`. Actions run after the new state
    /// has been persisted under the Phase 21d action-ledger
    /// (exactly-once invocation across replays); the per-action
    /// failure behaviour is set via [`withActionPolicy`] —
    /// without one, the engine defaults the action to
    /// `ActionFailurePolicy.DeadLetter` (commit + dead-letter +
    /// metric + audit, no silent loss). Set `LogOnly` explicitly
    /// to preserve the pre-21d best-effort posture for legacy
    /// registrations.
    let withAction (name: string) (action: WorkflowAction) (app: FormsServerApp) : FormsServerApp = {
        app with
            Actions = Map.add name action app.Actions
    }

    /// Phase 21d — override the default `ActionFailurePolicy` for a
    /// registered action. Without this call, the engine treats an
    /// action's failure under `DeadLetter` (the safe new default).
    /// Override to `FailSubmission` for actions whose downstream
    /// effect is load-bearing (the transition must roll back if the
    /// side effect can't be delivered) or to `LogOnly` for
    /// genuinely best-effort actions where dead-lettering adds no
    /// value. Last write wins; calling twice with the same action
    /// name keeps the most recent policy.
    let withActionPolicy (actionName: string) (policy: ActionFailurePolicy) (app: FormsServerApp) : FormsServerApp = {
        app with
            ActionPolicies = Map.add actionName policy app.ActionPolicies
    }

    /// Phase 21d — supply an `IActionLedger` impl. Without this call
    /// the compose step auto-defaults to a fresh
    /// `InMemoryActionLedger` per process (survives in-process
    /// retries; does not survive a process restart). Distributed
    /// deployments wire a durable ledger here so a process restart
    /// between state-persist and action-completion does not re-fire
    /// the action on restart. The `IActionLedgerContract` portability
    /// pack validates any impl against the same conformance bar as
    /// the in-memory default.
    let withActionLedger (ledger: IActionLedger) (app: FormsServerApp) : FormsServerApp = {
        app with
            ActionLedger = Some ledger
    }

    /// Phase 21e — supply an `IShareTokenRateLimiter` impl. Without
    /// this call the compose step auto-defaults to a fresh
    /// `InMemoryShareTokenRateLimiter` per process (single-instance
    /// only — see the impl's module header). Distributed deployments
    /// wire a companion backed by Redis / their `IRateLimitStore`
    /// (Phase 56) / equivalent here so per-token windows are shared
    /// across replicas. The `IShareTokenRateLimiterContract`
    /// portability pack validates any impl against the same
    /// conformance bar as the in-memory default.
    let withShareTokenRateLimiter (rateLimiter: IShareTokenRateLimiter) (app: FormsServerApp) : FormsServerApp = {
        app with
            ShareTokenRateLimiter = Some rateLimiter
    }

    /// Phase 1h composition seam — apply every Forms-specific
    /// contribution (entity registrations, config validators, metric
    /// declarations, DI registrations, the `IFormApi` + public-forms
    /// route handlers, and the anonymous-route prefix) onto the inner
    /// `ServerApp`, returning the composed result without driving it.
    /// `FormsServerApp.run` calls this and then `ServerApp.run`; the
    /// future `ServerApp.withForms` builder calls it without invoking
    /// `ServerApp.run`, so the same Forms contributions can stack with
    /// AI / RAG contributions onto one composition root (Phase 1h goal).
    ///
    /// **Advanced.** Consumers should use `FormsServerApp.run` unless
    /// they are stacking multiple companion supersets. Hidden from
    /// IntelliSense via `[<EditorBrowsable>]`.
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    let composeForms (app: FormsServerApp) : ServerApp =
        // Phase 1h conflict validator — fail fast if Forms has already
        // been composed onto this pipeline (e.g. `withForms ... |>
        // withForms ...`). Pre-empts the cascading
        // duplicate-entity-registration / duplicate-metric-name /
        // double-mounted-route failures that the second composition
        // would otherwise surface deep inside `compose` or at first
        // request.
        ServerApp.ensureCompanionNotAlreadyComposed "ToolUp.Forms" app.Base

        // 1. Register the two entity types via the base's
        //    `withEntity` pipeline. Also register the Phase 21b
        //    Publishable-form preflight validator — it's free at
        //    runtime when no Publishable schemas are configured
        //    (registers with `Ok` and exits) and warns loudly when
        //    they ARE configured but the deployment is missing
        //    PublicBaseUrl / ShareTokenStore.
        let publishableValidator =
            PublishableFormConfigValidator.PublishableFormConfigValidator(app.Base.Config, app.Schemas)

        let withEntities =
            app.Base
            |> ServerApp.withEntity EntityRegistrations.formSchemaRegistration
            |> ServerApp.withEntity EntityRegistrations.submissionRegistration
            |> ServerApp.withConfigValidator publishableValidator
            |> ServerApp.withMetricRegistrations ActionLedgerMetrics.registrations
            |> ServerApp.withMetricRegistrations FormStore.metricRegistrations
            // Phase 66 Stream B.6 — register the synthetic
            // `Forms.PublicSubmit` surface module so the two
            // `/api/public/forms/*` routes declare
            // `SurfaceRequirement.claimBearerOnly`. See the module
            // preamble for the rationale (companion-owned per-route
            // declarations rather than central SDK overrides).
            |> ServerApp.addModule formsPublicSubmitSurfaceModule

        // Capture the compose-time-registered maps so the DI
        // factories can close over them (no per-request allocation).
        let schemas = app.Schemas
        let workflows = app.Workflows
        let validators = app.CustomValidators
        let guards = app.Guards
        let actions = app.Actions
        let actionPolicies = app.ActionPolicies

        // Phase 21d — auto-default the action ledger to a fresh
        // `InMemoryActionLedger` per process when the consumer didn't
        // opt in via `withActionLedger`. The default is correct for
        // dev / single-process deployments; distributed deployments
        // must wire a durable ledger explicitly. Warn loudly whenever
        // the deployment exposes any authenticated surface (Phase 66:
        // the pre-66 non-`Anonymous` modes) so an operator deploying
        // with workflow-bearing forms in production sees a clear signal
        // they need a durable ledger.
        let resolvedActionLedger: IActionLedger =
            match app.ActionLedger with
            | Some ledger -> ledger
            | None ->
                if DeploymentConfig.requiresAnyAuth app.Base.Config then
                    // The standard SDK preflight (`/dev/inspect` +
                    // startup-log) surfaces resolved interfaces; this
                    // Console.Error.WriteLine is intentionally
                    // out-of-band so the warning is visible even
                    // before the logger is fully wired.
                    System.Console.Error.WriteLine(
                        "ToolUp.Forms: no IActionLedger registered via withActionLedger; "
                        + "defaulting to in-process InMemoryActionLedger. "
                        + "Distributed deployments MUST wire a durable ledger "
                        + "(per Phase 21d migration doc) — a process restart between "
                        + "state-persist and action-completion will not re-fire actions "
                        + "but is also not durable across the restart."
                    )

                InMemoryActionLedger.InMemoryActionLedger() :> IActionLedger

        // Phase 21e — auto-default the share-token rate limiter to a
        // fresh `InMemoryShareTokenRateLimiter` per process when the
        // consumer didn't opt in via `withShareTokenRateLimiter`. The
        // default is single-instance only; multi-replica deployments
        // wire a distributed companion explicitly.
        let resolvedShareTokenRateLimiter: IShareTokenRateLimiter =
            match app.ShareTokenRateLimiter with
            | Some limiter -> limiter
            | None -> InMemoryShareTokenRateLimiter.create ()

        // Phase 136 part 2 — refuse / warn when the resolved share-token
        // rate limiter is the in-memory default in a scale-out-shaped
        // deployment. Reads the limiter's declared `IsDistributed`
        // capability rather than guessing by type; free at runtime
        // (registers `Ok` when single-instance / distributed / no
        // share-token surface). Registered here — after the limiter is
        // resolved — so it inspects the exact instance DI will serve.
        let withEntities =
            withEntities
            |> ServerApp.withConfigValidator (
                ShareTokenRateLimiterDistributionValidator.ShareTokenRateLimiterDistributionValidator(
                    app.Base.Config,
                    resolvedShareTokenRateLimiter
                )
            )

        // Capture the explicitly-configured analyser cache (via
        // `withAnalyserCache`) so the DI factory can fall back to
        // an auto-constructed `ResultStoreAnalyserCache` when the
        // consumer didn't opt in but the deployment has an
        // `IResultStore` wired (Phase 8 substrate).
        let explicitAnalyserCache = app.AnalyserCache

        // 2. DI registrations for IFormStore + IWorkflowEngine.
        let formsServiceConfig (services: IServiceCollection) =
            services
                .AddSingleton<IFormStore>(
                    System.Func<System.IServiceProvider, IFormStore>(fun sp ->
                        let entityStore = sp.GetService(typeof<IEntityStore>) :?> IEntityStore

                        let metricsSink =
                            sp.GetService(typeof<ToolUp.Platform.Metrics.IMetricsSink>)
                            :?> ToolUp.Platform.Metrics.IMetricsSink

                        let logger =
                            (sp.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory)
                                .CreateLogger("ToolUp.Forms.FormStore")

                        let warn: FormStore.FormStoreWarn = fun msg -> logger.LogWarning msg

                        let baseStore = FormStore.FormStore(entityStore, warn, metricsSink) :> IFormStore

                        DefaultedFormStore(baseStore, schemas) :> IFormStore)
                )
                .AddSingleton<IActionLedger>(resolvedActionLedger)
                .AddSingleton<IShareTokenRateLimiter>(resolvedShareTokenRateLimiter)
                .AddSingleton<IWorkflowEngine>(
                    System.Func<System.IServiceProvider, IWorkflowEngine>(fun sp ->
                        let formStore = sp.GetService(typeof<IFormStore>) :?> IFormStore
                        let auditLog = sp.GetService(typeof<IAuditLog>) :?> IAuditLog
                        let ledger = sp.GetService(typeof<IActionLedger>) :?> IActionLedger

                        // Phase 21d — `IMetricsSink` is always in DI
                        // (the SDK registers `NoOpMetricsSink` when no
                        // sink is wired), so the resolution is
                        // unconditional. Tests inject a counter-recording
                        // double via the constructor instead of through DI.
                        let metricsSink =
                            sp.GetService(typeof<ToolUp.Platform.Metrics.IMetricsSink>)
                            :?> ToolUp.Platform.Metrics.IMetricsSink

                        let logger =
                            (sp.GetService(typeof<ILoggerFactory>) :?> ILoggerFactory)
                                .CreateLogger("ToolUp.Forms.WorkflowEngine")

                        let warn: WorkflowEngine.WorkflowWarn = fun msg -> logger.LogWarning msg

                        WorkflowEngine.WorkflowEngine(
                            formStore,
                            auditLog,
                            ledger,
                            metricsSink,
                            warn,
                            workflows,
                            guards,
                            actions,
                            actionPolicies,
                            sp
                        )
                        :> IWorkflowEngine)
                )
                // Phase 21c follow-up B — analyser-output cache. Three
                // ladder rungs:
                //   1. Consumer called `withAnalyserCache` → use that
                //      instance.
                //   2. No explicit instance, but `IResultStore` is in
                //      DI (Phase 8 substrate) → auto-construct
                //      `ResultStoreAnalyserCache` so deployments with
                //      durable analytical storage get caching for free.
                //   3. Neither — fall back to `NoOpAnalyserCache`
                //      (pre-Phase-21c behaviour byte-for-byte).
                // The scope resolver uses the standard `StorageScope`
                // resolution from `HttpContext.Items` if available; the
                // DI factory closes over `sp` for the cache lifetime but
                // the resolver runs per `tryLookup` call.
                .AddSingleton<ToolUp.Forms.AnalyserCache.IAnalyserCache>(
                    System.Func<System.IServiceProvider, ToolUp.Forms.AnalyserCache.IAnalyserCache>(fun sp ->
                        match explicitAnalyserCache with
                        | Some cache -> cache
                        | None ->
                            match sp.GetService(typeof<IResultStore>) with
                            | :? IResultStore as store ->
                                // Resolve scope at construction time
                                // via the AccessContext flowed onto
                                // each request. The cache is a
                                // singleton (one container per
                                // deployment) so the scope resolver
                                // must read the current request's
                                // AccessContext fresh each call.
                                let httpAccessor =
                                    sp.GetService(typeof<IHttpContextAccessor>) :?> IHttpContextAccessor

                                let scopeResolver () =
                                    match httpAccessor with
                                    | null -> "_system"
                                    | _ ->
                                        match httpAccessor.HttpContext with
                                        | null -> "_system"
                                        | ctx ->
                                            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                                            | true, (:? StorageScope as s) -> s.ScopeId
                                            | _ -> "_system"

                                ToolUp.Forms.AnalyserCache.createResultStoreBacked store scopeResolver
                            | _ -> ToolUp.Forms.AnalyserCache.noOpCache)
                )

        // 3. API handler. Resolves IFormStore + IWorkflowEngine +
        //    IAuditLog per request out of DI; closes over the
        //    compose-time-registered validators + workflow map.
        // Capture the deployment-resolved share-token store + public
        // base URL outside the per-request lambda. ShareTokenStore may
        // be unregistered (deployment skipped enabling the substrate);
        // the formApi factory passes the option through and the
        // IssueTokens handler returns a clear error on absent store.
        let publicBaseUrl = app.Base.Config.PublicBaseUrl

        let formsApiHandler =
            makeApi (fun (ctx: HttpContext) ->
                let formStore = ctx.RequestServices.GetService(typeof<IFormStore>) :?> IFormStore

                let workflowEngine =
                    ctx.RequestServices.GetService(typeof<IWorkflowEngine>) :?> IWorkflowEngine

                let auditLog = ctx.RequestServices.GetService(typeof<IAuditLog>) :?> IAuditLog

                let shareTokenStore =
                    match ctx.RequestServices.GetService(typeof<IShareTokenStore>) with
                    | :? IShareTokenStore as s -> Some s
                    | _ -> None

                // Phase 21b (slice 6) — INotificationChannel + IBlobStorage
                // for the optional DispatchInvitationsByEmail surface. Both
                // are SDK-mandatory in any non-trivial deployment so the
                // typed `option` here is for graceful degradation, not a
                // real "missing service" expectation.
                let notificationChannel =
                    match ctx.RequestServices.GetService(typeof<INotificationChannel>) with
                    | :? INotificationChannel as c -> Some c
                    | _ -> None

                let blobStorage =
                    match ctx.RequestServices.GetService(typeof<IBlobStorage>) with
                    | :? IBlobStorage as b -> Some b
                    | _ -> None

                // Phase 21c follow-up — analyser cache + registered
                // analysers. The DI factory chose the cache rung
                // (explicit / ResultStore-backed / NoOp); the
                // analyser list is resolved per-request via
                // `GetServices<IFormSubmissionAnalyser>()` so
                // deployments that register multiple analysers
                // (sentiment + topic, etc.) compose cleanly.
                let analyserCache =
                    ctx.RequestServices.GetService(typeof<ToolUp.Forms.AnalyserCache.IAnalyserCache>)
                    :?> ToolUp.Forms.AnalyserCache.IAnalyserCache

                let analysers =
                    ctx.RequestServices.GetServices(
                        typeof<ToolUp.Forms.IFormSubmissionAnalyser.IFormSubmissionAnalyser>
                    )
                    |> Seq.cast<ToolUp.Forms.IFormSubmissionAnalyser.IFormSubmissionAnalyser>
                    |> List.ofSeq

                formApi
                    formStore
                    workflowEngine
                    validators
                    auditLog
                    workflows
                    shareTokenStore
                    publicBaseUrl
                    notificationChannel
                    blobStorage
                    analyserCache
                    analysers
                    ctx)

        // 3b. Phase 21b — public-form Fable.Remoting handler at
        //     /api/public/forms/. Token-gated (no AccessContext).
        //     Resolves the same IFormStore + IAuditLog as the
        //     authenticated handler, plus the IShareTokenStore that
        //     the deployment registered via
        //     ServerConfig.ShareTokenStore = EnabledShareTokenStore.
        //     If IShareTokenStore is not in DI (deployment forgot to
        //     enable the substrate), the handler factory throws on
        //     first hit and ASP.NET surfaces a 500 — documented
        //     prerequisite, not a silent degradation.
        let publicFormsApiHandler =
            Api.make (
                (fun (ctx: HttpContext) ->
                    let shareTokenStore =
                        ctx.RequestServices.GetService(typeof<IShareTokenStore>) :?> IShareTokenStore

                    let formStore = ctx.RequestServices.GetService(typeof<IFormStore>) :?> IFormStore
                    let auditLog = ctx.RequestServices.GetService(typeof<IAuditLog>) :?> IAuditLog

                    let rateLimiter =
                        ctx.RequestServices.GetService(typeof<IShareTokenRateLimiter>) :?> IShareTokenRateLimiter

                    let logger = ctx.RequestServices.GetService(typeof<ILogger>) :?> ILogger

                    publicFormApi shareTokenStore formStore validators auditLog rateLimiter logger ctx),
                routeBuilder = PublicFormApi.routeBuilder
            )

        // 4. Merge into the base extensions. The Phase 66 Stream B.6
        //    per-route `SurfaceRequirement.claimBearerOnly`
        //    declarations on the two `/api/public/forms/*` routes
        //    were registered above via
        //    `formsPublicSubmitSurfaceModule`, so the public-submit
        //    endpoints reach `SurfaceEnforcementMiddleware` with an
        //    explicit `claimBearerOnly` requirement: anonymous
        //    subjects get `401 authentication_required`; user /
        //    team-member subjects get `403
        //    claim_bearer_not_admitted`; only valid share-token
        //    bearers pass through.
        let baseExt = withEntities.Extensions

        let mergedExt: ComposeExtensions = {
            baseExt with
                Handlers = baseExt.Handlers @ [ formsApiHandler; publicFormsApiHandler ]
                ServiceConfig =
                    match baseExt.ServiceConfig with
                    | None -> Some formsServiceConfig
                    | Some baseFn -> Some(fun s -> formsServiceConfig (baseFn s))
        }

        // Phase 1h — append the Forms marker so a second `withForms` on
        // the same pipeline trips the entry-guard above.
        {
            withEntities with
                Extensions = mergedExt
        }
        |> ServerApp.withCompanionMarker "ToolUp.Forms"

    /// Drive the final composition. Registers the two form entity
    /// types, wires `IFormStore` (overlaid with registered defaults)
    /// + `IWorkflowEngine` into DI, mounts the `IFormApi` handler,
    /// and delegates to `ServerApp.run`. Phase 1h — implementation is
    /// now `composeForms >> ServerApp.run`; consumers needing to
    /// stack Forms with AI / RAG companions on one composition root
    /// call `composeForms` directly instead.
    let run (app: FormsServerApp) : int = composeForms app |> ServerApp.run

// ─── Additive companion-set extension `withForms` (Phase 1h) ────────
//
// Stack Forms contributions onto an existing `ServerApp` pipeline
// alongside AI / RAG / future companions, without forcing the
// deployment to commit to `FormsServerApp.run` as the terminal call.

/// Phase 1h — stack Forms contributions onto an existing `ServerApp`
/// pipeline. The `configure` function builds Forms-specific state
/// (schemas, workflows, guards, actions, custom validators, action
/// ledger / policies, analyser cache, share-token rate limiter) on a
/// fresh `FormsServerApp` whose `Base` is the input `ServerApp`. The
/// configurator should call only Forms-specific helpers
/// (`FormsServerApp.withFormSchema` / `withWorkflow` / `withGuard` /
/// `withAction` / etc.); the delegating helpers (`withConfig` /
/// `withAuth` / …) exist on `FormsServerApp` for backcompat but
/// calling them inside the configurator overwrites the base
/// `ServerApp`'s existing configuration. Set base configuration on
/// the outer pipeline before calling `withForms`.
///
/// Calling `withForms` twice on the same pipeline composes Forms
/// twice — the Phase 1h conflict validator (task 4) surfaces this at
/// compose time. (Each call re-registers the two Forms entity types
/// and double-mounts the IFormApi route; both fail loudly via
/// existing duplicate-detection logic in `compose`, but the
/// dedicated validator gives a clearer diagnostic.)
///
/// Example — Forms + AI in one composition root:
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> ServerApp.withStorage storage
///     |> FormsCompose.withForms (fun f ->
///         f
///         |> FormsServerApp.withFormSchema mySchema
///         |> FormsServerApp.withWorkflow myWorkflow
///         |> FormsServerApp.withAction "stampJob" stampJobAction)
///     |> AICompose.withAI factory providerProfile (fun ai ->
///         ai |> AIServerApp.withAIConfig assistant)
///     |> ServerApp.run
let withForms (configure: FormsServerApp -> FormsServerApp) (app: ServerApp) : ServerApp =
    FormsServerApp.createFrom app |> configure |> FormsServerApp.composeForms