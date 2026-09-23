module ToolUp.Scheduling.Tests.InProcess.MicrosoftGraphCalendarBridgeTests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Web
open Expecto
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Metrics
open ToolUp.Platform.Secrets
open ToolUp.Scheduling
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.BookingScheduler
open ToolUp.Scheduling.CalendarSync
open ToolUp.Scheduling.SchedulingCompose
open ToolUp.Calendar.MicrosoftGraphOAuth
open ToolUp.Calendar.MicrosoftGraph
open ToolUp.Calendar.MicrosoftGraphSubscriptions
open ToolUp.Calendar.MicrosoftGraphHealth
open ToolUp.Scheduling.Tests.Contracts
open ToolUp.Scheduling.Tests.InProcess.InMemoryEntityStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryEventStore

// ─── Phase 831 — binding 3 of the ICalendarBridge contract pack ─────
//
// The REAL `MicrosoftGraphCalendarBridge` over a stub `HttpMessageHandler`
// standing in for BOTH Microsoft endpoints: the v2.0 token endpoint the
// Phase 10h refresher posts to, and the Graph v1.0 surface the bridge
// calls. The stub holds events as Graph JSON — what Graph stores — so
// every line of the bridge (request composition, the booking ↔ Graph
// mapping, the extended-property snapshot, delta paging, the cursor, the
// status classification) is exercised; only the socket is not.
//
// Tokens flow through the real `InProcessOAuthTokenRefresher` (over a
// scheduler that accepts and forgets), so rotation is the substrate's
// behaviour, not a fake's. Subscriptions are validated by the stub the
// way Graph validates them — by calling the notification URL — and the
// URL resolves to the companion's own notification route in-process, so
// the handshake and the notification path run through the same Giraffe
// handler a deployment mounts.
//
// A live case rides the same bridge, gated on `TOOLUP_MSGRAPH_CALENDAR_TEST_*`
// so a fresh checkout is green with no Entra registration.

[<Literal>]
let private Endpoint = "https://graph.invalid"

[<Literal>]
let private CalendarId = "AAMkCal-work=="

[<Literal>]
let private ReadOnlyCalendarId = "AAMkCal-shared=="

[<Literal>]
let private ClientSecretValue = "client-secret-value"

[<Literal>]
let private WebhookSecretValue = "webhook-secret-value"

[<Literal>]
let private NotificationBase = "https://app.invalid/webhooks/calendar-microsoft"

let private tokenPath = "/common/oauth2/v2.0/token"

// ─── The stub Microsoft (token endpoint + Graph) ────────────────────

/// A minimal in-memory Microsoft identity platform + Graph. Events are
/// held as Graph JSON objects; every write appends to a change log the
/// delta surface reads.
type StubMicrosoft() =
    let events = Dictionary<string, JsonObject>()
    let transactions = Dictionary<string, string>()
    let changes = ResizeArray<int * string * bool>()
    let subscriptions = Dictionary<string, JsonObject>()
    let validAccess = HashSet<string>()
    let mutable seq = 0
    let mutable nextId = 0
    let mutable refreshToken = "rt-0"
    let mutable issued = 0
    let mutable deltaExpired = false

    member val Requests = ResizeArray<string * string>() with get
    member val PageSize = 2 with get, set
    member val TokenRequests = ResizeArray<Map<string, string>>() with get

    /// The notification endpoint Graph would POST to — wired by a test
    /// to the companion's notification route, in-process.
    /// `url -> body -> (status, contentType, body)`.
    member val NotificationEndpoint: (string -> string -> Async<int * string * string>) option = None with get, set

    member _.Events = events
    member _.Subscriptions = subscriptions
    member _.CurrentRefreshToken = refreshToken
    member _.IssuedTokens = issued

    member _.InvalidateAccessTokens() = validAccess.Clear()
    member _.ExpireDeltaTokens() = deltaExpired <- true

    member _.Record(eventId: string, removed: bool) =
        seq <- seq + 1
        changes.Add((seq, eventId, removed))

    member _.Seq = seq

    member _.ChangesAfter(since: int) =
        changes |> Seq.filter (fun (s, _, _) -> s > since) |> List.ofSeq

    member _.DeltaExpired = deltaExpired

    member _.NextEventId() =
        nextId <- nextId + 1
        sprintf "AAMkEvt%03d==" nextId

    member _.Transactions = transactions

    member this.Token(form: Map<string, string>) =
        this.TokenRequests.Add form

        match Map.tryFind "grant_type" form, Map.tryFind "refresh_token" form with
        | Some "refresh_token", Some presented when presented = refreshToken ->
            issued <- issued + 1
            let access = sprintf "at-%d" issued
            validAccess.Add access |> ignore
            // The identity platform rotates on every refresh.
            refreshToken <- sprintf "rt-%d" issued
            Some(access, refreshToken)
        | _ -> None

    member _.IsAuthorised(bearer: string) = validAccess.Contains bearer

