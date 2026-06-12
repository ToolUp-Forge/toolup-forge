# Phase 9e.A — Auth-pipeline `IMetricsSink` DI registration (consumer migration)

**What changes.** The OIDC and Entra External ID auth providers now bind their `IMetricsSink` via construction rather than via a process-wide setter. The prior `OidcAuthProvider.setMetricsSink` / `EntraExternalIdAuthProvider.setMetricsSink` setters (introduced under Cluster D1 alongside the `AuthMetrics` counter names) and their backing `let mutable private metricsSink: IMetricsSink option = None` cells are gone. Each provider instance carries its own sink in its closure; there is no module-level state to leak between deployments, tests, or hot-reload cycles.

**Scope.** Server-side only — `ToolUp.AuthProviders.Oidc` and `ToolUp.AuthProviders.EntraExternalId`. No client-side change. No on-the-wire change. No change to `AuthMetrics` counter names or `provider=` tag values.

**Backward compatibility.** Every existing public function signature is preserved:

- `OidcAuthProvider.fromConfig` — unchanged.
- `OidcAuthProvider.fromConfigWith` — unchanged.
- `EntraExternalIdAuthProvider.create` — unchanged.
- `EntraExternalIdAuthProvider.createWith` — unchanged.
- `EntraExternalIdAuthProvider.fromEnv` — unchanged.
- `AuthProvider.fromEnv` (in `ToolUp.Platform.Server`) — unchanged.
- `OidcAuthBuilder` type alias — unchanged.

Consumers that never wired metrics (the entire workspace at the time of writing — `setMetricsSink` had no live call sites) compile and run identically. No record literal grows a field, no callback signature shifts, no opt-in is needed.

**What was added.** Four new public functions on the providers plus one new function + type alias in the env-driven dispatcher:

| New surface | Module | Purpose |
|---|---|---|
| `OidcAuthProvider.fromConfigMetered` | `ToolUp.AuthProviders.Oidc` | Production shorthand with `IMetricsSink option` parameter; uses the lazy default `HttpClient`. |
| `OidcAuthProvider.fromConfigWithMetrics` | `ToolUp.AuthProviders.Oidc` | Custom-`HttpClient` variant with `IMetricsSink option` parameter. Tests use this with stub handlers. |
| `EntraExternalIdAuthProvider.createMetered` | `ToolUp.AuthProviders.EntraExternalId` | Production shorthand with metrics. |
| `EntraExternalIdAuthProvider.createWithMetrics` | `ToolUp.AuthProviders.EntraExternalId` | Custom-`HttpClient` variant with metrics. |
| `EntraExternalIdAuthProvider.fromEnvMetered` | `ToolUp.AuthProviders.EntraExternalId` | Env-driven variant with metrics. |
| `OidcAuthBuilderMetered` | `ToolUp.Platform.AuthProvider` | Type alias: `ILogger option -> IMetricsSink option -> AuthConfig -> IAuthProvider`. |
| `AuthProvider.fromEnvMetered` | `ToolUp.Platform.AuthProvider` | Env-driven dispatcher that threads `IMetricsSink option` through to the OIDC builder. |

**What was removed.**

- `OidcAuthProvider.setMetricsSink: IMetricsSink -> unit`.
- `EntraExternalIdAuthProvider.setMetricsSink: IMetricsSink -> unit`.
- The backing `let mutable private metricsSink: IMetricsSink option = None` cell in both modules.

Consumers calling `setMetricsSink` will see a compile error. The fix is to migrate to the `*Metered` / `*WithMetrics` constructors below; no workspace consumer was on this path at migration time.

## When to opt in

If your deployment registers a real `IMetricsSink` (e.g. the Prometheus sink shipped by default when `ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint`, or the `OtelMetricsSink` companion), wiring the auth provider through the metered constructors lights up the standard `toolup.auth.validate.*` counters on the same observability backend that already carries `toolup.requests.*` / `toolup.errors.*` / `toolup.sse.*`.

If your deployment leaves metrics disabled (`NoMetricsEndpoint`, the default), there is nothing to do — `fromConfig` / `create` / `fromEnv` continue to elide emission. The `NoOpMetricsSink` registered by the SDK is what they bind internally when no sink is supplied.

## Diff to apply

### Pattern 1 — direct construction with a resolved sink

If your composition root resolves the SDK's `IMetricsSink` from DI (via `ServiceProvider.GetService<IMetricsSink>()` or equivalent) and you want the OIDC / Entra provider to emit counters against it:

