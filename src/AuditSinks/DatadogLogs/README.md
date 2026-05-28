# Datadog Logs audit sink

Phase 9g `IAuditSink` companion. POSTs every audit batch to Datadog's `/api/v2/logs` endpoint with an API key from `ISecretStore`. No Datadog SDK dependency — uses BCL `HttpClient` directly.

## How to enable

1. Reference this companion's `Server.props` and add a `<ProjectReference>`.

2. Provision a Datadog API key (Datadog → Organization Settings → API Keys → New Key).

3. Store it in `ISecretStore` under the `_platform` scope:

   ```fsharp
   do! secretStore.SetSecret("_platform", "datadog_api_key", "abc123...")
   ```

   Or via env vars: `TOOLUP_SECRET_PLATFORM_DATADOG_API_KEY=abc123...`.

4. Construct + register:

   ```fsharp
   open ToolUp.Platform.AuditSinks.DatadogLogs
   open System.Net.Http

   let httpClient = new HttpClient()
   let settings: DatadogLogsSettings = {
       EndpointUrl = "https://http-intake.logs.datadoghq.com/api/v2/logs"
       Service = "toolup"
       Env = "prod"
       DdSource = "toolup_audit"
       Host = Some "toolup-prod-1"
   }
   let sink = DatadogLogs.create "datadog-prod" settings secretStore "datadog_api_key" httpClient

   ServerApp.empty
   |> ServerApp.withAuditSink sink
   |> ServerApp.run
   ```

## Wire format

Datadog's `/api/v2/logs` endpoint accepts a JSON array of log entries:

```json
[
  {
    "ddsource": "toolup_audit",
    "ddtags": "env:prod,event_type:UserLoggedIn,scope_id:team-acme",
    "service": "toolup",
    "host": "toolup-prod-1",
    "message": {"Case": "UserLoggedIn", "Fields": [{"UserId": "u123", "AuthProvider": "Header"}]}
  }
]
```

- **`ddsource`** — source label (`toolup_audit`). Datadog routes by this for audit-specific pipelines.
- **`ddtags`** — comma-separated tag list. Includes `env:`, `event_type:`, and `scope_id:` (best-effort per-event extraction). Datadog log queries filter by tags efficiently — `service:toolup scope_id:team-acme` returns one tenant's audit trail.
- **`service`** + **`host`** + **`env`** — Datadog's canonical attribution dimensions. Combined dashboards, alerts, anomaly detection all key off these.
- **`message`** — the SDK's `AuditEvent` as a nested JSON object. Datadog parses nested JSON automatically; `@message.Case:UserLoggedIn` filters by event type without needing custom parsers.

## Datadog region endpoints

Datadog has region-specific intake endpoints. Pick the one matching the deployment's Datadog organization:
- US: `https://http-intake.logs.datadoghq.com/api/v2/logs`
- EU: `https://http-intake.logs.datadoghq.eu/api/v2/logs`
- US3 (Azure): `https://http-intake.logs.us3.datadoghq.com/api/v2/logs`
- US5 (East 1): `https://http-intake.logs.us5.datadoghq.com/api/v2/logs`
- AP1 (Tokyo): `https://http-intake.logs.ap1.datadoghq.com/api/v2/logs`

Sending to the wrong region fails the request — Datadog returns `403 Forbidden` because the API key isn't valid for that region's intake.

## Idempotency on retry

Datadog Logs intake is **best-effort dedup** — entries with the same `service` + `host` + `timestamp` MAY be deduplicated by Datadog's pipeline, but this is not a hard contract. The dispatcher retries entire batches on `Result.Error`; deployments that need stricter at-most-once semantics route through a vendor-side dedup layer (Datadog's `_dd.uuid` is documented for this in some configurations) or accept that occasional duplicates appear in the log stream.

For most regulatory audits this is acceptable: the audit trail is "complete with possible duplicates" rather than "exactly-once". Deduplication in queries via Datadog's `event.id` (extracted from `message.Fields[].EventId` if the AuditEvent's payload carries an Id) is the recommended path when strict counting matters.

## Status code handling

| Datadog response | Sink result | Dispatcher behaviour |
|---|---|---|
| `2xx OK` | `Ok ()` | Cursor advances |
| `400 Bad Request` (malformed body) | `Error (HTTP 400)` | Retried until exhaustion → dead-letter (operator investigates wire format) |
| `401 / 403` (auth) | `Error (HTTP 4xx)` | Retried; persistent → dead-letter (operator rotates key) |
| `429 Too Many Requests` | `Error (HTTP 429)` | Retried per `RetryPolicy` (Datadog's rate limits are usually transient) |
| `5xx Server Error` | `Error (HTTP 5xx)` | Retried per `RetryPolicy` |

## Acceptance test

Real-Datadog integration test gated on `TOOLUP_DATADOG_*` env vars (deferred — needs a stable Datadog account). Unit tests against a fake `HttpClient` handler verify:
- POST URL matches the configured endpoint.
- `DD-API-KEY: <key>` header populated from `ISecretStore`.
- Body is a JSON array (`[ ... ]`) with one entry per event.
- Each entry carries `ddsource`, `ddtags` (with `env:`, `event_type:`, optional `scope_id:`), `service`, `message`.

## Single-instance limitation

Same as every Phase 9g companion. See the S3Archive companion's README for the full explanation.
