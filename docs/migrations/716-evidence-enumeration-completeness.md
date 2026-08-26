# Migration — the walk states what it enumerated

**Phase 716.** Additive in behaviour and opt-in in cost: nothing is composed, registered, mounted or
started by this phase, and a deployment that never walks an evidence chain is byte-for-byte
unchanged (GP 11 / GP 13). Two source-level edges do need a one-line change if you touch them, and
one exported artefact moves its content id. Both are below.

## What changes

Phase 713 reports each hop the walk visited. It does not report whether the walk carried everything
each hop's own linkage NAMED — and those are different claims. An ancestor walk that asks a source
for a parent record, is told the source holds none, and moves on renders as a clean short
enumeration, which reads exactly like a genuinely short history. Selective omission and a small
history are then indistinguishable, which is the failure the hop list itself exists to prevent one
level up.

Every walk now carries an **enumeration-completeness verdict**:

```fsharp
type EnumerationCompleteness =
    | Complete
    | Bounded of bounds: EnumerationBound list
    | Incomplete of missing: EnumerationPosition list * reason: string
```

reachable as `chain.Enumeration`, with `EnumerationCompleteness.label` (`"complete"` / `"bounded"` /
`"incomplete"`) as the stable wire label and `EnumerationCompleteness.describe` as the one-line
account a diagnostic, a test and an exported bundle all read the same way.

## Three states, not two

`Bounded` is the case worth understanding before anything else. A walk that stopped at a limit the
caller was **told about** — the work depth it asked for, the walker's declared closure cap, the
per-hop enumeration cap that states its own truncation in the render — is bounded, not incomplete. A
told limit is not an omission, and collapsing the two would teach a reader to treat every capped
walk as a finding, which ends with the finding ignored.

`Incomplete` is reserved for the case with no such excuse: the linkage named a position, no declared
bound accounts for it, and the render is silent. It names the missing positions rather than counting
them, so the finding is actionable from the one line. An omission outranks a bound that applied
elsewhere in the same walk — a cap somewhere else is not an excuse for a position nothing accounts
for.

A walk **refused** at a cap produces no chain at all, and its refusal maps to `Bounded` through
`EvidenceEnumeration.ofRefusal`. It is never `Incomplete`: a refused walk enumerated nothing and hid
nothing.

## The expected positions are derived, never configured

The positions come from the chain's own linkage — the parent refs a work record carries, the
attestation state of each dependency-closure entry, the index a ledger read reached — and the stage
tier comes from `EvidenceChain.order`. There is no configuration surface for them, deliberately: an
expectation that could be tuned until it matched whatever was produced measures nothing, and an
expectation read back out of the render is the same failure wearing a different hat. A render
carrying fewer hops therefore fails the check **whatever expectation it is handed**, including an
empty one.

## Two source-level edges

Neither is reachable by a deployment that composes the seam and calls it; both bite only if you
construct these values yourself.

- **`EvidenceChain` gained a field.** A hand-built chain needs `Enumeration = …` — pass
  `EnumerationCompleteness.Complete` for a fixture whose links are hand-written and have no linkage
  behind them to be incomplete about. This retypes the record's compiler-generated constructor,
  which is the one non-additive line in this phase's public-API baseline.
- **`IEvidenceChainWalker` is unchanged.** A composed walker needs no edit at all.

## The exported bundle states the verdict, and its content id moves

`EvidenceBundleExport.bundleOf` now attaches the chain's verdict as a `BundleClaimQualifier` under
the id `enumeration-completeness` — on **every** bundle, a complete one included, because a
qualifier that appeared only where the walk fell short would be a caveat nobody reads, and its
absence would be ambiguous between "this walk was complete" and "this producer does not measure
completeness".

Qualifiers render last in the bundle's canonical form, so the addition appends lines and moves
nothing before them — a reader diffing two canonical forms across the upgrade can tell a growth from
a re-statement. **The content id does move**, because the id covers the qualifiers. A holder pinning
the content id of a bundle exported before this phase re-exports and re-pins; the chain's own
`VerdictDigest` is untouched, because that digest names the LINK SET and this is a statement about
the enumeration behind the links.

`EvidenceBundleExport.bundleWith` is unchanged and still takes the qualifier list explicitly, for a
caller that wants to state something else or nothing.

## One new structural check in the offline verifier

`EvidenceBundle.verifyWith` (and therefore `verifyBundle` / `verifyDocument` / `verifyCommand`) now
reports `BrokenAt("bundle/qualifiers/enumeration-completeness", …)` when a document's stated verdict
disagrees with the chain it qualifies. A document that says two things about what its walk
enumerated says neither, so it is refused rather than read from whichever half a reader happened to
look at. Nothing else about the verifier's contract changes — it still establishes nothing about the
outer signature, and nothing about whether the records the chain carries are true.

## See also

- [`714-evidence-bundle-statement-export.md`](714-evidence-bundle-statement-export.md) — the bundle,
  its claim boundary, and the qualifier slot this phase fills.
