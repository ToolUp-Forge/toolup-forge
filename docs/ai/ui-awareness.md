# `ToolUp.AI.UiAwareness` — reference UI-inspection sample

This page walks through the `withInspectState` → `UiStateReport` seam and the
`ToolUp.AI.UiAwareness` companion built on it, using the worked example at
[`samples/MinimalClient/UiAwarenessSample.fs`](../../samples/MinimalClient/UiAwarenessSample.fs)
as the reference. It complements the companion's own
[`src/AIExtensions/UiAwareness/README.md`](../../src/AIExtensions/UiAwareness/README.md)
(package layout, access control) — read that first for the full surface; this
page is the "what does adopting it look like in a real module" walkthrough,
and the explicit boundary between what forge answers natively and what stays
an external adapter's job.

## The seam: `withInspectState` → `UiStateReport`

A module answers "what is on the user's screen right now" by declaring a pure
projection of its own Elmish model — nothing more:

```fsharp skip=fragment
let private project (model: Model) : UiStateReport =
    UiStateReport.empty
    |> UiStateReport.withField "filter" (sprintf "\"%s\"" model.Filter)
    |> UiStateReport.withField "refreshCount" (string model.RefreshCount)
    |> UiStateReport.withAction "refresh" (not (System.String.IsNullOrWhiteSpace model.Filter))

let register () : ErasedModule =
    ClientModule.create moduleSpec
    |> ClientModule.withView view
    |> ClientModule.withInspectState project
    |> ClientModule.register
```

Three maps, all string-keyed, all host-neutral data (Phase 536,
`ToolUp.Platform.Client/Client/SDK.ClientTypes.fs`):

- **`Fields`** — each field's current value, as a JSON-encoded leaf (a string
  carries its own quotes — `Fable.Core.JS.JSON.stringify` or an equivalent
  encoder produces the right shape; a bare number's decimal text is already a
  valid JSON leaf).
- **`Selections`** — the key(s) of the current selection per selectable
  surface. An empty list is a real answer ("nothing selected"), not an absent
  one.
- **`Actions`** — whether each named action is currently enabled, mirroring
  whatever `prop.disabled` / button-guard logic the view itself already
  computes, so the report and the rendered UI can never silently disagree.

Nothing about the projection is AI-specific — it is exactly the same shape a
non-AI host (a test harness asserting on module state, a future debugging
panel) could read. `ModuleStateObserver` (`Client/ModuleStateObserver.fs`)
records the latest report at the shell's own publish points — a `ModuleMsg`
update, an action dispatch — so `ModuleStateObserver.tryInspect moduleId`
always answers with the most recent one, or `None` before the module's first
update.

## The companion: `_platform.ui.inspect_active_module`

`ToolUp.AI.UiAwareness` is the thin, opt-in AI-facing reader over that
substrate — it introduces no new module-authoring API. Composing it is two
calls, one per tier:

```fsharp skip=fragment
// Server composition root
aiApp |> ToolUp.AI.UiAwareness.Server.AICompose.register

// Client boot
ToolUp.AI.UiAwareness.Client.InspectActiveModule.install ()
```

`register` appends one `ClientResident`, `ReadFacts`-only, no-argument tool to
the deployment's `AITools`; `install` registers the browser-side executor the
agent loop's SSE dispatch calls by name. Neither call does anything unless
both are made — a deployment that never composes the companion is
byte-for-byte unchanged (GP 11 / GP 13), and a client that installs the
executor without the server registering the tool never receives a call to
run it.

The tool takes no parameters: the module inspected is always the **request's
own** active module and page, read off `ClientToolContext`, never a module
name the model supplies. That is what keeps the read-only guarantee real
under the same access-control story every other tool passes (per-module RBAC
over the reserved `_platform.ui` source, `IClientToolAuthorizer`, the
`ReadFacts` effect ceiling) — see the companion README for the full
walkthrough.

## The worked example

[`samples/MinimalClient/UiAwarenessSample.fs`](../../samples/MinimalClient/UiAwarenessSample.fs)
declares a small two-field, one-action module (`Filter` text + a
`RefreshCount`, with a `refresh` action enabled once the filter is non-empty)
and installs the companion's client executor at module load — it sits beside
`ToolUp.AI.SampleClientTool.Client.SampleHandler` (referenced from
[`src/AI.Samples/ToolUp.AI.SampleClientTool.Server/Server/Compose.fs`](../../src/AI.Samples/ToolUp.AI.SampleClientTool.Server/Server/Compose.fs))
so the two `ClientResident` tool shapes read side by side:

