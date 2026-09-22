module ToolUp.Scheduling.CalendarSync

open System
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.SchedulingEvents
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.IBookingScheduler

// ─── Phase 20a — the calendar sync engine ───────────────────────────
//
// The half of two-way calendar sync that is provider-independent: the
// resource ↔ external-calendar LINK store, the push path, the pull path,
// and the conflict policy. Bridges (`ICalendarBridge`) know one
// provider's wire format and nothing else; everything stateful lives
// here, over the parent companion's own storage seam (`IEntityStore`)
// and its scheduler (`IBookingScheduler`).
//
// **Two entity types**, registered by `SchedulingCompose` only when at
// least one bridge is composed (GP 13 — a deployment that never calls
// `withCalendarBridge` registers neither and is byte-for-byte
// unchanged):
//
//   * `CalendarLink` — one per (resource, provider). Carries the
//     external calendar id, the user whose credentials authorise the
//     mirror, the conflict policy, and the pull cursor.
//   * `CalendarEventLink` — one per (resource, provider, booking).
//     Carries the provider's event id and WHEN we last pushed it, which
//     is the instant `LatestModifiedWins` compares against.
//
// **Push is event-driven.** `SchedulingCompose` registers this module's
// push handler against `Trigger.OnEvent` for `BookingCreated` /
// `BookingRescheduled` / `BookingCancelled`, so a booking written
// through `IBookingScheduler` mirrors outward with no call-site change
// anywhere. The handler resolves the originating event from
// `IEventStore` and reads its `BookingId` — every scheduling payload
// carries one.
//
// **Pull is poll- or webhook-driven.** A bridge that declares
// `SupportsWebhooks` is driven by `HandleWebhook`; one that does not is
// polled by a cron job the compose step registers, clamped to the
// bridge's declared `MinimumPollInterval` (portability rule 6).
//
// **Two limits are declared rather than half-implemented**:
//
//   * *External deletions are not detected by a poll.* A pull sees the
//     events that exist; an event that vanished is indistinguishable
//     from one outside the pulled window. A webhook-capable bridge
//     names the changed event, so a provider that reports deletions can
//     have them applied — that belongs with the bridges that support
//     them (Phases 830 / 831), not here.
//   * *An external edit applied locally records `BookingCreated`* when
//     it changed anything other than the times, because Phase 20's
//     `IBookingScheduler` exposes `Book` / `Cancel` / `Reschedule` and
//     no general update verb. A pure time change DOES take the precise
//     verb (`Reschedule`) and records `BookingRescheduled`.

// ─── Entity type names ──────────────────────────────────────────────

[<Literal>]
let CalendarLinkTypeName = "CalendarLink"

[<Literal>]
let CalendarEventLinkTypeName = "CalendarEventLink"

/// Job-handler names the compose step registers. Namespaced under the
/// scheduling companion's reserved module id, per the `IJobScheduler`
/// convention that a handler name is namespaced against its module.
[<Literal>]
let PushHandlerPrefix = "_scheduling.calendar-push-"

/// Poll job handler name.
[<Literal>]
let PollHandlerName = "_scheduling.calendar-poll"

// ─── The two link entities ──────────────────────────────────────────

/// One resource mirrored into one external calendar. `Id` is
/// `"<resourceId>|<kind>"`, so a resource links at most once per
/// provider and re-linking is an idempotent overwrite.
type CalendarLink = {
    Id: string
    Type: string
    Version: int
    ResourceId: ResourceId
    /// `ICalendarBridge.Kind` of the provider this link mirrors into.
    Kind: string
    ExternalCalendarId: ExternalCalendarId
    /// The user whose credentials the bridge resolves per call.
    UserId: string
    /// `ConflictPolicy.toString` form. Persisted as a string so the
    /// stored shape stays a plain record of primitives.
    Policy: string
    /// Pull cursor, advanced only for a bridge declaring
    /// `SupportsIncrementalPull` — for any other bridge `since` is a
    /// bound on event TIME, not on modification time, so carrying a
    /// cursor forward would hide every event the provider still holds.
    CursorUtc: DateTimeOffset option
}

