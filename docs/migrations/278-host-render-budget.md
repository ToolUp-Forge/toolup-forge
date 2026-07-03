# Migration 278 — hosted-tree render-cost budget gate

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

An AI-emitted (or runaway server-driven) hosted tree can blow up in node count, depth, or render
time; Phase 192 / 213 budget cold-start + web-vitals but neither sees **tree shape**. This phase
ships a neutral render-cost budget that warns at runtime (through the Phase 268 sink) and gates in CI
against fixtures.

New surface in `src/ToolUp.Platform.Client/Client/HostRenderBudget.fs` (namespace `ToolUp.Platform`):

- `HostRenderBudget` — `{ MaxNodes: int option; MaxDepth: int option; MaxRenderMillis: float option }`.
  Each dimension optional; all-`None` (`HostRenderBudget.unlimited`) is "not configured" (no
  measurement — GP 13).
- `HostRenderMeasure` — `{ NodeCount; Depth; RenderMillis }`. The host supplies it.
- `HostBudgetBreach` — `NodesExceeded | DepthExceeded | RenderTimeExceeded`.
- `HostRenderBudgetResult` — `WithinBudget | OverBudget of HostBudgetBreach list`.
- `HostRenderBudget.{unlimited, isConfigured, ofShape, measureTree, measureOf, evaluate,
  describeBreach, reportBreaches, enforce}`. `measureTree` is generic over any children-function
  (neutral over the tree language — GP 1). `reportBreaches` emits each breach as a Phase 268
  `HostRenderFault` and returns whether it was over budget — **non-fatal**; `enforce` is the opt-in
  hard-fail.

## How to adopt (opt-in)

```fsharp
let budget = HostRenderBudget.ofShape 5000 40   // max 5000 nodes, depth 40
let measure = HostRenderBudget.measureOf myTreeChildren (Some renderMs) rootNode
let result = HostRenderBudget.evaluate budget measure

// Runtime — warn through the Phase 268 sink (non-fatal):
HostRenderBudget.reportBreaches sink nodeId result |> ignore
// …or opt into a hard-fail:
// HostRenderBudget.enforce sink nodeId result

// CI — a fixture gate (assert a representative tree is WithinBudget).
```

A deployment that configures no budget performs no measurement and is byte-for-byte unchanged (GP
11/13).

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostRenderBudget"
cd samples/MinimalClient && dotnet fable -o output   # the client-tier budget compiles under Fable
```

## Rollback

Delete `HostRenderBudget.fs` + its `<Compile>` entry, `InProcess/HostRenderBudgetTests.fs` + its
`<Compile>` and `Program.fs` registration. No runtime impact on any deployment that never configured a
budget.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in render-cost budget for hosted trees.
No current matrix consumer hosts a typed-tree UI; a deployment that configures no budget is
byte-for-byte unchanged (GP 11/13).
