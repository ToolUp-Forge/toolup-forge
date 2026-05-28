module ToolUp.Scheduling.Tests.Contracts.IBookingSchedulerContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.SchedulingEvents
open ToolUp.Scheduling.IBookingScheduler

// ─── IBookingScheduler contract pack ─────────────────────────────
//
// Framework-agnostic test pack: takes a factory that produces a
// fresh `(IBookingScheduler, captured-events accessor, scopeId)`
// triple per test and exercises the public contract.
//
// Default impl bound by `BookingSchedulerTests.fs` over the test
// project's in-memory `IEntityStore` + `IEventStore` stubs. A
// distributed companion (Akka.NET / Orleans grain layer) would
// bind the same pack against its own factory and prove
// portability — same shape as `IEntityStoreContract` /
// `IJobSchedulerContract` packs in `ToolUp.Platform.Tests`.

type SchedulerFactory = unit -> IBookingScheduler * (unit -> ModuleEvent list) * string

let private utc (y: int) (mo: int) (d: int) (h: int) (mi: int) : DateTimeOffset =
    DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero)

let private timeOf (h: int) (mi: int) : TimeOnly = TimeOnly(h, mi)

let private makeResource (id: ResourceId) : BookableResource = {
    Id = id
    Type = "BookableResource"
    Version = 1
    ResourceType = "Person"
    DisplayName = id
    Timezone = "UTC"
    DefaultAvailability = [
        {
            DayOfWeek = None
            StartTime = timeOf 9 0
            EndTime = timeOf 17 0
            EffectiveFrom = None
            EffectiveTo = None
        }
    ]
    Metadata = Map.empty
}

let private makeBooking (id: BookingId) (resourceId: ResourceId) (start: DateTimeOffset) : Booking = {
    Id = id
    Type = "Booking"
    Version = 0
    ResourceId = resourceId
    Title = "test"
    StartUtc = start
    EndUtc = start.AddHours(1.0)
    Status = Confirmed
    BookedBy = "u1"
    BookedFor = None
    Recurrence = None
    ParentBookingId = None
    Metadata = Map.empty
}

let private mondayMorning = utc 2026 6 1 10 0

