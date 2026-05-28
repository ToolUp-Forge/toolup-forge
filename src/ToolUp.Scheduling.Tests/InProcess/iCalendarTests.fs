module ToolUp.Scheduling.Tests.InProcess.iCalendarTests

open System
open Expecto
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.iCalendar

// ─── iCalendar — parse + emit + round-trip tests ─────────────────
//
// `iCalendar` is a pure module; tests exercise it directly with
// inline iCal text. Vendor fixtures (Apple / Outlook / Google) are
// embedded as raw strings rather than file resources so the test
// project carries no Content items to copy at build time.

let private utc (y: int) (mo: int) (d: int) (h: int) (mi: int) (s: int) : DateTimeOffset =
    DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero)

let private parseOk (text: string) : VCalendar =
    match parse text with
    | Ok c -> c
    | Error e -> failtestf "parse failed: %s" e

let private singleEvent
    (uid: string)
    (summary: string)
    (start: DateTimeOffset)
    (rrule: RecurrenceRule option)
    : VCalendar =
    {
        Version = "2.0"
        ProdId = CanonicalProdId
        Events = [
            {
                Uid = uid
                Summary = summary
                DtStart = start
                DtEnd = start.AddHours(1.0)
                DtStamp = start
                Tzid = None
                RRule = rrule
                Description = None
            }
        ]
    }

// ─── Vendor fixtures (synthetic but format-faithful) ─────────────

let private appleFixture =
    "BEGIN:VCALENDAR\r\n"
    + "PRODID:-//Apple Inc.//macOS 14.0//EN\r\n"
    + "VERSION:2.0\r\n"
    + "CALSCALE:GREGORIAN\r\n"
    + "BEGIN:VEVENT\r\n"
    + "UID:0123456789ABCDEF@apple.com\r\n"
    + "DTSTAMP:20260530T120000Z\r\n"
    + "DTSTART:20260601T170000Z\r\n"
    + "DTEND:20260601T180000Z\r\n"
    + "SUMMARY:Team Sync\r\n"
    + "RRULE:FREQ=WEEKLY;BYDAY=MO\r\n"
    + "X-APPLE-TRAVEL-ADVISORY-BEHAVIOR:AUTOMATIC\r\n"
    + "END:VEVENT\r\n"
    + "END:VCALENDAR\r\n"

let private outlookFixture =
    "BEGIN:VCALENDAR\r\n"
    + "METHOD:PUBLISH\r\n"
    + "PRODID:Microsoft Exchange Server 2010\r\n"
    + "VERSION:2.0\r\n"
    + "BEGIN:VEVENT\r\n"
    + "ORGANIZER;CN=john@example.com:MAILTO:john@example.com\r\n"
    + "ATTENDEE;ROLE=REQ-PARTICIPANT;CN=jane@example.com:MAILTO:jane@example.com\r\n"
    + "DESCRIPTION:Quarterly planning meeting.\r\n"
    + "RRULE:FREQ=MONTHLY;COUNT=4\r\n"
    + "UID:040000008200E00074C5B7101A82E0080000000010000000@example.com\r\n"
    + "SUMMARY:Q3 Planning\r\n"
    + "DTSTART:20260901T130000Z\r\n"
    + "DTEND:20260901T140000Z\r\n"
    + "DTSTAMP:20260530T120000Z\r\n"
    + "CLASS:PUBLIC\r\n"
    + "TRANSP:OPAQUE\r\n"
    + "STATUS:CONFIRMED\r\n"
    + "END:VEVENT\r\n"
    + "END:VCALENDAR\r\n"

