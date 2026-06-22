# Phase 177 — Deployment-readiness scorecard

**What changes:** A new opt-in, read-only `IDeploymentReadinessApi` that consolidates the
four already-shipped operability signals — `IConfigValidator` preflight (Phase 9m),
`ISmokeTest` results (Phase 9o), the `ConfigDrift` finding (Phase 9q), and the `IHealthCheck`
aggregate (Phase 9k) — into one Platform-Admin go/no-go verdict
(`Ready | DegradedReady | NotReady`). Pure projection over signals that already exist: no new
gate, no new control-plane behaviour, no new substrate interface.

**Breaking?** No. Additive and **off by default** (`ServerConfig.DeploymentReadiness =
NoReadinessReport`). A deployment that does not opt in is byte-for-byte unchanged — the route
is not mounted and the surface 404s (GP 13). Existing signals are untouched.

## Opting in

```fsharp
open ToolUp.Platform

// Fluent helper (preferred):
let app =
    ServerApp.create config
    |> ServerApp.withDeploymentReadiness
    |> // … rest of composition

// Or set the config field directly:
let config =
    { ServerConfig.defaults with
        DeploymentReadiness = EnabledReadinessReport }

// Or via env var:
//   TOOLUP_DEPLOYMENT_READINESS=enabled
```

Enabling mounts the Platform-Admin-gated `IDeploymentReadinessApi.GetReadinessReport`. The read
is **deployment-wide** (no per-tenant data, GP 4); Anonymous-mode and non-admin callers receive
`Error "platform admin role required"` — same gate shape as `IHealthMonitorApi` /
`IServiceStatusBoardApi`.

## Verdict semantics

| Source state | Contributes |
|---|---|
| preflight `Error` / failed smoke / `Unhealthy` probe | hard failure ⇒ `NotReady` |
| preflight `Warning` / drift detected / `Degraded` probe | soft signal ⇒ `DegradedReady` |
| wired + all-green | `Clean` ⇒ `Ready` (when no failure/warning) |
| substrate not composed | `NotComposed` — never inflates the verdict to `Ready` |

- **Any** hard failure ⇒ `NotReady` (names the failing item(s)).
- Else **any** soft signal ⇒ `DegradedReady`.
- Else **at least one** wired-and-green source (remaining sources may be `NotComposed`) ⇒ `Ready`.
- All sources `NotComposed` (empty scorecard) ⇒ `DegradedReady` — an absent signal cannot attest
  full readiness, and a `NotComposed` source never fabricates a pass.

A source's `NotComposed` state is decided per-source: preflight by `IPreflightSnapshot` presence;
smoke by `SmokeTest = EnabledSmokeTest`; drift by `ConfigDriftDetection =
EnabledConfigDriftDetection`; health by whether any `IHealthCheck` is registered. The scorecard
**runs** the registered `ISmokeTest` + `IHealthCheck` probes live (against the reserved `_smoke`
sentinel scope for smoke) so the verdict reflects "right now".

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — `Platform.Tests` pack green, including the
  Phase 177 `DeploymentReadiness scorecard` truth-table + handler-gate tests.

## Rollback

Remove the `ServerApp.withDeploymentReadiness` call (or set `DeploymentReadiness =
NoReadinessReport`). No data migration, no persisted state — the report is a pure read.
