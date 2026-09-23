module ToolUp.Scheduling.Tests.InProcess.GoogleCalendarBridgeTests

open System
open System.Collections.Generic
open System.Globalization
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.Platform.ProviderOAuthFlow
open ToolUp.Scheduling
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.BookingScheduler
open ToolUp.Scheduling.CalendarSync
open ToolUp.Calendar.GoogleCalendarOAuth
open ToolUp.Calendar.GoogleCalendar
open ToolUp.Calendar.GoogleCalendarHealth
open ToolUp.Calendar.GoogleCalendarChannels
open ToolUp.Scheduling.Tests.Contracts
open ToolUp.Scheduling.Tests.InProcess.InMemoryEntityStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryEventStore

// ─── Binding 3 of the ICalendarBridge contract pack (Phase 830) ─────
//
// The REAL `GoogleCalendarBridge`, over a stub `HttpMessageHandler`
// standing in for Calendar v3: calendars.get, events.list (time window,
// updatedMin, sync tokens, paging, showDeleted), events.insert / get /
// update / delete, events.watch and channels.stop — answered the way the
// v3 reference says Google answers them, from a dictionary. The OAuth
// token endpoint is the substrate's own `OAuthTokenPost` seam, stubbed.
// Every line of the bridge is exercised; only the socket is not.
//
// The contract pack is bound twice: once with watch channels configured
// (a webhook-capable bridge) and once without (polling-only), because the
// capability changes which laws apply. A live case rides the same bridge
// against the real API, gated on `TOOLUP_GOOGLE_CALENDAR_TEST_*` so a
// fresh checkout is green with no Google account.

[<Literal>]
let private ApiBase = "https://calendar.invalid/calendar/v3"

[<Literal>]
let private CalendarId = "team-cal@group.calendar.google.com"

[<Literal>]
let private Connection = "alice"

[<Literal>]
let private ChannelSecret = "channel-secret-value"

[<Literal>]
let private WebhookAddress = "https://app.invalid/calendar/google/notifications"

let private rfc3339 (value: DateTimeOffset) =
    value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

let private parseInstant (value: string) =
    DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)

// ─── The stub Calendar v3 server ────────────────────────────────────

/// A minimal in-memory Calendar v3. One calendar, events keyed by id,
/// a change counter that doubles as the sync token.
type StubGoogleCalendar(clock: unit -> DateTimeOffset) =
    let events = Dictionary<string, JsonObject>()
    let order = ResizeArray<string>()
    let changedAt = Dictionary<string, int>()
    let mutable seq = 0

    member val ValidTokens = HashSet<string>() with get
    member val Requests = ResizeArray<string * string>() with get
    member val Watches = ResizeArray<JsonObject>() with get
    member val Stops = ResizeArray<string>() with get
    member val FailWatch = false with get, set
    member val ExpireSyncTokens = false with get, set
    member val PageSize = 2 with get, set
    member _.Clock = clock
    member _.Seq = seq

    member _.Touch(id: string) =
        seq <- seq + 1
        changedAt[id] <- seq

    member this.Store(id: string, event: JsonObject) =
        if not (events.ContainsKey id) then
            order.Add id

        events[id] <- event
        this.Touch id

    member _.TryGet(id: string) =
        match events.TryGetValue id with
        | true, e -> Some e
        | _ -> None

    member _.Ids = List.ofSeq order

    member _.ChangedSince(n: int) =
        order |> Seq.filter (fun id -> changedAt[id] > n) |> List.ofSeq

let private respond (status: HttpStatusCode) (body: string) =
    let response = new HttpResponseMessage(status)
    response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    response

let private queryOf (uri: Uri) =
    uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun kv ->
        match kv.Split('=', 2) with
        | [| k; v |] -> Uri.UnescapeDataString k, Uri.UnescapeDataString v
        | other -> Uri.UnescapeDataString other[0], "")
    |> Map.ofArray

let private str (o: JsonObject) (name: string) =
    match o[name] with
    | null -> None
    | node -> Some(node.GetValue<string>())

let private instantOf (o: JsonObject) (name: string) =
    match o[name] with
    | :? JsonObject as i ->
        match str i "dateTime" with
        | Some v -> Some(parseInstant v)
        | None -> None
    | _ -> None

