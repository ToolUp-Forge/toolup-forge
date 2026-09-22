module ToolUp.Scheduling.SchedulingCompose

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.Server
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.SchedulingApi
open ToolUp.Scheduling.SchedulingEvents
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.SchedulingApiHandler

// ─── Phase 20 — SchedulingServerApp composition root ─────────────
//
// `SchedulingServerApp` mirrors `AIServerApp` / `RAGServerApp` shape:
// it wraps a base `ServerApp` and adds a `with*` helper for every
// `ServerApp.with*` so consumers write a single fluent pipeline.
//
// **Required substrate**: `IEntityStore` + `IEventStore`. The compose
// step does NOT enforce `ServerConfig.EntityStore = EnabledEntityStore`
// — if the consumer forgets, the DI lookup for `IEntityStore` at
// first-call time throws `null reference`, which the SDK's error
// handler propagates as `500`. Documented requirement; explicit
// enforcement is a follow-up.
//
// **Three entity registrations** flow through:
//   * `BookableResource` — index on `ResourceType`
//   * `Booking` — index on `ResourceId` / `Status` / compound
//     `(ResourceId, StartUtc-ticks)`
//   * `AvailabilityException` — index on `ResourceId`
//
// **API handler**: a single `ISchedulingApi` mounted via
// `makeApi (fun ctx -> SchedulingApiHandler.schedulingApi scheduler ctx)`.
// The handler resolves `scopeId`/`userId` from
// `HttpContext.Items` (populated by `ScopeResolutionMiddleware`).

/// Phase 20a — the cron the polling bridges are pulled on unless the
/// deployment overrides it with `withCalendarPollTrigger`. Fifteen
/// minutes, matching `BridgeCapabilities.pollingOnly`'s declared floor.
[<Literal>]
let DefaultCalendarPollCron = "*/15 * * * *"

/// Record form of compose arguments. Wraps a base `ServerApp` and
/// carries the scheduler-specific `RemindersEnabled` flag (a
/// follow-up; see `withReminders`).
type SchedulingServerApp = {
    Base: ServerApp
    /// Reminder fusion is opt-in (default `false`). When `true` the
    /// compose step would register a `BookingReminderJobHandler`
    /// against `IJobScheduler` — Phase 20 follow-up; currently no-op.
    RemindersEnabled: bool
    /// Phase 20a — composed calendar bridges, in compose order, at most
    /// one per `ICalendarBridge.Kind`. Empty (the default) means the
    /// deployment mirrors nothing: no link entities are registered, no
    /// sync singleton is built and no job is scheduled, so `run` is
    /// byte-for-byte what it was before the seam landed (GP 13).
    CalendarBridges: ICalendarBridge list
    /// Phase 20a — how often polling bridges are pulled. `None` (the
    /// default) uses a fifteen-minute cron, which matches
    /// `BridgeCapabilities.pollingOnly`'s declared floor. Ignored when
    /// no composed bridge is polling-only.
    CalendarPollTrigger: Trigger option
}

