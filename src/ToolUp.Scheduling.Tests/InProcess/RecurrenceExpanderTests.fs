module ToolUp.Scheduling.Tests.InProcess.RecurrenceExpanderTests

open System
open Expecto
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.RecurrenceExpander

// ─── RecurrenceExpander — pure unit tests ────────────────────────
//
// `RecurrenceExpander` is a pure module — no IO, no DI — so tests
// exercise it directly with literal seeds + rules + windows.
//
// The seed used across most tests is Mon 2026-06-01 09:00 UTC.
// Picking a Monday makes the Weekly + ByWeekday cases easy to read.

let private seedAt (instant: DateTimeOffset) : Booking = {
    Id = "B1"
    Type = "Booking"
    Version = 0
    ResourceId = "R1"
    Title = "test"
    StartUtc = instant
    EndUtc = instant.AddHours(1.0)
    Status = Confirmed
    BookedBy = "u1"
    BookedFor = None
    Recurrence = None
    ParentBookingId = None
    Metadata = Map.empty
}

let private mondaySeed = DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)

let private wideWindow = {
    Start = DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
    End = DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
}

let tests =
    testList "RecurrenceExpander" [

        test "Daily interval=1 count=5 emits 5 occurrences one day apart" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Daily
                Interval = 1
                ByWeekday = []
                Until = None
                Count = Some 5
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 5 "5 occurrences"

            let starts = occurrences |> List.map _.StartUtc

            Expect.equal
                starts
                [
                    mondaySeed
                    mondaySeed.AddDays(1.0)
                    mondaySeed.AddDays(2.0)
                    mondaySeed.AddDays(3.0)
                    mondaySeed.AddDays(4.0)
                ]
                "consecutive days"
        }

        test "Daily interval=2 until=seed+10d emits 5 occurrences" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Daily
                Interval = 2
                ByWeekday = []
                Until = Some(mondaySeed.AddDays(10.0))
                Count = None
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 5 "0,2,4,6,8 days = 5 occurrences"
            let lastStart = (List.last occurrences).StartUtc
            Expect.equal lastStart (mondaySeed.AddDays(8.0)) "last occurrence at +8 days (next would hit +10 = until)"
        }

        test "Weekly interval=1 ByWeekday=Mon,Wed,Fri count=10" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Weekly
                Interval = 1
                ByWeekday = [ DayOfWeek.Monday; DayOfWeek.Wednesday; DayOfWeek.Friday ]
                Until = None
                Count = Some 10
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 10 "10 occurrences"

            // Every emitted occurrence must be one of {Mon, Wed, Fri}.
            let allowed = Set.ofList [ DayOfWeek.Monday; DayOfWeek.Wednesday; DayOfWeek.Friday ]

            for o in occurrences do
                Expect.isTrue
                    (Set.contains o.StartUtc.DayOfWeek allowed)
                    (sprintf "occurrence %O is %A — not in mask" o.StartUtc o.StartUtc.DayOfWeek)

            // Counts per weekday should match the M/W/F-then-cycle pattern:
            // 10 occurrences = 3 full M/W/F cycles (9) + 1 (Mon of week 4).
            let monCount =
                occurrences
                |> List.filter (fun o -> o.StartUtc.DayOfWeek = DayOfWeek.Monday)
                |> List.length

            Expect.equal monCount 4 "Mondays: 4 (week 1, 2, 3, 4)"
        }

        test "Weekly interval=2 ByWeekday=Tue count=4 — every other Tuesday" {
            // Seed is Mon 2026-06-01. First Tuesday is 2026-06-02.
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Weekly
                Interval = 2
                ByWeekday = [ DayOfWeek.Tuesday ]
                Until = None
                Count = Some 4
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 4 "4 Tuesdays"

            for o in occurrences do
                Expect.equal o.StartUtc.DayOfWeek DayOfWeek.Tuesday "Tuesday"

            let starts = occurrences |> List.map _.StartUtc

            // Tue 2026-06-02 is in week 0 (since seed Mon Jun 1 / 7 = 0).
            // Then week 2, 4, 6 (every other week starting from seed).
            let firstTue = DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero)

            Expect.equal
                starts
                [
                    firstTue
                    firstTue.AddDays(14.0)
                    firstTue.AddDays(28.0)
                    firstTue.AddDays(42.0)
                ]
                "two-week stride between Tuesdays"
        }

        test "Monthly interval=1 count=12 — UTC arithmetic doesn't drift across DST" {
            // Seed in March (post-DST start in Europe), continuing through
            // November (post-DST end). UTC arithmetic should preserve the
            // hour-of-day on every occurrence regardless of local DST.
            let seedInstant = DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero)
            let seed = seedAt seedInstant

            let rule = {
                Frequency = Monthly
                Interval = 1
                ByWeekday = []
                Until = None
                Count = Some 12
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 12 "12 monthly occurrences"

            for o in occurrences do
                Expect.equal o.StartUtc.Hour 12 "hour preserved"
                Expect.equal o.StartUtc.Minute 0 "minute preserved"
                Expect.equal o.StartUtc.Day 15 "day-of-month preserved"
        }

        test "Yearly interval=1 count=3 preserves time of day" {
            let seedInstant = DateTimeOffset(2026, 6, 1, 9, 30, 0, TimeSpan.Zero)
            let seed = seedAt seedInstant

            let rule = {
                Frequency = Yearly
                Interval = 1
                ByWeekday = []
                Until = None
                Count = Some 3
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 3 "3 yearly occurrences"
            let starts = occurrences |> List.map _.StartUtc

            Expect.equal
                starts
                [
                    seedInstant
                    DateTimeOffset(2027, 6, 1, 9, 30, 0, TimeSpan.Zero)
                    DateTimeOffset(2028, 6, 1, 9, 30, 0, TimeSpan.Zero)
                ]
                "year-step preserved"
        }

        test "Window narrower than seed yields empty list" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Daily
                Interval = 1
                ByWeekday = []
                Until = None
                Count = Some 5
            }

            let narrowWindow = {
                Start = mondaySeed.AddDays(-10.0)
                End = mondaySeed.AddDays(-5.0)
            }

            let occurrences = expand seed rule narrowWindow
            Expect.isEmpty occurrences "no occurrences before seed"
        }

        test "Until earlier than seed yields empty list" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Daily
                Interval = 1
                ByWeekday = []
                Until = Some(mondaySeed.AddDays(-1.0))
                Count = None
            }

            let occurrences = expand seed rule wideWindow
            Expect.isEmpty occurrences "until in the past terminates immediately"
        }

        test "Both Until and Count — Count exhausted first" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Daily
                Interval = 1
                ByWeekday = []
                Until = Some(mondaySeed.AddDays(100.0)) // far beyond Count
                Count = Some 3
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 3 "count caps at 3"
        }

        test "Both Until and Count — Until reached first" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Daily
                Interval = 1
                ByWeekday = []
                Until = Some(mondaySeed.AddDays(3.0))
                Count = Some 100
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 3 "0,1,2 = 3 occurrences (until=+3 exclusive)"
        }

        test "Hard cap stops pathological Count without raising" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Daily
                Interval = 1
                ByWeekday = []
                Until = None
                Count = Some 20_000
            }

            // Window span chosen so it doesn't bound before the hard cap.
            let openWindow = {
                Start = mondaySeed.AddDays(-1.0)
                End = mondaySeed.AddDays(50_000.0)
            }

            let occurrences = expand seed rule openWindow
            Expect.equal occurrences.Length 10_000 "hard cap enforced"
        }

        test "Weekly with empty ByWeekday strides 7*Interval days" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Weekly
                Interval = 2
                ByWeekday = []
                Until = None
                Count = Some 4
            }

            let occurrences = expand seed rule wideWindow
            Expect.equal occurrences.Length 4 "4 occurrences"

            let starts = occurrences |> List.map _.StartUtc

            Expect.equal
                starts
                [
                    mondaySeed
                    mondaySeed.AddDays(14.0)
                    mondaySeed.AddDays(28.0)
                    mondaySeed.AddDays(42.0)
                ]
                "fortnight stride"

            // All on the same weekday (since the stride is a multiple of 7).
            for o in occurrences do
                Expect.equal o.StartUtc.DayOfWeek DayOfWeek.Monday "stays on Monday"
        }

        test "Invalid rule (Interval = 0) yields empty list" {
            let seed = seedAt mondaySeed

            let rule = {
                Frequency = Daily
                Interval = 0
                ByWeekday = []
                Until = None
                Count = Some 5
            }

            let occurrences = expand seed rule wideWindow
            Expect.isEmpty occurrences "structurally invalid rule"
        }
    ]