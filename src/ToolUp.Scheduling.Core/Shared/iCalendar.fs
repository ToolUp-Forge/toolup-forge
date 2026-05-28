module ToolUp.Scheduling.iCalendar

open System
open System.Text
open ToolUp.Scheduling.SchedulingTypes

// ─── Phase 20 — RFC 5545 (iCalendar) parser + emitter ───────────────
//
// Pure F# implementation. No third-party dependency (e.g., Ical.Net),
// because Ical.Net is paid-licensed and Phase 20 is no-paid-deps by
// default (Guiding Principle 2).
//
// **Supported subset**:
//   * `VCALENDAR` envelope with `VERSION` / `PRODID` / `CALSCALE`.
//   * `VEVENT` blocks with `UID` / `SUMMARY` / `DTSTART` / `DTEND` /
//     `DTSTAMP` / `RRULE` / `DESCRIPTION`.
//   * `RRULE` with `FREQ` (DAILY/WEEKLY/MONTHLY/YEARLY) / `INTERVAL` /
//     `BYDAY` (with weekday tokens MO/TU/.../SU; positional offsets
//     like 1MO not supported) / `UNTIL` / `COUNT`.
//   * `DTSTART` / `DTEND` / `DTSTAMP` / `UNTIL` in UTC form
//     (`YYYYMMDDTHHMMSSZ`) or floating-with-TZID form
//     (`DTSTART;TZID=Europe/London:YYYYMMDDTHHMMSS`).
//   * Date-only form (`YYYYMMDD`) — interpreted as midnight UTC.
//
// **Not supported**:
//   * `VTODO` / `VJOURNAL` / `VFREEBUSY` / `VTIMEZONE` definitions —
//     `TZID` parameters look up via `TimeZoneInfo.FindSystemTimeZoneById`
//     against the host BCL.
//   * Positional `BYDAY` (`1MO`, `-1FR`), `BYMONTHDAY`, `BYSETPOS`,
//     `BYHOUR`, `BYMINUTE` — deferred (matches `RecurrenceExpander`'s
//     v1 capability).
//   * `ATTENDEE` / `ORGANIZER` / `CATEGORIES` / `LOCATION` — silently
//     dropped on parse, never emitted. Apps wanting them ride them
//     through `Booking.Metadata` round-tripped on `X-TOOLUP-META-*`.
//   * X-properties — silently dropped on parse, never emitted.
//
// **Line discipline**: RFC 5545 mandates CRLF line endings and line
// folding at 75 octets. Parser unfolds (treating any continuation
// line — one starting with whitespace — as part of the prior line).
// Emitter folds at 74 octets followed by `\r\n ` (CRLF + space).

/// One parsed VEVENT. All instants are normalised to UTC after parse.
/// `Tzid` is preserved for round-trip when the source carried a TZID
/// parameter on `DTSTART`; otherwise `None`.
type VEvent = {
    Uid: string
    Summary: string
    DtStart: DateTimeOffset
    DtEnd: DateTimeOffset
    DtStamp: DateTimeOffset
    Tzid: string option
    RRule: RecurrenceRule option
    Description: string option
}

/// One parsed VCALENDAR. `ProdId` is preserved from the source on
/// parse (or `"-//ToolUp//Scheduling 1.0//EN"` for our own emissions).
type VCalendar = {
    Version: string
    ProdId: string
    Events: VEvent list
}

/// Canonical emitter `PRODID` for ToolUp-emitted calendars. Vendor
/// exports preserve their own `PRODID` through the round-trip.
[<Literal>]
let CanonicalProdId = "-//ToolUp//Scheduling 1.0//EN"

// ─── Line discipline: unfold + fold ─────────────────────────────────

/// Unfold a raw iCalendar text into logical lines per RFC 5545
/// section 3.1. Continuation lines start with a single space or tab;
/// the parser appends them (sans leading whitespace) to the prior
/// line. Accepts either CRLF or LF line terminators (vendor exports
/// in the wild aren't always strict).
let private unfoldLines (raw: string) : string list =
    let lines = raw.Replace("\r\n", "\n").Split('\n')
    let mutable result = []
    let buffer = StringBuilder()

    let flush () =
        if buffer.Length > 0 then
            result <- buffer.ToString() :: result
            buffer.Clear() |> ignore

    for line in lines do
        if line.Length > 0 && (line[0] = ' ' || line[0] = '\t') then
            buffer.Append(line.Substring(1)) |> ignore
        else
            flush ()
            buffer.Append(line) |> ignore

    flush ()
    result |> List.rev |> List.filter (fun l -> l.Length > 0)

