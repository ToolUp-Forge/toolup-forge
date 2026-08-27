# ToolUp.TelemetrySinks.Ga4

GA4 Measurement Protocol-backed `ITelemetrySink` for ToolUp.Platform
(Phase 163). Ships end-user product-analytics events (page views, funnel
events, feature usage) to Google Analytics 4 over BCL `HttpClient` — no
vendor SDK, so nothing reaches `ToolUp.Platform.*` (GP 1) and nothing paid
is pulled (GP 2). Server-only companion.

`ITelemetrySink` is distinct from `IEventStore`/`IAuditLog` (the
system-of-record) and `IMetricsSink` (operational metrics) — it is
best-effort behavioural analytics. `TelemetryEvent.Properties` are
operator-declared keys; the SDK never auto-populates PII.

## Quick start

```fsharp skip=fragment
open System.Net.Http
open ToolUp.TelemetrySinks.Ga4

// Resolve the api secret from your ISecretStore — don't hard-code it.
let sink = Ga4TelemetrySink.create httpClient "G-XXXXXXX" apiSecret

// Compose:
//   ServerConfig.TelemetrySink = CustomTelemetrySink
//   services.AddSingleton<ITelemetrySink>(sink)
```

Each `Track(scopeId, event)` POSTs one event to
`https://www.google-analytics.com/mp/collect`, sending the per-tenant
`scopeId` as the GA4 `client_id` and the event properties as `params`.
Delivery is best-effort — a transport failure is swallowed (never thrown
across the boundary), so telemetry never breaks the request that emitted it.

## Consent

Analytics consent gating belongs **client-side**: gate `Telemetry.track`
against the client-tier `IConsentProvider` before an event leaves the
browser, so analytics that never ships can never breach consent. This
server sink ships whatever reaches it.

## Verification

The sink requires live GA4 stream credentials (no offline fake). Mirrors
the env-gated live-arm convention of the storage / AI-provider companions:
the `ITelemetrySinkContract` pack runs against this sink in
`ToolUp.Platform.Tests` when `TOOLUP_GA4_MEASUREMENT_ID` +
`TOOLUP_GA4_API_SECRET` are set; unset, the arm reports skipped, so a fresh
checkout is green without credentials. The no-op default + an in-test
recording sink run the same pack always-on.

## License

Apache-2.0.
