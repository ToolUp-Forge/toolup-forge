# Phase 26 — Deploy Plane substrate

## What changes

A new substrate ships at the SDK boundary giving the typed contract any deploy backend composes against: four interfaces (`IBuildOrchestrator`, `ITenantFleet`, `IDeployPipeline`, `IContainerScheduler`) plus their supporting types, a `Tenant` entity registration on `IEntityStore`, three single-node F# default implementations, and a `ServerConfig.DeployPlane` opt-in switch wiring them into DI. The substrate remains **interface-first**: every default is replaceable by a distributed-companion DI registration, and `IContainerScheduler` is consumer-supplied (the SDK ships no default — operators wire `DockerLocalContainerScheduler` or a cloud-specific impl).

**No consumer-side behaviour changes by default.** `ServerConfig.DeployPlane = NoDeployPlane` is the SDK-wide default — every existing deployment loads no deploy-plane code, byte-for-byte identical to pre-Phase-26 behaviour (GP 11 / GP 13). Consumers that opt in via `ServerConfig.DeployPlane = SingleNodeDeployPlane` pick up `IBuildOrchestrator` + `IDeployPipeline` + `ITenantFleet` automatically, plus a Tenant entity registration prepended to the entity store.

## What ships in this migration

| Surface | File | Tier | Status |
|---|---|---|---|
| `DeployManifest` + structural validator | `Core/Shared/Types/DeployManifestTypes.fs` | Core (Fable-compatible) | ✅ |
| Substrate identity aliases + supporting types (TenantId / BuildId / DeployId / ContainerId / BuildRequest / DeployState / ContainerSpec / LogEntry / 4 error DUs / TenantHealth) | `Core/Shared/Types/DeployPlaneTypes.fs` | Core (Fable-compatible) | ✅ |
| `Tenant` entity + registration | `Server/Server/TenantEntity.fs` | Server | ✅ |
| `IBuildOrchestrator` | `Server/Server/IBuildOrchestrator.fs` | Server | ✅ |
| `ITenantFleet` | `Server/Server/ITenantFleet.fs` | Server | ✅ |
| `IDeployPipeline` | `Server/Server/IDeployPipeline.fs` | Server | ✅ |
| `IContainerScheduler` | `Server/Server/IContainerScheduler.fs` | Server | ✅ |
| `EntityStoreTenantFleet` single-node default | `Server/Server/EntityStoreTenantFleet.fs` | Server | ✅ |
| `JobSchedulerBuildOrchestrator` single-node default | `Server/Server/JobSchedulerBuildOrchestrator.fs` | Server | ✅ `a631c4a` |
| `DefaultDeployPipeline` single-node default | `Server/Server/DefaultDeployPipeline.fs` | Server | ✅ `cbfd1d4` |
| `ServerConfig.DeployPlaneMode` DU + DI wiring | `Core/Shared/SDK.Shared.fs` + `Server/Compose/ComposeStores.fs` + `Server/SDK.Server.fs` | Core + Server compose | ✅ `4bf6466` |
| `DockerLocalContainerScheduler` reference companion | `src/ContainerSchedulers/DockerLocal/` | companion package | ✅ `1a1bf22` |
| `Tests/Contracts/I*Contract.fs` (×4) + in-process bindings | `Tests/Contracts/` + `Tests/InProcess/DeployPlaneTests.fs` | tests | ✅ `1a1bf22` |

