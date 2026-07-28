# Migration — Phase 435 cross-version composition upgrade planner (`CompositionUpgradePlan`)

**Status:** net-new, opt-in, purely additive. No existing type, function, or default changed; nothing is registered into DI, no middleware is added, nothing runs at compose. A deployment that never calls the planner composes byte-for-byte what it did before and pays nothing at runtime (GP 11 / GP 13). **No consumer action is required to upgrade.**

## Why

Phase 286 diffs two `CompositionManifest`s — two *instances* of the same vocabulary. The question a consumer actually asks before moving a package pin is the cross-*version* one: **what does moving to vNext mean for this app?** Answering it meant reading migration docs and working out which ones apply to the slots this particular composition fills.

`CompositionUpgradePlan` computes it from two data artefacts: this instance's Phase 280 manifest (plus its Phase 288 provenance) and the Phase 293 `ComposableSurface` of the target version set. `ComposableSurface` is a plain record the newer forge can serialise, so **planning never requires the new binaries** — a file exported by the target version is enough, which is the point: the question is asked before the pin moves, when those binaries are by definition absent.

## Using it

```fsharp
open ToolUp.Platform

// The target side: a snapshot the newer forge exported (deserialise it from
// wherever you stored it — it is a plain record).
let target : ComposableSurfaceSnapshot = readTargetSnapshot ()

// The offline form — manifest in, plan out. Nothing is loaded.
let plan =
    CompositionUpgradePlan.input myManifest target
    |> CompositionUpgradePlan.withProvenance myProvenance          // Phase 288, optional
    |> CompositionUpgradePlan.withDescriptorSchemaVersion 1        // the STORED descriptor's version
    |> CompositionUpgradePlan.withAvailableMigrationDocs docSlugs  // a listing of docs/migrations/
    |> CompositionUpgradePlan.plan

if CompositionUpgradePlan.isBlocked plan then
    for step in CompositionUpgradePlan.blockingSteps plan do
        printfn "%s %A %s" step.StepCode step.StepComponent step.StepDetail

printfn "%s" (CompositionUpgradePlan.render plan)
```

The live-app convenience, when you already hold the composed app (an admin endpoint, an upgrade preflight):

```fsharp
let plan = CompositionUpgradePlan.forApp app target
```

And the export side — what a forge emits so an *older* one can plan against it:

```fsharp
let snapshot = CompositionUpgradePlan.currentSnapshot ()   // surface + schema version + package version
File.WriteAllText(path, JsonSerializer.Serialize(snapshot, FableConverters.create ()))
```

## What the plan says

| Step kind | Severity | Meaning |
|---|---|---|
| `ComposedSlotWidened` | additive | Cardinality widened `SingleImpl` → `MultiImpl`, a substrate requirement dropped, or the interface became a first-class slot in the target. |
| `ComposedSlotChanged` | attention | Cardinality narrowed, or the slot gained a substrate requirement an implementation's `create` must now receive. |
| `ComposedSlotRemoved` | **blocking** | A composed slot the target does not declare at all. |
| `ComposedKnobWidened` | additive | A composition-shaping knob gained admissible values, or became composition-shaping. |
| `ComposedKnobChanged` | attention | The knob lost values other than the one this composition sets. |
| `ComposedKnobRemoved` | attention | The knob is no longer a composition-shaping enum in the target. |
| `ComposedKnobValueUnavailable` | **blocking** | The value this composition sets is no longer admissible. |
| `DescriptorSchemaMigration` | attention | One Phase 292 schema hop the stored descriptor crosses. Emitted **one per hop**, ascending. |
| `DescriptorSchemaRejected` | **blocking** | The stored descriptor is newer than the target understands, or is not a known version — rendered in Phase 292's own words. |
| `ModuleContractChanged` | attention / **blocking** | The four-file convention moved (attention), or a `ComponentId` slot prefix moved (blocking — every composed id is keyed under one). |

Steps are ordered deterministically: descriptor schema hops first in ascending version order (they gate everything after them), then the module contract, then the rest by severity — blocking, attention, additive.

## Three things worth knowing

**Blocking is named per composed component, not per slot.** A multi-impl slot with three composed sinks yields three steps, each carrying its own `ComponentId` and its own Phase 288 provenance ("which nupkg, at what version, provides the thing that breaks"), because that is the question an operator is actually asking.

**Empty plan ⇔ no composed slot is affected.** Both projections are restricted to the companion interfaces and knob names this instance's manifest enumerates, so a slot the app never composes cannot make its plan non-empty. Slots the target *adds* are reported in `PlanNewSlots` — informational, excluded from emptiness. The law is asserted against the committed Phase 287 golden-file baseline, not only a fixture.

**It is honest about what it cannot judge.** A composed companion interface that neither surface declares as a slot — a companion that wraps the composition rather than filling a `ServerApp` field, such as `IFactStore` — lands in `PlanUnjudgedComponents` rather than being counted as verified-unaffected. `PlanUnaffectedComponents` is a positive claim, and only components actually checked appear in it.

## Migration-doc linkage (435.C)

Each step carries `StepMigrationDoc: string option` — a repo-relative `docs/migrations/<slug>.md` path. It is a **path convention, not a hard dependency**: the pointer is populated only when the resolved slug appears in `PlanAvailableMigrationDocs` (hand the planner a listing of `docs/migrations/`; entries may be bare slugs, file names, or paths). Resolution order is the per-component slug from `PlanMigrationDocSlugs`, then the target version stamp. Nothing is invented — an unlisted doc yields `None`.

## Rollback

Delete the call. There is nothing to unwire: the planner registers nothing, decorates nothing, and runs only when invoked.
