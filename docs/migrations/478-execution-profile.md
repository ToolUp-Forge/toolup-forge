# Migration — Phase 478: `ExecutionProfile` on `ExternalWorkSpec`

**Affects:** anything that constructs an `ExternalWorkSpec` **by record literal**, or that implements
`IExternalComputeDispatcher` and wants to accept `Isolated` work.

**Does not affect:** anything that builds a spec through `ExternalWorkSpec.create` (the documented
route), or that only holds / polls / cancels handles. Runtime behaviour of every existing path is
unchanged.

## What changed

`ExternalWorkSpec` gained one field:

```fsharp
type ExecutionProfile =
    | Standard    // the default — the seam exactly as it was
    | Isolated    // requires a backend that declares the isolation posture

type ExternalWorkSpec = {
    Kind: string
    Payload: string
    ResourceHints: Map<string, string>
    Timeout: TimeSpan option
    Idempotency: string option
    Profile: ExecutionProfile      // ← new
}
```

`ExternalWorkSpec.create` sets `Profile = ExecutionProfile.Standard`, so behaviour is unchanged
(GP 11). Two new builders join `withHint` / `withTimeout` / `withIdempotency`:
`ExternalWorkSpec.withProfile` and `ExternalWorkSpec.isolated`.

Two new declarations sit beside the dispatcher seam: the `IsolationPosture` record (three clauses +
an `Enforcement` label) and the `IIsolatedComputeBackend` interface a backend implements to declare
one. `IExternalComputeDispatcher` itself is **unchanged** — no member was added, removed, or
retyped, so every existing implementation still compiles.

## Do I need to do anything?

### 1. Record-literal construction — one field to add

The record's constructor widened, so a positional or record-literal construction site must name the
new field. This is the only source-breaking change in the phase.

```fsharp
// BEFORE
let spec = {
    Kind = "train-forecast"
    Payload = payload
    ResourceHints = Map.empty
    Timeout = None
    Idempotency = None
}

// AFTER — either name the field…
let spec = {
    Kind = "train-forecast"
    Payload = payload
    ResourceHints = Map.empty
    Timeout = None
    Idempotency = None
    Profile = ExecutionProfile.Standard
}

// …or, preferred, use the builder, which is unchanged and always will be:
let spec = ExternalWorkSpec.create "train-forecast" payload
```

Prefer the builder. It is the reason this migration is one field and not a rewrite, and it is what
keeps the next additive field off your call site entirely.

### 2. Dispatcher implementations — nothing required

An implementation that does not implement `IIsolatedComputeBackend` reads as
`IsolationPosture.standardOnly`: it keeps accepting every `Standard` submission exactly as before,
and is refused `Isolated` ones. That is the intended default — declaring nothing is read as
claiming nothing.

To accept `Isolated` work, add the second interface and assert all three clauses:

```fsharp
type MyBackendDispatcher(config) =
    interface IExternalComputeDispatcher with
        // …unchanged…

    interface IIsolatedComputeBackend with
        member _.IsolationPosture = IsolationPosture.clauses "deny-all network policy + ephemeral volume"
```

`IsolationPosture.clauses` asserts all three (`NoEgress`, `InputsRestrictedToDeclaredRefs`,
`EphemeralWorkspace`) with the named enforcement mechanism. Assert them only if the backend really
enforces them — a partial posture is refused rather than downgraded, and there is no half-isolated
mode.

### 3. Enforcing the refusal — opt in where it matters

Nothing is composed automatically; `ServerConfig.ExternalCompute` is untouched. Where a deployment
submits `Isolated` work, wrap the dispatcher so the refusal happens before the payload leaves the
process:

```fsharp
let dispatcher = ExecutionProfileGate.enforce myBackendDispatcher
```

`Poll` and `Cancel` pass through unchanged, `Backend` reports the inner backend's own label, and the
decorator re-declares the inner posture so stacking it cannot downgrade a genuinely isolating
backend.

## Verification

- `dotnet build` your consumer — a missed record literal is a compile error, never a silent default.
- If you added `IIsolatedComputeBackend`, assert `ExecutionProfileGate.postureOf dispatcher` honours
  `ExecutionProfile.Isolated`, and that an `Isolated` submission reaches the backend (count what the
  backend *saw*, not what the caller was told).

## Rollback

Drop the `Profile` field from your literals and remove any `IIsolatedComputeBackend` implementation;
nothing else in your code referenced the phase. The SDK side cannot be rolled back independently —
the field is on a shipped record — but pinning the previous `ToolUp.Platform.Core` version restores
the prior shape exactly.

## See also

- [`../platform/external-compute.md`](../platform/external-compute.md) — the seam, the isolation
  posture contract in full, and the gated-output pipeline.
