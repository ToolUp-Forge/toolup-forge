// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ObservabilityHandlersTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.AlertRuleEngine
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 9x — the self-hosted observability endpoints ──────────────
//
// Pinned over real HTTP (a `TestServer` host, the shape
// `DevVersionEndpointTests` uses), against the shipped defaults of each
// source: an in-memory `ILogStore`, `InMemoryTimeSeriesStore`, and a
// status board filled by the alert engine's own `runTickObserved`.
//
//   1. **Strip mode (GP 13).** With the log store, the metrics history
//      and alert rules all off, no observability route exists — asserted
//      on `ObservabilityHandlers.routes` AND through the composed router
//      (`BuildRouteHandlers.buildRouteHandlers`), which is where a
//      mis-wired mount would actually show. Each source mounts only its
//      own data route.
//   2. **The gate.** A team Owner without the platform-admin role gets
//      403 from every endpoint.
//   3. **Each read is a projection of its source**, including the honest
//      503 when a source is on but its store is not composed.
//   4. **The board.** `observe` is the whole transition rule; the tick
//      that fills the board evaluates exactly as `runTick` does.
//
// The second list is spillover from Phase 9w: its three Datadog readback
// endpoints shipped without endpoint tests, and this file is their home.

// ─── Host ─────────────────────────────────────────────────────────────

let private json = FableConverters.create ()

let private buildHost (registrations: IServiceCollection -> unit) (routes: HttpHandler list) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .UseTestServer()
                .ConfigureServices(fun services ->
                    services.AddGiraffe() |> ignore
                    registrations services)
                .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe(choose routes))
            |> ignore)
        .Build()

/// GET each path against ONE fresh host; returns `(status, body)` per path.
let private getAll (registrations: IServiceCollection -> unit) (routes: HttpHandler list) (paths: string list) = async {
    use host = buildHost registrations routes
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()
    let results = ResizeArray<HttpStatusCode * string>()

    for path in paths do
        let! resp = client.GetAsync(path) |> Async.AwaitTask
        let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        results.Add((resp.StatusCode, text))

    do! host.StopAsync() |> Async.AwaitTask
    return List.ofSeq results
}

let private get registrations routes (path: string) = async {
    let! results = getAll registrations routes [ path ]
    return List.head results
}

let private parse<'T> (body: string) : 'T =
    JsonSerializer.Deserialize<'T>(body, json)

// ─── Fixtures ─────────────────────────────────────────────────────────

