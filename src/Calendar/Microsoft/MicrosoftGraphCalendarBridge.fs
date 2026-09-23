// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 831 - the Microsoft Graph `ICalendarBridge`: Outlook / Microsoft
/// 365 calendars over the Graph REST surface, delegated OAuth through the
/// shipped substrate, delta pulls and change-notification subscriptions.
module ToolUp.Calendar.MicrosoftGraph

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Scheduling
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Calendar.MicrosoftGraphOAuth

// ─── Phase 831 — the Microsoft Graph calendar bridge ────────────────
//
// The second `ICalendarBridge` after CalDAV, against Microsoft Graph
// v1.0. BCL `HttpClient` + `System.Text.Json` — no Graph SDK: the six
// endpoints below are a smaller surface than the SDK's dependency
// graph, and every request stays visible in one file.
//
//   * `GET  /me/calendars/{id}`                       — link / reachability
//   * `POST /me/calendars/{id}/events`                — push (create)
//   * `PATCH|DELETE /me/calendars/{id}/events/{eid}`  — push (update / cancel)
//   * `GET  /me/calendars/{id}/calendarView/delta`    — pull
//   * `GET  /me/calendars/{id}/events/{eid}`          — pull (the full event)
//   * `POST|PATCH|DELETE /subscriptions[/{sid}]`      — change notifications
//
// **The booking rides the event as two extended properties.** Graph
// assigns its own event ids and its own `iCalUId`, and a Graph event has
// no field for arbitrary metadata, so a pushed event carries two
// single-value extended properties in a ToolUp property set: the booking
// id (which is what makes push idempotent — a push with no known event
// id looks the booking up by it before creating anything, and a create
// carries a `transactionId` derived from it), and a JSON SNAPSHOT of what
// was pushed — the metadata map and the recurrence rule. On pull the
// native fields are authoritative wherever Outlook can edit them
// (subject, times, location, attendees, recurrence pattern); the
// snapshot supplies what Graph cannot express (the `Organizer` value —
// Graph always reports the mailbox owner — every non-reserved metadata
// key, and the exact `RecurrenceRule` when the native pattern still
// projects from it). The rule is: a snapshot value survives exactly when
// projecting it gives what Graph now reports; anything an Outlook user
// changed wins. That is what makes a push → pull round-trip lossless and
// an external edit visible at once.
//
// **Attendees are real attendees.** The reserved `Attendees` metadata
// key maps onto Graph's `attendees`, which means Outlook sends
// invitations from the mailbox that owns the calendar — the behaviour
// the 20a carry-through gives a CalDAV server that schedules. A
// deployment mirroring into a resource calendar with no invitations
// wanted keeps attendee addresses out of `Booking.Metadata`.
//
// **Pull: `calendarView/delta`.** A delta round over the pull window
// yields the events that changed since the round's cursor; occurrences
// of a series are folded onto their series master (one booking carries
// one recurrence rule), and each changed event is then read in full,
// because the delta surface returns no extended properties. The
// round's final `deltaLink` is kept per link in `ISecretStore` — the
// same per-connection, per-scope state store the Phase 10h refresher
// keeps its cached access token in — and followed by the next default-
// window pull, so a notification costs one delta page rather than a
// window scan. A stored cursor is abandoned for a fresh round once it is
// older than `FullResyncInterval` or Graph answers `410 Gone`.
//
// `Capabilities.SupportsIncrementalPull` is `false`, deliberately: the
// seam's `since` argument is honoured as an EVENT-TIME lower bound (a
// fresh round whose window starts there), which is what the contract
// pack's since-law holds every bridge to. The delta cursor is internal
// to the default window (`since = None`) and is invisible to the caller
// except as fewer unchanged events — which the sync engine, told the
// bridge is not incremental, already reconciles by value.
//
// **Deletions are observed but not applied.** A delta page reports a
// deleted event as `@removed`; the sync engine has no deletion verb on
// the seam (Phase 20a's declared limit), so the bridge drops them. A
// booking cancelled LOCALLY still removes its external event: that is a
// push.
//
// **Change notifications.** With `NotificationUrl` set, `LinkResource`
// opens (or renews) a Graph subscription on the linked calendar's
// events, pointed at the companion's notification route with the link's
// scope and calendar in the query string. Its `clientState` is an HMAC
// of exactly those two values under a deployment secret, so a
// notification proves both where it came from and which link it names.
// `HandleWebhook` checks that proof and nothing else; the handshake
// Graph performs when a subscription is created is answered by the
// route (`MicrosoftGraphSubscriptions.receive`) before a notification
// ever reaches the bridge.
//
// **Retry is the caller's.** Portability rule 3: the bridge classifies
// (`429` → `RateLimited` with Graph's `Retry-After`, `5xx` / transport →
// `Unreachable`) and never loops. The one re-issue it makes is not a
// retry of a transient failure but credential renewal: a `401` against
// a token the cache still thought valid forces one refresh through the
// refresher and re-sends once.
//
// **Distributed-ready.** Every call carries the whole `CalendarLinkRef`
// and reads its credential, cursor and subscription id from
// `ISecretStore` in the link's scope; there is no in-memory session and
// no affinity.

/// The bridge's `ICalendarBridge.Kind`. Persisted on every link, so it
/// is a wire value.
[<Literal>]
let KindName = "Microsoft"

/// Names of the query parameters the bridge puts on a subscription's
/// notification URL, and reads back from the inbound request.
module NotificationQuery =
    /// The link's storage scope.
    [<Literal>]
    let Scope = "scope"

    /// The link's external calendar id.
    [<Literal>]
    let CalendarId = "calendar"

/// Header names the webhook handler forwards the notification URL's
/// query values under when it hands a notification to
/// `ICalendarBridge.HandleWebhook`, whose surface carries headers and a
/// body but no query string.
module NotificationHeaders =
    /// Carries `NotificationQuery.Scope`.
    [<Literal>]
    let Scope = "X-ToolUp-Calendar-Scope"

    /// Carries `NotificationQuery.CalendarId`.
    [<Literal>]
    let CalendarId = "X-ToolUp-Calendar-Id"

/// The MAPI property-set GUID the two ToolUp extended properties live
/// in. A wire value: changing it orphans the booking id on every event
/// already pushed.
[<Literal>]
let PropertySetId = "{3a1ff38d-189d-4c98-ab73-ae81d6d5e08e}"

/// Extended-property id carrying the booking id.
let BookingIdPropertyId = sprintf "String %s Name ToolUp.BookingId" PropertySetId

/// Extended-property id carrying the pushed snapshot (metadata +
/// recurrence rule, JSON).
let SnapshotPropertyId = sprintf "String %s Name ToolUp.Snapshot" PropertySetId

