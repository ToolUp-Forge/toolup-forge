// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Datadog readback types (Phase 9w) ───────────────────────────────
//
// The read side of the Datadog pairing. The write side is the
// `IAuditSink` companion that POSTs each audit batch to Datadog Logs;
// this is the contract an admin surface reads those events — and the
// deployment's monitors and metric series — back through.
//
// **Why these types live in `Platform.Core` and not in the companion.**
// Only `Platform.Core` ships its sources under `fable/` in the nupkg, so
// a type a Fable client renders must be declared here (GP 10). The
// companion holds the `HttpClient` implementation of the interface below
// and nothing a client ever names; the SDK tiers hold the contract. That
// is the same split `ISecretStore` / `IAuditSink` / `IAIProvider` already
// use, and it is what keeps the vendor dependency out of the SDK's
// dependency graph (GP 1).
//
// **Portability (GP 12).** The interface is vendor-NAMED but its shape is
// deliberately generic — monitor state, log event, metric series are
// observability primitives, not Datadog concepts — so a future
// `IObservabilityReadback` can extract this contract with no behaviour
// change. The six rules are audited on it at the interface declaration
// below.

/// Datadog's regional API sites. A deployment's Datadog organization
/// lives in exactly one of them and its API host differs per site; a
/// call made against the wrong one fails authentication rather than
/// returning another region's data.
[<RequireQualifiedAccess>]
type DatadogRegion =
    /// The default US1 site — `api.datadoghq.com`.
    | US
    /// The EU site — `api.datadoghq.eu`.
    | EU
    /// The US3 site — `api.us3.datadoghq.com`.
    | US3
    /// The US5 site — `api.us5.datadoghq.com`.
    | US5
    /// The AP1 site — `api.ap1.datadoghq.com`.
    | AP1

[<RequireQualifiedAccess>]
module DatadogRegion =
    /// The API host for a region — no scheme, no trailing slash.
    let host (region: DatadogRegion) : string =
        match region with
        | DatadogRegion.US -> "api.datadoghq.com"
        | DatadogRegion.EU -> "api.datadoghq.eu"
        | DatadogRegion.US3 -> "api.us3.datadoghq.com"
        | DatadogRegion.US5 -> "api.us5.datadoghq.com"
        | DatadogRegion.AP1 -> "api.ap1.datadoghq.com"

    /// The `https://`-scheme base URL for a region, with no trailing
    /// slash — every endpoint path below is appended to it directly.
    let baseUrl (region: DatadogRegion) : string = "https://" + host region

    /// Parse a region from its conventional site code (`us`, `eu`,
    /// `us3`, `us5`, `ap1`; case-insensitive, `us1` accepted as an alias
    /// of `us`). `None` for anything else — a caller turning
    /// deployment config into a region reports the bad value rather than
    /// silently defaulting to US and querying the wrong organization.
    let tryParse (value: string) : DatadogRegion option =
        if String.IsNullOrWhiteSpace value then
            None
        else
            match value.Trim().ToLowerInvariant() with
            | "us"
            | "us1" -> Some DatadogRegion.US
            | "eu" -> Some DatadogRegion.EU
            | "us3" -> Some DatadogRegion.US3
            | "us5" -> Some DatadogRegion.US5
            | "ap1" -> Some DatadogRegion.AP1
            | _ -> None

/// Deployment-level settings for the Datadog readback surface.
/// Immutable (GP 5) and identity-by-value (GP 12 rule 1) — it names a
/// region and some query defaults, never a live client handle.
type DatadogReadbackConfig = {
    /// The Datadog site this deployment's organization lives in.
    Region: DatadogRegion
    /// Tags every query is narrowed by, in Datadog's `key:value` form —
    /// conventionally `[ "service:toolup"; "env:prod" ]`, matching the
    /// tags the write-side audit sink stamps. Empty means "no default
    /// narrowing", which on a shared organization returns other
    /// services' monitors too.
    DefaultTags: string list
    /// The default look-back window, in minutes, for the log and metric
    /// queries when a caller does not name one. Default 60.
    DefaultWindowMinutes: int
    /// The maximum number of log events a single search returns.
    /// Datadog's own page cap is 1000; the default 50 is what the
    /// recent-errors table shows without paging.
    LogPageLimit: int
}

