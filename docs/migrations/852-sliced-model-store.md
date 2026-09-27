# Phase 852 — a sliced model store: the shell and the active module subscribe to what they read

**What changes for you: one rule to check in your module views, and nothing to change in your code.**
No public API moved or changed type; four entry points were added. `Client.run` now mounts the shell
on a model store, so a message re-renders only the part of the shell whose slice it changed. A
module whose view reads only `(state, dispatch)` and React contexts — which is every module written
to [the module convention](../platform/modules.md) — renders exactly what it rendered before, less
often. Read "If your view reads something other than its arguments" below; if it does not apply,
upgrade and move on.

---

## What it is

Before this phase, `withReactSynchronous` mounted one React root and handed it a freshly constructed
view — the whole shell, chrome, sidebar and active module — on every render
(`root.render (view model dispatch)`), so React reconciled the entire tree from the top for every
message, with no boundary the runtime provided. [Phase 851](851-render-per-drain.md) made that
happen once per drain and gave module views a stable `dispatch`; this phase removes the remaining
cost, **scope**.

1. **A store binding (opt-in, any program).** `Program.withReactStore store placeholderId` publishes
   each model to a `ModelStore` — once per task, through the same scheduler the default binding uses
   — and mounts ONE root component that reads the store through React's `useSyncExternalStore` and
   calls `Program.view` on it. Any component below can read a slice with
   `ModelStore.useSelector store selector equals` and re-renders only when `equals` says the slice
   changed. `Program.withReactStoreHydrate` is the same with `hydrateRoot`, reading the first
   published model (the server-rendered one) as its server snapshot. A program that does not call
   either renders exactly as before (GP 11).
2. **The SDK shell uses it (`Client.run`).** The shell's chrome is a memo boundary over the model
   with `ModuleStates` masked out; the active module is a memo boundary that reads
   `ModuleStates[activeId]` from the store **by reference**. `Client.program`, `Client.view` and
   `Client.viewWithSignIn` keep the whole-tree path, so every outer composer (the AI assistant's
   `withAIAssistant`, a custom composition root, `Bootstrap.Hydration.run`) is unchanged.
3. **Two per-render allocations that defeated the boundaries are gone.** `Model.ProcessedData` was
   re-aggregated into a new list on every message, and the resolved message catalog was a new record
   on every render (`MessageCatalog.resolve` stamps the locale onto a copy). Both now keep their
   previous object when nothing changed — which also stops every `ProcessedData.forType` and
   `useMessages` reader re-rendering per message on the whole-tree path.
4. **The AG Grid wrapper no longer serialises its props.** `MemoizedGrid` compared every render's
   props by `JSON.stringify` — one full serialisation, rows included, per render. It now walks the
   props reference-first and decides exactly as the serialisation did (functions and `undefined`
   keys invisible, `NaN` equal to `NaN`, dates by time), allocating nothing.

## Before and after

Same harness as 849 and 851 (`ClientBench`, `src/ToolUp.AI.Client.Tests`, seed 849001, node v25.9.0
win32/x64, React production build), plus the render-scope leg this phase adds: a one-module SDK shell
mounted in jsdom and driven through one module message and one chrome message, on both paths. The
counts are components rendered — the shell's chrome (counted by a `GlobalOverlays` thunk, which the
chrome invokes once per construction) and the module's view function.

| Message | Whole-tree path (before; every `Client.program` composer) | Sliced store (after; `Client.run`) |
|---|---|---|
| a **module** message (`ModuleMsg`, changes only the active module's state) | 1 chrome + 1 module | **0 chrome** + 1 module |
| a **chrome** message (`CommandPaletteOpened`, changes only a shell field) | 1 chrome + 1 module | 1 chrome + **0 module** |

| AG Grid wrapper, per render, 1000 rows shared with the model, fresh `rowData` array and rebuilt `columnDefs` | Before | After |
|---|---|---|
| props compare | ~205 us (one `JSON.stringify` of the new props) | ~28 us (`sameGridProps`, no allocation) |

The other 849/851 levers are unchanged by this phase on the same run (render-hook calls per drain 1;
async hop min 0.003 ms; view per dispatch 748 us over the 200-row bench view, which does not mount
React).

**Falsifiers.** The render-scope counts are asserted on every Fable-pack run by `SlicedStoreTests`
in both directions: the whole-tree case must read 1 + 1 for both messages — so the chrome counter
demonstrably counts chrome renders, and a 0 on the sliced path is not a counter that never fires —
and the sliced case must read 0 + 1 and 1 + 0. The module message's text is read back from the DOM
(`count 1`), so the message reached the screen rather than only a counter. The grid figure is a
Node micro-measurement (min of 7 × 2000 iterations) of the two compares over the same props pair;
it is illustrative, not budgeted.

## If your view reads something other than its arguments

Under the store, the active module's view runs when **its own state** changes — not whenever the
shell happens to re-render. A view that read a module-level mutable, or a value a callback wrote
outside `update`, used to be refreshed by the next unrelated message; now it is refreshed only by
its own. The fix is the convention: put the value in your model (dispatch a message when it
changes), or read it through a React context or hook, which re-render their readers on their own.
The shell's shared data already arrives that way (`ProcessedData.forType`, feature flags, branding,
the message catalog).

## If your `update` copies state it did not change

Nothing breaks. A module whose `update` answers a no-op message with an equal *copy* of its state
(`{ model with X = model.X }`, a re-mapped list) re-renders its whole view for no visible change —
what every message cost before. Returning the same object is the rule
[`modules.md`](../platform/modules.md#return-the-same-state-when-nothing-changed) now states, and
what it buys.

## If you compose your own program over `Client.program`

Nothing changes: its view is the whole-tree `viewWithSignIn`, and whichever `withReact*` binding
you mount it with renders it as before. To adopt the store in a program of your own, create a store
before building the program and pass it to the binding:

```fsharp skip=fragment
let store = ModelStore.create<Model, Msg> ()

Program.mkProgram init update view
|> Program.withReactStore store "elmish-app"
|> Program.run
```

and read slices in components with `ModelStore.useSelector store (fun m -> m.Part) ModelStore.refEquals`.
The shell's sliced view itself is not public: its module host reads the SDK's own model type, so it
mounts only under the store `Client.run` creates.

## If you pass callbacks to an AG Grid

Unchanged, including the documented limitation: a render whose only change is a fresh callback
closure keeps the grid's previous callbacks. The compare that decides it is now `sameGridProps`
rather than a serialisation, with the same answers.

## Verification

- `dotnet run --project Build.fsproj -- VerifyFable` — `SlicedStoreTests` (store binding, selector
  scope, shell render scope on both paths, the chrome comparison, the grid compare).
- `cd src/ToolUp.AI.Client.Tests; NODE_ENV=production node --import ./register-loader.mjs output/Program.js ClientBench --seed 849001`
  — the `render scope` lines and the `renderScopeWholeTree` / `renderScopeSliced` levers.

## Rollback

Revert the phase's commit. The added entry points (`ModelStore`, `StoreSnapshot`,
`Program.withReactStore`, `Program.withReactStoreHydrate`) have no callers outside the SDK shell and
its tests; `Client.run` returns to `withReactSynchronous` over `viewWithSignIn`.
