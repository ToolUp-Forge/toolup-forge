namespace ToolUp.Platform

open System

// ─── IJobScheduler ───────────────────────────────────────────────
//
// Top-level orchestrator for the background-job substrate (Phase 9b).
// Combines the scheduling policy (when does a job run?) with the
// dispatch policy (which handler runs it?). Modules and admin UIs
// see this surface; `IJobStore` and `IJobHandler` are dependencies
// the scheduler resolves internally.
//
// **One scheduler per process.** The SDK ships an in-process default
// (`InProcessJobScheduler`) registered as a singleton. Distributed
// companions (Akka, Orleans, Hangfire) replace this singleton with
// their own implementation; the interface below is what they bind
// to. The in-process default ticks once per minute — `Second`
// precision is rejected at registration via
// `ScheduleError.PrecisionUnsupported`.
//
// **Phase 9c portability rules** (all six honoured below):
//
//   1. Identity by value. Every method takes / returns `JobId` (Guid)
//      and `ScopeId` (string) — no live handles.
//   2. Async at every boundary. Every method returns `Async<_>`.
//   3. Retry / supervision as data. `RetryPolicy` is a record on the
//      `JobDefinition` the caller supplies; the scheduler reads it
//      on every retry decision. No `OnFailure` callbacks.
//   4. Stateless handlers between invocations. `IJobHandler.Execute`
//      receives state via `JobContext`.
//   5. No cross-shard ordering promises. `JobDefinition.ShardKey` is
//      an affinity hint; the in-process default ignores it (single
//      process — every job runs in the same scheduler).
//   6. Precision at the lower bound. `Schedule` rejects
//      `JobPrecision.Second` with
//      `PrecisionUnsupported(Second, [Minute])` when the
//      implementation is the in-process default.

// `JobRegistration` lives in the shared layer (`Shared/JobTypes.fs`)
// so the Fable admin UI and module client code can construct
// registrations and submit them through the `JobApi` Fable.Remoting
// surface.

