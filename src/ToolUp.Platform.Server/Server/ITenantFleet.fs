// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.TenantEntity

// ─── ITenantFleet (Phase 26 substrate) ───────────────────────────────
//
// Server-only fleet API for the deploy plane. Composes per-tenant
// state (the `Tenant` entity via `IEntityStore`) with the operator's
// `IContainerScheduler` to expose a typed lifecycle: provision →
// deploy → health → restart → evict. The substrate ships a single-
// node default (`EntityStoreTenantFleet`); distributed companions
// (multi-region cluster, Akka.Cluster-Sharded fleet) live downstream
// and replace this singleton.
//
// **Per-tenant single-actor invariant.** Operations that mutate a
// tenant's state are serialised per `TenantId` — `ProvisionTenant`,
// `DeployToTenant`, `EvictTenant`, `RestartTenant`. In the single-
// node default this is enforced by a per-tenant `SemaphoreSlim`;
// distributed companions enforce via per-`TenantId` shard placement.
// Cross-tenant operations have no ordering promise (rule 5).
//
// **Six-rule portability audit (Phase 9c, Guiding Principle 12):**
//
//   1. Identity by value      — every method takes/returns `TenantId`
//                                (string), `BuildId` (string),
//                                `DeployId` (string). `Tenant` is a
//                                record with no live handles.
//                                `ProvisioningRequest` and
//                                `FleetDeployRequest` are records.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry/supervision as data — failure flows through
//                                  `TenantFleetError`; no
//                                  `OnFailure`-shaped callbacks.
//   4. Stateless between calls — implementations may cache tenant
//                                lookups, but contract assumes
//                                nothing survives between calls.
//   5. No cross-shard ordering — per-`TenantId` ordering preserved
//                                via single-actor invariant.
//                                Cross-tenant operations make no
//                                ordering promise.
//   6. Precision at lower bound — timestamps inherit
//                                  `IEntityStore`'s precision (no
//                                  time semantics beyond `Tenant.CreatedAt`
//                                  and `Tenant.StatusChangedAt`).

/// Typed fleet API for tenant lifecycle. Composes
/// `IEntityStore<Tenant>` with `IContainerScheduler` under a
/// per-tenant ordering invariant. Operator-side CLIs +
/// administration surfaces call this; per-tenant runtime code calls
/// its tenant's `scopeId`-isolated stores directly (it doesn't go
/// through the fleet).
type ITenantFleet =
    /// Provision a fresh tenant. Persists a `Tenant` entity (via
    /// `IEntityStore`) with `Status = Active`; no containers
    /// launch yet — that's `DeployToTenant`'s job.
    ///
    /// **Validation chain** (in order):
    ///   1. `request.Slug` passes `DeployManifest.isValidSlug`.
    ///   2. `(request.Slug, request.Region)` is not already taken.
    ///   3. `request.OwnerUserId` non-empty.
    /// First failure short-circuits with the appropriate
    /// `TenantFleetError`.
    ///
    /// Concurrent calls with the same `(Slug, Region)` resolve via
    /// per-(region,slug) lock; the loser receives `SlugAlreadyTaken`.
    abstract ProvisionTenant: request: ProvisioningRequest -> Async<Result<Tenant, TenantFleetError>>

    /// Fetch a tenant by id. Returns `UnknownTenant` when the id
    /// doesn't exist (including evicted tenants whose record is
    /// retained — `Evicted` is queryable, just not mutable).
    abstract GetTenant: tenantId: TenantId -> Async<Result<Tenant, TenantFleetError>>

    /// Enumerate tenants. `ownerUserId = Some uid` filters to one
    /// user's tenants (via the `OwnerUserId` index — O(1)); `None`
    /// returns every tenant (scan; intended for Platform-Admin
    /// only — gated at the API layer, not here).
    /// Ordering: `CreatedAt` ascending.
    abstract ListTenants: ownerUserId: string option -> Async<Tenant list>

    /// Deploy a built artefact to a provisioned tenant. Composes
    /// `IBuildOrchestrator` output with `IDeployPipeline.BeginDeploy`;
    /// the returned `DeployId` lets the caller poll
    /// `IDeployPipeline.GetDeployState` for progress.
    ///
    /// Returns `TenantEvicted` when the tenant is in `Evicted`
    /// state — evicted tenants accept no further deploys.
    abstract DeployToTenant: request: FleetDeployRequest -> Async<Result<DeployId, TenantFleetError>>

    /// Aggregate health across the tenant's containers. The
    /// substrate reads per-container `ContainerStatus` from the
    /// injected `IContainerScheduler` and aggregates (every container
    /// `Healthy` → `TenantHealthy`; any `Unhealthy` →
    /// `TenantUnhealthy`; mixed → `TenantDegraded`; no containers
    /// → `TenantHealthUnknown`). Suspended tenants always read
    /// `TenantHealthUnknown` (no containers running by design).
    abstract GetTenantHealth: tenantId: TenantId -> Async<Result<TenantHealth, TenantFleetError>>

    /// Offboard a tenant. Transitions `Status` to `Evicted` (terminal
    /// — no re-activation path), stops every container the tenant
    /// is running, and schedules data erasure per the operator's
    /// retention policy. The tenant entity is preserved (not deleted)
    /// so audit + billing reconciliation can replay the lifecycle.
    ///
    /// Returns `TenantEvicted` when the tenant is already evicted
    /// (idempotent on the success path — a second call after a
    /// first succeeded returns `Ok ()`).
    abstract EvictTenant:
        tenantId: TenantId * byUserId: string * reason: string -> Async<Result<unit, TenantFleetError>>

    /// Resume a `Suspended` tenant — transition `Status` back to
    /// `Active` and relaunch its containers. Returns
    /// `TenantEvicted` when the tenant is evicted (terminal).
    /// Idempotent: calling on an already-active tenant is `Ok`.
    abstract RestartTenant: tenantId: TenantId * byUserId: string -> Async<Result<unit, TenantFleetError>>

    /// Cheap pre-flight check for a tenant-creation UI. Returns
    /// `true` when no `Active` or `Suspended` tenant with this slug
    /// exists in `region`; `false` otherwise. (Evicted tenants
    /// retain their slug — the fleet does not re-issue an evicted
    /// tenant's slug, to prevent confusion between a fresh tenant
    /// and an old eviction.)
    /// Does not lock the slug — a subsequent `ProvisionTenant` may
    /// still race-lose. Use only as UI affordance; final
    /// authoritative check is `ProvisionTenant`'s own validation.
    abstract IsSlugAvailable: slug: string * region: string -> Async<bool>

    /// Suggest available slugs derived from `seed`. The single-node
    /// default appends `-1`, `-2`, ... until the first available
    /// slug; consumers wanting vocabulary-aware or brand-safe
    /// suggestions provide their own implementation via DI.
    /// Returns at most 5 suggestions.
    abstract SuggestSlugs: seed: string -> Async<string list>