# Migration — Phase 436 null-composition dry-run harness (`CompositionDryRun`)

**Status:** net-new, opt-in, purely additive. No existing type, function, or default changed; nothing is registered into DI, no middleware is added, nothing runs at compose. A deployment that does not call anything below composes byte-for-byte what it did before and pays nothing at runtime (GP 11 / GP 13). **No consumer action is required to upgrade.**

## Why

Phase 295 proves a `CompositionDescriptor` is *lossless* — lower an app to data, rebuild it, get the same component-id set back. That is a serialisation law, and it holds just as well for a descriptor naming an S3 bucket nobody can reach. The runtime question — **does this thing actually compose?** — had one answer available: boot it, with every cloud account and credential the composition names.

`CompositionDryRun` answers it offline. Take any descriptor (hand-written, preset / archetype, or tool-emitted), rebind every companion slot to the in-process default the slot already declares, rebuild through Phase 284 / 292's versioned `ofManifest`, run the Phase 281 / 294 structural well-formedness rules over what came out, drive the Phase 291 lifecycle in declared init order, dispose in reverse, and report. **Zero external services, zero network, zero cloud accounts.** The whole run is a handful of milliseconds, so every descriptor edit, cook, preset and AI-emitted composition can carry a wiring smoke test.

## The null default is `None`, and that is derived

`ServerApp.empty` sets every companion slot to `None` / `[]`, and an empty app composes — the SDK supplies its own in-process default for each slot at `run` time (`HeaderAuthProvider`, `InMemoryNotificationChannel`, `NoOpAnonymousSessionMigrator`, …). So "rebind this slot to its in-memory / `NoX` default" *is* "do not bind it", and the null catalogue's registration for every slot is the identity fold.

There is therefore no hand-maintained table of per-slot defaults to drift: the slot universe is Phase 293's reflected `ComposableSurface.slots ()`, so a newly-shipped companion slot is null-bindable here the day its `ServerApp` field lands. A selection naming a companion interface that is **not** a slot on `ServerApp` — a composition-*wrapping* companion such as `IFactStore`, or a typo — has no such default, and that is a **finding**, not a crash.

## Using it

```fsharp
open ToolUp.Platform

// The one-liner: raises the rendered report on anything short of a clean
// compose, returns the report on success.
let report = DryRun.shouldCompose catalogue descriptor

// The same claim plus an offline-speed bound (the cookbook-verification shape).
DryRun.shouldComposeWithin (TimeSpan.FromMilliseconds 250.0) catalogue descriptor |> ignore

// Or the report as data, for a caller that wants to inspect rather than assert.
let report = CompositionDryRun.run catalogue descriptor

match report.ReportVerdict with
| Composes -> ()
| DoesNotCompose ->
    for finding in report.ReportFindings do
        printfn "%s %A %s" finding.FindingCode finding.FindingComponent finding.FindingDetail
```

With real per-component init, or declared lifecycle edges:

```fsharp
CompositionDryRun.options catalogue
|> CompositionDryRun.withEdges [ secretStoreId, auditSinkId ]   // Phase 291 init-before
|> CompositionDryRun.withInitProbe myInit                        // a throw becomes a finding
|> CompositionDryRun.withDisposeProbe myDispose
|> fun opts -> CompositionDryRun.runWith opts descriptor
```

`CompositionDryRun.bindNulls : CompositionDescriptor -> CompositionDescriptor` is exposed on its own for a caller that wants the null-bound descriptor without running it (`bindNullsWithFindings` adds the slots rebound, the holes filled, and any `MissingNullDefault` findings).

## A composition defect is never an exception

Every failure mode lands in `CompositionDryRunReport.ReportFindings` as a `CompositionDryRunFinding`, attributed to a `ComponentId` wherever the id is derivable:

| `FindingKind` | `FindingCode` | Fires when |
|---|---|---|
| `MissingNullDefault` | `dry-run-missing-null-default` | a selection names a companion interface that is not a `ServerApp` slot |
| `UnresolvedComponent` | `dry-run-unresolved-component` | a selected id resolves to no catalogue entry (one finding per id) |
| `UnfilledDescriptorHole` | `dry-run-unfilled-hole` | a hole survived null binding (hand-modified descriptor) |
| `SchemaMigrationRejected` | `dry-run-schema-migration` | the Phase 292 version could not be migrated |
| `WellFormednessDefect` | *the rule's own code* | a Phase 281 / 294 rule fired — the code and message are the rule's, verbatim |
| `LifecycleOrderUnsatisfiable` | `dry-run-lifecycle-order` | the Phase 291 edges are cyclic; nothing is initialised |
| `ComponentInitFailed` | `dry-run-init-failed` | an init probe threw; init stops, everything already up still disposes |
| `ComponentDisposeFailed` | `dry-run-dispose-failed` | a dispose probe threw; dispose continues past it |

`FindingSeverity` reuses the Phase 281 `CompositionDefectSeverity`, so a rule's declared severity carries through: a `DefectWarning` is reported and the verdict stays `Composes`. Attribution for a `WellFormednessDefect` is best-effort — a rule evaluator returns a message, not an id — so `None` is an honest answer rather than a guess.

## Where the dry run deliberately stops

* **It does not call `ServerApp.run`.** Running binds sockets and starts hosted services, which is the opposite of "zero external dependencies".
* **It runs `CompositionValidator.checkClassWith` directly, not the Phase 9m `IConfigValidator` aggregator.** A companion's registered validator legitimately probes a remote dependency. The Phase 585 *structural* class is exactly the set of invariants answerable from memory, which is exactly the set a null dry run can honestly check. The external-probe class is opt-in (`CompositionDryRun.withExternalProbeRules true`) and empty today.
* **The caller's own binding for a slot is never reached.** The null catalogue is overlaid *after* the caller's entries, so a catalogue that binds `IBlobStorage` to a vendor companion cannot be invoked by a dry run. That is the point: the harness answers "does it compose?", not "does the vendor binding work?".

## Placement note (436.C)

The Phase 285 contract packs live in `ToolUp.Platform.Tests`, which is `IsPackable=false` — a consumer cannot reference it, so a helper placed beside them would be unreachable by the consumer test suites and cookbook verification checklists this affordance exists to serve. `DryRun.shouldCompose` therefore ships in `ToolUp.Platform.Server`, the package every such consumer already references, and is deliberately **Expecto-shaped but Expecto-free**: it raises a readable failure the way an assertion does, so it drops into an Expecto (or xUnit, or NUnit) test unchanged without dragging a test-framework dependency into a runtime package. A function that is never called costs nothing (GP 13).

## Naming note

The public types are prefixed `CompositionDryRun*` (`CompositionDryRunReport`, `CompositionDryRunFinding`, `CompositionDryRunOptions`, …) rather than the shorter `DryRun*`. `ToolUp.Platform` already carries `DryRunReport` from `ColumnMappingTypes` (the column-mapping validation preview), and F#'s last-declared-wins record / type inference would silently re-point existing consumer source at the new type. The module `DryRun` is deliberately **not** `[<AutoOpen>]` for the same reason.

## Verification

`src/ToolUp.Platform.Tests/InProcess/CompositionDryRunTests.fs` — 21 cases: null-binding (collapse to slot id, inputs cleared, idempotence, hole filling, the derived slot universe, id parsing), the missing-null-default finding, a green preset run with lifecycle drive + reverse dispose, declared edges, the cyclic order, a throwing init probe, unresolved ids, a version gap, a failed rule with its own code, duplicate-id attribution, the vendor-binding overlay, the two `DryRun.*` affordances, the wall-clock bound, and the GP 11 no-footprint check.
