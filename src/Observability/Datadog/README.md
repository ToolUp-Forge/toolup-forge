# ToolUp.Observability.Datadog

Read-only Datadog API companion for ToolUp.Platform. Implements
`IDatadogReadbackApi` over three Datadog REST endpoints so an operator can see
monitor state, recent error logs and key metric series inside the platform's own
admin surface instead of switching to Datadog's UI.

This is the **read** half of the Datadog pairing. The **write** half —
`ToolUp.AuditSinks.DatadogLogs`, which ships each audit batch into Datadog Logs —
is an independent companion. Neither requires the other; a deployment may compose
either alone.

## What it calls

| Purpose | Datadog endpoint |
|---|---|
| Monitor state | `GET /api/v1/monitor` |
| Log search | `POST /api/v2/logs/events/search` |
| Metric timeseries | `GET /api/v1/query` |

Three endpoints is the whole scope. There is no Synthetics, RUM or APM-trace
readback, and **no Datadog write API at all** — this companion cannot create a
monitor, submit a metric or edit a dashboard, so its credentials can be scoped to
reads.

No Datadog SDK dependency: BCL `HttpClient` only (GP 1). The package references
`ToolUp.Platform.Core` and nothing else.

## Composing it

```fsharp
open ToolUp.Platform
open ToolUp.Platform.Observability

let settings = {
    DatadogReadbackConfig.defaults with
        Region = DatadogRegion.EU
        DefaultTags = [ "service:toolup"; "env:prod" ]
}

// One HttpClient for the lifetime of the deployment.
let client = Datadog.create settings secretStore httpClient

services.AddSingleton<IDatadogReadbackApi>(client) |> ignore
```

and turn the endpoints on:

```fsharp
{ ServerConfig.defaults with DatadogReadback = EnabledDatadogReadback settings }
```

The same `settings` value goes to both, so the region and the default tag
narrowing cannot drift between the client and the endpoints that call it.

With `DatadogReadback = NoDatadogReadback` (the default) no route is mounted and
nothing resolves the interface — a deployment that does not use Datadog pays
nothing (GP 13).

## Credentials

Two secrets, read from `ISecretStore` under the `_platform` scope **on every
call**:

| Key | Purpose |
|---|---|
| `DD-API-KEY` | identifies the Datadog organization |
| `DD-APPLICATION-KEY` | authorises the read |

Reading per call rather than caching is deliberate and matches the write-side
sink: a key rotated in the secret store flows through to the next request with no
restart. A missing key is reported as `MissingCredential` naming the **key**,
never its value.

## Regions

Datadog's sites do not share an API host, and a call made against the wrong one
fails authentication rather than returning another region's data.

| `DatadogRegion` | API host |
|---|---|
| `US` | `api.datadoghq.com` |
| `EU` | `api.datadoghq.eu` |
| `US3` | `api.us3.datadoghq.com` |
| `US5` | `api.us5.datadoghq.com` |
| `AP1` | `api.ap1.datadoghq.com` |

`DatadogRegion.tryParse` accepts the site codes (`us`, `us1`, `eu`, `us3`, `us5`,
`ap1`) and returns `None` for anything else rather than defaulting to `US`.

## Failure behaviour

Every method returns `Result<_, DatadogReadbackError>`. Nothing throws across the
seam, and nothing retries inside it:

| Case | When |
|---|---|
| `MissingCredential key` | the named key is absent from the secret store |
| `DatadogApiError (status, body)` | Datadog answered with a non-success status, or a success whose body was not the documented shape |
| `TransportFailure message` | the call did not reach Datadog (DNS, TLS, timeout) |

The companion neither logs nor audits on its own behalf. The SDK's handlers turn
a failure into the admin surface's warning, the
`toolup.datadog_readback.errors_total` counter and a `DatadogReadbackFailed`
event — so a second consumer of the interface is free to treat failure
differently.

Partial-read robustness is separate from that: a single row whose attributes are
missing or wrongly typed yields that field's empty value rather than failing the
whole response, because one odd log line must not blank the panel. A metric
series' `null` points are **gaps**, and are dropped rather than rendered as zero,
which would draw a trough the deployment never had.

## Portability

`IDatadogReadbackApi` is vendor-**named** but its shape is generic — monitor
state, log event, metric series are observability primitives, not Datadog
concepts. The six portability rules (GP 12) are audited against the interface in
its declaration in `ToolUp.Platform.Core`, so a future
`IObservabilityReadback` can take the contract unchanged.

## Testing

The contract pack (`DatadogReadbackClientTests`) runs against a recording
`HttpMessageHandler` and a fake secret store — **no Datadog account is needed**
to build or verify. It pins URL construction per endpoint, header population,
region routing, time-window and tag-filter encoding, and the soft-fail
classification.