type IJobScheduler =
    /// Register a handler under `name`. Idempotent — re-registering
    /// the same name with a different implementation overwrites the
    /// previous binding. Modules typically call this once at compose
    /// time (`ServerModule.Server.fs`); the SDK does NOT call it per
    /// request.
    abstract RegisterHandler: name: string * handler: IJobHandler -> unit

    /// Async-boundary companion to `RegisterHandler` (portability rule
    /// 2). A distributed scheduler companion (Akka / Orleans / Hangfire)
    /// that registers handlers *over the network* — rather than purely
    /// at compose time in one process — needs an awaitable, fallible
    /// registration seam: a synchronous `RegisterHandler` would force it
    /// either to block (a rule-2 violation) or to break this interface
    /// later, and retrofitting rule 2 is destructive (see
    /// `docs/platform/portability-rules.md`). Adding the overload now,
    /// while the only implementation is in-process, keeps the seam
    /// available before the first distributed companion needs it.
    ///
    /// The in-process default implements this by performing the same
    /// registration as `RegisterHandler` and returning `Ok ()` —
    /// compose-time registration stays synchronous and free; nothing is
    /// forced through the async path that doesn't need it. `Error`
    /// carries an implementation-specific message (e.g. a cluster
    /// gossip / quorum timeout).
    abstract RegisterHandlerAsync: name: string * handler: IJobHandler -> Async<Result<unit, string>>

    /// Submit a new job. Returns the assigned `JobId` on success.
    ///
    /// Validation chain (in order):
    ///   1. `Trigger.CronTrigger` parses against the supported cron
    ///      subset (`*`, integers, commas, `*/N`).
    ///   2. `Handler` is a registered name.
    ///   3. `Precision` is supported by this implementation.
    /// First failure short-circuits with the appropriate
    /// `ScheduleError` case.
    ///
    /// Idempotency: if `Idempotency` is `Some` and a live job with
    /// the same key exists in the same scope within the TTL,
    /// returns the existing `JobId` without registering a new
    /// definition (the duplicate registration is a no-op). The
    /// caller cannot tell from the return value whether the job is
    /// brand-new or recovered — by design, since the contract
    /// guarantees "submitting twice = the job runs once".
    abstract Schedule: registration: JobRegistration -> Async<Result<JobId, ScheduleError>>

    /// Cancel a job — sets `Status = Cancelled`. The scheduler skips
    /// it on subsequent ticks; no further runs are dispatched. Does
    /// not delete the record (history remains for audit).
    /// Returns `unit` on success; idempotent — cancelling an already-
    /// cancelled or unknown job is a no-op.
    abstract Cancel: scopeId: string * jobId: JobId -> Async<unit>

    /// Disable a job — sets `Status = Disabled`. Stops dispatch
    /// without losing the schedule. Admins re-enable via a separate
    /// `Enable` call.
    abstract Disable: scopeId: string * jobId: JobId -> Async<unit>

    /// Re-enable a disabled job. No-op when the job is already
    /// `Active`. Reactivates `NextRunAt` recomputation on the next
    /// scheduler tick (cron-triggered) or arms it for the next
    /// matching `OnEvent` (event-triggered).
    abstract Enable: scopeId: string * jobId: JobId -> Async<unit>

    /// Read job state by id. Returns `None` for unknown ids — does
    /// not throw. Used by the admin UI and by handlers that need to
    /// inspect their own definition.
    abstract Get: scopeId: string * jobId: JobId -> Async<JobDefinition option>

    /// Enumerate every job in the scope, ordered by `CreatedAt`
    /// ascending. Returns all states including `Cancelled`.
    abstract ListJobs: scopeId: string -> Async<JobDefinition list>

    /// Read the most recent `count` run history rows for a job.
    /// Newest first. Powers the admin UI's per-job history panel.
    abstract GetRecentRuns: scopeId: string * jobId: JobId * count: int -> Async<JobRun list>

    /// Fire a job immediately, regardless of its `Trigger`. The
    /// scheduler enqueues an attempt with
    /// `TriggerSource.ScheduledManually byUserId`. Cron-triggered
    /// jobs continue on their schedule; event-triggered jobs
    /// continue to react to events; manual fires are additive.
    /// Returns `Error` when the job is unknown or its `Status` is
    /// `Cancelled`.
    abstract TriggerOnce: scopeId: string * jobId: JobId * byUserId: string -> Async<Result<unit, string>>

    /// Notify the scheduler that an event was written to
    /// `IEventStore`. The scheduler matches it against every
    /// registered `OnEvent` job in the same scope and dispatches
    /// matching ones immediately. Idempotency: an event redelivered
    /// to the scheduler will dispatch the matching jobs again — the
    /// caller is responsible for de-duplicating events upstream
    /// (the in-process scheduler is wired via `HookedEventStore` so
    /// a single write maps to a single notify).
    abstract NotifyEventWritten: scopeId: string * eventType: string * eventId: System.Guid -> Async<unit>

// ─── Phase 9b.B — compose-time scheduled-job declarations ─────────────
//
// A `ServerModule` or `ServerApp` carries declarations of recurring /
// event-driven jobs it wants the SDK to register + schedule for it at
// compose time. The compose pipeline (`ComposeJobs.registerScheduledJobDeclarations`)
// iterates these after the `IJobScheduler` singleton is built and
// applies the same `RegisterHandler` + `Schedule` shape the SDK already
// uses for its own internal handlers (`DataIngestionJobHandler`,
// `OAuthRefreshJobHandler`, etc.) — without forcing module authors to
// resolve `IJobScheduler` themselves after `Build`.
//
// Portability: this is a server-side declaration type only — it
// carries an `IJobHandler` instance, not a value, so it does not cross
// the Fable boundary. The persisted shape on disk is still
// `JobDefinition` per portability rule 1.