/// Fold a logical line into the on-the-wire form per RFC 5545. Lines
/// longer than 74 octets are split at the 74th, with continuation
/// lines starting with a single space.
let private foldLine (line: string) : string =
    if line.Length <= 75 then
        line + "\r\n"
    else
        let sb = StringBuilder()
        sb.Append(line.Substring(0, 75)) |> ignore
        sb.Append("\r\n") |> ignore
        let mutable cursor = 75

        while cursor < line.Length do
            let take = min 74 (line.Length - cursor)
            sb.Append(' ') |> ignore
            sb.Append(line.Substring(cursor, take)) |> ignore
            sb.Append("\r\n") |> ignore
            cursor <- cursor + take

        sb.ToString()

// ─── Property line: NAME(;PARAM=VALUE)*:VALUE ───────────────────────

type private PropertyLine = {
    Name: string
    Params: Map<string, string>
    Value: string
}

let private parsePropertyLine (line: string) : Result<PropertyLine, string> =
    let colonIdx = line.IndexOf(':')

    if colonIdx < 0 then
        Error(sprintf "Property line missing ':': %s" line)
    else
        let head = line.Substring(0, colonIdx)
        let value = line.Substring(colonIdx + 1)
        let parts = head.Split(';')
        let name = parts[0].ToUpperInvariant()

        let parameters =
            parts
            |> Array.skip 1
            |> Array.choose (fun p ->
                let eqIdx = p.IndexOf('=')

                if eqIdx < 0 then
                    None
                else
                    let k = p.Substring(0, eqIdx).ToUpperInvariant()
                    let v = p.Substring(eqIdx + 1)
                    Some(k, v))
            |> Map.ofArray

        Ok {
            Name = name
            Params = parameters
            Value = value
        }

// ─── DateTime / DateOnly parsing ────────────────────────────────────

let private parseIcalDateTime (value: string) (tzid: string option) : Result<DateTimeOffset, string> =
    // Date-only: "YYYYMMDD" (8 chars).
    if value.Length = 8 then
        match DateTime.TryParseExact(value, "yyyyMMdd", null, Globalization.DateTimeStyles.None) with
        | true, d -> Ok(DateTimeOffset(d, TimeSpan.Zero))
        | _ -> Error(sprintf "Invalid date-only value: %s" value)
    // UTC: "YYYYMMDDTHHMMSSZ" (16 chars, ending Z).
    elif value.Length = 16 && value.EndsWith("Z") then
        let core = value.Substring(0, 15)

        match DateTime.TryParseExact(core, "yyyyMMddTHHmmss", null, Globalization.DateTimeStyles.AssumeUniversal) with
        | true, d -> Ok(DateTimeOffset(d.ToUniversalTime(), TimeSpan.Zero))
        | _ -> Error(sprintf "Invalid UTC datetime: %s" value)
    // Floating with TZID: "YYYYMMDDTHHMMSS" (15 chars, no Z).
    elif value.Length = 15 then
        match DateTime.TryParseExact(value, "yyyyMMddTHHmmss", null, Globalization.DateTimeStyles.None) with
        | true, d ->
            match tzid with
            | None ->
                // Floating without TZID — interpret as UTC.
                Ok(DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc), TimeSpan.Zero))
            | Some zone ->
                try
                    let tz = TimeZoneInfo.FindSystemTimeZoneById(zone)
                    let offset = tz.GetUtcOffset(d)
                    let local = DateTimeOffset(d, offset)
                    Ok(local.ToUniversalTime())
                with _ ->
                    Error(sprintf "Unknown TZID: %s" zone)
        | _ -> Error(sprintf "Invalid floating datetime: %s" value)
    else
        Error(sprintf "Unrecognised datetime length %d: %s" value.Length value)