// ─── Link-scoped keys and the notification proof ────────────────────

/// A short, stable key for one link — the resource and the calendar it
/// mirrors into. Scopes the delta cursor and the subscription id inside
/// the link's own `ISecretStore` scope.
let linkKey (link: CalendarLinkRef) : string =
    let bytes = Encoding.UTF8.GetBytes(link.ResourceId + "\n" + link.ExternalCalendarId)
    Convert.ToHexString(SHA256.HashData bytes).Substring(0, 32).ToLowerInvariant()

let private deltaStateKey (link: CalendarLinkRef) =
    "msgraph-calendar-delta-" + linkKey link

let private subscriptionKey (link: CalendarLinkRef) =
    "msgraph-calendar-subscription-" + linkKey link

let private base64Url (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// The `clientState` a subscription for `(scopeId, calendarId)` carries:
/// base64url HMAC-SHA256 of both under the deployment's webhook secret.
/// 43 characters, inside Graph's 128-character limit, and unforgeable
/// without the secret — so a notification carrying it proves the query
/// values it arrived with.
let clientStateFor (secret: string) (scopeId: string) (calendarId: ExternalCalendarId) : string =
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
    base64Url (hmac.ComputeHash(Encoding.UTF8.GetBytes(scopeId + "\n" + calendarId)))

/// The notification URL a subscription for one link points at: the
/// configured route plus the link's scope and calendar as query values.
let notificationUrlFor (baseUrl: string) (scopeId: string) (calendarId: ExternalCalendarId) : string =
    let separator = if baseUrl.Contains "?" then "&" else "?"

    sprintf
        "%s%s%s=%s&%s=%s"
        baseUrl
        separator
        NotificationQuery.Scope
        (Uri.EscapeDataString scopeId)
        NotificationQuery.CalendarId
        (Uri.EscapeDataString calendarId)

// ─── Response classification ────────────────────────────────────────

/// Graph's `{ "error": { "code", "message" } }` body as one line, or the
/// raw body (truncated) when it is not that shape.
let private graphErrorText (body: string) : string =
    try
        let node = JsonNode.Parse body

        match node["error"] with
        | null -> body
        | error ->
            let code =
                match error["code"] with
                | null -> ""
                | c -> c.GetValue<string>()

            let message =
                match error["message"] with
                | null -> ""
                | m -> m.GetValue<string>()

            sprintf "%s: %s" code message
    with _ ->
        if body.Length > 200 then body.Substring(0, 200) else body

/// Map a Graph status onto the seam's closed error vocabulary. Callers
/// handle `404` / `410` themselves where they mean something specific;
/// this is the residual classification (portability rule 3 — the bridge
/// says whether retrying could work, it never retries).
let classifyStatus (status: HttpStatusCode) (body: string) (retryAfter: TimeSpan option) : BridgeError =
    match int status with
    | 401
    | 403 -> AuthenticationFailed(sprintf "Microsoft Graph returned %d: %s" (int status) (graphErrorText body))
    | 429 -> RateLimited retryAfter
    | s when s >= 500 -> Unreachable(sprintf "Microsoft Graph returned %d: %s" s (graphErrorText body))
    | s -> ExternalRejected(s, graphErrorText body)

// ─── Booking ↔ Graph event mapping ──────────────────────────────────

let private graphDateTime (value: DateTimeOffset) : JsonObject =
    JsonObject(
        dict [
            "dateTime", JsonValue.Create(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff")) :> JsonNode
            "timeZone", JsonValue.Create "UTC" :> JsonNode
        ]
    )

let private dayName (day: DayOfWeek) = day.ToString().ToLowerInvariant()

let private tryDay (name: string) : DayOfWeek option =
    match Enum.TryParse<DayOfWeek>(name, true) with
    | true, day -> Some day
    | _ -> None

/// The Graph-side shape of a recurrence, reduced to what both sides
/// can compare: pattern type, interval, days, day of month, month, and
/// the range.
type private RecurrenceProjection = {
    PatternType: string
    Interval: int
    Days: string list
    DayOfMonth: int
    Month: int
    RangeType: string
    EndDate: string
    Occurrences: int
}

let private projectRule (start: DateTimeOffset) (rule: RecurrenceRule) : RecurrenceProjection =
    let startUtc = start.UtcDateTime

    let patternType, days, dayOfMonth, month =
        match rule.Frequency with
        | Daily -> "daily", [], 0, 0
        | Weekly ->
            let days =
                match rule.ByWeekday with
                | [] -> [ startUtc.DayOfWeek ]
                | listed -> listed

            "weekly", (days |> List.map dayName |> List.distinct |> List.sort), 0, 0
        | Monthly -> "absoluteMonthly", [], startUtc.Day, 0
        | Yearly -> "absoluteYearly", [], startUtc.Day, startUtc.Month

    let rangeType, endDate, occurrences =
        match rule.Until, rule.Count with
        | Some until, _ ->
            // `Until` is an EXCLUSIVE instant; Graph's `endDate` is an
            // inclusive date — the last date an occurrence may fall on.
            "endDate", until.UtcDateTime.AddTicks(-1L).ToString("yyyy-MM-dd"), 0
        | None, Some count -> "numbered", "", count
        | None, None -> "noEnd", "", 0

    {
        PatternType = patternType
        Interval = max 1 rule.Interval
        Days = days
        DayOfMonth = dayOfMonth
        Month = month
        RangeType = rangeType
        EndDate = endDate
        Occurrences = occurrences
    }

let private recurrenceJson (start: DateTimeOffset) (rule: RecurrenceRule) : JsonObject =
    let p = projectRule start rule
    let pattern = JsonObject()
    pattern["type"] <- JsonValue.Create p.PatternType
    pattern["interval"] <- JsonValue.Create p.Interval

    if p.PatternType = "weekly" then
        pattern["daysOfWeek"] <- JsonArray(p.Days |> List.map (fun d -> JsonValue.Create d :> JsonNode) |> Array.ofList)
        pattern["firstDayOfWeek"] <- JsonValue.Create "sunday"

    if p.DayOfMonth > 0 then
        pattern["dayOfMonth"] <- JsonValue.Create p.DayOfMonth

    if p.Month > 0 then
        pattern["month"] <- JsonValue.Create p.Month

    let range = JsonObject()
    range["type"] <- JsonValue.Create p.RangeType
    range["startDate"] <- JsonValue.Create(start.UtcDateTime.ToString "yyyy-MM-dd")
    range["recurrenceTimeZone"] <- JsonValue.Create "UTC"

    if p.RangeType = "endDate" then
        range["endDate"] <- JsonValue.Create p.EndDate

    if p.RangeType = "numbered" then
        range["numberOfOccurrences"] <- JsonValue.Create p.Occurrences

    let recurrence = JsonObject()
    recurrence["pattern"] <- pattern
    recurrence["range"] <- range
    recurrence

let private tryProp (name: string) (el: JsonElement) : JsonElement option =
    if el.ValueKind <> JsonValueKind.Object then
        None
    else
        match el.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
        | _ -> None

let private tryStr (name: string) (el: JsonElement) : string option =
    match tryProp name el with
    | Some v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private tryNum (name: string) (el: JsonElement) : int option =
    match tryProp name el with
    | Some v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, n -> Some n
        | _ -> None
    | _ -> None

let private readProjection (recurrence: JsonElement) : RecurrenceProjection option =
    match tryProp "pattern" recurrence, tryProp "range" recurrence with
    | Some pattern, Some range ->
        let days =
            match tryProp "daysOfWeek" pattern with
            | Some arr when arr.ValueKind = JsonValueKind.Array ->
                arr.EnumerateArray()
                |> Seq.choose (fun d ->
                    if d.ValueKind = JsonValueKind.String then
                        Some(d.GetString().ToLowerInvariant())
                    else
                        None)
                |> List.ofSeq
                |> List.distinct
                |> List.sort
            | _ -> []

        let patternType = tryStr "type" pattern |> Option.defaultValue ""
        let rangeType = tryStr "type" range |> Option.defaultValue "noEnd"

        Some {
            PatternType = patternType
            Interval = tryNum "interval" pattern |> Option.defaultValue 1 |> max 1
            Days = (if patternType = "weekly" then days else [])
            DayOfMonth =
                (if patternType = "absoluteMonthly" || patternType = "absoluteYearly" then
                     tryNum "dayOfMonth" pattern |> Option.defaultValue 0
                 else
                     0)
            Month =
                (if patternType = "absoluteYearly" then
                     tryNum "month" pattern |> Option.defaultValue 0
                 else
                     0)
            RangeType = rangeType
            EndDate =
                (if rangeType = "endDate" then
                     tryStr "endDate" range |> Option.defaultValue ""
                 else
                     "")
            Occurrences =
                (if rangeType = "numbered" then
                     tryNum "numberOfOccurrences" range |> Option.defaultValue 0
                 else
                     0)
        }
    | _ -> None

/// The `RecurrenceRule` a Graph pattern means, when v1's rule can say
/// it. Relative monthly / yearly patterns ("the second Tuesday") have no
/// v1 form and read as `None` — the event is then mirrored as its first
/// occurrence, the same subset `iCalendar.fs` declares for `BYSETPOS`.
let private ruleOfProjection (p: RecurrenceProjection) : RecurrenceRule option =
    let frequency =
        match p.PatternType with
        | "daily" -> Some Daily
        | "weekly" -> Some Weekly
        | "absoluteMonthly" -> Some Monthly
        | "absoluteYearly" -> Some Yearly
        | _ -> None

    frequency
    |> Option.map (fun f -> {
        Frequency = f
        Interval = p.Interval
        ByWeekday = p.Days |> List.choose tryDay |> List.sortBy int
        Until =
            (if p.RangeType = "endDate" then
                 match DateTime.TryParse(p.EndDate, Globalization.CultureInfo.InvariantCulture) with
                 | true, d -> Some(DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)).AddDays 1.0)
                 | _ -> None
             else
                 None)
        Count =
            (if p.RangeType = "numbered" then
                 Some p.Occurrences
             else
                 None)
    })

