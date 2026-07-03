# Migration — Phase 287: composition golden-file CI gate

**Status:** test/build-infra only, no public surface, no runtime code path. Byte-for-byte absent
from any consumer build (GP 11 / GP 13). No consumer action required.

## Why

Phase 175 shipped a public-API baseline guard: snapshot every packable assembly's public surface to
a checked-in `.approved.txt` and fail CI on a silent breaking removal. This phase applies the same
discipline to the **composed surface**: snapshot a reference composition's Phase 280
[`CompositionManifest`](../../src/ToolUp.Platform.Server/Server/CompositionManifest.fs) to a
checked-in golden file, and fail CI when a change silently drops a module / companion, swaps an impl,
or changes a datatype — until the change is acknowledged by regenerating the baseline. The failure is
rendered through the Phase 286 [`CompositionDiff`](../../src/ToolUp.Platform.Server/Server/CompositionDiff.fs)
so the operator sees exactly what moved, not just "the JSON differs".

## What shipped

New test file
[`Composition/CompositionBaselineTests.fs`](../../src/ToolUp.Platform.Tests/Composition/CompositionBaselineTests.fs)
(in `ToolUp.Platform.Tests`, run by `VerifyAll`):

- A **reference composition** (`referenceApp`) — a couple of modules with explicit stable
  `ComponentId`s, a datatype + tool, and an audit-sink companion — projected to a
  `CompositionManifest`.
- The manifest is serialised (indented, via the `ToolUp.Remoting.Json.SystemTextJson.FableConverters`
  option set) to a checked-in golden file at
  **`toolup-forge/composition-baselines/composition-baseline.json`**.
- The gate compares the live manifest against the committed baseline via `CompositionDiff.diff`; a
  non-empty delta fails the test and prints `CompositionDiff.render` (the readable Phase 286 delta).
- **Gate-mechanism fixtures** prove the gate fails-closed (a dropped module / companion trips it) and
  that the baseline JSON round-trips losslessly — without touching the committed golden file.

### Regeneration path (acknowledging an intended change)

Mirrors the Phase 175 `TOOLUP_APPROVE_API` approve flow. When the reference composition changes on
purpose, regenerate the golden file and commit it in the same PR so the composition change is
reviewed:

```powershell
$env:TOOLUP_APPROVE_COMPOSITION = "1"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_COMPOSITION = $null
```

### CI wiring

The gate lives in the Platform test pack, so `dotnet run --project Build.fsproj -- VerifyAll` runs it
with every other pack — no new CI job, an accidental composition regression fails the existing gate.

## Consumer action

None. Test/build infrastructure only; ships no runtime surface. A consumer never references the gate.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `CompositionBaseline` test list (5 cases) green: the reference composition matches the committed
  baseline; the manifest round-trips through the JSON format; a dropped module / companion trips the
  gate with a readable delta.
- End-to-end negative check: corrupting the golden file (dropping a module) fails the gate with the
  rendered Phase 286 delta naming the differing module; regenerating restores green.