let private dayOfWeekFromIcal (token: string) : DayOfWeek option =
    // Strip leading positional prefix (e.g., "1MO" → "MO" — positional
    // offsets are ignored in v1 per the supported subset).
    let stripped =
        let mutable i = 0

        while i < token.Length && (Char.IsDigit(token[i]) || token[i] = '+' || token[i] = '-') do
            i <- i + 1

        token.Substring(i).ToUpperInvariant()

    match stripped with
    | "MO" -> Some DayOfWeek.Monday
    | "TU" -> Some DayOfWeek.Tuesday
    | "WE" -> Some DayOfWeek.Wednesday
    | "TH" -> Some DayOfWeek.Thursday
    | "FR" -> Some DayOfWeek.Friday
    | "SA" -> Some DayOfWeek.Saturday
    | "SU" -> Some DayOfWeek.Sunday
    | _ -> None

// ─── RRULE parsing ──────────────────────────────────────────────────

let private parseRRule (value: string) : Result<RecurrenceRule, string> =
    let parts =
        value.Split(';')
        |> Array.choose (fun p ->
            let eqIdx = p.IndexOf('=')

            if eqIdx < 0 then
                None
            else
                Some(p.Substring(0, eqIdx).ToUpperInvariant(), p.Substring(eqIdx + 1)))
        |> Map.ofArray

    let frequency =
        match Map.tryFind "FREQ" parts with
        | Some "DAILY" -> Ok Daily
        | Some "WEEKLY" -> Ok Weekly
        | Some "MONTHLY" -> Ok Monthly
        | Some "YEARLY" -> Ok Yearly
        | Some other -> Error(sprintf "Unsupported FREQ: %s" other)
        | None -> Error "RRULE missing FREQ"

    let interval =
        match Map.tryFind "INTERVAL" parts with
        | Some s ->
            match Int32.TryParse s with
            | true, n when n > 0 -> Ok n
            | _ -> Error(sprintf "Invalid INTERVAL: %s" s)
        | None -> Ok 1

    let byWeekday =
        match Map.tryFind "BYDAY" parts with
        | Some s -> s.Split(',') |> Array.toList |> List.choose dayOfWeekFromIcal
        | None -> []

    let until =
        match Map.tryFind "UNTIL" parts with
        | Some s ->
            match parseIcalDateTime s None with
            | Ok dt -> Some(Some dt)
            | Error _ -> Some None // malformed UNTIL — treat as absent
        | None -> None
        |> Option.defaultValue None

    let count =
        match Map.tryFind "COUNT" parts with
        | Some s ->
            match Int32.TryParse s with
            | true, n when n > 0 -> Some n
            | _ -> None
        | None -> None

    match frequency, interval with
    | Ok freq, Ok ivl ->
        Ok {
            Frequency = freq
            Interval = ivl
            ByWeekday = byWeekday
            Until = until
            Count = count
        }
    | Error e, _
    | _, Error e -> Error e

// ─── VEVENT parsing ─────────────────────────────────────────────────

let private parseVEvent (lines: string list) : Result<VEvent, string> =
    let mutable uid = None
    let mutable summary = ""
    let mutable dtStart = None
    let mutable dtEnd = None
    let mutable dtStamp = None
    let mutable tzid = None
    let mutable rrule = None
    let mutable description = None
    let mutable error = None

    for line in lines do
        if error.IsNone then
            match parsePropertyLine line with
            | Error e -> error <- Some e
            | Ok prop ->
                match prop.Name with
                | "UID" -> uid <- Some prop.Value
                | "SUMMARY" -> summary <- prop.Value
                | "DESCRIPTION" -> description <- Some prop.Value
                | "DTSTART" ->
                    let propTzid = Map.tryFind "TZID" prop.Params

                    match parseIcalDateTime prop.Value propTzid with
                    | Ok dt ->
                        dtStart <- Some dt

                        if propTzid.IsSome then
                            tzid <- propTzid
                    | Error e -> error <- Some(sprintf "DTSTART: %s" e)
                | "DTEND" ->
                    let propTzid = Map.tryFind "TZID" prop.Params

                    match parseIcalDateTime prop.Value propTzid with
                    | Ok dt -> dtEnd <- Some dt
                    | Error e -> error <- Some(sprintf "DTEND: %s" e)
                | "DTSTAMP" ->
                    match parseIcalDateTime prop.Value None with
                    | Ok dt -> dtStamp <- Some dt
                    | Error e -> error <- Some(sprintf "DTSTAMP: %s" e)
                | "RRULE" ->
                    match parseRRule prop.Value with
                    | Ok r -> rrule <- Some r
                    | Error e -> error <- Some(sprintf "RRULE: %s" e)
                | _ -> () // silently drop unsupported properties

    match error with
    | Some e -> Error e
    | None ->
        match uid, dtStart, dtEnd with
        | Some u, Some s, Some e ->
            Ok {
                Uid = u
                Summary = summary
                DtStart = s
                DtEnd = e
                DtStamp = dtStamp |> Option.defaultValue s
                Tzid = tzid
                RRule = rrule
                Description = description
            }
        | None, _, _ -> Error "VEVENT missing UID"
        | _, None, _ -> Error "VEVENT missing DTSTART"
        | _, _, None -> Error "VEVENT missing DTEND"

