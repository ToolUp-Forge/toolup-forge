// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DatadogReadbackClientTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Observability
open ToolUp.Platform.Secrets

// ─── Phase 9w — Datadog readback contract pack ───────────────────────
//
// Drives the shipped `IDatadogReadbackApi` implementation against a
// recording `HttpMessageHandler` + a fake `ISecretStore` — the pattern
// the write-side sink's pack established. **No Datadog account is
// needed**: every assertion is about the request this companion builds
// and the response it parses, which is the whole of its contract.
//
// The assertions are deliberately about the EXACT url and body rather
// than "contains the metric name": a query that reaches Datadog
// malformed comes back 400, and a query that reaches it correctly
// formed but for the wrong window comes back 200 with the wrong data.
// Only the second failure mode is silent, so it is the one worth
// pinning precisely.

/// Records every outgoing request and replies with a scripted response.
/// The scripted body/status is per-instance rather than per-call: each
/// test drives one endpoint, so a queue would add ordering to assert
/// without adding coverage.
type private RecordingHandler(status: HttpStatusCode, body: string) =
    inherit HttpMessageHandler()

    let requests =
        ConcurrentQueue<HttpMethod * string * string * (string * string) list>()

    new() = new RecordingHandler(HttpStatusCode.OK, "[]")

    /// `(method, url, body, headers)` per request, in call order.
    member _.Recorded: (HttpMethod * string * string * (string * string) list) list =
        requests |> List.ofSeq

    member this.LastUrl: string =
        match this.Recorded |> List.tryLast with
        | Some(_, url, _, _) -> url
        | None -> ""

    member this.LastBody: string =
        match this.Recorded |> List.tryLast with
        | Some(_, _, body, _) -> body
        | None -> ""

    member this.LastHeaders: (string * string) list =
        match this.Recorded |> List.tryLast with
        | Some(_, _, _, headers) -> headers
        | None -> []

    override _.SendAsync
        (request: HttpRequestMessage, _cancellationToken: CancellationToken)
        : Task<HttpResponseMessage> =
        task {
            let! requestBody =
                if isNull request.Content then
                    Task.FromResult ""
                else
                    request.Content.ReadAsStringAsync()

            let headers =
                request.Headers
                |> Seq.map (fun header -> header.Key, String.Join(",", header.Value))
                |> List.ofSeq

            requests.Enqueue(request.Method, request.RequestUri.ToString(), requestBody, headers)

            let response = new HttpResponseMessage(status)
            response.Content <- new StringContent(body)
            return response
        }

/// Always throws on send — the transport-failure arm, which no status
/// code can produce.
type private ThrowingHandler() =
    inherit HttpMessageHandler()

    override _.SendAsync
        (_request: HttpRequestMessage, _cancellationToken: CancellationToken)
        : Task<HttpResponseMessage> =
        raise (HttpRequestException "no such host is known")

/// Counts reads so the per-call resolution can be asserted rather than
/// assumed, and can be asked to withhold either key.
type private FakeSecretStore(?withhold: string) =
    let reads = ConcurrentQueue<string>()

    member _.Reads: string list = reads |> List.ofSeq

    interface ISecretStore with
        member _.GetSecret(_scope, key) = async {
            reads.Enqueue key

            match withhold with
            | Some missing when missing = key -> return None
            | _ -> return Some(key + "-value")
        }

        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

let private settings: DatadogReadbackConfig = {
    DatadogReadbackConfig.defaults with
        DefaultTags = [ "service:toolup"; "env:prod" ]
}

let private clientOver (handler: HttpMessageHandler) (store: ISecretStore) (config: DatadogReadbackConfig) =
    Datadog.create config store (new HttpClient(handler))

/// One `from`/`to` pair every timeseries assertion reuses, fixed so the
/// expected Unix seconds are literals rather than a restatement of the
/// conversion under test.
let private windowFrom = DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)
let private windowTo = DateTime(2026, 3, 1, 13, 0, 0, DateTimeKind.Utc)

