# Phase 688 — seam-granularity authority grants

**What changes:** a component can now declare *which seams it reaches* (`IEntityStore`,
`IAuditSink`, …), and the composition capability gate holds it to that. Before this, module
authority was **effect-lattice-granular**: a component allowed to do effecting work at all could
resolve every seam the composition would hand it, so reviewing what a module *can reach* meant
reading its code.

**Nothing changes for a deployment that declares nothing.** Declaration is optional outside the
verified composition profile, and an absent declaration resolves to `UnrestrictedSeams` — every seam
resolves, exactly as before (GP 11). Adopt it per component, at your own pace.

---

## 1. Declare a component's reachable seams

A grant is a value keyed by the same `ComponentId` the Phase 296 `CapabilitySignature` uses:

```fsharp
open ToolUp.Platform

let seamGrants: SeamGrantSignature =
    Map.ofList [
        ComponentId.ofModule "reports", SeamGrant.ofInterfaces [ "IEntityStore"; "IAuditSink" ]
        ComponentId.forCompanionSlot "IJobScheduler", SeamGrant.ofInterfaces [ "IJobStore" ]
    ]
```

`SeamGrant.ofSeams []` is a **real declaration meaning "reaches no seam at all"**, not a synonym for
unrestricted — the two are opposite ends of the order and are never folded together. To derive a
component's set: list the SDK interfaces its `create`/registration actually resolves, then run with
the gate composed and read the refusals, which name every seam and the set that was declared.

**Why the grant is a sibling of `CompanionCapability` rather than a fourth field on it.** Each of
the three capability axes has its *identity* at the bottom (pure / deterministic /
distributed-ready), which is what lets an undeclared companion contribute `identity` and change no
join. A seam set's behaviour-preserving value is the **top** of its order — an undeclared component
must keep reaching everything. Folding an inverted axis into `CompanionCapability.join` would stop
`identity` being the join identity and break the Phase 296 laws the manifest, the preflight and the
Phase 434 scale report all rest on.

## 2. Compose the gate

```fsharp
let gate =
    match
        VerifiedCompositionProfile.auditedSeamGate
            auditLog
            BootVerificationPreflight.PlatformScopeId
            profile              // CompositionProfile.Standard | .Verified
            (Some capabilities)  // the Phase 296 CapabilitySignature
            (Some seamGrants)    // the Phase 688 SeamGrantSignature
    with
    | Ok g -> g
    | Error refusal -> failwith (CompositionProfileRefusal.describe refusal)
```

`ISeamAuthorityGate` **inherits** `ICompositionCapabilityGate`, so it drops into every hole the
Phase 300 interface fits and no existing implementation or consumer changes. Passing `None` for the
grants returns a gate whose decisions are identical to `VerifiedCompositionProfile.resolveGate`'s.

## 3. Resolve seams through the choke point

```fsharp
match SeamAuthorityGate.resolveSeam gate ownerId (SeamId.ofInterface "IEntityStore") required (fun () ->
          services.GetRequiredService<IEntityStore>())
      with
| Ok store   -> useIt store
| Error denial -> log denial.Reason   // already observed + audited; fail closed
```

The factory runs **only** on a grant — there is no path through `resolveSeam` that yields a value
without one. For the Phase 266 host-capability path use `SeamAuthorityGate.guardSeamInvoke`, which
clears three gates in order: the declared effect envelope (300), the declared seam set (688), then
the registry's own default-deny authorizer (266).

A refusal is a `CapabilityDenial` — deliberately the **same** record a Phase 300 effect refusal
carries — so `VerifiedCompositionProfile.auditingObserver` already turns it into an
`AuditEvent.CompositionCapabilityRefused` and the Phase 658 hash-chained ledger already carries it.
There is no second audit event to wire and no second observer to remember. The seam is named in
`Reason`, which is the field the audit payload renders.

The Phase 300 effect check runs **first**, so a component that fails both axes is reported against
the axis it was already failing, with its existing reason — an effect refusal is never relabelled as
a seam refusal.

## 4. Make the grant set a reviewable CI diff

```fsharp
let surface = SeamAuthoritySurface.ofSignature seamGrants
let rendered = JsonSerializer.Serialize(SeamAuthoritySurface.toWire surface, jsonOptions)
// …compare against your committed golden file:
let delta = SeamAuthoritySurface.diff (SeamAuthoritySurface.ofWire baseline) surface
if not (SeamAuthoritySurface.isEmptyDelta delta) then
    failwith (SeamAuthoritySurface.renderDelta delta)
```

This is the **outbound** half of the Phase 438 authorization-surface manifest — a sibling projection
rather than fields grown onto `AuthorizationSurface`, following that file's own rule that growing a
shipped F# record breaks its constructor; the two join on `ComponentId`. A **widened** grant set is
`CriticalAuthorizationDrift`, the outbound twin of a weakened requirement, and so is a newly-declared
component that declares itself unrestricted. Two sets that are neither a subset nor a superset of one
another count as widened, deliberately: a swapped seam is not provably at most what it replaced.

forge ships no golden file of its own here. A `SeamGrantSignature` is composition-call-site data with
no `ServerApp` field to read it from — exactly like Phase 434's `ScaleDeclarations` — so forge's
reference composition declares no grants and could not until a later phase gives it somewhere to.
A baseline over an input nothing can reach is a gate that cannot fail, which reads as coverage
without being any.

## 5. Under the verified composition profile it is mandatory

Phase 657's `CompositionProfile.Verified` already refuses a composition that declares no
`CapabilitySignature`. It now also refuses one that declares no seam grants
(`SeamGrantsUndeclared []`), and one where a component **in the capability signature** declared an
effect envelope but no seam set (`SeamGrantsUndeclared [ ids… ]`, naming each). Same reason in both
cases: a mandatory check with nothing declared would permit everything while presenting as
enforcement, which is worse than no check because it is believed.

`CompositionProfile.requiresSeamGrants` reports the demand without you having to know that it
happens to move with `requiresCapabilityGate` today.

## Verification

1. `dotnet build ToolUp.Forge.sln`
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter ToolUp.Platform.Tests.SeamAuthority`
   (34 cases; note Expecto joins the path with `.`, and a filter matching nothing exits 0)
3. With the gate composed, exercise one seam a component does **not** declare and confirm the
   refusal names the component, the seam, and the declared set — and that a
   `CompositionCapabilityRefused` row lands on your audit log.

## What this does not prove

The gate is a **decision point, not a boundary** — Phase 657's bound, inherited whole. A component
that obtains a seam by a path that does not go through `resolveSeam` / `guardSeamInvoke` is not
stopped by it. What the declaration buys is that the seams a component reaches *through the composed
call sites* are reviewable as a diff instead of recoverable only from its source.

## Rollback

Pass `None` for the grants (or drop the `SeamGrantSignature` entirely) and the gate's decisions
revert to Phase 300's exactly; remove the `SeamAuthorityGate.resolveSeam` call sites to return to
direct resolution. Nothing else in a composition depends on the declaration.