| | `ToolUp.AI.SampleClientTool` | `ToolUp.AI.UiAwareness` |
|---|---|---|
| Shape | argument-taking (`{op, a, b}`) | no-argument, read-only |
| What it does | computes something the model asked for | reports what the module already knows about itself |
| Model author effort | one tool definition + one executor per capability | one `withInspectState` projection per module; the tool itself is shared |
| Reference file | `src/AI.Samples/ToolUp.AI.SampleClientTool.Server/Server/Compose.fs` | `samples/MinimalClient/UiAwarenessSample.fs` |

`samples/MinimalClient` is the repo's in-tree Fable smoke-test sample (see its
`.fsproj` header): `dotnet fable -o output` there transpiles the sample
module's `withInspectState` chain and the companion's client executor through
the real Fable + Feliz + ToolUp.Elmish toolchain, which is what proves the
seam is Fable-safe end to end. It is a compile-time proof, not a running
demo — the sample is not wired into a `ClientConfig` module shell (the
`Program.mkProgram` loop `samples/MinimalClient/Client.fs` runs is a bare
Elmish counter, not the shell `ModuleStateObserver` publishes through), and
there is no live LLM or browser session to drive the tool end to end in this
environment. `src/ToolUp.AI.Client.Tests/ModuleInspectStateTests.fs` and
`UiAwarenessInspectTests.fs` are what exercise the seam's actual runtime
behaviour (a driven shell `update`, an asserted report) under Node's
`node:test` harness — read those for the "does the reported state actually
follow a live update" proof; this page and the sample are for "here is what
adopting the seam looks like".

## The explicit boundary: native awareness vs. driving the UI

`ToolUp.AI.UiAwareness` answers **"what is on the screen"** — read-only,
model-projection awareness — natively in forge, on the same substrate every
module already has (`ClientModule`, `ModuleStateObserver`). It deliberately
does **not** answer **"click this / fill that in"** — driving the UI from the
model's side is a different capability with a different trust boundary
(arbitrary DOM mutation from model-authored instructions, not a pure
projection a module author wrote and reviewed), and stays out of this
companion by design:

- A `ClientResident` tool that *sets* state (`_platform.ui.set_field`, the
  worked example in [`extending.md`](extending.md#client-resident-tools)) is
  the SDK-native pattern for a module that wants to expose a specific,
  reviewed action — the module author still writes the tool definition and
  decides exactly what it may touch.
- A generic "drive any element on the page" capability — one that walks the
  DOM rather than reading a module's own declared projection — is
  **optional and external**: an adapter a deployment composes deliberately,
  reviewed against `IClientToolAuthorizer` like any other write-capable
  `ClientResident` tool, never something `ToolUp.AI.UiAwareness` or the core
  module-authoring surface reaches for on its own.

That split keeps the native surface's trust story simple: every fact
`_platform.ui.inspect_active_module` can report is something a module author
explicitly chose to project, over data the requesting user could already see
in their own browser.

## See also

- [`src/AIExtensions/UiAwareness/README.md`](../../src/AIExtensions/UiAwareness/README.md) — package layout, full result shapes, access-control detail.
- [`extending.md`](extending.md#client-resident-tools) — the general `ClientResident` tool-authoring contract this companion is built on, and the sibling `ToolUp.AI.SampleClientTool` reference companion.
- [`../migrations/14r-tool-aware-rag-framing.md`](../migrations/14r-tool-aware-rag-framing.md) — a RAG deployment's prompt framing already treats `_platform.ui.*` / any `ClientResident` tool as *live-interface*: when this companion (or any such tool) is composed, `RAGCompose.composeRAG` appends a framing paragraph that routes "what's on my screen" questions to the inspection tool instead of retrieval, with no consumer change required.
- [`../migrations/538-live-interface-flag.md`](../migrations/538-live-interface-flag.md) — the `IsLiveInterface` flag `_platform.ui.inspect_active_module` sets (`Location = ClientResident` already implies it; the flag exists for the server-resident case).
