# Migration — Phase 433 component data-footprint manifest (`DataFootprint`)

**Status:** net-new, opt-in, purely additive. No existing type, function, or default changed. A deployment that does not call anything below composes byte-for-byte what it did before and pays nothing at runtime (GP 11 / GP 13). **No consumer action is required to upgrade.**

## Why

`CompositionManifest` (Phase 280) says *what* was composed; `CompanionCapability` + the Phase 296 join say *whether it touches the world*. Neither says **what it stores, or where** — and that is the question a data-subject request (DSR) / offboarding pipeline has to answer before it can claim to be complete. Until now the answer lived in source: you read every module, every companion, and hoped nobody had added a store since.

`DataFootprint` makes it a static property. Per `ComponentId`: the data classes a component reads, writes, and leaves **at rest**, each carrying the store seam it sits behind (`IDataObjectStore` / `IBlobStorage` / `IEventStore` / audit sink / `ISecretStore`) and a declared `ContainsPii` marker. Joined across the composition the way Phase 296 joins effects.

## What it derives from (zero per-module effort)

| Seam | Direction | Read from | Attributed to |
|---|---|---|---|
| registered `DataType` | writes + persists an `EntityClass` behind `IDataObjectStore` | `ServerModule.DataTypes` / `ServerApp.DataTypeRegistrations` | the producing module's explicit `ComponentId`, else its `Name`-derived one |
| composed audit sink | writes + persists an `AuditRecordClass` behind the audit seam | `ServerApp.AuditSinks` | `ComponentId.forCompanionImpl "IAuditSink" <sink name>` |

A module that registers one more `DataType` surfaces with **no change to `DataFootprint.fs`**.

`Reads` is deliberately *not* derived — the registration does not say whether a producer reads its own objects back, and claiming it would over-state the footprint. `ContainsPii` is likewise never inferred: nothing in a registration can tell an order quantity from a home address. A derived class defaults to `false`, which is the conservative default for *behaviour* (an undeclared class adds no preflight finding); it is not a claim that the data is PII-free.

## The declared half

Two declarations, both keyed by the same `ComponentId` — a `FootprintSignature` sidecar beside `CapabilitySignature` (282) and `RequirementsSignature` (432), never a new field on `CompanionCapability` (growing a shipped F# record breaks its constructor).

```fsharp
// 1. What no registration can see — a companion's own backing store.
let declared =
    DataFootprint.none (ComponentId.forCompanionSlot "IBlobStorage")
    |> DataFootprint.withPersist (DataClass.pii "uploads" BlobClass BlobStorageSeam)
    |> List.singleton
    |> DataFootprint.signatureOf

// 2. The PII judgement over a class the derivation already found.
let reclassified = [ DataClass.pii "CustomerProfile" EntityClass DataObjectStoreSeam ]

let signature = DataFootprintDerivation.derive app declared reclassified
```

`derive` folds declaration on top of derivation and then applies the re-classification across the whole signature, so a derived class and its declared PII form collapse to one member rather than two. Classes are matched by **identity** — name + seam — so marking a class PII later never orphans a coverage claim written against it.

## Asking the questions (433.C)

```fsharp
let composed =
    DataFootprintDerivation.overManifest (ServerApp.compositionManifest app) signature

DataFootprint.persistedClasses composed         // what is stored
DataFootprint.seams composed                    // where
DataFootprint.classesBySeam BlobStorageSeam composed
DataFootprint.classesOfKind AuditRecordClass composed
DataFootprint.persistedPiiClasses composed      // the DSR-relevant subset
DataFootprint.componentsPersisting cls signature // …and whose store it is in
```

`overManifest` restricts to the components the manifest actually enumerates, so a stale declaration for something this deployment does not compose cannot inflate the surface.

## DSR / offboarding completeness (433.D, opt-in)

A **declarative coverage claim**, not a behavioural test: every persisted PII-flagged class must be claimed by a DSR path this deployment actually composes (an `IDataExporter.Name` / `IErasureHandler.Name`), or carry a declared exemption. Whether the named handler erases everything it should is a question for the handler's own tests; this rules out the failure that has no test at all — a class nobody remembered to wire up.

```fsharp
let claims = [
    DsrCoverage.create customerProfileClass [ "customer-export" ] [ "customer-erase" ]
    DsrCoverage.exempt auditRecordClass "retained under statutory audit obligation"
]

// Fold into the composition root's ServiceConfig hook, exactly as the
// Phase 281 / 431 / 432 registrations are folded:
let services =
    DataFootprintPreflight.serviceRegistrationForApp app claims signature services
```

Two rules, exported in the Phase 294 `ruleManifest` and the Phase 585 `classifiedRuleManifest`, both **structural-class** (a pure in-memory sweep — `SkipPreflight` does not bypass them):

| Code | Default severity | Fires when |
|---|---|---|
| `data-footprint-pii-uncovered` | `DefectWarning` (configurable) | a persisted PII class has no substantive claim, or every claimed path is unresolved |
| `data-footprint-dsr-path-unresolved` | `DefectWarning` | a claim names an exporter / erasure handler this deployment does not compose |

Pass `DefectError` to `serviceRegistration` to make DSR completeness a boot gate. **Nothing is registered when there is nothing to check** — no persisted PII class and no claim — so `ServerApp.empty |> ServerApp.run` composes a byte-identical service collection.

## CI gate

`composition-baselines/data-footprint-baseline.json` is a third golden file beside the composition and event-topology baselines, approved by the same flag:

```powershell
$env:TOOLUP_APPROVE_COMPOSITION = "1"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_COMPOSITION = $null
```

The failure renders through `DataFootprint.renderDelta`, which prints each class with its `PII` marker — so "a component started persisting personal data" is legible in the failure itself.

## Rollback

Delete the declarations and the `serviceRegistration` call. Nothing else reads the footprint; no shipped default depends on it.
