# 1202 — A tree-lowered surface loads the Fuaran-UI renderer on demand

**Additive, 0.39.0. No composition root changes.** `ToolUp.Platform.Client` gains an internal seam that lowers a
Fuaran-UI tree to the `ReactElement` a module view returns, with the renderer in a chunk of its own. No forge surface
uses it yet, so a shell built on this version loads exactly what it loaded before.

## What changes

| Before | After |
|---|---|
| `ToolUp.Platform.Client` does not depend on the Fuaran-UI renderer | it takes `Fuaran.UI.Renderer` (Apache-2.0, nuget.org) at the Fuaran-UI pin, and with it `Fuaran.UI` and their dependencies |
| no tree lowering in the client tier | `TreeLowering` (internal): the authoring helpers and `lower`; `TreeLoweringRuntime` (internal): the renderer, reached only through a dynamic import |
| — | a surface drawn through `lower` shows a placeholder (`role="status"`, `aria-busy`) until the renderer chunk arrives, the shape a grid or chart shows |
| — | a renderer chunk that fails throws into the nearest error boundary; in the SDK shell that is `ModuleBoundary`, whose **Reload module** fetches it again |
| the gate script checks the AG chunks | it also refuses a shell whose first load holds the renderer (`FuaranUIRendererAtBoot`) or whose `TreeLoweringRuntime` chunk is not on demand |

## What a consumer edits

Nothing. A consumer's own Vite build splits the chunk with no setting, because the renderer is reached only through
`import("./TreeLoweringRuntime.js")`. A consumer restoring the package from a feed resolves that import through the
`TreeLoweringRuntime.js` re-export packed beside the source under `fable/`; nothing has to be copied. Two things are
worth knowing:

- **Restore reaches nuget.org for the Fuaran-UI packages,** as it already does for a consumer of the fact
  presentation packages. A Fable compile of the client tier takes slightly longer, because Fable compiles the
  renderer's sources too; the bundle does not grow while nothing calls `lower`.
- **A program that mounts a tree surface outside the SDK shell needs an error boundary of its own**, as it does for a
  grid since 0.38.0, or a failed renderer chunk unmounts its React root.

A surface in another assembly, such as a fact browse in `ToolUp.AI.Client`, cannot reach the internal seam yet; the
phase that adds the first such surface decides how it is exposed.

## What it costs a shell that draws a tree at boot

Measured with three real surfaces as trees in a scratch tree: the first load is 1,803.92 KiB with the renderer on
demand against 2,078.25 KiB with it imported statically and 1,476.33 KiB with no tree surface. The renderer chunk is
284,224 bytes. The rest is `Fuaran.UI`'s node vocabulary and its compute closure, which the authoring helpers import
statically. That is over the first-load ceiling, which is why no forge surface draws a tree yet; the figures are in
`docs/platform/budgets.md`.

## Verification

```powershell
pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipLoad
```

The shell's first load is unchanged, and the run stops with a named finding if a boot path imports the renderer
statically. `dotnet run --project Build.fsproj -- VerifyFable` runs the three client-harness cases that pin the
placeholder in a prerender, hydration keeping the server's node, and a failed chunk recovered by Reload.

## Rollback

Revert the phase's commits. Nothing persisted depends on it.
