# Migration — the seam-authority gate gains a call site

**What changes.** `ToolUp.Platform.Server` adds `SeamAuthorityEnforcement`, the first place in the
SDK that calls `ISeamAuthorityGate.CheckSeam` on a real composition. It answers one question — *for
each composed module, which substrate seams does it reach, and is it permitted to?* — and returns
the refusals.

New public surface in `ToolUp.Platform.Server`, all additive:

| Symbol | Shape |
|---|---|
| `ComponentSeamReach` | record — `ReachComponent: ComponentId`, `ReachedSeams: SeamId list` |
| `SeamAuthorityRefusal` | DU — `Profile of CompositionProfileRefusal` \| `Reaches of CapabilityDenial list` |
| `SeamAuthorityEnforcement.reachOf` | `ServerModule -> ComponentSeamReach` |
| `SeamAuthorityEnforcement.reach` | `ServerModule list -> ComponentSeamReach list` |
| `SeamAuthorityEnforcement.verify` | `ISeamAuthorityGate -> ServerModule list -> Result<unit, CapabilityDenial list>` |
| `SeamAuthorityEnforcement.verifyAudited` | `IAuditLog -> string -> CompositionProfile -> CapabilitySignature option -> SeamGrantSignature option -> ServerModule list -> Result<unit, SeamAuthorityRefusal>` |
| `SeamAuthorityEnforcement.describeRefusal` | `SeamAuthorityRefusal -> string` |

## Do I need to change anything?

**No.** Nothing existing changed. No record was retyped, no interface gained a member, no default
moved. `ServerConfig`, `ServerApp`, `ServerModule` and `BootVerificationOptions` are byte-for-byte
what they were. A deployment that never calls `verify` derives no reach and pays nothing (GP 13);
a deployment that calls it with no declared `SeamGrantSignature` gets `Ok ()` for every module
shape (GP 11).

This is deliberately a **function** rather than a `ServerConfig` knob or a `ServerApp` field — the
same rationale `ServerApp.verifyComposition` and `BootVerificationOptions` already record: widening
a record every consumer builds, for a feature no existing consumer configures, costs every consumer
a recompile to buy nothing. The modules a composition adds are the modules you already hold, so
there is nothing to thread anywhere.

## Opting in

```fsharp
let modules = [ salesModule; reportsModule; ingestionModule ]

// 1. What does each module actually reach? Start here — the answer is derived
//    from the modules' own registrations, so it is already correct.
for entry in SeamAuthorityEnforcement.reach modules do
    printfn "%s -> %s"
        (ComponentId.value entry.ReachComponent)
        (entry.ReachedSeams |> List.map SeamId.value |> String.concat ", ")

// 2. Declare that as each component's grant, and bind it to the profile.
let grants: SeamGrantSignature =
    SeamAuthorityEnforcement.reach modules
    |> List.map (fun e -> e.ReachComponent, SeamGrant.ofSeams e.ReachedSeams)
    |> Map.ofList

match
    SeamAuthorityEnforcement.verifyAudited
        auditLog
        scopeId
        CompositionProfile.Verified
        (Some capabilitySignature)
        (Some grants)
        modules
with
| Ok() -> ()
| Error refusal -> failwith (SeamAuthorityEnforcement.describeRefusal refusal)
```

Step 1 is the migration: read your own reach, declare it, then tighten it by hand where a module
reaches substrate you would rather it did not. A grant generated from the reach admits by
construction — it is a starting point that cannot break you, not a security posture on its own.

Under `CompositionProfile.Standard` with nothing declared this is unconditionally `Ok ()`. Under
`CompositionProfile.Verified` an absent `CapabilitySignature` is `Profile CapabilityGateUndeclared`
and an absent (or half-declared) `SeamGrantSignature` is `Profile (SeamGrantsUndeclared …)`, both
refused *before* any reach is checked — a mandatory check with nothing to check against would admit
everything while presenting as enforcement.

## Where the reach comes from

It is **not a new map**. Phase 438/554's `ModuleSurface` already computes the substrate a module's
registrations imply, keyed off the registration fields themselves — a module declaring `AITools`
needs an `IAIProvider`, one declaring `JobHandlers` needs an `IJobScheduler`, one declaring nothing
needs nothing. `SeamAuthorityEnforcement.reachOf` reads that projection's `Needs` and keeps its
`substrate` entries, whose `Key` is a companion interface name and therefore already a `SeamId`.
So the SDK carries one declaration-to-substrate map rather than two that can disagree, and a new
registration field that implies new substrate extends the enforcement by itself.

## What this does NOT claim

A refusal is sound — every seam named is genuinely reached by a declaration. **An admission is a
subset claim, not a proof of confinement.** `ModuleSurface` reports its own blind spots rather than
guessing at them: a Giraffe `HttpHandler` is a closure whose routes are not enumerable, and no
`ServerModule` field declares the `(TargetModule, QueryKey)` pairs a module *asks* for. A module can
still resolve substrate from the `IServiceProvider` by hand, and nothing here observes that.

Read the guarantee as "declared reach is now enforced", not "reach is now confined". An enforcement
layer that overstated its coverage would be worse than one that does not exist, because it would be
believed.

## Known gap — the client-host bridge is not covered

Phase 688 recorded that `ClientHostBridge`'s `ClientHostInvoke.create` calls the
`IHostCapabilityRegistry` directly, bypassing both gates. That is still true, and it is **not
fixable additively**: `ClientHostBridge` is in `ToolUp.Platform.Client` (Fable-compiled) while
`ICompositionCapabilityGate` / `ISeamAuthorityGate` are in `ToolUp.Platform.Server`, which the
client tier does not and must not reference. The gate file has no server-only dependency — every
type it names lives in `Platform.Core` — so moving it down a tier would make it reachable, but that
removes it from `ToolUp.Platform.Server`'s public surface, which is a break rather than an addition.

`SeamAuthorityGate.guardSeamInvoke` remains the shipped choke point for a server-side registry
invoke. Routing the client bridge through a gate needs the tier move, and that is a deliberate
breaking-change decision for a future major, not something to do quietly.

## See also

- `src/ToolUp.Platform.Server/Server/SeamAuthorityEnforcement.fs` — the call site.
- `src/ToolUp.Platform.Server/Server/CompositionCapabilityGate.fs` — the Phase 300 gate and the
  Phase 688 seam gate it inherits.
- `src/ToolUp.Platform.Server/Server/BootVerificationPreflight.fs` —
  `VerifiedCompositionProfile.auditedSeamGate`, which builds the gate `verifyAudited` uses.
- `src/ToolUp.Platform.Server/Server/ModuleSurface.fs` — the `Needs` projection the reach is
  derived from.
- `src/ToolUp.Platform.Tests/InProcess/SeamAuthorityEnforcementTests.fs` — the additive floor over
  real compositions, and one perturbation per reached seam.
