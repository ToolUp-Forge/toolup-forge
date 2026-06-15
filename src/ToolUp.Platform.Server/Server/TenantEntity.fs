// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.TenantEntity

open System
open ToolUp.Platform.EntityTypes

// ─── Tenant entity (Phase 26 substrate) ─────────────────────────────
//
// Persisted shape for a deployed tenant. Lives behind `IEntityStore`
// — the substrate ships entity registration (this file) +
// `EntityStoreTenantFleet` (next slice) which composes the typed
// fleet API over `IEntityStore<Tenant>` plus an injected
// `IContainerScheduler`.
//
// **Why an EntityStore entity rather than a bespoke store?** The
// EntityStore substrate (Phase 19) already solves versioning,
// per-scope isolation, declared indexes, and `Save` / `Get` /
// `Delete` semantics — re-deriving them for tenants would duplicate
// shipping infrastructure. Tenants are versioned per the
// `IEntityStore` semantics: every `Save` bumps the version; previous
// versions stay queryable for audit / rollback.
//
// **Scope.** Tenant entities live under the reserved `_platform`
// scope (the SDK-wide cross-tenant scope — the same one audit events
// use). This is deliberate: a tenant record describes the tenant
// itself, not data owned by the tenant. Per-tenant data flows
// through that tenant's own `scopeId` (typically `TenantId` matches
// `scopeId` for the tenant the record describes). The reserved
// `_platform` scope is the only authority allowed to enumerate every
// tenant — operator tooling (`ITenantFleet.ListTenants`) is gated
// to Platform-Admin role at the API layer.

// `TenantId` is defined in `ToolUp.Platform.DeployPlaneTypes` (the
// Core-tier substrate types file) so the four Server-side interfaces
// — IBuildOrchestrator / ITenantFleet / IDeployPipeline /
// IContainerScheduler — and a future Fable client UI can share the
// same identity alias. Brought into scope via the `open ToolUp.Platform`
// namespace via System / EntityTypes opens above (TenantId lives at
// namespace level).

/// Tenant lifecycle state. Transitions:
///   Active → Suspended (operator pause; tenant data preserved)
///   Suspended → Active (operator resume)
///   Active → Evicted (full offboarding; data scheduled for erasure)
///   Suspended → Evicted (offboarding from a paused state)
///
/// Evicted is terminal: an evicted tenant can never re-activate —
/// the operator provisions a fresh tenant with a new id if needed.
/// The `Evicted` state is preserved (not deleted) so audit + billing
/// reconciliation can replay the lifecycle.
type TenantStatus =
    /// Tenant is serving traffic. Default state after
    /// `ProvisionTenant`.
    | Active
    /// Operator paused the tenant. Containers may be stopped to save
    /// cost; data preserved. Resumable via `RestartTenant`.
    | Suspended
    /// Tenant is fully offboarded. Data scheduled for erasure per
    /// the operator's retention policy. Terminal — no
    /// re-activation path.
    | Evicted

module TenantStatus =
    /// String form for index segments + audit. Stable across schema
    /// bumps; do not rename without bumping `Tenant.SchemaVersion`.
    let toString =
        function
        | Active -> "Active"
        | Suspended -> "Suspended"
        | Evicted -> "Evicted"

    let tryFromString =
        function
        | "Active" -> Some Active
        | "Suspended" -> Some Suspended
        | "Evicted" -> Some Evicted
        | _ -> None

/// Persisted tenant record. The three `IEntityStore` required
/// fields (`Id`, `Type`, `Version`) come first; the rest are
/// substrate fields the operator's CLI + deploy plane read.
///
/// **`Id` semantics:** identical to `TenantId`. The reserved
/// `_platform` scope holds the tenant catalog; per-tenant entity
/// stores use `Id` as their `scopeId`.
///
/// **Free-form fields.** `Region` and `Tier` are operator-defined
/// strings — the substrate does not enumerate them. A hosted
/// operator may recognise specific regions / tiers; a
/// self-hosted operator picks their own vocabulary.
type Tenant = {
    /// Required by `IEntityStore`. Equals `TenantId`.
    Id: TenantId
    /// Required by `IEntityStore`. Always `"Tenant"`.
    Type: string
    /// Required by `IEntityStore`. Monotonically bumped per Save.
    Version: int
    /// DNS-safe slug. Unique within `(Region, scope=_platform)`;
    /// the `Slug` index supports
    /// `ITenantFleet.IsSlugAvailable` lookups.
    Slug: string
    /// The human who owns this tenant — `userId` per
    /// `ISubjectResolver`. Indexed so a single user's tenants are
    /// O(1) lookup.
    OwnerUserId: string
    /// Operator-defined deployment region.
    Region: string
    /// Operator-defined billing tier (e.g. `"free"`, `"starter"`,
    /// `"pro"`). Free-form; substrate stores it for indexing only.
    Tier: string
    /// Current lifecycle state.
    Status: TenantStatus
    /// When `ProvisionTenant` first persisted the tenant. UTC.
    CreatedAt: DateTime
    /// When the tenant transitioned into its current `Status`.
    /// Equals `CreatedAt` on a freshly-provisioned tenant.
    StatusChangedAt: DateTime
    /// Display name shown in admin UIs. Defaults to `Slug` at
    /// provisioning time; operators / tenant owners may rename.
    DisplayName: string
}

