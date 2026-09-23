// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 830 - the Google Calendar `ICalendarBridge`: the Calendar v3
/// REST surface over BCL `HttpClient`, OAuth tokens through the refresh
/// substrate, change notifications through a watch channel.
module ToolUp.Calendar.GoogleCalendar

open System
open System.Globalization
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
open ToolUp.Calendar.GoogleCalendarOAuth

// ─── Phase 830 — the Google Calendar bridge ─────────────────────────
//
// The second `ICalendarBridge`, and the first that is not polling-only.
// BCL `HttpClient` over the Calendar v3 REST surface — no Google client
// library (GP 1): the bridge needs six endpoints, and the vendor SDK's
// watch-channel and batch support would save none of the code this file
// holds while adding its dependency tree to every deployment that
// composes the bridge. The README records the decision.
//
// **Idempotent push by construction.** An event created by `Push` gets a
// CLIENT-SPECIFIED Google event id derived from the booking id
// (`eventIdOf`), so a second push of the same booking is refused by
// Google as a duplicate (409) instead of creating a twin — and the
// bridge turns that refusal into the update it should have been. The
// booking id also rides a private extended property, so the mapping back
// never depends on decoding an id.
//
// **Update is read-merge-write, not PATCH.** An update fetches the event,
// replaces exactly the fields this bridge manages, and writes the whole
// resource back. A PATCH cannot remove one entry from the private
// extended-property map, and a blind PUT would wipe what the calendar's
// owner set in Google (reminders, colour, conferencing). Read-merge-write
// does neither.
//
// **Pull is incremental.** `since` is honoured against Google's
// modification cursor — the stored sync token when there is one,
// `updatedMin` otherwise — so `SupportsIncrementalPull` is `true` and the
// sync engine carries a cursor for this bridge. The sync token is kept in
// `ISecretStore` beside the connection's credentials: the only store a
// bridge holds, and it is what lets the bridge stay stateless between
// calls (portability rule 4). An expired token (410) is dropped and the
// pull re-runs on `updatedMin`.
//
// **Change notifications** arrive on a watch channel whose token
// authenticates them: `channelToken` is an HMAC, under a deployment
// secret, over the scope and the calendar the channel watches, so a
// notification that does not carry it is refused and one that does names
// its own scope. Channels expire; `LinkResource` renews a channel that is
// near its end, and the renewal job re-links on a schedule and polls a
// link whose renewal fails (`GoogleCalendarChannels.renewalDeclaration`).
//
// **Declared limits.** An event Google reports as cancelled is not
// returned by `Pull` (the sync engine has no verb to apply an external
// deletion yet), and a modified single instance of a recurring event
// (`recurringEventId`) is skipped (the booking model has no per-instance
// exception). Both are recorded in the README.

/// Stable `ICalendarBridge.Kind` of the Google bridge. Persisted on every
/// link, so it is a wire value.
[<Literal>]
let KindName = "Google"

/// `ISecretStore` key of the deployment secret watch-channel tokens are
/// signed with. Read from `GoogleCalendarSettings.ChannelSecretScope`.
[<Literal>]
let ChannelSecretKey = "GOOGLE_CALENDAR_CHANNEL_SECRET"

/// Settings for the Google Calendar bridge. Carries no credential.
type GoogleCalendarSettings = {
    /// Calendar v3 base URL.
    ApiBaseUrl: string
    /// When set, replaces `ApiBaseUrl` for every request — a test double
    /// or an egress proxy standing in for Google.
    EndpointOverride: string option
    /// Which OAuth connection authorises a link. `None` — per user: the
    /// connection id is the link's `UserId`, so every user mirrors
    /// through their own consent. `Some id` — one shared connection (a
    /// room or service account) authorises every link.
    ConnectionId: string option
    /// The public HTTPS URL the deployment mounted
    /// `GoogleCalendarChannels.handler` at. `None` — no watch channels:
    /// the bridge declares itself polling-only and the sync engine polls
    /// it.
    WebhookAddress: string option
    /// Scope the `ChannelSecretKey` secret is read from.
    ChannelSecretScope: string
    /// Requested lifetime of a watch channel. Google caps it; the
    /// expiration it grants is what the bridge records.
    ChannelTtl: TimeSpan
    /// A channel expiring within this window is renewed by the next
    /// `LinkResource` (the renewal job's tick).
    ChannelRenewalLead: TimeSpan
    /// How far forward a pull with no `since` looks.
    PullWindowDays: int
    /// How far back a pull with no `since` looks.
    PullLookbackDays: int
}

/// Defaults and the effective-base resolution for `GoogleCalendarSettings`.
module GoogleCalendarSettings =
    /// Google's production API, per-user connections, no watch channels,
    /// a seven-day channel lifetime renewed a day early, and a 90-day
    /// pull window.
    let defaults: GoogleCalendarSettings = {
        ApiBaseUrl = "https://www.googleapis.com/calendar/v3"
        EndpointOverride = None
        ConnectionId = None
        WebhookAddress = None
        ChannelSecretScope = "_platform"
        ChannelTtl = TimeSpan.FromDays 7.0
        ChannelRenewalLead = TimeSpan.FromDays 1.0
        PullWindowDays = 90
        PullLookbackDays = 1
    }

    /// The base every request is issued against.
    let effectiveBase (settings: GoogleCalendarSettings) : string =
        (settings.EndpointOverride |> Option.defaultValue settings.ApiBaseUrl).TrimEnd '/'

    /// The OAuth connection that authorises `link`.
    let connectionOf (settings: GoogleCalendarSettings) (link: CalendarLinkRef) : string =
        settings.ConnectionId |> Option.defaultValue link.UserId