/// Compose-time declaration of a job the SDK should register +
/// schedule for the declaring module / app. Mirrors the fields of
/// `JobRegistration` minus the per-scope `ScopeId` (the compose
/// pipeline fans out across `Scopes` — empty list defaults to
/// `["_platform"]`). Construct via `ScheduledJobDeclaration.create`
/// + the fluent `with*` helpers; the record fields are public so
/// callers needing the full shape can also construct directly.
type ScheduledJobDeclaration = {
    /// Logical handler name. Passed verbatim to
    /// `IJobScheduler.RegisterHandler` and stamped onto the persisted
    /// `JobDefinition.Handler`. Modules should namespace this against
    /// their module id (`"sales.daily-rollup"`, not just `"daily-rollup"`)
    /// so cross-module name clashes are caught at registration time.
    HandlerName: string
    /// The handler implementation registered against `HandlerName`.
    /// Stateless between invocations per portability rule 4.
    Handler: IJobHandler
    /// When the job runs — `CronTrigger` / `OnEvent` / `Manual`.
    Trigger: Trigger
    /// Pre-serialised payload string handed to every dispatch
    /// attempt. Empty for parameter-less jobs (the SDK's existing
    /// internal handlers use empty payloads). Handlers serialise via
    /// `ToolUp.Remoting.Json.SystemTextJson.FableConverters` to keep the persisted
    /// shape Fable-compatible.
    Payload: string
    /// Scopes to schedule the job under. Empty defaults to
    /// `["_platform"]` — the conventional reserved scope the SDK's
    /// own internal handlers run under. Modules that want per-tenant
    /// fan-out at compose time enumerate active scopes explicitly
    /// (the SDK does not yet enumerate tenants automatically; a
    /// dynamic-scope follow-up would replace this with a thunk).
    Scopes: string list
    /// Idempotency token controlling re-registration on process
    /// restart. `None` (the default) auto-builds a stable key from
    /// `("module-" + HandlerName + "-" + scopeId)` with a one-year
    /// TTL, so a restart with the same declaration is a no-op (the
    /// existing job definition is returned). Override only when the
    /// caller needs a non-default TTL or a custom key shape.
    Idempotency: IdempotencyKey option
    /// Per-job retry policy. Defaults to `JobRetryPolicy.defaults`
    /// (3 attempts, 30s/30min backoff, no dead-letter destination)
    /// — sufficient for most analytical jobs that fail because of
    /// transient resource pressure and recover inside the half-hour
    /// window.
    RetryPolicy: JobRetryPolicy
    /// Affinity hint for distributed scheduler companions
    /// (portability rule 5). The in-process default ignores it; an
    /// Akka cluster-sharded scheduler routes jobs with the same
    /// `ShardKey` to the same node so they execute serially.
    ShardKey: string option
    /// Scheduling-granularity contract (portability rule 6). The
    /// in-process default supports `Minute` only; `Second` is
    /// rejected at `Schedule` with `ScheduleError.PrecisionUnsupported`.
    Precision: JobPrecision
    /// Free-form metadata the scheduler ignores; admin UIs and
    /// downstream telemetry can read it.
    Tags: Map<string, string>
}