module Tenant =
    /// Entity-type discriminator. Used as the `EntityType` argument
    /// to `IEntityStore` calls.
    [<Literal>]
    let EntityType = "Tenant"

    /// Reserved scope under which the tenant catalog lives. Equal to
    /// the SDK's reserved `_platform` scope — the same one
    /// audit events use. Per-tenant data uses the tenant's own id
    /// as `scopeId`; the catalog itself is `_platform`-scoped.
    [<Literal>]
    let CatalogScope = "_platform"

    /// Index names. Re-declared as `[<Literal>]` so query call
    /// sites can use them in pattern matches without stringly-typed
    /// reference drift.
    [<Literal>]
    let SlugIndex = "Slug"

    [<Literal>]
    let OwnerUserIdIndex = "OwnerUserId"

    [<Literal>]
    let RegionIndex = "Region"

    [<Literal>]
    let TierIndex = "Tier"

    [<Literal>]
    let StatusIndex = "Status"

    /// Compound index on `(Region, Slug)` — backs the canonical
    /// `IsSlugAvailable(slug, region)` query path. Without this,
    /// the substrate would have to scan every tenant in the region
    /// to check uniqueness. Compound indexes match `And`-of-`Eq`
    /// predicates in the declared order (Region first, then Slug)
    /// per the Phase 19a query semantics.
    [<Literal>]
    let RegionSlugCompoundIndex = "Region_Slug"

    /// `EntityRegistration<Tenant>` ready to hand to
    /// `ServerApp.withEntity<Tenant>`. Declares the five
    /// single-field indexes the substrate uses, plus the
    /// `(Region, Slug)` compound index for slug-uniqueness lookup.
    let registration: EntityRegistration<Tenant> =
        EntityRegistration.create<Tenant> EntityType
        |> EntityRegistration.withIndex SlugIndex _.Slug
        |> EntityRegistration.withIndex OwnerUserIdIndex _.OwnerUserId
        |> EntityRegistration.withIndex RegionIndex _.Region
        |> EntityRegistration.withIndex TierIndex _.Tier
        |> EntityRegistration.withIndex StatusIndex (fun t -> TenantStatus.toString t.Status)
        |> EntityRegistration.withCompoundIndex RegionSlugCompoundIndex (fun t -> [ t.Region; t.Slug ])

    /// Build a freshly-provisioned tenant record. `Status` defaults
    /// to `Active`; `Version = 0` (the store bumps to 1 on the
    /// first Save). `now` is supplied so deterministic tests can
    /// pin the timestamp.
    let provision
        (tenantId: TenantId)
        (slug: string)
        (ownerUserId: string)
        (region: string)
        (tier: string)
        (displayName: string)
        (now: DateTime)
        : Tenant =
        {
            Id = tenantId
            Type = EntityType
            Version = 0
            Slug = slug
            OwnerUserId = ownerUserId
            Region = region
            Tier = tier
            Status = Active
            CreatedAt = now
            StatusChangedAt = now
            DisplayName =
                if String.IsNullOrWhiteSpace displayName then
                    slug
                else
                    displayName
        }

    /// Transition a tenant to a new lifecycle state. Returns
    /// `Error` when the transition is illegal (e.g. `Evicted ->
    /// Active`). The store-side Save bumps `Version`; this helper
    /// updates `Status` + `StatusChangedAt` only.
    let transitionStatus (next: TenantStatus) (now: DateTime) (tenant: Tenant) : Result<Tenant, string> =
        match tenant.Status, next with
        | Evicted, _ -> Error "tenant is evicted (terminal); cannot transition"
        | s, t when s = t ->
            // No-op — return as-is to keep the caller's Save idempotent.
            Ok tenant
        | Active, Suspended
        | Active, Evicted
        | Suspended, Active
        | Suspended, Evicted ->
            Ok {
                tenant with
                    Status = next
                    StatusChangedAt = now
            }
        | _ ->
            Error(
                sprintf
                    "illegal tenant transition: %s -> %s"
                    (TenantStatus.toString tenant.Status)
                    (TenantStatus.toString next)
            )