[<RequireQualifiedAccess>]
module DatadogReadbackConfig =
    /// The documented defaults — US site, `service:toolup` narrowing, a
    /// one-hour window and a 50-event page.
    let defaults: DatadogReadbackConfig = {
        Region = DatadogRegion.US
        DefaultTags = [ "service:toolup" ]
        DefaultWindowMinutes = 60
        LogPageLimit = 50
    }

/// The state of one Datadog monitor, normalised off Datadog's
/// `overall_state` string. `Unknown` is the honest reading for a state
/// this SDK does not recognise — never a silent coercion to `OK`.
[<RequireQualifiedAccess>]
type DatadogMonitorStatus =
    /// The monitor is not triggered.
    | OK
    /// The monitor is in its warning threshold.
    | Warn
    /// The monitor is alerting.
    | Alert
    /// The monitor has received no data in its evaluation window.
    | NoData
    /// Datadog reported a state this SDK does not recognise (including
    /// `Skipped` and any state added upstream after this phase shipped).
    | Unknown

[<RequireQualifiedAccess>]
module DatadogMonitorStatus =
    /// Normalise Datadog's `overall_state` string. Unrecognised values
    /// map to `Unknown` rather than to `OK`: a monitor whose state this
    /// SDK cannot read must not render as healthy.
    let ofOverallState (value: string) : DatadogMonitorStatus =
        if isNull value then
            DatadogMonitorStatus.Unknown
        else
            match value.Trim().ToLowerInvariant() with
            | "ok" -> DatadogMonitorStatus.OK
            | "warn" -> DatadogMonitorStatus.Warn
            | "alert" -> DatadogMonitorStatus.Alert
            | "no data" -> DatadogMonitorStatus.NoData
            | _ -> DatadogMonitorStatus.Unknown

/// One Datadog monitor as the admin surface renders it. A value, not a
/// handle (GP 12 rule 1) — the monitor id is Datadog's numeric id
/// carried as a value so a UI can key a row by it stably.
type DatadogMonitorState = {
    /// Datadog's numeric monitor id.
    Id: int64
    /// The monitor's display name.
    Name: string
    /// Normalised overall state.
    Status: DatadogMonitorStatus
    /// When the monitor last changed state, in UTC. Datadog reports this
    /// as a Unix second; a monitor that has never transitioned carries
    /// its creation time.
    LastTransitionUtc: DateTime
    /// The monitor's tags, in Datadog's `key:value` form.
    Tags: string list
}

/// One log event from Datadog's log search, flattened to the fields the
/// recent-errors table shows. The raw Datadog attribute bag is
/// deliberately not carried: the handler returns SDK record shapes,
/// never vendor JSON.
type DatadogLogEvent = {
    /// Event timestamp in UTC.
    Timestamp: DateTime
    /// The `service` tag — conventionally `toolup`.
    Service: string
    /// The reporting host, or `""` when Datadog recorded none.
    Host: string
    /// Datadog's `status` attribute (`error`, `critical`, `warn`, …),
    /// lower-cased. Carried as a string rather than a DU because
    /// Datadog's status vocabulary is open and a pipeline can introduce
    /// its own.
    Level: string
    /// The log message.
    Message: string
    /// The event's tags, in Datadog's `key:value` form.
    Tags: string list
}

/// One metric series from Datadog's timeseries query. Points are
/// `(timestamp, value)` pairs in ascending time order; Datadog's own
/// nulls (gaps in the series) are dropped rather than rendered as zero,
/// which would draw a false trough.
type DatadogMetricSeries = {
    /// The queried metric name, e.g. `toolup.requests.total`.
    Metric: string
    /// `(timestampUtc, value)` in ascending time order. Empty when the
    /// query matched nothing in the window.
    Points: (DateTime * float) list
    /// The tag filter the series was queried under.
    Tags: string list
}