module SchedulingServerApp =

    let create () : SchedulingServerApp = {
        Base = ServerApp.empty
        RemindersEnabled = false
        CalendarBridges = []
        CalendarPollTrigger = None
    }

    // ─── Delegating helpers (mirror every `ServerApp.with*`) ─────

    let withConfig (c: ServerConfig) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withConfig c app.Base
    }

    let withAuth (a: IAuthProvider) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withAuth a app.Base
    }

    let withLogger (l: ILogger) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withLogger l app.Base
    }

    let withStorage (s: IBlobStorage) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withStorage s app.Base
    }

    let withNotifications (n: INotificationChannel) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withNotifications n app.Base
    }

    let withTransactionalSink (sink: INotificationSink) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withTransactionalSink sink app.Base
    }

    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withHealthCheck check app.Base
    }

    let withConfigValidator
        (validator: ConfigValidation.IConfigValidator)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withConfigValidator validator app.Base
        }

    let withEncryptedBlobStorage
        (resolver: IBlobEncryptionKeyResolver)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withEncryptedBlobStorage resolver app.Base
        }

    let withEntity<'T>
        (registration: EntityTypes.EntityRegistration<'T>)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withEntity registration app.Base
        }

    /// Phase 9b.B — declare a composition-root-owned background job.
    /// Delegates to `ServerApp.withJobHandler`.
    let withJobHandler
        (handlerName: string, handler: IJobHandler, trigger: Trigger)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withJobHandler (handlerName, handler, trigger) app.Base
        }

    /// Phase 9b.B — declare a composition-root-owned background job
    /// with full control over every `JobRegistration` knob. Delegates
    /// to `ServerApp.withScheduledJob`.
    let withScheduledJob (declaration: ScheduledJobDeclaration) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withScheduledJob declaration app.Base
    }

    /// Phase 9b.A — opt into back-fill of `OnEvent` jobs on detected
    /// scheduler tick drift. Delegates to `ServerApp.withBackfillMissedTicks`.
    let withBackfillMissedTicks (enabled: bool) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withBackfillMissedTicks enabled app.Base
    }

    /// Phase 598 — opt into the event-trigger catch-up watermark.
    /// Delegates to `ServerApp.withEventTriggerCatchUp`.
    let withEventTriggerCatchUp (enabled: bool) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withEventTriggerCatchUp enabled app.Base
    }

    /// Phase 9t — audit-write failure policy. Delegates to
    /// `ServerApp.withAuditFailurePolicy`.
    let withAuditFailurePolicy (policy: AuditFailurePolicy) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withAuditFailurePolicy policy app.Base
    }

    /// Phase 599 — opt into the entity-write outbox. Delegates to
    /// `ServerApp.withEntityOutbox`.
    let withEntityOutbox (enabled: bool) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withEntityOutbox enabled app.Base
    }

    let withPreMiddleware
        (f: IApplicationBuilder -> IApplicationBuilder)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withPreMiddleware f app.Base
        }

    let withPostMiddleware
        (f: IApplicationBuilder -> IApplicationBuilder)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withPostMiddleware f app.Base
        }

    let addModule (m: ServerModule) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.addModule m app.Base
    }

    let addModules (modules: ServerModule list) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.addModules modules app.Base
    }

    // ─── Scheduling-specific helpers ──────────────────────────────

    /// Opt into reminder fusion. Reserved for a Phase 20 follow-up;
    /// currently a no-op so the call site can ship without breaking
    /// when the feature lands.
    let withReminders (app: SchedulingServerApp) : SchedulingServerApp = { app with RemindersEnabled = true }

    /// Phase 20a — compose one calendar bridge, so bookings on a linked
    /// resource round-trip with an external calendar.
    ///
    /// Several bridges may be composed, keyed by `ICalendarBridge.Kind`
    /// — a deployment can mirror one resource into CalDAV and another
    /// into a vendor calendar. A SECOND bridge under a kind already
    /// composed is refused here, naming both implementations and the
    /// contested kind: the kind is persisted on every link, so two
    /// bridges claiming one kind would make a stored link ambiguous.
    /// (Same rule, same reason, as the one-per-`Kind` transactional
    /// notification-sink registry.)
    let withCalendarBridge (bridge: ICalendarBridge) (app: SchedulingServerApp) : SchedulingServerApp =
        match app.CalendarBridges |> List.tryFind (fun b -> b.Kind = bridge.Kind) with
        | Some existing ->
            failwithf
                "Calendar bridge kind '%s' is already composed by %s; %s cannot also claim it. A link records its kind, so one kind must name one bridge."
                bridge.Kind
                (existing.GetType().Name)
                (bridge.GetType().Name)
        | None -> {
            app with
                CalendarBridges = app.CalendarBridges @ [ bridge ]
          }

    /// Phase 20a — override the cron trigger the polling bridges are
    /// pulled on. The supplied trigger is used verbatim; a deployment
    /// polling faster than a provider's declared
    /// `BridgeCapabilities.MinimumPollInterval` is the operator's call
    /// to make, and the floor is declared so it can be made knowingly.
    let withCalendarPollTrigger (trigger: Trigger) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            CalendarPollTrigger = Some trigger
    }

    /// Drive the final composition. Registers the three booking
    /// entity types, wires `IBookingScheduler` into DI, mounts the
    /// `ISchedulingApi` handler, and delegates to `ServerApp.run`.
    let run (app: SchedulingServerApp) : int =
        // 1. Register the three entity types via the base's
        //    `withEntity` pipeline.
        let baseEntities =
            app.Base
            |> ServerApp.withEntity BookingScheduler.resourceRegistration
            |> ServerApp.withEntity BookingScheduler.bookingRegistration
            |> ServerApp.withEntity BookingScheduler.exceptionRegistration

        // 1a. Phase 20a — the calendar-sync additions, and ONLY when a
        //     bridge was composed: two more entity types, the sync
        //     singleton, the three push jobs and (for polling bridges)
        //     the poll job. With no bridge composed this whole block is
        //     skipped and `withEntities` is `baseEntities` (GP 13).
        let calendarComposed = not (List.isEmpty app.CalendarBridges)

        let withEntities =
            if not calendarComposed then
                baseEntities
            else
                baseEntities
                |> ServerApp.withEntity CalendarSync.calendarLinkRegistration
                |> ServerApp.withEntity CalendarSync.calendarEventLinkRegistration

        // 2. DI registration for the default `IBookingScheduler`, plus —
        //    when a bridge is composed — the calendar sync singleton and
        //    the jobs that drive it. Both the push and the poll handler
        //    resolve their dependencies from the BUILT provider through
        //    `DeferredScheduledJobDeclaration` rather than closing over
        //    instances built here: the sync engine is a singleton the
        //    provider owns, and a handler holding its own copy would be
        //    carrying state between invocations (portability rule 4).
        let schedulingServiceConfig (services: IServiceCollection) =
            let services =
                services.AddSingleton<IBookingScheduler>(
                    System.Func<System.IServiceProvider, IBookingScheduler>(fun sp ->
                        let entityStore = sp.GetService(typeof<IEntityStore>) :?> IEntityStore
                        let eventStore = sp.GetService(typeof<IEventStore>) :?> IEventStore
                        BookingScheduler.BookingScheduler(entityStore, eventStore) :> IBookingScheduler)
                )

            if not calendarComposed then
                services
            else
                let bridges = app.CalendarBridges

                services.AddSingleton<CalendarSync.ICalendarSync>(
                    System.Func<System.IServiceProvider, CalendarSync.ICalendarSync>(fun sp ->
                        let entityStore = sp.GetService(typeof<IEntityStore>) :?> IEntityStore
                        let scheduler = sp.GetService(typeof<IBookingScheduler>) :?> IBookingScheduler

                        CalendarSync.CalendarSync(entityStore, scheduler, bridges) :> CalendarSync.ICalendarSync)
                )
                |> ignore

                let pushDeclaration (eventType: string) =
                    DeferredScheduledJobDeclaration.ofHandler
                        (CalendarSync.PushHandlerPrefix + eventType)
                        (Trigger.OnEvent eventType)
                        (fun sp ->
                            let sync =
                                sp.GetService(typeof<CalendarSync.ICalendarSync>) :?> CalendarSync.ICalendarSync

                            let scheduler = sp.GetService(typeof<IBookingScheduler>) :?> IBookingScheduler
                            let eventStore = sp.GetService(typeof<IEventStore>) :?> IEventStore

                            let logger =
                                match sp.GetService(typeof<ILogger>) with
                                | :? ILogger as l -> Some l
                                | _ -> None

                            CalendarSync.CalendarPushJobHandler(sync, scheduler, eventStore, logger) :> IJobHandler)

                // A bridge that pushes change notifications is driven by
                // its webhook route; only a polling bridge needs the cron.
                let needsPoll =
                    bridges |> List.exists (fun b -> not b.Capabilities.SupportsWebhooks)

                let pollTrigger =
                    app.CalendarPollTrigger
                    |> Option.defaultValue (Trigger.CronTrigger DefaultCalendarPollCron)

                let declarations = [
                    pushDeclaration BookingCreated
                    pushDeclaration BookingRescheduled
                    pushDeclaration BookingCancelled

                    if needsPoll then
                        DeferredScheduledJobDeclaration.ofHandler CalendarSync.PollHandlerName pollTrigger (fun sp ->
                            let sync =
                                sp.GetService(typeof<CalendarSync.ICalendarSync>) :?> CalendarSync.ICalendarSync

                            let entityStore = sp.GetService(typeof<IEntityStore>) :?> IEntityStore
                            CalendarSync.CalendarPollJobHandler(sync, entityStore) :> IJobHandler)
                ]

                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                    System.Func<System.IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                        DeferredScheduledJobDeclaration.hostedService "Phase 20a calendar sync" declarations sp)
                )

        // 3. API handler. Resolves `IBookingScheduler` per request
        //    out of DI; the per-request `scopeId`/`userId` come from
        //    `HttpContext.Items` inside `schedulingApi`.
        let schedulingApiHandler =
            makeApi (fun (ctx: HttpContext) ->
                let scheduler =
                    ctx.RequestServices.GetService(typeof<IBookingScheduler>) :?> IBookingScheduler

                schedulingApi scheduler ctx)

        // 4. Merge into the base extensions.
        let baseExt = withEntities.Extensions

        let mergedExt: ComposeExtensions = {
            baseExt with
                Handlers = baseExt.Handlers @ [ schedulingApiHandler ]
                ServiceConfig =
                    match baseExt.ServiceConfig with
                    | None -> Some schedulingServiceConfig
                    | Some baseFn -> Some(fun s -> schedulingServiceConfig (baseFn s))
        }

        let final = {
            withEntities with
                Extensions = mergedExt
        }

        ServerApp.run final