module ToolUp.Reporting.ReportingCompose

open System
open ToolUp.Platform
open ToolUp.Reporting
open ToolUp.Reporting.RendererRegistry

// NOTE: `MarkdownRenderer` / `HtmlRenderer` are deliberately NOT opened.
// Both modules expose a `create ()`, so opening both put two identically-
// named functions in scope and the later `open` silently won — an
// unqualified `create ()` meant to build the Markdown renderer resolved to
// `HtmlRenderer.create`. That is a defect no type error can catch (both
// return `IReportRenderer`) and no reviewer sees. Every renderer
// construction below is module-qualified; keep it that way.

// ─── ReportingCompose ────────────────────────────────────────────────
//
// Helper for setting up a `RendererRegistry` with the zero-dep
// defaults (Markdown + HTML) plus any sub-companion renderers the
// deployment wants. Sub-companion impls (Pdf, Docx, Xlsx, Pptx) call
// `registry.Register` against their own factory.
//
// The full `ReportingServerApp.run` flat-superset wrapper (which
// composes the registry + template store + API handler + IAuditLog
// + IDataObjectStore wiring into a single fluent ServerApp builder)
// is a follow-up in this phase. Today's MVP exposes the building
// blocks; consumers wire `ReportApiHandler.create` into their own
// composition root manually. The Phase 23 acceptance criteria
// ("Worked example: TestHarness registers a Markdown template…") is
// satisfied by the Core + Server building-blocks; the fluent
// wrapper lands in a follow-up commit.

/// Build a registry pre-populated with the zero-dep default
/// renderers (Markdown + HTML). Sub-companions register additional
/// formats by calling `registry.Register` after this returns.
let buildDefaultRegistry () : RendererRegistry =
    let registry = RendererRegistry()
    registry.Register(MarkdownRenderer.create ()) |> ignore
    registry.Register(HtmlRenderer.create ()) |> ignore
    registry

/// Convenience: register one extra renderer against an existing
/// registry. Chains for builder-style composition.
let withRenderer (renderer: IReportRenderer) (registry: RendererRegistry) =
    registry.Register renderer |> ignore
    registry

// ─── Narrative components (Phase 534, closing 575's spillover) ───────
//
// Phase 575 shipped `DocxReportRenderer.createWith`, which takes a
// registry resolving narrative `Component(name, props)` blocks to a
// picture. Nothing here offered a place to declare one, so a deployment
// composing the Docx renderer reached for the no-argument `create ()`
// and every component block silently took its data-table degradation —
// the seam existed and nothing was plugged into it.
//
// The registry cannot be supplied here directly: Reporting must not
// name a rendering companion (GP 1), and `ReportingCompose` cannot
// reference a sub-companion without inverting the dependency. So the
// compose surface names a FUNCTION SHAPE instead. A deployment declares
// its component registry once, and hands in the factories that want it:
//
// ```fsharp skip=fragment
// ReportingCompose.buildRegistryWith {
//     ReportingComposeOptions.defaults with
//         NarrativeComponents = myChartRegistry
//         ComponentAwareRenderers = [ DocxReportRenderer.createWithComponents ]
// }
// ```
//
// **Absent registration degrades exactly as Phase 575 shipped.**
// `NarrativeComponents` defaults to `ReportComponentRegistry.empty`, so
// a deployment that composes renderers and says nothing about
// components gets byte-identical behaviour to `create ()` (GP 11), and
// one that composes nothing at all pays nothing (GP 13).

/// Declarative inputs for building the renderer registry.
type ReportingComposeOptions = {
    /// Renderers that need no component registry. Registered after the
    /// zero-dep defaults.
    Renderers: IReportRenderer list
    /// Renderer factories that take the composed component registry.
    /// The Docx sub-companion's `createWithComponents` is the shape
    /// this expects.
    ComponentAwareRenderers: (ReportComponentRegistry -> IReportRenderer) list
    /// Resolves narrative `Component` blocks. Defaults to the empty
    /// registry — every component degrades, exactly as before.
    NarrativeComponents: ReportComponentRegistry
}