// ─── VCALENDAR parsing ──────────────────────────────────────────────

/// Parse a raw iCalendar text into a `VCalendar`. Returns `Error` on
/// structural problems (missing required fields, malformed dates,
/// unsupported FREQ values). Unknown properties (`LOCATION`, `X-*`,
/// etc.) are silently dropped.
let parse (raw: string) : Result<VCalendar, string> =
    let lines = unfoldLines raw

    if lines.IsEmpty then
        Error "Empty input"
    else
        // Locate VCALENDAR envelope.
        let vcalStart =
            lines |> List.tryFindIndex (fun l -> l.ToUpperInvariant() = "BEGIN:VCALENDAR")

        let vcalEnd =
            lines |> List.tryFindIndex (fun l -> l.ToUpperInvariant() = "END:VCALENDAR")

        match vcalStart, vcalEnd with
        | None, _ -> Error "Missing BEGIN:VCALENDAR"
        | _, None -> Error "Missing END:VCALENDAR"
        | Some s, Some e when e <= s -> Error "END:VCALENDAR before BEGIN:VCALENDAR"
        | Some s, Some e ->
            let body = lines |> List.skip (s + 1) |> List.take (e - s - 1)

            let mutable version = "2.0"
            let mutable prodid = ""
            let mutable events = []
            let mutable error = None
            let mutable i = 0

            while i < List.length body && error.IsNone do
                let line = body[i].ToUpperInvariant()

                if line.StartsWith("VERSION:") then
                    version <- body[i].Substring("VERSION:".Length)
                    i <- i + 1
                elif line.StartsWith("PRODID:") then
                    prodid <- body[i].Substring("PRODID:".Length)
                    i <- i + 1
                elif line = "BEGIN:VEVENT" then
                    let endIdx =
                        body
                        |> List.skip (i + 1)
                        |> List.tryFindIndex (fun l -> l.ToUpperInvariant() = "END:VEVENT")

                    match endIdx with
                    | None -> error <- Some "BEGIN:VEVENT without matching END:VEVENT"
                    | Some idx ->
                        let evtLines = body |> List.skip (i + 1) |> List.take idx

                        match parseVEvent evtLines with
                        | Ok v -> events <- v :: events
                        | Error e -> error <- Some e

                        i <- i + idx + 2
                else
                    i <- i + 1

            match error with
            | Some e -> Error e
            | None ->
                Ok {
                    Version = version
                    ProdId = if prodid.Length = 0 then CanonicalProdId else prodid
                    Events = List.rev events
                }

// ─── Emit ───────────────────────────────────────────────────────────

let private dayOfWeekToIcal (d: DayOfWeek) : string =
    match d with
    | DayOfWeek.Monday -> "MO"
    | DayOfWeek.Tuesday -> "TU"
    | DayOfWeek.Wednesday -> "WE"
    | DayOfWeek.Thursday -> "TH"
    | DayOfWeek.Friday -> "FR"
    | DayOfWeek.Saturday -> "SA"
    | DayOfWeek.Sunday -> "SU"
    | _ -> "MO"

let private frequencyToIcal (f: RecurrenceFrequency) : string =
    match f with
    | Daily -> "DAILY"
    | Weekly -> "WEEKLY"
    | Monthly -> "MONTHLY"
    | Yearly -> "YEARLY"

let private formatUtcDateTime (dt: DateTimeOffset) : string =
    dt.UtcDateTime.ToString("yyyyMMddTHHmmssZ")

