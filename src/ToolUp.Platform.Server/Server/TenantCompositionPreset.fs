// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Per-tenant composition presets (TenantCompositionPreset) ─────────
//
// Multi-tenant **composition variants from one archetype**: a base preset
// `CompositionDescriptor` ([Phase 295]) whose declared holes are filled
// *per tenant*, with per-tenant component config ([Phase 289]) — rather
// than a forked app per tenant. Each tenant resolves to a
// partially-applied composition: the shared base preset + a small
// per-tenant binding, composed via `ServerApp.ofManifest`.
//
// **Scope-isolated by construction (GP 4).** Per-tenant bindings live in a
// map keyed by tenant id; a tenant resolves *only its own* entry — one
// tenant's preset can never read another's bindings. There is no shared
// mutable state and no cross-tenant fallback.
//
// **Opt-in; single-composition deployments pay nothing (GP 11 / GP 13).**
// The default is one global composition (no preset). A deployment that
// never builds a `TenantCompositionPreset` is byte-for-byte unchanged; the
// per-tenant resolution rides the SDK's existing tenant/scope resolver (a
// tenant id is a `string` — a `StorageScope.ScopeId`).
//
// **Fail at preflight, not at first request.** A tenant whose preset
// leaves a required hole unbound (or names an unresolvable id) fails when
// its composition is resolved — surfaced as `CompositionInvalid`, and via
// the Phase 289 `IConfigValidator` for its declared component config — not
// deep in a request path.

/// One tenant's contribution over a base preset: the fillings for the
/// preset's declared holes, plus the tenant's per-component config
/// sections (Phase 289). Both default empty — a tenant that binds nothing
/// inherits the base preset unchanged (and fails to compose if the base
/// still has unbound holes).
type TenantComposition = {
    /// Hole name → the selections that fill it for this tenant. Applied to
    /// the base preset's holes at resolution time.
    HoleBindings: Map<string, ComponentSelection list>
    /// Per-component config sections (Phase 289) scoped to this tenant —
    /// the `known` set the tenant's `IConfigValidator` preflight validates
    /// id-scoped overrides against.
    Config: ComponentConfig list
}

/// A base partial-descriptor preset shared across tenants, plus each
/// tenant's per-tenant filling. Resolving a tenant yields a
/// fully-applied `CompositionDescriptor` (base + that tenant's bindings)
/// that composes via `ServerApp.ofManifest`.
type TenantCompositionPreset = {
    /// The shared base preset — a partial `CompositionDescriptor` that
    /// declares the holes every tenant fills.
    BasePreset: CompositionDescriptor
    /// Per-tenant compositions, keyed by tenant id. Scope-isolated: a
    /// resolve reads only the requested tenant's entry (GP 4).
    Tenants: Map<string, TenantComposition>
}

/// Why a tenant could not be resolved to a composed `ServerApp`. (Cases
/// are named `Tenant*`-distinct to avoid colliding with `TenantFleetError`
/// in `Core`, whose `UnknownTenant` is an unrelated fleet-lookup error.)
type TenantResolutionError =
    /// No composition is registered for this tenant id.
    | TenantNotRegistered of tenantId: string
    /// The tenant's resolved (base + bindings) descriptor did not compose —
    /// an unbound required hole or an unresolvable component id. Carries the
    /// underlying `DescriptorError`.
    | TenantCompositionInvalid of tenantId: string * error: DescriptorError

/// Author a single tenant's contribution.
module TenantComposition =

    /// An empty tenant composition — no hole bindings, no config. Build it
    /// up with `bindHole` / `withConfig`.
    let empty: TenantComposition = {
        HoleBindings = Map.empty
        Config = []
    }

    /// Bind the base preset's hole `name` to `selections` for this tenant.
    let bindHole (name: string) (selections: ComponentSelection list) (tc: TenantComposition) : TenantComposition = {
        tc with
            HoleBindings = Map.add name selections tc.HoleBindings
    }

    /// Attach the tenant's per-component config sections (Phase 289).
    let withConfig (sections: ComponentConfig list) (tc: TenantComposition) : TenantComposition = {
        tc with
            Config = sections
    }