// Snapshot JSON: { "metadata": { … }, "recurrence": null | { … } }.

let private frequencyName (f: RecurrenceFrequency) =
    match f with
    | Daily -> "Daily"
    | Weekly -> "Weekly"
    | Monthly -> "Monthly"
    | Yearly -> "Yearly"

let private snapshotJson (booking: Booking) : string =
    let metadata = JsonObject()

    for KeyValue(k, v) in booking.Metadata do
        metadata[k] <- JsonValue.Create v

    let root = JsonObject()
    root["metadata"] <- metadata

    root["recurrence"] <-
        match booking.Recurrence with
        | None -> null
        | Some rule ->
            let r = JsonObject()
            r["frequency"] <- JsonValue.Create(frequencyName rule.Frequency)
            r["interval"] <- JsonValue.Create rule.Interval

            r["byWeekday"] <-
                JsonArray(
                    rule.ByWeekday
                    |> List.map (fun d -> JsonValue.Create(d.ToString()) :> JsonNode)
                    |> Array.ofList
                )

            r["until"] <-
                match rule.Until with
                | Some u -> JsonValue.Create(u.ToString("o"))
                | None -> null

            r["count"] <-
                match rule.Count with
                | Some c -> JsonValue.Create c
                | None -> null

            r

    root.ToJsonString()

type private Snapshot = {
    Metadata: Map<string, string>
    Recurrence: RecurrenceRule option
}

let private parseSnapshot (json: string) : Snapshot option =
    try
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        let metadata =
            match tryProp "metadata" root with
            | Some m when m.ValueKind = JsonValueKind.Object ->
                m.EnumerateObject()
                |> Seq.choose (fun p ->
                    if p.Value.ValueKind = JsonValueKind.String then
                        Some(p.Name, p.Value.GetString())
                    else
                        None)
                |> Map.ofSeq
            | _ -> Map.empty

        let recurrence =
            match tryProp "recurrence" root with
            | Some r when r.ValueKind = JsonValueKind.Object ->
                let frequency =
                    match tryStr "frequency" r with
                    | Some "Daily" -> Some Daily
                    | Some "Weekly" -> Some Weekly
                    | Some "Monthly" -> Some Monthly
                    | Some "Yearly" -> Some Yearly
                    | _ -> None

                frequency
                |> Option.map (fun f -> {
                    Frequency = f
                    Interval = tryNum "interval" r |> Option.defaultValue 1
                    ByWeekday =
                        match tryProp "byWeekday" r with
                        | Some arr when arr.ValueKind = JsonValueKind.Array ->
                            arr.EnumerateArray()
                            |> Seq.choose (fun d ->
                                if d.ValueKind = JsonValueKind.String then
                                    tryDay (d.GetString())
                                else
                                    None)
                            |> List.ofSeq
                        | _ -> []
                    Until =
                        tryStr "until" r
                        |> Option.bind (fun s ->
                            match
                                DateTimeOffset.TryParse(
                                    s,
                                    Globalization.CultureInfo.InvariantCulture,
                                    Globalization.DateTimeStyles.RoundtripKind
                                )
                            with
                            | true, v -> Some v
                            | _ -> None)
                    Count = tryNum "count" r
                })
            | _ -> None

        Some {
            Metadata = metadata
            Recurrence = recurrence
        }
    with _ ->
        None