/// Answers Calendar v3 requests out of a `StubGoogleCalendar`.
type StubGoogleHandler(server: StubGoogleCalendar) =
    inherit HttpMessageHandler()

    override _.SendAsync(request, _ct) = task {
        let uri = request.RequestUri

        let! body =
            match request.Content with
            | null -> System.Threading.Tasks.Task.FromResult ""
            | c -> c.ReadAsStringAsync()

        server.Requests.Add(request.Method.Method, uri.PathAndQuery)

        let authorised =
            match request.Headers.Authorization with
            | null -> false
            | h -> h.Scheme = "Bearer" && server.ValidTokens.Contains h.Parameter

        if not authorised then
            return respond HttpStatusCode.Unauthorized """{"error":{"code":401}}"""
        else
            let path = uri.AbsolutePath

            let segments =
                path.Substring("/calendar/v3".Length).Trim('/').Split('/')
                |> Array.map Uri.UnescapeDataString
                |> List.ofArray

            let query = queryOf uri
            let method = request.Method.Method
            let now = server.Clock()

            match method, segments with
            | "GET", [ "users"; "me"; "calendarList" ] -> return respond HttpStatusCode.OK """{"items":[]}"""
            | "POST", [ "channels"; "stop" ] ->
                let o = JsonNode.Parse(body).AsObject()
                server.Stops.Add(o["id"].GetValue<string>())
                return respond HttpStatusCode.NoContent ""
            | _, "calendars" :: calendar :: _ when calendar <> CalendarId ->
                return respond HttpStatusCode.NotFound """{"error":{"code":404}}"""
            | "GET", [ "calendars"; _ ] -> return respond HttpStatusCode.OK (sprintf """{"id":"%s"}""" CalendarId)
            | "POST", [ "calendars"; _; "events"; "watch" ] ->
                if server.FailWatch then
                    return respond HttpStatusCode.ServiceUnavailable """{"error":{"code":503}}"""
                else
                    let o = JsonNode.Parse(body).AsObject()
                    server.Watches.Add o

                    let ttl = o["params"].AsObject().Item("ttl").GetValue<string>() |> Int64.Parse

                    let expiration = now.AddSeconds(float ttl).ToUnixTimeMilliseconds()

                    return
                        respond
                            HttpStatusCode.OK
                            (sprintf
                                """{"kind":"api#channel","id":"%s","resourceId":"res-%d","expiration":"%d"}"""
                                (o["id"].GetValue<string>())
                                server.Watches.Count
                                expiration)
            | "GET", [ "calendars"; _; "events" ] ->
                if query.ContainsKey "syncToken" && server.ExpireSyncTokens then
                    return respond HttpStatusCode.Gone """{"error":{"code":410}}"""
                else
                    let candidates =
                        match Map.tryFind "syncToken" query with
                        | Some token -> server.ChangedSince(int (token.Substring 3))
                        | None -> server.Ids

                    let showDeleted =
                        query.ContainsKey "syncToken" || Map.tryFind "showDeleted" query = Some "true"

                    let keep (id: string) =
                        let e = (server.TryGet id).Value
                        let cancelled = str e "status" = Some "cancelled"

                        let updatedOk =
                            match Map.tryFind "updatedMin" query, str e "updated" with
                            | Some min, Some updated -> parseInstant updated >= parseInstant min
                            | _ -> true

                        let windowOk =
                            match Map.tryFind "timeMin" query, Map.tryFind "timeMax" query with
                            | Some tmin, Some tmax ->
                                match instantOf e "start", instantOf e "end" with
                                | Some s, Some f -> f > parseInstant tmin && s < parseInstant tmax
                                | _ -> true
                            | _ -> true

                        (showDeleted || not cancelled) && updatedOk && windowOk

                    let matching = candidates |> List.filter keep

                    let offset =
                        Map.tryFind "pageToken" query |> Option.map int |> Option.defaultValue 0

                    let page = matching |> List.skip offset |> List.truncate server.PageSize
                    let items = JsonArray()

                    for id in page do
                        items.Add((server.TryGet id).Value.DeepClone())

                    let result = JsonObject()
                    result["items"] <- items

                    if offset + server.PageSize < List.length matching then
                        result["nextPageToken"] <- JsonValue.Create(string (offset + server.PageSize))
                    else
                        result["nextSyncToken"] <- JsonValue.Create(sprintf "st-%d" server.Seq)

                    return respond HttpStatusCode.OK (result.ToJsonString())
            | "POST", [ "calendars"; _; "events" ] ->
                let o = JsonNode.Parse(body).AsObject()
                let id = o["id"].GetValue<string>()

                if (server.TryGet id).IsSome then
                    return respond HttpStatusCode.Conflict """{"error":{"code":409,"message":"duplicate"}}"""
                else
                    o["updated"] <- JsonValue.Create(rfc3339 now)
                    server.Store(id, o)
                    return respond HttpStatusCode.OK (o.ToJsonString())
            | "GET", [ "calendars"; _; "events"; id ] ->
                match server.TryGet id with
                | Some e -> return respond HttpStatusCode.OK (e.ToJsonString())
                | None -> return respond HttpStatusCode.NotFound """{"error":{"code":404}}"""
            | "PUT", [ "calendars"; _; "events"; id ] ->
                match server.TryGet id with
                | None -> return respond HttpStatusCode.NotFound """{"error":{"code":404}}"""
                | Some _ ->
                    let o = JsonNode.Parse(body).AsObject()
                    o["id"] <- JsonValue.Create id
                    o["updated"] <- JsonValue.Create(rfc3339 now)
                    server.Store(id, o)
                    return respond HttpStatusCode.OK (o.ToJsonString())
            | "DELETE", [ "calendars"; _; "events"; id ] ->
                match server.TryGet id with
                | None -> return respond HttpStatusCode.NotFound """{"error":{"code":404}}"""
                | Some e when str e "status" = Some "cancelled" ->
                    return respond HttpStatusCode.Gone """{"error":{"code":410}}"""
                | Some e ->
                    e["status"] <- JsonValue.Create "cancelled"
                    e["updated"] <- JsonValue.Create(rfc3339 now)
                    server.Touch id
                    return respond HttpStatusCode.NoContent ""
            | _ -> return respond HttpStatusCode.MethodNotAllowed (sprintf "%s %s" method path)
    }

// ─── Credentials ────────────────────────────────────────────────────

