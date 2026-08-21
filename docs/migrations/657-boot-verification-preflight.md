# 657 — boot verification preflight + the verified composition profile

**What changes:** a deployment can now ask, at startup, whether the composition it is actually
running is the one its sealed deploy record covers — and can opt into a profile under which a
drifted or unsealed answer refuses the start and the composition capability gate stops being
optional. All additive. `ServerConfig`, `ServerApp`, `DeployRecord`, `DeployManifest` and
`CompositionManifest` are **unchanged**; a deployment that calls none of this runs byte-for-byte as
it did (GP 11, GP 13).

| Type / module | Tier | Purpose |
|---|---|---|
| `CompositionBinding`, `SealedCompositionBinding` | `Platform.Server` | the composition as sealed, tied to its deploy record by that record's digest |
| `CompositionDrift` (+ `.describe`) | `Platform.Server` | one named finding per difference |
| `BootVerificationVerdict`, `BootVerificationPolicy`, `BootVerificationResult`, `BootVerificationOptions` | `Platform.Server` | the verdict, the policy, and what the preflight needs |
| `BootVerificationPreflight` (`bindingFor`, `sealBinding`, `compare`, `verify`, `run`, …) | `Platform.Server` | minting a binding, and the boot check |
| `CompositionProfile`, `CompositionProfileRefusal`, `VerifiedCompositionProfile` | `Platform.Server` | the profile, its refusals, the mandatory gate, the execution-profile enforcement |
| `ServerApp.verifyComposition` | `Platform.Server` | derive this app's manifest and run the preflight over it |
| `CompositionVerificationRecorded`, `CompositionCapabilityRefused` | `Platform.Core` | the two new audit events |

## The guarantee, stated precisely

The preflight answers **four questions and no others**, and accumulates every failure rather than
stopping at the first:

1. **Was anything supplied to verify against?** No sealed deploy record, or no sealed composition
   binding, is the `Unsealed` verdict — reported as its own outcome rather than folded into a
   failure, because "unsealed" and "sealed and wrong" call for opposite operator responses.
2. **Does the seal hold, and do the recorded artifacts match the files on disk?** Phase 656's
   `DeployRecords.verify`, reused unchanged.
3. **Does the binding belong to *this* record?** The binding carries the digest of the record's
   canonical bytes, so a binding minted for a different deploy — individually genuine, correctly
   sealed — cannot be presented alongside this one.
4. **Is the running composition the one the binding recorded?** A component-by-component,
   knob-by-knob comparison. A difference is reported by name (`composed but not recorded: companion
   'companion:iauditsink:shadow'`), never as a digest that failed to match.

**What it does not prove.** Written here at the same level of care as what it does, because a reader
who over-reads a verification is worse off than one who knows its bound:

- **Nothing about post-boot mutation.** The verdict describes the composition at the instant it was
  derived. A module hot-reloaded afterwards, a handler re-registered, a knob flipped through an admin
  surface — all of it is outside what a boot-time check can see, and **this profile does not freeze
  the composition to close that gap**. A deployment that needs "and it stayed that way" needs a
  freeze, and does not have one here.
- **Nothing about the truth of the recorded inputs.** A seal makes a statement attributable and
  tamper-evident; it does not make it correct. That is Phase 656's bound, inherited whole.
- **Nothing about code that was never composed.** The manifest enumerates composed units; a library
  linked into the process and registered with nothing is invisible to it.
- **The capability gate is a decision point, not a boundary.** It binds a component to the envelope
  its composition *declared*, at the call sites that consult it. Code that does not go through those
  call sites is not stopped by it. It is not a sandbox and this phase does not make it one.

## The policy default is log-and-serve

`BootVerificationPolicy.LogAndServe` is the default and it changes no behaviour: the verdict is
recorded through the audit seam and the process serves regardless. That is deliberate — a check
adopted as a refusal on day one is a check adopted by nobody. Watch the verdict for as long as it
takes to believe it, then move to `RefuseOnDrift`, which is adoptable on its own without the rest of
the profile.

The verdict is recorded on **every** outcome, including the affirmative one. A row written only when
something is wrong cannot distinguish "verified" from "the check never ran", and those are the two
states an operator most needs to tell apart.

## The verified composition profile

`CompositionProfile.Verified` makes three things true at once; the value is in the conjunction:

1. The preflight is refuse-on-drift, **whatever policy was passed**. A profile that could be
   configured back to serving on drift would not be a profile.
2. The capability gate is **mandatory**. A composition that declares no `CapabilitySignature` is
   refused (`CapabilityGateUndeclared`) rather than quietly granted everything — a mandatory gate
   with nothing to check against would present as enforcement while permitting everything, which is
   worse than no gate because it is believed.
3. External compute is submitted through Phase 478's `ExecutionProfileGate`, so an `Isolated` spec a
   backend does not declare the posture for is refused before the payload leaves the process. This
   profile is the isolated-execution profile's enforcement layer rather than a second switch to
   remember.

## Refusals reach whatever sinks you composed

Both events go out through `IAuditLog`, so every composed `IAuditSink` receives them and this
substrate depends on none of them. A deployment running a hash-chained audit ledger gets a
chain-covered boot verdict and chain-covered refusals for free; one running no sink at all still gets
the rows in its own audit trail. There is no package reference either way.

## Adopting it

```fsharp
open ToolUp.Platform

// At deploy time, beside the record you already seal (Phase 656):
let binding = BootVerificationPreflight.bindingFor deployRecord composedManifest
let! sealedBinding = BootVerificationPreflight.sealBinding sealer binding

// At startup, one line between building the app and running it:
let options = {
    Profile = CompositionProfile.Standard        // Verified once you trust the verdict
    Policy = BootVerificationPolicy.LogAndServe  // the no-behaviour-change default
    Sealer = sealer
    Locate = DeployRecords.locateUnder deployRoot
    Record = Some sealedRecord
    Binding = Some sealedBinding
    Transcript = Some transcript                 // None SKIPS that question, never answers it
    AuditLog = Some auditLog
    ScopeId = BootVerificationPreflight.PlatformScopeId
}

match! ServerApp.verifyComposition options app with
| Ok result -> // serve
| Error result -> // the policy refused the start; result.Verdict says why
```

The mandatory gate, with its refusals already on the audit path:

```fsharp
match VerifiedCompositionProfile.auditedGate auditLog scopeId profile (Some capabilitySignature) with
| Ok gate -> // thread it through your call sites, or use CompositionCapabilityGate.guardInvoke
| Error refusal -> failwith (CompositionProfileRefusal.describe refusal)

let dispatcher = VerifiedCompositionProfile.enforceExecutionProfile profile innerDispatcher
```

## Verification steps

1. `dotnet build ToolUp.Forge.sln`
2. `dotnet run --project Build.fsproj -- VerifyAll` — the `Platform` pack's
   `BootVerificationPreflight` list covers the honest deployment verifying, and a broken seal, an
   edited artifact, a swapped binding and a drifted composition each failing separately; the
   log-and-serve default serving a drifted composition; the profile overriding it; the mandatory
   gate's refusal and its audit row naming module and envelope; and the isolated-execution
   enforcement in both directions.

## Rollback

Stop calling `ServerApp.verifyComposition` / `VerifiedCompositionProfile.*`. Nothing else observes
them, no hosted service is registered, and no persisted state depends on them. A sealed composition
binding left in storage is inert to a deployment that stops reading it.
