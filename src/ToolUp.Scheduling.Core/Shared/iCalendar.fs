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
//   * `CATEGORIES` and vendor X-properties (`X-APPLE-*`, `X-MICROSOFT-*`,
//     …) — silently dropped on parse, never emitted.
//
// **Attendee metadata — carried, since Phase 20a.** `ATTENDEE` /
// `ORGANIZER` / `LOCATION` used to be dropped on parse and never
// emitted, which made a `Booking → ics → Booking` round-trip lossy for
// exactly the fields an external calendar cares most about. They are
// now parsed into `VEvent` and re-emitted, and `Booking.Metadata` rides
// the round-trip by value:
//
//   * the three reserved keys (`iCalendar.LocationKey` /
//     `OrganizerKey` / `AttendeesKey`) map to the matching standard
//     properties — real interop, so an external calendar app shows the
//     location and the invitee list rather than an opaque X-property;
//   * every OTHER metadata entry rides one `X-TOOLUP-META-<KEY>`
//     property, where `<KEY>` is the metadata key percent-encoded into
//     `[A-Z0-9-]` (property names are case-insensitive in RFC 5545, so
//     a key's case has to survive inside the encoding rather than in
//     the name).
//
// `ATTENDEE` / `ORGANIZER` parameters (`CN=`, `ROLE=`, `PARTSTAT=`) are
// NOT carried: the value — the calendar-user address — is what round-
// trips. A deployment needing per-attendee participation status models
// it in `Booking.Metadata` under its own key, where it rides an
// `X-TOOLUP-META-*` property untouched.
//
// **Text escaping**: RFC 5545 §3.3.11 escapes `\`, `;`, `,` and
// newlines inside a TEXT value. `SUMMARY` / `DESCRIPTION` / `LOCATION`
// and the `X-TOOLUP-META-*` values are escaped on emit and unescaped on
// parse. A value containing none of those four characters is emitted
// byte-for-byte as before (GP 11).
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
    /// `LOCATION` — the free-text place. `None` when the source carried
    /// none. Phase 20a.
    Location: string option
    /// `ORGANIZER` — the calendar-user address of the event's owner,
    /// value only (parameters are dropped). Phase 20a.
    Organizer: string option
    /// `ATTENDEE` — one calendar-user address per invitee, in source
    /// order, values only. Empty when the source carried none. Phase 20a.
    Attendees: string list
    /// Round-tripped `X-TOOLUP-META-*` properties, keyed by the decoded
    /// metadata key. Empty for a calendar we did not emit. Phase 20a.
    Extended: Map<string, string>
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

// ─── Phase 20a — the reserved `Booking.Metadata` keys ───────────────
//
// Three metadata keys are RESERVED: they map to standard iCalendar
// properties instead of riding an `X-TOOLUP-META-*` property, so an
// external calendar app renders them natively. A deployment that does
// not use them pays nothing — an absent key emits no property, and a
// `Booking` with empty `Metadata` produces byte-identical output to the
// pre-20a emitter (GP 11).

/// `Booking.Metadata` key carried as the VEVENT's `LOCATION`.
[<Literal>]
let LocationKey = "Location"

/// `Booking.Metadata` key carried as the VEVENT's `ORGANIZER`.
[<Literal>]
let OrganizerKey = "Organizer"

/// `Booking.Metadata` key carried as the VEVENT's `ATTENDEE` properties
/// — one address per attendee, joined by `,` in the metadata value.
[<Literal>]
let AttendeesKey = "Attendees"

/// Property-name prefix for a non-reserved `Booking.Metadata` entry.
[<Literal>]
let ExtendedPropertyPrefix = "X-TOOLUP-META-"

/// The separator joining several attendee addresses into the single
/// `Booking.Metadata` string under `AttendeesKey`. A calendar-user
/// address never contains one.
[<Literal>]
let AttendeeSeparator = ","

// ─── Text value escaping (RFC 5545 §3.3.11) ─────────────────────────