let private stripMailto (value: string) =
    let v = value.Trim()

    if v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) then
        v.Substring 7
    else
        v

let private attendeeAddresses (joined: string) : string list =
    joined.Split([| iCalendar.AttendeeSeparator |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map stripMailto
    |> Array.filter (fun a -> a.Length > 0)
    |> List.ofArray

let private addressSet (addresses: string list) =
    addresses |> List.map (fun a -> a.ToLowerInvariant()) |> Set.ofList

/// The Graph event JSON a booking is pushed as. `transactionId` (a GUID
/// derived from the booking id) is included for a create, so a create
/// Graph accepted but whose response was lost is not duplicated by the
/// re-push.
let bookingToGraphEvent (booking: Booking) (forCreate: bool) : string =
    let body = JsonObject()
    body["subject"] <- JsonValue.Create booking.Title
    body["start"] <- graphDateTime booking.StartUtc
    body["end"] <- graphDateTime booking.EndUtc

    let location = JsonObject()

    location["displayName"] <-
        JsonValue.Create(Map.tryFind iCalendar.LocationKey booking.Metadata |> Option.defaultValue "")

    body["location"] <- location

    let attendees =
        match Map.tryFind iCalendar.AttendeesKey booking.Metadata with
        | None -> []
        | Some joined -> attendeeAddresses joined

    body["attendees"] <-
        JsonArray(
            attendees
            |> List.map (fun address ->
                let email = JsonObject()
                email["address"] <- JsonValue.Create address
                let attendee = JsonObject()
                attendee["emailAddress"] <- email
                attendee["type"] <- JsonValue.Create "required"
                attendee :> JsonNode)
            |> Array.ofList
        )

    match booking.Recurrence with
    | Some rule -> body["recurrence"] <- recurrenceJson booking.StartUtc rule
    | None ->
        if not forCreate then
            body["recurrence"] <- null

    let property (id: string) (value: string) =
        let p = JsonObject()
        p["id"] <- JsonValue.Create id
        p["value"] <- JsonValue.Create value
        p :> JsonNode

    body["singleValueExtendedProperties"] <-
        JsonArray(property BookingIdPropertyId booking.Id, property SnapshotPropertyId (snapshotJson booking))

    if forCreate then
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes("toolup-booking:" + booking.Id))
        body["transactionId"] <- JsonValue.Create(Guid(digest.AsSpan(0, 16)).ToString())

    body.ToJsonString()

let private parseGraphInstant (el: JsonElement) : Result<DateTimeOffset, string> =
    match tryStr "dateTime" el with
    | None -> Error "event time carried no dateTime"
    | Some raw ->
        match
            DateTime.TryParse(
                raw,
                Globalization.CultureInfo.InvariantCulture,
                Globalization.DateTimeStyles.AdjustToUniversal
                ||| Globalization.DateTimeStyles.AssumeUniversal
            )
        with
        | false, _ -> Error(sprintf "unparseable event time '%s'" raw)
        | true, parsed ->
            let wall = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)

            match tryStr "timeZone" el |> Option.defaultValue "UTC" with
            | "UTC"
            | "Etc/UTC"
            | "tzone://Microsoft/Utc" -> Ok(DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Utc)))
            | zone ->
                try
                    let tz = TimeZoneInfo.FindSystemTimeZoneById zone
                    Ok(DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, tz)))
                with _ ->
                    Error(sprintf "unknown event time zone '%s'" zone)

/// Project one full Graph event (as read with its extended properties)
/// onto the seam's `ExternalEvent`, filling what no calendar carries
/// from `defaults`. `Ok None` for an event the mirror does not carry —
/// one the organiser cancelled.
let graphEventToBooking (defaults: Booking) (json: string) : Result<ExternalEvent option, string> =
    try
        use doc = JsonDocument.Parse json
        let e = doc.RootElement

        match tryStr "id" e with
        | None -> Error "event carried no id"
        | Some eventId ->
            match e.TryGetProperty "isCancelled" with
            | true, v when v.ValueKind = JsonValueKind.True -> Ok None
            | _ ->
                let properties =
                    match tryProp "singleValueExtendedProperties" e with
                    | Some arr when arr.ValueKind = JsonValueKind.Array ->
                        arr.EnumerateArray()
                        |> Seq.choose (fun p ->
                            match tryStr "id" p, tryStr "value" p with
                            | Some id, Some value -> Some(id, value)
                            | _ -> None)
                        |> List.ofSeq
                    | _ -> []

                // Graph may normalise the GUID's spelling when it echoes
                // a property id back; the NAME is ours and exact.
                let property (name: string) =
                    properties
                    |> List.tryFind (fun (id, _) -> id.EndsWith("Name " + name, StringComparison.OrdinalIgnoreCase))
                    |> Option.map snd

                let snapshot = property "ToolUp.Snapshot" |> Option.bind parseSnapshot

                let bookingId =
                    property "ToolUp.BookingId"
                    |> Option.orElse (tryStr "iCalUId" e)
                    |> Option.defaultValue eventId

                match tryProp "start" e, tryProp "end" e with
                | Some startEl, Some endEl ->
                    match parseGraphInstant startEl, parseGraphInstant endEl with
                    | Error m, _
                    | _, Error m -> Error m
                    | Ok startUtc, Ok endUtc ->
                        let nativeLocation =
                            tryProp "location" e
                            |> Option.bind (tryStr "displayName")
                            |> Option.filter (fun l -> l.Length > 0)

                        let nativeAttendees =
                            match tryProp "attendees" e with
                            | Some arr when arr.ValueKind = JsonValueKind.Array ->
                                arr.EnumerateArray()
                                |> Seq.choose (fun a -> tryProp "emailAddress" a |> Option.bind (tryStr "address"))
                                |> List.ofSeq
                            | _ -> []

                        let snapshotMetadata =
                            snapshot |> Option.map _.Metadata |> Option.defaultValue Map.empty

                        let setOrRemove key value metadata =
                            match value with
                            | Some v -> Map.add key v metadata
                            | None -> Map.remove key metadata

                        // A snapshot value survives exactly when it still
                        // projects to what Graph reports.
                        let withLocation =
                            if Map.tryFind iCalendar.LocationKey snapshotMetadata = nativeLocation then
                                snapshotMetadata
                            else
                                setOrRemove iCalendar.LocationKey nativeLocation snapshotMetadata

                        let snapshotAttendees =
                            Map.tryFind iCalendar.AttendeesKey snapshotMetadata
                            |> Option.map attendeeAddresses
                            |> Option.defaultValue []

                        let withAttendees =
                            if addressSet snapshotAttendees = addressSet nativeAttendees then
                                withLocation
                            else
                                let joined =
                                    match nativeAttendees with
                                    | [] -> None
                                    | list ->
                                        list
                                        |> List.map (fun a -> "mailto:" + a)
                                        |> String.concat iCalendar.AttendeeSeparator
                                        |> Some

                                setOrRemove iCalendar.AttendeesKey joined withLocation

                        let metadata =
                            match snapshot with
                            | Some _ -> withAttendees
                            | None ->
                                // An event created in Outlook: the organiser
                                // Graph reports is genuinely the organiser.
                                let organizer =
                                    tryProp "organizer" e
                                    |> Option.bind (tryProp "emailAddress")
                                    |> Option.bind (tryStr "address")
                                    |> Option.map (fun a -> "mailto:" + a)

                                setOrRemove iCalendar.OrganizerKey organizer withAttendees

                        let nativeProjection = tryProp "recurrence" e |> Option.bind readProjection

                        let recurrence =
                            match snapshot |> Option.bind _.Recurrence, nativeProjection with
                            | Some rule, Some native when projectRule startUtc rule = native -> Some rule
                            | _, Some native -> ruleOfProjection native
                            | _, None -> None

                        let lastModified =
                            tryStr "lastModifiedDateTime" e
                            |> Option.bind (fun s ->
                                match
                                    DateTimeOffset.TryParse(
                                        s,
                                        Globalization.CultureInfo.InvariantCulture,
                                        Globalization.DateTimeStyles.AssumeUniversal
                                    )
                                with
                                | true, v -> Some(v.ToUniversalTime())
                                | _ -> None)

                        Ok(
                            Some {
                                ExternalEventId = eventId
                                Booking = {
                                    defaults with
                                        Id = bookingId
                                        Title = tryStr "subject" e |> Option.defaultValue ""
                                        StartUtc = startUtc
                                        EndUtc = endUtc
                                        Recurrence = recurrence
                                        Metadata = metadata
                                }
                                LastModifiedUtc = lastModified
                            }
                        )
                | _ -> Error "event carried no start or end"
    with :? JsonException as ex ->
        Error(sprintf "event is not valid JSON: %s" ex.Message)

