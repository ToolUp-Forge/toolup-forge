# Migration 300 — composition capability sandbox (`CompositionCapabilityGate`)

**Status:** additive, opt-in, **default off** — a deployment that composes no enabled gate is byte-for-byte unchanged (GP 11) and pays nothing (GP 13); the enabled gate is a fail-closed security control.

## What changes

Enforces the [Phase 296](296-capability-effect-join.md) effect-join **at runtime**: a composed
component may only exercise capabilities at or below its declared
[Phase 282](282-companion-capability.md) `CompanionCapability` envelope — **default-deny** anything
beyond it. The security property that turns "we reviewed the AI-emitted app" into "the app physically
cannot touch a capability it didn't declare, by construction" (GP 4).

- **New types** (in `ToolUp.Platform.Server`, `Server/CompositionCapabilityGate.fs`):
  `ICompositionCapabilityGate` (`Check : ComponentId -> CompanionCapability -> CapabilityGateDecision`),
  `CapabilityGateDecision` (`Granted` | `Denied of CapabilityDenial`), and `CapabilityDenial` (the
  offending `ComponentId` + `Required` / `Declared` capabilities + a readable, component-named reason).

- **Dominance is the Phase 296 lattice order.** `CompositionCapabilityGate.permits declared required`
  is `CompanionCapability.join declared required = declared` — the requirement sits at or below the
  declared envelope on every axis. Effecting beyond a `Pure` declaration, a determinism factor the
  declaration didn't list, or dev-only beyond a distributed-ready declaration each push the join above
  `declared` and are denied.

- **Default-deny by construction.** The enabled gate (`create onDeny signature`) resolves a component's
  declared capability from a `CapabilitySignature`; an **undeclared** component is absent and resolves
  to `CompanionCapability.identity` ("pure"), so any effecting / hidden-read access it attempts is
  denied. Every deny is **observable** — handed to the `onDeny` observer (logged / audited) AND returned
  fail-closed — never silent.

- **Generalises [Phase 266](266-host-capability-registry.md).** `CompositionCapabilityGate.guardInvoke`
  enforces the composition effect-envelope (300) **in front of** the Phase 266 authorizer-gated
  registry (266): a capability invocation clears BOTH the declared envelope and the tenant/tier
  authorizer. A sandbox deny returns a `HostCapabilityOutcome.Denied` carrying the sandbox reason and
  the registry is never reached.

- **Opt-in, default off.** `CompositionCapabilityGate.disabled` is a grant-all passthrough — the
  default a deployment that never opts in uses (no signature consulted, nothing observed, byte-for-byte
  unchanged). A deployment opts in by composing `create` over its `CapabilitySignature` at the call
  sites where capability invocations occur.

## How to adopt (opt-in)

```fsharp
open ToolUp.Platform

// Declare each component's envelope (Phase 282/296), keyed by ComponentId:
let signature: CapabilitySignature =
    Map [
        ComponentId.forCompanionImpl "IAuditSink" "splunk-archive", CompanionCapability.distributedEffecting
    ]

// Compose the sandbox with an observer (log / audit every deny):
let gate =
    CompositionCapabilityGate.create
        (fun denial -> logger.Warn("capability sandbox deny: {Reason}", denial.Reason))
        signature

// Guard a capability invocation — envelope (300) then the Phase 266 authorizer (266):
let! outcome =
    CompositionCapabilityGate.guardInvoke
        gate owningComponentId requiredCapability
        hostCapabilityRegistry capabilityId args accessContext
// Off by default: pass CompositionCapabilityGate.disabled to keep the pre-300 posture.
```

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "CompositionCapabilityGate"
```

(Server-tier, .NET-only — no Fable leg.)

## Rollback

Delete `Server/CompositionCapabilityGate.fs` + its `<Compile>` entry, delete
`InProcess/CompositionCapabilityGateTests.fs` + its `<Compile>` and `Program.fs` registration. No
runtime impact — no deployment composes an enabled gate unless it opts in.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in, default-off runtime security control.
A deployment that composes no enabled gate is byte-for-byte unchanged (GP 11/13); adopters wire it at
their capability-invocation call sites.
