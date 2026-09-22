module ToolUp.Calendar.CalDAV

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks
open System.Xml.Linq
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Scheduling
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.ICalendarBridge

// ─── Phase 20a — the generic CalDAV calendar bridge ─────────────────
//
// The first `ICalendarBridge`: RFC 4791 over BCL `HttpClient`, against
// any conforming CalDAV server (Fastmail, Nextcloud, iCloud, Apple
// Calendar Server, Radicale, Baikal). No vendor SDK, no paid dependency
// and no OAuth application registration — which is what makes it the
// bridge that can ship with the seam (GP 1 / GP 2).
//
// **Four verbs, and no more**:
//
//   * `PROPFIND` `Depth: 0` on the collection — does it exist, and can
//     we see it? Used by `LinkResource` and by the health probe.
//   * `REPORT` `calendar-query` with a `time-range` filter — the pull.
//     The response is a `multistatus` carrying one `calendar-data`
//     (an iCalendar `VCALENDAR`) per matching event.
//   * `PUT` an `.ics` at `<collection>/<uid>.ics` — the push. The UID
//     is the booking id, which makes the push idempotent by
//     construction: the second push of a booking overwrites the first
//     rather than creating a twin.
//   * `DELETE` the same href — a cancelled booking.
//
// **Authentication** is HTTP Basic with the configured username and a
// password (conventionally a server-issued app-password) resolved from
// `ISecretStore` PER CALL, so a rotated credential is picked up without
// a restart and the bridge never caches one. `CalDAVSettings` therefore
// carries no secret and is safe to log.
//
// **Polling only.** CalDAV's push story (`WebDAV-Sync`, RFC 6578) is a
// server-side sync token, not a notification the server sends us, so
// `Capabilities.SupportsWebhooks` is `false` and `HandleWebhook` is an
// honest no-op. `SupportsIncrementalPull` is `false` too: a
// `calendar-query` `time-range` filters on event TIME, not modification
// time, so the sync engine passes no cursor and reconciles by value.
//
// **Distributed-ready.** Stateless between calls, no in-memory session,
// no affinity: several instances may run the same bridge concurrently.

/// Connection settings for the CalDAV bridge. Carries no credential —
/// the password comes from `ISecretStore` under `CalDAVSettings.SecretKey`.
type CalDAVSettings = {
    /// Base URL of the CalDAV server, e.g.
    /// `https://caldav.fastmail.com/`. A relative
    /// `CalendarLinkRef.ExternalCalendarId` resolves against it, so a
    /// deployment can store `/dav/calendars/user/alice/work/` as the
    /// link's calendar id and move servers by changing this one value.
    BaseUrl: string
    /// The username the bridge authenticates as.
    Username: string
    /// When set, replaces `BaseUrl` for every request — the convention
    /// the other HTTP companions use for a self-hosted endpoint or a
    /// test double standing in for the provider.
    EndpointOverride: string option
    /// How far forward a pull looks when the caller supplies no
    /// `since`. A `calendar-query` with no `time-range` returns the
    /// whole calendar, which on a long-lived account is unbounded; this
    /// is the bound. Default 90 days.
    PullWindowDays: int
    /// How far BACK a pull looks from the reference instant. Default
    /// one day, so an event that started this morning is still
    /// reconciled.
    PullLookbackDays: int
}

module CalDAVSettings =
    /// `ISecretStore` key the password / app-password is read from, in
    /// the link's own scope. Not a `TOOLUP_*` variable: it is a secret
    /// name, and `EnvironmentSecretStore` maps it to the scoped
    /// environment spelling itself.
    [<Literal>]
    let SecretKey = "CALDAV_PASSWORD"

    /// Settings with no server configured — the shape a deployment
    /// overrides field by field.
    let defaults: CalDAVSettings = {
        BaseUrl = ""
        Username = ""
        EndpointOverride = None
        PullWindowDays = 90
        PullLookbackDays = 1
    }

    /// Read settings from the environment:
    ///   TOOLUP_CALDAV_URL      — required, the server base URL
    ///   TOOLUP_CALDAV_USERNAME — required
    ///   TOOLUP_CALDAV_ENDPOINT — optional override
    /// The window knobs are not environment-configurable: they bound a
    /// request rather than address a server, and a deployment that needs
    /// to change them is already constructing the record.
    let fromEnv () : CalDAVSettings =
        let read name =
            match Environment.GetEnvironmentVariable(name: string) with
            | null
            | "" -> None
            | v -> Some v

        let readRequired name =
            match read name with
            | Some v -> v
            | None -> failwithf "CalDAV calendar bridge: env var %s is required" name

        {
            defaults with
                BaseUrl = readRequired ConfigKeys.Names.calDavUrl
                Username = readRequired ConfigKeys.Names.calDavUsername
                EndpointOverride = read ConfigKeys.Names.calDavEndpoint
        }

    /// The base every request is issued against — the override when one
    /// is set, the configured base otherwise.
    let effectiveBase (settings: CalDAVSettings) : string =
        settings.EndpointOverride |> Option.defaultValue settings.BaseUrl

