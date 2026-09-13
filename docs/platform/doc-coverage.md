# Public XML-doc coverage — the policy and the ratchet (Phase 261)

The 1.0 surface should be the *documented* surface. An adopter meets this SDK through hover-docs and
the generated reference, not by opening `ToolUp.Platform.Core/Shared/ServerConfig.fs` — and until
Phase 261 they met it through neither, because the packages shipped no XML documentation file at
all. Every `///` comment in this repo was visible in the source and invisible the moment the package
crossed a project boundary.

This document is the policy: what is measured, over what, what the gate does, what today's numbers
are, and how they are meant to move before the 1.0 tag.

## What is measured

**The documented fraction of the tracked public surface, per assembly.**

- **The denominator** is the set of documentable subjects of the surface Phase 175
  tracks: every type and every public/protected member the api-baseline renderer emits, for every
  packable `ToolUp.*` assembly. Not the source tree, not the compiler's view of "publicly visible",
  and not a list anyone maintains. It is produced by the *same walk* that produces the
  `.approved.txt` files, so it cannot disagree with them.
- **The numerator** is how many of those subjects the compiler's XML documentation file keys — i.e.
  carry a `///` doc comment that reached the emitted artefact.

**Internal and vendored code is out of scope by construction, not by exclusion list.** The gate never
sees a `module internal`, a `private` member, or an assembly's plumbing, because the api-baseline
renderer never sees them. This is the point of scoping to Phase 175's surface rather than to a
compiler warning: a gate that measured everything the compiler calls public would demand doc comments
on infrastructure no consumer can call, authors would satisfy it with placeholder prose, and the
measurement would stop meaning anything. What the SDK **contracts** is what it must **document**.

By the same construction, Phase 256 — the public-API surface-minimisation
sweep — improves this number for free: a member that stops being public leaves the denominator.

## Where it lives

`src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs`, beside the two gates it shares a walk
with: the Phase 618 surface-drift comparison and the
[Phase 258 deprecation-message policy](deprecation-policy.md). One `MetadataLoadContext` load per
assembly answers all three.

It therefore runs wherever the Public-API approval pack runs: `dotnet run --project Build.fsproj --
VerifyAll` locally, and the `verify-all` job in CI. **There is no separate CI job and no separate
command** — deliberately. A coverage gate in its own workflow would need its own build of the
solution (the surface is rendered from built DLLs), which is the most expensive thing in the pipeline
already, and it would be a second place for the definition of "the public surface" to live.

## The gate

`api-baselines/doc-coverage.approved.txt` records one line per assembly:

```
ToolUp.Platform.Core 4914/12702
```

The graded property is:

> **`total - documented` must not increase.**

That single number is what covers both regressions worth catching:

- a **new public member lands without a doc comment** — the undocumented count rises;
- an **existing doc comment is deleted**, or the member it documents is made non-public — the
  undocumented count rises, even though the fraction over a 12,000-member denominator would barely
  move and the surface itself did not change at all.

Because the denominator may grow, an undocumented count that does not rise also means the documented
*fraction* did not fall. So the "threshold" is per-assembly and is exactly today's measurement — no
number to tune, no global percentage that one assembly's improvement can use to hide another's
regression.

**Removing a documented member does not fire it.** Total and documented fall together, the
undocumented count is unchanged, and the removal is reported by the surface comparer next door. One
change, one finding.

### Improving coverage never fails

This is the one place the gate departs from its two neighbours — Phase 618
fails on additive surface growth until the baseline is folded, and Phase 259's conformance registry
fails when a listed shortfall has been fixed and not removed. Both of those record a **set of names**,
where an entry that no longer matches the tree is a false statement, and folding it is the only way
to keep the file honest.

This records a **floor**. A floor that is beaten is not stale; it is doing its job. Failing a run
because someone documented three members would tax precisely the act the gate exists to encourage,
and the tax would be paid by every unrelated PR that happened to touch a documented file. So
improvements land silently and are locked in deliberately — see the ratchet step below.

### Accepting a new level

Same switch as the api-baselines beside it, scoped the same way:

```powershell
$env:TOOLUP_APPROVE_API = "ToolUp.Platform.Core"   # or a comma-separated list, or "1" for all
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_API = $null
```

Then commit `api-baselines/doc-coverage.approved.txt` in the same PR. A scoped regeneration rewrites
only its own assemblies' lines and leaves every other line byte-identical, so it is safe in a shared
tree in a way an unscoped one is not.

### The two preconditions

Both are reported **once** for the whole tree rather than per assembly, for the reason
Phase 731 established: a single environmental fact answered with 165
assertion failures reads as a catastrophe and misdirects whoever triages it.

| Precondition | Message | Meaning |
|---|---|---|
| the solution is built | `SOLUTION NOT BUILT — …` | the surface is rendered from DLLs; nothing has drifted |
| documentation files are emitted | `NO XML DOCUMENTATION FILES — …` | `GenerateDocumentationFile` is not in effect; coverage cannot be measured, and reads as 0% if it is |

