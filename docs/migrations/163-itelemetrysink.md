# Phase 163 — `ITelemetrySink` substrate

**What changes.** A clean seam for **end-user product telemetry** (page
views, funnel events, feature usage) — distinct from `IEventStore` /
`IAuditLog` (system-of-record) and `IMetricsSink` (operational metrics).

New surface:
- `ITelemetrySink` (`ToolUp.Platform.Core`): `Name` + `Track(scopeId,
  event)` (async, best-effort, never throws across the boundary).
- `TelemetryEvent = { Event; Properties: Map<string,string> }` —
  operator-declared properties, **no SDK-populated PII**.
- `NoOpTelemetrySink` — the true-no-op default.
- `ToolUp.TelemetrySinks.Ga4` — a GA4 Measurement Protocol companion over
  BCL `HttpClient` (GP 1 — no vendor SDK; GP 2 — nothing paid).
- `ServerConfig.TelemetrySink: TelemetrySinkMode` (`NoTelemetrySink`
  default → registers `NoOpTelemetrySink`; `CustomTelemetrySink` → a
  companion sink the consumer registers).

**Consumer action: none by default (GP 11 / GP 13).** `NoTelemetrySink`
(the default) registers the no-op sink, so emission sites are free. A
deployment opts in only to ship analytics.

## Adopt (opt-in)

```fsharp
open System.Net.Http
open ToolUp.TelemetrySinks.Ga4

let sink = Ga4TelemetrySink.create httpClient "G-XXXXXXX" apiSecret  // apiSecret from ISecretStore
services.AddSingleton<ITelemetrySink>(sink) |> ignore
ServerConfig.defaults with TelemetrySink = CustomTelemetrySink
```

Resolve `ITelemetrySink` from DI in module server code and call `Track`.

## Consent + PII

`TelemetryEvent.Properties` are operator-declared keys — the SDK never
auto-populates a user identifier. Analytics-consent gating belongs
**client-side**: gate the (forthcoming) `Telemetry.track` client helper
against the client-tier `IConsentProvider` before an event leaves the
browser, so analytics that never ships can never breach consent. The
server sink ships whatever reaches it.

## Deferred (client transport)

The client-side `Telemetry.track` Fable helper + the server fan-out
endpoint are a deferred follow-on — the **seam** (sink + no-op default +
GA4 companion + contract + `ServerConfig` mode) is the substrate this phase
lands ahead of demand; the client transport (with its consent gate) lands
when a consumer wires product analytics. All sink-side acceptance criteria
(no-op default, scope-tagged delivery, vendor isolation, contract pack) are
met by this substrate.

## Portability

`ITelemetrySink` satisfies the six portability rules; the
`ITelemetrySinkContract` pack (`ToolUp.Platform.Tests`) validates any sink
(stable `Name`, best-effort `Track`). The no-op default + an in-test
recording sink run it always-on; the GA4 companion runs it env-gated
(`TOOLUP_GA4_MEASUREMENT_ID` + `TOOLUP_GA4_API_SECRET`).

## Rollback

Set `TelemetrySink = NoTelemetrySink` (the default) and drop the companion
`PackageReference`. No persisted-state or wire change.