/// A dictionary-backed `ISecretStore`.
type private MemorySecretStore() =
    let secrets = Dictionary<string * string, string>()

    member _.Set(scopeId: string, key: string, value: string) = secrets[(scopeId, key)] <- value

    member _.TryGet(scopeId: string, key: string) =
        match secrets.TryGetValue((scopeId, key)) with
        | true, v -> Some v
        | _ -> None

    interface ISecretStore with
        member this.GetSecret(scopeId, key) = async { return this.TryGet(scopeId, key) }

        member _.SetSecret(scopeId, key, value) = async {
            secrets[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            secrets.Remove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                secrets.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// The token endpoint, stubbed at the substrate's `OAuthTokenPost` seam:
/// a refresh grant for refresh token `rt` mints `at-<rt>`, which the stub
/// Calendar accepts.
type private StubTokenEndpoint(server: StubGoogleCalendar) =
    member val Posts = ResizeArray<(string * string) list>() with get

    member this.Post: OAuthTokenPost =
        fun _url fields -> async {
            this.Posts.Add fields

            let field name =
                fields |> List.tryFind (fst >> (=) name) |> Option.map snd

            match field "grant_type", field "refresh_token" with
            | Some "refresh_token", Some rt ->
                let token = "at-" + rt
                server.ValidTokens.Add token |> ignore
                return Ok(sprintf """{"access_token":"%s","expires_in":3600}""" token)
            | _ -> return Ok """{"error":"unsupported_grant_type"}"""
        }

let private seedCredentials (secrets: MemorySecretStore) (scopeId: string) (refreshToken: string) =
    let flow = DefaultFlowName
    secrets.Set(scopeId, clientIdKey flow Connection, "client-id")
    secrets.Set(scopeId, clientSecretKey flow Connection, "client-secret")
    secrets.Set(scopeId, refreshTokenKey flow Connection, refreshToken)
    secrets.Set("_platform", ChannelSecretKey, ChannelSecret)

let private settingsWith (webhook: bool) : GoogleCalendarSettings = {
    GoogleCalendarSettings.defaults with
        EndpointOverride = Some ApiBase
        WebhookAddress = (if webhook then Some WebhookAddress else None)
        // Wide windows, so the pack's fixed 2026 dates stay inside a
        // no-`since` pull for years (the CalDAV binding's precedent).
        PullWindowDays = 3650
        PullLookbackDays = 3650
}

type private Rig = {
    Server: StubGoogleCalendar
    Secrets: MemorySecretStore
    Tokens: StubTokenEndpoint
    Bridge: GoogleCalendarBridge
    Link: CalendarLinkRef
    Now: DateTimeOffset ref
}

let private rigWith (webhook: bool) (refresher: IOAuthTokenRefresher option) : Rig =
    let now = ref DateTimeOffset.UtcNow
    let clock () = now.Value
    let server = StubGoogleCalendar clock
    let secrets = MemorySecretStore()
    let endpoint = StubTokenEndpoint server
    let scopeId = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    seedCredentials secrets scopeId "rt-1"

    let tokens =
        GoogleCalendarTokenSource(endpoint.Post, secrets, refresher, GoogleCalendarOAuthConfig.defaults, clock)

    let bridge =
        GoogleCalendarBridge(tokens, secrets, settingsWith webhook, new StubGoogleHandler(server), clock)

    {
        Server = server
        Secrets = secrets
        Tokens = endpoint
        Bridge = bridge
        Link = {
            ScopeId = scopeId
            ResourceId = "room-101"
            ExternalCalendarId = CalendarId
            UserId = Connection
        }
        Now = now
    }

let private defaults: Booking = {
    ICalendarBridgeContract.makeBooking "" "" DateTimeOffset.MinValue with
        Title = ""
        Metadata = Map.empty
}

/// Out-of-band edit, as the calendar's owner would make it in Google:
/// project the stored event, transform it, write it back stamped `at`.
let private externalEdit
    (server: StubGoogleCalendar)
    (eventId: string)
    (transform: Booking -> Booking)
    (at: DateTimeOffset)
    =
    match server.TryGet eventId with
    | None -> failwithf "stub Google calendar has no event %s" eventId
    | Some stored ->
        use doc = JsonDocument.Parse(stored.ToJsonString())

        match ofGoogleEvent defaults doc.RootElement with
        | Ok(Ok e) ->
            let edited = toGoogleEvent (transform e.Booking) (Some stored)
            edited["updated"] <- JsonValue.Create(rfc3339 at)
            server.Store(eventId, edited)
        | other -> failwithf "stored event %s is not a booking: %A" eventId other

let private harness (webhook: bool) () : ICalendarBridgeContract.BridgeHarness =
    let rig = rigWith webhook None

    {
        Bridge = rig.Bridge
        Link = rig.Link
        MissingCalendarId = "nobody@group.calendar.google.com"
        ExternalEdit = externalEdit rig.Server
    }

let private expectOk (label: string) (result: Result<'T, BridgeError>) : 'T =
    match result with
    | Ok v -> v
    | Error e -> failtestf "%s: %s" label (BridgeError.message e)

let private bridgeOf (rig: Rig) = rig.Bridge :> ICalendarBridge

let private utc (y: int) (mo: int) (d: int) (h: int) =
    DateTimeOffset(y, mo, d, h, 0, 0, TimeSpan.Zero)

/// A refresh substrate that knows what it was asked: `RefreshNow` for a
/// registered descriptor writes a new access token under the derived key
/// and makes the stub Calendar accept it.
type private StubRefresher(secrets: ISecretStore, server: StubGoogleCalendar) =
    let descriptors = Dictionary<string, OAuthRefreshDescriptor>()

    member val RefreshCalls = 0 with get, set
    member val Registered = ResizeArray<OAuthRefreshDescriptor>() with get
    member val Unregistered = ResizeArray<string * string>() with get

    interface IOAuthTokenRefresher with
        member this.RefreshNow(provider, configId) = async {
            match descriptors.TryGetValue(provider + ":" + configId) with
            | false, _ -> return PermanentError "unknown descriptor"
            | true, d ->
                this.RefreshCalls <- this.RefreshCalls + 1
                let token = sprintf "substrate-%d" this.RefreshCalls
                server.ValidTokens.Add token |> ignore
                let expiry = DateTimeOffset.UtcNow.AddHours 1.0
                let! _ = secrets.SetSecret(d.ScopeId, OAuthRefreshDescriptor.accessTokenKey d, token)

                let! _ =
                    secrets.SetSecret(
                        d.ScopeId,
                        OAuthRefreshDescriptor.accessExpiryKey d,
                        expiry.ToString("o", CultureInfo.InvariantCulture)
                    )

                return Refreshed expiry
        }

        member this.RegisterDescriptor d = async {
            this.Registered.Add d
            descriptors[OAuthRefreshDescriptor.key d] <- d
        }

        member this.UnregisterDescriptor(provider, configId) = async {
            this.Unregistered.Add(provider, configId)
            descriptors.Remove(provider + ":" + configId) |> ignore
        }

        member _.GetDescriptor(provider, configId) = async {
            match descriptors.TryGetValue(provider + ":" + configId) with
            | true, d -> return Some d
            | _ -> return None
        }

        member _.ListDescriptors() = async { return List.ofSeq descriptors.Values }

// ─── The sync-engine fixture the route and renewal cases run over ───

type private SyncRig = {
    Rig: Rig
    Sync: ICalendarSync
    Scheduler: IBookingScheduler
    Store: IEntityStore
}

let private syncRig (rig: Rig) (policy: ConflictPolicy) = async {
    let store = InMemoryEntityStore() :> IEntityStore

    let scheduler =
        BookingScheduler(store, InMemoryEventStore() :> IEventStore) :> IBookingScheduler

    let sync =
        CalendarSync(store, scheduler, [ rig.Bridge :> ICalendarBridge ]) :> ICalendarSync

    let actor = EntityPrincipal.ofPrincipal Connection

    let resource: BookableResource = {
        Id = rig.Link.ResourceId
        Type = "BookableResource"
        Version = 0
        ResourceType = "Room"
        DisplayName = rig.Link.ResourceId
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

    let! _ = scheduler.RegisterResource(rig.Link.ScopeId, actor, resource)

    match!
        sync.LinkResource(
            rig.Link.ScopeId,
            rig.Link.ResourceId,
            KindName,
            rig.Link.ExternalCalendarId,
            rig.Link.UserId,
            policy,
            actor
        )
    with
    | Error e -> return failtestf "link refused: %s" (CalendarSyncError.message e)
    | Ok _ ->
        return {
            Rig = rig
            Sync = sync
            Scheduler = scheduler
            Store = store
        }
}

let private bookAndPush (s: SyncRig) (booking: Booking) = async {
    match! s.Scheduler.Book(s.Rig.Link.ScopeId, booking, Connection) with
    | Error e -> return failtestf "local booking refused: %A" e
    | Ok saved ->
        match! s.Sync.PushBooking(s.Rig.Link.ScopeId, saved, EntityPrincipal.ofPrincipal Connection) with
        | Error e -> return failtestf "push refused: %s" (CalendarSyncError.message e)
        | Ok outcome ->
            Expect.isEmpty outcome.Failures "push reported a bridge failure"
            return saved
}

/// The headers Google sends on a channel notification.
let private notification (token: string) (state: string) =
    Map.ofList [
        "X-Goog-Channel-ID", "toolup-channel"
        "X-Goog-Channel-Token", token
        "X-Goog-Resource-State", state
        "X-Goog-Resource-ID", "res-1"
        "X-Goog-Resource-URI", sprintf "%s/calendars/%s/events?alt=json" ApiBase (Uri.EscapeDataString CalendarId)
        "X-Goog-Message-Number", "2"
    ]

let private cronContext (scopeId: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = scopeId
    AccessContext = AccessContext.unrestricted (AuthenticatedUser Connection)
    Attempt = 1
    Trigger = CronTrigger DefaultRenewalCron
    TriggerSource = ScheduledByCron
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = ""
    DeadLetterDestination = None
}

// ─── The env-gated live case ────────────────────────────────────────

let private liveEnv (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

let private liveTests =
    match
        liveEnv "TOOLUP_GOOGLE_CALENDAR_TEST_CLIENT_ID",
        liveEnv "TOOLUP_GOOGLE_CALENDAR_TEST_CLIENT_SECRET",
        liveEnv "TOOLUP_GOOGLE_CALENDAR_TEST_REFRESH_TOKEN",
        liveEnv "TOOLUP_GOOGLE_CALENDAR_TEST_CALENDAR_ID"
    with
    | Some clientId, Some clientSecret, Some refreshToken, Some calendarId ->
        testList "Google Calendar (live)" [
            testAsync "link, push, pull back and cancel against the real API" {
                let secrets = MemorySecretStore()
                secrets.Set("_platform", clientIdKey DefaultFlowName "live", clientId)
                secrets.Set("_platform", clientSecretKey DefaultFlowName "live", clientSecret)
                secrets.Set("_platform", refreshTokenKey DefaultFlowName "live", refreshToken)
                use http = new HttpClient()

                let tokens =
                    GoogleCalendarTokenSource(httpPost http, secrets, None, GoogleCalendarOAuthConfig.defaults)

                let bridge =
                    GoogleCalendarBridge(tokens, secrets, GoogleCalendarSettings.defaults) :> ICalendarBridge

                let link: CalendarLinkRef = {
                    ScopeId = "_platform"
                    ResourceId = "live-probe"
                    ExternalCalendarId = calendarId
                    UserId = "live"
                }

                expectOk "live link" (bridge.LinkResource link |> Async.RunSynchronously)

                let booking = {
                    ICalendarBridgeContract.makeBooking
                        ("live-" + Guid.NewGuid().ToString("N").Substring(0, 12))
                        "live-probe"
                        (DateTimeOffset(DateTime.UtcNow.Date.AddDays 2.0, TimeSpan.Zero)) with
                        Metadata = Map.ofList [ "probe", "phase-830" ]
                }

                let! pushed = bridge.Push(link, booking, None)
                let eventId = expectOk "live push" pushed

                let! pulled = bridge.Pull(link, None, defaults)
                let events = expectOk "live pull" pulled

                Expect.isTrue
                    (events |> List.exists (fun e -> e.Booking.Id = booking.Id))
                    "the pushed booking came back from Google"

                let! removed = bridge.Push(link, { booking with Status = Cancelled }, Some eventId)
                expectOk "live cancel" removed |> ignore
            }
        ]
    | _ ->
        testList "Google Calendar (live)" [
            // Pending, not absent: a checkout WITH a Google test account runs
            // it, and one without can see why it did not.
            ptestCase
                "skipped — set TOOLUP_GOOGLE_CALENDAR_TEST_CLIENT_ID / _CLIENT_SECRET / _REFRESH_TOKEN / _CALENDAR_ID to run"
            <| fun _ -> ()
        ]

// ─── The pack ───────────────────────────────────────────────────────

let tests =
    testList "GoogleCalendarBridge" [
        ICalendarBridgeContract.tests "Google over a stub server (watch channels)" (harness true)
        ICalendarBridgeContract.tests "Google over a stub server (polling only)" (harness false)

        testList "event ids" [
            test "an event id is in Google's alphabet and round-trips to the booking id" {
                for bookingId in [ "b"; "bk-1"; "booking/with spaces"; "ünïcødé-✓"; String('x', 300) ] do
                    let id = eventIdOf bookingId
                    Expect.isGreaterThanOrEqual id.Length 5 "Google's minimum length"

                    Expect.all id (fun c -> (c >= 'a' && c <= 'v') || (c >= '0' && c <= '9')) "only a–v and 0–9"

                    Expect.equal (tryBookingIdOfEventId id) (Some bookingId) "decodes back"
            }

            test "an id this bridge did not mint decodes to nothing" {
                Expect.isNone (tryBookingIdOfEventId "abc123googlemade") "a Google-made id"
                Expect.isNone (tryBookingIdOfEventId "toolupzzz") "outside the alphabet"
            }
        ]

        testList "mapping" [
            testAsync "RRULE and the attendee carry-through reach Google as native fields" {
                let rig = rigWith false None

                let booking = {
                    ICalendarBridgeContract.makeBooking "bk-weekly" "room-101" (utc 2026 10 5 9) with
                        Recurrence =
                            Some {
                                Frequency = Weekly
                                Interval = 2
                                ByWeekday = [ DayOfWeek.Monday; DayOfWeek.Wednesday ]
                                Until = None
                                Count = Some 6
                            }
                }

                let! pushed = (bridgeOf rig).Push(rig.Link, booking, None)
                let eventId = expectOk "push" pushed
                let stored = (rig.Server.TryGet eventId).Value

                Expect.equal
                    (stored["recurrence"].AsArray().Item(0).GetValue<string>())
                    "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6"
                    "the rule rides Google's recurrence array"

                Expect.equal
                    (stored["start"].AsObject().Item("timeZone").GetValue<string>())
                    "UTC"
                    "recurring events carry a zone"

                let emails =
                    stored["attendees"].AsArray()
                    |> Seq.map (fun a -> a.AsObject().Item("email").GetValue<string>())
                    |> List.ofSeq

                Expect.equal emails [ "bob@example.com"; "carol@example.com" ] "attendees are real Google attendees"
                Expect.equal (str stored "location") (Some "Room 3; second floor, east") "location is native"

                let! pulled = (bridgeOf rig).Pull(rig.Link, None, defaults)
                let back = expectOk "pull" pulled |> List.find (fun e -> e.Booking.Id = booking.Id)
                Expect.equal back.Booking.Recurrence booking.Recurrence "the rule round-trips"
                Expect.equal back.Booking.Metadata booking.Metadata "metadata round-trips"
            }

            testAsync "an attendee change made in Google wins over the pushed spelling" {
                let rig = rigWith false None

                let booking =
                    ICalendarBridgeContract.makeBooking "bk-attendees" "room-101" (utc 2026 10 6 9)

                let! pushed = (bridgeOf rig).Push(rig.Link, booking, None)
                let eventId = expectOk "push" pushed
                let stored = (rig.Server.TryGet eventId).Value
                let attendees = JsonArray()
                let dave = JsonObject()
                dave["email"] <- JsonValue.Create "dave@example.com"
                attendees.Add dave
                stored["attendees"] <- attendees
                rig.Server.Store(eventId, stored)

                let! pulled = (bridgeOf rig).Pull(rig.Link, None, defaults)
                let back = expectOk "pull" pulled |> List.find (fun e -> e.Booking.Id = booking.Id)

                Expect.equal
                    (Map.tryFind iCalendar.AttendeesKey back.Booking.Metadata)
                    (Some "mailto:dave@example.com")
                    "Google's attendee list is what comes back"
            }

            testAsync "an update keeps what the calendar's owner set in Google" {
                let rig = rigWith false None

                let booking =
                    ICalendarBridgeContract.makeBooking "bk-owner" "room-101" (utc 2026 10 7 9)

                let! pushed = (bridgeOf rig).Push(rig.Link, booking, None)
                let eventId = expectOk "push" pushed
                let stored = (rig.Server.TryGet eventId).Value
                stored["colorId"] <- JsonValue.Create "5"

                stored["extendedProperties"].AsObject().Item("private").AsObject().Item("owner.note") <-
                    JsonValue.Create "keep me"

                rig.Server.Store(eventId, stored)

                let renamed = {
                    booking with
                        Title = "Renamed"
                        Metadata = booking.Metadata |> Map.remove "cost centre"
                }

                let! _ = (bridgeOf rig).Push(rig.Link, renamed, Some eventId)
                let after = (rig.Server.TryGet eventId).Value
                let privateProps = after["extendedProperties"].AsObject().Item("private").AsObject()

                Expect.equal (str after "summary") (Some "Renamed") "the managed field moved"
                Expect.equal (str after "colorId") (Some "5") "the owner's colour survived"
                Expect.equal (str privateProps "owner.note") (Some "keep me") "the owner's property survived"
                Expect.isNone (str privateProps "toolup.meta.cost centre") "a removed metadata key is removed"
            }

            testAsync "a cancelled or single-instance event is not reported by a pull" {
                let rig = rigWith false None

                let booking =
                    ICalendarBridgeContract.makeBooking "bk-kept" "room-101" (utc 2026 10 8 9)

                let! _ = (bridgeOf rig).Push(rig.Link, booking, None)

                let instance =
                    toGoogleEvent (ICalendarBridgeContract.makeBooking "x" "room-101" (utc 2026 10 8 12)) None

                instance["id"] <- JsonValue.Create "instance1"
                instance["recurringEventId"] <- JsonValue.Create "series1"
                instance["updated"] <- JsonValue.Create(rfc3339 DateTimeOffset.UtcNow)
                rig.Server.Store("instance1", instance)

                let! pulled = (bridgeOf rig).Pull(rig.Link, Some(DateTimeOffset.UtcNow.AddDays -1.0), defaults)
                let ids = expectOk "pull" pulled |> List.map _.ExternalEventId
                Expect.equal ids [ eventIdOf "bk-kept" ] "only the booking comes back"
            }
        ]

        testList "push and pull" [
            testAsync "a re-push without the existing id updates rather than duplicating" {
                let rig = rigWith false None

                let booking =
                    ICalendarBridgeContract.makeBooking "bk-twice" "room-101" (utc 2026 10 9 9)

                let! first = (bridgeOf rig).Push(rig.Link, booking, None)
                let! second = (bridgeOf rig).Push(rig.Link, { booking with Title = "Second" }, None)
                Expect.equal (expectOk "second" second) (expectOk "first" first) "the same event id"
                Expect.equal rig.Server.Ids [ eventIdOf "bk-twice" ] "exactly one event exists"
                Expect.equal (str (rig.Server.TryGet(eventIdOf "bk-twice")).Value "summary") (Some "Second") "updated"
            }

            testAsync "a pull pages through every result" {
                let rig = rigWith false None

                for i in 1..5 do
                    let! _ =
                        (bridgeOf rig)
                            .Push(
                                rig.Link,
                                ICalendarBridgeContract.makeBooking
                                    (sprintf "bk-page-%d" i)
                                    "room-101"
                                    (utc 2026 10 10 i),
                                None
                            )

                    ()

                let! pulled = (bridgeOf rig).Pull(rig.Link, None, defaults)
                Expect.equal (expectOk "pull" pulled |> List.length) 5 "all five across three pages"

                let pages =
                    rig.Server.Requests
                    |> Seq.filter (fun (m, p) -> m = "GET" && p.Contains "/events?")
                    |> Seq.length

                Expect.equal pages 3 "page size two, five events"
            }

            testAsync "the second incremental pull rides the sync token, and an expired token falls back" {
                let rig = rigWith false None

                let booking =
                    ICalendarBridgeContract.makeBooking "bk-sync" "room-101" (utc 2026 10 11 9)

                let! _ = (bridgeOf rig).Push(rig.Link, booking, None)
                let! first = (bridgeOf rig).Pull(rig.Link, None, defaults)
                expectOk "first" first |> ignore
                Expect.isSome (rig.Secrets.TryGet(rig.Link.ScopeId, syncTokenKey rig.Link)) "a sync token was kept"

                externalEdit
                    rig.Server
                    (eventIdOf "bk-sync")
                    (fun b -> { b with Title = "Changed" })
                    DateTimeOffset.UtcNow

                let! second = (bridgeOf rig).Pull(rig.Link, Some(DateTimeOffset.UtcNow.AddDays -1.0), defaults)
                let events = expectOk "second" second
                Expect.equal (events |> List.map _.Booking.Title) [ "Changed" ] "only the change since the token"

                Expect.isTrue
                    (rig.Server.Requests |> Seq.exists (fun (_, p) -> p.Contains "syncToken="))
                    "the pull asked with the stored sync token"

                rig.Server.ExpireSyncTokens <- true
                let! third = (bridgeOf rig).Pull(rig.Link, Some(DateTimeOffset.UtcNow.AddDays -1.0), defaults)
                expectOk "third" third |> ignore

                Expect.isTrue
                    (rig.Server.Requests |> Seq.exists (fun (_, p) -> p.Contains "updatedMin="))
                    "an expired token fell back to updatedMin"
            }

            testAsync "an unknown calendar is CalendarNotFound on pull" {
                let rig = rigWith false None

                let missing = {
                    rig.Link with
                        ExternalCalendarId = "nobody@group.calendar.google.com"
                }

                match! (bridgeOf rig).Pull(missing, None, defaults) with
                | Error(CalendarNotFound _) -> ()
                | other -> failtestf "expected CalendarNotFound, got %A" other
            }
        ]

        testList "credentials" [
            testAsync "a rotated refresh token is used on the very next call" {
                let rig = rigWith false None
                let! _ = (bridgeOf rig).LinkResource rig.Link
                rig.Secrets.Set(rig.Link.ScopeId, refreshTokenKey DefaultFlowName Connection, "rt-2")
                let! _ = (bridgeOf rig).LinkResource rig.Link

                let used =
                    rig.Tokens.Posts
                    |> Seq.choose (List.tryFind (fst >> (=) "refresh_token") >> Option.map snd)
                    |> List.ofSeq

                Expect.equal used [ "rt-1"; "rt-2" ] "no restart, no cache: the second call read the new token"
            }

            testAsync "a missing credential is an authentication failure naming the key" {
                let rig = rigWith false None
                let orphan = { rig.Link with UserId = "nobody" }

                match! (bridgeOf rig).LinkResource orphan with
                | Error(AuthenticationFailed message) ->
                    Expect.stringContains message (clientIdKey DefaultFlowName "nobody") "names the missing key"
                | other -> failtestf "expected AuthenticationFailed, got %A" other
            }

            testAsync "with the refresh substrate the bridge registers, refreshes through it and uses the cache" {
                let now = ref DateTimeOffset.UtcNow
                let server = StubGoogleCalendar(fun () -> now.Value)
                let secrets = MemorySecretStore()
                let endpoint = StubTokenEndpoint server
                seedCredentials secrets "team-r" "rt-1"
                let refresher = StubRefresher(secrets, server)

                let tokens =
                    GoogleCalendarTokenSource(
                        endpoint.Post,
                        secrets,
                        Some(refresher :> IOAuthTokenRefresher),
                        GoogleCalendarOAuthConfig.defaults,
                        (fun () -> now.Value)
                    )

                let bridge =
                    GoogleCalendarBridge(
                        tokens,
                        secrets,
                        settingsWith false,
                        new StubGoogleHandler(server),
                        (fun () -> now.Value)
                    )
                    :> ICalendarBridge

                let link: CalendarLinkRef = {
                    ScopeId = "team-r"
                    ResourceId = "room-101"
                    ExternalCalendarId = CalendarId
                    UserId = Connection
                }

                expectOk "first link" (bridge.LinkResource link |> Async.RunSynchronously)
                Expect.equal refresher.Registered.Count 1 "the descriptor was registered on first use"
                Expect.equal refresher.Registered[0].ScopeId "team-r" "pinned to the link's scope"
                Expect.equal refresher.RefreshCalls 1 "and refreshed through the substrate"

                let! _ = bridge.LinkResource link
                Expect.equal refresher.RefreshCalls 1 "a fresh cached token is used as it is"
                Expect.isEmpty endpoint.Posts "the bridge never minted a token itself"

                // Google revokes the cached token: one forced refresh, then success.
                server.ValidTokens.Remove "substrate-1" |> ignore
                let! third = bridge.LinkResource link
                expectOk "after revocation" third
                Expect.equal refresher.RefreshCalls 2 "a 401 forced exactly one refresh"
            }
        ]

        testList "the OAuth flow" [
            testAsync "the authorize URL asks for offline access, forces consent and carries PKCE" {
                let secrets = MemorySecretStore()
                seedCredentials secrets "team-f" "rt-1"
                let server = StubGoogleCalendar(fun () -> DateTimeOffset.UtcNow)

                let flow =
                    ToolUp.Calendar.GoogleCalendarOAuth.create
                        (StubTokenEndpoint server).Post
                        secrets
                        None
                        GoogleCalendarOAuthConfig.defaults

                let ctx = OAuthFlowContext.forDataSource "team-f" Connection None

                match!
                    flow.BuildAuthorizeUrl(
                        ctx,
                        "state-1",
                        "https://app.invalid/cb",
                        Some { Challenge = "chal"; Method = "S256" }
                    )
                with
                | Error e -> failtestf "authorize URL refused: %s" (OAuthError.toMessage e)
                | Ok url ->
                    Expect.stringStarts url DefaultAuthorizeEndpoint "Google's endpoint"
                    Expect.stringContains url "access_type=offline" "offline access"
                    Expect.stringContains url "prompt=consent" "forced consent"
                    Expect.stringContains url "code_challenge=chal" "PKCE"
                    Expect.stringContains url (Uri.EscapeDataString EventsScope) "the events scope"
            }

            testAsync "an exchange that returns no refresh token is refused with the reason" {
                let secrets = MemorySecretStore()
                seedCredentials secrets "team-f" "rt-1"

                let post: OAuthTokenPost =
                    fun _ _ -> async { return Ok """{"access_token":"a","expires_in":3600}""" }

                let flow =
                    ToolUp.Calendar.GoogleCalendarOAuth.create post secrets None GoogleCalendarOAuthConfig.defaults

                let ctx = OAuthFlowContext.forDataSource "team-f" Connection None

                match! flow.ExchangeCode(ctx, "code", "https://app.invalid/cb", Some "verifier") with
                | Error(OAuthFlowFailed message) -> Expect.stringContains message "prompt=consent" "names the cause"
                | other -> failtestf "expected OAuthFlowFailed, got %A" other
            }

            testAsync "revoking unregisters the refresh descriptor first" {
                let secrets = MemorySecretStore()
                let server = StubGoogleCalendar(fun () -> DateTimeOffset.UtcNow)
                let refresher = StubRefresher(secrets, server)
                let post: OAuthTokenPost = fun _ _ -> async { return Ok "" }

                let flow =
                    ToolUp.Calendar.GoogleCalendarOAuth.create
                        post
                        secrets
                        (Some(refresher :> IOAuthTokenRefresher))
                        GoogleCalendarOAuthConfig.defaults

                let ctx = OAuthFlowContext.forDataSource "team-f" Connection None
                let! result = flow.Revoke(ctx, "rt-1")
                Expect.equal result (Ok()) "revoked"

                Expect.equal
                    (List.ofSeq refresher.Unregistered)
                    [ DefaultFlowName, Connection ]
                    "descriptor unregistered"
            }
        ]

        testList "watch channels" [
            testAsync "linking opens a channel, a second link keeps it, a near-expiry link renews it" {
                let rig = rigWith true None
                let bridge = bridgeOf rig
                expectOk "link" (bridge.LinkResource rig.Link |> Async.RunSynchronously)
                Expect.equal rig.Server.Watches.Count 1 "one channel opened"
                let watch = rig.Server.Watches[0]
                Expect.equal (str watch "address") (Some WebhookAddress) "at the configured address"

                Expect.equal
                    (str watch "token")
                    (Some(channelToken ChannelSecret rig.Link.ScopeId CalendarId))
                    "carrying the signed channel token"

                let! _ = bridge.LinkResource rig.Link
                Expect.equal rig.Server.Watches.Count 1 "a live channel is kept"

                rig.Now.Value <- rig.Now.Value.AddDays 6.5
                let! _ = bridge.LinkResource rig.Link
                Expect.equal rig.Server.Watches.Count 2 "a channel inside the renewal lead is replaced"
                Expect.equal (List.ofSeq rig.Server.Stops) [ watch["id"].GetValue<string>() ] "and the old one stopped"
            }

            testAsync "unlinking stops the channel and forgets the link's state" {
                let rig = rigWith true None
                let bridge = bridgeOf rig
                let! _ = bridge.LinkResource rig.Link
                let! _ = bridge.Pull(rig.Link, None, defaults)
                let! result = bridge.UnlinkResource rig.Link
                Expect.equal result (Ok()) "unlinked"
                Expect.equal rig.Server.Stops.Count 1 "the channel was stopped"
                Expect.isNone (rig.Secrets.TryGet(rig.Link.ScopeId, channelKey rig.Link)) "channel record gone"
                Expect.isNone (rig.Secrets.TryGet(rig.Link.ScopeId, syncTokenKey rig.Link)) "sync token gone"
            }

            testAsync "a genuine notification names the calendar; a handshake names nothing" {
                let rig = rigWith true None
                let token = channelToken ChannelSecret rig.Link.ScopeId CalendarId
                let! changed = (bridgeOf rig).HandleWebhook(notification token "exists", [||])

                Expect.equal
                    (expectOk "exists" changed)
                    [
                        {
                            ExternalCalendarId = CalendarId
                            ChangedEventId = None
                        }
                    ]
                    "the watched calendar changed"

                let! handshake = (bridgeOf rig).HandleWebhook(notification token "sync", [||])
                Expect.isEmpty (expectOk "sync" handshake) "the opening handshake is not a change"
            }

            testAsync "a forged or misdirected notification is refused" {
                let rig = rigWith true None
                let forged = channelToken "not-the-secret" rig.Link.ScopeId CalendarId

                match! (bridgeOf rig).HandleWebhook(notification forged "exists", [||]) with
                | Error(MalformedPayload _) -> ()
                | other -> failtestf "a forged token was accepted: %A" other

                let otherCalendar =
                    channelToken ChannelSecret rig.Link.ScopeId "someone-else@example.com"

                match! (bridgeOf rig).HandleWebhook(notification otherCalendar "exists", [||]) with
                | Error(MalformedPayload _) -> ()
                | other -> failtestf "a token for another calendar was accepted: %A" other

                match! (bridgeOf rig).HandleWebhook(Map.empty, [||]) with
                | Error(MalformedPayload _) -> ()
                | other -> failtestf "a header-less request was accepted: %A" other
            }

            testAsync "a notification through the route drives a pull that applies the external edit" {
                let rig = rigWith true None
                let! s = syncRig rig ExternalWins

                let booking =
                    ICalendarBridgeContract.makeBooking "bk-routed" "room-101" (utc 2026 10 12 9)

                let! saved = bookAndPush s booking

                externalEdit
                    rig.Server
                    (eventIdOf saved.Id)
                    (fun b -> { b with Title = "Edited in Google" })
                    (DateTimeOffset.UtcNow.AddMinutes 5.0)

                let token = str rig.Server.Watches[0] "token" |> Option.get
                let! receipt = receive s.Sync (notification token "exists") [||]
                Expect.equal receipt.StatusCode 200 "accepted"

                match receipt.Outcome with
                | None -> failtest "no pull ran"
                | Some outcome -> Expect.equal outcome.AppliedLocally 1 "the edit was applied locally"

                match! s.Scheduler.GetBooking(rig.Link.ScopeId, saved.Id) with
                | Some local -> Expect.equal local.Title "Edited in Google" "the local booking moved"
                | None -> failtest "the local booking vanished"

                let! forged =
                    receive s.Sync (notification (channelToken "wrong" rig.Link.ScopeId CalendarId) "exists") [||]

                Expect.equal forged.StatusCode 401 "a forged notification is refused"
                let! stray = receive s.Sync Map.empty [||]
                Expect.equal stray.StatusCode 400 "a non-notification is refused"
            }

            testAsync "the renewal job renews an expiring channel, and polls a link whose renewal fails" {
                let rig = rigWith true None
                let! s = syncRig rig LatestModifiedWins
                Expect.equal rig.Server.Watches.Count 1 "linking opened the channel"

                let job =
                    GoogleCalendarChannelRenewalJobHandler(rig.Bridge, s.Sync, s.Store) :> IJobHandler

                rig.Now.Value <- rig.Now.Value.AddDays 6.5

                match! job.Execute(cronContext rig.Link.ScopeId) with
                | Success -> ()
                | other -> failtestf "renewal did not succeed: %A" other

                Expect.equal rig.Server.Watches.Count 2 "the scheduled job renewed the channel"

                rig.Now.Value <- rig.Now.Value.AddDays 6.5
                rig.Server.FailWatch <- true

                let pullsBefore =
                    rig.Server.Requests
                    |> Seq.filter (fun (_, p) -> p.Contains "/events?")
                    |> Seq.length

                match! job.Execute(cronContext rig.Link.ScopeId) with
                | TransientFailure reason -> Expect.stringContains reason "room-101" "names the link"
                | other -> failtestf "a failed renewal should be retryable, got %A" other

                let pullsAfter =
                    rig.Server.Requests
                    |> Seq.filter (fun (_, p) -> p.Contains "/events?")
                    |> Seq.length

                Expect.isGreaterThan pullsAfter pullsBefore "the failed renewal fell back to a pull"
            }
        ]

        testList "health" [
            testAsync "healthy with a working credential, unhealthy without one" {
                let rig = rigWith false None

                let tokens =
                    GoogleCalendarTokenSource(rig.Tokens.Post, rig.Secrets, None, GoogleCalendarOAuthConfig.defaults)

                let healthy =
                    GoogleCalendarBridgeHealth(
                        tokens,
                        settingsWith false,
                        rig.Link.ScopeId,
                        Connection,
                        new StubGoogleHandler(rig.Server)
                    )
                    :> IHealthCheck

                let! ok = healthy.Check()
                Expect.equal ok Healthy "calendarList answered"

                let orphan =
                    GoogleCalendarBridgeHealth(
                        tokens,
                        settingsWith false,
                        rig.Link.ScopeId,
                        "nobody",
                        new StubGoogleHandler(rig.Server)
                    )
                    :> IHealthCheck

                match! orphan.Check() with
                | Unhealthy _ -> ()
                | other -> failtestf "a missing credential should be Unhealthy, got %A" other
            }
        ]

        liveTests
    ]