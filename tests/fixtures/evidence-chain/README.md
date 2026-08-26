# The evidence-chain break-injection corpus

One home for the fixtures behind the evidence-chain break corpus. The tests **read** these files;
they do not reconstruct equivalents inline, and there is no second copy anywhere in the repo.

| File | What it enumerates |
|---|---|
| `healthy-baseline.json` | The one synthetic chain in which every hop resolves, with its verdict digest and its bundle content id **pinned** — so an unintended change to either canonical form fails loudly rather than silently re-addressing the artefact. |
| `chain-break-cases.json` | One variant per break class at the walked-chain level: a severed reference, a digest mismatch at each hop, a ledger break at a named position, an absent substrate, a withheld record, and the substrates that are composed and will not answer. Each names the **verdict** and the **position**. |
| `bundle-tamper-cases.json` | One variant per tamper class at the bundle and document level: a re-ordered hop list, a dropped hop, a renumbered hop, an altered hop, a flattered outcome, a stripped claim boundary, an unrecognised nested-attestation ruling, an unreadable schema, an un-recomputed content id, a document re-signed over altered content, and a document whose inner statement verifies while its outer envelope does not. |
| `absent-vs-broken-pairs.json` | One dedicated pair per hop, so "nothing joins these two facts" and "the join is recorded and does not hold" are pinned as distinguishable at **every** hop rather than at the one that happened to be probed. |

## Reading a case

Every case carries the same five fields, whatever level it sits at:

* `id` — the injection the test applies. The test's injector table and this file are checked
  against each other in both directions, so a case with no injector and an injector no case names
  are both failures rather than silent gaps.
* `class` — the break class the case is a variant of.
* `verdict` / `position` — what the code under test must say, and **where**. A case that asserted
  only "not intact" would pass against a verifier that reported every document broken at one
  invented coordinate.
* `falsification` — how this case was demonstrated to fail against code lacking its check. Two
  methods are used, and each case names which:
  * `discriminating-twin` — the case's assertion is re-applied to the healthy baseline and must
    fail there, and the baseline's own assertion is re-applied to the injected fixture and must
    fail there. A check that could not fire fails one direction or the other.
  * `weakened-verifier` — a deliberately-weakened copy of the verifier, omitting exactly this
    check and nothing else, reports the document intact where the shipped verifier reports it
    broken. The weakened copy is itself checked against the shipped one with nothing omitted, so
    it is a faithful copy minus one check rather than a strawman.
* `unproven` — present and `true` only where a break class has **no verdict to assert**. Such a
  case is recorded as unproven, with what was observed instead; it is never quietly kept as
  though it had been proved.

## Identifiers

Every identifier in this corpus is synthetic and belongs to no deployment, tenant, key or system
that exists. The prefix `corpus-` and the `wc-` / `deploy-corpus-` / `pack-manifest-corpus`
families are reserved for it.

## Pinned digests

`healthy-baseline.json` and three `position` fields carry lowercase-hex SHA-256 values computed
over canonical forms declared in the shared type tier. They are pins, not inputs: if a canonical
form changes, these fail and name both values, which is the whole point of recording them. Regenerate
them deliberately — from the tree in which the canonical form changed — rather than by copying an
actual value out of a failure message without asking why it moved.