/// Overlap test the stub's calendarView uses. A recurring series is
/// "in" any window that starts before the series ends — close enough
/// for the stub, which never enumerates occurrences.
let private overlaps (event: JsonObject) (windowStart: DateTimeOffset) (windowEnd: DateTimeOffset) =
    let instant (name: string) =
        let raw = event[name].["dateTime"].GetValue<string>()

        DateTimeOffset(
            DateTime.SpecifyKind(DateTime.Parse(raw, Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)
        )

    let start = instant "start"
    let finish = instant "end"

    match event["recurrence"] with
    | null -> start < windowEnd && finish > windowStart
    | _ -> start < windowEnd

let private deltaItems (event: JsonObject) : JsonNode list =
    let copy = JsonNode.Parse(event.ToJsonString()).AsObject()
    copy.Remove "singleValueExtendedProperties" |> ignore

    match event["recurrence"] with
    | null ->
        copy["type"] <- JsonValue.Create "singleInstance"
        [ copy ]
    | _ ->
        // calendarView expands a series: the delta page names occurrences,
        // each pointing back at its master.
        let masterId = event["id"].GetValue<string>()

        [ 1; 2 ]
        |> List.map (fun n ->
            let occurrence = JsonNode.Parse(copy.ToJsonString()).AsObject()
            occurrence["id"] <- JsonValue.Create(sprintf "%s_occ%d" masterId n)
            occurrence["type"] <- JsonValue.Create "occurrence"
            occurrence["seriesMasterId"] <- JsonValue.Create masterId
            occurrence.Remove "recurrence" |> ignore
            occurrence :> JsonNode)

type StubHandler(server: StubMicrosoft) =
    inherit HttpMessageHandler()

    let respond (status: HttpStatusCode) (body: string) =
        let response = new HttpResponseMessage(status)
        response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        response

    let graphError (status: HttpStatusCode) (code: string) =
        respond status (sprintf """{"error":{"code":"%s","message":"%s"}}""" code code)

    let segments (uri: Uri) =
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map Uri.UnescapeDataString
        |> List.ofArray

    let pageOf (baseUrl: string) (query: Collections.Specialized.NameValueCollection) (items: JsonNode list) =
        let offset =
            match query["$skiptoken"] with
            | null -> 0
            | s -> int s

        let page =
            items |> List.skip (min offset items.Length) |> List.truncate server.PageSize

        let root = JsonObject()
        root["value"] <- JsonArray(page |> List.map (fun n -> JsonNode.Parse(n.ToJsonString())) |> Array.ofList)

        let window =
            sprintf
                "startDateTime=%s&endDateTime=%s"
                (Uri.EscapeDataString query["startDateTime"])
                (Uri.EscapeDataString query["endDateTime"])

        let carried =
            match query["$deltatoken"] with
            | null -> ""
            | t -> "&$deltatoken=" + t

        if offset + server.PageSize < items.Length then
            root["@odata.nextLink"] <-
                JsonValue.Create(sprintf "%s?%s%s&$skiptoken=%d" baseUrl window carried (offset + server.PageSize))
        else
            root["@odata.deltaLink"] <- JsonValue.Create(sprintf "%s?%s&$deltatoken=%d" baseUrl window server.Seq)

        respond HttpStatusCode.OK (root.ToJsonString())

    let calendarExists (id: string) =
        id = CalendarId || id = ReadOnlyCalendarId

    let readBody (request: HttpRequestMessage) =
        match request.Content with
        | null -> ""
        | content -> content.ReadAsStringAsync().Result

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let uri = request.RequestUri
        let method = request.Method.Method
        server.Requests.Add((method, Uri.UnescapeDataString uri.PathAndQuery))
        let query = HttpUtility.ParseQueryString uri.Query
        let path = segments uri
        let body = readBody request

        let result: HttpResponseMessage =
            if uri.AbsolutePath = tokenPath then
                let form =
                    let parsed = HttpUtility.ParseQueryString body
                    parsed.AllKeys |> Seq.map (fun k -> k, parsed[k]) |> Map.ofSeq

                match server.Token form with
                | Some(access, refresh) ->
                    respond
                        HttpStatusCode.OK
                        (sprintf
                            """{"access_token":"%s","refresh_token":"%s","expires_in":3600,"token_type":"Bearer"}"""
                            access
                            refresh)
                | None ->
                    respond
                        HttpStatusCode.BadRequest
                        """{"error":"invalid_grant","error_description":"stale refresh token"}"""
            else
                let bearer =
                    match request.Headers.Authorization with
                    | null -> ""
                    | auth -> auth.Parameter

                if not (server.IsAuthorised bearer) then
                    graphError HttpStatusCode.Unauthorized "InvalidAuthenticationToken"
                else
                    match method, path with
                    // GET /v1.0/me/calendars?$top=1 — the health probe.
                    | "GET", [ "v1.0"; "me"; "calendars" ] ->
                        respond HttpStatusCode.OK (sprintf """{"value":[{"id":"%s"}]}""" CalendarId)

                    | "GET", [ "v1.0"; "me"; "calendars"; cal ] ->
                        if calendarExists cal then
                            respond
                                HttpStatusCode.OK
                                (sprintf """{"id":"%s","name":"Work","canEdit":%b}""" cal (cal = CalendarId))
                        else
                            graphError HttpStatusCode.NotFound "ErrorItemNotFound"

                    | "GET", [ "v1.0"; "me"; "calendars"; cal; "calendarView"; "delta" ] when calendarExists cal ->
                        let baseUrl =
                            sprintf "%s/v1.0/me/calendars/%s/calendarView/delta" Endpoint (Uri.EscapeDataString cal)

                        let windowStart = DateTimeOffset.Parse query["startDateTime"]
                        let windowEnd = DateTimeOffset.Parse query["endDateTime"]

                        match query["$deltatoken"] with
                        | null ->
                            let items =
                                server.Events.Values
                                |> Seq.filter (fun e -> overlaps e windowStart windowEnd)
                                |> Seq.sortBy (fun e -> e["id"].GetValue<string>())
                                |> Seq.collect deltaItems
                                |> List.ofSeq

                            pageOf baseUrl query items
                        | _ when server.DeltaExpired -> graphError HttpStatusCode.Gone "SyncStateNotFound"
                        | token ->
                            let changedIds =
                                server.ChangesAfter(int token)
                                |> List.map (fun (_, id, _) -> id)
                                |> List.distinct

                            let items =
                                changedIds
                                |> List.collect (fun id ->
                                    match server.Events.TryGetValue id with
                                    | true, e when overlaps e windowStart windowEnd -> deltaItems e
                                    | true, _ -> []
                                    | false, _ -> [
                                        JsonNode.Parse(sprintf """{"id":"%s","@removed":{"reason":"deleted"}}""" id)
                                      ])

                            pageOf baseUrl query items

                    // Lookup by the booking-id extended property.
                    | "GET", [ "v1.0"; "me"; "calendars"; cal; "events" ] when calendarExists cal ->
                        let filter = query["$filter"]
                        let m = Regex.Match(filter, "ep/value eq '((?:[^']|'')*)'\)")
                        let wanted = m.Groups[1].Value.Replace("''", "'")

                        let matches =
                            server.Events.Values
                            |> Seq.filter (fun e ->
                                match e["singleValueExtendedProperties"] with
                                | :? JsonArray as props ->
                                    props
                                    |> Seq.exists (fun p ->
                                        p["id"].GetValue<string>() = BookingIdPropertyId
                                        && p["value"].GetValue<string>() = wanted)
                                | _ -> false)
                            |> Seq.map (fun e -> sprintf """{"id":"%s"}""" (e["id"].GetValue<string>()))
                            |> String.concat ","

                        respond HttpStatusCode.OK (sprintf """{"value":[%s]}""" matches)

                    | "GET", [ "v1.0"; "me"; "calendars"; _; "events"; id ] ->
                        match server.Events.TryGetValue id with
                        | true, e -> respond HttpStatusCode.OK (e.ToJsonString())
                        | _ -> graphError HttpStatusCode.NotFound "ErrorItemNotFound"

                    | "POST", [ "v1.0"; "me"; "calendars"; cal; "events" ] ->
                        if cal <> CalendarId then
                            graphError HttpStatusCode.NotFound "ErrorItemNotFound"
                        else
                            let created = JsonNode.Parse(body).AsObject()

                            let transaction =
                                match created["transactionId"] with
                                | null -> None
                                | t -> Some(t.GetValue<string>())

                            match
                                transaction
                                |> Option.bind (fun t ->
                                    match server.Transactions.TryGetValue t with
                                    | true, id -> Some id
                                    | _ -> None)
                            with
                            | Some existing -> respond HttpStatusCode.Created (server.Events[existing].ToJsonString())
                            | None ->
                                let id = server.NextEventId()
                                created["id"] <- JsonValue.Create id
                                created["iCalUId"] <- JsonValue.Create("040000008200E00074C5B7101A82E008" + id)
                                created["lastModifiedDateTime"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString "o")

                                created["organizer"] <-
                                    JsonNode.Parse
                                        """{"emailAddress":{"name":"Owner","address":"owner@contoso.test"}}"""

                                server.Events[id] <- created

                                match transaction with
                                | Some t -> server.Transactions[t] <- id
                                | None -> ()

                                server.Record(id, false)
                                respond HttpStatusCode.Created (created.ToJsonString())

                    | "PATCH", [ "v1.0"; "me"; "calendars"; _; "events"; id ] ->
                        match server.Events.TryGetValue id with
                        | true, e ->
                            let patch = JsonNode.Parse(body).AsObject()

                            for KeyValue(k, v) in List.ofSeq patch do
                                match v with
                                | null -> e.Remove k |> ignore
                                | v -> e[k] <- JsonNode.Parse(v.ToJsonString())

                            e["lastModifiedDateTime"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString "o")
                            server.Record(id, false)
                            respond HttpStatusCode.OK (e.ToJsonString())
                        | _ -> graphError HttpStatusCode.NotFound "ErrorItemNotFound"

                    | "DELETE", [ "v1.0"; "me"; "calendars"; _; "events"; id ] ->
                        if server.Events.Remove id then
                            server.Record(id, true)
                            respond HttpStatusCode.NoContent ""
                        else
                            graphError HttpStatusCode.NotFound "ErrorItemNotFound"

                    | "POST", [ "v1.0"; "subscriptions" ] ->
                        let subscription = JsonNode.Parse(body).AsObject()
                        let notificationUrl = subscription["notificationUrl"].GetValue<string>()
                        let validationToken = "validation token " + Guid.NewGuid().ToString("N")

                        // Graph validates the endpoint BEFORE it creates the
                        // subscription, and refuses unless the token comes back.
                        let validated =
                            match server.NotificationEndpoint with
                            | None -> false
                            | Some post ->
                                let status, contentType, echoed =
                                    post
                                        (notificationUrl + "&validationToken=" + Uri.EscapeDataString validationToken)
                                        ""
                                    |> Async.RunSynchronously

                                status = 200 && echoed = validationToken && contentType.StartsWith "text/plain"

                        if not validated then
                            graphError HttpStatusCode.BadRequest "ValidationError"
                        else
                            let id = "sub-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                            subscription["id"] <- JsonValue.Create id
                            server.Subscriptions[id] <- subscription
                            respond HttpStatusCode.Created (subscription.ToJsonString())

                    | "PATCH", [ "v1.0"; "subscriptions"; id ] ->
                        match server.Subscriptions.TryGetValue id with
                        | true, s ->
                            let patch = JsonNode.Parse(body).AsObject()
                            s["expirationDateTime"] <- JsonNode.Parse(patch["expirationDateTime"].ToJsonString())
                            respond HttpStatusCode.OK (s.ToJsonString())
                        | _ -> graphError HttpStatusCode.NotFound "ResourceNotFound"

                    | "DELETE", [ "v1.0"; "subscriptions"; id ] ->
                        if server.Subscriptions.Remove id then
                            respond HttpStatusCode.NoContent ""
                        else
                            graphError HttpStatusCode.NotFound "ResourceNotFound"

                    | _ -> graphError HttpStatusCode.BadRequest (sprintf "unrouted %s %s" method uri.AbsolutePath)

        Task.FromResult result

// ─── Substrate doubles ──────────────────────────────────────────────

/// A dictionary `ISecretStore` — the bridge, the refresher and the
/// OAuth substrate all read and write through it.
type MemorySecretStore() =
    let values = Dictionary<string * string, string>()

    member _.Values = values

    member _.TryGet(scopeId: string, key: string) =
        match values.TryGetValue((scopeId, key)) with
        | true, v -> Some v
        | _ -> None

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            return
                match values.TryGetValue((scopeId, key)) with
                | true, v -> Some v
                | _ -> None
        }

        member _.SetSecret(scopeId, key, value) = async {
            values[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            values.Remove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                values.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// The refresher schedules its periodic job through `IJobScheduler`; the
/// tests drive refresh through `RefreshNow`, so the scheduler accepts and
/// forgets.
let private nullScheduler =
    { new IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async { return Ok() }
        member _.Schedule(_) = async { return Ok(Guid.NewGuid()) }
        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs(_) = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }
        member _.TriggerOnce(_, _, _) = async { return Ok() }
        member _.NotifyEventWritten(_, _, _) = async { return () }
    }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private nullAudit =
    { new IAuditLog with
        member _.Record(_, _) = async { return () }
        member _.GetAuditTrail(_, _, _) = async { return [] }
    }

let private settings: MicrosoftGraphCalendarSettings = {
    MicrosoftGraphCalendarSettings.defaults with
        ClientId = "client-id-value"
        EndpointOverride = Some Endpoint
        // Wide, so the pack's 2026 fixtures stay inside the window
        // whenever the suite runs.
        PullWindowDays = 3650
        PullLookbackDays = 3650
}

let private webhookSettings = {
    settings with
        NotificationUrl = Some NotificationBase
}

/// Everything one test needs, built fresh.
type private Fixture = {
    Server: StubMicrosoft
    Secrets: MemorySecretStore
    Refresher: IOAuthTokenRefresher
    Bridge: MicrosoftGraphCalendarBridge
    Link: CalendarLinkRef
}

let private seedConnection (secrets: MemorySecretStore) (scopeId: string) (userId: string) =
    let store = secrets :> ISecretStore

    store.SetSecret(scopeId, refreshTokenKey (connectionId userId), "rt-0")
    |> Async.RunSynchronously
    |> ignore

    store.SetSecret(scopeId, SecretKeys.ClientSecret, ClientSecretValue)
    |> Async.RunSynchronously
    |> ignore

    store.SetSecret(scopeId, SecretKeys.WebhookSecret, WebhookSecretValue)
    |> Async.RunSynchronously
    |> ignore

let private fixtureWith (bridgeSettings: MicrosoftGraphCalendarSettings) : Fixture =
    let server = StubMicrosoft()
    let secrets = MemorySecretStore()

    let refresher =
        InProcessOAuthTokenRefresher.create
            nullScheduler
            (secrets :> ISecretStore)
            nullAudit
            (NoOpMetricsSink() :> IMetricsSink)
            (NoOpRateLimiter() :> IRateLimiter)
            (new HttpClient(new StubHandler(server)))
            silentLogger
        :> IOAuthTokenRefresher

    let link: CalendarLinkRef = {
        ScopeId = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        ResourceId = "room-101"
        ExternalCalendarId = CalendarId
        UserId = "alice"
    }

    seedConnection secrets link.ScopeId link.UserId

    let bridge =
        MicrosoftGraphCalendarBridge(secrets, refresher, bridgeSettings, new StubHandler(server))

    {
        Server = server
        Secrets = secrets
        Refresher = refresher
        Bridge = bridge
        Link = link
    }

let private editDefaults: Booking = {
    ICalendarBridgeContract.makeBooking "" "" DateTimeOffset.MinValue with
        Title = ""
        Metadata = Map.empty
}

/// An out-of-band edit, as an Outlook user makes one: the NATIVE fields
/// change and the ToolUp snapshot property is left exactly as it was.
let private externalEdit
    (server: StubMicrosoft)
    (eventId: string)
    (transform: Booking -> Booking)
    (at: DateTimeOffset)
    =
    match server.Events.TryGetValue eventId with
    | false, _ -> failwithf "stub Graph has no event %s" eventId
    | true, stored ->
        match graphEventToBooking editDefaults (stored.ToJsonString()) with
        | Error e -> failwithf "stub event %s does not map: %s" eventId e
        | Ok None -> failwithf "stub event %s is cancelled" eventId
        | Ok(Some current) ->
            let edited = transform current.Booking
            let rendered = JsonNode.Parse(bookingToGraphEvent edited false).AsObject()

            for field in [ "subject"; "start"; "end"; "location"; "attendees" ] do
                stored[field] <- JsonNode.Parse(rendered[field].ToJsonString())

            match rendered["recurrence"] with
            | null -> stored.Remove "recurrence" |> ignore
            | r -> stored["recurrence"] <- JsonNode.Parse(r.ToJsonString())

            stored["lastModifiedDateTime"] <- JsonValue.Create(at.ToString "o")
            server.Record(eventId, false)

let private harness () : ICalendarBridgeContract.BridgeHarness =
    let f = fixtureWith settings

    {
        Bridge = f.Bridge
        Link = f.Link
        MissingCalendarId = "AAMkCal-nowhere=="
        ExternalEdit = externalEdit f.Server
    }

// ─── The notification route, in-process ─────────────────────────────

/// Request services carrying the sync engine, as the deployment's
/// container does once `SchedulingCompose` has registered it.
let private servicesWith (sync: ICalendarSync) : IServiceProvider =
    ServiceCollection().AddLogging().AddSingleton<ICalendarSync>(sync).BuildServiceProvider() :> IServiceProvider

/// POST one request through a Giraffe handler and read the response.
let private invoke
    (services: IServiceProvider)
    (route: HttpHandler)
    (url: string)
    (body: string)
    : Async<int * string * string> =
    async {
        let uri = Uri url
        let ctx = DefaultHttpContext()
        ctx.RequestServices <- services
        ctx.Request.Method <- "POST"
        ctx.Request.Path <- PathString uri.AbsolutePath
        ctx.Request.QueryString <- QueryString uri.Query
        ctx.Request.ContentType <- "application/json"
        ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
        let responseBody = new MemoryStream()
        ctx.Response.Body <- responseBody
        let! _ = route (fun c -> Task.FromResult(Some c)) ctx |> Async.AwaitTask
        responseBody.Position <- 0L
        use reader = new StreamReader(responseBody)
        let text = reader.ReadToEnd()

        let contentType =
            match ctx.Response.ContentType with
            | null -> ""
            | ct -> ct

        return ctx.Response.StatusCode, contentType, text
    }

type private SyncFixture = {
    F: Fixture
    Sync: ICalendarSync
    Scheduler: IBookingScheduler
    Actor: EntityPrincipal
}

/// A sync engine over the bridge, the resource registered, and the stub's
/// notification endpoint wired to the real route.
let private syncFixture (now: unit -> DateTimeOffset) = async {
    let f = fixtureWith webhookSettings
    let entityStore = InMemoryEntityStore() :> IEntityStore
    let eventStore = InMemoryEventStore() :> IEventStore
    let scheduler = BookingScheduler(entityStore, eventStore) :> IBookingScheduler

    let sync =
        CalendarSync(entityStore, scheduler, [ f.Bridge :> ICalendarBridge ], now) :> ICalendarSync

    let resource: BookableResource = {
        Id = f.Link.ResourceId
        Type = "BookableResource"
        Version = 0
        ResourceType = "Room"
        DisplayName = f.Link.ResourceId
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

    let actor = EntityPrincipal.ofPrincipal "calendar-sync-test"
    let! _ = scheduler.RegisterResource(f.Link.ScopeId, actor, resource)

    // The route as a deployment mounts it: POST at the notification path.
    let mounted =
        POST
        >=> route "/webhooks/calendar-microsoft"
        >=> handler (f.Bridge :> ICalendarBridge)

    f.Server.NotificationEndpoint <- Some(invoke (servicesWith sync) mounted)

    return
        {
            F = f
            Sync = sync
            Scheduler = scheduler
            Actor = actor
        },
        entityStore
}

let private utc (y: int) (mo: int) (d: int) (h: int) =
    DateTimeOffset(y, mo, d, h, 0, 0, TimeSpan.Zero)

let private expectOk (label: string) (result: Result<'T, BridgeError>) : 'T =
    match result with
    | Ok v -> v
    | Error e -> failtestf "%s: %s" label (BridgeError.message e)

let private defaultsFor (link: CalendarLinkRef) : Booking = {
    editDefaults with
        ResourceId = link.ResourceId
        BookedBy = link.UserId
}

/// POST a notification for `eventId` to every subscription the stub
/// holds, as Graph does. `clientState` overrides the subscription's own.
let private notify (server: StubMicrosoft) (eventId: string) (clientState: string option) = async {
    let results = ResizeArray<int>()

    for KeyValue(id, s) in List.ofSeq server.Subscriptions do
        let item = JsonObject()
        item["subscriptionId"] <- JsonValue.Create id

        item["clientState"] <-
            JsonValue.Create(clientState |> Option.defaultValue (s["clientState"].GetValue<string>()))

        item["changeType"] <- JsonValue.Create "updated"
        item["resource"] <- JsonValue.Create(sprintf "Users/u-1/Events/%s" eventId)
        item["resourceData"] <- JsonNode.Parse(sprintf """{"id":"%s"}""" eventId)
        let body = JsonObject()
        body["value"] <- JsonArray(item :> JsonNode)

        match server.NotificationEndpoint with
        | None -> failtest "no notification endpoint wired"
        | Some post ->
            let! status, _, _ = post (s["notificationUrl"].GetValue<string>()) (body.ToJsonString())
            results.Add status

    return List.ofSeq results
}

// ─── Behaviour beyond the pack ──────────────────────────────────────

let private graphTests =
    testList "Microsoft Graph specifics" [

        testAsync "a pull walks every delta page, then follows the stored deltaLink" {
            let f = fixtureWith settings
            let! _ = (f.Bridge :> ICalendarBridge).LinkResource f.Link

            for i in 1..5 do
                let! pushed =
                    (f.Bridge :> ICalendarBridge)
                        .Push(
                            f.Link,
                            ICalendarBridgeContract.makeBooking
                                (sprintf "bk-page-%d" i)
                                f.Link.ResourceId
                                (utc 2026 10 i 9),
                            None
                        )

                expectOk "push" pushed |> ignore

            f.Server.Requests.Clear()
            let! first = (f.Bridge :> ICalendarBridge).Pull(f.Link, None, defaultsFor f.Link)
            let events = expectOk "fresh pull" first
            Expect.equal (List.length events) 5 "every event came back across the pages"

            let deltaPages =
                f.Server.Requests
                |> Seq.filter (fun (_, u) -> u.Contains "calendarView/delta")
                |> Seq.length

            Expect.equal deltaPages 3 "five events at two per page is three delta pages"

            let edited = events |> List.find (fun e -> e.Booking.Id = "bk-page-3")

            externalEdit
                f.Server
                edited.ExternalEventId
                (fun b -> { b with Title = "Moved in Outlook" })
                (utc 2026 9 30 12)

            f.Server.Requests.Clear()

            let! second = (f.Bridge :> ICalendarBridge).Pull(f.Link, None, defaultsFor f.Link)
            let changed = expectOk "incremental pull" second

            Expect.equal (changed |> List.map _.Booking.Id) [ "bk-page-3" ] "only the edited event came back"
            Expect.equal changed.Head.Booking.Title "Moved in Outlook" "with the Outlook edit"

            Expect.isTrue
                (f.Server.Requests |> Seq.exists (fun (_, u) -> u.Contains "$deltatoken="))
                "the second pull followed the stored deltaLink"

            Expect.isFalse
                (f.Server.Requests
                 |> Seq.exists (fun (_, u) ->
                     u.Contains "calendarView/delta?startDateTime" && not (u.Contains "$deltatoken")))
                "and did not start a fresh round"
        }

        testAsync "an expired delta cursor (410 Gone) falls back to a fresh round" {
            let f = fixtureWith settings

            let booking =
                ICalendarBridgeContract.makeBooking "bk-gone" f.Link.ResourceId (utc 2026 10 2 9)

            let! _ = (f.Bridge :> ICalendarBridge).Push(f.Link, booking, None)
            let! _ = (f.Bridge :> ICalendarBridge).Pull(f.Link, None, defaultsFor f.Link)
            f.Server.ExpireDeltaTokens()
            let! again = (f.Bridge :> ICalendarBridge).Pull(f.Link, None, defaultsFor f.Link)
            let events = expectOk "pull after expiry" again
            Expect.isTrue (events |> List.exists (fun e -> e.Booking.Id = "bk-gone")) "the fresh round saw the event"
        }

        testAsync "a recurring booking round-trips its rule, and occurrences fold onto the series" {
            let f = fixtureWith settings

            let rule: RecurrenceRule = {
                Frequency = Weekly
                Interval = 2
                ByWeekday = [ DayOfWeek.Monday; DayOfWeek.Wednesday ]
                Until = Some(utc 2026 12 31 0)
                Count = None
            }

            let booking = {
                ICalendarBridgeContract.makeBooking "bk-series" f.Link.ResourceId (utc 2026 10 5 9) with
                    Recurrence = Some rule
            }

            let! pushed = (f.Bridge :> ICalendarBridge).Push(f.Link, booking, None)
            let eventId = expectOk "push" pushed
            let stored = f.Server.Events[eventId]
            let pattern = stored["recurrence"].["pattern"]
            Expect.equal (pattern["type"].GetValue<string>()) "weekly" "a weekly pattern"
            Expect.equal (pattern["interval"].GetValue<int>()) 2 "every second week"

            Expect.equal
                (pattern["daysOfWeek"].AsArray() |> Seq.map _.GetValue<string>() |> List.ofSeq)
                [ "monday"; "wednesday" ]
                "on the rule's days"

            let range = stored["recurrence"].["range"]
            Expect.equal (range["type"].GetValue<string>()) "endDate" "bounded by an end date"

            Expect.equal
                (range["endDate"].GetValue<string>())
                "2026-12-30"
                "the exclusive Until as Graph's inclusive date"

            let! pulled = (f.Bridge :> ICalendarBridge).Pull(f.Link, None, defaultsFor f.Link)
            let events = expectOk "pull" pulled
            let series = events |> List.filter (fun e -> e.Booking.Id = "bk-series")
            Expect.equal (List.length series) 1 "two occurrences folded onto one series master"
            Expect.equal series.Head.ExternalEventId eventId "addressed by the master's id"
            Expect.equal series.Head.Booking.Recurrence (Some rule) "the exact rule came back"

            // An Outlook edit to the pattern wins over the snapshot.
            externalEdit
                f.Server
                eventId
                (fun b -> {
                    b with
                        Recurrence =
                            Some {
                                rule with
                                    Interval = 1
                                    Until = None
                                    Count = Some 6
                            }
                })
                (utc 2026 9 30 12)

            let! again = (f.Bridge :> ICalendarBridge).Pull(f.Link, None, defaultsFor f.Link)

            let edited =
                expectOk "pull after edit" again
                |> List.find (fun e -> e.Booking.Id = "bk-series")

            Expect.equal
                edited.Booking.Recurrence
                (Some {
                    rule with
                        Interval = 1
                        Until = None
                        Count = Some 6
                })
                "the native pattern, read back"
        }

        testAsync "attendees are native Graph attendees, and an Outlook attendee edit comes back" {
            let f = fixtureWith settings

            let booking =
                ICalendarBridgeContract.makeBooking "bk-attendees" f.Link.ResourceId (utc 2026 10 6 9)

            let! pushed = (f.Bridge :> ICalendarBridge).Push(f.Link, booking, None)
            let eventId = expectOk "push" pushed

            let native =
                f.Server.Events[eventId].["attendees"].AsArray()
                |> Seq.map (fun a -> a["emailAddress"].["address"].GetValue<string>())
                |> List.ofSeq

            Expect.equal native [ "bob@example.com"; "carol@example.com" ] "the mailto: addresses, as Graph attendees"

            Expect.equal
                (f.Server.Events[eventId].["location"].["displayName"].GetValue<string>())
                "Room 3; second floor, east"
                "the location, as Graph's location"

            externalEdit
                f.Server
                eventId
                (fun b -> {
                    b with
                        Metadata = b.Metadata |> Map.add iCalendar.AttendeesKey "mailto:dave@example.com"
                })
                (utc 2026 9 30 12)

            let! pulled = (f.Bridge :> ICalendarBridge).Pull(f.Link, None, defaultsFor f.Link)
            let e = expectOk "pull" pulled |> List.find (fun e -> e.Booking.Id = "bk-attendees")

            Expect.equal
                (Map.tryFind iCalendar.AttendeesKey e.Booking.Metadata)
                (Some "mailto:dave@example.com")
                "Outlook's attendee list won"

            Expect.equal
                (Map.tryFind "cost centre" e.Booking.Metadata)
                (Some "R&D / 42")
                "the snapshot still carried the key Outlook cannot edit"

            Expect.equal
                (Map.tryFind iCalendar.OrganizerKey e.Booking.Metadata)
                (Some "mailto:alice@example.com")
                "and the organiser Graph cannot express"
        }

        testAsync "a push with no known event id finds the event by booking id instead of duplicating it" {
            let f = fixtureWith settings

            let booking =
                ICalendarBridgeContract.makeBooking "bk-lost-id" f.Link.ResourceId (utc 2026 10 7 9)

            let! first = (f.Bridge :> ICalendarBridge).Push(f.Link, booking, None)
            let firstId = expectOk "first push" first
            // The engine lost the id (a crash between the push and its
            // bookkeeping): the re-push must address the same event.
            let! second = (f.Bridge :> ICalendarBridge).Push(f.Link, { booking with Title = "Again" }, None)
            let secondId = expectOk "re-push" second
            Expect.equal secondId firstId "the same Graph event"
            Expect.equal f.Server.Events.Count 1 "and still exactly one"
        }

        testAsync "a read-only calendar refuses the link" {
            let f = fixtureWith settings

            match!
                (f.Bridge :> ICalendarBridge).LinkResource {
                    f.Link with
                        ExternalCalendarId = ReadOnlyCalendarId
                }
            with
            | Error(ExternalRejected(403, _)) -> ()
            | other -> failtestf "expected a 403 rejection, got %A" other
        }

        testAsync "429 is RateLimited and carries Graph's Retry-After" {
            let throttling =
                { new HttpMessageHandler() with
                    member _.SendAsync(_, _) =
                        let response = new HttpResponseMessage(enum<HttpStatusCode> 429)
                        response.Headers.RetryAfter <- Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds 17.0)

                        response.Content <-
                            new StringContent("""{"error":{"code":"TooManyRequests","message":"slow down"}}""")

                        Task.FromResult response
                }

            let f = fixtureWith settings
            // Mint a token through the real stub first, then throttle Graph.
            let! _ = (f.Bridge :> ICalendarBridge).LinkResource f.Link

            let throttled =
                MicrosoftGraphCalendarBridge(f.Secrets, f.Refresher, settings, throttling) :> ICalendarBridge

            match! throttled.LinkResource f.Link with
            | Error(RateLimited(Some after)) -> Expect.equal after (TimeSpan.FromSeconds 17.0) "the provider's own hint"
            | other -> failtestf "expected RateLimited, got %A" other
        }
    ]

// ─── Tokens: the Phase 10h refresher end to end ─────────────────────

let private tokenTests =
    testList "tokens through IOAuthTokenRefresher" [

        testAsync "the first call refreshes through the refresher, and the rotated refresh token is persisted" {
            let f = fixtureWith settings
            let! linked = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            expectOk "link" linked
            Expect.equal f.Server.IssuedTokens 1 "one refresh minted the first access token"

            let key = refreshTokenKey (connectionId f.Link.UserId)

            Expect.equal
                (f.Secrets.TryGet(f.Link.ScopeId, key))
                (Some "rt-1")
                "the rotated refresh token was written back"

            let! descriptor = f.Refresher.GetDescriptor(FlowName, connectionId f.Link.UserId)
            Expect.isSome descriptor "the connection's descriptor is registered with the refresher"
        }

        testAsync "a cached token is reused, and an expired one is refreshed with the ROTATED refresh token" {
            let f = fixtureWith settings
            let! _ = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            let! _ = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            Expect.equal f.Server.IssuedTokens 1 "the second call reused the cached access token"

            let descriptor =
                refreshDescriptor settings f.Link.ScopeId (connectionId f.Link.UserId)

            let store = f.Secrets :> ISecretStore

            let! _ =
                store.SetSecret(
                    f.Link.ScopeId,
                    OAuthRefreshDescriptor.accessExpiryKey descriptor,
                    DateTimeOffset.UtcNow.AddSeconds(30.0).ToString "o"
                )

            let! linked = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            expectOk "link after expiry" linked
            Expect.equal f.Server.IssuedTokens 2 "a nearly-expired token was refreshed"

            let lastForm = f.Server.TokenRequests |> Seq.last
            Expect.equal (Map.tryFind "refresh_token" lastForm) (Some "rt-1") "the refresh presented the rotated token"

            Expect.equal
                (Map.tryFind "client_secret" lastForm)
                (Some ClientSecretValue)
                "authenticated with the stored client secret"
        }

        testAsync "a 401 on a cached token forces one refresh and re-sends" {
            let f = fixtureWith settings
            let! _ = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            f.Server.InvalidateAccessTokens()
            let! linked = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            expectOk "link after the token was revoked upstream" linked
            Expect.equal f.Server.IssuedTokens 2 "the 401 minted a new token"
        }

        testAsync "no connection is an authentication failure naming the data source to connect" {
            let f = fixtureWith settings
            let stranger = { f.Link with UserId = "mallory" }

            match! (f.Bridge :> ICalendarBridge).LinkResource stranger with
            | Error(AuthenticationFailed message) ->
                Expect.stringContains message (connectionId "mallory") "names the connection id"
                Expect.stringContains message DataSourceKind "and the Kind to connect it under"
            | other -> failtestf "expected AuthenticationFailed, got %A" other
        }

        testAsync "the flow builds a PKCE authorize URL against the v2.0 endpoint" {
            let flow =
                createFlow (new HttpClient(new StubHandler(StubMicrosoft()))) (MemorySecretStore()) None settings

            let ctx = OAuthFlowContext.forDataSource "team-a" (connectionId "alice") None

            let! url =
                flow.BuildAuthorizeUrl(
                    ctx,
                    "state-1",
                    "https://app.invalid/api/oauth/microsoft-graph-calendar/callback",
                    Some { Challenge = "chal"; Method = "S256" }
                )

            match url with
            | Error e -> failtestf "authorize URL refused: %s" (OAuthError.toMessage e)
            | Ok u ->
                Expect.stringStarts u (Endpoint + "/common/oauth2/v2.0/authorize?") "the v2.0 authorize endpoint"
                Expect.stringContains u "code_challenge=chal" "carries the PKCE challenge"
                Expect.stringContains u "offline_access" "asks for a refresh token"

                Expect.stringContains
                    u
                    (Uri.EscapeDataString "https://graph.microsoft.com/Calendars.ReadWrite")
                    "and the calendar scope"

                Expect.equal flow.Name FlowName "the flow name the data-ingestion admin derives from the Kind"
        }
    ]

// ─── Subscriptions through the notification route ───────────────────

let private subscriptionTests =
    testList "subscriptions through the notification route" [

        testAsync "linking opens a subscription, and the validation handshake is answered by the route" {
            let! fixture, _ = syncFixture (fun () -> utc 2026 10 1 12)
            let f = fixture.F
            let! linked = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            expectOk "link" linked
            Expect.equal f.Server.Subscriptions.Count 1 "Graph created the subscription after validating the endpoint"
            let subscription = f.Server.Subscriptions.Values |> Seq.head

            Expect.equal
                (subscription["clientState"].GetValue<string>())
                (clientStateFor WebhookSecretValue f.Link.ScopeId CalendarId)
                "the clientState proves the link's scope and calendar"

            Expect.equal
                (subscription["resource"].GetValue<string>())
                (sprintf "me/calendars/%s/events" CalendarId)
                "on the linked calendar's events"

            // Re-linking renews in place rather than opening a second one.
            let! relinked = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            expectOk "re-link" relinked
            Expect.equal f.Server.Subscriptions.Count 1 "renewed, not duplicated"
        }

        testAsync "the handshake is answered as text/plain, verbatim" {
            let! fixture, _ = syncFixture (fun () -> utc 2026 10 1 12)
            let post = fixture.F.Server.NotificationEndpoint.Value
            let! status, contentType, body = post (NotificationBase + "?validationToken=a%20token%2Bwith%2Fsymbols") ""
            Expect.equal status 200 "200"
            Expect.stringStarts contentType "text/plain" "text/plain"
            Expect.equal body "a token+with/symbols" "the decoded token, verbatim"
        }

        testAsync "a notification through the route triggers a delta pull that applies the Outlook edit" {
            let pinned = utc 2026 10 1 12
            let! fixture, _ = syncFixture (fun () -> pinned)
            let f = fixture.F

            match!
                fixture.Sync.LinkResource(
                    f.Link.ScopeId,
                    f.Link.ResourceId,
                    KindName,
                    CalendarId,
                    f.Link.UserId,
                    ExternalWins,
                    fixture.Actor
                )
            with
            | Error e -> failtestf "link refused: %s" (CalendarSyncError.message e)
            | Ok _ -> ()

            let booking =
                ICalendarBridgeContract.makeBooking "bk-notified" f.Link.ResourceId (utc 2026 10 8 9)

            let! booked = fixture.Scheduler.Book(f.Link.ScopeId, booking, fixture.Actor.Principal)

            let saved =
                match booked with
                | Ok b -> b
                | Error e -> failtestf "booking refused: %A" e

            match! fixture.Sync.PushBooking(f.Link.ScopeId, saved, fixture.Actor) with
            | Ok outcome -> Expect.equal outcome.Pushed 1 "mirrored out"
            | Error e -> failtestf "push refused: %s" (CalendarSyncError.message e)

            // Prime the cursor the way the first notification would.
            let! _ = (f.Bridge :> ICalendarBridge).Pull(f.Link, None, defaultsFor f.Link)
            let eventId = f.Server.Events.Keys |> Seq.head
            externalEdit f.Server eventId (fun b -> { b with Title = "Renamed in Outlook" }) (pinned.AddHours 1.0)
            f.Server.Requests.Clear()

            let! statuses = notify f.Server eventId None
            Expect.equal statuses [ 202 ] "the route accepted the verified notification"

            Expect.isTrue
                (f.Server.Requests |> Seq.exists (fun (_, u) -> u.Contains "$deltatoken="))
                "the notification drove a delta pull from the stored cursor"

            match! fixture.Scheduler.GetBooking(f.Link.ScopeId, saved.Id) with
            | Some local -> Expect.equal local.Title "Renamed in Outlook" "the Outlook edit was applied locally"
            | None -> failtest "the local booking vanished"
        }

        testAsync "a request naming no link is refused without touching the bridge" {
            let! fixture, _ = syncFixture (fun () -> utc 2026 10 1 12)
            let post = fixture.F.Server.NotificationEndpoint.Value
            fixture.F.Server.Requests.Clear()
            let! status, _, _ = post NotificationBase """{"value":[]}"""
            Expect.equal status 400 "no scope / calendar in the URL"
            Expect.isEmpty fixture.F.Server.Requests "nothing reached Graph"
        }

        testAsync "a notification whose clientState does not verify is rejected and pulls nothing" {
            let! fixture, _ = syncFixture (fun () -> utc 2026 10 1 12)
            let f = fixture.F
            let! _ = (f.Bridge :> ICalendarBridge).LinkResource f.Link

            let booking =
                ICalendarBridgeContract.makeBooking "bk-spoofed" f.Link.ResourceId (utc 2026 10 9 9)

            let! pushed = (f.Bridge :> ICalendarBridge).Push(f.Link, booking, None)
            let eventId = expectOk "push" pushed
            f.Server.Requests.Clear()

            let forged = clientStateFor "not-the-secret" f.Link.ScopeId CalendarId
            let! statuses = notify f.Server eventId (Some forged)
            Expect.equal statuses [ 401 ] "fail closed"

            Expect.isFalse
                (f.Server.Requests |> Seq.exists (fun (_, u) -> u.Contains "calendarView"))
                "an unverified notification reached no pull"
        }

        testAsync "a genuine notification replayed against another scope's URL does not verify" {
            let! fixture, _ = syncFixture (fun () -> utc 2026 10 1 12)
            let f = fixture.F
            let! _ = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            let subscription = f.Server.Subscriptions.Values |> Seq.head
            let genuine = subscription["clientState"].GetValue<string>()
            // The attacker re-points the genuine body at a different scope —
            // one that HAS a webhook secret, so only the HMAC stands between.
            let! _ = (f.Secrets :> ISecretStore).SetSecret("team-other", SecretKeys.WebhookSecret, WebhookSecretValue)

            subscription["notificationUrl"] <-
                JsonValue.Create(notificationUrlFor NotificationBase "team-other" CalendarId)

            let! statuses = notify f.Server "AAMkEvt001==" (Some genuine)
            Expect.equal statuses [ 401 ] "the HMAC binds the scope"
        }

        testAsync "unlinking deletes the subscription" {
            let! fixture, _ = syncFixture (fun () -> utc 2026 10 1 12)
            let f = fixture.F
            let! _ = (f.Bridge :> ICalendarBridge).LinkResource f.Link
            Expect.equal f.Server.Subscriptions.Count 1 "open"
            let! unlinked = (f.Bridge :> ICalendarBridge).UnlinkResource f.Link
            expectOk "unlink" unlinked
            Expect.equal f.Server.Subscriptions.Count 0 "deleted at Graph"
            let! again = (f.Bridge :> ICalendarBridge).UnlinkResource f.Link
            expectOk "second unlink" again
        }

        testAsync "the renewal job re-creates a subscription Graph dropped" {
            let! fixture, entityStore = syncFixture (fun () -> utc 2026 10 1 12)
            let f = fixture.F

            match!
                fixture.Sync.LinkResource(
                    f.Link.ScopeId,
                    f.Link.ResourceId,
                    KindName,
                    CalendarId,
                    f.Link.UserId,
                    ExternalWins,
                    fixture.Actor
                )
            with
            | Error e -> failtestf "link refused: %s" (CalendarSyncError.message e)
            | Ok _ -> ()

            let before = f.Server.Subscriptions.Keys |> Seq.head
            f.Server.Subscriptions.Clear()

            let job =
                MicrosoftGraphSubscriptionRenewalJobHandler(f.Bridge, fixture.Sync, entityStore) :> IJobHandler

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = f.Link.ScopeId
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
                Attempt = 1
                Trigger = Trigger.CronTrigger DefaultRenewalCron
                TriggerSource = ScheduledByCron
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = ""
                DeadLetterDestination = None
            }

            let! result = job.Execute ctx
            Expect.equal result JobResult.Success "renewed"
            Expect.equal f.Server.Subscriptions.Count 1 "a subscription exists again"

            Expect.notEqual
                (f.Server.Subscriptions.Keys |> Seq.head)
                before
                "a new one, created after the old was dropped"
        }

        testAsync "a failed renewal falls back to a delta pull on the same tick" {
            let! fixture, entityStore = syncFixture (fun () -> utc 2026 10 1 12)
            let f = fixture.F

            match!
                fixture.Sync.LinkResource(
                    f.Link.ScopeId,
                    f.Link.ResourceId,
                    KindName,
                    CalendarId,
                    f.Link.UserId,
                    ExternalWins,
                    fixture.Actor
                )
            with
            | Error e -> failtestf "link refused: %s" (CalendarSyncError.message e)
            | Ok _ -> ()

            // The endpoint is unreachable: Graph can no longer validate it.
            f.Server.Subscriptions.Clear()
            f.Server.NotificationEndpoint <- None
            f.Server.Requests.Clear()

            let job =
                MicrosoftGraphSubscriptionRenewalJobHandler(f.Bridge, fixture.Sync, entityStore) :> IJobHandler

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = f.Link.ScopeId
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
                Attempt = 1
                Trigger = Trigger.CronTrigger DefaultRenewalCron
                TriggerSource = ScheduledByCron
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = ""
                DeadLetterDestination = None
            }

            let! result = job.Execute ctx

            match result with
            | JobResult.PermanentFailure message ->
                Expect.stringContains message "renewal failed" "names the failed renewal"
            | other -> failtestf "expected the failed renewal to be reported, got %A" other

            Expect.isTrue
                (f.Server.Requests |> Seq.exists (fun (_, u) -> u.Contains "calendarView/delta"))
                "the link was pulled instead"
        }

        test "compose registers the bridge and, with notifications on, the renewal job" {
            let f = fixtureWith webhookSettings

            let composed =
                ToolUp.Calendar.MicrosoftGraphSubscriptions.compose
                    f.Bridge
                    [ "team-a" ]
                    (SchedulingServerApp.create ())

            Expect.equal (composed.CalendarBridges |> List.map _.Kind) [ KindName ] "the bridge is composed"
            Expect.isSome composed.Base.Extensions.ServiceConfig "the renewal hosted service is registered"

            let polling =
                ToolUp.Calendar.MicrosoftGraphSubscriptions.compose
                    (fixtureWith settings).Bridge
                    [ "team-a" ]
                    (SchedulingServerApp.create ())

            Expect.isNone polling.Base.Extensions.ServiceConfig "a polling-only bridge adds nothing (GP 13)"
        }
    ]

