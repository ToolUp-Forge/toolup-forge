# Migration — Phase 286: composition structural diff (`CompositionDiff.diff`)

**Status:** net-new, additive, pure. No public surface changed; no default path changed;
byte-for-byte unchanged for a deployment that never diffs (GP 11 / GP 13). No consumer action
required to upgrade.

## Why

The Phase 280 [`CompositionManifest`](../../src/ToolUp.Platform.Server/Server/CompositionManifest.fs)
describes *one* composition — every module / companion slot / datatype / tool by its stable
Phase 279 `ComponentId`, plus the config knobs that shaped composition. Its natural companion is a
*diff*: given the manifest of a known-good composition and the manifest of a candidate, report the
whole surface change. `ConfigDriftDetector` (Phase 9q) only hashes the companion-assembly *set* — a
boolean "did anything change?"; `CompositionDiff.diff` reports the *specific* field deltas keyed by
identity: which module vanished, which companion impl was swapped, which config knob was flipped.

Use cases: deploy review, generated release notes, GitOps drift detection, and the Phase 287
composition golden-file CI gate (which renders `CompositionDiff.render` as its failure message).

## What shipped

New file [`CompositionDiff.fs`](../../src/ToolUp.Platform.Server/Server/CompositionDiff.fs)
(namespace `ToolUp.Platform`):

- **`CompositionDelta`** — the structural difference between two manifests. Per kind (modules,
  companion slots, datatypes, tools): `…Added` / `…Removed` / `…Changed`; plus `ConfigKnobsAdded`
  / `…Removed` / `…Changed`.
- **`ComponentEntryChange`** — a `Changed` entry that KEPT its stable `ComponentId`: reports the
  specific field deltas — `LabelDelta: (string * string) option` (a module renamed under a stable
  explicit id) and `ImplDelta: (string option * string option) option` (a single-impl slot whose
  impl sub-id moved). At least one delta is populated; it is never a bare "changed".
- **`ConfigKnobChange`** — `{ Name; Before; After }` for a knob whose value moved.
- **`CompositionDiff.diff : CompositionManifest -> CompositionManifest -> CompositionDelta`** —
  keyed by stable `ComponentId` throughout (config knobs by `Name`), never by list position, so the
  result is **order-independent**: two otherwise-identical compositions registered in a different
  order diff to `empty`. An identical pair always diffs to `empty`.
- **`CompositionDiff.empty`** / **`CompositionDiff.isEmpty`** — the empty delta + its predicate.
- **`CompositionDiff.render : CompositionDelta -> string`** — a deterministic, human-readable
  rendering (the CI-gate failure message); the empty delta renders to a single benign line.

### Add / remove / swap semantics

- A **module** added/removed → `Modules{Added,Removed}`. Renamed while holding an explicit
  `ComponentId` → `ModulesChanged` with a `LabelDelta` (not remove + add).
- A **multi-impl companion** swap (audit sink `splunk` → `datadog`) surfaces as remove + add,
  because the `ComponentId` composes the interface slot with the impl sub-id (Phase 279 rule) — the
  old and new impls are distinct identities.
- A **single-impl companion** slot whose impl sub-id moved under a stable slot id →
  `CompanionSlotsChanged` with an `ImplDelta`.
- A **config knob** whose value moved → `ConfigKnobsChanged` with `Before` / `After`.

## Consumer action

None. This is additive, pure substrate. A deployment that never calls `CompositionDiff.diff`
constructs nothing and pays nothing.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `CompositionDiffTests` (in `ToolUp.Platform.Tests`, run by `VerifyAll`) — add / remove / swap /
  config-change each surface as the right delta; a renamed module (stable id) is a changed entry
  with a label delta; an identical pair diffs to empty; the diff is order-independent.