/// One booking mirrored as one external event.
/// `Id` is `"<resourceId>|<kind>|<bookingId>"`.
type CalendarEventLink = {
    Id: string
    Type: string
    Version: int
    ResourceId: ResourceId
    Kind: string
    BookingId: BookingId
    ExternalEventId: ExternalEventId
    /// When this deployment last pushed the booking. `LatestModifiedWins`
    /// compares the external event's `LastModifiedUtc` against it — the
    /// only two instants both sides agree on.
    LastPushedAtUtc: DateTimeOffset
    /// The `Booking.Version` that push carried, so a later local edit is
    /// detectable without a second timestamp.
    LastPushedVersion: int
}

let calendarLinkRegistration: EntityRegistration<CalendarLink> =
    EntityRegistration.create<CalendarLink> CalendarLinkTypeName
    |> EntityRegistration.withIndex "ResourceId" _.ResourceId
    |> EntityRegistration.withIndex "Kind" _.Kind
    |> EntityRegistration.withIndex "ExternalCalendarId" _.ExternalCalendarId

let calendarEventLinkRegistration: EntityRegistration<CalendarEventLink> =
    EntityRegistration.create<CalendarEventLink> CalendarEventLinkTypeName
    |> EntityRegistration.withIndex "BookingId" _.BookingId
    |> EntityRegistration.withIndex "ResourceId" _.ResourceId
    |> EntityRegistration.withIndex "ExternalEventId" _.ExternalEventId

let linkId (resourceId: ResourceId) (kind: string) : string = sprintf "%s|%s" resourceId kind

let eventLinkId (resourceId: ResourceId) (kind: string) (bookingId: BookingId) : string =
    sprintf "%s|%s|%s" resourceId kind bookingId

// ─── Outcomes + errors ──────────────────────────────────────────────

/// Why a sync operation could not be performed at all. A per-bridge
/// failure is NOT one of these — it rides the outcome's `Failures`, so
/// one unreachable provider never hides the work the others did.
type CalendarSyncError =
    /// No bridge is composed under that `Kind`.
    | UnknownBridge of kind: string
    /// The resource has no link for that provider.
    | NotLinked of resourceId: ResourceId * kind: string
    /// The underlying entity store refused a read or a write.
    | SyncStorageFailure of message: string

module CalendarSyncError =
    let message (error: CalendarSyncError) : string =
        match error with
        | UnknownBridge kind -> sprintf "no calendar bridge is composed for kind '%s'" kind
        | NotLinked(resourceId, kind) -> sprintf "resource '%s' has no '%s' calendar link" resourceId kind
        | SyncStorageFailure m -> sprintf "calendar link storage failed: %s" m

/// What one push did, per resource. `Failures` is per provider, so a
/// resource mirrored into two calendars reports both independently.
type PushOutcome = {
    /// External events created or updated.
    Pushed: int
    /// External events removed (the booking was cancelled).
    Removed: int
    /// `(Kind, error)` for every bridge that refused.
    Failures: (string * BridgeError) list
}

/// What one pull did, per resource.
type PullOutcome = {
    /// External events the providers returned.
    Examined: int
    /// External changes written into the local schedule.
    AppliedLocally: int
    /// Local bookings re-pushed because the local side won.
    RePushed: int
    /// Events already in agreement, or that could not be applied
    /// (an external event overlapping a local booking).
    Skipped: int
    Failures: (string * BridgeError) list
}

module PushOutcome =
    let empty: PushOutcome = {
        Pushed = 0
        Removed = 0
        Failures = []
    }

module PullOutcome =
    let empty: PullOutcome = {
        Examined = 0
        AppliedLocally = 0
        RePushed = 0
        Skipped = 0
        Failures = []
    }