/// Why a readback call did not return data. Every case is a value a
/// handler can audit and a UI can render (GP 12 rule 3 — the failure is
/// data, not a callback).
type DatadogReadbackError =
    /// A required key was absent from `ISecretStore` under the
    /// `_platform` scope. Carries the key name, never the key.
    | MissingCredential of key: string
    /// Datadog answered with a non-success status. Carries the status
    /// and a truncated response body — the body is Datadog's own error
    /// text and never contains the deployment's keys.
    | DatadogApiError of status: int * body: string
    /// The call did not reach Datadog at all (DNS, TLS, timeout).
    | TransportFailure of message: string

[<RequireQualifiedAccess>]
module DatadogReadbackError =
    /// A one-line operator-facing rendering. Used for the warning the
    /// admin surface shows and for the audit event's reason field.
    let describe (error: DatadogReadbackError) : string =
        match error with
        | MissingCredential key -> sprintf "Datadog credential '%s' is not present in the secret store" key
        | DatadogApiError(status, body) ->
            if String.IsNullOrWhiteSpace body then
                sprintf "Datadog API returned HTTP %d" status
            else
                sprintf "Datadog API returned HTTP %d: %s" status body
        | TransportFailure message -> sprintf "Datadog API was unreachable: %s" message

    /// The `status` tag value the readback error counter is incremented
    /// under. Bounded by construction — three cases, no caller-supplied
    /// text — so the metric's cardinality cannot be driven by a
    /// misconfigured deployment.
    let statusTag (error: DatadogReadbackError) : string =
        match error with
        | MissingCredential _ -> "missing_credential"
        | DatadogApiError _ -> "api_error"
        | TransportFailure _ -> "transport"

/// The monitors endpoint's response. `Warning` is `Some` when the call
/// soft-failed: the surface renders the empty state plus one warning
/// rather than blanking (the degradation contract).
type DatadogMonitorsResponse = {
    /// The monitors matching the tag filter. Empty on a soft failure.
    Monitors: DatadogMonitorState list
    /// A one-line operator-facing reason the result is empty, or `None`
    /// when the call succeeded.
    Warning: string option
}

/// The log-search endpoint's response. Same degradation contract as
/// `DatadogMonitorsResponse`.
type DatadogLogsResponse = {
    /// The matching log events, most recent first. Empty on a soft failure.
    Events: DatadogLogEvent list
    /// A one-line operator-facing reason the result is empty, or `None`
    /// when the call succeeded.
    Warning: string option
}

/// The metric-query endpoint's response. Same degradation contract as
/// `DatadogMonitorsResponse`.
type DatadogMetricResponse = {
    /// The queried series. `Points` is empty on a soft failure.
    Series: DatadogMetricSeries
    /// A one-line operator-facing reason the result is empty, or `None`
    /// when the call succeeded.
    Warning: string option
}

