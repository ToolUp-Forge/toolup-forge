# Phase 302 — per-tenant composition presets (consumer migration)

**What changes.** A `TenantCompositionPreset` seam lets each tenant resolve to a **partially-applied
composition** — a base preset `CompositionDescriptor` ([Phase 295](295-descriptor-completeness-presets.md))
whose declared holes are filled *per tenant*, plus per-tenant component config
([Phase 289](289-component-config.md)) — composed via `ServerApp.ofManifest`. Multi-tenant
**composition variants from one archetype**: a shared base preset + a small per-tenant binding, rather
than a forked app per tenant.

**Scope.** Purely additive and opt-in (GP 11 + GP 13): the default is a single global composition
(no preset), and a deployment that never builds a `TenantCompositionPreset` is byte-for-byte
unchanged. Per-tenant resolution rides the SDK's existing tenant/scope resolver (a tenant id is a
`string` — a `StorageScope.ScopeId`). Scope-isolated by construction (GP 4): per-tenant bindings live
in a map keyed by tenant id; a resolve reads only the requested tenant's entry, so one tenant's preset
can never observe another's bindings.

## The shape

```fsharp
type TenantComposition = {
    HoleBindings: Map<string, ComponentSelection list>  // fills the base preset's holes
    Config: ComponentConfig list                        // Phase 289 per-tenant config
}

type TenantCompositionPreset = {
    BasePreset: CompositionDescriptor                   // the shared partial descriptor (declares holes)
    Tenants: Map<string, TenantComposition>             // per-tenant fillings, scope-isolated
}

type TenantResolutionError =
    | TenantNotRegistered of tenantId: string
    | TenantCompositionInvalid of tenantId: string * error: DescriptorError

// TenantComposition.empty / bindHole / withConfig
// TenantCompositionPreset.create / withTenant / tenantIds
// TenantCompositionPreset.resolveDescriptor : tenantId -> preset -> Result<descriptor, TenantResolutionError>
// TenantCompositionPreset.resolve           : catalogue -> tenantId -> preset -> Result<ServerApp, TenantResolutionError>
// TenantCompositionPreset.preflight         : catalogue -> tenantId -> preset -> Result<unit, string>
// TenantCompositionPreset.tenantConfigValidator : tenantId -> preset -> IConfigValidator option
```

## Authoring per-tenant variants

```fsharp
// One base archetype: Core fixed, a "region" hole every tenant fills.
let basePreset =
    CompositionDescriptor.create
        [ CompositionDescriptor.select (ComponentId.ofModule "Core") ]
        ServerConfig.defaults
    |> CompositionDescriptor.withHoles [ "region" ]

let preset =
    TenantCompositionPreset.create basePreset
    |> TenantCompositionPreset.withTenant "acme"
        (TenantComposition.empty
         |> TenantComposition.bindHole "region" [ CompositionDescriptor.select (ComponentId.ofModule "EU") ])
    |> TenantCompositionPreset.withTenant "globex"
        (TenantComposition.empty
         |> TenantComposition.bindHole "region" [ CompositionDescriptor.select (ComponentId.ofModule "US") ])

// Resolve a tenant to its ServerApp (scope-isolated — reads only that tenant's bindings).
match TenantCompositionPreset.resolve catalogue "acme" preset with
| Ok app  -> app |> ServerApp.run           // Core + EU
| Error e -> eprintfn "%s" (TenantCompositionPreset.renderError e); exit 1
```

## Fail at preflight, not at first request

- A tenant whose preset leaves a **required hole unbound** resolves to `Error (TenantCompositionInvalid
  (tenantId, UnfilledHoles […]))`. Run `TenantCompositionPreset.preflight catalogue tenantId preset`
  at startup so this fails loudly, naming the tenant + the unbound hole — never deep in a request path.
- `TenantCompositionPreset.tenantConfigValidator` yields the Phase 289 `IConfigValidator` over a
  tenant's declared `ComponentConfig` sections; wire it into the Phase 9m preflight aggregator so a
  stray id-scoped override (`TOOLUP_COMPONENT__<id>__<key>`) for that tenant fails startup.

## Verification

- `InProcess/TenantCompositionPresetTests.fs` in `ToolUp.Platform.Tests`: two tenants resolve to
  distinct variants from one base preset; a tenant's resolution never observes another's bindings
  (scope isolation); an unbound required hole fails preflight readably naming tenant + hole (and
  surfaces as `TenantCompositionInvalid (UnfilledHoles …)`); an unregistered tenant fails with
  `TenantNotRegistered`; a fully-bound tenant composes exactly the equivalent direct app;
  `tenantConfigValidator` yields the Phase 289 validator for a known tenant, `None` otherwise.
- The Phase 175 public-API baseline treats the new types + module as additive surface growth.

## Rollback

Don't build a `TenantCompositionPreset` — the default single global composition is untouched. Or
revert the Phase 302 forge commit — additive; no persisted state.