/// The provider-independent half of calendar sync.
type ICalendarSync =
    /// Kinds of every composed bridge, in compose order.
    abstract Kinds: string list

    /// Mirror `resourceId` into `externalCalendarId` at the named
    /// provider. Idempotent — re-linking overwrites the stored link
    /// (policy and external calendar included) and re-runs the bridge's
    /// own provider-side setup.
    abstract LinkResource:
        scopeId: string *
        resourceId: ResourceId *
        kind: string *
        externalCalendarId: ExternalCalendarId *
        userId: string *
        policy: ConflictPolicy *
        actor: EntityPrincipal ->
            Async<Result<CalendarLink, CalendarSyncError>>

    /// Remove the mirror. Idempotent — unlinking an unlinked resource
    /// returns `Ok`. Leaves already-pushed external events in place and
    /// drops the per-booking event links.
    abstract UnlinkResource:
        scopeId: string * resourceId: ResourceId * kind: string * actor: EntityPrincipal ->
            Async<Result<unit, CalendarSyncError>>

    /// Every link on a resource, across providers.
    abstract ListLinks: scopeId: string * resourceId: ResourceId -> Async<CalendarLink list>

    /// Mirror one booking outward on every link its resource carries. A
    /// booking whose `Status` is `Cancelled` is removed externally.
    abstract PushBooking:
        scopeId: string * booking: Booking * actor: EntityPrincipal -> Async<Result<PushOutcome, CalendarSyncError>>

    /// Read every link's external calendar and reconcile it against the
    /// local schedule under each link's conflict policy.
    abstract PullResource:
        scopeId: string * resourceId: ResourceId * actor: EntityPrincipal ->
            Async<Result<PullOutcome, CalendarSyncError>>

    /// Interpret one inbound provider notification and pull whatever it
    /// says changed. `scopeId` is resolved by the deployment's own
    /// webhook route (conventionally from the route path) before the
    /// call — a notification body never names a storage scope.
    abstract HandleWebhook:
        scopeId: string * kind: string * headers: Map<string, string> * body: byte[] * actor: EntityPrincipal ->
            Async<Result<PullOutcome, CalendarSyncError>>

// ─── Implementation ─────────────────────────────────────────────────

let private jsonOptions =
    let o = JsonSerializerOptions()
    o.PropertyNamingPolicy <- null
    o

/// Every scheduling event payload carries a `BookingId`; the push
/// handler needs only that field, so it reads it out of the raw JSON
/// rather than branching on four payload types.
let bookingIdOfEventPayload (payload: string) : string option =
    try
        use doc = JsonDocument.Parse payload

        match doc.RootElement.TryGetProperty "BookingId" with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None
    with _ ->
        None

