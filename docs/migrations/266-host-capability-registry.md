# Migration 266 — extensible host-capability registry (`IHostCapabilityRegistry`)

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

Phase 110 shipped a fixed **four** host capabilities a hosted typed-tree routes its actions through
(`ClientHostCapabilities<'Msg>` — `Navigate` / `Call` / `Notify` / `Dispatch`). Everything else a tree
language needs — clipboard-write, file-read, set-state, any consumer-registered capability — was
delegated to the *tree language's own* browser runtime, which bypasses forge substrate **and** the
Phase 113 action authorizer.

This phase adds a neutral, additive registry that closes that seam:

- **New type** `IHostCapabilityRegistry` (in `ToolUp.Platform.Core`,
  `Shared/Types/HostCapabilityRegistry.fs`): `Register : CapabilityId -> HostCapabilityHandler -> unit`
  and `Invoke : CapabilityId -> HostCapabilityArgs -> AccessContext -> Async<HostCapabilityOutcome>`.
  A host wires it once at compose time; a tree invokes a capability by opaque id.
- **Every `Invoke` is gated by `IActionAuthorizer`** (Phase 113, default-deny). The invoke maps to a
  neutral `ActionDescriptor` (`Kind = "invoke"`, `Target = <capabilityId>`, `Scope` = the caller's own
  scope, or a reserved `toolup.targetScope` arg), routes through the authorizer, and runs the handler
  **only** on `Allow` **and** when a handler is registered. An unregistered id denies; an empty
  registry denies everything (GP 13).
- **Forge-public built-ins** `HostCapability.ClipboardWrite` / `HostCapability.FileRead` +
  `registerClipboardWrite` / `registerFileRead` helpers. Each is opt-in — the raw browser side-effect
  is injected by the host tier (so `Core` stays neutral + Fable-safe); only the args/result shaping
  ships. Not registered ⇒ `Invoke` denies.
- **Additive client seam** (in `ToolUp.Platform.Client`, `Client/ClientHostBridge.fs`): a separate
  `ClientHostInvoke` surface + `ClientHostInvokeView.withInvokableElementView` builder that hands a
  hosted view the four built-in hooks **plus** the authorizer-gated `Invoke`. Kept separate from
  `ClientHostCapabilities` (whose four-member object-expression shape every existing binding
  implements) so the existing bag and every `ClientHostView.withElementView` caller compile
  byte-for-byte unchanged (GP 11).

`CapabilityId` is an opaque string the host owns; args cross the boundary as a stringly-typed
`Map<string,string>` prop bag — the same erasure seam the `NarrativeElement.Component` renderer uses —
so no tree-language payload type reaches forge. The registry satisfies the six portability rules (GP
12): identity by value, async at every boundary, stateless handlers.

## How to adopt (opt-in)

A deployment that wants a hosted tree to reach capabilities beyond the fixed four:

```fsharp
// Compose the registry over the deployment's action authorizer (default-deny).
let registry = HostCapabilityRegistry.create actionAuthorizer

// Register the forge-public built-ins (each opt-in — the host injects the effect):
registry |> HostCapability.registerClipboardWrite (fun text -> async { do! Clipboard.writeText text })
registry |> HostCapability.registerFileRead       (fun path -> async { return! HostFiles.read path })

// Register any custom capability:
registry.Register (CapabilityId "report.export") (fun args ctx -> async { ... ; return Map.empty })

// Hand a hosted view the four hooks + the gated Invoke:
ClientModule.create spec
|> ClientHostInvokeView.withInvokableElementView registry (fun model dispatch host invoke ->
    MyTreeRuntime.render (view model) host invoke)
|> ClientModule.register
```

Gate invokes in the deployment's `ActionPolicy` with rules on the `"invoke"` kind, e.g.
`{ Kind = "invoke"; Target = "report.*"; Requirement = ActionRequirement.Permission("reports", Write) }`.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostCapabilityRegistry"
cd samples/MinimalClient && dotnet fable -o output   # client-tier bridge compiles under Fable
```

## Rollback

Delete `Shared/Types/HostCapabilityRegistry.fs` + its `<Compile>` entry, remove the Phase 266 append
block from `ClientHostBridge.fs`, delete `InProcess/HostCapabilityRegistryTests.fs` + its `<Compile>`
and `Program.fs` registration. No runtime impact on any deployment that never composed a registry.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in client-tree-hosting seam. No current
matrix consumer hosts a typed-tree UI language, and a deployment that composes no registry is
byte-for-byte unchanged (GP 11/13).