module ScheduledJobDeclaration =
    /// Default constructor — `Minute` precision, no payload, empty
    /// `Scopes` (compose defaults to `["_platform"]`), no
    /// idempotency override (compose auto-builds a stable per-scope
    /// key), default retry policy, no shard-key, no tags. Refine via
    /// the `with*` helpers below.
    let create (handlerName: string) (handler: IJobHandler) (trigger: Trigger) : ScheduledJobDeclaration = {
        HandlerName = handlerName
        Handler = handler
        Trigger = trigger
        Payload = ""
        Scopes = []
        Idempotency = None
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = None
        Precision = JobPrecision.Minute
        Tags = Map.empty
    }

    let withPayload (payload: string) (d: ScheduledJobDeclaration) : ScheduledJobDeclaration = {
        d with
            Payload = payload
    }

    let withScopes (scopes: string list) (d: ScheduledJobDeclaration) : ScheduledJobDeclaration = {
        d with
            Scopes = scopes
    }

    let withIdempotency (key: IdempotencyKey) (d: ScheduledJobDeclaration) : ScheduledJobDeclaration = {
        d with
            Idempotency = Some key
    }

    let withRetryPolicy (policy: JobRetryPolicy) (d: ScheduledJobDeclaration) : ScheduledJobDeclaration = {
        d with
            RetryPolicy = policy
    }

    let withShardKey (shardKey: string) (d: ScheduledJobDeclaration) : ScheduledJobDeclaration = {
        d with
            ShardKey = Some shardKey
    }

    let withPrecision (precision: JobPrecision) (d: ScheduledJobDeclaration) : ScheduledJobDeclaration = {
        d with
            Precision = precision
    }

    let withTags (tags: Map<string, string>) (d: ScheduledJobDeclaration) : ScheduledJobDeclaration = {
        d with
            Tags = tags
    }

    /// Register + schedule ONE declaration against a live scheduler —
    /// the per-declaration body `ComposeJobs.registerScheduledJobDeclarations`
    /// runs at compose time, lifted here (Phase 623) so the DI-deferred
    /// form below reaches the identical registration path instead of
    /// re-implementing it. Behaviour is unchanged: `RegisterHandler`
    /// (idempotent at the scheduler), `Scopes = []` defaulted to
    /// `["_platform"]`, a stable `module-{handlerName}-{scopeId}`
    /// idempotency key (one-year TTL) so a restart returns the existing
    /// `JobId`, and a `Warn` — never a throw — on a failed `Schedule`.
    let registerWith (scheduler: IJobScheduler) (logger: ILogger) (declaration: ScheduledJobDeclaration) : unit =
        scheduler.RegisterHandler(declaration.HandlerName, declaration.Handler)

        let scopes =
            if List.isEmpty declaration.Scopes then
                [ "_platform" ]
            else
                declaration.Scopes

        for scopeId in scopes do
            let idempotency =
                match declaration.Idempotency with
                | Some key -> Some key
                | None ->
                    Some {
                        Key = sprintf "module-%s-%s" declaration.HandlerName scopeId
                        TtlSeconds = 60 * 60 * 24 * 365
                    }

            let registration: JobRegistration = {
                ScopeId = scopeId
                Handler = declaration.HandlerName
                Payload = declaration.Payload
                Trigger = declaration.Trigger
                Idempotency = idempotency
                RetryPolicy = declaration.RetryPolicy
                ShardKey = declaration.ShardKey
                Precision = declaration.Precision
                CreatedBy = "_platform"
                Tags = declaration.Tags |> Map.add "source" "compose-time"
            }

            let result = scheduler.Schedule registration |> Async.RunSynchronously

            match result with
            | Ok _ -> ()
            | Error err ->
                logger.Warn(
                    sprintf "[Phase 9b.B] Failed to schedule %s in scope %s: %A" declaration.HandlerName scopeId err
                )

