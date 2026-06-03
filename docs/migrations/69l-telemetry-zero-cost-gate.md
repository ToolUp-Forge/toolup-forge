# Phase 69l — Telemetry seam zero-cost gate (migration)

## What changes

`Api.make`'s default `IRemotingTelemetry` bridge now pairs with a `defaultBridgeGate` that short-circuits the per-request `Stopwatch` + `MethodTelemetry` allocation when the registered `IMetricsSink` resolves to `NoOpMetricsSink` (the `NoMetricsEndpoint` default).

Before Phase 69l, every `Api.make`-composed API allocated a `Stopwatch`, a `MethodTelemetry` record, a tags `Map`, and did a trailing-segment `Substring` + DI service-locator lookup on every request — even when the resolved sink was `NoOpMetricsSink` and the work was unobserved. The terminating `Record` call was a no-op, but everything upstream paid in full. GP 13 ("advanced behaviour is opt-in; deployments that don't use it pay nothing") was technically violated.

Phase 69l adds:

- **`RemotingOptions.TelemetryGate: (unit -> bool) option`** — a new optional field on the `RemotingOptions` record. When `Some`, the dispatcher invokes the gate before allocating the per-request telemetry record. `false` skips the allocation entirely.
- **`Remoting.withTelemetryGate gate options`** — the setter consumers can pair with their own `IRemotingTelemetry` if they want gated emission.
- **`ApiSeams.defaultBridgeGate`** — the gate the `Api.make` wrapper composes against the default bridge. Process-memoised; resolves `IMetricsSink` from the AsyncLocal-stashed `IServiceProvider` once, then caches the bool answer. Returns `false` when the sink is `NoOpMetricsSink`; `true` otherwise (consumer-installed companion sinks fall through to `true`).
- Both the non-streaming and SSE-streaming branches of `GiraffeAdapter.fs` gate the `Stopwatch` + `MethodTelemetry` allocation on the new `telemetryActive` discriminant.

Consumer-supplied `?telemetry` sinks bypass the gate — the consumer opted in to telemetry, so the dispatcher emits unconditionally for them.

## Diff to apply

**No consumer source change required.** The fix is internal to the dispatcher and the `Api.make` wrapper. Existing `Api.make (api, errorHandler = eh, ...)` call sites pick up the gate automatically at the next package upgrade.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — green (1,794 passed, 8 ignored, 0 failed; the new `Phase 69l — Telemetry seam zero-cost gate` pack adds 9 source-audit tests pinning the gate composition + dispatcher wiring).
- The Phase 69l test pack lives in [`src/ToolUp.Platform.Tests/InProcess/TelemetryZeroCostGateTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/TelemetryZeroCostGateTests.fs). It textually pins (a) `RemotingOptions.TelemetryGate` field declaration, (b) `Remoting.withTelemetryGate` setter presence, (c) `createApi` defaults `TelemetryGate = None`, (d) `ApiSeams.defaultBridgeGate` exists and detects `NoOpMetricsSink`, (e) `Api.make` composes `withTelemetryGate ApiSeams.defaultBridgeGate` conditionally on `telemetryIsDefault`, (f) both dispatcher branches read the gate before allocation.
- An integration-shape allocation-pin test (compose a tracking `IRemotingTelemetry`, dispatch N requests through a `TestServer`, assert zero `OnMethodCompleted` invocations when `MetricsEndpoint = NoMetricsEndpoint`) is the right next step but requires a TestServer scaffold the `ToolUp.Platform.Tests` runner does not have today. Tracked as a follow-up TIDY-UP item.

## Rollback

Revert the gate composition in `ToolUp.Platform.Server/Server/Api.fs` (the `|> if telemetryIsDefault then Remoting.withTelemetryGate ApiSeams.defaultBridgeGate else id` line) AND the dispatcher gates in `ToolUp.Platform.Server/Server/Remoting/Giraffe/GiraffeAdapter.fs` (the `telemetryActive` / `streamingTelemetryActive` discriminants + the `if … then Stopwatch.StartNew() else …` guards). `RemotingOptions.TelemetryGate` may stay as an additive field; the dispatcher with `TelemetryGate = None` (the `createApi` default) emits exactly as pre-69l.

## See also

- [Phase 69b — `ToolUp.Remoting.Server` platform seams](../../../ToolUp-Diametrical/roadmap/phases/69b-toolup-remoting-server-platform-seams.md) — the substrate this gate composes on top of.
- [Phase 69b.tail — Forge `Api.fs` wrapper composition](../../../ToolUp-Diametrical/roadmap/phases/69b-tail-forge-api-wrapper-composition.md) — the wrapper that introduced the default-bridge substitution this phase makes zero-cost.
- [Phase 9e — `IMetricsSink` substrate](../../../ToolUp-Diametrical/roadmap/phases/09e-imetricssink-substrate.md) — the singleton sink the default bridge reads.
- Source plan: [`application-plans/toolup-remoting-hot-path-perf.md`](../../../ToolUp-Diametrical/application-plans/toolup-remoting-hot-path-perf.md) (Finding F1).
- Companion TIDY-UP bundle: [`ToolUp.Remoting per-request cleanups`](../../../ToolUp-Diametrical/roadmap/TIDY-UP.md).
