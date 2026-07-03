# Phase 293 — composable-surface descriptor (`ComposableSurface`) (consumer migration)

**What changes.** A new read-only accessor `ComposableSurface.describe : unit -> ComposableSurface`
emits forge's **available** composition vocabulary as data — every companion-interface slot the SDK
can compose (`IAuthProvider` / `IBlobStorage` / `IAuditSink` / …) with its cardinality and substrate
requirements, the enum-like composition-shaping config-knob schemas and their admissible values, and
the consumer module contract. It is the **type-level vocabulary** counterpart to the Phase 280
`CompositionManifest`: the manifest describes *what THIS app composed*; the surface describes *what
forge CAN compose*. An external scaffolding / config-editor tool snapshots the surface to stay
forge-version-synced instead of hand-mirroring a drifting copy.

**Derived from the live registry (no hand-listed set).** The slot enumeration reflects over the
`ServerApp` composition record's own fields — every `IX option` (single-impl slot) or `IX list`
(multi-impl slot) where `IX` is an interface. A newly-shipped companion slot (a new interface-typed
field on `ServerApp`) therefore surfaces here with **no change to this descriptor**. Likewise the
config-knob schemas reflect `ServerConfig`'s enum-like (all-nullary-case) union fields.

**Scope.** Purely additive, opt-in, pure-on-demand (GP 11 + GP 13): a deployment that never calls
`describe` builds nothing and is byte-for-byte unchanged. The shape carries no vendor or domain type
(GP 1) — only `ComponentId`, interface names, and strings.

## The shape

```fsharp
type SlotCardinality = SingleImpl | MultiImpl

type ComposableSlot = {
    Slot: ComponentId          // companion:<interface> — the Phase 279 / 280 slot-id space
    Interface: string          // "IAuthProvider", "IAuditSink", …
    Cardinality: SlotCardinality
    SubstrateRequirements: string list   // best-effort; empty when unknown (slot still surfaces)
}

type ConfigKnobSchema = { Name: string; Values: string list }   // enum-like ServerConfig mode knobs
type ModuleContractShape = { Files: string list; ModuleSlotPrefix: string; DataTypeSlotPrefix: string; ToolSlotPrefix: string }

type ComposableSurface = { Slots: ComposableSlot list; ConfigKnobs: ConfigKnobSchema list; ModuleContract: ModuleContractShape }
```

## Reading the surface

```fsharp
let surface = ComposableSurface.describe ()
// surface.Slots       — every companion slot forge can compose, by companion:<iface> id
// surface.ConfigKnobs — the composition-shaping mode knobs + their admissible values
// surface.ModuleContract — the four-file module convention + the Phase 279 slot prefixes
```

`ComposableSurface.slots ()` / `configKnobs ()` expose the two derivations independently.

## Verification

- `InProcess/ComposableSurfaceTests.fs`: the representative companion slots are enumerated; the slot
  set equals the independently-reflected `ServerApp` companion-interface set (derived, not
  hand-listed); cardinality distinguishes options from lists; slot ids line up with
  `ComponentId.forCompanionSlot`; a slot's substrate requirements surface; the config-knob schemas
  are derived from `ServerConfig`; `describe` is deterministic.
- The Phase 175 public-API baseline treats the new types + accessor as additive surface growth
  (allowed under SemVer-on-`0.x`) — no `.approved.txt` edit.

## Rollback

Stop calling `ComposableSurface.describe` — nothing else references it and no behaviour changes when
unused. Or revert the Phase 293 forge commit; no persisted state is involved.