// ─── Health ─────────────────────────────────────────────────────────

let private healthTests =
    testList "health" [
        testAsync "healthy with a working connection" {
            let f = fixtureWith settings

            let probe =
                MicrosoftGraphCalendarBridgeHealth(
                    f.Secrets,
                    f.Refresher,
                    settings,
                    f.Link.ScopeId,
                    f.Link.UserId,
                    new StubHandler(f.Server)
                )
                :> IHealthCheck

            let! status = probe.Check()
            Expect.equal status Healthy "Graph answered with the connection's token"
        }

        testAsync "unhealthy with no connection" {
            let f = fixtureWith settings

            let probe =
                MicrosoftGraphCalendarBridgeHealth(
                    f.Secrets,
                    f.Refresher,
                    settings,
                    f.Link.ScopeId,
                    "nobody",
                    new StubHandler(f.Server)
                )
                :> IHealthCheck

            match! probe.Check() with
            | Unhealthy _ -> ()
            | other -> failtestf "expected Unhealthy, got %A" other
        }
    ]

// ─── The env-gated live case ────────────────────────────────────────
//
// Runs only when every `TOOLUP_MSGRAPH_CALENDAR_TEST_*` value is set, so
// a fresh checkout is green with no Entra registration:
//   _CLIENT_ID, _CLIENT_SECRET, _REFRESH_TOKEN (a delegated refresh token
//   with Calendars.ReadWrite + offline_access), _CALENDAR_ID, and
//   optionally _TENANT (default `common`).
// It links, pushes one event, pulls it back and cancels it — the probe
// recipe in the companion README, minus the notification half (which
// needs a public HTTPS endpoint the suite cannot provide).