// ─── XML namespaces + request bodies ────────────────────────────────

let private davNs = XNamespace.Get "DAV:"
let private calNs = XNamespace.Get "urn:ietf:params:xml:ns:caldav"

/// `PROPFIND` body asking only for the resource type — the cheapest
/// "does this collection exist and can I see it" probe there is.
let private propfindBody =
    """<?xml version="1.0" encoding="utf-8" ?><D:propfind xmlns:D="DAV:"><D:prop><D:resourcetype/><D:displayname/></D:prop></D:propfind>"""

let private icalInstant (value: DateTimeOffset) : string =
    value.UtcDateTime.ToString "yyyyMMddTHHmmssZ"

/// `REPORT calendar-query` body: every `VEVENT` overlapping the window,
/// with its etag, its last-modified stamp (servers that publish one) and
/// its iCalendar payload.
let private calendarQueryBody (rangeStart: DateTimeOffset) (rangeEnd: DateTimeOffset) : string =
    sprintf
        """<?xml version="1.0" encoding="utf-8" ?><C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:prop><D:getetag/><D:getlastmodified/><C:calendar-data/></D:prop><C:filter><C:comp-filter name="VCALENDAR"><C:comp-filter name="VEVENT"><C:time-range start="%s" end="%s"/></C:comp-filter></C:comp-filter></C:filter></C:calendar-query>"""
        (icalInstant rangeStart)
        (icalInstant rangeEnd)

// ─── URL composition ────────────────────────────────────────────────

/// Resolve a possibly-relative external calendar id against the
/// effective base. An absolute id is used verbatim, so a deployment can
/// mix servers across links if it has to.
let resolveCollectionUrl (settings: CalDAVSettings) (calendarId: ExternalCalendarId) : string =
    let baseUrl = CalDAVSettings.effectiveBase settings

    if
        calendarId.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || calendarId.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
    then
        calendarId
    else
        let trimmedBase = baseUrl.TrimEnd '/'
        let trimmedId = calendarId.TrimStart '/'
        sprintf "%s/%s" trimmedBase trimmedId

/// The href one booking occupies inside a collection. The UID is the
/// booking id, which is what makes `PUT` idempotent.
let eventHref (collectionUrl: string) (bookingId: BookingId) : string =
    sprintf "%s/%s.ics" (collectionUrl.TrimEnd '/') (Uri.EscapeDataString bookingId)

// ─── Response classification ────────────────────────────────────────

/// Map a transport status onto the seam's closed error vocabulary. The
/// classification is the bridge's whole contribution to retry policy
/// (portability rule 3): it never retries, it says whether retrying
/// could work.
let classifyStatus (status: HttpStatusCode) (body: string) : BridgeError =
    match int status with
    | 401
    | 403 -> AuthenticationFailed(sprintf "CalDAV returned %d" (int status))
    | 404 -> ExternalRejected(404, "CalDAV resource not found")
    | 429 -> RateLimited None
    | s when s >= 500 -> Unreachable(sprintf "CalDAV returned %d: %s" s body)
    | s -> ExternalRejected(s, body)

// ─── The bridge ─────────────────────────────────────────────────────

