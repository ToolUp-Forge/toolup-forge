module ToolUp.Reporting.ReportingCompose

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
/// factory. Both are returned rather than registered here because
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
    : IJobHandler * (string -> string -> IReportSubscriptionApi) =
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

    ReportSubscriptionJobHandler.create deps, ReportSubscriptionApiHandler.create apiDeps