# Migration 270 — hosted-tree capability/version negotiation gate

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

The host runtime (Phase 110) and an external tree language version independently, and nothing checked
compatibility — so a tree that needed a newer capability (Phase 266's `Invoke`, or a binding shape the
host predates) rendered **broken**: a cryptic partial render with a console warning. This phase adds a
lightweight mount-time handshake that fails **loud and early** with a structured error naming the gap.

New surface in `src/ToolUp.Platform.Client/Client/ClientHostBridge.fs` (namespace `ToolUp.Platform`):

- `HostCapabilitySet` — `{ Version: int; Capabilities: Set<string> }`. The host advertises its version
  + supported ids (the four built-ins + any Phase 266 registered ids); a tree declares its minimum
  required set.
- `HostCapabilityMismatch` — `VersionTooLow of required * hostVersion` | `MissingCapabilities of missing list`.
  A structured error, not a console warning. `HostCapabilityMismatch.describe` renders the gap.
- `HostCapabilitySet.{builtInCapabilities, CurrentVersion, defaultRequired, host, requires}` — helpers.
  `defaultRequired` is the four built-ins at the current version; a view declaring no required set uses
  it and **always mounts** (GP 11).
- `HostCapabilityNegotiation.negotiate : required -> host -> Result<unit, HostCapabilityMismatch>` —
  called at mount. Version check first (coarser incompatibility), then the missing-capability set.
- `ClientHostNegotiatedView.withNegotiatedElementView` — `withElementView` with a mount-time
  handshake: on success the tree renders as usual; on a mismatch it renders a **structured error
  state** (never a silent partial render) and reports the gap through an `onMismatch` hook (wire the
  Phase 268 render-failure telemetry sink here; pass `ignore` when none is composed).

Complements Phase 203's *structural* (SSR↔CSR) parity check with a *capability* compatibility check.

## How to adopt (opt-in)

```fsharp
let host = HostCapabilitySet.host registeredCapabilityIds        // four built-ins + Phase 266 ids
let required = HostCapabilitySet.requires 1 [ HostCapability.ClipboardWrite ]  // what the tree needs

ClientModule.create spec
|> ClientHostNegotiatedView.withNegotiatedElementView required host reportToPhase268Sink (fun model dispatch host ->
    MyTreeRuntime.render (view model) host)
|> ClientModule.register
```

An existing `ClientHostView.withElementView` caller is unchanged — it implicitly requires only the
four built-ins (`defaultRequired`), which every host satisfies.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostCapabilityNegotiation"
cd samples/MinimalClient && dotnet fable -o output   # client-tier bridge compiles under Fable
```

## Rollback

Remove the Phase 270 append block from `ClientHostBridge.fs`, delete
`InProcess/HostCapabilityNegotiationTests.fs` + its `<Compile>` and `Program.fs` registration. No
runtime impact on any deployment that never negotiated a capability set.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in client-tree-hosting handshake. No
current matrix consumer hosts a typed-tree UI; a deployment that declares no required set is
byte-for-byte unchanged (GP 11/13).