let private platformAdmin: AccessContext = {
    AccessContext.unrestricted (TeamMember("admin", "team-1")) with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

/// A team member with every module unrestricted but no platform role —
/// the caller the gate exists to refuse.
let private teamOwner: AccessContext =
    AccessContext.unrestricted (TeamMember("owner", "team-1"))

let private withAccess (access: AccessContext) (services: IServiceCollection) =
    services.AddSingleton<AccessContext>(access) |> ignore

let private also
    (first: IServiceCollection -> unit)
    (second: IServiceCollection -> unit)
    (services: IServiceCollection)
    =
    first services
    second services

/// An `ILogStore` over a list, searched with the canonical
/// `LogSearchQuery.apply` — the same fake the log-store contract pack
/// validates.
type private InMemoryLogStore() =
    let gate = obj ()
    let entries = ResizeArray<LogRecord>()

    interface ILogStore with
        member _.Append(entry: LogRecord) = async {
            lock gate (fun () ->
                entries.Add {
                    entry with
                        TimestampUtc = LogRecord.truncateToSecond entry.TimestampUtc
                })
        }

        member _.Search(query: LogSearchQuery) = async {
            return lock gate (fun () -> LogSearchQuery.apply query (List.ofSeq entries))
        }

        member _.Prune(olderThanUtc: DateTime) = async {
            return lock gate (fun () -> entries.RemoveAll(fun e -> e.TimestampUtc < olderThanUtc))
        }

let private queueRule: AlertRule = {
    Name = "queue-deep"
    Source = Metric("toolup.jobs.queued", Map.empty)
    Condition = GreaterThan 10.0
    ForDuration = TimeSpan.FromMinutes 2.0
    Severity = SystemMessageLevel.Warning
    DeliverVia = [ ViaChannel "_platform" ]
}

let private probeRule: AlertRule = {
    Name = "db-down"
    Source = HealthProbe "database"
    Condition = ProbeUnhealthy
    ForDuration = TimeSpan.FromMinutes 1.0
    Severity = SystemMessageLevel.Error
    DeliverVia = [ ViaChannel "_platform" ]
}

let private logsOn = {
    ServerConfig.defaults with
        LogStore = LogStoreMode.SqliteLogStore(LogStoreConfig.create "never-opened.db")
}

let private metricsOn = {
    ServerConfig.defaults with
        MetricsHistory = EnabledMetricsHistory MetricsHistoryConfig.defaults
}

let private alertsOn = {
    ServerConfig.defaults with
        AlertRules = [ queueRule; probeRule ]
}

let private allOn = {
    logsOn with
        MetricsHistory = metricsOn.MetricsHistory
        AlertRules = alertsOn.AlertRules
}

let private observabilityPaths = [
    "/api/observability/sources"
    "/api/observability/logs"
    "/api/observability/metrics?metric=toolup.requests.total"
    "/api/observability/alerts"
]

let private datadogPaths = [
    "/api/observability/datadog/monitors"
    "/api/observability/datadog/logs"
    "/api/observability/datadog/metric?metric=toolup.requests.total"
]

/// The route list exactly as `compose` mounts it: the real
/// `buildRouteHandlers` over `config`, every other argument the empty
/// value a minimal deployment passes.
let private composedRouter (config: ServerConfig) : HttpHandler list =
    let handlers =
        BuildRouteHandlers.buildRouteHandlers
            config
            []
            []
            ComposeExtensions.empty
            []
            None
            NoNotifications
            (ref None)
            (ref None)

    [ handlers.Router [] ]

let private now = DateTime.UtcNow

let private record (level: LogLevel) (message: string) (secondsAgo: float) (correlation: string option) : LogRecord = {
    (LogRecord.create level "toolup" message) with
        TimestampUtc = LogRecord.truncateToSecond (now.AddSeconds(-secondsAgo))
        CorrelationId = correlation
}

let private seededLogStore () : ILogStore =
    let store = InMemoryLogStore() :> ILogStore

    [
        record LogLevel.Info "request served" 30.0 (Some "req-1")
        record LogLevel.Error "payment gateway timeout" 20.0 (Some "req-2")
        record LogLevel.Warn "retrying payment" 10.0 (Some "req-2")
        record LogLevel.Error "stale entry" (3.0 * 3600.0) None
    ]
    |> List.iter (fun e -> store.Append e |> Async.RunSynchronously)

    store

let private point (minutesAgo: float) (value: float) : TimeSeriesPoint = {
    Timestamp = DateTimeOffset(now.AddMinutes(-minutesAgo))
    Value = value
}

let private seededSeriesStore () : ITimeSeriesStore =
    let store = InMemoryTimeSeriesStore() :> ITimeSeriesStore
    let scope = MetricsHistoryFlusher.ScopeId

    let append (series: string) (points: TimeSeriesPoint list) =
        store.Append(scope, series, points) |> Async.RunSynchronously |> ignore

    append "toolup.requests.total{route_class=\"api\"}" [ point 3.0 4.0; point 2.0 6.0 ]
    append "toolup.requests.total{route_class=\"web\"}" [ point 3.0 1.0; point 2.0 2.0 ]
    // A decoy sharing the prefix — must not be claimed by the metric.
    append "toolup.requests.total_bytes" [ point 3.0 999.0 ]
    // Outside the default 60-minute window.
    append "toolup.requests.total" [ point 300.0 42.0 ]
    append "toolup.requests.latency_ms.p99{route_class=\"api\"}" [ point 2.0 120.0 ]
    append "toolup.requests.latency_ms.p99{route_class=\"web\"}" [ point 2.0 480.0 ]
    store

// ─── Phase 9x ─────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 9x — self-hosted observability endpoints" [

        testList "strip mode (GP 13)" [
            test "all three sources off mounts no route" {
                Expect.isEmpty (ObservabilityHandlers.routes ServerConfig.defaults) "no source ⇒ no route"

                Expect.equal
                    (ObservabilityHandlers.sourcesOf ServerConfig.defaults)
                    {
                        Logs = false
                        Metrics = false
                        Alerts = false
                    }
                    "the defaults enable nothing"
            }

            testAsync "through the composed router, every observability path is a 404 by default" {
                let! results =
                    getAll (withAccess platformAdmin) (composedRouter ServerConfig.defaults) observabilityPaths

                for (status, _), path in List.zip results observabilityPaths do
                    Expect.equal status HttpStatusCode.NotFound (sprintf "%s must not be mounted" path)
            }

            testAsync "through the composed router, the routes ARE mounted once a source is on" {
                // The probe above is only meaningful if the same router
                // answers these paths when enabled — a 403 proves the
                // route matched and reached the gate.
                let! results = getAll (withAccess teamOwner) (composedRouter allOn) observabilityPaths

                for (status, _), path in List.zip results observabilityPaths do
                    Expect.equal status HttpStatusCode.Forbidden (sprintf "%s is mounted and gated" path)
            }

            testAsync "each source mounts only its own data route" {
                for config, mounted in
                    [
                        logsOn, "/api/observability/logs"
                        metricsOn, "/api/observability/metrics?metric=toolup.requests.total"
                        alertsOn, "/api/observability/alerts"
                    ] do
                    let! results =
                        getAll (withAccess teamOwner) (ObservabilityHandlers.routes config) observabilityPaths

                    for (status, _), path in List.zip results observabilityPaths do
                        let expected =
                            if path = mounted || path = "/api/observability/sources" then
                                HttpStatusCode.Forbidden
                            else
                                HttpStatusCode.NotFound

                        Expect.equal status expected (sprintf "%s with only %s enabled" path mounted)
            }

            testAsync "/sources reports exactly the enabled sources" {
                let! status, body =
                    get (withAccess platformAdmin) (ObservabilityHandlers.routes metricsOn) "/api/observability/sources"

                Expect.equal status HttpStatusCode.OK "admin reads the sources"

                Expect.equal
                    (parse<ObservabilitySources> body)
                    {
                        Logs = false
                        Metrics = true
                        Alerts = false
                    }
                    "only the metrics history is on"
            }

            test "the status board is registered only when a rule is declared" {
                let boardRegistered (config: ServerConfig) =
                    let services = ServiceCollection()
                    ComposeObservability.registerObservabilityReadback services config

                    services |> Seq.exists (fun d -> d.ServiceType = typeof<AlertRuleStatusBoard>)

                Expect.isFalse (boardRegistered ServerConfig.defaults) "no rules ⇒ no board"
                Expect.isFalse (boardRegistered logsOn) "a log store alone does not need one"
                Expect.isTrue (boardRegistered alertsOn) "declared rules ⇒ a board"
            }
        ]

        testAsync "a team Owner without the platform-admin role is refused everywhere" {
            let registrations =
                also (withAccess teamOwner) (fun services ->
                    services.AddSingleton<ILogStore>(seededLogStore ()) |> ignore
                    services.AddSingleton<ITimeSeriesStore>(seededSeriesStore ()) |> ignore)

            let! results = getAll registrations (ObservabilityHandlers.routes allOn) observabilityPaths

            for (status, body), path in List.zip results observabilityPaths do
                Expect.equal status HttpStatusCode.Forbidden (sprintf "%s refuses a team Owner" path)
                Expect.isFalse (body.Contains "payment") (sprintf "%s leaks nothing in its refusal" path)
        }

        testList "logs" [
            testAsync "a source that is on with no store composed answers 503" {
                let! status, _ =
                    get (withAccess platformAdmin) (ObservabilityHandlers.routes logsOn) "/api/observability/logs"

                Expect.equal status HttpStatusCode.ServiceUnavailable "the composition defect is named, not hidden"
            }

            testAsync "the default search is the last hour, newest first" {
                let registrations =
                    also (withAccess platformAdmin) (fun s -> s.AddSingleton<ILogStore>(seededLogStore ()) |> ignore)

                let! status, body = get registrations (ObservabilityHandlers.routes logsOn) "/api/observability/logs"
                Expect.equal status HttpStatusCode.OK "admin reads the logs"
                let response = parse<ObservabilityLogsResponse> body

                Expect.equal
                    (response.Records |> List.map _.Message)
                    [ "retrying payment"; "payment gateway timeout"; "request served" ]
                    "the three-hour-old line is outside the default window; the rest newest first"

                Expect.equal response.WindowMinutes ObservabilityReadback.DefaultWindowMinutes "the default window"
                Expect.equal response.Limit ObservabilityReadback.DefaultLogLimit "the default page"
            }

            testAsync "text, level and correlation filters narrow the search" {
                let registrations =
                    also (withAccess platformAdmin) (fun s -> s.AddSingleton<ILogStore>(seededLogStore ()) |> ignore)

                let! results =
                    getAll registrations (ObservabilityHandlers.routes logsOn) [
                        "/api/observability/logs?q=payment&levels=error"
                        "/api/observability/logs?correlationId=req-2"
                        "/api/observability/logs?levels=Warn,bogus,Error&minutes=600"
                        "/api/observability/logs?limit=1"
                    ]

                let messages (_, body) =
                    (parse<ObservabilityLogsResponse> body).Records |> List.map _.Message

                Expect.equal (messages results[0]) [ "payment gateway timeout" ] "whole-word text AND level"

                Expect.equal
                    (messages results[1])
                    [ "retrying payment"; "payment gateway timeout" ]
                    "one request's lines"

                Expect.equal
                    (messages results[2])
                    [ "retrying payment"; "payment gateway timeout"; "stale entry" ]
                    "an unknown level name is dropped, not fatal; the wider window reaches the old line"

                Expect.equal (messages results[3]) [ "retrying payment" ] "the limit keeps the newest"
            }

            test "the query clamps the window and the page" {
                let query = ObservabilityHandlers.logQuery None [] 100_000 None None 100_000 now

                Expect.equal
                    query.SinceUtc
                    (now.AddMinutes(-(float ObservabilityReadback.MaxWindowMinutes)))
                    "a day at most"

                Expect.equal query.Limit ObservabilityReadback.MaxLogLimit "the largest page"
                Expect.isGreaterThan query.UntilUtc now "a line stamped this second is inside the half-open window"
            }
        ]

        testList "metrics" [
            testAsync "no metric named is a 400; no store composed is a 503" {
                let registrations = withAccess platformAdmin

                let! results =
                    getAll registrations (ObservabilityHandlers.routes metricsOn) [
                        "/api/observability/metrics"
                        "/api/observability/metrics?metric=toolup.requests.total"
                    ]

                Expect.equal (fst results[0]) HttpStatusCode.BadRequest "the metric is required"
                Expect.equal (fst results[1]) HttpStatusCode.ServiceUnavailable "no ITimeSeriesStore"
            }

            testAsync "a read returns every tag set of the metric, and only the metric" {
                let registrations =
                    also (withAccess platformAdmin) (fun s ->
                        s.AddSingleton<ITimeSeriesStore>(seededSeriesStore ()) |> ignore)

                let! status, body =
                    get
                        registrations
                        (ObservabilityHandlers.routes metricsOn)
                        "/api/observability/metrics?metric=toolup.requests.total"

                Expect.equal status HttpStatusCode.OK "admin reads the metric"
                let response = parse<ObservabilityMetricResponse> body

                Expect.equal
                    (response.Series |> List.map _.SeriesId)
                    [
                        "toolup.requests.total{route_class=\"api\"}"
                        "toolup.requests.total{route_class=\"web\"}"
                    ]
                    "both tag sets, ordered; the _bytes decoy excluded; the bare series has no point in the window"

                Expect.isNone response.Warning "a clean read"

                Expect.equal
                    (ObservabilityReadback.combineSeries response.Metric response.Series
                     |> List.map snd)
                    [ 5.0; 8.0 ]
                    "counter deltas of one flush sum across tag sets"
            }

            testAsync "a percentile field combines as the maximum across tag sets" {
                let registrations =
                    also (withAccess platformAdmin) (fun s ->
                        s.AddSingleton<ITimeSeriesStore>(seededSeriesStore ()) |> ignore)

                let! _, body =
                    get
                        registrations
                        (ObservabilityHandlers.routes metricsOn)
                        "/api/observability/metrics?metric=toolup.requests.latency_ms.p99"

                let response = parse<ObservabilityMetricResponse> body

                Expect.equal
                    (ObservabilityReadback.combineSeries response.Metric response.Series
                     |> List.map snd)
                    [ 480.0 ]
                    "the slowest route's p99, not a meaningless sum"
            }

            test "series ownership is the bare name or the name followed by its tag set" {
                let owns = ObservabilityReadback.isSeriesOf "toolup.requests.total"
                Expect.isTrue (owns "toolup.requests.total") "tag-free"
                Expect.isTrue (owns "toolup.requests.total{a=\"b\"}") "tagged"
                Expect.isFalse (owns "toolup.requests.total_bytes") "a longer name is a different metric"
                Expect.isFalse (owns "toolup.requests.total.p99") "a field is a different series"
            }
        ]

        testList "alerts" [
            testAsync "with no board, every declared rule is NotEvaluated, rendered as the engine renders it" {
                let! status, body =
                    get (withAccess platformAdmin) (ObservabilityHandlers.routes alertsOn) "/api/observability/alerts"

                Expect.equal status HttpStatusCode.OK "admin reads the alerts"
                let response = parse<ObservabilityAlertsResponse> body

                Expect.equal (response.Rules |> List.map _.Name) [ "queue-deep"; "db-down" ] "declaration order"

                let queue = response.Rules[0]
                Expect.equal queue.Signal "toolup.jobs.queued" "the engine's signal rendering"
                Expect.equal queue.Condition "> 10" "the engine's condition rendering"
                Expect.equal queue.ForMinutes 2.0 "the debounce window"
                Expect.equal queue.Severity "Warning" "the severity"
                Expect.equal queue.MetricName (Some "toolup.jobs.queued") "a metric rule links to its chart"
                Expect.equal queue.Observation.State AlertRuleState.NotEvaluated "no tick has run in this process"

                Expect.equal response.Rules[1].Signal "probe:database" "a probe rule"
                Expect.isNone response.Rules[1].MetricName "a probe rule has no chart"
            }

            testAsync "the board the engine fills is what the endpoint reports" {
                let board = AlertRuleStatusBoard()
                let states = ConcurrentDictionary<string, RuleState>()
                let t0 = DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc)
                let publish _ _ = async { return () }
                let readProbe _ = async { return None }

                let tick (value: float option) (minute: float) =
                    runTickObserved
                        (fun _ _ -> value)
                        readProbe
                        publish
                        [ queueRule; probeRule ]
                        states
                        board
                        (t0.AddMinutes minute)

                do! tick (Some 50.0) 0.0
                do! tick (Some 50.0) 1.0
                do! tick (Some 50.0) 2.0

                let registrations =
                    also (withAccess platformAdmin) (fun s -> s.AddSingleton<AlertRuleStatusBoard>(board) |> ignore)

                let! _, body = get registrations (ObservabilityHandlers.routes alertsOn) "/api/observability/alerts"
                let response = parse<ObservabilityAlertsResponse> body
                let queue = response.Rules[0].Observation

                Expect.equal queue.State AlertRuleState.Firing "breached for the whole ForDuration"
                Expect.equal queue.LastFiredUtc (Some(t0.AddMinutes 2.0)) "fired on the tick that completed the window"
                Expect.equal queue.BreachingSinceUtc (Some t0) "the breach began at the first tick"

                Expect.equal
                    response.Rules[1].Observation.State
                    AlertRuleState.NoData
                    "an unregistered probe has no data"
            }
        ]

        testList "the status board" [
            let t0 = DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc)
            let clear = initialState

            let breaching = {
                BreachingSince = Some t0
                Fired = false
            }

            let fired = { breaching with Fired = true }

            test "a fire and a clear are Fired flipping, stamped with the tick" {
                let afterFire =
                    observe AlertRuleObservation.notEvaluated true breaching fired (t0.AddMinutes 5.0)

                Expect.equal afterFire.State AlertRuleState.Firing "fired"
                Expect.equal afterFire.LastFiredUtc (Some(t0.AddMinutes 5.0)) "fire instant"
                Expect.isNone afterFire.LastClearedUtc "never cleared"

                let stillFiring = observe afterFire true fired fired (t0.AddMinutes 6.0)

                Expect.equal
                    stillFiring.LastFiredUtc
                    afterFire.LastFiredUtc
                    "a sustained breach does not re-stamp the fire"

                let afterClear = observe stillFiring true fired clear (t0.AddMinutes 7.0)
                Expect.equal afterClear.State AlertRuleState.Clear "recovered"
                Expect.equal afterClear.LastClearedUtc (Some(t0.AddMinutes 7.0)) "clear instant"
                Expect.equal afterClear.LastFiredUtc afterFire.LastFiredUtc "the last fire survives the clear"
                Expect.isNone afterClear.BreachingSinceUtc "no current breach"
            }

            test "a breach inside its window is Pending; a tick with no data is NoData and keeps history" {
                let pending = observe AlertRuleObservation.notEvaluated true clear breaching t0
                Expect.equal pending.State AlertRuleState.Pending "breaching, not yet fired"

                let firedObs = observe pending true breaching fired (t0.AddMinutes 5.0)
                let noData = observe firedObs false fired fired (t0.AddMinutes 6.0)
                Expect.equal noData.State AlertRuleState.NoData "nothing to read"
                Expect.equal noData.LastFiredUtc firedObs.LastFiredUtc "history carries over"
                Expect.equal noData.LastEvaluatedUtc (Some(t0.AddMinutes 6.0)) "the tick still counts as an evaluation"
            }

            testAsync "the observed tick evaluates exactly as runTick does" {
                // Same script through both entry points: the notifications
                // published must be identical, so recording observations
                // cannot have changed a fire / re-arm decision.
                let script = [
                    Some 50.0
                    Some 50.0
                    Some 50.0
                    Some 1.0
                    None
                    Some 50.0
                    Some 50.0
                    Some 50.0
                ]

                let run (observed: bool) = async {
                    let published = List<string * Notification>()
                    let publish scope n = async { published.Add((scope, n)) }
                    let states = ConcurrentDictionary<string, RuleState>()
                    let board = AlertRuleStatusBoard()
                    let readProbe _ = async { return None }

                    for i, value in List.indexed script do
                        let at = t0.AddMinutes(float i)

                        if observed then
                            do! runTickObserved (fun _ _ -> value) readProbe publish [ queueRule ] states board at
                        else
                            do! runTick (fun _ _ -> value) readProbe publish [ queueRule ] states at

                    return List.ofSeq published
                }

                let! plain = run false
                let! observed = run true
                Expect.equal observed plain "identical deliveries"
                Expect.equal (List.length plain) 2 "fired, re-armed on recovery, fired again"
            }
        ]
    ]

