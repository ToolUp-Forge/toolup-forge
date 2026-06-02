// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Threading
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.TenantEntity

// ─── EntityStoreTenantFleet (Phase 26 substrate default) ─────────────
//
// Single-node default implementation of `ITenantFleet`. Composes
// `IEntityStore<Tenant>` for persistence with an injected
// `IContainerScheduler` for runtime state. Per-tenant ordering is
// enforced by a per-`TenantId` `SemaphoreSlim` map — distributed
// companions (Akka.Cluster-Sharded fleet, Orleans grain-per-tenant)
// replace the singleton entirely and enforce ordering via shard
// placement.
//
// **Scope.** Tenant catalog lives under `Tenant.CatalogScope`
// (`_platform`) — the SDK-wide reserved scope. Per-tenant runtime
// data flows through that tenant's own `scopeId` (which equals its
// `TenantId`); the catalog enumeration / write surface is
// `_platform`-scoped only.
//
// **Limitations.** The single-node default does not pre-flight DNS
// resolution for `Domains`, does not interact with `IDeployPipeline`
// here (`DeployToTenant` delegates to the injected pipeline), and
// uses a naive `seed-1 / seed-2 / ...` slug suggestion. Operators
// wanting vocabulary-aware suggestions register their own
// `ITenantFleet` implementation.