The second is scoped to assemblies that have a tracked surface at all. `ToolUp.Hosts.Docker` is a
published package with no `.fs` and no shipped DLL — its payload is a set of `contentFiles/`
templates — so it sets `GenerateDocumentationFile=false` correctly and its api-baseline is a header
with no members. Requiring a sidecar there would be requiring documentation of the empty set. The
exemption is derived from the committed baseline rather than from the package's name, so the day it
grows public code the precondition starts applying to it with no edit to the gate.

## The census — measured 2026-09-12, on the untouched tree

**20,145 of 49,285 documentable public subjects across 164 packable assemblies: 40.9%.**

That is the floor the gate was armed at. It is deliberately not a target.

| Coverage band | Assemblies |
|---|---|
| 0% | 3 |
| under 25% | 16 |
| 25–50% | 61 |
| 50–75% | 70 |
| 75%+ | 14 |

The assemblies that dominate the shortfall are the three core tiers, simply because they are most of
the surface:

| Assembly | Documented | Coverage |
|---|---|---|
| `ToolUp.Platform.Core` | 4914 / 12702 | 38.7% |
| `ToolUp.Platform.Server` | 5172 / 10609 | 48.8% |
| `ToolUp.Platform.Client` | 2037 / 6101 | 33.4% |
| `InterPlatform` | 1237 / 2422 | 51.1% |
| `ToolUp.PublicRendering` | 581 / 1062 | 54.7% |
| `Feliz.AgGrid` | 63 / 686 | 9.2% |
| `Feliz.AgGrid.Enterprise` | 45 / 667 | 6.7% |
| `Feliz.AgCharts` | 40 / 333 | 12.0% |

Two readings are worth writing down before anyone acts on this table.

**The three Feliz binding packages are the lowest and are also the least alarming.** They are
mechanical bindings over `ag-grid` / `ag-charts`, where the authoritative documentation is upstream's
and a per-prop doc comment would mostly restate a name. They are also the clearest candidates for
Phase 256: much of that surface is erased-type plumbing rather than a contract.

**The three 0% assemblies with a non-zero surface are one or two members each** (`ToolUp.Hosts.AwsLambda`,
`ToolUp.Hosts.AzureFunctions`, `ToolUp.AIProviders.Claude.Client`); `ToolUp.Cloud.Aws`, `ToolUp.Cloud.Azure`,
`ToolUp.Cloud.Gcp` and `ToolUp.Sdk` measure 0/0 because they are metapackages with no surface at all.
Neither group is a documentation problem.

## The ratchet plan

The floor holds the line; it does not climb on its own. Three steps, in the order they pay off:

1. **Now — the line holds.** Every new public member arrives documented, because an undocumented one
   fails the gate in the PR that adds it. This is the whole of the ongoing obligation, it is paid by
   the author who knows what the member does, and it costs a sentence.

2. **Before the 1.0 tag — raise the floor deliberately, by tier.** The order that buys the most is
   the order of *reach*, not of size: `ToolUp.Platform.Core` first (every consumer of every package
   depends on it and its types cross every wire), then `Server`, then `Client`. Each pass is a phase
   of its own with its own regeneration; none of them belongs inside an unrelated change.
   Phase 256's surface minimisation should run *before* the large passes rather
   than after — a member that should never have been public is cheaper to internalise than to
   document.

3. **At 1.0 — decide whether the floor becomes a target.** The natural end state is 100% on the core
   tiers with the bindings exempted by a smaller surface rather than by a rule. Whether the gate then
   changes from "never worse" to "must be complete" is a decision for that release, not a promise
   made here: a hard 100% is only honest once the surface has stopped moving, and a rule adopted
   before then is one that gets suspended.

## What this does NOT check

Stated so the gate is not read as stronger than it is.

- **Quality.** `/// Gets the name.` on a property called `Name` satisfies it. No automated check can
  do better, and a length or word-count heuristic would only teach people to pad.
- **Accuracy.** A doc comment that describes what the member did two versions ago passes.
- **`<param>` / `<returns>` completeness.** Presence of an entry for the member is the whole test.
- **Anything outside the tracked public surface** — by design; see the first section.
- **Fable consumers.** The client tiers ship *source* under `fable/` in the nupkg, so a Fable
  consumer's tooling reads the `///` comments directly and never consults the sidecar. The
  measurement is the same either way; the delivery mechanism is not.

## See also

- [`deprecation-policy.md`](deprecation-policy.md) — the sibling gate on the same walk (Phase 258).
- [`docs/migrations/261-public-xml-doc-coverage-gate.md`](../migrations/261-public-xml-doc-coverage-gate.md)
  — what changed, and what a consumer has to do about it (nothing).
- `src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs` — the implementation, including the
  doc-comment-id computation and why each design choice was made.
