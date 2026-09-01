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
- `Telemetry.track` (`ToolUp.Platform.Client`, Fable) — the browser-side
  helper: consent-gated, then POSTs the event to the server.
- `POST /api/_platform/telemetry` (`ToolUp.Platform.Server`) — the fan-out
  endpoint, which hands the event to the composed `ITelemetrySink` tagged
  with the caller's resolved scope. Mounted **only** under
  `CustomTelemetrySink`.

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

## Emitting from the client

Opting in with `CustomTelemetrySink` also mounts `POST
/api/_platform/telemetry`. From client-tier code:

```fsharp
Telemetry.trackNow {
    Event = "report_exported"
    Properties = Map [ "format", "pdf"; "module", "sales" ]
}
```

`trackNow` is fire-and-forget; `Telemetry.track` is the awaitable shape.
Both are consent-gated (below) and best-effort — a network failure, or the
404 a `NoTelemetrySink` deployment returns, resolves to `unit` rather than
surfacing at the call site.

The server tags the event with the caller's already-resolved config scope
(team id / user id), falling back to the deployment-wide `_platform`
bucket, and hands it to the composed sink. The route declares no
`SurfaceRequirement`, so it inherits the fail-closed `userOrTeam` default
on an authenticating deployment and the `/api/` `public_` default in
Anonymous mode — it is deliberately not registered as a public sink the way
the ad endpoints are, since an unauthenticated write into a third-party
analytics product is an abuse vector.

## Consent + PII

`TelemetryEvent.Properties` are operator-declared keys — the SDK never
auto-populates a user identifier, and the client helper adds nothing of its
own in transit. Analytics-consent gating is **client-side**:
`Telemetry.track` asks the client-tier `IConsentProvider` for
`ConsentCategory.Analytics` and dispatches only on an explicit `Granted`,
so an un-consented event never leaves the browser and there is no window in
which it sits in a server log awaiting deletion. `Denied` and
`NotYetDecided` both suppress (opt-in semantics), as does a provider that
throws. The default `NoOpConsentProvider` grants only `Necessary`, so a
deployment that has wired no CMP suppresses analytics until it does. The
server sink ships whatever reaches it.

## Portability

`ITelemetrySink` satisfies the six portability rules; the
`ITelemetrySinkContract` pack (`ToolUp.Platform.Tests`) validates any sink
(stable `Name`, best-effort `Track`). The no-op default + an in-test
recording sink run it always-on; the GA4 companion runs it env-gated
(`TOOLUP_GA4_MEASUREMENT_ID` + `TOOLUP_GA4_API_SECRET`).

## Rollback

Set `TelemetrySink = NoTelemetrySink` (the default) and drop the companion
`PackageReference`. No persisted-state or wire change.