module ReportingComposeOptions =
    /// The zero-dep defaults only: no extra renderers, no components.
    let defaults: ReportingComposeOptions = {
        Renderers = []
        ComponentAwareRenderers = []
        NarrativeComponents = ReportComponentRegistry.empty
    }

/// Build the renderer registry from declared options: the zero-dep
/// defaults, then each plain renderer, then each component-aware
/// factory applied to the one declared component registry.
let buildRegistryWith (options: ReportingComposeOptions) : RendererRegistry =
    let registry = buildDefaultRegistry ()

    for renderer in options.Renderers do
        registry.Register renderer |> ignore

    for factory in options.ComponentAwareRenderers do
        registry.Register(factory options.NarrativeComponents) |> ignore

    registry

// ─── Scheduled subscriptions (Phase 534.D) ───────────────────────────
//
// Opt-in, and loudly so. Subscriptions compose three seams the SDK does
// not require a deployment to have — a job scheduler, a data-object
// store and a transactional sink — and a deployment missing any of them
// gets a feature that appears to work (a subscription is created, a
// list shows it) and never delivers anything.
//
// The Phase 623 `DeferredScheduledJobDeclaration` machinery already
// handles the "no scheduler" case with a warning at StartAsync, and
// that is the right shape for a job DECLARED at compose time. It is the
// wrong shape here, because subscriptions are created at RUNTIME: by
// the time the warning fires, the management API has already been
// mounted and is accepting subscriptions that will never run. So this
// compose refuses at compose time instead (GP 13 — fail loud, not
// silent).

/// Why the subscription surface could not be composed. Raised as a
/// compose-time exception rather than logged, because the alternative
/// is a management surface that accepts subscriptions it can never
/// deliver.
exception ReportSubscriptionsNotComposable of missing: string list

/// The message a caller sees. Names every missing seam at once — a
/// deployment wiring this for the first time is usually missing more
/// than one, and reporting them one restart at a time is the failure
/// mode this text exists to avoid.
let subscriptionDiagnostic (missing: string list) =
    let seams = String.concat ", " missing

    $"ToolUp.Reporting: scheduled report subscriptions were composed, but this deployment supplies no {seams}. "
    + "Subscriptions render on a schedule and deliver out of band, so they need an IJobScheduler (set "
    + "ServerConfig.JobScheduler), an IDataObjectStore for the run artefacts, and at least one INotificationSink "
    + "registered via ServerApp.withTransactionalSink. Compose those, or do not compose subscriptions — a "
    + "deployment that never calls withReportSubscriptions pays nothing."

/// Assemble the subscription substrate, or refuse.
///
/// Returns the job handler to register against the scheduler under
/// `ReportSubscription.JobHandlerName`, and the per-caller API handler
/// factory — `ReportSubscriptionApiHandler.createUnder`, taking the
/// principal, the scope the platform resolved for the managing request
/// (`ScopeResolution.forRequest ctx`) and the shard the subscriptions are
/// stored in. Phase 991: a subscription created through it runs under its
/// creator's scope with no further opt-in, and re-saving one re-stamps its
/// job; the anonymous `ReportSubscriptionApiHandler.create` is the explicit,
/// named opt-out, and this function never hands it out. Both are returned rather than registered here because
/// registration is the composition root's job — this function's
/// responsibility is that the parts are consistent with each other,
/// which is exactly what the shared `RetryPolicy` below buys.
///
/// `missing` is supplied by the caller rather than probed: a compose
/// helper in a sub-companion cannot see the deployment's
/// `IServiceCollection`, and the `IIdempotencyStore` precedent is clear
/// that guessing at DI state from outside produces a validator that is
/// confidently wrong. The composition root knows what it wired.
let withReportSubscriptions
    (missing: string list)
    (deps: ReportSubscriptionJobDeps)
    (apiDeps: ReportSubscriptionApiHandler.ReportSubscriptionApiDeps)
    : IJobHandler * (string -> ResolvedScope -> string -> IReportSubscriptionApi) =
    if not (List.isEmpty missing) then
        raise (ReportSubscriptionsNotComposable missing)

    if deps.RetryPolicy <> apiDeps.RetryPolicy then
        // The handler decides a transient failure is TERMINAL by
        // comparing the attempt count against this policy; the API
        // handler registers the job WITH this policy. Two different
        // values means the handler either gives up early or never
        // announces a terminal failure at all — a divergence that would
        // only ever be noticed as "we stopped getting told when reports
        // fail".
        raise (
            ReportSubscriptionsNotComposable [
                "a single JobRetryPolicy — the job handler and the API handler were given different ones"
            ]
        )

    ReportSubscriptionJobHandler.create deps, ReportSubscriptionApiHandler.createUnder apiDeps