Every substrate surface, single-node default, the reference `DockerLocalContainerScheduler` companion, and the four contract packs now ship together. Operator-side `ServerConfig.DeployPlane = SingleNodeDeployPlane` lights up `IBuildOrchestrator` + `IDeployPipeline` + `ITenantFleet` automatically; `IContainerScheduler` remains consumer-supplied (the reference companion is one valid choice; cloud-specific implementations ship downstream — Diametrical's [Phase 26.C](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/diametrical-roadmap/phases/26-C-toolup-cloud-operation.md) ToolUp Cloud composition; a self-hosted operator on Docker Swarm; a Kubernetes-based shop).

## Placement deviation from the phase spec

The phase file prescribed `Shared/Types/I*.fs` for the four substrate interfaces. The actual ship places them at `Server/I*.fs`. **Reason:** `IContainerScheduler.StreamLogs` returns `IAsyncEnumerable<LogEntry>` — Fable cannot transpile this. `ToolUp.Platform.Core` ships its `Shared/**` source under `fable/` in the nupkg per the Phase 11.C.2 client-tier closure (see `Core/ToolUp.Platform.Core.fsproj`), so any interface compiled into `Core/Shared/` is reachable from every Fable consumer. Placing the four interfaces in `Server/` mirrors the existing convention for `IEntityStore` and `IJobScheduler` (also server-only substrate). The supporting types (`BuildRequest`, `DeploySummary`, `ContainerSpec`, etc.) remain in `Core/Shared/Types/DeployPlaneTypes.fs` so a future Fable admin UI can render deploy state without a DTO round-trip.

This deviation is documented inline in the phase file and is the correct call for the substrate's split; no downstream consumer is affected (the interfaces are server-only by definition).

## Diff to apply (downstream consumers — none required by default)

`NoDeployPlane` is the SDK-wide default, so existing consumers need no change. Consumers wanting to opt in to the substrate's single-node defaults set four `ServerConfig` fields and register one consumer-supplied DI binding:

```fsharp
let serverConfig = {
    ServerConfig.defaults with
        JobScheduler = InProcessJobScheduler        // required dep
        EntityStore = EnabledEntityStore            // required dep (Tenant catalog)
        DeployPlane = SingleNodeDeployPlane         // opt-in switch
        // ... rest of consumer's config
}

// Register a consumer-supplied IContainerScheduler. The SDK ships no
// default — operators wire the dev-grade reference companion or a
// cloud-specific impl:
services.AddSingleton<IContainerScheduler>(
    // DockerLocalContainerScheduler when Track A lane 2 ships, or:
    YourCustomContainerScheduler()
)
```

The SDK's `registerDeployPlane` factory will resolve dependencies at first-use and raise a clear remediation error if any are missing (e.g. `IContainerScheduler` not registered, `JobScheduler = NoJobScheduler`, `EntityStore = NoEntityStore`).

## Verification steps

The following all pass today against the shipped substrate surface:

- `dotnet build src/ToolUp.Platform.Core/ToolUp.Platform.Core.fsproj` — clean.
- `dotnet build src/ToolUp.Platform.Server/ToolUp.Platform.Server.fsproj` — clean.
- `dotnet build src/ContainerSchedulers/DockerLocal/ToolUp.ContainerSchedulers.DockerLocal.fsproj` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "IBuildOrchestrator contract"` — 18/18 pass against `InMemoryBuildOrchestrator`.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "ITenantFleet contract"` — 26/26 pass against `EntityStoreTenantFleet` (binding uses `InMemoryBlobStorage` so the compound-index path materialisation does not touch NTFS — see TIDY-UP entry "Compound-index pipe encoding on Windows" for the substrate-side filesystem-safety fix).
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "IDeployPipeline contract"` — 10/10 pass against `InMemoryDeployPipeline`.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "IContainerScheduler contract"` — 24/24 pass (11 against `InMemoryContainerScheduler`, 13 against `DockerLocalContainerScheduler` when a local Docker socket / Windows named pipe is reachable; otherwise the Docker-backed binding self-skips).
- The six-rule portability audit comment block appears at the top of each of the four interface files (`IBuildOrchestrator.fs`, `ITenantFleet.fs`, `IDeployPipeline.fs`, `IContainerScheduler.fs`); the executable counterpart now lands in the contract packs as `Rule 1 — identity-by-value` / `Rule 2 — async at every boundary` / `Rule 3 — failure flows as data` test cases.
- `NoDeployPlane` (default) registers nothing — no `IBuildOrchestrator` / `IDeployPipeline` / `ITenantFleet` in DI, no Tenant entity registration, no `_platform.build` / `_platform.deploy` event emission. Existing consumers see byte-for-byte unchanged behaviour.
- `SingleNodeDeployPlane` with all four prerequisites satisfied (JobScheduler / EntityStore / IContainerScheduler / EventStore) registers the three defaults end-to-end; a build → deploy → launch round-trip persists events under `_platform.build` and `_platform.deploy` SourceModules.

## Rollback

The substrate is purely additive. Consumers that have not flipped `ServerConfig.DeployPlane = SingleNodeDeployPlane` need no rollback action — `NoDeployPlane` is the default and registers nothing. To revert at the SDK level: drop `ServerConfig.DeployPlane` and the `DeployPlaneMode` DU from `SDK.Shared.fs`, drop the `registerDeployPlane` call from `SDK.Server.fs`, drop the deploy-plane prepend in `registerEntityStore`. No data-layout migration to reverse.

## See also

- [Phase 26 — Layer 3 Deploy Plane substrate](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/roadmap/phases/26-layer-3-deploy-plane-mvp.md) (forge — substrate scope after the 2026-06-02 carve)
- [Diametrical Phase 26.C — ToolUp Cloud operation](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/diametrical-roadmap/phases/26-C-toolup-cloud-operation.md) (commercial body composing against this substrate)
- [Implementation plan](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/application-plans/forge-substrate-26-30a-30d-54-implementation.md) — Track A sub-sequence + risks
- [Phase 9c Six portability rules](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/roadmap/phases/09c-distributed-task-framework-companion-support.md) (audited by every method on the four interfaces)
- [Phase 19 — Entity Store substrate](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/roadmap/phases/19-entity-store-substrate.md) (Tenant is registered as a typed entity here)
