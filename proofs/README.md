<!--
SPDX-License-Identifier: Apache-2.0
Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
-->

# `proofs/` — the machine-checked theorems

This directory holds two theorems and the machinery that keeps them honest. Each has its own
claims ladder below, because the two say different things and a reader should not have to work
out which rung a sentence about one belongs to by reading the other.

**The decoder totality theorem (Phase 787).** Given a parsed `Value`, no combinator in
`ToolUp.Remoting.Decode` and no decoder built from them can diverge, throw, or reach a state that
is neither an accept nor a named refusal — and *which* of the two is characterised structurally, so
the failure classification is exhaustive.

**The disclosure noninterference theorem (Phase 790).** For any ranking, any total verdict
function and any policy assignment, the population disclosure fold — the one both population doors
run — discloses no fact whose verdict was not-disclosable, reports the withheld members as a count
grouped by policy that depends on the verdicts alone, keeps every disclosed fact's true rank,
suppresses the magnitude block exactly when anything was withheld, and produces *identical* output
for two rankings that agree on their disclosable facts, whatever the withheld facts' values. Its
ladder is [further down](#the-claims-ladder--the-disclosure-fold-phase-790).

**The machinery**, because a theorem about a model is worth what the tie to the code is worth:

| File | What it is |
|---|---|
| `RemotingDecode.fst` | the decoder model — `Decode.fs` clause for clause, each definition naming its F# counterpart, every refusal message reproduced verbatim |
| `DisclosureFold.fst` | the disclosure model — `DisclosureEgress.evaluate` and `PopulationDisclosure.fold` / `valuesWithheld` / `disclosedStats` clause for clause, each definition naming its F# counterpart |
| `fstar-pin.json` | the pinned prover (an F\* release, which bundles Z3), with its hash |
| `check.ps1` | the whole proof leg, over a module list: resolve the pin, then per module check, extract and byte-diff; build the oracle project; run each module's differential host |
| `oracle/RemotingDecode.fs` | **generated** — the decoder model extracted to F#, committed so the repository never needs a prover to build |
| `oracle/DisclosureFold.fs` | **generated** — the disclosure model extracted to F#, committed for the same reason |
| `oracle/Prims.fs` | the nine-name runtime the extractions need, because F\*'s F# backend ships none; the second model references a subset of the same nine |
| [`../proofs.json`](../proofs.json) | both ladders below, declared as **data** — hand-authored, never generated, so a registry can read what a human decided rather than parse this prose |

```powershell
pwsh ./proofs/check.ps1            # the whole leg, once
pwsh ./proofs/check.ps1 -Runs 3    # three cold checks with --quake 3, what CI runs
pwsh ./proofs/check.ps1 -SkipHost  # the proof half only
```

Nothing else in the repository depends on any of it. `dotnet build`, `VerifyAll` and every other CI
job compile the *committed* extractions like ordinary source; the prover is a ~200 MB download this
one script fetches on demand into a gitignored directory. That is deliberate: a contributor with no
interest in proofs should never install one.

---

## The claims ladder — the decoder (Phase 787)

Four rungs. The distinction between them is the point of writing them down: a reader who takes
everything here as rung 1 has been misled, and the way to prevent that is to say which rung each
claim sits on rather than to claim less.

The same four rungs are declared as data in [`../proofs.json`](../proofs.json) — `proved` /
`tested` / `assumed` / `policy`, one entry per claim, each `proved` entry naming its lemma and the
prover pin so an evidence check can resolve it in the module. That file is hand-authored and never
generated: what it records is which rung a human put a claim on, and a generator could only restate
what the code already says. Every later proof phase in this repository appends to it rather than
starting a second manifest. Prose and manifest are kept in step by hand; when they disagree, the
manifest is the one a machine reads and this page is the one that explains why.

### Rung 1 — Proved

**The words "formally verified" are spent in this file's two Rung-1 sections and nowhere else in
this repository; here, they are spent on the combinator layer alone.** What a machine has checked,
on the pinned prover, with `--report_assumes error` so that an `assume` or an `admit` would fail the
leg rather than quietly weaken the result:

* **Totality, for every decoder at once.** `decode_total` quantifies over *all* decoders, not one
  lemma per combinator — including any a consumer or the source generator builds out of them —
  because it rests on two facts nothing built from the algebra can escape: `decoder` is a total
  arrow, so the checker has already rejected anything partial or non-terminating, and the outcome
  type has exactly two cases, so there is no third state to reach. This is why the phase models the
  algebra rather than auditing it: the property is compositional, so a proof about the combinators
  is a proof about everything built from them.
* **Structural characterisation.** For `as_bool`, `as_string`, `as_unit`, the integer worker,
  `as_float32`, `field` and `union`: *which* outcome, for which input, with the exact refusal —
  expected text, found text and path. This is the half that could have been false.
* **Termination, by the value model's own measure.** `element_at` — the accessor `field` and `index`
  are both built from — carries `size (result) < size (input)` in its return type. A decoder that
  descends through it cannot recur forever, and not because of a depth counter.
* **Phase 786's information rule, as five lemmas.** A negative value never decodes into an unsigned
  target; a negative value fits a signed target exactly when it clears that target's minimum; a
  source no wider than its target always survives; a source wider than its target must fit its
  range; and the rule *decides* for every width class, so the integer arms' classification is
  exhaustive too.
* **What an accept licenses.** On a reader-producible value, an `as_int32` accept implies the
  payload carries no more than **thirty-two bits of information** — the union of the signed and
  unsigned 32-bit ranges. It deliberately does **not** imply the payload sits in `Int32`'s signed
  range, because `writeDecimal`'s negative words arrive as `uint32 0xFFFFFFFF` and the host's cast
  reinterprets them. That union is exactly the set on which the cast is a bijection, which is
  exactly the licence the cast needs.
* **The round trip, over a reference API-record vocabulary.** For a nested-record shape with a
  string, a bounded integer and a flag: the encoding of any value decodes, and decodes back to the
  value it started from. Not "decodes to something" — the decoder can never refuse traffic its own
  encoder produced.

### Rung 2 — Differentially tested

Not proved. *Measured*, on every run of the gate, over the Phase 784 wire corpus.

* **The model agrees with production.** `ProofOracleTests` decodes every covered corpus case, and
  every refuse-path mutation, through the extracted model *and* through the shipped algebra, and
  requires them to agree on **outcome class, decoded value, decoded runtime type and refusal message
  at once**. The messages are compared because they are the cheapest place drift shows.
* **The committed extraction is what the prover produced.** `check.ps1` re-extracts and byte-diffs
  against `oracle/RemotingDecode.fs`. Without this step the committed file would be a claim; with
  it, it is a checked one.
* **The differential is known to be able to fail.** A committed go-red case hands the model a bridge
  that has forgotten the source width class — every integer declared 64-bit — and asserts that the
  comparison *catches* it. It catches four cases, and every one is the width rule losing: the sbyte
  minimum (`-128y`, on the wire as `uint8 128`), two decimal words that arrive unsigned, and the
  compacted `int64` narrowing. A differential that has never been shown to fail agrees with whatever
  it is shown.

**Why this rung exists at all.** A hand-written model can drift from the code it describes silently
and for months, and no amount of proving fixes a model of the wrong thing. Rung 1 says the model has
the property. Rung 2 is the only thing that says the model is about the shipped code.

### Rung 3 — Assumed, and stated

Load-bearing and *not* checked by anything here. Each is a place where a defect would produce a
green leg over a false claim.

* **The F\* extractor and the F# compiler are trusted.** The theorem is about the model; the code
  that runs is the extraction. Nothing verifies that the extractor preserves semantics.
* **The bridge is hand-written.** `ProofOracleTests`' `bridge` maps a `Value` to the model's value,
  and a defect in it would make the differential compare the wrong thing. It is short and
  case-for-case on purpose, and the go-red bridge beside it exercises the one field most likely to
  be got wrong.
* **`Prims.fs` is a hand-written shim.** Nine names. It is small enough to read in a minute, which
  is the mitigation; the byte-diff means a model change reaching for a tenth name fails the leg
  rather than compiling against a widened shim.
* **Three payload facts come from the host, not the model**: a string's length, a `bin`'s length,
  and a float's rendered text. Recomputing any of them in F\* would introduce a second
  implementation free to disagree with the host's — and for string length it demonstrably would, as
  F\* counts characters and .NET counts UTF-16 code units. The model carries them as data instead.
* **Well-formedness is a predicate, not a type.** The value model admits an integer whose payload
  exceeds its declared width class (`VInt 99999999999 Bits8`). The one-pass reader cannot produce
  one — it reads the payload out of a field of that width — but nothing here enforces it, so the
  lemmas that need it say so in their premises rather than assuming it away.
* **Reproducibility rests on the pin plus `--quake`, because proof hints no longer exist.**
  `--record_hints` and `--use_hints` were removed from F\*, so the usual way to pin an SMT search is
  unavailable. What replaces it: the pinned release and its bundled Z3, a margin on `--z3rlimit`
  well above what any query here needs, and `--quake 3` over three cold runs in CI. That is
  evidence, not a guarantee.

### Rung 4 — Not claimed

Named because an unstated exclusion reads, to anyone who finds it later, as a claim that failed.

* **The bytes-to-`Value` pass.** Phase 786 bounds it — length against remaining on every
  length-prefixed read, a depth ceiling — but bounded is not proved. This is the one place an
  EverParse-shaped binary-format proof would apply if it is ever wanted. A payload the reader itself
  refuses never reaches either decoder, and the differential skips it rather than pretending
  otherwise.
* **The System.Text.Json path.** Outside until its value carrier is decided: the current JSON value
  model's numeric case cannot carry an `int64` past 2^53, an exact decimal, or the source width.
* **The reflection fallback.** Outside *by construction*, not by omission. `Read.Reader.Read` is an
  interpreter over an open, possibly recursive type graph walked with a mutable cursor, so "total on
  every input" is not a well-formed statement about it — the input includes the type graph. That
  asymmetry is the whole argument for the closed value model.
* **The emitter.** One narrowing is unrefusable at the reader by construction: `writeInt64` compacts
  `2147483648L` into bytes that *are* a well-formed `int32 -2147483648`. The value model faithfully
  carries a 32-bit source and the information rule admits it — correctly, since refusing it would
  refuse every negative decimal the corpus pins. Closing it needs a writer that never compacts
  across a sign boundary, which is a wire break.
* **Semantics, authorisation, tenancy.** A decoded value is well-typed, not authorised, not
  tenant-scoped and not semantically valid. Those are other phases' boundaries.
* **`DateOnly` and `TimeOnly`.** No combinator, so no model: the Fable MessagePack reader refuses
  both types outright, and this ships a cross-host algebra or it ships nothing.
* **Any record not opted in.** The algebra is an opt-in replacement; a record still decoding through
  reflection is on the shipped default path and outside everything above.

---

## The claims ladder — the disclosure fold (Phase 790)

The same four rungs, for `DisclosureFold.fst`. The subject is the pair of functions every
population door runs before it reports anything: the egress predicate
`DisclosureEgress.evaluate`, and the fold `PopulationDisclosure.fold` with its two companions
`valuesWithheld` and `disclosedStats`. Both are pure and total over closed types, which is why a
theorem about them is cheap to state and — the important half — cheap to keep tied to the code.

### Rung 1 — Proved

**Formally verified, on the pinned prover, with `--report_assumes error`, and spent on these five
lemmas alone:**

* **`no_undisclosed_output`.** No fact whose verdict is not-disclosable appears in the disclosed
  list — for any ranking, any total verdict function, any policy assignment.
* **`withheld_is_count_only`.** The withheld count is the number of not-disclosable verdicts, and
  the withheld projection is `countBy`-then-`sortBy` over their policy refs: both are functions of
  the verdict list alone. Its corollary `withheld_blind_to_values` says the same relationally, over
  two rankings whose facts may differ in every field and even in *type* — pointwise-equal verdicts
  give an identical projection, which is the strongest way to say a value was never consulted.
* **`ranks_preserved`.** Every disclosed `(rank, fact)` has `fact` at one-based position `rank` of
  the input ranking. A withheld member leaves a visible gap rather than promoting the member below
  it, and a contiguous renumbering cannot satisfy the statement.
* **`magnitudes_absent_iff_withheld`.** When anything was withheld, the gated summary's minimum,
  maximum and mean are absent; when nothing was, the summary is returned untouched; and the
  existence-level fields — counts, period coverage, freshness, method mix — ride through in both
  cases. The biconditional is over the gate's *action*: it fires exactly when some verdict denied
  (`withheld_iff_any_denied`). It does not say an absent magnitude implies a withheld member — a
  summary over nothing comparable carries none to begin with.
* **`verdict_noninterference`.** Two rankings that agree on every disclosable fact, and on every
  verdict, yield *identical* `PopulationDisclosure` records — the same disclosed list, the same
  withheld count, the same withheld projection — whatever the withheld facts' identities or values.
  No observation of the fold's output distinguishes one withheld population from another.

Supporting, and proved beside them: `evaluate_characterised` — `Surfaceable` is always disclosable,
`Internal` never, `Restricted` exactly when the resolver answers `Some true`, so `Some false` and
`None` are one clause and an unknown policy ref can never fail open — and `unknown_policy_denies`
for the conservative default. They are what tie the verdicts the fold consumes to the predicate
that produces them.

Three things the model leaves *opaque*, each because the fold never inspects it and a second
implementation here would be free to disagree with the host's: the egress surface (the predicate
hands it to the resolver and reads nothing from it), a fact's payload (every field but its id —
which is what lets the noninterference lemma say "whatever the withheld facts' values" and mean
it), and the string ordering the projection is sorted by (see Rung 3). Every lemma holds for any
ordering.