// ─── Phase 623 — DI-deferred scheduled-job declarations ──────────────
//
// `ScheduledJobDeclaration.Handler` is a **constructed** `IJobHandler`,
// which a compose root can only supply when the handler's dependencies
// are already in hand. Companion composition is **lazy-DI**: the store,
// the scheduler, a metric registry and the like are only resolvable from
// the *built* `IServiceProvider`, long after the declaration list is
// assembled. Three in-tree companions (Phase 449 model-fit, Phase 9h.A
// DSR, Phase 563 fact-base coherence) each hand-rolled the same startup
// `IHostedService` to bridge that gap; Phase 623 makes the bridge
// substrate so the fourth (reactive fact recomputation) and every later
// one declare instead of hand-roll.
//
// **Additive by construction (GP 11).** `ScheduledJobDeclaration` is
// untouched — no field added, none retyped, no helper resigned — so every
// existing declaration, every direct record construction, and every
// `ComposeJobs.registerScheduledJobDeclarations` call compiles and
// behaves exactly as before. The deferred form is a *second, separate*
// declaration type that RESOLVES to the first; both converge on
// `ScheduledJobDeclaration.registerWith`, so a deferred job is registered
// and scheduled by the identical code path as an eager one.
//
// **Why not eager construction at end-of-compose?** It would work only
// for handlers whose dependencies are registered instances; anything
// resolved through a DI factory (the fact tier's `IFactStore` is built
// from `IBlobStorage` + `IEventStore` at first use) is not constructible
// at that point without forcing an early `BuildServiceProvider`, which
// duplicates singletons and is the documented ASP.NET Core anti-pattern.
// Deferring to `IHostedService.StartAsync` resolves from the *one* real
// container, after every companion has registered.

/// A `ScheduledJobDeclaration` whose handler (and any other
/// DI-dependent field) is produced from the built `IServiceProvider`
/// rather than supplied at compose time. Register with
/// `DeferredScheduledJobDeclaration.hostedService`.
type DeferredScheduledJobDeclaration = {
    /// Produce the concrete declaration from the built provider. Called
    /// once, at `IHostedService.StartAsync`, on the container every
    /// request-time consumer also resolves from.
    Resolve: IServiceProvider -> ScheduledJobDeclaration
}

module DeferredScheduledJobDeclaration =

    /// The general form — resolve the whole declaration from the provider.
    let create (resolve: IServiceProvider -> ScheduledJobDeclaration) : DeferredScheduledJobDeclaration = {
        Resolve = resolve
    }

    /// The common form — a fixed `handlerName` + `trigger` with only the
    /// handler resolved from DI. Refine the resolved declaration with the
    /// ordinary `ScheduledJobDeclaration.with*` helpers by composing them
    /// into `create` instead.
    let ofHandler
        (handlerName: string)
        (trigger: Trigger)
        (handler: IServiceProvider -> IJobHandler)
        : DeferredScheduledJobDeclaration =
        create (fun sp -> ScheduledJobDeclaration.create handlerName (handler sp) trigger)

    /// An `IHostedService` that, at startup, resolves each deferred
    /// declaration against the built provider and registers + schedules
    /// it through `ScheduledJobDeclaration.registerWith`. `label` names
    /// the composing feature in the diagnostic emitted when the
    /// deployment has no scheduler (`JobScheduler = NoJobScheduler`) —
    /// declarations are dead code there, which is a config mismatch, not
    /// a crash. An empty list registers nothing and logs nothing.
    let hostedService
        (label: string)
        (declarations: DeferredScheduledJobDeclaration list)
        (sp: IServiceProvider)
        : Microsoft.Extensions.Hosting.IHostedService =
        { new Microsoft.Extensions.Hosting.IHostedService with
            member _.StartAsync(_ct) =
                if not (List.isEmpty declarations) then
                    let logger = sp.GetService(typeof<ILogger>) :?> ILogger

                    match sp.GetService(typeof<IJobScheduler>) with
                    | :? IJobScheduler as scheduler ->
                        for deferred in declarations do
                            ScheduledJobDeclaration.registerWith scheduler logger (deferred.Resolve sp)
                    | _ ->
                        logger.Warn(
                            sprintf
                                "[Phase 623] %s declared %d deferred scheduled job(s) but JobScheduler = NoJobScheduler — none were registered. Pair with JobScheduler = InProcessJobScheduler (or a distributed scheduler companion)."
                                label
                                declarations.Length
                        )

                System.Threading.Tasks.Task.CompletedTask

            member _.StopAsync(_ct) =
                System.Threading.Tasks.Task.CompletedTask
        }