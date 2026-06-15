module ToolUp.Platform.DeployPlaneDepsValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 26 follow-up (Investigate-gaps #6) — deploy-plane preflight ──
//
// `SingleNodeDeployPlane` wires `IBuildOrchestrator` / `IDeployPipeline`
// / `ITenantFleet` as lazy DI factories that `failwith` at FIRST RESOLVE
// when `IJobScheduler` / `IContainerScheduler` / `IEntityStore` are
// missing (see `registerDeployPlane` in `Compose/ComposeStores.fs`). That
// means a misconfigured deploy plane starts green and only 500s when the
// first request reaches one of those services — production unavailability
// hidden until traffic arrives, with no startup signal.
//
// This validator surfaces the same three missing-dependency conditions at
// compose-time preflight (as a `Warning`), so the operator sees the
// likely-missing wiring at startup rather than at first request. It is a
// Warning, not an Error, because the three services are lazy DI factories
// — a deployment may compose `SingleNodeDeployPlane` and legitimately use
// only a subset, so a hard abort would false-positive that case. The
// per-service resolution-time `failwith`s stay as the usage-accurate hard
// gate.

/// Warn when `SingleNodeDeployPlane` is composed with any deploy-plane
/// dependency unregistered. Inspects the live `IServiceCollection` for the
/// three interfaces the deploy-plane factories require, mirroring the
/// first-resolve guards in `registerDeployPlane`. `NoDeployPlane`
/// (default) validates to `Ok`.
type DeployPlaneDepsValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    let isRegistered (t: Type) =
        services
        |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType = t)

    interface IConfigValidator with
        member _.Name = "deploy-plane-deps"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.DeployPlane with
            | NoDeployPlane -> return Ok
            | SingleNodeDeployPlane ->
                let missing = [
                    if not (isRegistered typeof<IJobScheduler>) then
                        "IJobScheduler (set ServerConfig.JobScheduler = InProcessJobScheduler, or register a distributed scheduler companion) — required by IBuildOrchestrator"
                    if
                        config.EntityStore <> EnabledEntityStore
                        || not (isRegistered typeof<IEntityStore.IEntityStore>)
                    then
                        "IEntityStore (set ServerConfig.EntityStore = EnabledEntityStore) — required by ITenantFleet"
                    if not (isRegistered typeof<IContainerScheduler>) then
                        "IContainerScheduler (register a backend companion, e.g. DockerLocalContainerScheduler from src/ContainerSchedulers/DockerLocal/, via services.AddSingleton<IContainerScheduler>(...)) — required by IDeployPipeline and ITenantFleet"
                ]

                match missing with
                | [] -> return Ok
                | xs ->
                    // Warning, not Error: the three deploy-plane services are
                    // lazy DI factories, so a deployment may compose
                    // SingleNodeDeployPlane and legitimately use only a subset
                    // (e.g. IBuildOrchestrator alone needs no
                    // IContainerScheduler). A hard abort would false-positive
                    // that case. The per-service first-resolve `failwith`s in
                    // registerDeployPlane remain the usage-accurate hard gate;
                    // this preflight just makes the likely-missing wiring
                    // visible at startup instead of at first request.
                    return
                        Warning(
                            "ServerConfig.DeployPlane = SingleNodeDeployPlane, but these required dependencies appear unregistered:\n  - "
                            + String.concat "\n  - " xs
                            + "\nThe deploy plane will fail at the first request that resolves the affected service (IBuildOrchestrator / IDeployPipeline / ITenantFleet) until they are wired. If this deployment uses only a subset, ignore the entries it does not need."
                        )
        }