// ─── Standing subscriptions (Phase 1005) ─────────────────────────────
//
// A subscription created on a request carries the scope that request
// resolved (Phases 990/991). A STANDING subscription is declared in the
// composition instead — "run report R on cadence C as principal P, deliver
// to D" — so a fresh deployment runs it with nobody signing in. It names a
// principal, never a scope: at startup the deployment's own scope resolver
// resolves the principal (`DeclaredPrincipalResolver`, registered by the
// platform's composition), and the subscription's job is scheduled under
// that resolution through the same typed `Schedule` and carrier a
// request-created one uses. A scope literal reaches no store.
//
// **Refused at startup, never at the first tick.** Every declaration is
// checked before anything is written: its key, its principal (declared,
// resolved, to a persistent scope), its template (present at that scope),
// and the subscription itself (producer, cadence, parameters, format,
// recipients). Any refusal fails the host's start naming every failing
// subscription and what it lacks, and seeds nothing.
//
// **Idempotent.** A declaration's row is keyed on its key at its
// principal's scope, so a re-deploy updates the row it seeded (and
// re-issues its job's carried scope) rather than adding one. A ledger
// records where rows were seeded, so a declaration removed from the
// composition — or whose principal now resolves elsewhere — has its old
// row retired: job cancelled, row deleted. Rows a request created are never
// in the ledger and never touched.

/// A deployment's standing-subscription declarations.
type StandingSubscriptions = {
    /// The principals standing subscriptions may run as.
    Principals: StandingSubscriptionPrincipal list
    /// The subscriptions. An empty list retires every row seeded before.
    Subscriptions: StandingReportSubscription list
}

/// What seeding needs beyond the declarations.
type StandingSubscriptionDeps = {
    /// The SAME deps the management API handler is built with, so a seeded
    /// row's job is registered exactly as a request-created one's.
    Api: ReportSubscriptionApiHandler.ReportSubscriptionApiDeps
    /// The deployment's template store — read to refuse a declaration
    /// whose template is absent at its principal's scope.
    Templates: IReportTemplateStore.IReportTemplateStore
    /// Where rows were seeded, for retirement.
    Ledger: IStandingSubscriptionLedger
}

/// Why a standing subscription refused startup. Each case names the
/// subscription (or principal) and what it lacks.
type StandingSubscriptionRefusal =
    /// The key is empty or holds a character a storage path cannot.
    | InvalidStandingKey of key: string * reason: string
    /// Two declarations share a key.
    | DuplicateStandingKey of key: string
    /// Two principals share a name.
    | DuplicatePrincipal of principal: string
    /// The subscription names a principal the deployment did not declare.
    | UndeclaredPrincipal of subscription: string * principal: string
    /// The deployment's scope resolver did not resolve the principal;
    /// `missing` names the grant it lacks.
    | PrincipalUnresolved of subscription: string * principal: string * missing: string
    /// The principal resolved to a scope whose data does not persist — a
    /// session, or an ephemeral user scope — which a subscription that
    /// runs after a restart cannot report on.
    | PrincipalScopeNotPersistent of subscription: string * principal: string * scopeId: string
    /// The declared template does not exist at the principal's scope.
    | StandingTemplateMissing of subscription: string * templateId: TemplateId * scopeId: string
    /// The subscription itself is invalid: unknown producer, bad cadence,
    /// parameters, format or recipients.
    | InvalidStandingSubscription of subscription: string * error: SubscriptionError
    /// The deployment composes no `DeclaredPrincipalResolver` — it was not
    /// built through the platform's composition.
    | NoPrincipalResolver
    /// The ledger of seeded rows could not be read or written.
    | StandingLedgerUnavailable of reason: string
    /// Writing the row, scheduling its job, or retiring an old row failed.
    | StandingSeedFailed of subscription: string * reason: string