let private liveEnv (suffix: string) =
    match Environment.GetEnvironmentVariable("TOOLUP_MSGRAPH_CALENDAR_TEST_" + suffix) with
    | null
    | "" -> None
    | v -> Some v

let private liveTests =
    match liveEnv "CLIENT_ID", liveEnv "CLIENT_SECRET", liveEnv "REFRESH_TOKEN", liveEnv "CALENDAR_ID" with
    | Some clientId, Some clientSecret, Some refreshToken, Some calendarId ->
        testList "Microsoft Graph (live)" [
            testAsync "link, push, pull and cancel against a real calendar" {
                let liveSettings = {
                    MicrosoftGraphCalendarSettings.defaults with
                        ClientId = clientId
                        Tenant = liveEnv "TENANT" |> Option.defaultValue "common"
                }

                let secrets = MemorySecretStore()
                let store = secrets :> ISecretStore

                let link: CalendarLinkRef = {
                    ScopeId = "_platform"
                    ResourceId = "live-probe"
                    ExternalCalendarId = calendarId
                    UserId = "live"
                }

                let! _ = store.SetSecret(link.ScopeId, refreshTokenKey (connectionId link.UserId), refreshToken)
                let! _ = store.SetSecret(link.ScopeId, SecretKeys.ClientSecret, clientSecret)

                let refresher =
                    InProcessOAuthTokenRefresher.create
                        nullScheduler
                        store
                        nullAudit
                        (NoOpMetricsSink() :> IMetricsSink)
                        (NoOpRateLimiter() :> IRateLimiter)
                        (new HttpClient())
                        silentLogger
                    :> IOAuthTokenRefresher

                let bridge =
                    MicrosoftGraphCalendarBridge(secrets, refresher, liveSettings) :> ICalendarBridge

                let! linked = bridge.LinkResource link
                expectOk "live link" linked

                let booking = {
                    ICalendarBridgeContract.makeBooking
                        ("live-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                        link.ResourceId
                        (DateTimeOffset.UtcNow.Date.AddDays(2.0).AddHours(9.0) |> DateTimeOffset) with
                        Metadata = Map.ofList [ "cost centre", "probe" ]
                }

                let! pushed = bridge.Push(link, booking, None)
                let eventId = expectOk "live push" pushed
                let! pulled = bridge.Pull(link, None, defaultsFor link)
                let events = expectOk "live pull" pulled
                Expect.isTrue (events |> List.exists (fun e -> e.Booking.Id = booking.Id)) "the pushed event came back"
                let! cancelled = bridge.Push(link, { booking with Status = Cancelled }, Some eventId)
                expectOk "live cancel" cancelled |> ignore
            }
        ]
    | _ ->
        testList "Microsoft Graph (live)" [
            ptestCase
                "skipped — set TOOLUP_MSGRAPH_CALENDAR_TEST_CLIENT_ID / _CLIENT_SECRET / _REFRESH_TOKEN / _CALENDAR_ID to run"
            <| fun _ -> ()
        ]

let tests =
    testList "MicrosoftGraphCalendarBridge" [
        ICalendarBridgeContract.tests "Microsoft Graph over a stub server" harness
        graphTests
        tokenTests
        subscriptionTests
        healthTests
        liveTests
    ]