// ─── Event ids ──────────────────────────────────────────────────────

let private base32Hex = "0123456789abcdefghijklmnopqrstuv"

/// Prefix of every event id this bridge mints. Itself inside Google's
/// event-id alphabet (`a`–`v`, `0`–`9`), and long enough that the
/// shortest id clears Google's five-character minimum.
[<Literal>]
let EventIdPrefix = "toolup"

/// The Google event id a booking is created under: `EventIdPrefix` plus
/// the booking id's UTF-8 bytes in lower-case base32hex, which is
/// exactly Google's permitted alphabet. Deterministic, so a re-push of
/// the same booking addresses the same event.
let eventIdOf (bookingId: BookingId) : string =
    let bytes = Encoding.UTF8.GetBytes bookingId
    let sb = StringBuilder(EventIdPrefix)
    let mutable buffer = 0
    let mutable bits = 0

    for b in bytes do
        buffer <- ((buffer <<< 8) ||| int b) &&& 0xFFFF
        bits <- bits + 8

        while bits >= 5 do
            bits <- bits - 5
            sb.Append(base32Hex[(buffer >>> bits) &&& 31]) |> ignore

    if bits > 0 then
        sb.Append(base32Hex[(buffer <<< (5 - bits)) &&& 31]) |> ignore

    sb.ToString()

/// The booking id an `eventIdOf` id encodes, or `None` for an id this
/// bridge did not mint (an event created in Google itself).
let tryBookingIdOfEventId (eventId: string) : BookingId option =
    if isNull eventId || not (eventId.StartsWith EventIdPrefix) then
        None
    else
        let encoded = eventId.Substring EventIdPrefix.Length
        let bytes = ResizeArray<byte>()
        let mutable buffer = 0
        let mutable bits = 0
        let mutable valid = encoded.Length > 0

        for c in encoded do
            let v = base32Hex.IndexOf c

            if v < 0 then
                valid <- false
            else
                buffer <- ((buffer <<< 5) ||| v) &&& 0xFFFF
                bits <- bits + 5

                if bits >= 8 then
                    bits <- bits - 8
                    bytes.Add(byte ((buffer >>> bits) &&& 0xFF))

        if not valid then
            None
        else
            try
                let decoded = UTF8Encoding(false, true).GetString(bytes.ToArray())

                if eventIdOf decoded = eventId then Some decoded else None
            with :? ArgumentException ->
                None

// ─── Watch-channel tokens ───────────────────────────────────────────

/// The token format version prefix.
[<Literal>]
let ChannelTokenVersion = "v1"

let private hexHmac (secret: string) (payload: string) : string =
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
    Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes payload)).ToLowerInvariant()

let private base64Url (value: string) : string =
    Convert.ToBase64String(Encoding.UTF8.GetBytes value).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private tryUnbase64Url (value: string) : string option =
    try
        let padded =
            let s = value.Replace('-', '+').Replace('_', '/')

            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Some(Encoding.UTF8.GetString(Convert.FromBase64String padded))
    with :? FormatException ->
        None

/// The token a watch channel is created with and every notification on
/// it echoes (`X-Goog-Channel-Token`): `v1.<base64url scope>.<hex HMAC>`,
/// the HMAC-SHA256 under the deployment channel secret over the scope and
/// the watched calendar. Naming the scope is what lets the webhook route
/// find the links without a cross-scope index; the MAC is what stops a
/// caller naming one it has no token for.
let channelToken (secret: string) (scopeId: string) (calendarId: ExternalCalendarId) : string =
    let mac =
        hexHmac secret (sprintf "%s\n%s\n%s" ChannelTokenVersion scopeId calendarId)

    sprintf "%s.%s.%s" ChannelTokenVersion (base64Url scopeId) mac

/// The scope a channel token NAMES — unverified. The webhook route reads
/// it to address the sync engine; the bridge verifies it before any
/// notification is acted on.
let tryScopeOfChannelToken (token: string) : string option =
    match (if isNull token then "" else token).Split '.' with
    | [| version; scope; _mac |] when version = ChannelTokenVersion -> tryUnbase64Url scope
    | _ -> None

/// Whether `token` is the genuine token for `calendarId`, under `secret`.
/// Constant-time over the MAC.
let verifyChannelToken (secret: string) (calendarId: ExternalCalendarId) (token: string) : bool =
    match tryScopeOfChannelToken token with
    | None -> false
    | Some scopeId ->
        let expected = Encoding.UTF8.GetBytes(channelToken secret scopeId calendarId)
        let provided = Encoding.UTF8.GetBytes token
        CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, ReadOnlySpan provided)

/// The calendar id a notification's `X-Goog-Resource-URI` names
/// (`…/calendars/<id>/events…`), URL-decoded.
let tryCalendarIdOfResourceUri (resourceUri: string) : ExternalCalendarId option =
    if String.IsNullOrWhiteSpace resourceUri then
        None
    else
        let marker = "/calendars/"
        let path = resourceUri.Split('?').[0]
        let start = path.IndexOf marker

        if start < 0 then
            None
        else
            let rest = path.Substring(start + marker.Length)
            let finish = rest.IndexOf '/'
            let encoded = if finish < 0 then rest else rest.Substring(0, finish)

            if encoded.Length = 0 then
                None
            else
                Some(Uri.UnescapeDataString encoded)

// ─── Booking ↔ Google event ─────────────────────────────────────────

/// Prefix of every private extended property this bridge manages. Keys
/// outside it are the calendar owner's and survive every update.
[<Literal>]
let ManagedPropertyPrefix = "toolup."