/// Escape a TEXT value: backslash, semicolon, comma and newlines. A
/// value containing none of them is returned unchanged, so ordinary
/// summaries emit exactly as they did before Phase 20a.
let private escapeText (value: string) : string =
    let sb = StringBuilder()

    for ch in value do
        match ch with
        | '\\' -> sb.Append("\\\\") |> ignore
        | ';' -> sb.Append("\\;") |> ignore
        | ',' -> sb.Append("\\,") |> ignore
        | '\n' -> sb.Append("\\n") |> ignore
        | '\r' -> ()
        | c -> sb.Append(c) |> ignore

    sb.ToString()

/// Reverse `escapeText`. An unrecognised escape (`\q`) yields the
/// escaped character itself — the lenient reading, because vendor
/// exports in the wild over-escape.
let private unescapeText (value: string) : string =
    if not (value.Contains '\\') then
        value
    else
        let sb = StringBuilder()
        let mutable i = 0

        while i < value.Length do
            if value[i] = '\\' && i + 1 < value.Length then
                match value[i + 1] with
                | 'n'
                | 'N' -> sb.Append('\n') |> ignore
                | c -> sb.Append(c) |> ignore

                i <- i + 2
            else
                sb.Append(value[i]) |> ignore
                i <- i + 1

        sb.ToString()

// ─── Extended-property key encoding ─────────────────────────────────
//
// Property names are case-insensitive per RFC 5545 and the parser
// upper-cases them, so a metadata key's case cannot survive in the name
// itself. Every character outside `[A-Z0-9-]` — lowercase included — is
// therefore percent-encoded, which is total, reversible, and leaves an
// all-uppercase key readable in the emitted file.

