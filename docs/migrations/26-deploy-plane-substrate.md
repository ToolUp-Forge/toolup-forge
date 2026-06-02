# Phase 26 — Deploy Plane substrate (substrate + single-node defaults + ServerConfig opt-in)

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
| `DockerLocalContainerScheduler` reference companion | `src/ContainerSchedulers/DockerLocal/` | companion package | ⏸ Track A lane 2 (in flight) |
| `Tests/Contracts/I*Contract.fs` (×4) | `Tests/Contracts/` | tests | ⏸ Track A lane 2 (in flight) |

The deferred items don't block consumers that implement the substrate against their own backend (Diametrical's [Phase 26.C](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/diametrical-roadmap/phases/26-C-toolup-cloud-operation.md) ToolUp Cloud composition; a self-hosted operator on Docker Swarm; a Kubernetes-based shop). The substrate's contract is stable; downstream composes against the interfaces today and the SDK's single-node defaults / reference companion / contract packs land in a follow-up phase commit set.

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
- `DeployManifest.validate` returns the expected `MissingRequiredField` / `InvalidSlug` / `DuplicateDomain` / `ConflictingModuleVersions` errors for malformed inputs (covered by the manifest's own internal logic — formal contract pack lands with Track A lane 2).
- The six-rule portability audit comment block appears at the top of each of the four interface files (`IBuildOrchestrator.fs`, `ITenantFleet.fs`, `IDeployPipeline.fs`, `IContainerScheduler.fs`) and traces each rule to a specific signature decision. This is the **prose** half of the audit; the **executable** half (contract-pack assertions) ships with Track A lane 2.
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
