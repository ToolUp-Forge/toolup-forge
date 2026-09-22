// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Observability.Datadog

open System
open System.Globalization
open System.Net.Http
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Public surface ──────────────────────────────────────────────────
//
// Phase 9w Datadog READBACK companion — the `IDatadogReadbackApi`
// implementation behind the SDK's `/api/observability/datadog/*`
// endpoints. The write side (an `IAuditSink` shipping audit batches into
// Datadog Logs) is an independent companion; this one only reads, and
// the two can be composed separately.
//
// No Datadog SDK dependency — BCL `HttpClient`, exactly as the write
// side does (GP 1).
//
// **Three endpoints, deliberately.**
//
//   GET  /api/v1/monitor                — monitor state
//   POST /api/v2/logs/events/search     — log search
//   GET  /api/v1/query                  — metric timeseries
//
// Synthetics, RUM and APM trace readback are out of scope; so is every
// Datadog WRITE API. This companion cannot create a monitor, submit a
// metric or edit a dashboard, and its credentials are the operator's to
// scope accordingly.
//
// **Authentication.** Both `DD-API-KEY` and `DD-APPLICATION-KEY`
// headers. The API key identifies the organization; the application key
// authorises the read. Both are resolved from `ISecretStore` on EVERY
// call — never cached — so a key rotated in the secret store flows
// through to the next request, the same convention the write-side sink
// follows.
//
// **Region.** Datadog's sites do not share an API host, and a call made
// against the wrong one fails authentication rather than returning
// another region's data. The base URL is derived from
// `DatadogReadbackConfig.Region`, so a misconfigured region surfaces as
// an audited `DatadogApiError 403` rather than as silence.
//
// **Failure is returned, not thrown.** Every method answers
// `Result<_, DatadogReadbackError>`; a transport exception becomes
// `TransportFailure` and a non-success status becomes `DatadogApiError`.
// The SDK handler is what turns that into the admin surface's warning
// toast, the error counter and the `DatadogReadbackFailed` event — this
// companion neither logs nor audits on its own behalf, so a second
// consumer of the interface is free to treat failure differently.

// ─── Wire encoding ────────────────────────────────────────────────────
//
// Kept companion-side rather than in the shared contract: these are
// Datadog's spellings, not the SDK's, and the client tier that shares
// the readback RECORD types has no business compiling a URL builder.

/// Datadog reports monitor and timeseries instants as Unix time. The
/// monitor API uses SECONDS; the timeseries `pointlist` uses
/// MILLISECONDS — the one place the two endpoint families disagree, and
/// the reason this is two functions rather than one.
let private ofUnixSeconds (seconds: int64) : DateTime =
    DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime

let private ofUnixMilliseconds (milliseconds: float) : DateTime =
    DateTimeOffset.FromUnixTimeMilliseconds(int64 milliseconds).UtcDateTime

let private toUnixSeconds (instant: DateTime) : int64 =
    DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)).ToUnixTimeSeconds()

/// The log-search API takes ISO-8601 bounds rather than Unix seconds.
let private toIso8601 (instant: DateTime) : string =
    DateTime.SpecifyKind(instant, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)

let private nonBlank (values: string list) : string list =
    values |> List.filter (String.IsNullOrWhiteSpace >> not)

/// Datadog's monitor-search tag filter — the `tags` query parameter,
/// comma-separated. An empty list omits the parameter entirely rather
/// than sending an empty one, which Datadog rejects.
let internal monitorTagQuery (tags: string list) : string option =
    match nonBlank tags with
    | [] -> None
    | filtered -> Some(String.Join(",", filtered))

/// A log search narrowed by a tag list — `query` AND every tag, which
/// is Datadog's own search grammar for a space-separated conjunction.
let internal narrowedLogQuery (query: string) (tags: string list) : string =
    let parts =
        (if String.IsNullOrWhiteSpace query then [] else [ query ]) @ nonBlank tags

    String.Join(" ", parts)

/// A timeseries query in Datadog's `metric{tag,tag}` form. An empty tag
/// list yields `metric{*}` — Datadog's spelling of "no narrowing"; an
/// empty brace pair is a syntax error there, so the wildcard is the
/// correct query rather than a convenience.
let internal metricQuery (metric: string) (tags: string list) : string =
    match nonBlank tags with
    | [] -> sprintf "%s{*}" metric
    | filtered -> sprintf "%s{%s}" metric (String.Join(",", filtered))

// ─── JSON reading ─────────────────────────────────────────────────────
//
// Datadog's responses are vendor JSON, not F#-shaped, so they are read
// through `JsonDocument` rather than deserialised into records. Every
// reader below is total: a missing or wrongly-typed field yields the
// field's empty value rather than throwing, because a single unexpected
// attribute on one row must not blank the whole panel.

let private tryProperty (element: JsonElement) (name: string) : JsonElement option =
    if element.ValueKind <> JsonValueKind.Object then
        None
    else
        match element.TryGetProperty name with
        | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
        | _ -> None