let private googleFixture =
    "BEGIN:VCALENDAR\r\n"
    + "PRODID:-//Google Inc//Google Calendar 70.9054//EN\r\n"
    + "VERSION:2.0\r\n"
    + "CALSCALE:GREGORIAN\r\n"
    + "METHOD:PUBLISH\r\n"
    + "X-WR-CALNAME:Personal\r\n"
    + "X-WR-TIMEZONE:Europe/London\r\n"
    + "BEGIN:VEVENT\r\n"
    + "DTSTART:20260601T090000Z\r\n"
    + "DTEND:20260601T100000Z\r\n"
    + "DTSTAMP:20260530T120000Z\r\n"
    + "UID:abc123@google.com\r\n"
    + "CREATED:20260530T120000Z\r\n"
    + "DESCRIPTION:Weekly review\r\n"
    + "LAST-MODIFIED:20260530T120000Z\r\n"
    + "LOCATION:Home Office\r\n"
    + "SEQUENCE:0\r\n"
    + "STATUS:CONFIRMED\r\n"
    + "SUMMARY:Weekly Review\r\n"
    + "TRANSP:OPAQUE\r\n"
    + "RRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;COUNT=20\r\n"
    + "END:VEVENT\r\n"
    + "END:VCALENDAR\r\n"

