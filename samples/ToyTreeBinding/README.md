# ToyTreeBinding — a second reference tree-binding (neutrality proof)

A deliberately tiny, hand-rolled typed-tree UI language (`ToyNode`) that binds the
host-neutral SDK seams **as a stranger to the substrate**. It exists to prove those seams are
genuinely renderer-neutral — by being a *second*, independent consumer of them, which is the SDK's
own "attempt a second implementation before declaring an interface stable" discipline.

The seams under test (all additive, opt-in):

| Seam | Where | The toy's binding |
|---|---|---|
| `ClientHostCapabilities` / `ClientHostView.withElementView` | `ToolUp.Platform.Client` | `Binding.fs` hosts the toy tree as a `ClientModule` view, routing all four capabilities (`Navigate` / `Notify` / `Dispatch` / `Call`) from the toy's own `ToyEvent` vocabulary |
| Server-rendered-fragment source (`IContentSource` / `IResolvedContentSource`) | `ToolUp.PublicRendering` | the toy lowers to an HTML string a fragment source returns |
| Scope-isolated live channel (`ILiveChannel`) | `ToolUp.Platform.Server` | a `ToyNode`-derived frame is pushed to a subscriber, scope-isolated |
| Host-neutral action authorizer (`IActionAuthorizer` / `ActionDescriptor`) | `ToolUp.Platform.Core` | each toy event maps to an `ActionDescriptor` gated default-deny |

## The whole language

`ToyNode.fs` defines three constructors — `Text`, `Element (tag, children)`, `OnClick (event, child)`
— and three lowerings, deliberately split by tier:

- `lower` → `ReactElement` (the client/Fable surface; the `withElementView` binding wires its events
  onto the four capabilities).
- `lowerToHtml` → a static HTML string (tier-neutral; drives the server-side fragment + live-frame
  seams the .NET test runner can reach).
- `toAction` → an `ActionDescriptor` (lets a host gate a toy event before routing it).

## Verify

This sample binds no network listener, so it has no port allocation. Two checks:

**Fable** (drives the toy + the full Client tier through the Fable compiler):

```powershell
dotnet tool restore
dotnet fable -o output --noCache
```

**Neutrality test pack** (the server-side seams + the open-core grep-guard + the all-four-capabilities
client-binding shape pin) lives in `ToolUp.Platform.Tests` and runs with the standard suite:

```powershell
dotnet run --project ../../src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "SecondBindingNeutrality"
```

## Not in the solution

Like `MinimalClient`, this sample is a Fable-verify target and is intentionally **not** in
`ToolUp.Forge.sln`. It is pulled into the `dotnet build` graph transitively as a project reference of
`ToolUp.Platform.Tests`, so the solution build still compiles it.
