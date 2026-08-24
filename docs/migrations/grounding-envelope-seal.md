# Migration — the grounding envelope, sealed past boot

**Phase 684.** Additive and opt-in. A deployment that composes nothing new is byte-for-byte
unchanged (GP 11 / GP 13); there is nothing to migrate unless you want the property.

## What changes

The boot preflight seals the composition **at** boot and says plainly that it proves nothing
about what happens afterwards. For the grounding tier that gap is the live one: the declarations
a later answer's provenance is judged against are free to move the instant the verdict lands, and
nothing in the trail says they did.

This phase closes it the op-stream way rather than the freeze way. Grounding-relevant mutation
stays **possible**; it stops being **invisible**. Each becomes a typed, audited operation
carrying the before/after envelope digest, so

```
boot seal  +  recorded mutation chain  ⇒  live envelope
```

is a computation an auditor performs from the audit trail alone.

## The enumerated mutation surface

`GroundingFacet` is the enumeration, and it is a **closed union** so that adding a facet is a
compile error rather than a documentation task:

| Facet | Label | Covers |
|---|---|---|
| `MetricRegistrationFacet` | `metric-registration` | a registered grounding metric appearing or disappearing |
| `SubjectRegistrationFacet` | `subject-registration` | the same for a subject hierarchy |
| `PurposeDeclarationFacet` | `purpose-declaration` | a declared disclosure purpose + its taxonomy version |
| `CanonicalMethodFacet` | `canonical-method` | which method identity a method-less query resolves to |
| `DisclosurePolicyFacet` | `disclosure-policy` | the per-egress-surface allowed purpose sets |

**A sixth grounding-relevant declaration must join this union, `GroundingEnvelope.ofManifest` /
`ofComposition`, and this table — in the same change that introduces it.** A facet outside the
enumeration is one the digest silently does not cover, which is worse than one it visibly does
not, because the seal still verifies over the hole. `GroundingEnvelopeSealTests` asserts the
enumeration so the omission fails loudly.

## Adopting it

```fsharp
ServerApp.empty
|> ServerApp.withStorage blob
|> FactsCompose.withFactStore
|> FactsCompose.withDisclosurePurposes purposeConfig
|> FactsCompose.withGroundingEnvelopeSeal CompositionProfile.Verified None   // ← LAST
|> ServerApp.run
```

`withGroundingEnvelopeSeal` seals the envelope **as it stands at the call**, so place it last
among the grounding compose steps — a declaration composed after it is outside the seal. It
registers `IGroundingEnvelopeMutator`; resolve it wherever grounding declarations change and route
the change through `Apply`.

Two profiles, one implementation, and the adoption ladder is the preflight's own: under
`CompositionProfile.Standard` every finding is **recorded** on the mutation row as an observation
and the mutation lands; under `Verified` the same findings **refuse**. Move to `Verified` once you
have watched `Standard` report clean.

### Verification steps

1. `mutator.Continuity()` returns `Continuous(steps, digest)` on a clean deployment.
2. Route one mutation through `Apply`; a `GroundingEnvelopeMutated` row appears carrying
   `BeforeDigest` / `AfterDigest` / `Sequence`, and `Continuity()` still returns `Continuous`.
3. Change a declaration **without** the door; `Continuity()` returns `Diverged`, naming the
   position and what moved, and the next `Apply` under `Verified` returns
   `Error [ OutOfPathDrift … ]` with a `GroundingMutationRefused` row.

### Rollback

Drop the `withGroundingEnvelopeSeal` line. Nothing else reads the mutator, no stored state
depends on it, and the two audit event types are additive — existing rows and sinks are
untouched.

## What this does NOT prove

Stated at the same length as the guarantees, per the Phase 657 discipline, because a reader who
over-reads this is worse off than one who knows its bound.

- **The observation is only as live as the function supplied.** Continuity compares the chain
  against whatever `observe` returns. A deployment whose grounding declarations are compose-time
  immutable — **every composition this SDK ships** — passes `None`, and continuity is then
  continuous by construction. What that proves is that the deployment has nothing that could
  drift, *not* that a drift check caught nothing. Only a deployment holding mutable grounding
  state, passing `Some` a function that reads it, gets a check with something to catch.
- **It is a decision point, not a boundary.** Code that mutates grounding state without going
  through the door is not *stopped*; it is *detected* — on the next continuity check, and on the
  next mutation, which the door then refuses because it can no longer prove the chain. The
  capability gate carries the identical bound for the identical reason.
- **A recorded mutation is attributable, not correct.** The chain proves who moved what, in what
  order, from and to which envelope. Whether the new canonical method is the *right* one is not a
  question a digest can answer.
- **It says nothing about facets outside the enumeration** — which is why the enumeration is
  closed, and why the paragraph above about joining it is the load-bearing one on this page.
- **It inherits the boot seal's own bounds whole.** If the sealed composition was wrong, a
  perfectly continuous chain leads from a wrong start to a wrong present.
- **The chain is as durable as the audit sink composed under it.** In-process the chain lives on
  the mutator; a deployment wanting it to survive a restart composes a hash-chained ledger sink,
  and the rows reach it like any other audit event.