/// Generic CalDAV `ICalendarBridge`. Construct one per deployment and
/// compose it with `SchedulingServerApp.withCalendarBridge`.
///
/// `handler` is the transport. The parameterless-transport constructor
/// builds the platform's own egress-policy-wrapped client, which is
/// what a deployment wants; the explicit one exists so a test can serve
/// canned CalDAV responses through the same code path.
type CalDAVCalendarBridge(secretStore: ISecretStore, settings: CalDAVSettings, handler: HttpMessageHandler) =

    /// The bridge's `ICalendarBridge.Kind`. Persisted on every link, so
    /// it is a wire value.
    [<Literal>]
    static let KindName = "CalDAV"

    let client = PlatformHttpClient.createWith EgressSurface.Other handler

    let authHeader (password: string) =
        let raw = sprintf "%s:%s" settings.Username password
        AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes raw))

    /// Resolve the credential for the link's own scope. Read per call —
    /// never cached — so a rotated app-password takes effect at once.
    let withCredential (link: CalendarLinkRef) (act: AuthenticationHeaderValue -> Async<Result<'T, BridgeError>>) = async {
        match! secretStore.GetSecret(link.ScopeId, CalDAVSettings.SecretKey) with
        | None
        | Some "" ->
            return
                Error(
                    AuthenticationFailed(
                        sprintf
                            "no '%s' secret in scope '%s' — the CalDAV bridge reads its password from ISecretStore per call"
                            CalDAVSettings.SecretKey
                            link.ScopeId
                    )
                )
        | Some password -> return! act (authHeader password)
    }

    let send (request: HttpRequestMessage) = async {
        try
            let! response = client.SendAsync request |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return Ok(response.StatusCode, body)
        with
        | :? TaskCanceledException as ex -> return Error(Unreachable(sprintf "CalDAV request timed out: %s" ex.Message))
        | :? HttpRequestException as ex -> return Error(Unreachable ex.Message)
    }

    let request
        (method: string)
        (url: string)
        (auth: AuthenticationHeaderValue)
        (depth: string option)
        (body: string option)
        =
        let message = new HttpRequestMessage(HttpMethod method, url)
        message.Headers.Authorization <- auth

        match depth with
        | Some d -> message.Headers.TryAddWithoutValidation("Depth", d) |> ignore
        | None -> ()

        match body with
        | Some payload -> message.Content <- new StringContent(payload, Encoding.UTF8, "application/xml")
        | None -> ()

        message

    /// Parse a `multistatus` into `(href, etag, lastModified, calendarData)`
    /// rows, keeping only the responses that carried calendar data.
    let parseMultistatus
        (xml: string)
        : Result<(string * string option * DateTimeOffset option * string) list, BridgeError> =
        try
            let doc = XDocument.Parse xml

            let rows =
                doc.Descendants(davNs + "response")
                |> Seq.choose (fun response ->
                    let href =
                        response.Element(davNs + "href")
                        |> Option.ofObj
                        |> Option.map _.Value
                        |> Option.defaultValue ""

                    let calendarData =
                        response.Descendants(calNs + "calendar-data")
                        |> Seq.tryHead
                        |> Option.map _.Value

                    let etag =
                        response.Descendants(davNs + "getetag") |> Seq.tryHead |> Option.map _.Value

                    let lastModified =
                        response.Descendants(davNs + "getlastmodified")
                        |> Seq.tryHead
                        |> Option.bind (fun e ->
                            match DateTimeOffset.TryParse e.Value with
                            | true, parsed -> Some(parsed.ToUniversalTime())
                            | _ -> None)

                    match calendarData with
                    | Some data when not (String.IsNullOrWhiteSpace data) -> Some(href, etag, lastModified, data)
                    | _ -> None)
                |> List.ofSeq

            Ok rows
        with ex ->
            Error(MalformedPayload(sprintf "CalDAV multistatus could not be parsed: %s" ex.Message))

    /// The production constructor: the platform's egress-policy-wrapped
    /// transport. Explicit rather than an optional argument, because an
    /// optional constructor argument folds both shapes into one widened
    /// constructor and the approval gate reads the narrow one as removed.
    new(secretStore: ISecretStore, settings: CalDAVSettings) =
        CalDAVCalendarBridge(secretStore, settings, new HttpClientHandler())

    /// The collection URL a link resolves to — exposed so a health
    /// probe and a diagnostic can name the same URL the bridge uses.
    member _.CollectionUrl(calendarId: ExternalCalendarId) : string =
        resolveCollectionUrl settings calendarId

    /// The settings this bridge was constructed with.
    member _.Settings: CalDAVSettings = settings

    interface ICalendarBridge with

        member _.Kind = KindName

        member _.Capabilities = BridgeCapabilities.pollingOnly

        member _.LinkResource(link) =
            withCredential link (fun auth -> async {
                let url = resolveCollectionUrl settings link.ExternalCalendarId
                use message = request "PROPFIND" url auth (Some "0") (Some propfindBody)

                match! send message with
                | Error e -> return Error e
                | Ok(status, body) ->
                    if int status = 404 then
                        return Error(CalendarNotFound link.ExternalCalendarId)
                    elif int status >= 200 && int status < 300 then
                        return Ok()
                    else
                        return Error(classifyStatus status body)
            })

        member _.UnlinkResource(_link) = async {
            // CalDAV has no subscription to cancel: the mirror is
            // whatever we pushed, and the sync engine drops the stored
            // link. Idempotent by construction.
            return Ok()
        }

        member _.Push(link, booking, existing) =
            withCredential link (fun auth -> async {
                let collection = resolveCollectionUrl settings link.ExternalCalendarId

                let href =
                    match existing with
                    | Some id when id.Length > 0 -> resolveCollectionUrl settings id
                    | _ -> eventHref collection booking.Id

                if booking.Status = Cancelled then
                    use message = request "DELETE" href auth None None

                    match! send message with
                    | Error e -> return Error e
                    | Ok(status, body) ->
                        // A cancelled booking whose event is already gone
                        // is the state we wanted; 404 is success, not a
                        // failure to be retried.
                        if (int status >= 200 && int status < 300) || int status = 404 then
                            return Ok(existing |> Option.defaultValue href)
                        else
                            return Error(classifyStatus status body)
                else
                    let calendar: iCalendar.VCalendar = {
                        Version = "2.0"
                        ProdId = iCalendar.CanonicalProdId
                        Events = [ iCalendar.bookingToVEvent booking ]
                    }

                    use message = new HttpRequestMessage(HttpMethod "PUT", href)
                    message.Headers.Authorization <- auth

                    message.Content <- new StringContent(iCalendar.emit calendar, Encoding.UTF8, "text/calendar")

                    match! send message with
                    | Error e -> return Error e
                    | Ok(status, body) ->
                        if int status >= 200 && int status < 300 then
                            return Ok href
                        elif int status = 404 then
                            // The collection is gone, or the event we were
                            // told to update is. The caller distinguishes:
                            // with no `existing` there was nothing to miss.
                            match existing with
                            | Some id -> return Error(EventNotFound id)
                            | None -> return Error(CalendarNotFound link.ExternalCalendarId)
                        else
                            return Error(classifyStatus status body)
            })

        member _.Pull(link, since, defaults) =
            withCredential link (fun auth -> async {
                let url = resolveCollectionUrl settings link.ExternalCalendarId
                let now = DateTimeOffset.UtcNow

                // `since` bounds event TIME here, not modification time —
                // which is exactly what `SupportsIncrementalPull = false`
                // declares, and why the sync engine does not hand this
                // bridge a cursor.
                let rangeStart =
                    since |> Option.defaultValue (now.AddDays(float -settings.PullLookbackDays))

                let rangeEnd = now.AddDays(float settings.PullWindowDays)

                use message =
                    request "REPORT" url auth (Some "1") (Some(calendarQueryBody rangeStart rangeEnd))

                match! send message with
                | Error e -> return Error e
                | Ok(status, body) ->
                    if int status = 404 then
                        return Error(CalendarNotFound link.ExternalCalendarId)
                    elif int status < 200 || int status >= 300 then
                        return Error(classifyStatus status body)
                    else
                        match parseMultistatus body with
                        | Error e -> return Error e
                        | Ok rows ->
                            let events = ResizeArray<ExternalEvent>()
                            let mutable failure = None

                            for href, _etag, lastModified, data in rows do
                                if failure.IsNone then
                                    match iCalendar.parse data with
                                    | Error e ->
                                        failure <-
                                            Some(
                                                MalformedPayload(
                                                    sprintf "CalDAV event at %s is not parseable iCalendar: %s" href e
                                                )
                                            )
                                    | Ok calendar ->
                                        for vevent in calendar.Events do
                                            events.Add {
                                                ExternalEventId = href
                                                Booking = iCalendar.vEventToBooking defaults vevent
                                                LastModifiedUtc = lastModified
                                            }

                            match failure with
                            | Some e -> return Error e
                            | None -> return Ok(List.ofSeq events)
            })

        member _.HandleWebhook(_headers, _body) = async {
            // Polling-only: CalDAV servers do not call us. The honest
            // answer to a notification we cannot have received is that
            // nothing changed.
            return Ok []
        }