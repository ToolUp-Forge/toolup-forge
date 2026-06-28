# Phase 280 — introspectable composition manifest (`CompositionManifest`) (consumer migration)

**What changes.** A new read-only accessor `ServerApp.compositionManifest : ServerApp -> CompositionManifest`
projects the live composition registry into a generic, machine-readable enumeration of *what an
application composed* — every module, companion slot, datatype, and tool by its stable Phase 279
`ComponentId`, plus the `ServerConfig` knobs that shape composition. It widens the startup snapshot
the config-drift detector already takes (companion-assembly hash + resolved `ServerConfig`) into a
full queryable surface, **derived from the live registry** (`ServerApp.ModuleComponentIds` /
`DataTypeRegistrations` / `AITools` and the `with*`-registered companion lists) — never a
separately-declared list, so it cannot drift from what was actually composed.

**Scope.** Purely additive, opt-in, and pure-on-demand (GP 11 + GP 13): a deployment that never calls
`compositionManifest` builds no manifest and is byte-for-byte unchanged. The shape carries no vendor
or domain type (GP 1) — only `ComponentId`, a kind discriminator, a label, and string config values —
so any reader (a `IConfigValidator` preflight, an `IHealthCheck` report, an admin/ops dashboard, a
GitOps drift check) consumes it without depending on a concrete companion.

## The shape

```fsharp
type ComponentKind =
    | ModuleComponent
    | CompanionComponent
    | DataTypeComponent
    | ToolComponent

type ComponentEntry = {
    Id: ComponentId        // the stable Phase 279 identity
    Kind: ComponentKind
    Label: string          // module Name / interface slot name / datatype id / tool name
    Impl: string option    // multi-impl companion sub-id (sink Name / Kind); None otherwise
}

type ConfigKnob = { Name: string; Value: string }

type CompositionManifest = {
    Modules: ComponentEntry list
    CompanionSlots: ComponentEntry list
    DataTypes: ComponentEntry list
    Tools: ComponentEntry list
    ConfigKnobs: ConfigKnob list
}
```

## Reading the manifest

```fsharp
let app =
    ServerApp.empty
    |> ServerApp.addModules [ ordersModule; inventoryModule ]
    |> ServerApp.withAuditSink (SplunkHecAuditSink.create secrets)

let manifest = ServerApp.compositionManifest app
// manifest.Modules        — every module by its resolved ComponentId (module:<id>)
// manifest.CompanionSlots — one entry per single-impl slot; one per impl for
//                           multi-impl lists (IAuditSink / INotificationSink /
//                           IHealthCheck / IConfigValidator / ISmokeTest),
//                           keyed companion:<iface>/<sub-id>, never positional
// manifest.DataTypes      — every registered datatype (datatype:<id>)
// manifest.Tools          — every registered AI tool (tool:<name>)
// manifest.ConfigKnobs    — composition-shaping ServerConfig switches
```

`CompositionManifest.allComponents manifest` flattens the four component lists into one (config
knobs excluded) for a uniqueness sweep or a flat dump.

## Verification

- `InProcess/CompositionManifestTests.fs` in `ToolUp.Platform.Tests`: a composed app enumerates its
  modules + companion slots + datatypes + tools by stable `ComponentId`; the projection is
  deterministic; an empty pipeline yields a manifest with no components (GP 13); the manifest grows
  when the registry grows (no drift-vs-reflection gap).
- The Phase 175 public-API baseline test treats the new types + accessor as additive surface growth,
  which is allowed under the SemVer-on-`0.x` policy — the test stays green with no `.approved.txt`
  edit (the baseline only needs a deliberate edit for a *breaking* removal/retype).

## Rollback

Stop calling `ServerApp.compositionManifest` — nothing else references it, and no behaviour changes
when it is unused. Or revert the Phase 280 forge commit; no persisted state is involved.