/// Single-node default `ITenantFleet` over `IEntityStore` +
/// `IContainerScheduler` + `IDeployPipeline`. Constructed at compose
/// time when `ServerConfig.DeployPlane = SingleNodeDeployPlane`.
type EntityStoreTenantFleet
    (
        entityStore: IEntityStore,
        containerScheduler: IContainerScheduler,
        deployPipeline: IDeployPipeline,
        logger: ILogger
    ) =

    // Per-tenant ordering. The lock is acquired on the mutating
    // surface (Provision, DeployTo, Evict, Restart); read-only paths
    // (Get, List, IsSlugAvailable) bypass it. Per-(region,slug) lock
    // for the slug-uniqueness window inside ProvisionTenant.
    let tenantLocks = ConcurrentDictionary<TenantId, SemaphoreSlim>()
    let slugLocks = ConcurrentDictionary<string * string, SemaphoreSlim>()

    let acquireTenantLock (tenantId: TenantId) =
        tenantLocks.GetOrAdd(tenantId, fun _ -> new SemaphoreSlim(1, 1))

    let acquireSlugLock (slug: string) (region: string) =
        slugLocks.GetOrAdd((slug, region), fun _ -> new SemaphoreSlim(1, 1))

    /// Single-region slug-uniqueness lookup via the (Region, Slug)
    /// compound index. Returns true when no active tenant holds the
    /// slug in the region.
    let isSlugFree (slug: string) (region: string) : Async<bool> = async {
        let! found =
            entityStore.FindByIndex<Tenant>(
                Tenant.CatalogScope,
                Tenant.EntityType,
                Tenant.RegionSlugCompoundIndex,
                sprintf "%s|%s" region slug
            )

        match found with
        | Ok refs ->
            // Filter to non-evicted refs only — evicted tenants retain
            // their slug in the catalog but the fleet does not treat
            // them as holding the slug for new-tenant gating. We need
            // to materialise the entity to inspect Status, since the
            // ref doesn't carry it.
            let! tenants =
                refs
                |> List.map (fun (r: EntityRef<Tenant>) ->
                    entityStore.Get<Tenant>(Tenant.CatalogScope, Tenant.EntityType, r.Id))
                |> Async.Sequential

            let live =
                tenants
                |> Array.choose (function
                    | Ok t when t.Status <> Evicted -> Some t
                    | _ -> None)

            return live.Length = 0
        | Error _ -> return false
    }

    interface ITenantFleet with

        member _.ProvisionTenant(request: ProvisioningRequest) : Async<Result<Tenant, TenantFleetError>> = async {
            if not (DeployManifest.isValidSlug request.Slug) then
                return
                    Error(
                        TenantSlugInvalid(
                            request.Slug,
                            "must be DNS-safe: lowercase ASCII letters/digits/hyphens, 1-63 chars, no leading/trailing hyphen"
                        )
                    )
            elif String.IsNullOrWhiteSpace request.OwnerUserId then
                return Error(FleetStorageFailure "ProvisioningRequest.OwnerUserId is required")
            else
                let slugLock = acquireSlugLock request.Slug request.Region
                do! slugLock.WaitAsync() |> Async.AwaitTask

                try
                    let! free = isSlugFree request.Slug request.Region

                    if not free then
                        return Error(SlugAlreadyTaken(request.Slug, request.Region))
                    else
                        let tenantId = Guid.NewGuid().ToString("N")

                        let tenant =
                            Tenant.provision
                                tenantId
                                request.Slug
                                request.OwnerUserId
                                request.Region
                                request.Tier
                                request.DisplayName
                                DateTime.UtcNow

                        let! saved = entityStore.Save<Tenant>(Tenant.CatalogScope, tenant)

                        match saved with
                        | Ok _ -> return Ok { tenant with Version = 1 }
                        | Error e -> return Error(FleetStorageFailure(EntityError.message e))
                finally
                    slugLock.Release() |> ignore
        }

        member _.GetTenant(tenantId: TenantId) : Async<Result<Tenant, TenantFleetError>> = async {
            let! result = entityStore.Get<Tenant>(Tenant.CatalogScope, Tenant.EntityType, tenantId)

            return
                match result with
                | Ok t -> Ok t
                | Error(EntityError.NotFound _) -> Error(UnknownTenant tenantId)
                | Error e -> Error(FleetStorageFailure(EntityError.message e))
        }

        member _.ListTenants(ownerUserId: string option) : Async<Tenant list> = async {
            match ownerUserId with
            | Some uid ->
                let! refs =
                    entityStore.FindByIndex<Tenant>(
                        Tenant.CatalogScope,
                        Tenant.EntityType,
                        Tenant.OwnerUserIdIndex,
                        uid
                    )

                match refs with
                | Ok rs ->
                    let! fetched =
                        rs
                        |> List.map (fun (r: EntityRef<Tenant>) ->
                            entityStore.Get<Tenant>(Tenant.CatalogScope, Tenant.EntityType, r.Id))
                        |> Async.Sequential

                    return
                        fetched
                        |> Array.choose (function
                            | Ok t -> Some t
                            | _ -> None)
                        |> Array.sortBy _.CreatedAt
                        |> Array.toList
                | Error _ -> return []
            | None ->
                let! refs = entityStore.ListAll<Tenant>(Tenant.CatalogScope, Tenant.EntityType, 0, 10_000)

                let! fetched =
                    refs
                    |> List.map (fun (r: EntityRef<Tenant>) ->
                        entityStore.Get<Tenant>(Tenant.CatalogScope, Tenant.EntityType, r.Id))
                    |> Async.Sequential

                return
                    fetched
                    |> Array.choose (function
                        | Ok t -> Some t
                        | _ -> None)
                    |> Array.sortBy _.CreatedAt
                    |> Array.toList
        }

        member self.DeployToTenant(request: FleetDeployRequest) : Async<Result<DeployId, TenantFleetError>> = async {
            // Refuse deploy on evicted tenants. Per-tenant lock prevents
            // racing with EvictTenant.
            let lock = acquireTenantLock request.TenantId
            do! lock.WaitAsync() |> Async.AwaitTask

            try
                let! tenant = (self :> ITenantFleet).GetTenant request.TenantId

                match tenant with
                | Error e -> return Error e
                | Ok t when t.Status = Evicted -> return Error(TenantEvicted request.TenantId)
                | Ok _ ->
                    let! deploy =
                        deployPipeline.BeginDeploy(
                            request.TenantId,
                            request.BuildId,
                            request.Manifest,
                            request.SubmittedBy
                        )

                    return
                        match deploy with
                        | Ok did -> Ok did
                        | Error err ->
                            logger.Warn(
                                sprintf
                                    "DeployToTenant: pipeline BeginDeploy failed for tenant %s: %A"
                                    request.TenantId
                                    err
                            )

                            Error(FleetStorageFailure(sprintf "deploy pipeline rejected: %A" err))
            finally
                lock.Release() |> ignore
        }

        member self.GetTenantHealth(tenantId: TenantId) : Async<Result<TenantHealth, TenantFleetError>> = async {
            let! tenant = (self :> ITenantFleet).GetTenant tenantId

            match tenant with
            | Error e -> return Error e
            | Ok t when t.Status = Suspended -> return Ok TenantHealthUnknown
            | Ok t when t.Status = Evicted -> return Ok TenantHealthUnknown
            | Ok _ ->
                let! containers = containerScheduler.ListContainers(Some tenantId)

                if containers.IsEmpty then
                    return Ok TenantHealthUnknown
                else
                    let aggregate =
                        containers
                        |> List.fold
                            (fun acc info ->
                                match acc, info.Status with
                                | TenantUnhealthy _, _ -> acc
                                | _, ContainerCrashed(reason, _) -> TenantUnhealthy reason
                                | _, ContainerExited(code, _) when code <> 0 ->
                                    TenantUnhealthy(sprintf "container exited with code %d" code)
                                | TenantDegraded _, _ -> acc
                                | _, ContainerRestarting -> TenantDegraded "one or more containers restarting"
                                | _, ContainerCreated -> TenantDegraded "one or more containers not yet running"
                                | _ -> acc)
                            TenantHealthy

                    return Ok aggregate
        }

        member self.EvictTenant
            (tenantId: TenantId, byUserId: string, reason: string)
            : Async<Result<unit, TenantFleetError>> =
            async {
                let lock = acquireTenantLock tenantId
                do! lock.WaitAsync() |> Async.AwaitTask

                try
                    let! tenant = (self :> ITenantFleet).GetTenant tenantId

                    match tenant with
                    | Error e -> return Error e
                    | Ok t when t.Status = Evicted ->
                        // Idempotent: already evicted is Ok.
                        return Ok()
                    | Ok t ->
                        match Tenant.transitionStatus Evicted DateTime.UtcNow t with
                        | Error msg -> return Error(FleetStorageFailure msg)
                        | Ok next ->
                            let! saved = entityStore.Save<Tenant>(Tenant.CatalogScope, next)

                            match saved with
                            | Error e -> return Error(FleetStorageFailure(EntityError.message e))
                            | Ok _ ->
                                // Stop every container the tenant is
                                // running. Failures are logged but do
                                // not abort the eviction — the catalog
                                // record is already evicted.
                                let! containers = containerScheduler.ListContainers(Some tenantId)

                                for c in containers do
                                    let! stopped = containerScheduler.StopContainer c.ContainerId

                                    match stopped with
                                    | Ok _ -> ()
                                    | Error err ->
                                        logger.Warn(
                                            sprintf
                                                "EvictTenant %s (by %s, reason: %s): failed to stop container %s: %A"
                                                tenantId
                                                byUserId
                                                reason
                                                c.ContainerId
                                                err
                                        )

                                return Ok()
                finally
                    lock.Release() |> ignore
            }

        member self.RestartTenant(tenantId: TenantId, byUserId: string) : Async<Result<unit, TenantFleetError>> = async {
            let lock = acquireTenantLock tenantId
            do! lock.WaitAsync() |> Async.AwaitTask

            try
                let! tenant = (self :> ITenantFleet).GetTenant tenantId

                match tenant with
                | Error e -> return Error e
                | Ok t when t.Status = Evicted -> return Error(TenantEvicted tenantId)
                | Ok t when t.Status = Active ->
                    // Idempotent: already active is Ok.
                    return Ok()
                | Ok t ->
                    match Tenant.transitionStatus Active DateTime.UtcNow t with
                    | Error msg -> return Error(FleetStorageFailure msg)
                    | Ok next ->
                        let! saved = entityStore.Save<Tenant>(Tenant.CatalogScope, next)

                        match saved with
                        | Ok _ ->
                            logger.Info(sprintf "Tenant %s restarted by %s" tenantId byUserId)
                            return Ok()
                        | Error e -> return Error(FleetStorageFailure(EntityError.message e))
            finally
                lock.Release() |> ignore
        }

        member _.IsSlugAvailable(slug: string, region: string) : Async<bool> =
            if not (DeployManifest.isValidSlug slug) then
                async.Return false
            else
                isSlugFree slug region

        member _.SuggestSlugs(seed: string) : Async<string list> = async {
            // Naive numeric-suffix suggester. Operators wanting vocabulary-
            // aware or brand-safe suggestions register their own ITenantFleet.
            let safe =
                seed
                |> Seq.choose (fun c ->
                    if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' then
                        Some c
                    elif (c >= 'A' && c <= 'Z') then
                        Some(System.Char.ToLowerInvariant c)
                    else
                        None)
                |> System.String.Concat
                |> fun s -> s.Trim('-')

            let baseSlug = if String.IsNullOrEmpty safe then "app" else safe

            let suggestions = ResizeArray<string>()

            let mutable n = 1

            while suggestions.Count < 5 && n < 50 do
                let candidate = sprintf "%s-%d" baseSlug n
                // Use a representative region for availability — substrate
                // suggestion is region-agnostic; operators with regional
                // checks should override.
                let! free = isSlugFree candidate ""

                if free then
                    suggestions.Add candidate

                n <- n + 1

            return List.ofSeq suggestions
        }