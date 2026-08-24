# Migration — the composition manifest records canonical-method selectors

**Phase 694.** Additive, and the manifest schema is now **versioned**. A deployment that composes
nothing new changes no behaviour; a deployment that holds a **sealed composition binding** minted
before this phase gets one new, non-adverse verdict on its first boot after upgrade, and one act
closes it. Nothing refuses to start that did not refuse before.

## What changes, and why it needed changing

`CompositionManifest.metricEntry` recorded a registered grounding metric as its id with
`Impl = None`. The boot verification preflight compares manifests component by component, so two
boots either side of a **canonical-method flip** — a change to which method's lineage a
method-less query over that metric resolves to — compared **equal**, and the preflight reported
`verified`.

That is the one grounding mutation which changes what an already recorded number *means* without
changing anything else the manifest enumerates. Only the Phase 684 grounding envelope saw it, and
that envelope is a post-boot construct: a deployment could seal, flip, and re-boot with the
preflight affirming across the move.

The manifest now records the selector:

```fsharp
type MetricCanonicalMethod = { MetricId: string; Selector: string }

type CompositionManifest = {
    SchemaVersion: int              // NEW — 2 for anything this SDK projects
    …
    CanonicalMethods: MetricCanonicalMethod list   // NEW
}
```

**Why not the metric entry's unused `Impl` slot.** The comparison folds an absent `Impl` into the
entry's `Label`, so moving the selector there would change every already-sealed deployment's
recorded composition and report the *upgrade itself* as drift on all of them (GP 11). Phase 684
recorded that argument; this phase honours it with a versioned field instead.

## Upgrade semantics — the three states, and the one act

The version is what lets a reader tell **"records no selectors"** from **"cannot say"**. Both
render as an empty list; only the version distinguishes them.

| Sealed binding | Running composition | Verdict | Refuses under `RefuseOnDrift`? |
|---|---|---|---|
| pre-694 (schema absent) | any | `VerifiedUnrecorded` naming each metric | **no** |
| schema 2, same selectors | same | `Verified` | no |
| schema 2, different selector | flipped | `Drifted` naming the metric and both selectors | yes |

* **`BootVerificationVerdict.VerifiedUnrecorded`** is a new, **affirmative** case.
  `BootVerificationVerdict.isAffirmative` returns `true` for it — the transition boot must not
  cost a refuse-on-drift deployment an outage. It is deliberately **not** `Verified`, because the
  preflight did not check what it did not check; `isAffirmative` has a stricter twin,
  **`isFullyCompared`**, for a caller that needs the difference.
* **A grounding-free deployment sees nothing.** With no metric recorded on either side, no
  selector can exist to be silent about, so the verdict stays `Verified`.
* **The one act: re-seal the composition binding** from the running composition
  (`BootVerificationPreflight.bindingFor` → `sealBinding`, the same call your deploy tooling
  already makes). After that the verdict is `Verified` and stays there, and a later flip is drift.

**Already-minted seals keep verifying.** `compositionCanonicalForm` emits the canonical-method
block **only** for a manifest that records it. Appending even a zero length unconditionally would
change the canonical bytes of every manifest in existence, and an upgraded deployment's first
finding would be that its own genuine, untampered seal no longer verifies.

## Consumer-visible surface

Additive in every case; nothing is retyped or removed.

| Surface | Change |
|---|---|
| `CompositionManifest` | two new fields; `build` / `empty` / `withGrounding` / `withPurposes` unchanged in signature |
| `CompositionManifest.withCanonicalMethods` | new — records the selectors and stamps the schema |
| `CompositionManifest.canonicalMethodsOf` | new — **the** derivation from `Grounding.MetricRegistration list` |
| `CompositionManifest.recordsCanonicalMethods` / `effectiveSchemaVersion` / `canonicalMethods` | new readers |
| `CompositionDrift` | three new cases: `CanonicalMethodChanged` / `CanonicalMethodDeclared` / `CanonicalMethodWithdrawn` |
| `CompositionUnrecorded` | new union, one case: `CanonicalMethodUnrecorded` |
| `BootVerificationPreflight.unrecorded` | new — what an older binding could not record |
| `BootVerificationVerdict` | new case `VerifiedUnrecorded`; `label` gains `"verified-unrecorded"` |
| `BootSealIntegrity` | new case `BootSealVerifiedUnrecorded`; the report section reads `observed` (non-adverse) |
| `GroundingEnvelope.ofManifest` | now emits the canonical-method facet, read from the manifest |
| `GroundingEnvelope.withCanonicalMethods` | now **replaces** the facet rather than appending |
| `AnswerProvenanceAnchors.fromBootVerification` | names the composition seal only on a **fully compared** boot — a `VerifiedUnrecorded` boot carries no seal id on its answer rows until the binding is re-sealed |

**If you `match` exhaustively on `BootVerificationVerdict`, `CompositionDrift`, or
`BootSealIntegrity`, add the new cases.** The compiler names each site.

## One derivation, not two that agree

`GroundingEnvelope.ofManifest` now reads the selectors out of the manifest instead of a second
code path deriving them from the metric registry, and `ofComposition` feeds that same one
derivation. So for any app:

```fsharp
GroundingEnvelope.ofManifest (ServerApp.compositionManifest app)
  = GroundingEnvelope.ofComposition (ServerApp.compositionManifest app) app.RegisteredMetrics
```

by construction. Two derivations of one declaration can stop agreeing, and the boot seal and the
envelope would both go on verifying while they did — each internally consistent, jointly
describing a deployment that does not exist.

## Verification

```
dotnet run --project Build.fsproj -- VerifyAll
```

`BootVerificationPreflightTests` → "canonical-method visibility (Phase 694)" probes both
directions: a flip between two recorded boots is drift naming both selectors, **and** the
legacy→recorded transition boot is not drift, with the gap reported as unrecorded rather than
resolved as a match.

## Rollback

Revert the phase commit. The manifest loses its version and its selector list, the preflight
returns to comparing everything except the selector, and any binding sealed at schema 2
canonicalises differently under the reverted code — so **re-seal the binding after a rollback**,
exactly as after the upgrade.