let tests =
    testList "iCalendar" [

        test "round-trip simple event" {
            let start = utc 2026 6 1 9 0 0
            let cal = singleEvent "uid-simple" "Hello" start None
            let text = emit cal
            let r = parseOk text
            Expect.equal r.Events.Length 1 "one event"
            let e = r.Events[0]
            Expect.equal e.Uid "uid-simple" "uid"
            Expect.equal e.Summary "Hello" "summary"
            Expect.equal e.DtStart start "dtstart"
            Expect.equal e.DtEnd (start.AddHours(1.0)) "dtend"
            Expect.isNone e.RRule "no rrule"
        }

        test "round-trip Daily count=5" {
            let start = utc 2026 6 1 9 0 0

            let rrule = {
                Frequency = Daily
                Interval = 1
                ByWeekday = []
                Until = None
                Count = Some 5
            }

            let cal = singleEvent "uid-daily" "Daily" start (Some rrule)
            let r = parseOk (emit cal)
            let e = r.Events[0]
            Expect.equal e.RRule (Some rrule) "rrule preserved"
        }

        test "round-trip Weekly with BYDAY=MO,WE,FR" {
            let start = utc 2026 6 1 9 0 0

            let rrule = {
                Frequency = Weekly
                Interval = 1
                ByWeekday = [ DayOfWeek.Monday; DayOfWeek.Wednesday; DayOfWeek.Friday ]
                Until = None
                Count = Some 12
            }

            let cal = singleEvent "uid-weekly" "Weekly" start (Some rrule)
            let r = parseOk (emit cal)
            let e = r.Events[0]
            Expect.equal e.RRule (Some rrule) "rrule preserved including weekday mask"
        }

        test "round-trip Monthly with UNTIL" {
            let start = utc 2026 1 15 12 0 0

            let rrule = {
                Frequency = Monthly
                Interval = 1
                ByWeekday = []
                Until = Some(utc 2027 1 15 12 0 0)
                Count = None
            }

            let cal = singleEvent "uid-monthly" "Monthly" start (Some rrule)
            let r = parseOk (emit cal)
            let e = r.Events[0]
            Expect.equal e.RRule (Some rrule) "rrule UNTIL preserved"
        }

        test "round-trip Yearly with INTERVAL=2" {
            let start = utc 2026 6 1 9 0 0

            let rrule = {
                Frequency = Yearly
                Interval = 2
                ByWeekday = []
                Until = None
                Count = Some 5
            }

            let cal = singleEvent "uid-yearly" "Yearly" start (Some rrule)
            let r = parseOk (emit cal)
            let e = r.Events[0]
            Expect.equal e.RRule (Some rrule) "rrule INTERVAL=2 preserved"
        }

        test "parses UTC datetime form (YYYYMMDDTHHMMSSZ)" {
            let text =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\n"
                + "BEGIN:VEVENT\r\nUID:u\r\nDTSTART:20260601T090000Z\r\nDTEND:20260601T100000Z\r\n"
                + "DTSTAMP:20260601T080000Z\r\nSUMMARY:t\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"

            let r = parseOk text
            Expect.equal r.Events[0].DtStart (utc 2026 6 1 9 0 0) "UTC parsed correctly"
        }

        test "parses floating + TZID and lifts to UTC" {
            let text =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\n"
                + "BEGIN:VEVENT\r\nUID:u\r\n"
                + "DTSTART;TZID=UTC:20260601T090000\r\n"
                + "DTEND;TZID=UTC:20260601T100000\r\n"
                + "DTSTAMP:20260601T080000Z\r\nSUMMARY:t\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"

            let r = parseOk text
            // UTC TZID resolves on every BCL platform; lift gives the same instant.
            Expect.equal r.Events[0].DtStart (utc 2026 6 1 9 0 0) "TZID=UTC preserves wall time"
            Expect.equal r.Events[0].Tzid (Some "UTC") "TZID preserved on parse"
        }

        test "parses date-only form as midnight UTC" {
            let text =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\n"
                + "BEGIN:VEVENT\r\nUID:u\r\nDTSTART:20260601\r\nDTEND:20260602\r\n"
                + "DTSTAMP:20260601T080000Z\r\nSUMMARY:t\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"

            let r = parseOk text
            Expect.equal r.Events[0].DtStart (utc 2026 6 1 0 0 0) "midnight UTC"
            Expect.equal r.Events[0].DtEnd (utc 2026 6 2 0 0 0) "midnight UTC"
        }

        test "emitter folds long lines at 75 octets" {
            let longSummary = String.replicate 200 "a"
            let start = utc 2026 6 1 9 0 0
            let cal = singleEvent "uid-long" longSummary start None
            let text = emit cal
            // Find the SUMMARY line (folded form may span multiple lines).
            // Each line in the output must be <= 75 octets before its CRLF.
            let lines = text.Split([| "\r\n" |], StringSplitOptions.None)

            for line in lines do
                Expect.isLessThanOrEqual line.Length 75 (sprintf "line %d chars: %s" line.Length line)
            // Round-trip preserves the long summary.
            let r = parseOk text
            Expect.equal r.Events[0].Summary longSummary "long summary survives fold/unfold"
        }

        test "parser unfolds continuation lines (CRLF + space)" {
            // Line is split across three physical lines with continuation.
            let text =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\n"
                + "BEGIN:VEVENT\r\nUID:u\r\nDTSTART:20260601T090000Z\r\nDTEND:20260601T100000Z\r\n"
                + "DTSTAMP:20260601T080000Z\r\n"
                + "SUMMARY:Hello\r\n World\r\n Again\r\n"
                + "END:VEVENT\r\nEND:VCALENDAR\r\n"

            let r = parseOk text
            Expect.equal r.Events[0].Summary "HelloWorldAgain" "continuation lines concatenated"
        }

        test "parser tolerates LF-only line endings" {
            let text =
                "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:test\n"
                + "BEGIN:VEVENT\nUID:u\nDTSTART:20260601T090000Z\nDTEND:20260601T100000Z\n"
                + "DTSTAMP:20260601T080000Z\nSUMMARY:t\nEND:VEVENT\nEND:VCALENDAR\n"

            let r = parseOk text
            Expect.equal r.Events.Length 1 "parsed despite LF-only endings"
        }

        test "Apple Calendar fixture parses without semantic drift" {
            let r1 = parseOk appleFixture
            Expect.equal r1.Events.Length 1 "one event"
            let e = r1.Events[0]
            Expect.equal e.Uid "0123456789ABCDEF@apple.com" "uid"
            Expect.equal e.Summary "Team Sync" "summary"
            Expect.equal e.DtStart (utc 2026 6 1 17 0 0) "dtstart UTC"
            Expect.isSome e.RRule "rrule present"
            Expect.equal e.RRule.Value.Frequency Weekly "weekly"
            Expect.equal e.RRule.Value.ByWeekday [ DayOfWeek.Monday ] "Monday"
            // Re-emit + re-parse → semantic equality on the first parse.
            let r2 = parseOk (emit r1)
            Expect.equal r2.Events r1.Events "round-trip semantically stable"
        }

        test "Outlook fixture parses and round-trips" {
            let r1 = parseOk outlookFixture
            Expect.equal r1.Events.Length 1 "one event"
            let e = r1.Events[0]
            Expect.equal e.Summary "Q3 Planning" "summary"
            Expect.equal e.DtStart (utc 2026 9 1 13 0 0) "dtstart UTC"
            Expect.isSome e.RRule "rrule present"
            Expect.equal e.RRule.Value.Frequency Monthly "monthly"
            Expect.equal e.RRule.Value.Count (Some 4) "count=4"
            let r2 = parseOk (emit r1)
            Expect.equal r2.Events r1.Events "round-trip stable"
        }

        test "Google Calendar fixture parses and round-trips" {
            let r1 = parseOk googleFixture
            Expect.equal r1.Events.Length 1 "one event"
            let e = r1.Events[0]
            Expect.equal e.Summary "Weekly Review" "summary"
            Expect.equal e.Description (Some "Weekly review") "description preserved"
            Expect.isSome e.RRule "rrule present"
            Expect.equal e.RRule.Value.Frequency Weekly "weekly"
            Expect.equal e.RRule.Value.ByWeekday.Length 5 "5 weekdays"
            Expect.equal e.RRule.Value.Count (Some 20) "count=20"
            let r2 = parseOk (emit r1)
            Expect.equal r2.Events r1.Events "round-trip stable"
        }

        test "empty calendar (no VEVENTs) parses to empty list" {
            let text = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\nEND:VCALENDAR\r\n"

            let r = parseOk text
            Expect.isEmpty r.Events "no events"
            Expect.equal r.Version "2.0" "version"
            Expect.equal r.ProdId "test" "prodid preserved"
        }

        test "calendar with multiple events parses each" {
            let text =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\n"
                + "BEGIN:VEVENT\r\nUID:u1\r\nDTSTART:20260601T090000Z\r\nDTEND:20260601T100000Z\r\n"
                + "DTSTAMP:20260601T080000Z\r\nSUMMARY:First\r\nEND:VEVENT\r\n"
                + "BEGIN:VEVENT\r\nUID:u2\r\nDTSTART:20260602T090000Z\r\nDTEND:20260602T100000Z\r\n"
                + "DTSTAMP:20260601T080000Z\r\nSUMMARY:Second\r\nEND:VEVENT\r\n"
                + "END:VCALENDAR\r\n"

            let r = parseOk text
            Expect.equal r.Events.Length 2 "two events"
            Expect.equal r.Events[0].Summary "First" "first"
            Expect.equal r.Events[1].Summary "Second" "second"
        }

        test "missing UID returns Error" {
            let text =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\n"
                + "BEGIN:VEVENT\r\nDTSTART:20260601T090000Z\r\nDTEND:20260601T100000Z\r\n"
                + "DTSTAMP:20260601T080000Z\r\nSUMMARY:t\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"

            match parse text with
            | Ok _ -> failtest "expected Error for missing UID"
            | Error e -> Expect.stringContains e "UID" "error mentions UID"
        }

        test "unknown FREQ returns Error" {
            let text =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\n"
                + "BEGIN:VEVENT\r\nUID:u\r\nDTSTART:20260601T090000Z\r\nDTEND:20260601T100000Z\r\n"
                + "DTSTAMP:20260601T080000Z\r\nSUMMARY:t\r\nRRULE:FREQ=HOURLY\r\n"
                + "END:VEVENT\r\nEND:VCALENDAR\r\n"

            match parse text with
            | Ok _ -> failtest "expected Error for unsupported FREQ"
            | Error e -> Expect.stringContains e "FREQ" "error mentions FREQ"
        }

        test "byte-stable round-trip on our own canonical emission" {
            // emit → parse → emit should produce identical bytes (byte-stable
            // round-trip property required by the worked-example acceptance
            // criterion). Vendor inputs aren't byte-stable because we drop
            // unknown properties, but our own emissions must be.
            let start = utc 2026 6 1 9 0 0

            let rrule = {
                Frequency = Weekly
                Interval = 2
                ByWeekday = [ DayOfWeek.Tuesday; DayOfWeek.Thursday ]
                Until = None
                Count = Some 8
            }

            let cal = singleEvent "uid-canonical" "Canonical" start (Some rrule)
            let text1 = emit cal
            let cal2 = parseOk text1
            let text2 = emit cal2
            Expect.equal text2 text1 "byte-stable round-trip"
        }
    ]