let tests (label: string) (factory: SchedulerFactory) =
    testList (sprintf "IBookingScheduler contract — %s" label) [

        testAsync "RegisterResource then GetResource returns the resource" {
            let scheduler, _, scopeId = factory ()
            let resource = makeResource "R1"
            let! reg = scheduler.RegisterResource(scopeId, resource)
            Expect.equal reg (Ok()) "register ok"

            let! fetched = scheduler.GetResource(scopeId, "R1")
            Expect.isSome fetched "resource present"
            Expect.equal fetched.Value.DisplayName "R1" "round-tripped"
        }

        testAsync "ListResources returns every registered resource" {
            let scheduler, _, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R2")
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R3")

            let! all = scheduler.ListResources scopeId
            Expect.equal all.Length 3 "three resources"
            Expect.equal (all |> List.map _.Id |> List.sort) [ "R1"; "R2"; "R3" ] "deterministic"
        }

        testAsync "Book against unknown resource returns UnknownResource" {
            let scheduler, _, scopeId = factory ()
            let booking = makeBooking "B1" "R-missing" mondayMorning
            let! result = scheduler.Book(scopeId, booking, "u1")
            Expect.equal result (Error(UnknownResource "R-missing")) "unknown"
        }

        testAsync "Book with no conflict succeeds and emits BookingCreated" {
            let scheduler, getEvents, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let booking = makeBooking "B1" "R1" mondayMorning
            let! result = scheduler.Book(scopeId, booking, "u1")

            match result with
            | Ok b -> Expect.equal b.Id "B1" "round-tripped"
            | Error e -> failtestf "expected Ok, got %A" e

            let events = getEvents ()

            let bookingCreated =
                events
                |> List.filter (fun e -> e.SourceModule = SourceModule && e.EventType = BookingCreated)

            Expect.equal bookingCreated.Length 1 "one BookingCreated emitted"
        }

        testAsync "Book with overlap returns Error Conflicts" {
            let scheduler, _, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let first = makeBooking "B1" "R1" mondayMorning
            let! _ = scheduler.Book(scopeId, first, "u1")

            let overlap = makeBooking "B2" "R1" (mondayMorning.AddMinutes(30.0))
            let! result = scheduler.Book(scopeId, overlap, "u1")

            match result with
            | Error(Conflicts cs) ->
                let hasOverlap =
                    cs
                    |> List.exists (fun c ->
                        match c with
                        | OverlappingBooking _ -> true
                        | _ -> false)

                Expect.isTrue hasOverlap "OverlappingBooking present"
            | other -> failtestf "expected Error Conflicts, got %A" other
        }

        testAsync "Cancel transitions booking to Cancelled and emits event" {
            let scheduler, getEvents, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let booking = makeBooking "B1" "R1" mondayMorning
            let! _ = scheduler.Book(scopeId, booking, "u1")

            let! cancelResult = scheduler.Cancel(scopeId, "B1", "no longer needed", "u1")
            Expect.equal cancelResult (Ok()) "cancel ok"

            let! fetched = scheduler.GetBooking(scopeId, "B1")
            Expect.equal fetched.Value.Status Cancelled "now cancelled"

            let cancelled =
                getEvents () |> List.filter (fun e -> e.EventType = BookingCancelled)

            Expect.equal cancelled.Length 1 "one BookingCancelled emitted"
        }

        testAsync "Cancel is idempotent on already-cancelled booking" {
            let scheduler, getEvents, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let booking = makeBooking "B1" "R1" mondayMorning
            let! _ = scheduler.Book(scopeId, booking, "u1")
            let! _ = scheduler.Cancel(scopeId, "B1", "first", "u1")
            let! second = scheduler.Cancel(scopeId, "B1", "second", "u1")
            Expect.equal second (Ok()) "still ok"

            let cancelled =
                getEvents () |> List.filter (fun e -> e.EventType = BookingCancelled)

            Expect.equal cancelled.Length 1 "no second event"
        }

        testAsync "Reschedule moves the booking and emits event" {
            let scheduler, getEvents, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let booking = makeBooking "B1" "R1" mondayMorning
            let! _ = scheduler.Book(scopeId, booking, "u1")

            let newStart = mondayMorning.AddHours(3.0)
            let newEnd = newStart.AddHours(1.0)
            let! result = scheduler.Reschedule(scopeId, "B1", newStart, newEnd, "u1")

            match result with
            | Ok b ->
                Expect.equal b.StartUtc newStart "moved"
                Expect.equal b.EndUtc newEnd "moved end"
            | Error e -> failtestf "expected Ok, got %A" e

            let rescheduled =
                getEvents () |> List.filter (fun e -> e.EventType = BookingRescheduled)

            Expect.equal rescheduled.Length 1 "one BookingRescheduled emitted"
        }

        testAsync "Reschedule with conflict returns Error Conflicts" {
            let scheduler, _, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let! _ = scheduler.Book(scopeId, makeBooking "B1" "R1" mondayMorning, "u1")
            let! _ = scheduler.Book(scopeId, makeBooking "B2" "R1" (mondayMorning.AddHours(2.0)), "u1")

            // Move B1 onto B2's slot.
            let conflictStart = mondayMorning.AddHours(2.0)
            let! result = scheduler.Reschedule(scopeId, "B1", conflictStart, conflictStart.AddHours(1.0), "u1")

            match result with
            | Error(Conflicts _) -> ()
            | other -> failtestf "expected Conflicts, got %A" other
        }

        testAsync "Reschedule unknown booking returns UnknownBooking" {
            let scheduler, _, scopeId = factory ()
            let! result = scheduler.Reschedule(scopeId, "B-missing", mondayMorning, mondayMorning.AddHours(1.0), "u1")
            Expect.equal result (Error(UnknownBooking "B-missing")) "unknown booking"
        }

        testAsync "MarkNoShow on Confirmed booking emits event and transitions" {
            let scheduler, getEvents, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let! _ = scheduler.Book(scopeId, makeBooking "B1" "R1" mondayMorning, "u1")

            let! result = scheduler.MarkNoShow(scopeId, "B1", "u1")
            Expect.equal result (Ok()) "marked"

            let! fetched = scheduler.GetBooking(scopeId, "B1")
            Expect.equal fetched.Value.Status NoShow "now no-show"

            let evt = getEvents () |> List.filter (fun e -> e.EventType = BookingNoShow)

            Expect.equal evt.Length 1 "one BookingNoShow emitted"
        }

        testAsync "ListBookings filters by date range" {
            let scheduler, _, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let! _ = scheduler.Book(scopeId, makeBooking "B1" "R1" mondayMorning, "u1")
            let! _ = scheduler.Book(scopeId, makeBooking "B2" "R1" (mondayMorning.AddDays(7.0)), "u1")
            let! _ = scheduler.Book(scopeId, makeBooking "B3" "R1" (mondayMorning.AddDays(14.0)), "u1")

            let window = {
                Start = mondayMorning
                End = mondayMorning.AddDays(8.0)
            }

            let! found = scheduler.ListBookings(scopeId, "R1", window)
            Expect.equal (found |> List.map _.Id |> List.sort) [ "B1"; "B2" ] "B1 + B2 only"
        }

        testAsync "AddAvailabilityException then ListAvailabilityExceptions returns it" {
            let scheduler, _, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")

            let exc: AvailabilityException = {
                Id = "ex1"
                Type = "AvailabilityException"
                Version = 0
                ResourceId = "R1"
                Date = DateOnly(2026, 6, 1)
                Kind = FullDay
                StartTime = None
                EndTime = None
                Reason = Some "Holiday"
            }

            let! addResult = scheduler.AddAvailabilityException(scopeId, exc)
            Expect.equal addResult (Ok()) "added"

            let window = {
                Start = mondayMorning
                End = mondayMorning.AddDays(7.0)
            }

            let! fetched = scheduler.ListAvailabilityExceptions(scopeId, "R1", window)
            Expect.equal fetched.Length 1 "one exception"
            Expect.equal fetched[0].Id "ex1" "id"
        }

        testAsync "DetectConflicts returns OverlappingBooking for overlap" {
            let scheduler, _, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")
            let! _ = scheduler.Book(scopeId, makeBooking "B1" "R1" mondayMorning, "u1")

            let probe = makeBooking "B-probe" "R1" (mondayMorning.AddMinutes(30.0))
            let! conflicts = scheduler.DetectConflicts(scopeId, probe)

            let hasOverlap =
                conflicts
                |> List.exists (fun c ->
                    match c with
                    | OverlappingBooking _ -> true
                    | _ -> false)

            Expect.isTrue hasOverlap "OverlappingBooking present"
        }

        testAsync "ExpandRecurrence expands a Daily count=4 booking" {
            let scheduler, _, scopeId = factory ()
            let! _ = scheduler.RegisterResource(scopeId, makeResource "R1")

            let rrule = {
                Frequency = Daily
                Interval = 1
                ByWeekday = []
                Until = None
                Count = Some 4
            }

            let booking = {
                makeBooking "B1" "R1" mondayMorning with
                    Recurrence = Some rrule
            }

            let window = {
                Start = mondayMorning
                End = mondayMorning.AddDays(10.0)
            }

            let! occurrences = scheduler.ExpandRecurrence(scopeId, booking, window)
            Expect.equal occurrences.Length 4 "4 occurrences"
        }
    ]