```fsharp
// Before (no metrics on the auth pipeline):
let auth =
    OidcAuthProvider.fromConfig (Some logger) authConfig

ServerApp.empty
|> ServerApp.withAuth auth
|> ...

// After (auth-pipeline counters emit via the resolved sink):
let metrics: IMetricsSink option =
    services.GetService<IMetricsSink>() |> Option.ofObj

let auth =
    OidcAuthProvider.fromConfigMetered (Some logger) metrics authConfig

ServerApp.empty
|> ServerApp.withAuth auth
|> ...
```

`fromConfigMetered` with `metrics = None` behaves identically to `fromConfig` — useful for composition roots that conditionally resolve metrics and want a single construction path.

### Pattern 2 — env-driven dispatch via `AuthProvider.fromEnv`

If your composition root uses the env-var-driven helper from Phase 11.G:

```fsharp
// Before (no metrics):
let auth =
    AuthProvider.fromEnv logger ToolUp.AuthProviders.OidcAuthProvider.fromConfig

ServerApp.empty
|> ServerApp.withAuth auth
|> ...

// After (env-driven dispatch with metrics threaded):
let metrics: IMetricsSink option =
    services.GetService<IMetricsSink>() |> Option.ofObj

let auth =
    AuthProvider.fromEnvMetered
        logger
        metrics
        ToolUp.AuthProviders.OidcAuthProvider.fromConfigMetered

ServerApp.empty
|> ServerApp.withAuth auth
|> ...
```

`fromEnvMetered` shares all dispatch behaviour with `fromEnv` — including the auth-hardening sweep's fail-fast dispatch (2026-06-12): an explicit `TOOLUP_AUTH_MODE=oidc` without `TOOLUP_OIDC_ISSUER`, and any unrecognised `TOOLUP_AUTH_MODE` value, now **refuse startup** (`invalidOp`) instead of warning and falling back; only a genuinely-unset mode falls back to the dev `HeaderAuthProvider`. Metrics only flow when `TOOLUP_AUTH_MODE=oidc` resolves to the OIDC branch.

### Pattern 3 — Entra External ID

Mirrors Pattern 1, on the Entra companion:

```fsharp
// Before:
let auth = EntraExternalIdAuthProvider.create (Some logger) config

// After:
let metrics = services.GetService<IMetricsSink>() |> Option.ofObj
let auth = EntraExternalIdAuthProvider.createMetered (Some logger) metrics config
```

For env-driven Entra wiring:

```fsharp
// Before:
let auth =
    EntraExternalIdAuthProvider.fromEnv (Some logger)
    |> Option.defaultWith (fun () -> failwith "TOOLUP_ENTRA_EXTERNAL_ID_TENANT not set")

// After:
let metrics = services.GetService<IMetricsSink>() |> Option.ofObj
let auth =
    EntraExternalIdAuthProvider.fromEnvMetered (Some logger) metrics
    |> Option.defaultWith (fun () -> failwith "TOOLUP_ENTRA_EXTERNAL_ID_TENANT not set")
```

### Pattern 4 — retiring `setMetricsSink`

If any consumer code reaches into the provider modules via the now-removed setter, replace at the construction site:

```fsharp
// Before — compile error after upgrade:
let auth = OidcAuthProvider.fromConfig (Some logger) authConfig
OidcAuthProvider.setMetricsSink resolvedSink   // ← gone

// After:
let auth =
    OidcAuthProvider.fromConfigMetered (Some logger) (Some resolvedSink) authConfig
```

No deployment in the workspace was on this path — the setter pattern was newly introduced under Cluster D1 and never wired by a real compose path. The migration is mechanical for any out-of-tree consumer that anticipated it.

## Verification

1. `dotnet build` against your composition root project — record the new `IMetricsSink` resolve in DI is reachable and the metered constructor receives it.
2. If you have a metrics scrape (`/metrics` endpoint, OTel exporter), confirm a successful authenticated request increments `toolup.auth.validate.success_total{provider="oidc"}` (or `provider="entra-external-id"`).
3. A token-less request increments `toolup.auth.validate.no_token_total`.
4. An expired token increments `toolup.auth.validate.expired_total`.
5. The provider's per-instance binding is verifiable in tests — see `AuthProviderTests.fs` "OidcAuthProvider — metrics emission" which asserts two provider instances bind independent sinks (no cross-instance leakage).

## Rollback

If a regression surfaces, the legacy constructors are still in place; reverting the call site from `fromConfigMetered` back to `fromConfig` (and dropping the `metrics` argument) restores the no-emission behaviour without any other change. No record migration, no env-var change, no DB shape change.