// ─── Phase 9w spillover — the Datadog readback endpoints ──────────────

/// A readback companion answering from fixed results, so the handler
/// path is exercised without a network.
let private fakeApi (fail: bool) =
    let failure = DatadogApiError(500, "upstream exploded")

    { new IDatadogReadbackApi with
        member _.GetMonitors _ = async {
            if fail then
                return Error failure
            else
                return
                    Ok [
                        ({
                            Id = 7L
                            Name = "error rate"
                            Status = DatadogMonitorStatus.Alert
                            LastTransitionUtc = now
                            Tags = [ "service:toolup" ]
                        }
                        : DatadogMonitorState)
                    ]
        }

        member _.SearchLogs(_, _, _) = async { if fail then return Error failure else return Ok [] }

        member _.QueryMetric(metric, tags, _, _) = async {
            if fail then
                return Error failure
            else
                let series: DatadogMetricSeries = {
                    Metric = metric
                    Points = [ now, 1.0 ]
                    Tags = tags
                }

                return Ok series
        }
    }

let private datadogRoutes =
    DatadogReadbackHandlers.routes DatadogReadbackConfig.defaults

let private datadogOn = {
    ServerConfig.defaults with
        DatadogReadback = EnabledDatadogReadback DatadogReadbackConfig.defaults
}

