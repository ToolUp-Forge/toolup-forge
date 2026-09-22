module ToolUp.Scheduling.Tests.InProcess.CalendarSyncTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.SchedulingEvents
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.BookingScheduler
open ToolUp.Scheduling.CalendarSync
open ToolUp.Scheduling.SchedulingCompose
open ToolUp.Scheduling.Tests.InProcess.InMemoryEntityStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryEventStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryCalendarBridge

// ─── Phase 20a — the sync engine's own behaviour ────────────────────
//
// What the `ICalendarBridgeContract` pack does NOT cover, because it is
// about the engine and the compose hook rather than about a provider:
// the one-bridge-per-kind rule, the strip-mode default, the pure
// resolution helpers, and the event-driven push path end to end.

[<Literal>]
let private CalendarId = "cal-sync-tests"

let private utc (y: int) (mo: int) (d: int) (h: int) : DateTimeOffset =
    DateTimeOffset(y, mo, d, h, 0, 0, TimeSpan.Zero)

let private booking (id: BookingId) (start: DateTimeOffset) : Booking = {
    Id = id
    Type = "Booking"
    Version = 3
    ResourceId = "room-101"
    Title = "Standup"
    StartUtc = start
    EndUtc = start.AddHours 1.0
    Status = Confirmed
    BookedBy = "alice"
    BookedFor = None
    Recurrence = None
    ParentBookingId = None
    Metadata = Map.empty
}

/// A second bridge under a DIFFERENT kind, for the multi-provider case.
type private SecondBridge() =
    interface ICalendarBridge with
        member _.Kind = "Second"
        member _.Capabilities = BridgeCapabilities.pollingOnly
        member _.LinkResource(_link) = async { return Ok() }
        member _.UnlinkResource(_link) = async { return Ok() }
        member _.Push(_link, booking, _existing) = async { return Ok("second/" + booking.Id) }
        member _.Pull(_link, _since, _defaults) = async { return Ok [] }
        member _.HandleWebhook(_headers, _body) = async { return Ok [] }

let private eventLinkFor (pushedAt: DateTimeOffset) : CalendarEventLink = {
    Id = "room-101|InMemory|bk-1"
    Type = CalendarEventLinkTypeName
    Version = 1
    ResourceId = "room-101"
    Kind = "InMemory"
    BookingId = "bk-1"
    ExternalEventId = "evt-1"
    LastPushedAtUtc = pushedAt
    LastPushedVersion = 3
}

let private externalEvent (lastModified: DateTimeOffset option) : ExternalEvent = {
    ExternalEventId = "evt-1"
    Booking = booking "bk-1" (utc 2026 10 1 9)
    LastModifiedUtc = lastModified
}