module StandingSubscriptionRefusal =
    /// A sentence naming the subscription and what it lacks.
    let describe =
        function
        | InvalidStandingKey(key, reason) -> $"standing subscription '{key}': {reason}"
        | DuplicateStandingKey key -> $"standing subscription '{key}' is declared more than once"
        | DuplicatePrincipal principal -> $"standing-subscription principal '{principal}' is declared more than once"
        | UndeclaredPrincipal(subscription, principal) ->
            $"standing subscription '{subscription}' runs as principal '{principal}', which the deployment does not declare"
        | PrincipalUnresolved(subscription, principal, missing) ->
            $"standing subscription '{subscription}': principal '{principal}' does not resolve — it lacks {missing}"
        | PrincipalScopeNotPersistent(subscription, principal, scopeId) ->
            $"standing subscription '{subscription}': principal '{principal}' resolves to scope '{scopeId}', whose data does not persist — it lacks a persistent scope"
        | StandingTemplateMissing(subscription, templateId, scopeId) ->
            $"standing subscription '{subscription}': template '{templateId}' does not exist at scope '{scopeId}' — seed it there before the subscription is declared"
        | InvalidStandingSubscription(subscription, error) ->
            $"standing subscription '{subscription}': {SubscriptionError.toMessage error}"
        | NoPrincipalResolver ->
            "standing subscriptions were declared, but this deployment composes no DeclaredPrincipalResolver — compose the server through the platform, whose scope-resolution registration provides it"
        | StandingLedgerUnavailable reason -> $"the standing-subscription ledger is unavailable: {reason}"
        | StandingSeedFailed(subscription, reason) ->
            $"standing subscription '{subscription}' could not be seeded: {reason}"

    /// The grant a scope-resolution refusal says the principal lacks.
    let missingGrant =
        function
        | NotAuthenticated -> "an identity the deployment's scope resolver accepts"
        | NoActiveTeam -> "an active team"
        | NotTeamMember teamId -> $"membership of team '{teamId}'"
        | ScopeResolutionFailed message -> $"a resolution (the scope resolver failed: {message})"

/// The host's start was refused; carries every refusal at once.
exception StandingSubscriptionsRefused of refusals: StandingSubscriptionRefusal list with
    override this.Message =
        let lines =
            this.refusals
            |> List.map (StandingSubscriptionRefusal.describe >> sprintf "  - %s")
            |> String.concat "\n"

        $"ToolUp.Reporting: standing report subscriptions refused startup:\n{lines}"

/// What a seeding did.
type StandingSeedReport = {
    /// Rows seeded (created or updated), as (scope id, subscription id).
    Seeded: (string * SubscriptionId) list
    /// Rows retired, as (scope id, subscription id).
    Retired: (string * SubscriptionId) list
}

/// A declaration that passed every check, ready to write.
type private CheckedStanding = {
    Declaration: StandingReportSubscription
    Principal: StandingSubscriptionPrincipal
    Scope: ResolvedScope
}

let private principalUser (principal: StandingSubscriptionPrincipal) : Auth.AuthenticatedUser = {
    UserId = principal.UserId
    DisplayName = principal.DisplayName
    Email = None
    TenantId = None
    Roles = []
    DirectoryRoles = []
    Admission = Auth.PrincipalAdmission.Member
}

let private oks (results: Result<'a, 'e> seq) : 'a list =
    results
    |> Seq.choose (function
        | Ok value -> Some value
        | Error _ -> None)
    |> List.ofSeq

let private errors (results: Result<'a, 'e> seq) : 'e list =
    results
    |> Seq.choose (function
        | Error e -> Some e
        | Ok _ -> None)
    |> List.ofSeq

