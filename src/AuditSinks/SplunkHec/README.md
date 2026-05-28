# Splunk HEC audit sink

Phase 9g `IAuditSink` companion. POSTs every audit batch to a Splunk HTTP Event Collector endpoint with an HEC token from `ISecretStore`. No Splunk SDK dependency — uses BCL `HttpClient` directly.

## How to enable

1. Reference this companion's `Server.props` in the consuming server project's `.fsproj` and add a `<ProjectReference>` to its `.fsproj`.

2. Provision a Splunk HEC token (Splunk admin UI → Settings → Data Inputs → HTTP Event Collector → New Token). Note the token value.

3. Store the token in `ISecretStore` under the `_platform` scope:

   ```fsharp
   do! secretStore.SetSecret("_platform", "splunk_hec_token", "abc123-def456-...")
   ```

   Or load it from environment variables via `EnvironmentSecretStore` — `TOOLUP_SECRET_PLATFORM_SPLUNK_HEC_TOKEN=abc123...`.

4. Construct the sink and register:

   ```fsharp
   open ToolUp.Platform.AuditSinks.SplunkHec
   open System.Net.Http

   let httpClient = new HttpClient()
   let settings: SplunkHecSettings = {
       EndpointUrl = "https://splunk.example.com:8088/services/collector/event"
       Sourcetype = "toolup_audit"
       Index = Some "audit"
       Host = Some "toolup-prod"
   }
   let sink = SplunkHec.create "splunk-prod" settings secretStore "splunk_hec_token" httpClient

   ServerApp.empty
   |> ServerApp.withAuditSink sink
   |> ServerApp.run
   ```

## Wire format

Splunk HEC's `/services/collector/event` endpoint accepts newline-delimited JSON. Each line:

```json
{"event":{"Case":"UserLoggedIn","Fields":[{"UserId":"u123","AuthProvider":"Header"}]},"sourcetype":"toolup_audit","_meta":{"uuid":"...","event_type":"UserLoggedIn"},"index":"audit","host":"toolup-prod"}
```

- **`event`** — the SDK's `AuditEvent` JSON, serialised via `FableJsonConverter` (the SDK's canonical converter). Splunk's `spath` extracts fields directly; SPL queries like `index=audit "Case"="UserLoggedIn" Fields{}.UserId="u123"` work without further processing.
- **`sourcetype`** — `toolup_audit` by default. Splunk admins use this to route audit events to a dedicated index, dashboards, and alerts without needing to inspect the payload.
- **`_meta.uuid`** — random GUID per event for Splunk-side deduplication on retry. The dispatcher retries entire batches on `Result.Error`, so an event may be POSTed multiple times if Splunk transiently fails after accepting some events but before completing the response. Splunk's `_meta.uuid` is the dedup key.
- **`_meta.event_type`** — wire-format event-type name (mirrors `AuditEvent.eventTypeName`). Splunk indexes this as a top-level field for fast type filtering.
- **`index`** / **`host`** — optional pins. Configure at the SDK side or rely on the HEC token's defaults.

## Authentication + token rotation

`Authorization: Splunk <token>` header. The token is read from `ISecretStore` on every `Deliver` call — no caching. Rotated tokens flow through immediately (next batch picks up the new value); stale tokens fail with `Result.Error` after Splunk returns 401, and the dispatcher exhausts retries.

## Status code handling

| Splunk response | Sink result | Dispatcher behaviour |
|---|---|---|
| `200 OK` | `Ok ()` | Cursor advances |
| `4xx Bad Request` | `Error (HTTP 4xx body)` | Retried per `RetryPolicy`; if exhausted, dead-lettered (operators investigate token / format) |
| `5xx Server Error` | `Error (HTTP 5xx body)` | Retried per `RetryPolicy` (transient infra) |
| Connection refused / DNS fail | `Error (exception message)` | Retried per `RetryPolicy` |

The sink doesn't distinguish 4xx from 5xx at the interface — both surface as `Result.Error` and the dispatcher's retry policy decides. Tightening this (skipping retries on 4xx) would require the dispatcher to interpret error strings, which would couple the dispatcher to vendor diagnostics.

## Acceptance test

A real-Splunk integration test is gated on `TOOLUP_SPLUNK_HEC_*` env vars (deferred — needs a stable Splunk HEC environment to run against). The unit test against a fake `HttpClient` handler verifies:
- POST URL matches the configured endpoint.
- `Authorization: Splunk <token>` header populated from `ISecretStore`.
- Body is newline-delimited JSON with one line per event.
- Each line carries `event`, `sourcetype`, `_meta.uuid`, `_meta.event_type`.

The contract pack (`IAuditSinkContract`) runs against this companion using a fake HTTP handler that records the POST body — same shape as the SMTP sink's tests against an unreachable host.

## Single-instance limitation

Same as every Phase 9g companion — the replicator is in-process. Multi-silo deployments running the same sink double-deliver until Phase 9c half 2's distributed lock lands. Document this in the deployment's compliance posture (or run the audit-emitting tier as `replicas: 1` until then).