let private stringField (element: JsonElement) (name: string) : string =
    match tryProperty element name with
    | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
    | Some value when value.ValueKind = JsonValueKind.Number -> value.ToString()
    | _ -> ""

let private int64Field (element: JsonElement) (name: string) : int64 =
    match tryProperty element name with
    | Some value when value.ValueKind = JsonValueKind.Number ->
        match value.TryGetInt64() with
        | true, n -> n
        | _ -> 0L
    | _ -> 0L

let private stringListField (element: JsonElement) (name: string) : string list =
    match tryProperty element name with
    | Some value when value.ValueKind = JsonValueKind.Array ->
        value.EnumerateArray()
        |> Seq.choose (fun item ->
            if item.ValueKind = JsonValueKind.String then
                Some(item.GetString())
            else
                None)
        |> List.ofSeq
    | _ -> []

/// Read an instant Datadog may have written either way. The monitor API
/// has reported `overall_state_modified` as a Unix second AND as an
/// ISO-8601 string across API versions; accepting both is cheaper than
/// pinning a version, and a value in neither shape yields `MinValue`
/// (which the admin surface renders as "never") rather than throwing.
let private instantField (element: JsonElement) (name: string) : DateTime =
    match tryProperty element name with
    | Some value when value.ValueKind = JsonValueKind.Number ->
        match value.TryGetInt64() with
        | true, seconds -> ofUnixSeconds seconds
        | _ -> DateTime.MinValue
    | Some value when value.ValueKind = JsonValueKind.String ->
        match
            DateTime.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal ||| DateTimeStyles.AssumeUniversal
            )
        with
        | true, parsed -> parsed
        | _ -> DateTime.MinValue
    | _ -> DateTime.MinValue

let private parseMonitors (json: string) : DatadogMonitorState list =
    use document = JsonDocument.Parse json

    if document.RootElement.ValueKind <> JsonValueKind.Array then
        []
    else
        document.RootElement.EnumerateArray()
        |> Seq.map (fun monitor -> {
            Id = int64Field monitor "id"
            Name = stringField monitor "name"
            Status = DatadogMonitorStatus.ofOverallState (stringField monitor "overall_state")
            LastTransitionUtc = instantField monitor "overall_state_modified"
            Tags = stringListField monitor "tags"
        })
        |> List.ofSeq

let private parseLogEvents (json: string) : DatadogLogEvent list =
    use document = JsonDocument.Parse json

    match tryProperty document.RootElement "data" with
    | Some data when data.ValueKind = JsonValueKind.Array ->
        data.EnumerateArray()
        |> Seq.map (fun entry ->
            let attributes = tryProperty entry "attributes" |> Option.defaultValue entry

            {
                Timestamp = instantField attributes "timestamp"
                Service = stringField attributes "service"
                Host = stringField attributes "host"
                Level = (stringField attributes "status").ToLowerInvariant()
                Message = stringField attributes "message"
                Tags = stringListField attributes "tags"
            })
        |> List.ofSeq
    | _ -> []

/// Datadog's `pointlist` is an array of `[timestampMs, value]` pairs in
/// which the value may be `null` — a GAP in the series, not a zero.
/// Gaps are DROPPED rather than rendered as zero, which would draw a
/// trough the deployment never had.
let private parsePoints (series: JsonElement) : (DateTime * float) list =
    match tryProperty series "pointlist" with
    | Some points when points.ValueKind = JsonValueKind.Array ->
        points.EnumerateArray()
        |> Seq.choose (fun point ->
            if point.ValueKind <> JsonValueKind.Array || point.GetArrayLength() < 2 then
                None
            else
                let at = point[0]
                let value = point[1]

                if at.ValueKind <> JsonValueKind.Number || value.ValueKind <> JsonValueKind.Number then
                    None
                else
                    Some(ofUnixMilliseconds (at.GetDouble()), value.GetDouble()))
        |> Seq.sortBy fst
        |> List.ofSeq
    | _ -> []

let private parseMetricSeries (metric: string) (tags: string list) (json: string) : DatadogMetricSeries =
    use document = JsonDocument.Parse json

    let firstSeries =
        match tryProperty document.RootElement "series" with
        | Some series when series.ValueKind = JsonValueKind.Array -> series.EnumerateArray() |> Seq.tryHead
        | _ -> None

    match firstSeries with
    | None -> {
        Metric = metric
        Points = []
        Tags = tags
      }
    | Some series -> {
        Metric =
            let reported = stringField series "metric"

            if String.IsNullOrWhiteSpace reported then
                metric
            else
                reported
        Points = parsePoints series
        Tags =
            match stringListField series "tag_set" with
            | [] -> tags
            | reported -> reported
      }

// ─── The client ───────────────────────────────────────────────────────

/// Truncate a Datadog error body so one 500 page cannot put a megabyte
/// of vendor HTML into an audit event.
let private truncateBody (body: string) : string =
    if isNull body then ""
    elif body.Length <= 512 then body
    else body.Substring(0, 512) + "…"

