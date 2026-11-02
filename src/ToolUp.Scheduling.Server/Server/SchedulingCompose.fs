module ToolUp.Scheduling.SchedulingCompose

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra.BlobEncryption
open ToolUp.Platform.Compose.RemotingHelpers
open ToolUp.Platform.Compose.Server
open ToolUp.Platform.Infra.IEntityStore
open ToolUp.Scheduling.SchedulingApi
open ToolUp.Scheduling.SchedulingEvents
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.SchedulingApiHandler
open ToolUp.Platform.Infra
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── The scheduling companion: `SchedulingOptions` + `withScheduling`
//
// Scheduling is a companion composed onto a `ServerApp`, not a record that
// wraps one. `SchedulingOptions` holds the scheduler-specific settings
// (reminders, calendar bridges, poll trigger); `withScheduling options`
// registers the companion with `ServerApp.withCompanion`, so it is applied
// once, when the deployment resolves, to the deployment as every builder
// left it. Every `ServerApp` builder stays reachable on either side of it.
//
// **Required substrate**: `IEntityStore` + `IEventStore`. The compose
// step does NOT enforce `ServerConfig.Stores.EntityStore = FeatureMode.Enabled`
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

/// The scheduling companion's identity: the slot `withScheduling` composes
/// under and the owner of its contributions.
let schedulingCompanion: ComponentId =
    ComponentId.forCompanionSlot "ToolUp.Scheduling"

/// What the scheduling companion composes.
type SchedulingOptions = {
    /// Reminder fusion is opt-in (default `false`). When `true` the
    /// compose step would register a `BookingReminderJobHandler`
    /// against `IJobScheduler` — Phase 20 follow-up; currently no-op.
    RemindersEnabled: bool
    /// Phase 20a — composed calendar bridges, in compose order, at most
    /// one per `ICalendarBridge.Kind`. Empty (the default) means the
    /// deployment mirrors nothing: no link entities are registered, no
    /// sync singleton is built and no job is scheduled, so the companion
    /// composes byte-for-byte what it did before the seam landed (GP 13).
    CalendarBridges: ICalendarBridge list
    /// Phase 20a — how often polling bridges are pulled. `None` (the
    /// default) uses a fifteen-minute cron, which matches
    /// `BridgeCapabilities.pollingOnly`'s declared floor. Ignored when
    /// no composed bridge is polling-only.
    CalendarPollTrigger: Trigger option
}

module SchedulingOptions =

    /// Reminders off, no calendar bridges, the default poll trigger.
    let create () : SchedulingOptions = {
        RemindersEnabled = false
        CalendarBridges = []
        CalendarPollTrigger = None
    }

    /// Opt into reminder fusion. Reserved for a Phase 20 follow-up;
    /// currently a no-op so the call site can ship without breaking
    /// when the feature lands.
    let withReminders (options: SchedulingOptions) : SchedulingOptions = { options with RemindersEnabled = true }

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
    let withCalendarBridge (bridge: ICalendarBridge) (options: SchedulingOptions) : SchedulingOptions =
        match options.CalendarBridges |> List.tryFind (fun b -> b.Kind = bridge.Kind) with
        | Some existing ->
            failwithf
                "Calendar bridge kind '%s' is already composed by %s; %s cannot also claim it. A link records its kind, so one kind must name one bridge."
                bridge.Kind
                (existing.GetType().Name)
                (bridge.GetType().Name)
        | None -> {
            options with
                CalendarBridges = options.CalendarBridges @ [ bridge ]
          }

    /// Phase 20a — override the cron trigger the polling bridges are
    /// pulled on. The supplied trigger is used verbatim; a deployment
    /// polling faster than a provider's declared
    /// `BridgeCapabilities.MinimumPollInterval` is the operator's call
    /// to make, and the floor is declared so it can be made knowingly.
    let withCalendarPollTrigger (trigger: Trigger) (options: SchedulingOptions) : SchedulingOptions = {
        options with
            CalendarPollTrigger = Some trigger
    }

/// The scheduling companion's `apply`: registers the three booking entity
/// types, wires `IBookingScheduler` into DI and contributes the
/// `ISchedulingApi` handler (plus the calendar-sync additions when a bridge
/// is composed).
let private composeScheduling (options: SchedulingOptions) (app: ServerApp) : ServerApp =
    // 1. Register the three entity types via the base's
    //    `withEntity` pipeline.
    let baseEntities =
        app
        |> ServerApp.withEntity BookingScheduler.resourceRegistration
        |> ServerApp.withEntity BookingScheduler.bookingRegistration
        |> ServerApp.withEntity BookingScheduler.exceptionRegistration

    // 1a. Phase 20a — the calendar-sync additions, and ONLY when a
    //     bridge was composed: two more entity types, the sync
    //     singleton, the three push jobs and (for polling bridges)
    //     the poll job. With no bridge composed this whole block is
    //     skipped and `withEntities` is `baseEntities` (GP 13).
    let calendarComposed = not (List.isEmpty options.CalendarBridges)

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
            let bridges = options.CalendarBridges

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
                options.CalendarPollTrigger
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

            // Once at start: the sync runs as scheduler jobs, placed by
            // the scheduler's own unit.
            Hosting.register services (Hosting.deferredJobs "calendar-sync" declarations)

    // 3. API handler. Resolves `IBookingScheduler` per request
    //    out of DI; the per-request `scopeId`/`userId` come from
    //    `HttpContext.Items` inside `schedulingApi`.
    let schedulingApiHandler =
        makeApi (fun (ctx: HttpContext) ->
            let scheduler =
                ctx.RequestServices.GetService(typeof<IBookingScheduler>) :?> IBookingScheduler

            schedulingApi scheduler ctx)

    // 4. Contribute the route and the registrations.
    withEntities
    |> ServerApp.addContribution (Contribution.routes schedulingCompanion [ schedulingApiHandler ])
    |> ServerApp.addContribution (Contribution.service schedulingCompanion schedulingServiceConfig)

/// **Sets** the scheduling companion: the booking entities, scheduler,
/// API handler and calendar bridges `options` describes are composed once,
/// when the deployment resolves, onto the deployment as every builder left
/// it — so a `ServerApp` builder applied after this call still reaches it.
/// A second call replaces the first.
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> SchedulingCompose.withScheduling (SchedulingOptions.create ())
///     |> ServerApp.run
let withScheduling (options: SchedulingOptions) (app: ServerApp) : ServerApp =
    ServerApp.withCompanion schedulingCompanion (composeScheduling options) app