### Rung 2 — Differentially tested

Not proved. *Measured*, on every run of the gate.

* **The model agrees with production.** `DisclosureProofOracleTests` runs the extracted fold
  beside the shipped one over the rankings the three door packs pin — the answer planner's, the
  population tool's, the coverage narrative's, reproduced as data — and over four hundred generated
  rankings, half with verdicts derived through the predicate the way a door derives them and half
  with verdicts sampled directly, so the fold meets refusal shapes no resolver in the file happens
  to produce. The two must agree on the **disclosed list, the withheld projection and the gated
  summary at once**, payloads included. The predicate is compared on its own first, over every
  classification, resolver and surface, because the fold takes verdicts as given and a drifted
  predicate would be invisible to it whenever both sides were handed the same wrong verdict.
* **The committed extraction is what the prover produced.** `check.ps1` re-extracts and byte-diffs
  against `oracle/DisclosureFold.fs`.
* **The differential is known to be able to fail.** A committed go-red oracle renumbers the
  disclosed ranks contiguously — the tidy-looking mistake that reports the third-best as the
  second-best — and the comparison is asserted to catch it, with the mechanism pinned on a
  three-member ranking: true ranks 1 and 3 against the oracle's 1 and 2. This is `ranks_preserved`
  running, and it is the one property a plausible reimplementation is most likely to lose.