let private bookingIdProperty = ManagedPropertyPrefix + "bookingId"
let private organizerProperty = ManagedPropertyPrefix + "organizer"
let private attendeesProperty = ManagedPropertyPrefix + "attendees"
let private metaPrefix = ManagedPropertyPrefix + "meta."

let private rfc3339 (value: DateTimeOffset) : string =
    value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

let private emailOf (address: string) : string =
    if address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) then
        address.Substring 7
    else
        address

let private splitAttendees (joined: string) : string list =
    joined.Split([| iCalendar.AttendeeSeparator |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map _.Trim()
    |> Array.filter (fun a -> a.Length > 0)
    |> List.ofArray

/// Merge `booking` into a Google event resource. `existing` is the event
/// as Google holds it (an update) or `None` (a create); every field this
/// bridge does not manage is left exactly as `existing` carries it.
let toGoogleEvent (booking: Booking) (existing: JsonObject option) : JsonObject =
    let event =
        match existing with
        | Some e -> e.DeepClone().AsObject()
        | None -> JsonObject()

    let set (name: string) (value: JsonNode) = event[name] <- value
    let remove (name: string) = event.Remove name |> ignore

    set "summary" (JsonValue.Create booking.Title)
    set "status" (JsonValue.Create "confirmed")

    let instant (value: DateTimeOffset) =
        let o = JsonObject()
        o["dateTime"] <- JsonValue.Create(rfc3339 value)
        // Google requires a time zone on a recurring event's start and
        // end; UTC matches the booking's own instants.
        o["timeZone"] <- JsonValue.Create "UTC"
        o

    set "start" (instant booking.StartUtc)
    set "end" (instant booking.EndUtc)

    match booking.Recurrence with
    | Some rule -> set "recurrence" (JsonArray([| JsonValue.Create("RRULE:" + iCalendar.emitRRule rule) :> JsonNode |]))
    | None -> remove "recurrence"

    match Map.tryFind iCalendar.LocationKey booking.Metadata with
    | Some location -> set "location" (JsonValue.Create location)
    | None -> remove "location"

    let attendees =
        Map.tryFind iCalendar.AttendeesKey booking.Metadata
        |> Option.map splitAttendees
        |> Option.defaultValue []

    match attendees with
    | [] -> remove "attendees"
    | list ->
        let array = JsonArray()

        for a in list do
            let o = JsonObject()
            o["email"] <- JsonValue.Create(emailOf a)
            array.Add o

        set "attendees" array

    // Private extended properties: keep the owner's, replace ours.
    let extended =
        match event["extendedProperties"] with
        | :? JsonObject as o -> o
        | _ -> JsonObject()

    let privateProps =
        match extended["private"] with
        | :? JsonObject as o -> o
        | _ -> JsonObject()

    let foreignKeys =
        privateProps
        |> Seq.map _.Key
        |> Seq.filter (fun k -> not (k.StartsWith ManagedPropertyPrefix))
        |> Set.ofSeq

    let rebuilt = JsonObject()

    for kvp in privateProps do
        if Set.contains kvp.Key foreignKeys then
            rebuilt[kvp.Key] <- (if isNull kvp.Value then null else kvp.Value.DeepClone())

    rebuilt[bookingIdProperty] <- JsonValue.Create booking.Id

    match Map.tryFind iCalendar.OrganizerKey booking.Metadata with
    | Some organizer -> rebuilt[organizerProperty] <- JsonValue.Create organizer
    | None -> ()

    match Map.tryFind iCalendar.AttendeesKey booking.Metadata with
    | Some joined -> rebuilt[attendeesProperty] <- JsonValue.Create joined
    | None -> ()

    let reserved =
        Set.ofList [ iCalendar.LocationKey; iCalendar.OrganizerKey; iCalendar.AttendeesKey ]

    for kvp in booking.Metadata do
        if not (Set.contains kvp.Key reserved) then
            rebuilt[metaPrefix + kvp.Key] <- JsonValue.Create kvp.Value

    let newExtended = JsonObject()

    for kvp in extended do
        if kvp.Key <> "private" then
            newExtended[kvp.Key] <- (if isNull kvp.Value then null else kvp.Value.DeepClone())

    newExtended["private"] <- rebuilt
    set "extendedProperties" newExtended
    event

let private tryProp (name: string) (el: JsonElement) : JsonElement option =
    match el.ValueKind with
    | JsonValueKind.Object ->
        match el.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
        | _ -> None
    | _ -> None

let private tryStr (name: string) (el: JsonElement) : string option =
    tryProp name el
    |> Option.bind (fun v ->
        if v.ValueKind = JsonValueKind.String then
            Some(v.GetString())
        else
            None)

let private parseInstant (el: JsonElement) : Result<DateTimeOffset, string> =
    match tryStr "dateTime" el, tryStr "date" el with
    | Some dt, _ ->
        match DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, v -> Ok(v.ToUniversalTime())
        | _ -> Error(sprintf "unparseable dateTime '%s'" dt)
    | None, Some d ->
        // An all-day event: the date at midnight UTC, the only instant a
        // zone-less date can honestly map to.
        match DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, v -> Ok(DateTimeOffset(DateTime.SpecifyKind(v.Date, DateTimeKind.Utc)))
        | _ -> Error(sprintf "unparseable date '%s'" d)
    | None, None -> Error "neither dateTime nor date"

/// Why an event in a pull was not turned into a booking.
type SkippedEvent =
    /// Google reports the event cancelled (deleted).
    | CancelledEvent
    /// A modified single instance of a recurring event.
    | RecurringInstance

/// Project one Google event onto the booking domain. `defaults` supplies
/// what no calendar carries, exactly as `iCalendar.vEventToBooking` does.
/// `Ok (Error _)` is an event deliberately skipped; `Error _` is one that
/// could not be read.
let ofGoogleEvent (defaults: Booking) (event: JsonElement) : Result<Result<ExternalEvent, SkippedEvent>, string> =
    match tryStr "id" event with
    | None -> Error "event has no id"
    | Some eventId ->
        if tryStr "status" event = Some "cancelled" then
            Ok(Error CancelledEvent)
        elif (tryStr "recurringEventId" event).IsSome then
            Ok(Error RecurringInstance)
        else
            let privateProps =
                tryProp "extendedProperties" event
                |> Option.bind (tryProp "private")
                |> Option.map (fun p ->
                    if p.ValueKind = JsonValueKind.Object then
                        p.EnumerateObject()
                        |> Seq.choose (fun kv ->
                            if kv.Value.ValueKind = JsonValueKind.String then
                                Some(kv.Name, kv.Value.GetString())
                            else
                                None)
                        |> Map.ofSeq
                    else
                        Map.empty)
                |> Option.defaultValue Map.empty

            let ours = Map.containsKey bookingIdProperty privateProps

            let bookingId =
                match Map.tryFind bookingIdProperty privateProps with
                | Some id -> id
                | None -> tryBookingIdOfEventId eventId |> Option.defaultValue eventId

            let start = tryProp "start" event |> Option.map parseInstant
            let finish = tryProp "end" event |> Option.map parseInstant

            match start, finish with
            | Some(Error e), _
            | _, Some(Error e) -> Error(sprintf "event %s: %s" eventId e)
            | None, _
            | _, None -> Error(sprintf "event %s has no start or end" eventId)
            | Some(Ok startUtc), Some(Ok endUtc) ->
                let recurrenceLine =
                    tryProp "recurrence" event
                    |> Option.bind (fun r ->
                        if r.ValueKind = JsonValueKind.Array then
                            r.EnumerateArray()
                            |> Seq.choose (fun line ->
                                if line.ValueKind = JsonValueKind.String then
                                    Some(line.GetString())
                                else
                                    None)
                            |> Seq.tryFind (fun line -> line.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
                        else
                            None)

                let recurrence =
                    match recurrenceLine with
                    | None -> Ok None
                    | Some line ->
                        match iCalendar.parseRRule (line.Substring 6) with
                        | Ok rule -> Ok(Some rule)
                        | Error e -> Error(sprintf "event %s: %s" eventId e)

                match recurrence with
                | Error e -> Error e
                | Ok recurrence ->
                    // Native attendees, minus the organizer entry Google
                    // may add, in Google's order.
                    let nativeAttendees =
                        tryProp "attendees" event
                        |> Option.map (fun a ->
                            if a.ValueKind = JsonValueKind.Array then
                                a.EnumerateArray()
                                |> Seq.filter (fun x ->
                                    match tryProp "organizer" x with
                                    | Some flag when flag.ValueKind = JsonValueKind.True -> false
                                    | _ -> true)
                                |> Seq.choose (tryStr "email")
                                |> List.ofSeq
                            else
                                [])
                        |> Option.defaultValue []

                    // The attendee carry-through: when Google still holds
                    // exactly the attendees we pushed, the pushed spelling
                    // and order come back; when someone changed them in
                    // Google, Google's list wins.
                    let attendees =
                        match Map.tryFind attendeesProperty privateProps with
                        | Some pushed ->
                            let pushedEmails = splitAttendees pushed |> List.map emailOf |> Set.ofList

                            if pushedEmails = Set.ofList nativeAttendees then
                                Some pushed
                            elif List.isEmpty nativeAttendees then
                                None
                            else
                                Some(nativeAttendees |> List.map (sprintf "mailto:%s") |> String.concat ",")
                        | None when List.isEmpty nativeAttendees -> None
                        | None -> Some(nativeAttendees |> List.map (sprintf "mailto:%s") |> String.concat ",")

                    let organizer =
                        match Map.tryFind organizerProperty privateProps with
                        | Some o -> Some o
                        | None when ours -> None
                        | None ->
                            tryProp "organizer" event
                            |> Option.bind (tryStr "email")
                            |> Option.map (sprintf "mailto:%s")

                    let location = tryStr "location" event |> Option.filter (fun l -> l.Length > 0)

                    let withKey key value metadata =
                        match value with
                        | Some v -> Map.add key v metadata
                        | None -> metadata

                    let metadata =
                        privateProps
                        |> Map.toSeq
                        |> Seq.choose (fun (k, v) ->
                            if k.StartsWith metaPrefix then
                                Some(k.Substring metaPrefix.Length, v)
                            else
                                None)
                        |> Map.ofSeq
                        |> withKey iCalendar.LocationKey location
                        |> withKey iCalendar.OrganizerKey organizer
                        |> withKey iCalendar.AttendeesKey attendees

                    let lastModified =
                        tryStr "updated" event
                        |> Option.bind (fun u ->
                            match
                                DateTimeOffset.TryParse(
                                    u,
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal
                                )
                            with
                            | true, v -> Some(v.ToUniversalTime())
                            | _ -> None)

                    Ok(
                        Ok {
                            ExternalEventId = eventId
                            Booking = {
                                defaults with
                                    Id = bookingId
                                    Title = tryStr "summary" event |> Option.defaultValue ""
                                    StartUtc = startUtc
                                    EndUtc = endUtc
                                    Recurrence = recurrence
                                    Metadata = metadata
                            }
                            LastModifiedUtc = lastModified
                        }
                    )

// ─── Per-link state in ISecretStore ─────────────────────────────────

let private linkHash (link: CalendarLinkRef) : string =
    let bytes =
        SHA256.HashData(Encoding.UTF8.GetBytes(link.ResourceId + "\n" + link.ExternalCalendarId))

    Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant()

/// `ISecretStore` key (link scope) holding a link's Google sync token.
let syncTokenKey (link: CalendarLinkRef) : string = "google-calendar-sync-" + linkHash link

/// `ISecretStore` key (link scope) holding a link's watch-channel record.
let channelKey (link: CalendarLinkRef) : string =
    "google-calendar-channel-" + linkHash link

/// The watch channel a link holds, as recorded when it was created.
type WatchChannel = {
    /// The channel id this bridge chose.
    ChannelId: string
    /// Google's id for the watched resource — `channels.stop` needs it.
    ResourceId: string
    /// The calendar the channel watches.
    CalendarId: ExternalCalendarId
    /// When Google stops delivering on the channel.
    ExpiresAtUtc: DateTimeOffset
}

let private channelToJson (c: WatchChannel) : string =
    let o = JsonObject()
    o["channelId"] <- JsonValue.Create c.ChannelId
    o["resourceId"] <- JsonValue.Create c.ResourceId
    o["calendarId"] <- JsonValue.Create c.CalendarId
    o["expiresAtUtc"] <- JsonValue.Create(c.ExpiresAtUtc.ToString("o", CultureInfo.InvariantCulture))
    o.ToJsonString()

let private tryChannelOfJson (json: string) : WatchChannel option =
    try
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        match
            tryStr "channelId" root, tryStr "resourceId" root, tryStr "calendarId" root, tryStr "expiresAtUtc" root
        with
        | Some id, Some resource, Some calendar, Some expires ->
            match DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, at ->
                Some {
                    ChannelId = id
                    ResourceId = resource
                    CalendarId = calendar
                    ExpiresAtUtc = at
                }
            | _ -> None
        | _ -> None
    with :? JsonException ->
        None

// ─── Response classification ────────────────────────────────────────

/// Map a Calendar v3 status onto the seam's error vocabulary. 403 is a
/// rate limit only when Google's reason says so; otherwise the principal
/// lacks access, which no retry fixes.
let classifyStatus (status: HttpStatusCode) (body: string) (retryAfter: TimeSpan option) : BridgeError =
    match int status with
    | 401 -> AuthenticationFailed "Google Calendar refused the access token (401)"
    | 403 when body.Contains "RateLimitExceeded" || body.Contains "rateLimitExceeded" -> RateLimited retryAfter
    | 429 -> RateLimited retryAfter
    | s when s >= 500 -> Unreachable(sprintf "Google Calendar returned %d" s)
    | s -> ExternalRejected(s, body)

// ─── The bridge ─────────────────────────────────────────────────────

type private Reply = {
    Status: HttpStatusCode
    Body: string
    RetryAfter: TimeSpan option
}

/// The Google Calendar `ICalendarBridge`. Construct one per deployment
/// and compose it with `SchedulingServerApp.withCalendarBridge`.
///
/// `tokens` resolves the bearer token per call (see
/// `GoogleCalendarTokenSource`); `secretStore` holds the per-link sync
/// token and channel record and the deployment channel secret. The
/// transport constructor exists so a test can serve canned Calendar v3
/// responses through the same code path; the production one uses the
/// platform's egress-policy-wrapped client.
type GoogleCalendarBridge
    (
        tokens: GoogleCalendarTokenSource,
        secretStore: ISecretStore,
        settings: GoogleCalendarSettings,
        handler: HttpMessageHandler,
        clock: unit -> DateTimeOffset
    ) =

    let client = PlatformHttpClient.createWith EgressSurface.Other handler
    let apiBase = GoogleCalendarSettings.effectiveBase settings

    let calendarUrl (calendarId: ExternalCalendarId) =
        sprintf "%s/calendars/%s" apiBase (Uri.EscapeDataString calendarId)

    let eventsUrl (calendarId: ExternalCalendarId) = calendarUrl calendarId + "/events"

    let eventUrl (calendarId: ExternalCalendarId) (eventId: ExternalEventId) =
        sprintf "%s/%s" (eventsUrl calendarId) (Uri.EscapeDataString eventId)

    let send (build: unit -> HttpRequestMessage) (token: string) = async {
        try
            use request = build ()
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            use! response = client.SendAsync request |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            let retryAfter =
                match response.Headers.RetryAfter with
                | null -> None
                | h when h.Delta.HasValue -> Some h.Delta.Value
                | _ -> None

            return
                Ok {
                    Status = response.StatusCode
                    Body = body
                    RetryAfter = retryAfter
                }
        with
        | :? TaskCanceledException as ex ->
            return Error(Unreachable(sprintf "Google Calendar request timed out: %s" ex.Message))
        | :? HttpRequestException as ex -> return Error(Unreachable ex.Message)
    }

    /// Issue one request with the link's bearer token; on a 401, ask for
    /// a NEW token once and re-issue. That is credential handling, not a
    /// retry: a cached token revoked since it was minted is the one
    /// failure the bridge can fix without an operator.
    let authorized (link: CalendarLinkRef) (build: unit -> HttpRequestMessage) = async {
        let connection = GoogleCalendarSettings.connectionOf settings link

        match! tokens.AccessToken(link.ScopeId, connection) with
        | Error e -> return Error e
        | Ok token ->
            match! send build token with
            | Ok reply when reply.Status = HttpStatusCode.Unauthorized ->
                match! tokens.RefreshedAccessToken(link.ScopeId, connection) with
                | Error e -> return Error e
                | Ok fresh -> return! send build fresh
            | other -> return other
    }

    let jsonRequest (method: HttpMethod) (url: string) (body: JsonNode option) () =
        let request = new HttpRequestMessage(method, url)

        match body with
        | Some node -> request.Content <- new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json")
        | None -> ()

        request

    let isSuccess (reply: Reply) =
        int reply.Status >= 200 && int reply.Status < 300

    let readSecret (scopeId: string) (key: string) = async {
        match! secretStore.GetSecret(scopeId, key) with
        | Some v when v <> "" -> return Some v
        | _ -> return None
    }

    let channelSecret () =
        readSecret settings.ChannelSecretScope ChannelSecretKey

    let stopChannel (link: CalendarLinkRef) (channel: WatchChannel) = async {
        let body = JsonObject()
        body["id"] <- JsonValue.Create channel.ChannelId
        body["resourceId"] <- JsonValue.Create channel.ResourceId

        let! _ = authorized link (jsonRequest HttpMethod.Post (apiBase + "/channels/stop") (Some body))
        return ()
    }

    /// Open a watch channel on the link's calendar, unless the recorded
    /// one still has more than `ChannelRenewalLead` to run.
    let ensureChannel (link: CalendarLinkRef) (address: string) = async {
        let! recorded = async {
            match! readSecret link.ScopeId (channelKey link) with
            | Some json -> return tryChannelOfJson json
            | None -> return None
        }

        let current =
            recorded
            |> Option.filter (fun c ->
                c.CalendarId = link.ExternalCalendarId
                && c.ExpiresAtUtc > clock () + settings.ChannelRenewalLead)

        match current with
        | Some _ -> return Ok()
        | None ->
            match! channelSecret () with
            | None ->
                return
                    Error(
                        AuthenticationFailed(
                            sprintf
                                "watch channels are configured but no '%s' secret exists in scope '%s'"
                                ChannelSecretKey
                                settings.ChannelSecretScope
                        )
                    )
            | Some secret ->
                let channelId = "toolup-" + Guid.NewGuid().ToString("N")
                let body = JsonObject()
                body["id"] <- JsonValue.Create channelId
                body["type"] <- JsonValue.Create "web_hook"
                body["address"] <- JsonValue.Create address
                body["token"] <- JsonValue.Create(channelToken secret link.ScopeId link.ExternalCalendarId)
                let parameters = JsonObject()

                parameters["ttl"] <- JsonValue.Create(string (int64 settings.ChannelTtl.TotalSeconds))

                body["params"] <- parameters

                let url = eventsUrl link.ExternalCalendarId + "/watch"

                match! authorized link (jsonRequest HttpMethod.Post url (Some body)) with
                | Error e -> return Error e
                | Ok reply when int reply.Status = 404 -> return Error(CalendarNotFound link.ExternalCalendarId)
                | Ok reply when not (isSuccess reply) ->
                    return Error(classifyStatus reply.Status reply.Body reply.RetryAfter)
                | Ok reply ->
                    try
                        use doc = JsonDocument.Parse reply.Body
                        let root = doc.RootElement

                        let expires =
                            match tryStr "expiration" root with
                            | Some ms ->
                                match Int64.TryParse ms with
                                | true, v -> DateTimeOffset.FromUnixTimeMilliseconds v
                                | _ -> clock () + settings.ChannelTtl
                            | None -> clock () + settings.ChannelTtl

                        match tryStr "resourceId" root with
                        | None -> return Error(MalformedPayload "Google watch response carried no resourceId")
                        | Some resourceId ->
                            let channel = {
                                ChannelId = channelId
                                ResourceId = resourceId
                                CalendarId = link.ExternalCalendarId
                                ExpiresAtUtc = expires
                            }

                            match! secretStore.SetSecret(link.ScopeId, channelKey link, channelToJson channel) with
                            | Error e ->
                                // A channel nobody recorded cannot be
                                // stopped or renewed — close it again.
                                do! stopChannel link channel
                                return Error(Unreachable(sprintf "could not record the watch channel: %s" e))
                            | Ok() ->
                                // The superseded channel overlaps the new
                                // one briefly; stop it rather than let it
                                // deliver duplicates until it lapses.
                                match recorded with
                                | Some old -> do! stopChannel link old
                                | None -> ()

                                return Ok()
                    with :? JsonException ->
                        return Error(MalformedPayload "Google watch response was not JSON")
    }

    let fetchEvent (link: CalendarLinkRef) (eventId: ExternalEventId) = async {
        match! authorized link (jsonRequest HttpMethod.Get (eventUrl link.ExternalCalendarId eventId) None) with
        | Error e -> return Error e
        | Ok reply when int reply.Status = 404 || int reply.Status = 410 -> return Ok None
        | Ok reply when not (isSuccess reply) -> return Error(classifyStatus reply.Status reply.Body reply.RetryAfter)
        | Ok reply ->
            let parsed =
                try
                    match JsonNode.Parse reply.Body with
                    | :? JsonObject as o -> Some o
                    | _ -> None
                with :? JsonException ->
                    None

            match parsed with
            | Some o -> return Ok(Some o)
            | None -> return Error(MalformedPayload(sprintf "Google event %s was not a JSON object" eventId))
    }

    /// Read-merge-write one existing event. `None` from the fetch means
    /// Google no longer has it.
    let updateEvent (link: CalendarLinkRef) (booking: Booking) (eventId: ExternalEventId) = async {
        match! fetchEvent link eventId with
        | Error e -> return Error e
        | Ok None -> return Error(EventNotFound eventId)
        | Ok(Some existing) ->
            let merged = toGoogleEvent booking (Some existing)
            let url = eventUrl link.ExternalCalendarId eventId + "?sendUpdates=none"

            match! authorized link (jsonRequest HttpMethod.Put url (Some merged)) with
            | Error e -> return Error e
            | Ok reply when int reply.Status = 404 || int reply.Status = 410 -> return Error(EventNotFound eventId)
            | Ok reply when not (isSuccess reply) ->
                return Error(classifyStatus reply.Status reply.Body reply.RetryAfter)
            | Ok _ -> return Ok eventId
    }

    let pullPages (link: CalendarLinkRef) (query: (string * string) list) (defaults: Booking) = async {
        let events = ResizeArray<ExternalEvent>()
        let mutable pageToken: string option = None
        let mutable nextSyncToken: string option = None
        let mutable failure: BridgeError option = None
        let mutable gone = false
        let mutable finished = false

        while not finished do
            let parameters =
                query
                @ [ "maxResults", "250" ]
                @ (match pageToken with
                   | Some t -> [ "pageToken", t ]
                   | None -> [])

            let url =
                eventsUrl link.ExternalCalendarId
                + "?"
                + (parameters
                   |> List.map (fun (k, v) -> sprintf "%s=%s" k (Uri.EscapeDataString v))
                   |> String.concat "&")

            match! authorized link (jsonRequest HttpMethod.Get url None) with
            | Error e ->
                failure <- Some e
                finished <- true
            | Ok reply when int reply.Status = 410 ->
                gone <- true
                finished <- true
            | Ok reply when int reply.Status = 404 ->
                failure <- Some(CalendarNotFound link.ExternalCalendarId)
                finished <- true
            | Ok reply when not (isSuccess reply) ->
                failure <- Some(classifyStatus reply.Status reply.Body reply.RetryAfter)
                finished <- true
            | Ok reply ->
                try
                    use doc = JsonDocument.Parse reply.Body
                    let root = doc.RootElement

                    match tryProp "items" root with
                    | Some items when items.ValueKind = JsonValueKind.Array ->
                        for item in items.EnumerateArray() do
                            if failure.IsNone then
                                match ofGoogleEvent defaults item with
                                | Error e -> failure <- Some(MalformedPayload(sprintf "Google event unreadable: %s" e))
                                | Ok(Ok e) -> events.Add e
                                | Ok(Error _skipped) -> ()
                    | _ -> ()

                    pageToken <- tryStr "nextPageToken" root

                    if pageToken.IsNone then
                        nextSyncToken <- tryStr "nextSyncToken" root
                        finished <- true
                with :? JsonException ->
                    failure <- Some(MalformedPayload "Google events list was not JSON")
                    finished <- true

            if failure.IsSome then
                finished <- true

        return
            match failure with
            | Some e -> Error e
            | None when gone -> Ok None
            | None -> Ok(Some(List.ofSeq events, nextSyncToken))
    }

    let header (headers: Map<string, string>) (name: string) =
        headers
        |> Map.toSeq
        |> Seq.tryFind (fun (k, _) -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map snd

    /// The production constructor: the wall clock and the platform's
    /// egress-policy-wrapped transport. Explicit rather than optional
    /// arguments, so the narrow shape stays its own approval token.
    new(tokens: GoogleCalendarTokenSource, secretStore: ISecretStore, settings: GoogleCalendarSettings) =
        GoogleCalendarBridge(tokens, secretStore, settings, new HttpClientHandler(), (fun () -> DateTimeOffset.UtcNow))

    /// The settings this bridge was constructed with.
    member _.Settings: GoogleCalendarSettings = settings

    /// The watch channel recorded for `link`, if any — the renewal job
    /// and diagnostics read it; the bridge itself reads it per call.
    member _.RecordedChannel(link: CalendarLinkRef) : Async<WatchChannel option> = async {
        match! readSecret link.ScopeId (channelKey link) with
        | Some json -> return tryChannelOfJson json
        | None -> return None
    }

    interface ICalendarBridge with

        member _.Kind = KindName

        member _.Capabilities = {
            SupportsWebhooks = settings.WebhookAddress.IsSome
            SupportsIncrementalPull = true
            // Calendar v3's per-user quota tolerates far more than this;
            // five minutes keeps a polling fallback cheap.
            MinimumPollInterval = TimeSpan.FromMinutes 5.0
        }

        member _.LinkResource(link) = async {
            // `calendarList.get`, not `calendars.get`: the latter is not
            // authorised under `calendar.events`, and the former is covered by
            // the `calendar.calendarlist.readonly` scope the flow already
            // requests for the health probe. Both answer 404 for a calendar
            // the user cannot reach. (Found by the live probe, 2026-09-23.)
            let url =
                sprintf "%s/users/me/calendarList/%s" apiBase (Uri.EscapeDataString link.ExternalCalendarId)

            match! authorized link (jsonRequest HttpMethod.Get url None) with
            | Error e -> return Error e
            | Ok reply when int reply.Status = 404 -> return Error(CalendarNotFound link.ExternalCalendarId)
            | Ok reply when not (isSuccess reply) ->
                return Error(classifyStatus reply.Status reply.Body reply.RetryAfter)
            | Ok _ ->
                match settings.WebhookAddress with
                | None -> return Ok()
                | Some address -> return! ensureChannel link address
        }

        member _.UnlinkResource(link) = async {
            // Best effort at Google: a channel that cannot be stopped
            // lapses at its expiration, and a notification arriving for
            // it finds no link and changes nothing. The local record and
            // the sync token go regardless, so a re-link starts clean.
            match! readSecret link.ScopeId (channelKey link) with
            | Some json ->
                match tryChannelOfJson json with
                | Some channel -> do! stopChannel link channel
                | None -> ()

                let! _ = secretStore.DeleteSecret(link.ScopeId, channelKey link)
                ()
            | None -> ()

            match! readSecret link.ScopeId (syncTokenKey link) with
            | Some _ ->
                let! _ = secretStore.DeleteSecret(link.ScopeId, syncTokenKey link)
                ()
            | None -> ()

            return Ok()
        }

        member _.Push(link, booking, existing) = async {
            let eventId =
                match existing with
                | Some id when id.Length > 0 -> id
                | _ -> eventIdOf booking.Id

            if booking.Status = Cancelled then
                let url = eventUrl link.ExternalCalendarId eventId + "?sendUpdates=none"

                match! authorized link (jsonRequest HttpMethod.Delete url None) with
                | Error e -> return Error e
                // Already gone is the state a cancel wants.
                | Ok reply when isSuccess reply || int reply.Status = 404 || int reply.Status = 410 -> return Ok eventId
                | Ok reply -> return Error(classifyStatus reply.Status reply.Body reply.RetryAfter)
            else
                match existing with
                | Some id when id.Length > 0 -> return! updateEvent link booking id
                | _ ->
                    let created = toGoogleEvent booking None
                    created["id"] <- JsonValue.Create eventId
                    let url = eventsUrl link.ExternalCalendarId + "?sendUpdates=none"

                    match! authorized link (jsonRequest HttpMethod.Post url (Some created)) with
                    | Error e -> return Error e
                    | Ok reply when isSuccess reply -> return Ok eventId
                    // The deterministic id already exists: this is the
                    // re-push idempotency promises, so update it instead
                    // (which also restores an event deleted in Google).
                    | Ok reply when int reply.Status = 409 ->
                        match! updateEvent link booking eventId with
                        | Error(EventNotFound _) -> return Error(ExternalRejected(409, reply.Body))
                        | other -> return other
                    | Ok reply when int reply.Status = 404 -> return Error(CalendarNotFound link.ExternalCalendarId)
                    | Ok reply -> return Error(classifyStatus reply.Status reply.Body reply.RetryAfter)
        }

        member _.Pull(link, since, defaults) = async {
            let now = clock ()

            let windowed () =
                pullPages
                    link
                    [
                        "timeMin", rfc3339 (now.AddDays(float -settings.PullLookbackDays))
                        "timeMax", rfc3339 (now.AddDays(float settings.PullWindowDays))
                        "showDeleted", "false"
                    ]
                    defaults

            let byUpdatedMin (t: DateTimeOffset) =
                pullPages link [ "updatedMin", rfc3339 t; "showDeleted", "true" ] defaults

            let! result = async {
                match since with
                | None -> return! windowed ()
                | Some t ->
                    match! readSecret link.ScopeId (syncTokenKey link) with
                    | Some syncToken ->
                        match! pullPages link [ "syncToken", syncToken ] defaults with
                        | Ok None ->
                            // 410: the token expired. Drop it and fall
                            // back to the modification bound.
                            let! _ = secretStore.DeleteSecret(link.ScopeId, syncTokenKey link)
                            return! byUpdatedMin t
                        | other -> return other
                    | None -> return! byUpdatedMin t
            }

            match result with
            | Error e -> return Error e
            | Ok None -> return Error(ExternalRejected(410, "Google refused the pull as expired"))
            | Ok(Some(events, nextSyncToken)) ->
                match nextSyncToken with
                | Some token ->
                    let! _ = secretStore.SetSecret(link.ScopeId, syncTokenKey link, token)
                    ()
                | None -> ()

                return Ok events
        }

        member _.HandleWebhook(headers, _body) = async {
            if settings.WebhookAddress.IsNone then
                // Polling-only as configured: the honest answer is that
                // nothing we asked for changed.
                return Ok []
            else
                match
                    header headers "X-Goog-Channel-Token",
                    header headers "X-Goog-Resource-State",
                    header headers "X-Goog-Resource-URI"
                with
                | Some token, Some state, Some resourceUri ->
                    match tryCalendarIdOfResourceUri resourceUri with
                    | None -> return Error(MalformedPayload "X-Goog-Resource-URI names no calendar")
                    | Some calendarId ->
                        match! channelSecret () with
                        | None ->
                            return
                                Error(
                                    AuthenticationFailed(
                                        sprintf
                                            "cannot verify a channel notification: no '%s' secret in scope '%s'"
                                            ChannelSecretKey
                                            settings.ChannelSecretScope
                                    )
                                )
                        | Some secret ->
                            if not (verifyChannelToken secret calendarId token) then
                                return Error(MalformedPayload "the channel token did not verify")
                            else
                                match state with
                                // `sync` is the handshake Google sends when a
                                // channel opens — nothing has changed yet.
                                | "sync" -> return Ok []
                                | "exists"
                                | "not_exists" ->
                                    return
                                        Ok [
                                            {
                                                ExternalCalendarId = calendarId
                                                ChangedEventId = None
                                            }
                                        ]
                                | _ -> return Ok []
                | _ -> return Error(MalformedPayload "not a Google Calendar channel notification")
        }