/// Read-only access to a Datadog organization's monitors, logs and
/// metric series. Implemented by the `ToolUp.Observability.Datadog`
/// companion; resolved from DI by the SDK's readback handlers.
///
/// **Six portability rules (GP 12), audited for this phase.**
/// 1. *Identity by value* — every parameter and every returned field is
///    a primitive or a domain record; no live handle crosses the seam.
/// 2. *Async at every boundary* — all three methods return `Async<_>`.
/// 3. *Retry / supervision as data* — failure is the
///    `DatadogReadbackError` DU in the returned `Result`, never an
///    `OnFailure` callback; the caller decides what to audit and what to
///    retry.
/// 4. *Stateless between invocations* — an implementation holds only its
///    settings; credentials are read from `ISecretStore` on every call,
///    so nothing is cached between them.
/// 5. *No cross-shard ordering promises* — each method is an independent
///    query; nothing orders one call against another.
/// 6. *Precision at the lower bound* — time bounds are `DateTime` in
///    UTC and Datadog's own resolution is one second for log and monitor
///    timestamps; no sub-second promise is made or implied.
///
/// Nothing in the shape is Datadog-specific, which is the point: a
/// future `IObservabilityReadback` can take this contract unchanged.
type IDatadogReadbackApi =
    /// Monitors whose tags match every entry of `tagsFilter` (Datadog's
    /// own AND semantics). An empty filter returns the organization's
    /// monitors.
    abstract GetMonitors: tagsFilter: string list -> Async<Result<DatadogMonitorState list, DatadogReadbackError>>

    /// Log events matching a Datadog search `query` since `sinceUtc`,
    /// most recent first, capped at `limit` events.
    abstract SearchLogs:
        query: string * sinceUtc: DateTime * limit: int -> Async<Result<DatadogLogEvent list, DatadogReadbackError>>

    /// One metric series over `[fromUtc, toUtc]`, narrowed by
    /// `tagsFilter`.
    abstract QueryMetric:
        metric: string * tagsFilter: string list * fromUtc: DateTime * toUtc: DateTime ->
            Async<Result<DatadogMetricSeries, DatadogReadbackError>>

[<RequireQualifiedAccess>]
module DatadogReadback =
    /// The `ISecretStore` scope every platform-level credential is read
    /// from — the same scope the write-side audit sink uses.
    [<Literal>]
    let SecretScope = "_platform"

    /// The `ISecretStore` key holding the Datadog API key.
    [<Literal>]
    let ApiKeySecret = "DD-API-KEY"

    /// The `ISecretStore` key holding the Datadog application key. The
    /// readback endpoints need BOTH: the API key identifies the
    /// organization, the application key authorises the read.
    [<Literal>]
    let ApplicationKeySecret = "DD-APPLICATION-KEY"

    /// `SourceModule` the readback's own events are recorded under.
    [<Literal>]
    let AuditSourceModule = "_platform.observability.datadog"

    /// `EventType` recorded when a readback call fails. Paired with
    /// `AuditSourceModule`, this is readable via
    /// `IEventStore.ReadBySource` without adding a case to the
    /// `AuditEvent` union (which every exhaustive match would have to
    /// grow).
    [<Literal>]
    let ReadbackFailedEvent = "DatadogReadbackFailed"

    /// The default log search — Datadog's own error and critical
    /// statuses. Callers may pass their own query; this is what the
    /// recent-errors tab asks for.
    [<Literal>]
    let DefaultErrorQuery = "status:error OR status:critical"

    /// The four SDK standard metrics the key-metrics tab charts. Names
    /// match the series `MetricsMiddleware.StandardMetrics` registers,
    /// so a deployment scraping `/metrics` into Datadog reads its own
    /// emissions back.
    let defaultChartedMetrics: string list = [
        "toolup.requests.total"
        "toolup.errors.total"
        "toolup.requests.latency_ms"
        "toolup.sse.active_connections"
    ]

    /// Clamp a caller-supplied log-page size into Datadog's own bounds.
    /// Datadog's log-search page cap is 1000; a non-positive request is
    /// a caller error rather than "give me none", so it takes the
    /// configured default.
    let clampLogLimit (configured: int) (requested: int) : int =
        let fallback = if configured > 0 then min configured 1000 else 50

        if requested <= 0 then fallback
        elif requested > 1000 then 1000
        else requested

    /// Clamp a caller-supplied look-back window, in minutes, into a
    /// sane range. A non-positive request takes the configured default;
    /// the upper bound is 7 days, past which a Datadog log search is an
    /// operator mistake rather than a dashboard read.
    let clampWindowMinutes (configured: int) (requested: int) : int =
        let fallback = if configured > 0 then configured else 60
        let chosen = if requested <= 0 then fallback else requested
        min chosen (7 * 24 * 60)