/// Read-only Datadog API client. One `HttpClient`, two secrets read per
/// call, no cached state between calls (GP 12 rule 4).
type DatadogReadbackClient(settings: DatadogReadbackConfig, secretStore: ISecretStore, httpClient: HttpClient) =

    let baseUrl = DatadogRegion.baseUrl settings.Region

    /// Resolve both keys. Reported as `MissingCredential` naming the
    /// KEY, never the value — the name is what an operator needs to fix
    /// it and the value is what must never reach a log.
    let credentials () = async {
        let! apiKey = secretStore.GetSecret(DatadogReadback.SecretScope, DatadogReadback.ApiKeySecret)

        match apiKey with
        | None -> return Error(MissingCredential DatadogReadback.ApiKeySecret)
        | Some api ->
            let! appKey = secretStore.GetSecret(DatadogReadback.SecretScope, DatadogReadback.ApplicationKeySecret)

            match appKey with
            | None -> return Error(MissingCredential DatadogReadback.ApplicationKeySecret)
            | Some app -> return Ok(api, app)
    }

    /// Issue one request and hand its body to `parse`. Owns the whole
    /// failure surface: credential absence, non-success status,
    /// transport exception and a body Datadog sent that does not parse.
    let send (method: HttpMethod) (url: string) (body: string option) (parse: string -> 'T) = async {
        match! credentials () with
        | Error error -> return Error error
        | Ok(apiKey, applicationKey) ->
            try
                use request = new HttpRequestMessage(method, url)
                request.Headers.Add(DatadogReadback.ApiKeySecret, (apiKey: string))
                request.Headers.Add(DatadogReadback.ApplicationKeySecret, (applicationKey: string))

                match body with
                | None -> ()
                | Some payload -> request.Content <- new StringContent(payload, Encoding.UTF8, "application/json")

                let! response = httpClient.SendAsync request |> Async.AwaitTask
                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    try
                        return Ok(parse responseBody)
                    with ex ->
                        // A 200 whose body is not the documented shape is
                        // an API-contract failure, not a transport one —
                        // classing it as transport would tell an operator
                        // to check the network when the answer is that
                        // Datadog changed a response.
                        return Error(DatadogApiError(int response.StatusCode, truncateBody ex.Message))
                else
                    return Error(DatadogApiError(int response.StatusCode, truncateBody responseBody))
            with ex ->
                return Error(TransportFailure ex.Message)
    }

    interface IDatadogReadbackApi with

        member _.GetMonitors(tagsFilter) =
            let url =
                match monitorTagQuery tagsFilter with
                | None -> baseUrl + "/api/v1/monitor"
                | Some tags -> sprintf "%s/api/v1/monitor?tags=%s" baseUrl (Uri.EscapeDataString tags)

            send HttpMethod.Get url None parseMonitors

        member _.SearchLogs(query, sinceUtc, limit) =
            let url = baseUrl + "/api/v2/logs/events/search"
            let narrowed = narrowedLogQuery query settings.DefaultTags
            let effectiveLimit = DatadogReadback.clampLogLimit settings.LogPageLimit limit

            // Hand-built rather than serialised from a record: this is
            // Datadog's request schema, and an F# record round-tripped
            // through the SDK's converter set would be one more shape to
            // keep in step with a vendor's API for no reader's benefit.
            let body =
                sprintf
                    """{"filter":{"query":%s,"from":%s,"to":%s},"page":{"limit":%d},"sort":"-timestamp"}"""
                    (JsonSerializer.Serialize narrowed)
                    (JsonSerializer.Serialize(toIso8601 sinceUtc))
                    (JsonSerializer.Serialize(toIso8601 DateTime.UtcNow))
                    effectiveLimit

            send HttpMethod.Post url (Some body) parseLogEvents

        member _.QueryMetric(metric, tagsFilter, fromUtc, toUtc) =
            let query = metricQuery metric tagsFilter

            let url =
                sprintf
                    "%s/api/v1/query?from=%d&to=%d&query=%s"
                    baseUrl
                    (toUnixSeconds fromUtc)
                    (toUnixSeconds toUtc)
                    (Uri.EscapeDataString query)

            send HttpMethod.Get url None (parseMetricSeries metric tagsFilter)

/// Construct the readback client. `settings` is the same
/// `DatadogReadbackConfig` the deployment puts in
/// `ServerConfig.DatadogReadback`, so region and default narrowing
/// cannot drift between the client and the endpoints that call it.
///
/// Register the result as a DI singleton —
/// `services.AddSingleton<IDatadogReadbackApi>(client)` — and set
/// `ServerConfig.DatadogReadback = EnabledDatadogReadback settings`.
/// The SDK resolves it from there; nothing else needs composing.
let create
    (settings: DatadogReadbackConfig)
    (secretStore: ISecretStore)
    (httpClient: HttpClient)
    : IDatadogReadbackApi =
    DatadogReadbackClient(settings, secretStore, httpClient) :> _