/// The refusals a declaration set earns before anything is resolved.
let private shapeRefusals (standing: StandingSubscriptions) : StandingSubscriptionRefusal list =
    let duplicates (keys: string list) =
        keys |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

    let declared = standing.Principals |> List.map _.Name |> Set.ofList

    [
        for d in standing.Subscriptions do
            match StandingReportSubscription.validateKey d.Key with
            | Ok() -> ()
            | Error reason -> InvalidStandingKey(d.Key, reason)
        yield!
            duplicates (standing.Subscriptions |> List.map _.Key)
            |> List.map DuplicateStandingKey
        yield!
            duplicates (standing.Principals |> List.map _.Name)
            |> List.map DuplicatePrincipal
        for d in standing.Subscriptions do
            if not (declared.Contains d.Principal) then
                UndeclaredPrincipal(d.Key, d.Principal)
    ]

/// Check one declaration under its principal's resolution. Writes nothing.
let private checkOne
    (deps: StandingSubscriptionDeps)
    (principal: StandingSubscriptionPrincipal)
    (resolved: Result<ResolvedScope, ScopeResolutionError>)
    (d: StandingReportSubscription)
    : Async<Result<CheckedStanding, StandingSubscriptionRefusal>> =
    async {
        match resolved with
        | Error e -> return Error(PrincipalUnresolved(d.Key, d.Principal, StandingSubscriptionRefusal.missingGrant e))
        | Ok scope ->
            match scope.Storage with
            | Some storage when storage.Persist ->
                let! template = deps.Templates.Get(scope.ScopeId, d.TemplateId)

                match template with
                | None -> return Error(StandingTemplateMissing(d.Key, d.TemplateId, scope.ScopeId))
                | Some _ ->
                    let validated =
                        ReportSubscriptionStore.validate
                            deps.Api.Producers
                            scope.ScopeId
                            (StandingReportSubscription.subscriptionId d.Key)
                            principal.UserId
                            DateTimeOffset.UtcNow
                            NeverRun
                            (StandingReportSubscription.toRequest d)

                    return
                        match validated with
                        | Error e -> Error(InvalidStandingSubscription(d.Key, e))
                        | Ok _ ->
                            Ok {
                                Declaration = d
                                Principal = principal
                                Scope = scope
                            }
            | _ -> return Error(PrincipalScopeNotPersistent(d.Key, d.Principal, scope.ScopeId))
    }

/// Check every declaration, resolving each principal once. Writes nothing.
let private checkStanding
    (resolver: DeclaredPrincipalResolver option)
    (deps: StandingSubscriptionDeps)
    (standing: StandingSubscriptions)
    : Async<Result<CheckedStanding list, StandingSubscriptionRefusal list>> =
    async {
        match shapeRefusals standing, resolver with
        | (_ :: _ as refusals), _ -> return Error refusals
        | [], _ when List.isEmpty standing.Subscriptions -> return Ok []
        | [], None -> return Error [ NoPrincipalResolver ]
        | [], Some resolver ->
            let used = standing.Subscriptions |> List.map _.Principal |> Set.ofList

            let! resolutions =
                standing.Principals
                |> List.filter (fun p -> used.Contains p.Name)
                |> List.map (fun p -> async {
                    let! resolved = resolver.Resolve(principalUser p)
                    return p.Name, (p, resolved)
                })
                |> Async.Sequential

            let resolutions = Map.ofArray resolutions

            let! checkedRows =
                standing.Subscriptions
                |> List.map (fun d ->
                    let principal, resolved = resolutions[d.Principal]
                    checkOne deps principal resolved d)
                |> Async.Sequential

            return
                match errors checkedRows with
                | [] -> Ok(oks checkedRows)
                | refusals -> Error refusals
    }

