# Phase 295 — descriptor completeness round-trip + partial/preset binding (consumer migration)

**What changes.** Two additions to [Phase 284](284-composition-descriptor.md)'s `CompositionDescriptor`:

1. **Completeness round-trip.** `CompositionDescriptor.toDescriptor : ServerApp -> CompositionDescriptor`
   lowers an *arbitrary* composed app to a descriptor, and rebuilding it (`ServerApp.ofManifest`
   against a catalogue that registers the same components) reproduces the app's **full** component-id
   set — lossless over modules + companions + the module-derived datatypes / tools. The descriptor is
   a complete lowering target for an external authoring tool.
2. **Partial / preset descriptors.** A descriptor may declare **holes** (`withHoles`) — a reusable
   archetype whose unbound slots are filled later (`apply`). `ofManifest` rejects a descriptor that
   still carries an unbound hole, naming it; a filled preset composes the equivalent full composition.

**Scope.** Purely additive and opt-in (GP 11 + GP 13): a descriptor with no holes behaves exactly as
in Phase 284; presets and `toDescriptor` cost nothing unless used. Presets are data (GP 1) — no app
logic.

## The shape

```fsharp
type DescriptorHole = {
    Name: string
    Filling: ComponentSelection list option   // None = unbound
}

type CompositionDescriptor = {
    Version: int
    Components: ComponentSelection list
    Holes: DescriptorHole list                 // NEW — declared holes (empty for a full descriptor)
    Config: ServerConfig
}

type DescriptorError =
    | UnknownComponents of ComponentId list
    | UnfilledHoles of string list             // NEW — unbound holes at compose time

// Authoring
CompositionDescriptor.withHoles : string list -> descriptor -> descriptor
CompositionDescriptor.apply     : string -> ComponentSelection list -> descriptor -> descriptor
CompositionDescriptor.unfilledHoles     : descriptor -> string list
CompositionDescriptor.effectiveComponents / effectiveComponentIds : descriptor -> …
// Completeness
CompositionDescriptor.toDescriptor : ServerApp -> CompositionDescriptor
```

## Preset (archetype) authoring

```fsharp
// A reusable archetype: the base module fixed, an "audit" slot left open.
let preset =
    CompositionDescriptor.create
        [ CompositionDescriptor.select (ComponentId.ofModule "billing-service") ]
        ServerConfig.defaults
    |> CompositionDescriptor.withHoles [ "audit" ]

// Fill the hole per use-site, then compose.
let app =
    preset
    |> CompositionDescriptor.apply "audit"
        [ CompositionDescriptor.select (ComponentId.forCompanionImpl "IAuditSink" "s3-archive") ]
    |> fun d -> ServerApp.ofManifest (catalogue, d)

// An unfilled hole composes to Error (UnfilledHoles ["audit"]) — never a silent partial app.
// A typo'd apply ("audi") leaves the real hole unbound, so the mistake surfaces as that same error.
```

## Round-trip completeness law

`ServerApp.ofManifest (cat, CompositionDescriptor.toDescriptor app)` reproduces `app`'s full
component-id set (modules + companions + module-derived datatypes / tools). `toDescriptor` lowers only
modules + companions as direct selections; datatypes / tools reappear transitively through the module
registrations.

> **Phase 286 note.** The phase specifies proving the round-trip via Phase 286's structural manifest
> diff. Phase 286 (Track W35-T4) is not yet shipped, so `DescriptorCompletenessTests` proves the law
> by an inline structural set-comparison of the projected component ids (the same added / removed
> delta a diff would report). When Phase 286 lands, swap the inline comparison for its diff.

## Verification

- `InProcess/DescriptorCompletenessTests.fs` in `ToolUp.Platform.Tests`: an arbitrary composed app
  round-trips losslessly; `toDescriptor` lowers modules + companions and the datatypes / tools
  reappear transitively; a preset + a hole-binding composes the equivalent full composition; an
  unfilled hole fails readably naming it; a typo'd `apply` leaves the real hole unbound; a holeless
  descriptor is unaffected.
- The Phase 175 public-API baseline treats the new field + types + helpers as additive surface growth.

## Rollback

Author descriptors without holes and stop calling `toDescriptor`; the `Holes` field defaults to `[]`,
so an existing full descriptor is unaffected. Or revert the Phase 295 forge commit — additive; no
persisted state.