[<Tests>]
let tests =
    testList "Phase 9w — Datadog readback client" [

        testList "url construction" [
            test "monitors: the tag filter is one comma-separated, url-encoded `tags` parameter" {
                let handler = new RecordingHandler()
                let client = clientOver handler (FakeSecretStore()) settings

                client.GetMonitors [ "service:toolup"; "env:prod" ]
                |> Async.RunSynchronously
                |> ignore

                Expect.equal
                    handler.LastUrl
                    "https://api.datadoghq.com/api/v1/monitor?tags=service%3Atoolup%2Cenv%3Aprod"
                    "the monitor list url"
            }

            test "monitors: an empty filter omits the parameter rather than sending an empty one" {
                let handler = new RecordingHandler()
                let client = clientOver handler (FakeSecretStore()) settings

                client.GetMonitors [] |> Async.RunSynchronously |> ignore

                Expect.equal handler.LastUrl "https://api.datadoghq.com/api/v1/monitor" "no trailing `?tags=`"
            }

            test "metric: the query is `metric{tags}` and the window is Unix SECONDS" {
                let handler = new RecordingHandler(HttpStatusCode.OK, """{"series":[]}""")
                let client = clientOver handler (FakeSecretStore()) settings

                client.QueryMetric("toolup.requests.total", [ "service:toolup" ], windowFrom, windowTo)
                |> Async.RunSynchronously
                |> ignore

                // The braces read back unescaped because `Uri`
                // canonicalises `%7B` / `%7D` away — this is the form
                // .NET actually puts on the wire, and it is also the
                // form Datadog's own documentation uses, so do not
                // "fix" it back to the escaped spelling.
                Expect.equal
                    handler.LastUrl
                    "https://api.datadoghq.com/api/v1/query?from=1772366400&to=1772370000&query=toolup.requests.total{service%3Atoolup}"
                    "the timeseries url"
            }

            test "metric: no tags is the `{*}` wildcard, which is Datadog's spelling of no narrowing" {
                let handler = new RecordingHandler(HttpStatusCode.OK, """{"series":[]}""")
                let client = clientOver handler (FakeSecretStore()) settings

                client.QueryMetric("toolup.errors.total", [], windowFrom, windowTo)
                |> Async.RunSynchronously
                |> ignore

                Expect.stringContains handler.LastUrl "query=toolup.errors.total{%2A}" "wildcard, not empty braces"
            }

            test "logs: a POST to the search endpoint, narrowed by the configured tags, ISO-8601 bounds" {
                let handler = new RecordingHandler(HttpStatusCode.OK, """{"data":[]}""")
                let client = clientOver handler (FakeSecretStore()) settings

                client.SearchLogs("status:error", windowFrom, 25)
                |> Async.RunSynchronously
                |> ignore

                let method, url, body, _ = handler.Recorded |> List.exactlyOne

                Expect.equal method HttpMethod.Post "log search is a POST"
                Expect.equal url "https://api.datadoghq.com/api/v2/logs/events/search" "the search url"

                Expect.stringContains
                    body
                    "\"query\":\"status:error service:toolup env:prod\""
                    "the caller's query AND the deployment's default tags"

                Expect.stringContains body "\"from\":\"2026-03-01T12:00:00Z\"" "ISO-8601 lower bound, not Unix seconds"
                Expect.stringContains body "\"limit\":25" "the caller's page size"
            }

            test "logs: a page size past Datadog's own cap is clamped rather than sent" {
                let handler = new RecordingHandler(HttpStatusCode.OK, """{"data":[]}""")
                let client = clientOver handler (FakeSecretStore()) settings

                client.SearchLogs("status:error", windowFrom, 5000)
                |> Async.RunSynchronously
                |> ignore

                Expect.stringContains handler.LastBody "\"limit\":1000" "clamped to Datadog's 1000-event page cap"
            }
        ]

        testList "region routing" [
            for region, host in
                [
                    DatadogRegion.US, "api.datadoghq.com"
                    DatadogRegion.EU, "api.datadoghq.eu"
                    DatadogRegion.US3, "api.us3.datadoghq.com"
                    DatadogRegion.US5, "api.us5.datadoghq.com"
                    DatadogRegion.AP1, "api.ap1.datadoghq.com"
                ] do
                test $"{region} routes to {host}" {
                    let handler = new RecordingHandler()

                    let client =
                        clientOver handler (FakeSecretStore()) { settings with Region = region }

                    client.GetMonitors [] |> Async.RunSynchronously |> ignore

                    Expect.equal handler.LastUrl $"https://{host}/api/v1/monitor" "the region's api host"
                }

            test "an unknown site code does not silently become US" {
                Expect.isNone (DatadogRegion.tryParse "us9") "an unrecognised site is None, not a default"
                Expect.equal (DatadogRegion.tryParse "US1") (Some DatadogRegion.US) "us1 is an alias of us"
                Expect.equal (DatadogRegion.tryParse " eu ") (Some DatadogRegion.EU) "trimmed and case-insensitive"
            }
        ]

        testList "credentials" [
            test "both keys ride as headers on every request" {
                let handler = new RecordingHandler()
                let client = clientOver handler (FakeSecretStore()) settings

                client.GetMonitors [] |> Async.RunSynchronously |> ignore

                let headers = handler.LastHeaders |> Map.ofList

                Expect.equal (Map.tryFind "DD-API-KEY" headers) (Some "DD-API-KEY-value") "the api key header"

                Expect.equal
                    (Map.tryFind "DD-APPLICATION-KEY" headers)
                    (Some "DD-APPLICATION-KEY-value")
                    "the application key header — the readback endpoints need both"
            }

            test "the secret store is read on EVERY dispatch, so a rotated key flows through" {
                let handler = new RecordingHandler()
                let store = FakeSecretStore()
                let client = clientOver handler store settings

                client.GetMonitors [] |> Async.RunSynchronously |> ignore
                client.GetMonitors [] |> Async.RunSynchronously |> ignore

                Expect.equal
                    store.Reads
                    [ "DD-API-KEY"; "DD-APPLICATION-KEY"; "DD-API-KEY"; "DD-APPLICATION-KEY" ]
                    "two reads per call, not one cached pair"
            }

            test "a missing key is named, and nothing is sent" {
                let handler = new RecordingHandler()

                let client =
                    clientOver handler (FakeSecretStore(withhold = "DD-APPLICATION-KEY")) settings

                let result = client.GetMonitors [] |> Async.RunSynchronously

                Expect.equal result (Error(MissingCredential "DD-APPLICATION-KEY")) "the key NAME, never its value"
                Expect.isEmpty handler.Recorded "an unauthenticated request is not worth making"
            }
        ]

        testList "soft failure" [
            test "a non-success status is an api error carrying the status and body" {
                let handler =
                    new RecordingHandler(HttpStatusCode.Forbidden, "Forbidden: application key is invalid")

                let client = clientOver handler (FakeSecretStore()) settings

                Expect.equal
                    (client.GetMonitors [] |> Async.RunSynchronously)
                    (Error(DatadogApiError(403, "Forbidden: application key is invalid")))
                    "a misconfigured region or key is a 403 the operator can act on"
            }

            test "an unreachable host is a transport failure, not an api error" {
                let client = clientOver (new ThrowingHandler()) (FakeSecretStore()) settings

                match client.GetMonitors [] |> Async.RunSynchronously with
                | Error(TransportFailure message) -> Expect.stringContains message "no such host" "the transport reason"
                | other -> failtestf "expected a transport failure, got %A" other
            }

            test "a 200 whose body is not the documented shape is an api error, not a transport one" {
                // The classification matters operationally: transport
                // sends an operator to the network, api error sends them
                // to Datadog's response.
                let handler = new RecordingHandler(HttpStatusCode.OK, "<html>not json</html>")
                let client = clientOver handler (FakeSecretStore()) settings

                match client.GetMonitors [] |> Async.RunSynchronously with
                | Error(DatadogApiError(status, _)) -> Expect.equal status 200 "the status Datadog actually sent"
                | other -> failtestf "expected an api error, got %A" other
            }

            test "every failure class has a distinct, bounded metric tag" {
                let classes =
                    [
                        MissingCredential "DD-API-KEY"
                        DatadogApiError(403, "nope")
                        TransportFailure "dns"
                    ]
                    |> List.map DatadogReadbackError.statusTag

                Expect.equal
                    classes
                    [ "missing_credential"; "api_error"; "transport" ]
                    "the tag values are literals, so a misconfigured deployment cannot drive cardinality"
            }
        ]

        testList "parsing" [
            test "monitors: an unrecognised overall_state is Unknown, never OK" {
                let body =
                    """[{"id":42,"name":"API latency","overall_state":"Skipped","overall_state_modified":1772366400,"tags":["service:toolup"]},
                        {"id":43,"name":"Error rate","overall_state":"Alert","overall_state_modified":"2026-03-01T12:00:00+00:00","tags":[]}]"""

                let handler = new RecordingHandler(HttpStatusCode.OK, body)
                let client = clientOver handler (FakeSecretStore()) settings

                match client.GetMonitors [] |> Async.RunSynchronously with
                | Error e -> failtestf "expected monitors, got %A" e
                | Ok monitors ->
                    Expect.equal (List.length monitors) 2 "both rows"

                    let first = monitors[0]
                    Expect.equal first.Id 42L "the numeric monitor id, carried by value"
                    Expect.equal first.Name "API latency" "the display name"

                    Expect.equal
                        first.Status
                        DatadogMonitorStatus.Unknown
                        "a state this SDK cannot read must not render as healthy"

                    Expect.equal first.LastTransitionUtc windowFrom "a Unix-second transition time"
                    Expect.equal first.Tags [ "service:toolup" ] "the monitor's own tags"

                    Expect.equal monitors[1].Status DatadogMonitorStatus.Alert "the alerting row"

                    Expect.equal
                        monitors[1].LastTransitionUtc
                        windowFrom
                        "an ISO-8601 transition time reads the same instant as the Unix-second one"
            }

            test "logs: attributes are flattened and the status is lower-cased" {
                let body =
                    """{"data":[{"attributes":{"timestamp":"2026-03-01T12:00:00Z","service":"toolup","host":"pod-1",
                        "status":"ERROR","message":"boom","tags":["env:prod"]}}]}"""

                let handler = new RecordingHandler(HttpStatusCode.OK, body)
                let client = clientOver handler (FakeSecretStore()) settings

                match client.SearchLogs("status:error", windowFrom, 10) |> Async.RunSynchronously with
                | Error e -> failtestf "expected log events, got %A" e
                | Ok events ->
                    let event = List.exactlyOne events
                    Expect.equal event.Timestamp windowFrom "the event instant"
                    Expect.equal event.Service "toolup" "the service tag"
                    Expect.equal event.Host "pod-1" "the reporting host"
                    Expect.equal event.Level "error" "normalised to lower case"
                    Expect.equal event.Message "boom" "the message"
                    Expect.equal event.Tags [ "env:prod" ] "the event tags"
            }

            test "logs: a row missing attributes yields empty fields rather than blanking the panel" {
                let body = """{"data":[{"attributes":{"message":"partial"}}]}"""
                let handler = new RecordingHandler(HttpStatusCode.OK, body)
                let client = clientOver handler (FakeSecretStore()) settings

                match client.SearchLogs("status:error", windowFrom, 10) |> Async.RunSynchronously with
                | Error e -> failtestf "expected log events, got %A" e
                | Ok events ->
                    let event = List.exactlyOne events
                    Expect.equal event.Message "partial" "the field that was present"
                    Expect.equal event.Host "" "an absent field is empty, not an exception"
                    Expect.equal event.Timestamp DateTime.MinValue "an absent instant is MinValue"
            }

            test "metric: points are ascending, milliseconds, and nulls are gaps rather than zeroes" {
                let body =
                    """{"series":[{"metric":"toolup.requests.total","tag_set":["service:toolup"],
                        "pointlist":[[1772370000000,7.0],[1772366400000,3.0],[1772368200000,null]]}]}"""

                let handler = new RecordingHandler(HttpStatusCode.OK, body)
                let client = clientOver handler (FakeSecretStore()) settings

                match
                    client.QueryMetric("toolup.requests.total", [ "service:toolup" ], windowFrom, windowTo)
                    |> Async.RunSynchronously
                with
                | Error e -> failtestf "expected a series, got %A" e
                | Ok series ->
                    Expect.equal series.Metric "toolup.requests.total" "the reported metric name"

                    Expect.equal
                        series.Points
                        [ windowFrom, 3.0; windowTo, 7.0 ]
                        "sorted ascending, the null point dropped rather than drawn as a trough"

                    Expect.equal series.Tags [ "service:toolup" ] "the reported tag set"
            }

            test "metric: no matching series is an empty series, not a failure" {
                let handler = new RecordingHandler(HttpStatusCode.OK, """{"series":[]}""")
                let client = clientOver handler (FakeSecretStore()) settings

                match
                    client.QueryMetric("toolup.sse.active_connections", [], windowFrom, windowTo)
                    |> Async.RunSynchronously
                with
                | Error e -> failtestf "expected an empty series, got %A" e
                | Ok series ->
                    Expect.equal series.Metric "toolup.sse.active_connections" "the queried name is echoed back"
                    Expect.isEmpty series.Points "no points"
            }
        ]

        testList "window and limit clamping" [
            test "a non-positive request takes the configured default" {
                Expect.equal (DatadogReadback.clampWindowMinutes 90 0) 90 "zero minutes means 'unspecified'"
                Expect.equal (DatadogReadback.clampLogLimit 25 0) 25 "zero events means 'unspecified'"
            }

            test "an unbounded request is clamped rather than passed to Datadog" {
                Expect.equal (DatadogReadback.clampWindowMinutes 60 999999) (7 * 24 * 60) "a seven-day ceiling"
                Expect.equal (DatadogReadback.clampLogLimit 50 100000) 1000 "Datadog's own page cap"
            }
        ]
    ]