let private emitRRule (r: RecurrenceRule) : string =
    let parts = ResizeArray<string>()
    parts.Add(sprintf "FREQ=%s" (frequencyToIcal r.Frequency))

    if r.Interval > 1 then
        parts.Add(sprintf "INTERVAL=%d" r.Interval)

    if not (List.isEmpty r.ByWeekday) then
        let days = r.ByWeekday |> List.map dayOfWeekToIcal |> String.concat ","
        parts.Add(sprintf "BYDAY=%s" days)

    match r.Until with
    | Some u -> parts.Add(sprintf "UNTIL=%s" (formatUtcDateTime u))
    | None -> ()

    match r.Count with
    | Some c -> parts.Add(sprintf "COUNT=%d" c)
    | None -> ()

    String.concat ";" parts

let private emitVEvent (v: VEvent) : string =
    let sb = StringBuilder()
    sb.Append(foldLine "BEGIN:VEVENT") |> ignore
    sb.Append(foldLine (sprintf "UID:%s" v.Uid)) |> ignore

    sb.Append(foldLine (sprintf "DTSTAMP:%s" (formatUtcDateTime v.DtStamp)))
    |> ignore

    sb.Append(foldLine (sprintf "DTSTART:%s" (formatUtcDateTime v.DtStart)))
    |> ignore

    sb.Append(foldLine (sprintf "DTEND:%s" (formatUtcDateTime v.DtEnd))) |> ignore
    sb.Append(foldLine (sprintf "SUMMARY:%s" v.Summary)) |> ignore

    match v.Description with
    | Some d -> sb.Append(foldLine (sprintf "DESCRIPTION:%s" d)) |> ignore
    | None -> ()

    match v.RRule with
    | Some r -> sb.Append(foldLine (sprintf "RRULE:%s" (emitRRule r))) |> ignore
    | None -> ()

    sb.Append(foldLine "END:VEVENT") |> ignore
    sb.ToString()

/// Emit a `VCalendar` to canonical iCalendar text (CRLF line endings,
/// folded at 75 octets). Always emits in UTC form (lossy for `Tzid`
/// — the round-trip via `parse → emit → parse` is byte-stable for
/// our own emissions but doesn't preserve the original `TZID`
/// parameter on `DTSTART`).
let emit (cal: VCalendar) : string =
    let sb = StringBuilder()
    sb.Append(foldLine "BEGIN:VCALENDAR") |> ignore
    sb.Append(foldLine (sprintf "VERSION:%s" cal.Version)) |> ignore
    sb.Append(foldLine (sprintf "PRODID:%s" cal.ProdId)) |> ignore
    sb.Append(foldLine "CALSCALE:GREGORIAN") |> ignore

    for e in cal.Events do
        sb.Append(emitVEvent e) |> ignore

    sb.Append(foldLine "END:VCALENDAR") |> ignore
    sb.ToString()

// ─── Booking ↔ VEvent converters ──────────────────────────────────
//
// Minimal v1 mapping. Booking-specific fields not represented in the
// iCalendar VEVENT subset (`ResourceId`, `Status`, `BookedBy`,
// `BookedFor`, `ParentBookingId`, `Metadata`) are NOT preserved
// through the round-trip — `vEventToBooking` requires the caller to
// supply a `defaults` `Booking` whose unknown-from-iCal fields fill
// the gap. A round-trip-stable encoding of those fields via
// `X-TOOLUP-*` properties is a Phase 20 follow-up; today the export
// path is intended for **interop** (sending a `.ics` to a calendar
// app that doesn't speak ToolUp) rather than perfect persistence
// round-trip.

/// Convert a `Booking` to a `VEvent`. Loses `Status` / `BookedBy` /
/// `BookedFor` / `ParentBookingId` / `Metadata`. Caller restores
/// them via `vEventToBooking`'s `defaults` parameter on import.
let bookingToVEvent (b: Booking) : VEvent = {
    Uid = b.Id
    Summary = b.Title
    DtStart = b.StartUtc
    DtEnd = b.EndUtc
    DtStamp = b.StartUtc
    Tzid = None
    RRule = b.Recurrence
    Description = None
}

/// Convert a `VEvent` to a `Booking`. `defaults` supplies fields that
/// don't appear in the iCal subset — `ResourceId`, `Status`,
/// `BookedBy`, `BookedFor`, `ParentBookingId`, `Metadata` come from
/// the defaults verbatim.
let vEventToBooking (defaults: Booking) (v: VEvent) : Booking = {
    defaults with
        Id = v.Uid
        Title = v.Summary
        StartUtc = v.DtStart
        EndUtc = v.DtEnd
        Recurrence = v.RRule
}