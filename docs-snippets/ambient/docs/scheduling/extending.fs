// Ambient context for `docs/scheduling/extending.md`.
//
// The page teaches how a CONSUMER extends the scheduling companion, so
// almost every block is an excerpt from a program it never shows in
// full: the deployment's `services` / `config`, the `ISchedulingApi`
// proxy a module holds, and the module-side helpers the page names in
// passing (the wait-list store, the external-calendar query, the RRULE
// library a custom expander wraps). None of these is SDK surface —
// they are what the page tells a module author to have beside them.
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.SchedulingApi
open ToolUp.Scheduling.SchedulingEvents
open ToolUp.Scheduling.IBookingScheduler

[<AutoOpen>]
module PageAmbient =

    // ─── The deployment's composition root ────────────────────────

    /// The DI collection a consumer registers its own
    /// `IBookingScheduler` into, before `SchedulingServerApp.run`.
    let services: IServiceCollection = failwith "ambient"

    let config: ServerConfig = failwith "ambient"

    /// The substrate a custom scheduler is constructed over.
    let entityStore: IEntityStore = failwith "ambient"

    /// The custom scheduler built in "Replacing `IBookingScheduler`" —
    /// whatever the deployment's own implementation turns out to be.
    let redisLockedScheduler: IBookingScheduler = failwith "ambient"

    // ─── The module's client-side proxy ───────────────────────────

    /// The `ISchedulingApi` proxy a consumer module holds. Every
    /// booking example on the page calls through it.
    let schedulingApi: ISchedulingApi = failwith "ambient"

    /// A booking the module has already assembled — the page's
    /// examples vary one field of it rather than re-spelling all
    /// thirteen at every call site.
    let seedBooking: Booking = failwith "ambient"

    // ─── Two-way calendar sync (the module-side layer) ────────────

    /// The external-calendar query a deployment writes itself while
    /// `ICalendarSyncProvider` remains a deferred extension.
    module googleCalendarApi =
        let fetchEvents (resourceId: ResourceId) (window: DateRange) : Async<AvailabilityException list> =
            failwith "ambient"

    /// Subtracts externally-busy windows from the free slots the
    /// scheduler emitted. Module-side, pure.
    let subtractExternal (slots: TimeSlot list) (externalEvents: AvailabilityException list) : TimeSlot list =
        failwith "ambient"

    // ─── Custom recurrence ────────────────────────────────────────

    /// The RFC 5545 library a custom expander wraps. The reader's
    /// dependency, not an SDK interface.
    type IMyRRuleLibrary =
        abstract ExpandRRule: string -> DateTimeOffset -> DateTimeOffset list

    // ─── Wait lists ───────────────────────────────────────────────

    /// One entry in the deployment's own wait-list store.
    type WaitListEntry = {
        Id: string
        CustomerId: string
        Email: string
    }

    /// Deserialises the `ModuleEvent.Payload` the scheduler wrote for
    /// `SchedulingEvents.BookingCancelled`.
    let parseBookingCancelled (payload: string) : BookingCancelledPayload = failwith "ambient"

    let readWaitListForResource (resourceId: ResourceId) : Async<WaitListEntry list> = failwith "ambient"

    let markWaitListEntryFulfilled (entryId: string) : Async<unit> = failwith "ambient"

    let sendPromotionEmail (address: string) : Async<unit> = failwith "ambient"

    /// The promoter the "Wait lists" block writes. Declared here so the
    /// registration block beside it resolves; that block's own
    /// declaration shadows this one.
    type WaitListPromoter(schedulingApi: ISchedulingApi) =
        interface IJobHandler with
            member _.Execute(ctx: JobContext) = failwith "ambient"

    // ─── Group bookings ───────────────────────────────────────────

    /// The weekly window every spot in the class shares, declared once
    /// by the deployment rather than per spot.
    let classWindows: AvailabilityWindow list = failwith "ambient"