let tests =
    testList "CalendarSync" [

        testList "compose" [
            test "a deployment that composes no bridge has none" {
                let app = SchedulingServerApp.create ()
                Expect.isEmpty app.CalendarBridges "no bridge is composed by default"

                Expect.isNone
                    app.CalendarPollTrigger
                    "no poll trigger is declared by default — `run` short-circuits the whole calendar block, so nothing is registered, built or scheduled"
            }

            test "composing a bridge records it in order" {
                let bridge = InMemoryCalendarBridge [ CalendarId ] :> ICalendarBridge

                let app =
                    SchedulingServerApp.create () |> SchedulingServerApp.withCalendarBridge bridge

                Expect.equal (List.length app.CalendarBridges) 1 "one bridge composed"
                Expect.equal app.CalendarBridges.Head.Kind "InMemory" "and it is the one composed"
            }

            test "two bridges of different kinds both compose" {
                let app =
                    SchedulingServerApp.create ()
                    |> SchedulingServerApp.withCalendarBridge (InMemoryCalendarBridge [ CalendarId ])
                    |> SchedulingServerApp.withCalendarBridge (SecondBridge())

                Expect.equal
                    (app.CalendarBridges |> List.map _.Kind)
                    [ "InMemory"; "Second" ]
                    "both kinds are composed, in compose order"
            }

            test "a second bridge of the same kind is refused, naming the kind" {
                let compose () =
                    SchedulingServerApp.create ()
                    |> SchedulingServerApp.withCalendarBridge (InMemoryCalendarBridge [ CalendarId ])
                    |> SchedulingServerApp.withCalendarBridge (InMemoryCalendarBridge [ CalendarId ])
                    |> ignore

                let ex = Expect.throws compose "a duplicate kind must be refused at compose"
                ignore ex
            }

            test "the poll trigger is overridable" {
                let app =
                    SchedulingServerApp.create ()
                    |> SchedulingServerApp.withCalendarPollTrigger (CronTrigger "*/5 * * * *")

                Expect.equal app.CalendarPollTrigger (Some(CronTrigger "*/5 * * * *")) "the override is recorded"
            }

            test "the default poll cron is a valid five-field expression" {
                let fields = DefaultCalendarPollCron.Split ' '
                Expect.equal fields.Length 5 "a standard five-field cron"
            }
        ]

        testList "resolution helpers" [
            test "ExternalWins always applies the external side" {
                Expect.isTrue
                    (resolveConflict ExternalWins (Some(eventLinkFor (utc 2026 10 1 12))) (externalEvent None))
                    "even with no modification stamp"
            }

            test "LocalWins never applies the external side" {
                Expect.isFalse
                    (resolveConflict
                        LocalWins
                        (Some(eventLinkFor (utc 2026 10 1 12)))
                        (externalEvent (Some(utc 2026 10 2 12))))
                    "even against a newer external edit"
            }

            test "LatestModifiedWins prefers the later stamp" {
                let link = Some(eventLinkFor (utc 2026 10 1 12))

                Expect.isTrue
                    (resolveConflict LatestModifiedWins link (externalEvent (Some(utc 2026 10 1 13))))
                    "an external edit after our push wins"

                Expect.isFalse
                    (resolveConflict LatestModifiedWins link (externalEvent (Some(utc 2026 10 1 11))))
                    "an external edit before our push loses"
            }

            test "LatestModifiedWins treats an unstamped external event as older" {
                Expect.isFalse
                    (resolveConflict LatestModifiedWins (Some(eventLinkFor (utc 2026 10 1 12))) (externalEvent None))
                    "an unordered external change never silently overwrites a local edit"
            }

            test "LatestModifiedWins takes a stamped event we have never pushed" {
                Expect.isTrue
                    (resolveConflict LatestModifiedWins None (externalEvent (Some(utc 2026 10 1 11))))
                    "with no push to compare against, the external side is the only description there is"
            }

            test "bookingsAgree ignores the fields no calendar carries" {
                let a = booking "bk-1" (utc 2026 10 1 9)

                let b = {
                    a with
                        Version = 9
                        Status = Tentative
                        BookedBy = "someone-else"
                }

                Expect.isTrue (bookingsAgree a b) "Version / Status / BookedBy are not calendar data"

                Expect.isFalse (bookingsAgree a { a with Title = "Renamed" }) "a title change is a change"

                Expect.isFalse
                    (bookingsAgree a {
                        a with
                            Metadata = Map.ofList [ "Location", "Room 4" ]
                    })
                    "a metadata change is a change"
            }

            test "the id shapes are the documented ones" {
                Expect.equal (linkId "room-101" "CalDAV") "room-101|CalDAV" "link id"

                Expect.equal (eventLinkId "room-101" "CalDAV" "bk-1") "room-101|CalDAV|bk-1" "event link id"
            }

            test "bookingIdOfEventPayload reads every scheduling payload" {
                let options = Text.Json.JsonSerializerOptions(PropertyNamingPolicy = null)

                let created: BookingCreatedPayload = {
                    UserId = "alice"
                    BookingId = "bk-created"
                    ResourceId = "room-101"
                    StartUtc = utc 2026 10 1 9
                    EndUtc = utc 2026 10 1 10
                }

                let cancelled: BookingCancelledPayload = {
                    UserId = "alice"
                    BookingId = "bk-cancelled"
                    Reason = "no longer needed"
                }

                Expect.equal
                    (bookingIdOfEventPayload (Text.Json.JsonSerializer.Serialize(created, options)))
                    (Some "bk-created")
                    "BookingCreated"

                Expect.equal
                    (bookingIdOfEventPayload (Text.Json.JsonSerializer.Serialize(cancelled, options)))
                    (Some "bk-cancelled")
                    "BookingCancelled"

                Expect.isNone (bookingIdOfEventPayload "not json at all") "garbage yields None, not an exception"
                Expect.isNone (bookingIdOfEventPayload """{"Other":1}""") "a payload with no BookingId yields None"
            }
        ]

        testList "the event-driven push path" [
            testAsync "a BookingCreated event mirrors the booking outward" {
                let entityStore = InMemoryEntityStore() :> IEntityStore
                let eventStore = InMemoryEventStore()
                let scopeId = "team-push-path"
                let actor = EntityPrincipal.ofPrincipal "calendar-sync-test"

                let scheduler =
                    BookingScheduler(entityStore, eventStore :> IEventStore) :> IBookingScheduler

                let bridge = InMemoryCalendarBridge [ CalendarId ]

                let sync =
                    CalendarSync(entityStore, scheduler, [ bridge :> ICalendarBridge ], (fun () -> utc 2026 10 1 12))
                    :> ICalendarSync

                let! _ =
                    scheduler.RegisterResource(
                        scopeId,
                        actor,
                        {
                            Id = "room-101"
                            Type = "BookableResource"
                            Version = 0
                            ResourceType = "Room"
                            DisplayName = "room-101"
                            Timezone = "UTC"
                            DefaultAvailability = [
                                {
                                    DayOfWeek = None
                                    StartTime = TimeOnly(0, 0)
                                    EndTime = TimeOnly(23, 59)
                                    EffectiveFrom = None
                                    EffectiveTo = None
                                }
                            ]
                            Metadata = Map.empty
                        }
                    )

                match! sync.LinkResource(scopeId, "room-101", "InMemory", CalendarId, "alice", LocalWins, actor) with
                | Error e -> failtestf "link refused: %s" (CalendarSyncError.message e)
                | Ok _ ->
                    // Booking through the scheduler is what writes the
                    // BookingCreated event the handler is triggered by.
                    match! scheduler.Book(scopeId, booking "bk-push-path" (utc 2026 10 1 9), "alice") with
                    | Error e -> failtestf "booking refused: %A" e
                    | Ok saved ->
                        let created =
                            eventStore.Events
                            |> List.filter (fun e -> e.EventType = BookingCreated)
                            |> List.head

                        let handler =
                            CalendarPushJobHandler(sync, scheduler, eventStore :> IEventStore, None) :> IJobHandler

                        let ctx: JobContext = {
                            JobId = Guid.NewGuid()
                            ScopeId = scopeId
                            AccessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
                            Attempt = 1
                            Trigger = OnEvent BookingCreated
                            TriggerSource = ScheduledByEvent(BookingCreated, created.Id)
                            ScheduledAt = DateTime.UtcNow
                            RunningAt = DateTime.UtcNow
                            Payload = ""
                            DeadLetterDestination = None
                        }

                        match! handler.Execute ctx with
                        | Success -> ()
                        | other -> failtestf "the push handler did not succeed: %A" other

                        match!
                            (bridge :> ICalendarBridge)
                                .Pull(
                                    {
                                        ScopeId = scopeId
                                        ResourceId = "room-101"
                                        ExternalCalendarId = CalendarId
                                        UserId = "alice"
                                    },
                                    None,
                                    saved
                                )
                        with
                        | Error e -> failtestf "verification pull refused: %s" (BridgeError.message e)
                        | Ok events ->
                            Expect.equal (List.length events) 1 "the booking was mirrored by the event-driven path"
                            Expect.equal events.Head.Booking.Id saved.Id "and it is the booking that was created"
            }

            testAsync "a dispatch that is not event-triggered mirrors nothing" {
                let entityStore = InMemoryEntityStore() :> IEntityStore
                let eventStore = InMemoryEventStore() :> IEventStore

                let scheduler = BookingScheduler(entityStore, eventStore) :> IBookingScheduler

                let sync =
                    CalendarSync(entityStore, scheduler, [], (fun () -> utc 2026 10 1 12)) :> ICalendarSync

                let handler =
                    CalendarPushJobHandler(sync, scheduler, eventStore, None) :> IJobHandler

                let ctx: JobContext = {
                    JobId = Guid.NewGuid()
                    ScopeId = "team-manual"
                    AccessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
                    Attempt = 1
                    Trigger = Manual
                    TriggerSource = ScheduledManually "admin"
                    ScheduledAt = DateTime.UtcNow
                    RunningAt = DateTime.UtcNow
                    Payload = ""
                    DeadLetterDestination = None
                }

                match! handler.Execute ctx with
                | Success -> ()
                | other -> failtestf "a manual dispatch should be a successful no-op, got %A" other
            }
        ]
    ]