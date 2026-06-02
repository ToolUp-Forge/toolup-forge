# Phase 26 — Deploy Plane substrate (partial — substrate surface only)

## What changes

A new substrate ships at the SDK boundary giving the typed contract any deploy backend composes against: four interfaces (`IBuildOrchestrator`, `ITenantFleet`, `IDeployPipeline`, `IContainerScheduler`) plus their supporting types and a `Tenant` entity registration on `IEntityStore`. The substrate is **interface-first**: this migration covers the contract surface and one of the three single-node default implementations (`EntityStoreTenantFleet`). The remaining defaults (`JobSchedulerBuildOrchestrator`, `DefaultDeployPipeline`), the reference `IContainerScheduler` companion (`DockerLocalContainerScheduler`), the four contract test packs, and the `ServerConfig.DeployPlane` wiring all ship in follow-up work — they are not blocking the substrate's contract surface for downstream consumers that wire their own implementations.

**No consumer-side behaviour changes by default.** The substrate is fully opt-in. Consumers that do not register any of the four interfaces in DI continue to load no deploy-plane code — byte-for-byte identical to today.

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
| `JobSchedulerBuildOrchestrator` single-node default | `Server/Server/JobSchedulerBuildOrchestrator.fs` | Server | ⏸ deferred |
| `DefaultDeployPipeline` single-node default | `Server/Server/DefaultDeployPipeline.fs` | Server | ⏸ deferred |
| `DockerLocalContainerScheduler` reference companion | `src/ContainerSchedulers/DockerLocal/` | companion package | ⏸ deferred |
| `Tests/Contracts/I*Contract.fs` (×4) | `Tests/Contracts/` | tests | ⏸ deferred |
| `ServerConfig.DeployPlaneMode` DU + DI wiring | `Core/Shared/SDK.Shared.fs` + `Server/Compose/BuildRouteHandlers.fs` | Core + Server compose | ⏸ deferred |

The deferred items don't block consumers that implement the substrate against their own backend (Diametrical's [Phase 26.C](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/diametrical-roadmap/phases/26-C-toolup-cloud-operation.md) ToolUp Cloud composition; a self-hosted operator on Docker Swarm; a Kubernetes-based shop). The substrate's contract is stable; downstream composes against the interfaces today and the SDK's single-node defaults / reference companion / contract packs land in a follow-up phase commit set.

## Placement deviation from the phase spec

The phase file prescribed `Shared/Types/I*.fs` for the four substrate interfaces. The actual ship places them at `Server/I*.fs`. **Reason:** `IContainerScheduler.StreamLogs` returns `IAsyncEnumerable<LogEntry>` — Fable cannot transpile this. `ToolUp.Platform.Core` ships its `Shared/**` source under `fable/` in the nupkg per the Phase 11.C.2 client-tier closure (see `Core/ToolUp.Platform.Core.fsproj`), so any interface compiled into `Core/Shared/` is reachable from every Fable consumer. Placing the four interfaces in `Server/` mirrors the existing convention for `IEntityStore` and `IJobScheduler` (also server-only substrate). The supporting types (`BuildRequest`, `DeploySummary`, `ContainerSpec`, etc.) remain in `Core/Shared/Types/DeployPlaneTypes.fs` so a future Fable admin UI can render deploy state without a DTO round-trip.

This deviation is documented inline in the phase file and is the correct call for the substrate's split; no downstream consumer is affected (the interfaces are server-only by definition).

## Diff to apply (downstream consumers — none required today)

Consumers do not need to apply any change yet. The substrate is opt-in via `ServerConfig.DeployPlane` (deferred), and no existing consumer composes against the four interfaces. When the deferred items land, this migration doc will be amended with the consumer-side wiring (DI registration, env vars, `ServerConfig` field).

## Verification steps

The following all pass today against the shipped substrate surface:

- `dotnet build src/ToolUp.Platform.Core/ToolUp.Platform.Core.fsproj` — clean.
- `dotnet build src/ToolUp.Platform.Server/ToolUp.Platform.Server.fsproj` — clean.
- `DeployManifest.validate` returns the expected `MissingRequiredField` / `InvalidSlug` / `DuplicateDomain` / `ConflictingModuleVersions` errors for malformed inputs (covered by the manifest's own internal logic — formal contract pack lands with the deferred work).
- The six-rule portability audit comment block appears at the top of each of the four interface files (`IBuildOrchestrator.fs`, `ITenantFleet.fs`, `IDeployPipeline.fs`, `IContainerScheduler.fs`) and traces each rule to a specific signature decision. This is the **prose** half of the audit; the **executable** half (contract-pack assertions) is part of the deferred work.

## Rollback

The substrate is purely additive. To revert: delete the eight new `.fs` files, the one modified record (`TenantEntity.fs`'s removal of the inline `TenantId = string`), and unwind the two `.fsproj` `<Compile>` additions. No consumer behaviour reverts; no data-layout migration to reverse.

## See also

- [Phase 26 — Layer 3 Deploy Plane substrate](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/roadmap/phases/26-layer-3-deploy-plane-mvp.md) (forge — substrate scope after the 2026-06-02 carve)
- [Diametrical Phase 26.C — ToolUp Cloud operation](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/diametrical-roadmap/phases/26-C-toolup-cloud-operation.md) (commercial body composing against this substrate)
- [Implementation plan](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/application-plans/forge-substrate-26-30a-30d-54-implementation.md) — Track A sub-sequence + risks
- [Phase 9c Six portability rules](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/roadmap/phases/09c-distributed-task-framework-companion-support.md) (audited by every method on the four interfaces)
- [Phase 19 — Entity Store substrate](https://github.com/ToolUp-Diametrical/ToolUp-Diametrical/blob/main/roadmap/phases/19-entity-store-substrate.md) (Tenant is registered as a typed entity here)
