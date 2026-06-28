# Migration — Phase 202: second in-tree reference tree-binding (neutrality proof)

**Status:** additive, **no consumer action**. This phase ships a samples/tests-only artefact; it adds no public SDK surface, changes no shipped behaviour, and is byte-for-byte absent from any build that does not reference the toy sample (GP 11 / GP 13).

## What changes

Wave 16 shipped the host-neutral seams that let an external typed-tree UI language bind to ToolUp substrate:

- `ClientHostCapabilities<'Msg>` / `ClientHostView.withElementView` (Phase 110, `ToolUp.Platform.Client`)
- the server-rendered-fragment source `IContentSource` / `IResolvedContentSource` (Phase 111, `ToolUp.PublicRendering`)
- the scope-isolated live-session host `ILiveSessionHost` / `ILiveChannel` (Phase 112, `ToolUp.Platform.Server`)
- the host-neutral default-deny action authorizer `IActionAuthorizer` / `ActionDescriptor` (Phase 113, `ToolUp.Platform.Core`)

Each seam claims to be renderer-neutral — "any external tree language binds an adapter onto these hooks". The wave shipped exactly one (gated) consumer, so the claim was asserted, not demonstrated. Phase 202 adds a **second**, trivially-simple tree language — a hand-rolled `ToyNode` algebra — that binds every seam, discharging GP 12's own "attempt a second implementation before declaring an interface stable" discipline.

New artefacts (none on the public package surface):

| Artefact | Purpose |
|---|---|
| `samples/ToyTreeBinding/ToyNode.fs` | the toy algebra (`Text` / `Element` / `OnClick`) + three lowerings: `lower` → `ReactElement`, `lowerToHtml` → HTML string, `toAction` → `ActionDescriptor` |
| `samples/ToyTreeBinding/Binding.fs` | hosts the toy as a `ClientModule` via `withElementView`, routing all four `ClientHostCapabilities` |
| `src/ToolUp.Platform.Tests/InProcess/SecondBindingNeutralityTests.fs` | binds the toy against the fragment source, the live channel, and the action authorizer (default-deny intact); grep-guards the toy against forge-banned vocabulary; pins the all-four-capabilities client-binding shape |

The toy is a **stranger to the substrate**: it carries zero platform-specific vocabulary and binds only the public seams. No seam required a toy-specific change.

## Tier split (why the client binding and the server bindings live in different files)

The client seam (`withElementView`) is Fable-only; the fragment-source and live-channel seams are server-tier and not Fable-reachable. So the toy sample (`ToyTreeBinding.fsproj`, a Fable-verify target like `MinimalClient`, **not** in `ToolUp.Forge.sln`) binds the client seam, while the server-side seams are exercised in the `.NET` test pack using the toy's tier-neutral lowerings (`lowerToHtml` / `toAction`). The sample is pulled into the `dotnet build` graph as a project reference of `ToolUp.Platform.Tests`.

## Breaking change

None. No existing signature touched; no public surface added.

## Verification

- `dotnet build ToolUp.Forge.sln` clean (the toy compiles transitively via `ToolUp.Platform.Tests`).
- `dotnet run --project Build.fsproj -- Pack` green.
- Fable: `cd samples/ToyTreeBinding && dotnet fable -o output --noCache` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "SecondBindingNeutrality"` — 10 passed.

## Rollback

Delete `samples/ToyTreeBinding/`, the test pack, and the two registration lines in `ToolUp.Platform.Tests` (the `<Compile>`/`<ProjectReference>` entries + the `Program.fs` list entry). Nothing else references the toy.