### Rung 3 — Assumed, and stated

* **The `Mean` residual — a floor, not a proof.** The theorem is about the *fold*, and the fold
  gates a summary the store computed over the *whole* matched population. A restricted member ranked
  below the ceiling still contributed to that summary's `Mean`, and under a highest-first ranking can
  *be* its `Minimum`. What is proved is that the gate acts on the summary exactly when it acts on
  the members; what is not proved, or claimed, is that a magnitude the gate lets through carries no
  information about a member the caller was never returned. `PopulationQueryTypes.fs` says this in
  the same words, and this rung exists so the ladder says it too rather than letting Rung 1 imply
  otherwise. The mitigation is structural: when *any* member of the requested ranking is withheld
  the whole magnitude block goes, so the residual is confined to members outside the ranking asked
  for.
* **The string ordering is host-supplied.** `List.sortBy fst` compares ordinally; F\* has no string
  comparison that extracts to `Prims` alone, so the model takes a `before: string -> string -> bool`
  from the host, as the decoder model takes a string's length. Every lemma holds for any ordering;
  what the model does not verify is that the host's ordinal comparison is the shipped one. The
  differential compares the projection's *order*, so a disagreement fails the leg on any ranking
  withheld under two policies.
* **The bridges are hand-written.** A fact is its id plus itself as the opaque payload; a summary is
  its three magnitudes plus itself as the opaque rest. Short on purpose, and the read-back is a
  structural comparison of production's own records.