// ─── Stored per-link state ──────────────────────────────────────────

type private DeltaState = {
    DeltaLink: string
    RoundStartedAt: DateTimeOffset
    WindowEnd: DateTimeOffset
}

let private readDeltaState (raw: string) : DeltaState option =
    try
        use doc = JsonDocument.Parse raw
        let root = doc.RootElement

        match tryStr "deltaLink" root, tryStr "roundStartedAt" root, tryStr "windowEnd" root with
        | Some link, Some started, Some windowEnd ->
            match DateTimeOffset.TryParse started, DateTimeOffset.TryParse windowEnd with
            | (true, s), (true, w) ->
                Some {
                    DeltaLink = link
                    RoundStartedAt = s
                    WindowEnd = w
                }
            | _ -> None
        | _ -> None
    with _ ->
        None

let private writeDeltaState (state: DeltaState) : string =
    let root = JsonObject()
    root["deltaLink"] <- JsonValue.Create state.DeltaLink
    root["roundStartedAt"] <- JsonValue.Create(state.RoundStartedAt.ToString "o")
    root["windowEnd"] <- JsonValue.Create(state.WindowEnd.ToString "o")
    root.ToJsonString()

// ─── The bridge ─────────────────────────────────────────────────────

type private GraphResponse = {
    Status: HttpStatusCode
    Body: string
    RetryAfter: TimeSpan option
}