/// Seed a deployment's standing subscriptions: check every declaration
/// (writing nothing if any is refused), seed each row under its principal's
/// resolved scope, retire every previously seeded row no declaration now
/// accounts for, and record where the rows now stand. `resolver` is the
/// platform's `DeclaredPrincipalResolver`; `None` refuses any declaration.
let seedStandingSubscriptions
    (resolver: DeclaredPrincipalResolver option)
    (deps: StandingSubscriptionDeps)
    (standing: StandingSubscriptions)
    : Async<Result<StandingSeedReport, StandingSubscriptionRefusal list>> =
    async {
        match! checkStanding resolver deps standing with
        | Error refusals -> return Error refusals
        | Ok checkedRows ->
            match! deps.Ledger.Read() with
            | Error reason -> return Error [ StandingLedgerUnavailable reason ]
            | Ok previous ->
                let! seeded =
                    checkedRows
                    |> List.map (fun c -> async {
                        let key = c.Declaration.Key

                        let! result =
                            ReportSubscriptionApiHandler.seedStanding
                                deps.Api
                                c.Principal.UserId
                                c.Scope
                                (StandingReportSubscription.subscriptionId key)
                                (StandingReportSubscription.toRequest c.Declaration)

                        return
                            match result with
                            | Ok row -> Ok(key, row.ScopeId)
                            | Error e -> Error(StandingSeedFailed(key, SubscriptionError.toMessage e))
                    })
                    |> Async.Sequential

                let seededRows = Set.ofList (oks seeded)

                // A previously seeded row is retired when no declaration
                // accounts for it now: its key is no longer declared, or its
                // declaration was seeded into another scope this time. A
                // declared key whose seeding failed keeps the rows it had.
                let failedKeys =
                    checkedRows
                    |> List.map _.Declaration.Key
                    |> List.filter (fun key -> not (seededRows |> Set.exists (fun (k, _) -> k = key)))
                    |> Set.ofList

                let toRetire =
                    previous
                    |> Set.filter (fun ((key, _) as row) -> not (seededRows.Contains row || failedKeys.Contains key))

                let! retired =
                    toRetire
                    |> Set.toList
                    |> List.map (fun (key, scopeId) -> async {
                        let api = ReportSubscriptionApiHandler.create deps.Api "_platform" scopeId

                        match! api.DeleteSubscription(StandingReportSubscription.subscriptionId key) with
                        | Ok()
                        | Error(SubscriptionNotFound _) -> return Ok(key, scopeId)
                        | Error e -> return Error(StandingSeedFailed(key, SubscriptionError.toMessage e))
                    })
                    |> Async.Sequential

                let retiredRows = Set.ofList (oks retired)

                // A row whose retirement failed stays in the ledger, so the
                // next start retires it.
                let! written = deps.Ledger.Write(Set.union seededRows (Set.difference previous retiredRows))

                let refusals = [
                    yield! errors seeded
                    yield! errors retired
                    match written with
                    | Ok() -> ()
                    | Error reason -> StandingLedgerUnavailable reason
                ]

                let rowId (key, scopeId) =
                    scopeId, StandingReportSubscription.subscriptionId key

                return
                    match refusals with
                    | [] ->
                        Ok {
                            Seeded = seededRows |> Set.toList |> List.map rowId
                            Retired = retiredRows |> Set.toList |> List.map rowId
                        }
                    | refusals -> Error refusals
    }

/// Compose a deployment's standing subscriptions, alongside — never instead
/// of — the request-created ones `withReportSubscriptions` serves. Returns
/// the startup service the composition root registers; at start it seeds
/// through the platform's `DeclaredPrincipalResolver`, and a refusal FAILS
/// the start (`StandingSubscriptionsRefused`) naming every subscription and
/// what it lacks:
///
/// ```fsharp skip=fragment
/// services.AddSingleton<IHostedService>(
///     Func<IServiceProvider, IHostedService>(
///         ReportingCompose.withStandingSubscriptions standing standingDeps))
/// ```
///
/// `deps.Api` must be the deps the subscription job handler's scheduler
/// was built with, so a seeded job has a handler to dispatch to.
let withStandingSubscriptions
    (standing: StandingSubscriptions)
    (deps: StandingSubscriptionDeps)
    : IServiceProvider -> Microsoft.Extensions.Hosting.IHostedService =
    fun sp ->
        { new Microsoft.Extensions.Hosting.IHostedService with
            member _.StartAsync _ =
                let resolver =
                    match sp.GetService(typeof<DeclaredPrincipalResolver>) with
                    | :? DeclaredPrincipalResolver as r -> Some r
                    | _ -> None

                async {
                    match! seedStandingSubscriptions resolver deps standing with
                    | Ok _ -> ()
                    | Error refusals -> return raise (StandingSubscriptionsRefused refusals)
                }
                |> Async.StartAsTask
                :> System.Threading.Tasks.Task

            member _.StopAsync _ =
                System.Threading.Tasks.Task.CompletedTask
        }