let private encodeExtendedKey (key: string) : string =
    let sb = StringBuilder()

    for b in Text.Encoding.UTF8.GetBytes key do
        let ch = char b

        if (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch = '-' then
            sb.Append(ch) |> ignore
        else
            sb.AppendFormat("%{0:X2}", b) |> ignore

    sb.ToString()

let private decodeExtendedKey (encoded: string) : string =
    let bytes = ResizeArray<byte>()
    let mutable i = 0

    while i < encoded.Length do
        if encoded[i] = '%' && i + 2 < encoded.Length then
            match Byte.TryParse(encoded.Substring(i + 1, 2), Globalization.NumberStyles.HexNumber, null) with
            | true, b ->
                bytes.Add b
                i <- i + 3
            | _ ->
                bytes.Add(byte encoded[i])
                i <- i + 1
        else
            bytes.Add(byte encoded[i])
            i <- i + 1

    Text.Encoding.UTF8.GetString(bytes.ToArray())

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
    let mutable location = None
    let mutable organizer = None
    let attendees = ResizeArray<string>()
    let mutable extended = Map.empty
    let mutable error = None

    for line in lines do
        if error.IsNone then
            match parsePropertyLine line with
            | Error e -> error <- Some e
            | Ok prop ->
                match prop.Name with
                | "UID" -> uid <- Some prop.Value
                | "SUMMARY" -> summary <- unescapeText prop.Value
                | "DESCRIPTION" -> description <- Some(unescapeText prop.Value)
                | "LOCATION" -> location <- Some(unescapeText prop.Value)
                | "ORGANIZER" -> organizer <- Some prop.Value
                | "ATTENDEE" -> attendees.Add prop.Value
                | name when name.StartsWith ExtendedPropertyPrefix ->
                    let key = decodeExtendedKey (name.Substring ExtendedPropertyPrefix.Length)
                    extended <- Map.add key (unescapeText prop.Value) extended
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
                Location = location
                Organizer = organizer
                Attendees = List.ofSeq attendees
                Extended = extended
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

    sb.Append(foldLine (sprintf "SUMMARY:%s" (escapeText v.Summary))) |> ignore

    match v.Description with
    | Some d -> sb.Append(foldLine (sprintf "DESCRIPTION:%s" (escapeText d))) |> ignore
    | None -> ()

    match v.Location with
    | Some l -> sb.Append(foldLine (sprintf "LOCATION:%s" (escapeText l))) |> ignore
    | None -> ()

    match v.Organizer with
    | Some o -> sb.Append(foldLine (sprintf "ORGANIZER:%s" o)) |> ignore
    | None -> ()

    for a in v.Attendees do
        sb.Append(foldLine (sprintf "ATTENDEE:%s" a)) |> ignore

    // `Map` enumerates in key order, so the emission is deterministic —
    // which is what makes the round-trip byte-stable rather than merely
    // semantically equal.
    for KeyValue(key, value) in v.Extended do
        sb.Append(foldLine (sprintf "%s%s:%s" ExtendedPropertyPrefix (encodeExtendedKey key) (escapeText value)))
        |> ignore

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
// `Metadata` round-trips by value since Phase 20a: the three reserved
// keys ride `LOCATION` / `ORGANIZER` / `ATTENDEE` and every other entry
// rides one `X-TOOLUP-META-*` property, so
// `booking → vEvent → emit → parse → booking` returns the metadata it
// started with. The remaining booking-specific fields (`ResourceId`,
// `Status`, `BookedBy`, `BookedFor`, `ParentBookingId`, `Type`,
// `Version`) are STILL not represented in the VEVENT subset and come
// from `vEventToBooking`'s `defaults` — deliberately, because they are
// the deployment's own bookkeeping, not calendar data, and mirroring a
// booking's `Status` into a third-party calendar would publish it.

/// Convert a `Booking` to a `VEvent`. `Metadata` maps onto the three
/// reserved properties plus one `X-TOOLUP-META-*` per remaining entry;
/// `Status` / `BookedBy` / `BookedFor` / `ParentBookingId` are not
/// represented and are restored via `vEventToBooking`'s `defaults`.
let bookingToVEvent (b: Booking) : VEvent =
    let reserved = Set.ofList [ LocationKey; OrganizerKey; AttendeesKey ]

    let attendees =
        match Map.tryFind AttendeesKey b.Metadata with
        | None -> []
        | Some joined ->
            joined.Split([| AttendeeSeparator |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter (fun a -> a.Length > 0)
            |> List.ofArray

    {
        Uid = b.Id
        Summary = b.Title
        DtStart = b.StartUtc
        DtEnd = b.EndUtc
        DtStamp = b.StartUtc
        Tzid = None
        RRule = b.Recurrence
        Description = None
        Location = Map.tryFind LocationKey b.Metadata
        Organizer = Map.tryFind OrganizerKey b.Metadata
        Attendees = attendees
        Extended = b.Metadata |> Map.filter (fun k _ -> not (Set.contains k reserved))
    }

/// Convert a `VEvent` to a `Booking`. `defaults` supplies the fields
/// that don't appear in the iCal subset — `ResourceId`, `Type`,
/// `Version`, `Status`, `BookedBy`, `BookedFor`, `ParentBookingId` come
/// from the defaults verbatim. `Metadata` is rebuilt from the event:
/// the extended properties plus whichever reserved keys the event
/// carried, so the result is what `bookingToVEvent` was given rather
/// than the defaults' metadata.
let vEventToBooking (defaults: Booking) (v: VEvent) : Booking =
    let withReserved key value metadata =
        match value with
        | Some v -> Map.add key v metadata
        | None -> metadata

    let attendees =
        match v.Attendees with
        | [] -> None
        | list -> Some(String.concat AttendeeSeparator list)

    let metadata =
        v.Extended
        |> withReserved LocationKey v.Location
        |> withReserved OrganizerKey v.Organizer
        |> withReserved AttendeesKey attendees

    {
        defaults with
            Id = v.Uid
            Title = v.Summary
            StartUtc = v.DtStart
            EndUtc = v.DtEnd
            Recurrence = v.RRule
            Metadata = metadata
    }