[<Tests>]
let datadogReadbackTests =
    testList "Phase 9w — Datadog readback endpoints (tests homed by Phase 9x)" [

        testAsync "a team Owner without the platform-admin role is refused on all three" {
            let registrations =
                also (withAccess teamOwner) (fun s -> s.AddSingleton<IDatadogReadbackApi>(fakeApi false) |> ignore)

            let! results = getAll registrations datadogRoutes datadogPaths

            for (status, _), path in List.zip results datadogPaths do
                Expect.equal status HttpStatusCode.Forbidden (sprintf "%s refuses a team Owner" path)
        }

        testAsync "the mode on with no companion composed is a 503 on all three" {
            let! results = getAll (withAccess platformAdmin) datadogRoutes datadogPaths

            for (status, body), path in List.zip results datadogPaths do
                Expect.equal status HttpStatusCode.ServiceUnavailable (sprintf "%s names the composition defect" path)
                Expect.stringContains body "IDatadogReadbackApi" "the missing seam is named"
        }

        testAsync "a Datadog failure degrades to 200, an empty payload and one warning" {
            let registrations =
                also (withAccess platformAdmin) (fun s -> s.AddSingleton<IDatadogReadbackApi>(fakeApi true) |> ignore)

            let! results = getAll registrations datadogRoutes datadogPaths

            for (status, _), path in List.zip results datadogPaths do
                Expect.equal status HttpStatusCode.OK (sprintf "%s soft-fails" path)

            let monitors = parse<DatadogMonitorsResponse> (snd results[0])
            Expect.isEmpty monitors.Monitors "no monitors on failure"
            Expect.isSome monitors.Warning "the reason is carried"
            Expect.stringContains monitors.Warning.Value "HTTP 500" "the operator-facing reason"

            let logs = parse<DatadogLogsResponse> (snd results[1])
            Expect.isEmpty logs.Events "no events on failure"
            Expect.isSome logs.Warning "the reason is carried"

            let metric = parse<DatadogMetricResponse> (snd results[2])
            Expect.isEmpty metric.Series.Points "no points on failure"
            Expect.equal metric.Series.Metric "toolup.requests.total" "the asked-for metric is echoed"
            Expect.isSome metric.Warning "the reason is carried"
        }

        testAsync "a successful read carries no warning" {
            let registrations =
                also (withAccess platformAdmin) (fun s -> s.AddSingleton<IDatadogReadbackApi>(fakeApi false) |> ignore)

            let! results = getAll registrations datadogRoutes datadogPaths
            let monitors = parse<DatadogMonitorsResponse> (snd results[0])
            Expect.equal (fst results[0]) HttpStatusCode.OK "read"
            Expect.equal (monitors.Monitors |> List.map _.Name) [ "error rate" ] "the companion's monitors"
            Expect.isNone monitors.Warning "a clean read"
        }

        testAsync "NoDatadogReadback mounts no route; EnabledDatadogReadback does" {
            let! off = getAll (withAccess platformAdmin) (composedRouter ServerConfig.defaults) datadogPaths

            for (status, _), path in List.zip off datadogPaths do
                Expect.equal status HttpStatusCode.NotFound (sprintf "%s is not mounted by default" path)

            // The same router with the mode on answers the same paths —
            // 503, because no companion is composed here — which is what
            // makes the 404 above evidence rather than a broken probe.
            let! on = getAll (withAccess platformAdmin) (composedRouter datadogOn) datadogPaths

            for (status, _), path in List.zip on datadogPaths do
                Expect.equal status HttpStatusCode.ServiceUnavailable (sprintf "%s is mounted when enabled" path)
        }
    ]