/// Build + resolve a `TenantCompositionPreset`.
module TenantCompositionPreset =

    /// A preset over `basePreset` with no tenants yet. Register tenants
    /// with `withTenant`.
    let create (basePreset: CompositionDescriptor) : TenantCompositionPreset = {
        BasePreset = basePreset
        Tenants = Map.empty
    }

    /// Register (or replace) a tenant's composition.
    let withTenant
        (tenantId: string)
        (composition: TenantComposition)
        (preset: TenantCompositionPreset)
        : TenantCompositionPreset =
        {
            preset with
                Tenants = Map.add tenantId composition preset.Tenants
        }

    /// The tenant ids the preset knows.
    let tenantIds (preset: TenantCompositionPreset) : string list =
        preset.Tenants |> Map.toList |> List.map fst

    /// Resolve a tenant to its fully-applied `CompositionDescriptor`: the
    /// base preset with *only that tenant's* hole bindings applied.
    /// Scope-isolated (GP 4) — reads only `preset.Tenants.[tenantId]`, so a
    /// tenant's resolution can never observe another tenant's bindings.
    /// `Error (UnknownTenant …)` when no composition is registered. The
    /// resolved descriptor may still carry unbound holes (a binding the
    /// tenant did not supply) — that surfaces at compose time as an
    /// `UnfilledHoles` error, not here.
    let resolveDescriptor
        (tenantId: string)
        (preset: TenantCompositionPreset)
        : Result<CompositionDescriptor, TenantResolutionError> =
        match Map.tryFind tenantId preset.Tenants with
        | None -> Error(TenantNotRegistered tenantId)
        | Some composition ->
            composition.HoleBindings
            |> Map.fold
                (fun descriptor name selections -> CompositionDescriptor.apply name selections descriptor)
                preset.BasePreset
            |> Ok

    /// Resolve a tenant and build its `ServerApp` against `catalogue` (via
    /// the total Phase 295 `CompositionDescriptor.ofManifest`). An unbound
    /// required hole or an unresolvable id fails with `CompositionInvalid`
    /// — at resolution time, never deep in a request path.
    let resolve
        (catalogue: RegistrationCatalogue)
        (tenantId: string)
        (preset: TenantCompositionPreset)
        : Result<ServerApp, TenantResolutionError> =
        resolveDescriptor tenantId preset
        |> Result.bind (fun descriptor ->
            CompositionDescriptor.ofManifest catalogue descriptor
            |> Result.mapError (fun e -> TenantCompositionInvalid(tenantId, e)))

    /// Render a `TenantResolutionError` to a readable, actionable message.
    let renderError (error: TenantResolutionError) : string =
        match error with
        | TenantNotRegistered tenantId ->
            sprintf
                "No composition is registered for tenant '%s'. Register it with TenantCompositionPreset.withTenant before resolving."
                tenantId
        | TenantCompositionInvalid(tenantId, e) ->
            sprintf "Tenant '%s' composition is invalid: %s" tenantId (CompositionDescriptor.renderError e)

    /// Preflight a tenant's composition: confirm it resolves + composes,
    /// so an unbound required hole (or an unresolvable id) fails **at
    /// startup**, not at the tenant's first request. Returns the readable
    /// error text on failure.
    let preflight
        (catalogue: RegistrationCatalogue)
        (tenantId: string)
        (preset: TenantCompositionPreset)
        : Result<unit, string> =
        match resolve catalogue tenantId preset with
        | Ok _ -> Ok()
        | Error e -> Error(renderError e)

    /// The Phase 289 `IConfigValidator` over a tenant's declared component
    /// config sections — wire it into the SDK's Phase 9m preflight
    /// aggregator so a stray id-scoped override for this tenant
    /// (`TOOLUP_COMPONENT__<id>__<key>` targeting no known component/key)
    /// fails startup, naming it. `None` when the tenant is unknown.
    let tenantConfigValidator
        (tenantId: string)
        (preset: TenantCompositionPreset)
        : ConfigValidation.IConfigValidator option =
        preset.Tenants
        |> Map.tryFind tenantId
        |> Option.map (fun composition -> ComponentConfigResolver.overrideValidator composition.Config)