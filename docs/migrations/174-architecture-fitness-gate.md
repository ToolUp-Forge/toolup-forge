# Migration — Phase 174: architecture-fitness dependency-direction gate

**Type:** test-only, no production behaviour change (GP 13).

## What changed

Added an **architecture-fitness** test pack to `ToolUp.Platform.Tests` that codifies the layer
boundaries the Phase 15d structural reorg established and fails the build (under `VerifyAll`, hence
CI) the moment a new `ProjectReference` or `open` re-introduces a forbidden edge. Two new files,
registered append-only in `Program.fs` `allTests` and the test `.fsproj`:

- `Contracts/ArchitectureFitness.fs` — pure detectors (reflection over the compiled assembly graph +
  `System.IO`/`Regex` source-tree scans), no Roslyn/FCS hook.
- `InProcess/ArchitectureFitnessTests.fs` — the Expecto cases.

It asserts:

1. **Tri-tier reference direction** (reflection over `Assembly.GetReferencedAssemblies()`): `Core`
   references neither `Server` nor `Client`; `Server` does not reference `Client`; `Client` does not
   reference `Server`. Reflecting the real IL reference set means a forbidden `ProjectReference`
   shows up here even if no `open` does. Each violation names the offending `from → to` edge.
2. **AG Grid Enterprise split (GP 2)** — `ToolUp.Platform.Client` (the default-composed Client tier)
   carries no reference to the opt-in `Feliz.AgGrid.Enterprise` companion, and no `Client` source
   file `open`s the Enterprise shim module. The paid tier stays off the default path.
3. **No infra/framework opens under a `Shared/` folder (GP 10)** — a source scan flags any
   `open Microsoft.AspNetCore` / `open Giraffe` / `open Saturn` under any `src/**/Shared/` folder, so
   an infra type leaking into the cross-tier shared layer fails at CI time, not at a downstream Fable
   consumer's build.
4. **Sample modules are self-contained (GP 9)** — a cross-module `open` between distinct
   `samples/*.Module` units is flagged. Intra-module opens (a module opening its own `SharedTypes` /
   `ClientModel`) are allowed.

Every live-tree assertion is paired with a **fail-closed fixture** feeding the detector a synthetic
planted violation (a planted `Server→Client` reference; a planted `open Giraffe` under a `Shared`
fixture; a planted `open AgGridEnterprise` in a `Client` fixture; a planted cross-unit sample open),
so a green run means the gate actually checked something rather than going vacuously green.

## Do I need to do anything?

No — test-tier only; absent the pack, production is byte-for-byte unchanged. The pack runs as part of
`dotnet run --project Build.fsproj -- VerifyAll`. All consumers are ⛔ N-A in `SDK-ADOPTION.md`.
