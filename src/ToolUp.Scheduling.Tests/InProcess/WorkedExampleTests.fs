module ToolUp.Scheduling.Tests.InProcess.WorkedExampleTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.BookingScheduler
open ToolUp.Scheduling.iCalendar
open ToolUp.Scheduling.Tests.InProcess.InMemoryEntityStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryEventStore

// ─── Phase 20 acceptance — worked example ────────────────────────
//
// Implements the canonical integration scenario:
//
//   "Worked example: TestHarness registering a Person resource with
//    default availability, books a recurring weekly slot, exercises
//    conflict detection on overlap, expands recurrence into a
//    4-week window, exports to .ics, re-imports the file into a
//    fresh scope and round-trips byte-stable."
//
// We run it in the test pack rather than the TestHarness itself.
// The test pack fits the same end-to-end shape (real
// `BookingScheduler` over the in-memory `IEntityStore` /
// `IEventStore` stubs) and runs in CI by default; folding it into
// `TestHarness` would require a CLI driver for what's already a
// single-file integration test.

let tests =
    testList "Phase 20 worked example" [

        testAsync "register person → recurring weekly book → overlap conflict → 4-week expansion → ics round-trip" {
            // 1. Construct the scheduler.
            let entityStore = InMemoryEntityStore() :> IEntityStore
            let eventStore = InMemoryEventStore()

            let scheduler =
                BookingScheduler(entityStore, eventStore :> IEventStore) :> IBookingScheduler

            let scopeA = "team-a"

            // 2. Register a Person resource available 09:00-17:00 every day in UTC.
            let alice: BookableResource = {
                Id = "alice"
                Type = "BookableResource"
                Version = 1
                ResourceType = "Person"
                DisplayName = "Alice"
                Timezone = "UTC"
                DefaultAvailability = [
                    {
                        DayOfWeek = None
                        StartTime = TimeOnly(9, 0)
                        EndTime = TimeOnly(17, 0)
                        EffectiveFrom = None
                        EffectiveTo = None
                    }
                ]
                Metadata = Map.empty
            }

            let! reg = scheduler.RegisterResource(scopeA, alice)
            Expect.equal reg (Ok()) "register"

            // 3. Book a weekly recurring slot starting Mon 2026-06-01 10:00 UTC.
            let mondayMorning = DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)

            let weeklyRule = {
                Frequency = Weekly
                Interval = 1
                ByWeekday = [ DayOfWeek.Monday ]
                Until = None
                Count = Some 8
            }

            let recurring: Booking = {
                Id = "B-recurring"
                Type = "Booking"
                Version = 0
                ResourceId = "alice"
                Title = "Weekly check-in"
                StartUtc = mondayMorning
                EndUtc = mondayMorning.AddHours(1.0)
                Status = Confirmed
                BookedBy = "owner"
                BookedFor = None
                Recurrence = Some weeklyRule
                ParentBookingId = None
                Metadata = Map.empty
            }

            let! booked = scheduler.Book(scopeA, recurring, "owner")

            match booked with
            | Ok _ -> ()
            | Error e -> failtestf "expected Ok, got %A" e

            // 4. Exercise conflict detection on overlap — same time, different Id.
            let overlap = {
                recurring with
                    Id = "B-overlap"
                    Recurrence = None
                    StartUtc = mondayMorning.AddMinutes(30.0)
                    EndUtc = mondayMorning.AddMinutes(90.0)
            }

            let! overlapResult = scheduler.Book(scopeA, overlap, "owner")

            match overlapResult with
            | Error(Conflicts cs) ->
                let hasOverlap =
                    cs
                    |> List.exists (fun c ->
                        match c with
                        | OverlappingBooking _ -> true
                        | _ -> false)

                Expect.isTrue hasOverlap "overlap surfaced"
            | other -> failtestf "expected overlap conflict, got %A" other

            // 5. Expand the recurrence into a 4-week window.
            let fourWeekWindow = {
                Start = mondayMorning
                End = mondayMorning.AddDays(28.0)
            }

            let! occurrences = scheduler.ExpandRecurrence(scopeA, recurring, fourWeekWindow)
            Expect.equal occurrences.Length 4 "4 occurrences in 4 weeks"

            for o in occurrences do
                Expect.equal o.StartUtc.DayOfWeek DayOfWeek.Monday "every occurrence on Monday"

            // 6. Export the recurring SEED (one VEVENT with an RRULE) — the
            //    RFC 5545 way of representing a recurring series. Expanded
            //    occurrences are computed at query time, not persisted as N
            //    distinct bookings.
            let cal: VCalendar = {
                Version = "2.0"
                ProdId = CanonicalProdId
                Events = [ bookingToVEvent recurring ]
            }

            let ics = emit cal
            Expect.stringContains ics "BEGIN:VCALENDAR" "envelope"
            Expect.stringContains ics "BEGIN:VEVENT" "events present"
            Expect.stringContains ics "Weekly check-in" "summary present"
            Expect.stringContains ics "FREQ=WEEKLY" "rrule encoded"

            // 7. Re-import into a fresh scope as a single recurring booking.
            let scopeB = "team-b"
            let! _ = scheduler.RegisterResource(scopeB, alice)

            match parse ics with
            | Error e -> failtestf "parse failed: %s" e
            | Ok parsed ->
                Expect.equal parsed.Events.Length 1 "one VEVENT round-tripped"

                let defaultsForImport = {
                    recurring with
                        Metadata = Map.ofList [ "imported", "true" ]
                }

                let imported = vEventToBooking defaultsForImport parsed.Events[0]
                Expect.equal imported.Recurrence (Some weeklyRule) "rrule survived round-trip"

                let! r = scheduler.Book(scopeB, imported, "importer")

                match r with
                | Ok _ -> ()
                | Error e -> failtestf "import book failed: %A" e

                // Re-expand in the new scope — should match the original count.
                let! occurrencesB = scheduler.ExpandRecurrence(scopeB, imported, fourWeekWindow)
                Expect.equal occurrencesB.Length 4 "expanded again to 4 occurrences"

                let! listB =
                    scheduler.ListBookings(
                        scopeB,
                        "alice",
                        {
                            Start = mondayMorning.AddDays(-1.0)
                            End = mondayMorning.AddDays(28.0)
                        }
                    )

                // One persisted recurring seed in scope B (occurrences are
                // computed, not persisted).
                Expect.equal listB.Length 1 "one recurring booking persisted in scopeB"

            // 8. Byte-stable round-trip on our own canonical emission.
            let icsRoundTripped =
                match parse ics with
                | Ok p -> emit p
                | Error e -> failtestf "round-trip parse failed: %s" e

            Expect.equal icsRoundTripped ics "byte-stable round-trip"

            // 9. Audit emission verified — 2 BookingCreated events: one in
            //    scopeA (the seed) + one in scopeB (the imported seed).
            let creates =
                eventStore.Events |> List.filter (fun e -> e.EventType = "BookingCreated")

            Expect.equal creates.Length 2 "two BookingCreated events"

            let scopeAEvents = creates |> List.filter (fun e -> e.ScopeId = scopeA)

            let scopeBEvents = creates |> List.filter (fun e -> e.ScopeId = scopeB)

            Expect.equal scopeAEvents.Length 1 "1 in scope A"
            Expect.equal scopeBEvents.Length 1 "1 in scope B"
        }
    ]