/// Whether two bookings agree on everything a calendar can express.
/// Deliberately NOT structural equality on the whole record: `Version`
/// moves on every local save and `Status` / `BookedBy` are never
/// mirrored, so comparing them would report a change on every pull.
let bookingsAgree (local: Booking) (external': Booking) : bool =
    local.Title = external'.Title
    && local.StartUtc = external'.StartUtc
    && local.EndUtc = external'.EndUtc
    && local.Recurrence = external'.Recurrence
    && local.Metadata = external'.Metadata

/// Whether ONLY the times moved — the case the scheduler has a precise
/// verb for.
let private onlyTimesMoved (local: Booking) (external': Booking) : bool =
    local.Title = external'.Title
    && local.Recurrence = external'.Recurrence
    && local.Metadata = external'.Metadata
    && (local.StartUtc <> external'.StartUtc || local.EndUtc <> external'.EndUtc)

/// Resolve the conflict. `eventLink` is `None` when this deployment has
/// never pushed the event, in which case the external side is the only
/// side that has ever described it.
let resolveConflict
    (policy: ConflictPolicy)
    (eventLink: CalendarEventLink option)
    (externalEvent: ExternalEvent)
    : bool =
    match policy with
    | ExternalWins -> true
    | LocalWins -> false
    | LatestModifiedWins ->
        match externalEvent.LastModifiedUtc, eventLink with
        | Some modified, Some link -> modified > link.LastPushedAtUtc
        | Some _, None -> true
        | None, _ -> false

/// The provider-independent sync engine. Construct one per process at
/// compose time; it holds no per-call state.
type CalendarSync
    (
        entityStore: IEntityStore,
        scheduler: IBookingScheduler,
        bridges: ICalendarBridge list,
        clock: unit -> DateTimeOffset
    ) =

    let byKind = bridges |> List.map (fun b -> b.Kind, b) |> Map.ofList

    let tryBridge (kind: string) =
        match Map.tryFind kind byKind with
        | Some b -> Ok b
        | None -> Error(UnknownBridge kind)

    let linkRefOf (link: CalendarLink) : CalendarLinkRef = {
        ScopeId = ""
        ResourceId = link.ResourceId
        ExternalCalendarId = link.ExternalCalendarId
        UserId = link.UserId
    }

    let linkRef (scopeId: string) (link: CalendarLink) : CalendarLinkRef = {
        linkRefOf link with
            ScopeId = scopeId
    }

    let policyOf (link: CalendarLink) =
        ConflictPolicy.ofString link.Policy |> Option.defaultValue LocalWins

    let listLinks (scopeId: string) (resourceId: ResourceId) : Async<CalendarLink list> = async {
        let! refs = entityStore.FindByIndex<CalendarLink>(scopeId, CalendarLinkTypeName, "ResourceId", resourceId)

        match refs with
        | Error _ -> return []
        | Ok found ->
            let acc = ResizeArray<CalendarLink>()

            for r in found do
                match! entityStore.Get<CalendarLink>(scopeId, CalendarLinkTypeName, r.Id) with
                | Ok l -> acc.Add l
                | Error _ -> ()

            return List.ofSeq acc
    }

    let tryGetEventLink (scopeId: string) (link: CalendarLink) (bookingId: BookingId) = async {
        let id = eventLinkId link.ResourceId link.Kind bookingId

        match! entityStore.Get<CalendarEventLink>(scopeId, CalendarEventLinkTypeName, id) with
        | Ok l -> return Some l
        | Error _ -> return None
    }

    let tryEventLinkByExternalId (scopeId: string) (link: CalendarLink) (externalId: ExternalEventId) = async {
        match!
            entityStore.FindByIndex<CalendarEventLink>(
                scopeId,
                CalendarEventLinkTypeName,
                "ExternalEventId",
                externalId
            )
        with
        | Error _ -> return None
        | Ok refs ->
            let mutable found = None

            for r in refs do
                if found.IsNone then
                    match! entityStore.Get<CalendarEventLink>(scopeId, CalendarEventLinkTypeName, r.Id) with
                    | Ok l when l.Kind = link.Kind && l.ResourceId = link.ResourceId -> found <- Some l
                    | _ -> ()

            return found
    }

    let recordEventLink (scopeId: string) (actor: EntityPrincipal) (link: CalendarLink) (booking: Booking) externalId = async {
        let record: CalendarEventLink = {
            Id = eventLinkId link.ResourceId link.Kind booking.Id
            Type = CalendarEventLinkTypeName
            Version = 0
            ResourceId = link.ResourceId
            Kind = link.Kind
            BookingId = booking.Id
            ExternalEventId = externalId
            LastPushedAtUtc = clock ()
            LastPushedVersion = booking.Version
        }

        let! _ = entityStore.Save<CalendarEventLink>(scopeId, actor, record)
        return ()
    }

    /// The booking fields no calendar can express. A pulled event that
    /// this deployment has never seen becomes a `Confirmed` booking on
    /// the linked resource, booked by the link's own user — the only
    /// principal the deployment can attribute an external event to.
    let defaultsFor (link: CalendarLink) : Booking = {
        Id = ""
        Type = "Booking"
        Version = 0
        ResourceId = link.ResourceId
        Title = ""
        StartUtc = DateTimeOffset.MinValue
        EndUtc = DateTimeOffset.MinValue
        Status = Confirmed
        BookedBy = link.UserId
        BookedFor = None
        Recurrence = None
        ParentBookingId = None
        Metadata = Map.empty
    }

    let pushOne
        (scopeId: string)
        (actor: EntityPrincipal)
        (booking: Booking)
        (link: CalendarLink)
        (outcome: PushOutcome)
        =
        async {
            match tryBridge link.Kind with
            | Error _ ->
                // A link naming a kind no longer composed is not a failure of
                // this push: the deployment dropped the bridge, and the link
                // is inert until it is composed again.
                return outcome
            | Ok bridge ->
                let! existing = tryGetEventLink scopeId link booking.Id
                let existingId = existing |> Option.map _.ExternalEventId

                match! bridge.Push(linkRef scopeId link, booking, existingId) with
                | Error err ->
                    return {
                        outcome with
                            Failures = outcome.Failures @ [ link.Kind, err ]
                    }
                | Ok externalId ->
                    if booking.Status = Cancelled then
                        match existing with
                        | Some l ->
                            let! _ = entityStore.Delete(scopeId, actor, CalendarEventLinkTypeName, l.Id)

                            ()
                        | None -> ()

                        return {
                            outcome with
                                Removed = outcome.Removed + 1
                        }
                    else
                        do! recordEventLink scopeId actor link booking externalId

                        return {
                            outcome with
                                Pushed = outcome.Pushed + 1
                        }
        }

    /// Write one external event into the local schedule. Uses the
    /// precise verb where the scheduler has one.
    let applyExternally (scopeId: string) (link: CalendarLink) (local: Booking option) (incoming: Booking) = async {
        match local with
        | None ->
            match! scheduler.Book(scopeId, incoming, link.UserId) with
            | Ok _ -> return true
            | Error _ -> return false
        | Some b when onlyTimesMoved b incoming ->
            match! scheduler.Reschedule(scopeId, b.Id, incoming.StartUtc, incoming.EndUtc, link.UserId) with
            | Ok _ -> return true
            | Error _ -> return false
        | Some b ->
            // No general update verb exists on `IBookingScheduler`, so a
            // non-time change rides `Book`'s upsert. The conflict
            // detector excludes the booking from its own overlap check,
            // so this does not self-conflict.
            let merged = {
                incoming with
                    Id = b.Id
                    Type = b.Type
                    Version = b.Version
                    ResourceId = b.ResourceId
                    Status = b.Status
                    BookedBy = b.BookedBy
                    BookedFor = b.BookedFor
                    ParentBookingId = b.ParentBookingId
            }

            match! scheduler.Book(scopeId, merged, link.UserId) with
            | Ok _ -> return true
            | Error _ -> return false
    }

    let pullLink (scopeId: string) (actor: EntityPrincipal) (link: CalendarLink) (outcome: PullOutcome) = async {
        match tryBridge link.Kind with
        | Error _ -> return outcome
        | Ok bridge ->
            // `since` is only a MODIFICATION cursor on a bridge that says
            // so; for any other bridge it bounds event TIME, and carrying
            // a cursor forward would hide everything the provider holds.
            let since =
                if bridge.Capabilities.SupportsIncrementalPull then
                    link.CursorUtc
                else
                    None

            match! bridge.Pull(linkRef scopeId link, since, defaultsFor link) with
            | Error err ->
                return {
                    outcome with
                        Failures = outcome.Failures @ [ link.Kind, err ]
                }
            | Ok events ->
                let policy = policyOf link

                let mutable acc = {
                    outcome with
                        Examined = outcome.Examined + List.length events
                }

                for e in events do
                    // By booking id FIRST. The event's UID is the booking
                    // id for anything this deployment pushed, and it is
                    // exact; an external id is the provider's own spelling
                    // and can legitimately differ between the form a push
                    // returned and the form a pull reports (a CalDAV
                    // `PUT` addresses an absolute URL, a `multistatus`
                    // href is a path). Matching on it alone would read a
                    // known event as never-before-seen, which silently
                    // changes what `LatestModifiedWins` decides.
                    let! byBookingId = tryGetEventLink scopeId link e.Booking.Id

                    let! eventLink =
                        match byBookingId with
                        | Some l -> async { return Some l }
                        | None -> tryEventLinkByExternalId scopeId link e.ExternalEventId

                    let bookingId =
                        match eventLink with
                        | Some l -> l.BookingId
                        | None -> e.Booking.Id

                    let! local = scheduler.GetBooking(scopeId, bookingId)
                    let incoming = { e.Booking with Id = bookingId }

                    match local with
                    | Some b when bookingsAgree b incoming -> acc <- { acc with Skipped = acc.Skipped + 1 }
                    | _ ->
                        if resolveConflict policy eventLink e then
                            let! applied = applyExternally scopeId link local incoming

                            if applied then
                                match local with
                                | Some b ->
                                    do!
                                        recordEventLink
                                            scopeId
                                            actor
                                            link
                                            { b with Version = b.Version + 1 }
                                            e.ExternalEventId
                                | None -> do! recordEventLink scopeId actor link incoming e.ExternalEventId

                                acc <- {
                                    acc with
                                        AppliedLocally = acc.AppliedLocally + 1
                                }
                            else
                                acc <- { acc with Skipped = acc.Skipped + 1 }
                        else
                            match local with
                            | None -> acc <- { acc with Skipped = acc.Skipped + 1 }
                            | Some b ->
                                match! bridge.Push(linkRef scopeId link, b, Some e.ExternalEventId) with
                                | Ok externalId ->
                                    do! recordEventLink scopeId actor link b externalId
                                    acc <- { acc with RePushed = acc.RePushed + 1 }
                                | Error err ->
                                    acc <- {
                                        acc with
                                            Failures = acc.Failures @ [ link.Kind, err ]
                                    }

                if bridge.Capabilities.SupportsIncrementalPull then
                    let advanced = { link with CursorUtc = Some(clock ()) }

                    let! _ = entityStore.Save<CalendarLink>(scopeId, actor, advanced)
                    ()

                return acc
    }

    /// The wall clock is injected so tests can pin it; this is the
    /// production constructor. An explicit secondary constructor rather
    /// than an optional argument — an optional constructor argument
    /// folds both shapes into one widened constructor, which the
    /// public-API approval gate reads as the removal of the narrow one.
    new(entityStore: IEntityStore, scheduler: IBookingScheduler, bridges: ICalendarBridge list) =
        CalendarSync(entityStore, scheduler, bridges, (fun () -> DateTimeOffset.UtcNow))

    interface ICalendarSync with

        member _.Kinds = bridges |> List.map _.Kind

        member _.LinkResource(scopeId, resourceId, kind, externalCalendarId, userId, policy, actor) = async {
            match tryBridge kind with
            | Error e -> return Error e
            | Ok bridge ->
                let link: CalendarLink = {
                    Id = linkId resourceId kind
                    Type = CalendarLinkTypeName
                    Version = 0
                    ResourceId = resourceId
                    Kind = kind
                    ExternalCalendarId = externalCalendarId
                    UserId = userId
                    Policy = ConflictPolicy.toString policy
                    CursorUtc = None
                }

                match! bridge.LinkResource(linkRef scopeId link) with
                | Error err -> return Error(SyncStorageFailure(BridgeError.message err))
                | Ok() ->
                    match! entityStore.Save<CalendarLink>(scopeId, actor, link) with
                    | Error err -> return Error(SyncStorageFailure(EntityError.message err))
                    | Ok saved -> return Ok { link with Version = saved.Version }
        }

        member _.UnlinkResource(scopeId, resourceId, kind, actor) = async {
            match! entityStore.Get<CalendarLink>(scopeId, CalendarLinkTypeName, linkId resourceId kind) with
            | Error _ -> return Ok() // idempotent — nothing linked
            | Ok link ->
                match tryBridge kind with
                | Error e -> return Error e
                | Ok bridge ->
                    match! bridge.UnlinkResource(linkRef scopeId link) with
                    | Error err -> return Error(SyncStorageFailure(BridgeError.message err))
                    | Ok() ->
                        let! eventRefs =
                            entityStore.FindByIndex<CalendarEventLink>(
                                scopeId,
                                CalendarEventLinkTypeName,
                                "ResourceId",
                                resourceId
                            )

                        match eventRefs with
                        | Error _ -> ()
                        | Ok refs ->
                            for r in refs do
                                match! entityStore.Get<CalendarEventLink>(scopeId, CalendarEventLinkTypeName, r.Id) with
                                | Ok l when l.Kind = kind ->
                                    let! _ = entityStore.Delete(scopeId, actor, CalendarEventLinkTypeName, l.Id)

                                    ()
                                | _ -> ()

                        let! _ = entityStore.Delete(scopeId, actor, CalendarLinkTypeName, link.Id)
                        return Ok()
        }

        member _.ListLinks(scopeId, resourceId) = listLinks scopeId resourceId

        member _.PushBooking(scopeId, booking, actor) = async {
            let! links = listLinks scopeId booking.ResourceId
            let mutable outcome = PushOutcome.empty

            for link in links do
                let! next = pushOne scopeId actor booking link outcome
                outcome <- next

            return Ok outcome
        }

        member _.PullResource(scopeId, resourceId, actor) = async {
            let! links = listLinks scopeId resourceId
            let mutable outcome = PullOutcome.empty

            for link in links do
                let! next = pullLink scopeId actor link outcome
                outcome <- next

            return Ok outcome
        }

        member this.HandleWebhook(scopeId, kind, headers, body, actor) = async {
            match tryBridge kind with
            | Error e -> return Error e
            | Ok bridge ->
                match! bridge.HandleWebhook(headers, body) with
                | Error err ->
                    return
                        Ok {
                            PullOutcome.empty with
                                Failures = [ kind, err ]
                        }
                | Ok notifications ->
                    let mutable outcome = PullOutcome.empty

                    for n in notifications do
                        let! refs =
                            entityStore.FindByIndex<CalendarLink>(
                                scopeId,
                                CalendarLinkTypeName,
                                "ExternalCalendarId",
                                n.ExternalCalendarId
                            )

                        match refs with
                        | Error _ -> ()
                        | Ok found ->
                            for r in found do
                                match! entityStore.Get<CalendarLink>(scopeId, CalendarLinkTypeName, r.Id) with
                                | Ok link when link.Kind = kind ->
                                    let! next = pullLink scopeId actor link outcome
                                    outcome <- next
                                | _ -> ()

                    return Ok outcome
        }

// ─── Job handlers ───────────────────────────────────────────────────

/// Mirrors a booking outward when the scheduler records a lifecycle
/// event. Registered by `SchedulingCompose` against `Trigger.OnEvent`
/// for `BookingCreated` / `BookingRescheduled` / `BookingCancelled`.
/// Stateless between invocations (portability rule 4): everything it
/// needs arrives on `JobContext`.
type CalendarPushJobHandler
    (sync: ICalendarSync, scheduler: IBookingScheduler, eventStore: IEventStore, logger: ILogger option) =

    let warn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            match ctx.TriggerSource with
            | ScheduledByEvent(eventType, eventId) ->
                let! events = eventStore.ReadByType(ctx.ScopeId, eventType)

                match events |> List.tryFind (fun e -> e.Id = eventId) with
                | None ->
                    warn (sprintf "calendar push: originating %s event %O is no longer readable" eventType eventId)
                    return JobResult.Success
                | Some evt ->
                    match bookingIdOfEventPayload evt.Payload with
                    | None -> return JobResult.PermanentFailure "event payload carries no BookingId"
                    | Some bookingId ->
                        match! scheduler.GetBooking(ctx.ScopeId, bookingId) with
                        | None ->
                            // Booked and hard-deleted before the mirror ran.
                            return JobResult.Success
                        | Some booking ->
                            match! sync.PushBooking(ctx.ScopeId, booking, EntityPrincipal.system) with
                            | Error e -> return JobResult.PermanentFailure(CalendarSyncError.message e)
                            | Ok outcome ->
                                match outcome.Failures with
                                | [] -> return JobResult.Success
                                | failures ->
                                    let text =
                                        failures
                                        |> List.map (fun (kind, err) -> sprintf "%s: %s" kind (BridgeError.message err))
                                        |> String.concat "; "

                                    if failures |> List.exists (fun (_, err) -> BridgeError.isRetryable err) then
                                        return JobResult.TransientFailure text
                                    else
                                        return JobResult.PermanentFailure text
            | _ ->
                // Only the event trigger carries a booking; a manual or
                // cron dispatch of this handler has nothing to mirror.
                return JobResult.Success
        }

/// Polls every link in the scope whose bridge cannot push notifications.
/// Registered by `SchedulingCompose` on a cron trigger when at least one
/// composed bridge declares `SupportsWebhooks = false`.
type CalendarPollJobHandler(sync: ICalendarSync, entityStore: IEntityStore, pageSize: int) =

    /// Default page size for the per-tick link enumeration.
    new(sync: ICalendarSync, entityStore: IEntityStore) = CalendarPollJobHandler(sync, entityStore, 200)

    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            let seen = ResizeArray<ResourceId>()
            let mutable skip = 0
            let mutable more = true

            while more do
                let! refs = entityStore.ListAll<CalendarLink>(ctx.ScopeId, CalendarLinkTypeName, skip, pageSize)

                if List.isEmpty refs then
                    more <- false
                else
                    for r in refs do
                        match! entityStore.Get<CalendarLink>(ctx.ScopeId, CalendarLinkTypeName, r.Id) with
                        | Ok link when not (seen.Contains link.ResourceId) -> seen.Add link.ResourceId
                        | _ -> ()

                    skip <- skip + List.length refs

            let failures = ResizeArray<string>()
            let mutable retryable = false

            for resourceId in seen do
                match! sync.PullResource(ctx.ScopeId, resourceId, EntityPrincipal.system) with
                | Error e -> failures.Add(CalendarSyncError.message e)
                | Ok outcome ->
                    for kind, err in outcome.Failures do
                        failures.Add(sprintf "%s/%s: %s" resourceId kind (BridgeError.message err))

                        if BridgeError.isRetryable err then
                            retryable <- true

            if failures.Count = 0 then
                return JobResult.Success
            elif retryable then
                return JobResult.TransientFailure(String.concat "; " failures)
            else
                return JobResult.PermanentFailure(String.concat "; " failures)
        }