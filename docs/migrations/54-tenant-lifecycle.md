# Phase 54 — `ITenantLifecycle` tenant-lifecycle substrate

## What changes

A new **opt-in** substrate consolidates tenant provision / offboard
choreography. Two consumer-visible additions, both default-off so an
existing deployment that upgrades stays byte-for-byte identical until it
opts in (GP 11 + GP 13):

1. **`ServerConfig.TenantLifecycle : TenantLifecycleMode`** — `NoTenantLifecycle`
   (default) or `EnabledTenantLifecycle`. When enabled, `compose`
   registers the four first-party `ITenantLifecycle` hooks + a snapshot
   holder and mounts the admin API.
2. **`IPlatformTenantApi`** at `/api/_platform/tenants/*` (Owner /
   Platform-Admin gated) — `ProvisionTenant` / `DeprovisionTenant` /
   `GetLifecycleSummary`. One `DeprovisionTenant` call runs every
   registered hook in parallel with per-hook timeout + isolation,
   returns a `LifecycleSummary`, and writes a `TenantDeprovisioned`
   audit row (`SourceModule = "_platform.tenant"`).

The four first-party hooks wrap already-shipped surfaces and each
`Skipped`s when its substrate is inactive, so enabling the mode on a
minimal deployment is safe:

| Hook | Wraps | Skips when |
|---|---|---|
| `encryption-key` | `PerScopeKeyResolver.DestroyKey` (crypto-shred) | resolver is not `PerScopeKeyResolver` |
| `membership-cache` | `ITeamStore.GetTeamMembers` + `TeamScopeResolver.InvalidateUser` | scope resolver is not `TeamScopeResolver` |
| `job-scheduler` | `IJobScheduler.ListJobs` + `Cancel` per job | no `IJobScheduler` registered |
| `data-erasure` | registered `IErasureHandler`s (`HardDelete`) | no `IErasureHandler` registered |

## Diff to apply (consumer opt-in)

No change is required to keep today's behaviour. To adopt:

```fsharp
// ServerConfig — opt in.
{ config with TenantLifecycle = EnabledTenantLifecycle }
```

Companions can register their own hook additively (the aggregator
resolves the full `seq<ITenantLifecycle>` at request time):

```fsharp
services.AddSingleton<ITenantLifecycle>(fun sp -> MyCompanionLifecycle.create sp)
```

Calling the offboard from an operator/admin client:

```fsharp
let api = Remoting.createApi () |> Remoting.buildProxy<IPlatformTenantApi>
let! result = api.DeprovisionTenant("team-acme", actorUserId, "contract ended")
// Owner / Platform-Admin only; non-admins receive Error "platform admin role required".
// result : Result<LifecycleSummary, string> — Outcomes carry per-hook
// Completed / Skipped reason / Failed error; the run never aborts on a failure.
```

## Verification steps

1. `dotnet build src/ToolUp.Platform.Server/ToolUp.Platform.Server.fsproj` — clean.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — full suite passes (2,023 / 2,023).
3. With `NoTenantLifecycle` (default): `/api/_platform/tenants/DeprovisionTenant` 404s and no `ITenantLifecycle` resolves from DI (byte-for-byte unchanged).
4. With `EnabledTenantLifecycle` on a minimal deployment: `DeprovisionTenant` returns a `LifecycleSummary` of four `Skipped` outcomes (no substrate wired) + a `TenantDeprovisioned` audit row.

## Rollback

Additive + opt-in — no existing API surface changes. Revert by setting
`TenantLifecycle = NoTenantLifecycle` (or rolling back the forge bump).
Consumers that never enabled it are unaffected.

## Deferred follow-ons

- `data-erasure` runs erasure **synchronously inline**; routing long
  multi-store erasures through the Phase 9h.A async / `IJobScheduler`-backed
  path is a follow-on. Absent 9h.A the synchronous path is the graceful
  degrade, bounded by the 5-min per-hook `OnDeprovisioned` timeout.
- A separate `INotificationChannel` end-of-offboard push is not
  implemented; the `TenantDeprovisioned` audit row is the durable marker.