* The extractor, the F# compiler, the `Prims` shim, and reproducibility resting on the pin plus
  `--quake` — exactly as for the decoder, above.

### Rung 4 — Not claimed

* **That every door calls `disclosedStats`.** The theorem is about what the fold computes, not
  about who calls it. A summary type does not force the call, and nothing here could make it: the
  obligation on a door is to run the ranking through `fold` and the summary through
  `disclosedStats` before reporting either, and the evidence that the two shipped doors do is
  their own packs (`AnswerPlannerTests`, `PopulationQueryToolTests`), which is where a third door
  would have to add itself.
* **Statistical disclosure control.** Nothing here says what a determined reader could infer from
  a count, a period range or a freshness histogram, nor from the magnitudes of a ranking with
  nothing withheld. The disclosure vocabulary is a declared classification enforced at egress, not
  an inference-control regime, and the shipped code's honesty boundary says so in the same words.
* **The gate's resolution of a policy ref**, taint propagation, declassification and the audit
  trail. The predicate takes a resolver as a total function and the theorem holds for every one;
  what a particular resolver answers is that resolver's claim.

---

## Method, and where it comes from

The method is not new. An open-source F# wire decoder whose combinators were proved total in F\*
established it — a hand-written model over an abstract value, extracted and run as a differential
host against the production implementation — and this directory inherits its findings rather than
rediscovering them. They are recorded here because each one cost a build to learn and each will be
met again by anyone touching this file:

* Proof hints no longer exist; the pin, the `z3rlimit` margin and `--quake` replace them.
* The F# backend ships no runtime, so a `Prims` shim is required.
* The emitted F# uses pre-F#-8 indentation, so the oracle project — and only that project — relaxes
  strict indentation and silences `FS0058`.
* A ghost definition needs `noextract_to "FSharp"`; refinement types are erased regardless.
* Extraction refuses outright on an unchecked module, so the leg is check-then-extract against the
  cache, never one pass.

Two further findings are this directory's own, learned by building rather than by reading, and
recorded beside the code they constrain:

* **A mutually-recursive type group extracts with its `and` indented**, which F# rejects
  (`FS0010: Unexpected keyword 'and' in member definition`). Fixed at source — the map case carries
  a parameterised pair rather than a second mutual inductive — not by post-processing the output.
* **`unfold` on the applicative pipeline operator is unusable.** It inlines at every step, and a
  four-step pipeline then extracts as four nested `match` expressions whose cases begin at column 0,
  unparseable under F#'s offside rule however the indentation flags are set. As an ordinary function
  it extracts as four flat calls.

And two from the disclosure model (Phase 790), the first second module and so the first to find out
what the leg had assumed about there being one:

* **A recursive ghost predicate that sits under `/\` must return `prop`, not `Type0`.** `each_rank_true
  ranked offset rest` as a `Tot Type0` conjunct fails with "Expected type Prims.prop but … has type
  Type0"; declaring the predicate `Tot prop` is the whole fix, and `True` / `False` / `==` / `==>`
  all sit happily inside it. A predicate that needs no `==` (a check on an `eqtype`) is better as a
  `bool` anyway.
* **Nothing in the leg should name a module twice.** The first version of `check.ps1` carried the
  module name in the pin's `extract` flags *and* in the script's steps; the second module would have
  meant a second flag set. The pin now carries only `--codegen FSharp`, and the script appends
  `--extract <module>` per entry of one `$modules` list — source, committed oracle, host list and
  case floor — so a third model is one entry and no other edit.

---

## When the byte-diff fails

It means a committed `oracle/*.fs` is not what the prover produces from the current `.fst` beside
it. That is the expected state after any model edit, and the fix is to copy the
fresh extraction over the committed one and commit the two together — `check.ps1` prints the exact
command and the first forty lines of the diff. It is *not* a state to resolve by editing the
extraction: the next run would simply report it again.

## Licence

Apache-2.0, like everything beside it. See [`LICENSE`](../LICENSE).