/// Microsoft Graph `ICalendarBridge`. Construct one per deployment and
/// compose it with `SchedulingServerApp.withCalendarBridge` (or
/// `MicrosoftGraphSubscriptions.compose`, which also registers the
/// subscription-renewal job).
///
/// `refresher` is the Phase 10h substrate every access token flows
/// through; `handler` is the transport and `clock` the wall clock, both
/// explicit only so a test can serve canned Graph responses and pin
/// time through the same code path.
type MicrosoftGraphCalendarBridge
    (
        secretStore: ISecretStore,
        refresher: IOAuthTokenRefresher,
        settings: MicrosoftGraphCalendarSettings,
        handler: HttpMessageHandler,
        clock: unit -> DateTimeOffset
    ) =

    let client = PlatformHttpClient.createWith EgressSurface.Other handler
    let apiBase = MicrosoftGraphCalendarSettings.graphBase settings + "/v1.0"

    let esc (value: string) = Uri.EscapeDataString value

    let calendarUrl (calendarId: ExternalCalendarId) =
        sprintf "%s/me/calendars/%s" apiBase (esc calendarId)

    let eventUrl (calendarId: ExternalCalendarId) (eventId: ExternalEventId) =
        sprintf "%s/events/%s" (calendarUrl calendarId) (esc eventId)

    /// A `nextLink` / `deltaLink` names the real Graph host; behind an
    /// endpoint override it is re-based onto the override so a test
    /// double (or a proxy) keeps receiving the follow-up pages.
    let rebase (url: string) =
        match settings.EndpointOverride with
        | Some overrideBase ->
            let real = settings.GraphEndpoint.TrimEnd '/'

            if url.StartsWith(real, StringComparison.OrdinalIgnoreCase) then
                overrideBase.TrimEnd '/' + url.Substring real.Length
            else
                url
        | None -> url

    let token (link: CalendarLinkRef) =
        accessToken secretStore refresher settings clock link.ScopeId link.UserId

    let send (build: string -> HttpRequestMessage) (bearer: string) = async {
        use request = build bearer
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearer)
        // Times come back in UTC; ids are immutable across folder moves.
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"")
        |> ignore

        request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"")
        |> ignore

        try
            use! response = client.SendAsync request |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            let retryAfter =
                match response.Headers.RetryAfter with
                | null -> None
                | ra when ra.Delta.HasValue -> Some ra.Delta.Value
                | ra when ra.Date.HasValue -> Some(ra.Date.Value - clock ())
                | _ -> None

            return
                Ok {
                    Status = response.StatusCode
                    Body = body
                    RetryAfter = retryAfter
                }
        with
        | :? TaskCanceledException as ex ->
            return Error(Unreachable(sprintf "Microsoft Graph request timed out: %s" ex.Message))
        | :? HttpRequestException as ex -> return Error(Unreachable ex.Message)
    }

    /// Issue one Graph request for a link. `build` gets the bearer token
    /// so the request can be rebuilt: a `401` against a token the cache
    /// still considered valid forces one refresh and re-sends once — the
    /// credential-renewal path, not a transient retry.
    let call (link: CalendarLinkRef) (build: string -> HttpRequestMessage) = async {
        match! token link with
        | Error e -> return Error e
        | Ok bearer ->
            match! send build bearer with
            | Ok response when response.Status = HttpStatusCode.Unauthorized ->
                match! forceRefresh secretStore refresher settings link.ScopeId link.UserId with
                | Error e -> return Error e
                | Ok renewed -> return! send build renewed
            | other -> return other
    }

    let jsonRequest (method: HttpMethod) (url: string) (json: string option) =
        fun (_: string) ->
            let request = new HttpRequestMessage(method, url)

            match json with
            | Some payload -> request.Content <- new StringContent(payload, Encoding.UTF8, "application/json")
            | None -> ()

            request

    let isSuccess (status: HttpStatusCode) = int status >= 200 && int status < 300

    /// The id of the event already carrying this booking id, if any — the
    /// lookup that makes a push with no known event id idempotent.
    let findByBookingId (link: CalendarLinkRef) (bookingId: BookingId) = async {
        let filter =
            sprintf
                "singleValueExtendedProperties/Any(ep: ep/id eq '%s' and ep/value eq '%s')"
                BookingIdPropertyId
                (bookingId.Replace("'", "''"))

        let url =
            sprintf "%s/events?$filter=%s&$select=id" (calendarUrl link.ExternalCalendarId) (esc filter)

        match! call link (jsonRequest HttpMethod.Get url None) with
        | Error e -> return Error e
        | Ok r when int r.Status = 404 -> return Error(CalendarNotFound link.ExternalCalendarId)
        | Ok r when not (isSuccess r.Status) -> return Error(classifyStatus r.Status r.Body r.RetryAfter)
        | Ok r ->
            try
                use doc = JsonDocument.Parse r.Body

                let found =
                    match tryProp "value" doc.RootElement with
                    | Some arr when arr.ValueKind = JsonValueKind.Array ->
                        arr.EnumerateArray() |> Seq.tryPick (tryStr "id")
                    | _ -> None

                return Ok found
            with :? JsonException as ex ->
                return Error(MalformedPayload(sprintf "Graph event lookup was not JSON: %s" ex.Message))
    }

    /// One full event, with the two ToolUp extended properties expanded.
    let readEvent (link: CalendarLinkRef) (eventId: ExternalEventId) (defaults: Booking) = async {
        let expand =
            sprintf
                "singleValueExtendedProperties($filter=id eq '%s' or id eq '%s')"
                BookingIdPropertyId
                SnapshotPropertyId

        let url =
            sprintf "%s?$expand=%s" (eventUrl link.ExternalCalendarId eventId) (esc expand)

        match! call link (jsonRequest HttpMethod.Get url None) with
        | Error e -> return Error e
        // Deleted between the delta page and this read — not an error,
        // just nothing to mirror.
        | Ok r when int r.Status = 404 -> return Ok None
        | Ok r when not (isSuccess r.Status) -> return Error(classifyStatus r.Status r.Body r.RetryAfter)
        | Ok r ->
            match graphEventToBooking defaults r.Body with
            | Ok e -> return Ok e
            | Error m -> return Error(MalformedPayload(sprintf "Graph event %s: %s" eventId m))
    }

    let isoInstant (value: DateTimeOffset) =
        value.UtcDateTime.ToString "yyyy-MM-dd'T'HH:mm:ss'Z'"

    /// Run one delta round from `startUrl` to its `deltaLink`, following
    /// every `nextLink`. Returns the ids of the events that changed
    /// (occurrences folded onto their series master), or `None` when
    /// Graph answered `410 Gone` — the cursor expired.
    let deltaRound (link: CalendarLinkRef) (startUrl: string) = async {
        let changed = ResizeArray<string>()
        let mutable next = Some startUrl
        let mutable finalLink = None
        let mutable failure = None
        let mutable gone = false

        while next.IsSome && failure.IsNone && not gone do
            let url = next.Value
            next <- None

            match! call link (jsonRequest HttpMethod.Get url None) with
            | Error e -> failure <- Some e
            | Ok r when int r.Status = 410 -> gone <- true
            | Ok r when int r.Status = 404 -> failure <- Some(CalendarNotFound link.ExternalCalendarId)
            | Ok r when not (isSuccess r.Status) -> failure <- Some(classifyStatus r.Status r.Body r.RetryAfter)
            | Ok r ->
                try
                    use doc = JsonDocument.Parse r.Body
                    let root = doc.RootElement

                    match tryProp "value" root with
                    | Some arr when arr.ValueKind = JsonValueKind.Array ->
                        for item in arr.EnumerateArray() do
                            // `@removed` — deleted, or moved out of the
                            // window. Not appliable through the seam.
                            if (tryProp "@removed" item).IsNone then
                                let target =
                                    match tryStr "type" item with
                                    | Some "occurrence"
                                    | Some "exception" -> tryStr "seriesMasterId" item
                                    | _ -> tryStr "id" item

                                match target with
                                | Some id when not (changed.Contains id) -> changed.Add id
                                | _ -> ()
                    | _ -> ()

                    next <- tryStr "@odata.nextLink" root |> Option.map rebase
                    finalLink <- tryStr "@odata.deltaLink" root |> Option.map rebase
                with :? JsonException as ex ->
                    failure <- Some(MalformedPayload(sprintf "Graph delta page was not JSON: %s" ex.Message))

        match failure with
        | Some e -> return Error e
        | None when gone -> return Ok None
        | None -> return Ok(Some(List.ofSeq changed, finalLink))
    }

    let freshRoundUrl (calendarId: ExternalCalendarId) (windowStart: DateTimeOffset) (windowEnd: DateTimeOffset) =
        sprintf
            "%s/calendarView/delta?startDateTime=%s&endDateTime=%s"
            (calendarUrl calendarId)
            (esc (isoInstant windowStart))
            (esc (isoInstant windowEnd))

    /// The ids of the events that changed in the window, following the
    /// stored cursor when the caller asked for the default window and a
    /// fresh-enough cursor exists, and a fresh round otherwise.
    let changedEventIds (link: CalendarLinkRef) (since: DateTimeOffset option) = async {
        let now = clock ()
        let windowEnd = now.AddDays(float settings.PullWindowDays)

        let windowStart =
            since |> Option.defaultValue (now.AddDays(float -settings.PullLookbackDays))

        let fresh () = async {
            match! deltaRound link (freshRoundUrl link.ExternalCalendarId windowStart windowEnd) with
            | Error e -> return Error e
            | Ok None -> return Error(ExternalRejected(410, "Graph refused a fresh delta round as expired"))
            | Ok(Some(ids, deltaLink)) ->
                // Only the DEFAULT window's cursor is worth keeping: a
                // bounded `since` read is an ad-hoc question, not the
                // mirror's position.
                match since, deltaLink with
                | None, Some l ->
                    let state = {
                        DeltaLink = l
                        RoundStartedAt = now
                        WindowEnd = windowEnd
                    }

                    let! _ = secretStore.SetSecret(link.ScopeId, deltaStateKey link, writeDeltaState state)
                    ()
                | _ -> ()

                return Ok ids
        }

        match since with
        | Some _ -> return! fresh ()
        | None ->
            let! stored = secretStore.GetSecret(link.ScopeId, deltaStateKey link)

            match stored |> Option.bind readDeltaState with
            | Some state when
                now - state.RoundStartedAt < settings.FullResyncInterval
                && state.WindowEnd > now
                ->
                match! deltaRound link state.DeltaLink with
                | Error e -> return Error e
                | Ok None -> return! fresh ()
                | Ok(Some(ids, deltaLink)) ->
                    match deltaLink with
                    | Some l when l <> state.DeltaLink ->
                        let! _ =
                            secretStore.SetSecret(
                                link.ScopeId,
                                deltaStateKey link,
                                writeDeltaState { state with DeltaLink = l }
                            )

                        ()
                    | _ -> ()

                    return Ok ids
            | _ -> return! fresh ()
    }

    // ─── Subscriptions ──────────────────────────────────────────────

    let subscriptionResource (calendarId: ExternalCalendarId) =
        sprintf "me/calendars/%s/events" calendarId

    /// Open or renew the subscription for one link. A stored id is
    /// renewed in place (`PATCH` the expiry); a missing or vanished one
    /// is created, which is when Graph performs its validation handshake
    /// against `notificationUrl`.
    let ensureSubscription (link: CalendarLinkRef) (notificationBase: string) = async {
        match! secretStore.GetSecret(link.ScopeId, SecretKeys.WebhookSecret) with
        | None
        | Some "" ->
            return
                Error(
                    AuthenticationFailed(
                        sprintf
                            "no '%s' secret in scope '%s' — change notifications need it to sign each subscription's clientState"
                            SecretKeys.WebhookSecret
                            link.ScopeId
                    )
                )
        | Some secret ->
            let expiresAt = clock () + settings.SubscriptionLifetime
            let expiry = isoInstant expiresAt
            let! stored = secretStore.GetSecret(link.ScopeId, subscriptionKey link)

            let create () = async {
                let body = JsonObject()
                body["changeType"] <- JsonValue.Create "created,updated,deleted"

                body["notificationUrl"] <-
                    JsonValue.Create(notificationUrlFor notificationBase link.ScopeId link.ExternalCalendarId)

                body["resource"] <- JsonValue.Create(subscriptionResource link.ExternalCalendarId)
                body["expirationDateTime"] <- JsonValue.Create expiry
                body["clientState"] <- JsonValue.Create(clientStateFor secret link.ScopeId link.ExternalCalendarId)
                body["latestSupportedTlsVersion"] <- JsonValue.Create "v1_2"

                let url = apiBase + "/subscriptions"

                match! call link (jsonRequest HttpMethod.Post url (Some(body.ToJsonString()))) with
                | Error e -> return Error e
                | Ok r when not (isSuccess r.Status) -> return Error(classifyStatus r.Status r.Body r.RetryAfter)
                | Ok r ->
                    try
                        use doc = JsonDocument.Parse r.Body

                        match tryStr "id" doc.RootElement with
                        | None -> return Error(MalformedPayload "Graph subscription response carried no id")
                        | Some id ->
                            let! _ = secretStore.SetSecret(link.ScopeId, subscriptionKey link, id)
                            return Ok()
                    with :? JsonException as ex ->
                        return
                            Error(MalformedPayload(sprintf "Graph subscription response was not JSON: %s" ex.Message))
            }

            match stored with
            | Some id when id <> "" ->
                let body = JsonObject()
                body["expirationDateTime"] <- JsonValue.Create expiry
                let url = sprintf "%s/subscriptions/%s" apiBase (esc id)

                match! call link (jsonRequest HttpMethod.Patch url (Some(body.ToJsonString()))) with
                | Error e -> return Error e
                | Ok r when int r.Status = 404 -> return! create ()
                | Ok r when not (isSuccess r.Status) -> return Error(classifyStatus r.Status r.Body r.RetryAfter)
                | Ok _ -> return Ok()
            | _ -> return! create ()
    }

    /// The production constructor: the platform's egress-policy-wrapped
    /// transport and the wall clock.
    new(secretStore: ISecretStore, refresher: IOAuthTokenRefresher, settings: MicrosoftGraphCalendarSettings) =
        MicrosoftGraphCalendarBridge(
            secretStore,
            refresher,
            settings,
            new HttpClientHandler(),
            fun () -> DateTimeOffset.UtcNow
        )

    /// An explicit transport, the wall clock.
    new
        (
            secretStore: ISecretStore,
            refresher: IOAuthTokenRefresher,
            settings: MicrosoftGraphCalendarSettings,
            handler: HttpMessageHandler
        ) =
        MicrosoftGraphCalendarBridge(secretStore, refresher, settings, handler, fun () -> DateTimeOffset.UtcNow)

    /// The settings this bridge was constructed with.
    member _.Settings: MicrosoftGraphCalendarSettings = settings

    /// The notification URL a link's subscription points at, or `None`
    /// when change notifications are not configured.
    member _.NotificationUrlFor(link: CalendarLinkRef) : string option =
        settings.NotificationUrl
        |> Option.map (fun baseUrl -> notificationUrlFor baseUrl link.ScopeId link.ExternalCalendarId)

    interface ICalendarBridge with

        member _.Kind = KindName

        member _.Capabilities = {
            SupportsWebhooks = settings.NotificationUrl.IsSome
            SupportsIncrementalPull = false
            MinimumPollInterval = TimeSpan.FromMinutes 5.0
        }

        member _.LinkResource(link) = async {
            let url = calendarUrl link.ExternalCalendarId + "?$select=id,name,canEdit"

            match! call link (jsonRequest HttpMethod.Get url None) with
            | Error e -> return Error e
            | Ok r when int r.Status = 404 -> return Error(CalendarNotFound link.ExternalCalendarId)
            | Ok r when not (isSuccess r.Status) -> return Error(classifyStatus r.Status r.Body r.RetryAfter)
            | Ok r ->
                let readOnly =
                    try
                        use doc = JsonDocument.Parse r.Body

                        match doc.RootElement.TryGetProperty "canEdit" with
                        | true, v -> v.ValueKind = JsonValueKind.False
                        | _ -> false
                    with _ ->
                        false

                if readOnly then
                    return Error(ExternalRejected(403, "the linked Microsoft calendar is read-only for this user"))
                else
                    match settings.NotificationUrl with
                    | None -> return Ok()
                    | Some notificationBase -> return! ensureSubscription link notificationBase
        }

        member _.UnlinkResource(link) = async {
            let clearLocal () = async {
                let! _ = secretStore.DeleteSecret(link.ScopeId, subscriptionKey link)
                let! _ = secretStore.DeleteSecret(link.ScopeId, deltaStateKey link)
                return Ok()
            }

            match! secretStore.GetSecret(link.ScopeId, subscriptionKey link) with
            | None
            | Some "" -> return! clearLocal ()
            | Some id ->
                let url = sprintf "%s/subscriptions/%s" apiBase (esc id)

                match! call link (jsonRequest HttpMethod.Delete url None) with
                // A subscription left behind expires on its own within
                // days; a revoked credential must not make the unlink
                // impossible.
                | Error(AuthenticationFailed _) -> return! clearLocal ()
                | Error e -> return Error e
                | Ok r when isSuccess r.Status || int r.Status = 404 -> return! clearLocal ()
                | Ok r when int r.Status = 401 || int r.Status = 403 -> return! clearLocal ()
                | Ok r -> return Error(classifyStatus r.Status r.Body r.RetryAfter)
        }

        member _.Push(link, booking, existing) = async {
            let known =
                match existing with
                | Some id when id.Length > 0 -> Some id
                | _ -> None

            let! target = async {
                match known with
                | Some id -> return Ok(Some id)
                | None -> return! findByBookingId link booking.Id
            }

            match target with
            | Error e -> return Error e
            | Ok target ->
                if booking.Status = Cancelled then
                    match target with
                    // Nothing was ever mirrored: already the state we want.
                    | None -> return Ok(existing |> Option.defaultValue "")
                    | Some id ->
                        match! call link (jsonRequest HttpMethod.Delete (eventUrl link.ExternalCalendarId id) None) with
                        | Error e -> return Error e
                        // Already gone is the state we wanted.
                        | Ok r when isSuccess r.Status || int r.Status = 404 -> return Ok id
                        | Ok r -> return Error(classifyStatus r.Status r.Body r.RetryAfter)
                else
                    match target with
                    | Some id ->
                        let payload = bookingToGraphEvent booking false

                        match!
                            call
                                link
                                (jsonRequest HttpMethod.Patch (eventUrl link.ExternalCalendarId id) (Some payload))
                        with
                        | Error e -> return Error e
                        | Ok r when int r.Status = 404 ->
                            match known with
                            | Some _ -> return Error(EventNotFound id)
                            | None -> return Error(CalendarNotFound link.ExternalCalendarId)
                        | Ok r when not (isSuccess r.Status) ->
                            return Error(classifyStatus r.Status r.Body r.RetryAfter)
                        | Ok _ -> return Ok id
                    | None ->
                        let payload = bookingToGraphEvent booking true
                        let url = calendarUrl link.ExternalCalendarId + "/events"

                        match! call link (jsonRequest HttpMethod.Post url (Some payload)) with
                        | Error e -> return Error e
                        | Ok r when int r.Status = 404 -> return Error(CalendarNotFound link.ExternalCalendarId)
                        | Ok r when not (isSuccess r.Status) ->
                            return Error(classifyStatus r.Status r.Body r.RetryAfter)
                        | Ok r ->
                            try
                                use doc = JsonDocument.Parse r.Body

                                match tryStr "id" doc.RootElement with
                                | Some id -> return Ok id
                                | None -> return Error(MalformedPayload "Graph create response carried no event id")
                            with :? JsonException as ex ->
                                return
                                    Error(MalformedPayload(sprintf "Graph create response was not JSON: %s" ex.Message))
        }

        member _.Pull(link, since, defaults) = async {
            match! changedEventIds link since with
            | Error e -> return Error e
            | Ok ids ->
                let events = ResizeArray<ExternalEvent>()
                let mutable failure = None

                for id in ids do
                    if failure.IsNone then
                        match! readEvent link id defaults with
                        | Error e -> failure <- Some e
                        | Ok None -> ()
                        | Ok(Some e) -> events.Add e

                match failure with
                | Some e -> return Error e
                | None -> return Ok(List.ofSeq events)
        }

        member _.HandleWebhook(headers, body) = async {
            match settings.NotificationUrl with
            // Polling-only: no subscription was ever opened, so nothing
            // Graph sends can be ours.
            | None -> return Ok []
            | Some _ ->
                let header name =
                    headers
                    |> Map.tryFindKey (fun k _ -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun k -> headers[k])

                match header NotificationHeaders.Scope, header NotificationHeaders.CalendarId with
                | Some scopeId, Some calendarId when scopeId <> "" && calendarId <> "" ->
                    match! secretStore.GetSecret(scopeId, SecretKeys.WebhookSecret) with
                    | None
                    | Some "" ->
                        return Error(MalformedPayload "no webhook secret configured to verify the notification with")
                    | Some secret ->
                        let expected = Encoding.UTF8.GetBytes(clientStateFor secret scopeId calendarId)

                        try
                            use doc = JsonDocument.Parse(Encoding.UTF8.GetString body)

                            let items =
                                match tryProp "value" doc.RootElement with
                                | Some arr when arr.ValueKind = JsonValueKind.Array ->
                                    arr.EnumerateArray() |> List.ofSeq
                                | _ -> []

                            let verified =
                                items
                                |> List.forall (fun item ->
                                    match tryStr "clientState" item with
                                    | Some state ->
                                        CryptographicOperations.FixedTimeEquals(
                                            ReadOnlySpan(Encoding.UTF8.GetBytes state),
                                            ReadOnlySpan expected
                                        )
                                    | None -> false)

                            if List.isEmpty items then
                                return Error(MalformedPayload "notification carried no change")
                            elif not verified then
                                return Error(MalformedPayload "notification clientState does not verify")
                            else
                                let changed =
                                    items
                                    |> List.choose (fun item ->
                                        tryProp "resourceData" item |> Option.bind (tryStr "id"))
                                    |> List.distinct

                                return
                                    Ok [
                                        {
                                            ExternalCalendarId = calendarId
                                            ChangedEventId =
                                                match changed with
                                                | [ one ] -> Some one
                                                | _ -> None
                                        }
                                    ]
                        with :? JsonException ->
                            return Error(MalformedPayload "notification body is not Graph's JSON")
                | _ -> return Error(MalformedPayload "notification carried no scope / calendar to verify")
        }