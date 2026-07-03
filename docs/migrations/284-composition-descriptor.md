# Phase 284 — declarative composition descriptor + `ServerApp.ofManifest` (consumer migration)

**What changes.** A new serializable `CompositionDescriptor` describes a whole composition **as
data** — which components (each by its stable Phase 279 `ComponentId`) plus the `ServerConfig` that
shaped it — and `CompositionDescriptor.ofManifest` / `ServerApp.ofManifest` build it back into a
`ServerApp`. It is the serializable *inverse* of the Phase 280 read-only `CompositionManifest`:
`compositionManifest` projects a live app **down** to an enumeration; a descriptor lifts a composition
**up** to config-as-data and rebuilds it. This unlocks reproducible deploys, GitOps composition,
diffable composition, and lightweight test fixtures.

**Scope.** Purely additive and opt-in (GP 11 + GP 13): the fluent builders (`ServerApp.addModule`,
`with*`) stay the primary composition path; a deployment that never authors a descriptor builds none
and is byte-for-byte unchanged. The descriptor carries **no application logic** (GP 1) — it references
components by id; the code side (a module's `ServerModule`, a companion registration) lives in a
`RegistrationCatalogue`, so handler / view / executor bodies never enter the serializable descriptor.

## The shape

```fsharp
type ComponentSelection = {
    Id: ComponentId              // the stable Phase 279 id (the catalogue key)
    Inputs: Map<string, string>  // serializable registration inputs (usually empty)
}

type CompositionDescriptor = {
    Components: ComponentSelection list  // modules + companions, by id
    Config: ServerConfig                 // the composition-shaping switches
}

type DescriptorError =
    | UnknownComponents of ComponentId list  // selected ids the catalogue can't resolve

// The code side: ComponentId -> (Inputs -> ServerApp -> ServerApp)
type ComponentRegistration = Map<string, string> -> ServerApp -> ServerApp
type RegistrationCatalogue = private { ... }
```

## Building a `ServerApp` from a descriptor

```fsharp
// 1. Register the code side — the deployment's known composition vocabulary.
let catalogue =
    RegistrationCatalogue.empty
    |> RegistrationCatalogue.addModule ordersModule       // keyed by the module's resolved id
    |> RegistrationCatalogue.addModule inventoryModule
    |> RegistrationCatalogue.add
        (ComponentId.forCompanionImpl "IAuditSink" "splunk-archive")
        (fun _ app -> ServerApp.withAuditSink splunkSink app)

// 2. Describe the composition as data (loaded from JSON / a GitOps repo / a fixture).
let descriptor =
    CompositionDescriptor.create
        [ CompositionDescriptor.select (ComponentId.ofModule "orders-service")
          CompositionDescriptor.select (ComponentId.ofModule "Inventory")
          CompositionDescriptor.select (ComponentId.forCompanionImpl "IAuditSink" "splunk-archive") ]
        ServerConfig.defaults

// 3a. Total form — handle the error as data.
match CompositionDescriptor.ofManifest catalogue descriptor with
| Ok app  -> app |> ServerApp.run
| Error e -> eprintfn "%s" (CompositionDescriptor.renderError e); exit 1

// 3b. Ergonomic form — raises a readable error on an unresolved id.
ServerApp.ofManifest (catalogue, descriptor) |> ServerApp.run
```

**Datatypes and tools are module-derived**, so a descriptor lists only modules + companions — a
module's datatypes / tools appear in the projected manifest transitively via its registration. The
round-trip law holds: `compositionManifest (ServerApp.ofManifest (cat, d))` reproduces `d`'s selected
module + companion ids (the fuller completeness round-trip lands in Phase 295).

## Verification

- `InProcess/CompositionDescriptorTests.fs` in `ToolUp.Platform.Tests`: a descriptor builds a
  `ServerApp` equivalent to the fluent-built one (identical projected manifests); module-derived
  datatypes / tools appear transitively; the round-trip law holds; the descriptor's `ServerConfig`
  seeds the built app; an unknown component id fails with a readable error that names every
  unresolved id; the raising `ServerApp.ofManifest` surfaces that message; an empty descriptor builds
  a component-free app.
- The Phase 175 public-API baseline treats the new types + accessors as additive surface growth
  (allowed under the SemVer-on-`0.x` policy) — no `.approved.txt` edit needed.

## Rollback

Stop authoring descriptors and calling `CompositionDescriptor.ofManifest` / `ServerApp.ofManifest`;
nothing else references them and no behaviour changes when unused. Or revert the Phase 284 forge
commit — no persisted state is involved.
