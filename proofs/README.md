<!--
SPDX-License-Identifier: Apache-2.0
Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
-->

# `proofs/` — the machine-checked theorems

This directory holds five theorems and the machinery that keeps them honest. Each has its own
claims ladder below, because the five say different things and a reader should not have to work
out which rung a sentence about one belongs to by reading the others.

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

**The tool gate soundness theorem (Phase 793).** For any RBAC predicate, any grant-liveness
predicate, any tool policy and any registry, the one decision behind the AI tool surface —
`ToolGate.decideDeclared`, which `ListAccessible` applies as a filter and the agent loop's dispatch
re-check applies to the tool a name resolves to — lists no tool whose declared effects exceed the
policy's ceiling, lists exactly the tools it admits, never admits at dispatch a name the list would
have refused, and never lets a tool declaring `Egress` through a ceiling that does not name it. Its
ladder is [at the end](#the-claims-ladder--the-tool-gate-phase-793).

**The model-input admissibility theorem (Phase 792).** Everything a language model is shown is one
closed value, and what a machine has checked about it is this: every fact that value holds carries
an affirmative disclosure verdict resolved for the caller's own scope — a verdict for somebody
else's scope is not permission, and the constructor refuses both, so a store offering material the
caller may not see cannot place any of it in the value however the caller folds over it; rendering
that value to what a provider accepts introduces nothing the value does not already carry, because
the renderer reads only the assembled text blocks and the conversation turns and cannot see the
fact list at all; and two stores that agree on the facts disclosable for that scope produce the
same value and byte-for-byte the same rendering, whatever the facts they withhold differ in —
identities, contents, counts, policies. **Two things it does not claim**, each because nothing
here can see them — and one it no longer needs to assume: since Phase 797 the scope a door
compares against is a `ResolvedScope` only the platform's scope resolution can mint, so "which
string is right" is settled by the type at every door rather than taken on trust (the
residual, a caller assembling outside the doors, is stated on the ladder); anything on the *outbound* side — the transport, what a model answers,
and what a tool it calls may fetch, the last of which is load-bearing for the plain-English
sentence and so is carried as a stated assumption rather than quietly dropped; and, within the
value, that retrieved passages
passed their content gate — the shipped constructor accepts a passage whatever its gate said, so
that half is a check a caller must run rather than an invariant the type enforces, and the theorem
proves exactly the decision procedure and the premise, not the guarantee. Its ladder is
[further down](#the-claims-ladder--the-model-input-phase-792).

**The taint-flow noninterference theorem (Phase 795).** One contributor's data does not reach
another contributor absent a declassification the first accepted. For any pipeline in the transform
algebra, any two input assignments differing only in one contributor's data, and any output whose
computed label does not carry that contributor, the outputs are *identical* — and over a sequence of
releases, the same holds of each contributor's whole observable slice, so ordering and count are
covered rather than one output at a time. The exception is stated as the exception: a label lost a
contributor only through a declassification routine that contributor's own scope accepted, and
nothing else lowers a label at all. Its ladder is
[further down still](#the-claims-ladder--the-taint-flow-phase-795).

**The Elmish runtime theorems (Phase 788).** Two models of the in-tree MVU runtime, the loop every
client on the platform runs on, whose core had no test on either host until this phase. The
**ring buffer** every reentrant `dispatch` waits in is a FIFO queue: over *any* sequence of pushes
and pops it produces exactly the outputs a reference queue produces, through every grow — so every
deferred message comes out once, in order, and never as one of the placeholder slots the grow step
manufactures. The **subscription diff** run after every `update` starts exactly the deduplicated new
subscriptions, stops exactly the removed ones, keeps exactly the common ones, never both for one key,
reports exactly the duplicates — and its `keys = newKeys` shortcut agrees with the general path,
which is where a started-twice / never-stopped leak would have lived. The ring theorem's one
precondition (two slots) is met by construction and shown necessary. Its ladder is
[at the end](#the-claims-ladder--the-elmish-runtime-phase-788).

**The dispatch loop theorem (Phase 789).** The loop *around* that ring — `Program.runWithDispatch`,
the scheduling skeleton over the ring, the `reentered` latch, the `terminated` latch and the model —
is a total step machine once its impure callees are abstracted as an oracle that says only which
messages they synchronously re-dispatch and whether they call `Terminate`. Over that machine: every
message `dispatch` accepts is handed to `update` **exactly once, in the order accepted**, from
outside and from inside alike; a reentrant dispatch is queued at the back and can neither be lost
nor moved ahead; once terminated, nothing is processed and nothing clears the flag; the boot drain
that duplicates the critical section by hand *is* that critical section; and `DispatcherCore.active`
is the loop's `terminated` negated in every reachable state, with the one production arm that
breaks the encoding computed as the exception. Its ladder is
[after the runtime's](#the-claims-ladder--the-dispatch-loop-phase-789).

**The machinery**, because a theorem about a model is worth what the tie to the code is worth:

| File | What it is |
|---|---|
| `RemotingDecode.fst` | the decoder model — `Decode.fs` clause for clause, each definition naming its F# counterpart, every refusal message reproduced verbatim |
| `DisclosureFold.fst` | the disclosure model — `DisclosureEgress.evaluate` and `PopulationDisclosure.fold` / `valuesWithheld` / `disclosedStats` clause for clause, each definition naming its F# counterpart |
| `ToolGate.fst` | the tool gate model — `ToolGate.decideDeclared`, `AIToolRegistry.ListAccessible`, `FindByName` and the dispatch re-check clause for clause, each definition naming its F# counterpart; RBAC and grant liveness taken from the host as predicates |
| `ModelInput.fst` | the model-input model — the closed value, its smart constructor, the assembly a caller folds over it, and `render` clause for clause, each definition naming its F# counterpart |
| `TaintFlow.fst` | the taint-flow model — the label lattice, `AssemblyLabelling.contributedBy` / `label`, `DisclosureTaintConfig.routineClears` and the derivation walk clause for clause, each definition naming its F# counterpart |
| `ElmishRing.fst` | the ring-buffer model — `RingBuffer<'item>`'s two-case state, `Push`, `Pop` and `doubleSize` clause for clause, the backing array as a slot list with the placeholder a constructor, and the `run` driver both hosts execute |
| `ElmishSub.fst` | the subscription-diff model — `Sub.Internal.diff`, `NewSubs.calculate` and the active-list half of `Fx.change` clause for clause, the key and the start function opaque |
| `fstar-pin.json` | the pinned prover (an F\* release, which bundles Z3), with its hash |
| `check.ps1` | the whole proof leg, over a module list: resolve the pin, then per module check, extract, normalise and byte-diff; build the two oracle projects; run each module's differential host |
| `normalise-extraction.fsx` | **Phase 850** — the layout normaliser the leg runs between extract and byte-diff: a parser for exactly the dialect the F# backend emits and a printer for indentation-clean F#, so every committed extraction compiles on both hosts with no flag (the section [below](#the-verified-implementation-spike-phase-850) says why the alternatives were not available) |
| `oracle/RemotingDecode.fs` | **generated** — the decoder model extracted to F#, committed so the repository never needs a prover to build |
| `oracle/DisclosureFold.fs` | **generated** — the disclosure model extracted to F#, committed for the same reason |
| `oracle/ToolGate.fs` | **generated** — the tool gate model extracted to F#, committed for the same reason |
| `oracle/ModelInput.fs` | **generated** — the model-input model extracted to F#, committed for the same reason |
| `oracle/TaintFlow.fs` | **generated** — the taint-flow model extracted to F#, committed for the same reason |
| `oracle/ElmishRing.fs`, `oracle/ElmishSub.fs` | **generated** — the two Elmish models extracted to F#, committed for the same reason |
| [`../tests/elmish-proof-corpus/`](../tests/elmish-proof-corpus/) | **generated** — the two Elmish models' verdicts over the seeded campaign, written by the .NET host and replayed by the **Fable** host against the transpiled runtime; since Phase 850 also the check that the two hosts' `Prims` shims compute the same ring |
| `oracle/Prims.fs` | the nine-name runtime the extractions need, because F\*'s F# backend ships none; every later model references a subset of the same nine |
| `oracle/fable/Prims.fs`, `oracle/fable/ToolUp.Remoting.Proofs.Oracle.Fable.fsproj` | **Phase 850** — the same shim over machine integers, and the project that compiles the committed `oracle/ElmishRing.fs` — and since Phase 884 `oracle/ElmishLoop.fs` — against it for the **Fable** host; `ToolUp.AI.Client.Tests` references it and runs the ring model and the dispatch-loop model live beside the transpiled runtime |
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
`tested` / `assumed` / `policy`, one entry per claim. Each `proved` entry names ONE theorem, the
model that declares it and the prover pin, with any further lemmas the claim rests on listed as
`supporting`, so an evidence check can resolve every one of them in the model; each `tested` entry
names its test list as `family`, the file it lives in as `host`, and its `cases`; each `assumed`
entry states its premise as `evidence.assumption`; each `policy` entry names the document that
states it. That file is hand-authored and never
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
* **The two combinators Phase 800 added, characterised, and the round trip widened over them.**
  `tuple_of` (the arity check every `tuple2`..`tuple4` runs first) accepts an array of exactly its
  arity and refuses every other value naming both arities — never a slice — and `tuple2` on such an
  array is that refusal verbatim, so an element decoder is never reached on an array of the wrong
  width (`lemma_tuple_of_characterised`, `lemma_tuple2_refuses_wrong_arity`). `fields` — the
  several-field union-case form, with `arity >= 2` stated as a refinement because a one-field case
  is written directly — hands the inner array to its pipeline exactly when it has the case's arity,
  and refuses a wrong-width array, a bare payload and a missing payload slot each by name
  (`lemma_fields_characterised`). The reference vocabulary gains a leg whose `hop` is a pair and
  whose `status` is a union with a several-field case, and `decode_encode_roundtrip_leg` /
  `decode_encode_roundtrip_status` carry the round trip over both shapes. Each lemma family was
  shown red on a deliberate break — `fields` accepting the wrong width, the encoder swapping a
  case's fields, `tuple_of` slicing — before it was trusted.

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
* **The Phase 800 shapes are measured on both paths.** The corpus's tuple fixtures are paired through
  `tuple2` / `tuple3`, and `Outcome` is paired a *second* time through `fields 2` beside its original
  `payload` pairing — a type may now carry several labelled pairings, so a disagreement names which.
  The corpus's mutations target no tuple or several-field case, so their refuse path is run from
  hand-built values — wrong width, non-array, bare payload, missing payload, an element that
  refuses, an element that narrows — through every pairing at the type, with one contrast pinned:
  an inner array carrying a *trailing* element is accepted by `payload` (a pipeline reads the
  positions it declares, as a record decoder does) and refused by `fields`, whose arity check is
  the difference. The widened vocabulary's round trip is run through the extraction, one instance
  per union case.

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

## The claims ladder — the tool gate (Phase 793)

The same four rungs, for `ToolGate.fst`. The subject is the decision every AI tool passes twice:
`ToolGate.decideDeclared` (`AIToolRegistry.fs`) — RBAC, grant liveness, then the declared effect
classes against the deployment's `ToolPolicy` ceiling — applied by `ListAccessible` as the filter
that builds the list the model is offered, and applied again at the agent loop's dispatch site to
the tool a produced name resolves to. Pure and total over closed types, with the two authority
predicates taken from the host.

### Rung 1 — Proved

**Formally verified, on the pinned prover, with `--report_assumes error`, and spent on these four
lemmas alone:**

* **`list_sound_against_policy`.** No tool whose declared effects exceed the policy's ceiling is
  listed — for any RBAC predicate, any grant predicate, any policy, any registry. Stated over a
  predicate that spells the ceiling out: under a bounded ceiling every listed tool's every declared
  effect has a class the ceiling names, and an undeclared tool is listed only when the policy admits
  undeclared tools. Its support, `admitted_iff_within`, is an *equality*: an admitting verdict is
  exactly the two authority predicates answering yes and the declaration sitting within the ceiling.
* **`listed_iff_admitted`.** A tool is in the list exactly when it is in the registry and the
  decision admits it. Nothing the decision refused reaches the list and nothing it admits is
  dropped — the list filter and the decision are one function, not two that happen to agree.
* **`dispatch_never_wider_than_list`.** If the dispatch re-check admits the name the model produced
  — looked up by authored name or provider alias, then decided — the list the model was offered
  carries a tool of that name. The "two gates that must agree should share their whole decision"
  comment on `ListAccessible`, as a lemma: a forged, hallucinated or replayed name the list would
  have refused is refused at dispatch too.
* **`egress_never_escapes`.** A tool declaring `Egress` to any destination is refused under every
  bounded ceiling that does not name the egress class, whatever else it declares and whatever the
  authority predicates answer. This is the go-red property in proof form.

Two things the model leaves *opaque*, each because the gate reads one bit of it per tool and a second
implementation here would be free to disagree with the host's: RBAC (`isToolSourcePermittedFor`,
with its permission hierarchy and reserved-namespace exemption) and grant liveness
(`moduleGrantGate`, a per-request verdict over consent stamps). Both are earlier phases' decisions;
the theorem takes them as total arrows and holds for every pair.

### Rung 2 — Differentially tested

Not proved. *Measured*, on every run of the gate.

* **The model agrees with production.** `ToolGateProofOracleTests` runs the extracted gate beside
  the shipped one over every in-tree tool definition it can reach — the narrative and cross-module
  built-ins, the three fact tools, the sample client tool — and sixty generated tools with generated
  declarations, under twenty-eight generated policies (the external-principal view of each
  included), eleven generated callers and their grant predicates. The two must agree on the
  **verdict** for every tuple, on the **listed set, in order**, and on the **dispatch answer** by
  authored name, by provider alias and for an unknown name.
* **The committed extraction is what the prover produced.** `check.ps1` re-extracts and byte-diffs
  against `oracle/ToolGate.fs`.
* **The differential is known to be able to fail.** A committed go-red gate drops `Egress` from a
  declaration before deciding — measuring a tool by what it reads rather than where it reaches — and
  the comparison is asserted to catch it, with the mechanism pinned on one tool declaring only
  `Egress` under the read-only policy: production and the honest oracle refuse naming the egress,
  the blind gate admits.

### Rung 3 — Assumed, and stated

* **The authority predicates are host-supplied.** What RBAC and grant liveness *answer* is not
  verified here — only that the gate composes them with the ceiling in one place. The differential
  passes production's own RBAC predicate and generated grant predicates to both sides, so the
  verdicts compared include the two refusals those predicates produce.
* **The envelope binds only the seams.** The theorem is about *which tools run*. What a running body
  may *do* is `ToolEffectEnvelope`'s business, and it refuses only what a declared tool reaches for
  through the SDK's seams — a host capability through `guardInvoke`, an outbound request through
  `guardEgress`, a scoped write through `guardWrite`. An executor that constructs its own
  `HttpClient` is outside every check, exactly as it is outside the composition capability gate;
  nothing short of process-level isolation closes that, and nothing here claims to. The verified
  composition profile makes the declaration mandatory, so under it every in-tree tool is bound at
  the seams.
* **The bridge is hand-written.** An effect case for case, a declared set as its sorted list, a
  policy's ceiling and undeclared flag, a tool's name, alias, source and folded declaration. Short on
  purpose; the go-red gate is built from production's own decision over a perturbed declaration, so
  the comparison is known to see a declaration defect.
* The extractor, the F# compiler, the `Prims` shim, and reproducibility resting on the pin plus
  `--quake` — exactly as for the two models above.

### Rung 4 — Not claimed

* **The ceiling is by class.** A policy admits or refuses effect *classes*, with payloads erased; it
  cannot say "egress to one host but nowhere else". That is a decision, not an omission: a ceiling
  that could only name exact destinations could never say "no egress at all". The exact payload is
  checked at the moment of use by the envelope, which refuses `Egress "b"` under a declaration of
  `Egress "a"`.
* **Client-resident bodies.** A client-resident tool's body runs in the browser and is outside the
  envelope; the client-tool allowlist seam binds it. The gate's list and dispatch decisions apply to
  both locations alike.
* **The approval keying is a policy, not a theorem.** `ToolPolicy.RequireApproval` holds a tool for
  the user's decision by declared class, ahead of the deployment's own `IToolApprovalPolicy`; the
  external-principal ceiling applies the same keying to an agent's grants, default-deny by class.
  Where and why that is decided is `docs/migrations/793-tool-effect-class.md`; the manifest carries
  it as a `policy` entry rather than letting Rung 1 imply it.

---

## The claims ladder — the model input (Phase 792)

The same four rungs, for `ModelInput.fst`. The subject is the value every provider call is given —
the facts, the retrieved passages, the assembled prompt blocks, the post-budget tool results and
the conversation turns — together with the smart constructor that populates it, the assembly a
caller folds over that constructor, and the pure `render` that turns the value into the two
arguments the provider interface takes.

### Rung 1 — Proved

**Formally verified, on the pinned prover, with `--report_assumes error`, and spent on these three
lemmas and their companions alone:**

* **`admissible_by_construction`.** For *any* candidate store — whatever verdicts it carries,
  whatever scopes those verdicts were resolved for, in any mixture — the assembled value holds only
  facts whose verdict is affirmative **and** whose scope is the caller's resolved one. Nothing about
  the store has to be checked in advance, which is what "by construction" means: the arm that would
  break the invariant is the arm that refuses. Its supporting lemmas say where the guarantee comes
  from and where it stops — `add_fact_preserves_admissible` needs no premise on the fact's verdict,
  `blocks_and_turns_preserve_admissible` covers the three constructors that touch neither facts nor
  passages, and `add_chunk_preserves_admissible` is the one that *does* carry a premise (Rung 3).
* **`render_faithful`**, with its relational companion **`render_blind_to_facts`**. Four clauses:
  the turns pass through untouched; the system prompt is exactly the blocks' text joined on the
  separator the prompt composer uses, and absent when there are no blocks, which is how a call that
  sent no system prompt stays byte for byte unchanged; every id the leak differential *reports* is
  genuinely present in the rendering and genuinely undeclared in the value; and every candidate id
  that is present and undeclared *is* reported — so an empty report means what a reader takes it to
  mean rather than merely that a filter found nothing. `render_blind_to_facts` is the stronger half
  and the reason the others matter: two values agreeing on their blocks and turns render
  identically whatever their facts, passages and tool results are, so the renderer cannot read the
  fact list at all and no rendering can depend on one.
* **`input_noninterference`.** Two stores that agree on every fact disclosable for the resolved
  scope assemble to the *same* value, and therefore to the same rendering and the same bytes. The
  withheld facts are quantified away entirely: the two stores may differ in how many they hold, in
  their identities, in their contents and in their policies, and no observation of what the
  provider is shown distinguishes one from the other. The renderer that turns admitted facts into
  the block a reader sees is *universally quantified*, so this holds for every one a caller might
  write — including one that prints each admitted fact's content verbatim. What it could not
  survive is a renderer handed the whole store, which is exactly why the shipped shape hands it
  what was admitted, and why the theorem is stated over the assembly rather than over `render`
  alone.

* **The scope is the resolver's — by type, not by lemma (Phase 797).** The model compares
  `fact_scope` against the caller's scope and never derives either; what used to sit on Rung 3
  was *which string the caller passes*. Since Phase 797 every fact door — the store's members, the
  gate's `Check`, the three tools' executors — takes a `ResolvedScope` whose representation is
  private and whose constructor is internal to the platform's server tier, minted at the one
  point the scope-resolution middleware resolves the request. A door therefore cannot be handed
  a scope the principal did not resolve to: the compiler refuses the construction. The prover
  here is the F# type system, not F\*, so this bullet is on Rung 1 by a different instrument;
  what keeps it honest is pinned in the test pack (`ScopeChokePointTests`): no public
  constructor and no public union case on the type, the mint internal, a `StorageScope` planted
  in the request items *not* reaching a door, and a source guard over the three doors that
  refuses the pre-797 spellings, go-red pinned. What it does NOT cover is stated on Rung 3.

Three things the model leaves *opaque*, each because a second implementation here would be free to
disagree with the host's: substring containment (the leak differential asks the host `Contains`;
every lemma holds for any containment test at all), scope resolution (compared, never derived — and
since Phase 797 the string compared is the resolver's by type; see the last bullet of this rung and
Rung 3), and the fact renderer just described. The turn type is *narrowed* rather than opaque — the
model carries the three fields rendering reads and the host's bridge passes the production record
through unchanged — which is stated here rather than left to be discovered.

### Rung 2 — Differentially tested

Not proved. *Measured*, on every run of the gate.

* **The model agrees with production.** `ModelInputProofOracleTests` runs the extracted value,
  constructor, assembly and renderer beside the shipped ones over a pinned corpus and 150 generated
  cases, and requires them to agree on the **assembled value, the constructor's refusal wording,
  the mirrored tool-result record, the rendering, every byte of the rendered text, and the leak
  differential over it — at once**. The migration seam is compared on its own first, over the
  prompt shapes every provider entry lifts through, because a value assembled some other way would
  not exercise it. Agreement on the value alone would miss a renderer that showed the model
  something the value never admitted, which is the whole failure this phase exists to exclude.
* **The non-entry probe, re-run over both halves.** A 250-subject population is offered to the
  assembly as a *store*: three facts carry an affirmative verdict for the caller's scope, and the
  other 247 are denied one of three ways — an internal classification, a named policy, or an
  affirmative verdict resolved for somebody else's scope, which only the scope half of the
  invariant excludes. Exactly three enter the value, exactly three reach the rendering, and not one
  of the other 247 appears in the bytes a provider would be handed. The end-to-end probe in
  `PopulationQueryToolTests` asserts the transcript half of the same claim against a live agent
  loop and now asserts the rendering half beside it; a subject absent from a transcript because a
  tool happened not to return it is otherwise indistinguishable, there, from one the value refused.
* **Noninterference, run rather than only proved.** Every case with something withheld is replayed
  against a store that keeps the disclosable facts and replaces the rest wholesale — different
  identities, different contents, different policies, a different count — and nothing a provider
  boundary can observe is allowed to move.
* **The committed extraction is what the prover produced.** `check.ps1` re-extracts and byte-diffs
  against `oracle/ModelInput.fs`.
* **The differential is known to be able to fail.** A committed go-red oracle renders one fact the
  constructor refused — the mistake a plausible reimplementation reaches by rendering from the
  store rather than from what was admitted — and the comparison is asserted to catch it **on every
  case that has a withheld fact to leak**, not merely on one. The mechanism is pinned separately on
  a three-fact case: the value is *identical* under both oracles and only the rendering differs,
  which is precisely the failure a value-only comparison would have passed.

### Rung 3 — Assumed, and stated

* **Scope resolution — retired to Rung 1 by Phase 797, with this residual.** Until Phase 797
  this bullet read: the scope is a caller-supplied string, compared and never derived, so
  everything above was conditional on that string being the one the caller is entitled to. At
  every fact door that is now settled by the type (Rung 1, last bullet). What remains assumed is
  narrower and is the same shape as the assembly bullet below: a caller that assembles a
  `ModelInput` *outside* the doors writes the `DisclosedFact.Scope` string itself, and nothing
  here checks that it wrote the resolver's. The doors are where the theorem's fold runs in the
  shipped code, so this is the residual, not the rule. Behind the doors, the scopes the platform
  *carries* rather than resolves are still strings, and Phase 818 names them: a job scheduled with a
  resolver-minted scope now runs under it (the in-process scheduler re-mints it through a second
  internal mint whose one caller is pinned), but the reactive recompute path — carried from a
  string-keyed data write — imports, coherence sweeps, knowledge-base dependency records, the
  job-admin API and any scheduler outside the platform's server tier still key the fact store on a
  carried string.
* **The tool-effect side is assumed, not checked here.** This theorem is about what goes *in*. The
  sentence a reader wants — that a model is isolated from knowledge except what is explicitly
  permitted — additionally needs that the tools a model may call cannot fetch what the input side
  refused, and nothing in this module can see that. It sits on this rung rather than the next one
  because it is load-bearing for the claim rather than merely outside it: a tool that reads freely
  would defeat everything above without contradicting any lemma in it. It is the subject of its own
  theorem, and this rung is where it stays until that lands.
* **The passage gate is CHECKED, not enforced.** The shipped constructor accepts a retrieved
  passage whatever its gate result, and offers a predicate for a caller to ask instead. So the
  passage clause of admissibility is proved as a *premise* on the add
  (`add_chunk_preserves_admissible`) and as a total *decision procedure*
  (`has_failed_gates_decides`), never as the unconditional invariant the fact clause is. A caller
  that never asks can hold a value carrying a failed passage, and nothing in the type stops it.
  "Ungated" is likewise recorded honestly: it says the gate did not run, never that it passed.
* **The model is the assembly's counterpart, and the assembly is a caller's code.** The shipped
  module ships the constructors; the fold over a candidate set is what each caller writes around
  them. The model states the theorem over that fold because a statement about one constructor call
  says nothing about a store — but a caller free to write a different fold is free to write one
  these lemmas do not describe. What keeps that honest is the differential, which runs production's
  constructors in the same sequence.
* **The bridge is hand-written.** A defect in it would make the comparison compare the wrong thing.
  It is case-for-case on purpose, and the block renderer is *shared* between the two sides — the
  model's facts are mapped back to production records and handed to production's own renderer —
  precisely so no second implementation can quietly disagree.
* The extractor, the F# compiler, the `Prims` shim, and reproducibility resting on the pin plus
  `--quake` — exactly as for the two models above.

### Rung 4 — Not claimed

* **The transport.** That the rendered bytes reach a provider unaltered, and that the provider
  sends what it was given. `render` is where this theorem stops.
* **The model itself.** Nothing here says anything whatever about what a language model does with
  the bytes, what it answers, or what it may have learned elsewhere. A theorem that implied any of
  that would be claiming something it cannot see; the value of keeping the model *outside* the
  trusted set is the entire point of stating the input side this precisely.
* **The builders' IO.** A block's text is data by the time the value holds it. How a builder
  obtained it — which store it read, which gate it ran, whether it was entitled to — is its own
  obligation and is not visible here.
* **That every caller runs the assembly this way.** The theorem describes a fold over the shipped
  constructors; a caller that hand-assembles the record type directly is outside it. The type is
  the seam, and what makes the seam worth having is that the constructor is the only way to add a
  fact — but nothing here forces a caller through it.
* **What can be inferred from what was disclosed.** Nothing above says what a reader could deduce
  from the facts that *were* admitted, from their count, or from their absence.

---

## The claims ladder — the taint flow (Phase 795)

The same four rungs, for `TaintFlow.fst`. The subject is what multi-party disclosure policies rest
on: the taint-label lattice a value's lineage carries, the label-generic fold that propagates it
down a transform pipeline, the entitlement predicate that decides whether a declassification routine
may clear a given policy, and the derivation walk whose empty result is the gate's whole condition.

Rung 4 of the disclosure-fold ladder above hands *taint propagation and declassification* back as
unclaimed. **This is the ladder where they land.** What that rung says about a resolver is
unchanged: the egress predicate still takes one as a total function and still claims nothing about
what a particular resolver answers.

### Rung 1 — Proved

**Formally verified, on the pinned prover, with `--report_assumes error`, and spent on these seven
lemmas alone:**

* **`join_laws`.** The six laws the shipped lattice checks executably. Associativity and `bottom` as
  a two-sided identity hold *on the nose*; commutativity, idempotence and the upper-bound law hold up
  to label equality — which is set equality, and the shipped label *is* a set. `join_is_least` adds
  the other half of the order, so the join is the *least* upper bound and the order is a lattice
  order rather than merely some order. `below_is_sub` proves the shipped definition of the order
  (`join a b = b`) is the same relation the model reasons with.
* **`label_monotone`.** Raise any source's declared label — one of them, all of them — and the label
  the pipeline's output carries can only rise. Adding a contributor's data to an input never lowers
  what the output is known to carry.
* **`label_never_falls`.** Along the pipeline the label is non-decreasing at *every* node. The fold
  only ever joins, so nothing in the transform algebra can lower a label. This is the other half of
  `declassify_only_lowers` below, proved over the algebra rather than over one operator.
* **`flow_noninterference`.** The flow half. For any pipeline, any two input assignments differing
  only in one contributor's data, and any output whose computed label does not carry that
  contributor — the outputs are *identical*. It is quantified over **every semantics** of the
  transform cases, so it is not a claim about what the transforms happen to compute; it is a claim
  about what they are *able to read*, which is the thing the label tracks. That quantification is why
  the model needs nothing from the production executor.
* **`declassify_only_lowers`.** The exception, stated as the exception. If a label carried a
  contributor and the narrowed label does not, the routine's entitlement predicate cleared it — and
  where that contributor's policy declares a contributor scope, the routine's accepting scopes name
  that scope. One contributor's consent can never lower another's label.
* **`conjunction_sound`.** An empty inherited-policy set means every taint-propagating source that
  reached the target was *dropped* somewhere on its derivation, and every drop anywhere in that
  derivation was made by a routine entitled to make it. So the gate's single condition — "this list
  is empty" — is exactly "a path satisfies every contributing scope's policy".
* **`trace_noninterference`.** The flow half over a **sequence**. A room emits an ordered trace of
  releases; `slice_tr q` is one contributor's view of it, with scoped segments that contributor is
  outside dropping out. Two runs differing only in another contributor's data give the same slice —
  the same values, in the same order, and the same number of them. Ordering and count are covered
  because the conclusion is list equality, not equality of one output.

And **`refinement_leaks_no_more`**, the seventh: an amended room whose contributor-`q` view can be
exhibited as a function of the baseline's contributor-`q` view and public data inherits the
baseline's noninterference. The obligation is on whoever supplies the rebuild function; the lemma is
that supplying it *suffices*, which is the whole content of the refinement pattern.

**The trace form is borrowed, and from a proof rather than a paper's abstract.** Rastogi, Swamy and
Hicks, *Wys⋆: A DSL for Verified Secure Multi-party Computations* (2019), proves security as a
delimited-release lemma over an observable trace, with each party's view a slice of the global trace
and an amended computation shown to leak nothing new by exhibiting its trace as a function of the
baseline trace plus public data. Three things transfer and none of the runtime: the trace-and-slice
statement, the refinement pattern, and the attacker model on Rung 3.

Four things the model leaves *opaque*, each because the code under proof never inspects it: a
source (the labelling hands it to the declared per-source lookup and reads nothing else), a frame
(the algebra never looks inside one, which is what lets the flow lemma say "whatever the other
contributor's rows" and mean it), a policy's contributor scope (resolved from the registered
vocabulary, which is compose-time data), and — the load-bearing one — the **semantics** of each
transform case.

### Rung 2 — Differentially tested

Not proved. *Measured*, on every run of the gate.

* **The model agrees with production.** `TaintFlowProofOracleTests` runs the extracted model beside
  the shipped code over the two-contributor fixtures and the Phase 794 generated pipelines. Four arms
  compare: the lattice over the power set of a three-ref vocabulary (`join`, the order, `isBottom`,
  `narrowsTo` under every clearing predicate), the entitlement predicate over every config and
  acceptance set including an undeclared scope, **the whole labelled assembly** — every node's label
  *and what that node contributed*, not merely the output, because the contribution is where a second
  contributor enters — and the derivation walk against production's own `InheritedLabel`.
* **The two relational laws are measured directly, not restated.** Comparing outputs cannot test a
  relational law, so these arms do the experiment: relabel one contributor's input rows and require
  the pipeline's output to be identical wherever production's *own* computed label says that
  contributor did not reach it; and the same over a room, comparing the other contributor's slice, so
  ordering and count are what is compared. The frame is a **transcript** — the free term over the two
  step functions — which is the most discriminating semantics available: nothing is combined and
  nothing is lost, so any dependence at all shows as a literal difference.
* **Both relational arms carry a sample-adequacy guard**, because a relational law over a sample in
  which the premise never holds passes for free. The sample must contain pipelines that *do* carry
  the contributor and pipelines that do not, and at least one of the latter must contain a join — or
  the label only ever tracked a single source. The trace arm additionally pins the premise *doing
  work*: a room where the observer does see a release computed from the other contributor's data
  fails the premise and its slice demonstrably **moves**.
* **`conjunction_sound` is run rather than restated too.** Wherever production reports an empty
  inherited set, the model's own `drop_occurred` and `drops_are_entitled` are evaluated over the same
  derivation, and the arm asserts it found such a case at all.
* **The committed extraction is what the prover produced.** `check.ps1` re-extracts and byte-diffs
  against `oracle/TaintFlow.fs`.
* **The differential is known to be able to fail.** The committed go-red oracle is the **blind
  join**: a fold that forgets a join brings a second source into the pipeline. It must be caught
  twice — by the labelling comparison, and by the relational arm, where a blinded label reports a
  frame as free of a contributor whose output moves when that contributor's rows move. The mechanism
  is pinned on a one-join fixture: production and the honest oracle carry both contributors past the
  join, the blind oracle carries one.

### Rung 3 — Assumed, and stated

* **The attacker is honest-but-curious.** The theorem is about what a *composed computation* can
  carry to whom. It assumes the parties run the computation as composed and observe what they are
  given; it says nothing about an adversary who deviates from the protocol, and nothing about one who
  can read memory, modify a binary, or reach the data outside the computation at all. That is the
  same attacker model the borrowed trace form was proved under, and naming it here is the point of
  this rung: containment read as operator-proof is the one overclaim this whole ladder exists to
  prevent.
* **The declassification routines' own correctness.** Whether an aggregation-over-k or a noise
  addition actually loses attribution is the routine's business, not this theorem's. **This is a
  proof about walls, not about routines.** `declassify_only_lowers` says a label was lowered only by
  a routine the contributor accepted; it does not say the routine deserved to be accepted.
* **Inference across declassified outputs.** A release whose computed label carries a contributor is
  *outside* the trace lemma by construction: that release is a declassification, the contributor's
  data did influence it, and that is what a declassification means. What a determined reader could
  reconstruct across a *sequence* of such releases is a budget question, and a budget is a metered
  allowance, never a proof. Nothing here meters one.
* **The storage scope is assumed resolved.** The theorem is about the computation's information flow.
  Where the inputs and outputs are held, and under whose control, is a separate obligation the
  theorem takes as discharged.
* **The derivation is modelled as a tree.** Production walks a graph with a memo table and a cycle
  guard. A memo cannot change a value; the content-addressed store is acyclic, and a tree is the
  acyclic case unfolded — where a graph shares an upstream the unfolding visits it twice and joins
  the same refs into the same label, which is immaterial under set equality. The differential checks
  the unfolding against the real walk on every fixture rather than leaving it asserted, and mirrors
  the cycle guard so a malformed graph cannot diverge in the host either.
* **The theorem is about the label, not the ordered ref list.** Production keeps the inherited refs
  in nearest-declared-first order so the single deny ref it reports is unchanged from Phase 562.
  Order is a presentation fact about a refusal message; the label is the set, and the set is what is
  reasoned about.
* **The bridges are hand-written.** A label crosses as its own contents and returns through the
  shipped constructor; a transform crosses as its case; a fact's derivation is unfolded from the
  graph, mirroring the private source-policy test rather than reaching for it. Short on purpose, and
  the go-red oracle beside them exercises the one field most likely to be got wrong.
* The extractor, the F# compiler, the `Prims` shim, and reproducibility resting on the pin plus
  `--quake` — exactly as for the two models above.

### Rung 4 — Not claimed

Named because an unstated exclusion reads, to anyone who finds it later, as a claim that failed.

* **The inferred-lineage fallback.** Where no transform tree has labelled a fact, the walk falls back
  to lineage inferred from evidence linkage — and that inference is sound only where a derivation was
  routed through a series output. A derivation that was not is invisible to it, and an invisible
  upstream carries no taint at all. The theorem covers the **computed** side; the shipped code raises
  a finding naming every fact it fell back on, precisely so a verdict resting on inference cannot
  pass for one resting on computation, and this ladder does not quietly promote it.
* **A hostile operator.** Root access, memory dumps, a modified binary and a compromised host are all
  outside every sentence above, and no amount of information-flow proof reaches them. Closing that
  gap is a hardware-attestation question, not a theorem.
* **That every pipeline declares its sources honestly.** A source nobody declared labels as clean.
  That is a *positive statement* the composition makes — which contributor a source belongs to is
  knowledge the composition has and the frame does not — and nothing here can detect a declaration
  that was simply wrong.
* **That the transform algebra stays closed.** `contributedBy` is an exhaustive match, so a sixth
  transform case fails to compile until someone says what it reads; that is production's guard, not
  this theorem's, and the model would have to gain the case too.

## The claims ladder — the Elmish runtime (Phase 788)

The same four rungs, for `ElmishRing.fst` and `ElmishSub.fst` together — two models, because they
are two closed algebraic structures, and one ladder, because they are one runtime and one
differential host drives both. The subject is `src/ToolUp.Platform.Client/Client/Elmish/`: the
~2,000-line MVU runtime every client in the estate runs on, whose core (`RingBuffer`,
`Sub.Internal.diff`, `DispatcherCore`, the `Cmd` combinators) had **no test on either host** before
this phase. The two structures with their law already written in the source are the ones proved.

### Rung 1 — Proved

**Formally verified, on the pinned prover, with `--report_assumes error`, and spent on these lemma
families alone:**

*The ring.*

* **`ring_is_queue`** — the headline. For any well-formed ring and *any* sequence of pushes and pops,
  the ring produces exactly the outputs a reference FIFO queue produces from the ring's unread
  contents, and ends well-formed holding exactly what the queue holds. That is `fifo`, `no_lost_slot`
  and `no_double_pop` in one statement, and it holds through every grow: every pushed item is popped
  exactly once, in push order, however many times the backing array doubles on the way. `push_spec`
  and `pop_spec` are the two single-step halves it composes; `create_wf` says every ring the
  constructor builds is well-formed and empty, whatever capacity was asked for.
* **`fifo`, `no_lost_slot_no_double_pop`** — the phase's lemma families as named corollaries over the
  ring the constructor builds.
* **`placeholder_unobserved`.** A pop on a well-formed ring never returns one of the placeholder
  slots the constructor's `Array.zeroCreate` and the grow step's tail manufacture. The source comment
  says `Unchecked.defaultof` "is never observed as a value"; this is that sentence as a theorem,
  which is possible only because the model makes the placeholder a *constructor* rather than an opaque
  default.
* **`order_across_grow`.** The grow step in isolation: the push that triggers `doubleSize` leaves the
  unread contents in order with the pushed item last, in a ring of `2n + 1` slots whose read head is
  0. The model reproduces what the code does, not what its comment says: the inclusive
  `0 .. items.Length` range yields `n + 1` placeholders, so "doubling" is `2n + 1`.
* **`succ_is_mod`.** The modelling step is faithful. The index step is modelled as a case split rather
  than `(i + 1) % n`, so every lemma above is linear arithmetic — cheap, and stable under `--quake` —
  and this lemma says the two are the same function on every index the code holds.
* **The precondition, met and necessary.** Every ring theorem assumes at least two slots. `create_wf`
  shows the constructor guarantees it (`minimum_capacity = 2`, the one number
  `RingBuffer.MinimumCapacity` and `Program.withRingBufferCapacity` both read since 788.D), and
  `capacity_one_loses_an_item` shows it is not a convenience: at one slot the `ReadWritable` state
  cannot tell one unread slot from none, the second push overwrites the first item before the grow
  step runs, and the first pop returns the *second* item — computed on the model with the floor
  bypassed. Before this phase the constructor floored at 10 while `withRingBufferCapacity` floored
  at 1 and documented per-program configurability; the fork's claim sheet in the top-level README
  records the reconciliation.

*The diff.*

* **`start_exactly_new`, `stop_exactly_removed`, `keep_exactly_common`.** `toStart` is exactly the
  deduplicated requested subs whose key was not active, as *pairs* — so the start function that
  survives dedup is the one the caller runs; `toStop` exactly the active subs no longer requested;
  `toKeep` exactly the active subs still requested, and with `toStop` a partition of the active list.
* **`never_both`.** No key is both started and stopped, and none both kept and stopped.
* **`dupes_exact`.** A key is reported duplicate iff it occurs more than once in the requested subs.
  `calculate_characterised` pins which occurrence survives: the *last*, because `List.foldBack` folds
  the last entry first — a fact the source does not state and a caller could reasonably guess the
  other way.
* **`fast_path_agrees`.** When `keys = newKeys` the shortcut returns `dupes, [], active, []` — and the
  general path, run on the same input, returns the same four lists. The shortcut is an optimisation,
  not a different answer. This is the lemma the phase was really for: a shortcut that disagreed with
  the general path is precisely where a "started twice / never stopped" leak would live, and nothing
  else in the tree would have seen it.
* **`change_keys`.** After `Fx.change`, every active key is a requested key, and — when every start
  succeeds — every requested key is active. **Stated of the ungated path (Phase 900).** `Fx.change`
  now reads the loop's `terminated` flag before each start, and a start that terminates the program
  ends the diff — nothing after it starts, what the call started is stopped, the next active list is
  empty. That gate is the *loop's*: the flag is a cell of `runWithDispatch`, set by a teardown the
  diff neither owns nor observes, and the theorem about it needs the loop's counters and its
  `terminate` transition, so it is stated where those live — `ElmishLoop.fst`'s `start_all` and
  `diff_terminating_starts_nothing_after`, over the same `start` records the boot's gate uses. The
  model here is `Fx.change` with the flag clear throughout, which is what the differential host
  runs production against and exactly the path production takes whenever no start terminates the
  program.

### Rung 2 — Differentially tested

Not proved. *Measured*, on every run of the gate — **and on both hosts**, which no earlier ladder
needed: the runtime ships to .NET and, through Fable, to the browser, and the browser's copy is the
one every client executes.

* **The model agrees with production, on .NET.** `ElmishProofOracleTests` in the platform pack runs
  the extracted `run` beside the shipped `RingBuffer` over generated push/pop sequences — capacities
  2–13, push biases that grow the ring past several doublings and drain it to empty, every sequence
  also run with a full drain appended — and the extracted `diff` / `change` beside the shipped ones
  over generated subscription sets with duplicates and with the exact active key set, so the shortcut
  fires. The comparison is on keys **and on the identity** of every handle and start function carried
  through: production must hand back the same object, not an equal-looking one.
* **…and under Fable — from the model's recorded verdicts, and since Phase 850 from the ring model
  itself.** The drivers are one shared module
  (`src/ToolUp.Platform.Tests/Client/ElmishProofDifferential.fs`, no test framework and no model in
  it) compiled into both packs. Until Phase 850 the extraction compiled on .NET only — the F\*
  extractor emits pre-F#-8 layout that needed `--strict-indentation-`, which Fable reads from no
  fsproj — so the .NET host also writes the model's verdicts for its campaign to
  `tests/elmish-proof-corpus/` as a self-describing corpus — each case carries its inputs and the
  outputs the proved model produced — holds that file to the live model on every run, and the Fable
  pack's `ElmishProofOracleTests` replays it against the transpiled `Ring.fs` / `Sub.fs`. Phase 850's
  normaliser made the committed extraction F# both hosts compile, and the Fable pack now ALSO
  compiles `oracle/ElmishRing.fs` over a machine-integer `Prims` and runs the ring model live: it
  reproduces the corpus's verdicts (so the `BigInteger` and `int` shims are held to each other), it
  agrees with the transpiled ring on a second campaign the corpus never recorded, and it catches the
  broken ring. Both hosts hold the shipped ring to the proved model's answer, and both compute it;
  the diff is still replayed. The generator is a small LCG rather than `System.Random`, so the
  sequences are the same by seed on either host — **true only since Phase 884.** Fable transpiled
  the LCG's `uint32` step as a plain JavaScript `a * b + c` with no reduction modulo 2^32, so the
  state lost its low bits within two draws and every draw after the first few was 0; the live
  campaign above was, until then, almost entirely capacity-2, one-push sequences. Nothing compared
  the two hosts' draws, so nothing noticed. The step is now widened to `uint64` and narrowed back,
  which is the value the wrapping `uint32` step always produced on .NET (no corpus moved), and the
  dispatch-loop differential pins a fingerprint of a draw that both hosts assert. **Since Phase 900
  the ring and the diff campaigns are pinned the same way**, in the shape of 884's two pins: each host
  draws the campaign the .NET corpus was written from (100 ring sequences, 400 diff inputs, the same
  seed) and asserts its canonical rendering against one literal — so "the Fable host replays what the
  .NET host drew" no longer rests on the loop's pin alone, and a generator that drew differently on
  one host fails by name on both. **The diff pin caught one on its first Fable run.** The diff
  generator's shuffle sorted by a projection that drew from the LCG, and Fable's `List.sortBy` applies
  the projection inside the comparer (per comparison) where FSharp.Core's projects each element once —
  so from the same seed, with the same LCG, the Fable host drew a different diff campaign from the
  first shuffled input on (its rendering was 400:32117:… against .NET's 400:31330:…). The replay
  never noticed because it never drew. The shuffle now draws one key per element, which is what .NET
  always did, so the corpus did not move; the pin holds both hosts to it.
* **The campaign is known to have reached the grow step.** The .NET host asserts the largest backing
  array the model reached is past several doublings, and that the diff campaign hit both the shortcut
  and the duplicate path — a campaign that only ever exercised the steady state would pass every
  comparison and prove nothing about the clauses that matter.
* **Two committed go-red cases.** `BrokenRing` is the production ring with the wrap check removed —
  the write head runs over unread slots instead of growing. `brokenDiff` computes `toStart` without
  excluding the active keys, so a key both active and requested is kept *and* started. Each is run
  against the model over the same campaign and the number of inputs on which it is caught is asserted
  positive. A differential that has never been shown to fail agrees with whatever it is shown.

### Rung 3 — Assumed, and stated

* **The bridges are hand-written.** An op sequence crosses as its cases; a subscription list as
  key/handle pairs; the model's popped slot comes back as an `option` with a popped placeholder kept
  *distinguishable* from `None`, so it can never read as agreement. A defect in any of them would make
  the comparison compare the wrong thing. Mitigation: short and case-for-case, and the two go-red
  cases beside them are each shown to be caught.
* **The subscription key is opaque.** `SubId` is a `string list`; the model takes any type with
  decidable equality and the host instantiates it with the real one. The diff only ever asks whether
  two keys are equal, so nothing is lost — but a future diff that ordered or prefix-matched keys would
  need the model to gain the operation.

### Rung 4 — Not claimed

Named because an unstated exclusion reads, to anyone who finds it later, as a claim that failed.

* **The dispatch loop around the ring.** `Program.runWith`'s reentrancy flag, its termination
  predicate and the order in which `update`, `subscribe` and `setState` run are the loop's contract.
  The ring theorem says every deferred message comes out once and in order; what the loop does with
  it is a separate model — **Phase 789's, [below](#the-claims-ladder--the-dispatch-loop-phase-789)**,
  which claims the synchronous loop and `DispatcherCore`'s two flags. The `Cmd` combinators and the
  dispatcher's async path remain untested.
* **What a subscription does when started or stopped.** `Fx.change` calls the host's start and
  dispose functions and reports their exceptions through `onError`; the theorem is about which keys
  survive the call, with the start function opaque and its failure modelled as `None`. Whether a
  start that threw left a resource behind is the subscription author's contract. One thing a start
  *can* do is no longer out, but it is the loop's claim, not this one's: a start that calls
  `Terminate` ends the diff (Phase 900, `Fx.change`'s gate), and what that means — which starts a
  terminating diff makes, that it holds nothing afterwards, that the message's command does not run —
  is [the dispatch-loop ladder's](#the-claims-ladder--the-dispatch-loop-phase-789)
  `diff_terminating_starts_nothing_after`.

---

## The claims ladder — the dispatch loop (Phase 789)

The same four rungs, for `ElmishLoop.fst` — the model of `Program.runWithDispatch`
(`src/ToolUp.Platform.Client/Client/Elmish/Program.fs`) that Phase 788's Rung 4 named as the
missing one. The loop is a scheduling skeleton over five mutable cells — the ring, the two latches,
the model and (since Phase 851) the `dirty` flag; its transitions depend on the impure callees
(`update`, `setState`, `Subs.Fx.change`, `Cmd.exec`) only through **which messages they synchronously
re-dispatch and whether they call `Terminate`**, so those callees are abstracted as oracles and the
loop becomes a total, deterministic step function. Two oracles since 851: `update : msg -> model ->
reply` (update, subscribe, the subscription diff and the command, per message — since Phase 900 a
`reply` is the new model, the diff's *starts* one at a time, and the command, where until 900 it was
the model and one flattened event list) and `render : model -> ev list` (the render hook, once per
drain). The ring is *imported* from
`ElmishRing.fst` — `push`, `pop`, `wf`, `unread` and the 788 lemmas — and nothing about it is
restated; this proof is about the two latches and the paint.

The model carries six observables beside the cells: `trace`, the messages `update` was handed;
`log`, the messages `dispatch` accepted (pushed) — external and reentrant alike, in order; `painted`,
the model the render hook was last handed; `renders`, how many times it was handed one; and since
Phase 871 `started`, how many of the boot's gated starts were made, and `held`, how many handles the
loop holds for them. Every theorem is a statement about those, and the differential compares exactly
them.

**Phase 851 moved the render hook.** Until 851 `setState` ran after every `update`, so a drain of N
messages built the view N times; 851 calls it once when the ring is empty, with the model the drain
ended on, and the loop model was amended and re-proved rather than assumed to survive. The `dirty`
cell is set by every `update` and cleared by the paint; the drain exits only when the ring is empty
AND nothing is dirty, and a paint is followed by one more pop because the hook may have dispatched
(the events `setState` re-dispatches now arrive after the drain rather than mid-drain — the case the
differential gained). The boot paint stays explicit and unconditional, BEFORE `init`'s command, so a
hydrating renderer is handed the model the server rendered. The six theorems below hold on the
amended machine and two are added; the operator accepted the re-proof in advance (2026-09-26).

**The boot was then restated.** "BEFORE `init`'s command" turned out not to be enough. The loop set
its latch *after* the dispatcher-handle sinks, the effect-controller sinks and the effects' start
functions had run, so any of them that dispatched found the latch clear and ran a whole drain —
`update`, the subscription diff, the command, and a paint — ahead of the boot paint. The hook's
first model was then one `init` never returned, the boot painted a second time, and the boot's
subscription diff (computed from the init model before anything ran) stopped what that drain had
started. No shipped effect dispatches from its start function, so nothing in the tree was broken;
the model simply had no clause for it, having taken "after `Wire`" as the first moment anything
could dispatch and gone straight to the boot. The latch is now set before anything the program
supplied is called. The model gains `pre_evs` — the events raised before the boot paint, applied
under the latch — as a parameter of `boot` and `program`; the eight theorems were re-proved over it
and `boot_paints_init_model` is added.

**Phase 871 made termination total.** Two faults, one subject. A terminating *message* ran the
consumer's handler inside the message arm's `try`, with both flags set after it: a handler that
raised was reported and the program stayed active, handing messages to `update` with every
subscription stopped and every effect disposed (the other route, `IDispatcher.Terminate`, already
guarded the handler — the two were meant to be one). And termination was not absorbing at boot: a
sink or an effect's start function that called `Terminate` was followed by the remaining effects,
the init model's subscriptions and `init`'s command all starting — after the teardown that should
have disposed them had run — and an effect that terminated from inside its own start function was
stored in the registry *after* `DisposeAll` had emptied it. The model said a terminated machine
*processes* nothing and, with the teardown's calls abstracted, nothing about what it *starts*. Both
routes now run one `teardown` (the flag first, each callee under its own guard, `MarkTerminated` in a
`finally`); the boot checks the flag before every effect, the subscription start and the command; and
a start that terminated the program has what it returned released at once. The model gains the
`started` and `held` observables, a `start` record and the `gated` transition; the boot now takes the
sinks' events, the effects, the subscription start and the command; teardown releases every handle;
and the invariant gains `terminated ==> held == 0`. Every theorem was re-proved and two are added.

**Phase 900 split the step at the diff.** 871 reached the boot and named what it could not reach:
the same fault in the steady state. In the message arm the loop called `Subs.Fx.change` once for the
whole diff; if the first subscription to start called `Terminate` from its start function, the
teardown stopped the active set *as it stood before the diff*, `change` started the second anyway,
the loop assigned both to the stopped set, nothing ever disposed them, and the message's command
ran after. The model could not see it: the `update` oracle returned one flattened event list, so a
`Terminate` from a start was an event among events and every later event was absorbed — which is
exactly what a model of the loop's *events* should say, and nothing about what the loop went on
*starting*. `Fx.change` now takes the loop's flag and reads it before each start (`Sub.fs`, the gate
871 could not place); once set, nothing further starts, what the call started is stopped, and the
next active set is empty; the message arm runs the command only while the program is still running
after the diff. The oracle returns a `reply` — the model, the diff's starts as `start` records (one
per subscription, in the order `toStart` holds them), the command — and `step` applies them as the
boot does: `start_all` makes each start only while the program runs and holds its handle only if it
is still running when it returns, `start_opt` runs the command under the same gate. `started` and
`held` now count the message arm's starts too, every message's command counts as a start, and every
theorem was re-proved on the pinned prover with no flag change. One is added. Pinned red first on
production, one case per fault. Separately, effects now start in *attach* order (`withEffect`
prepends, as the sink registrations do, and the boot reversed the sinks but not the effects; the
accessor `effectIds` already reported attach order) — a contract with a test and a migration note,
not a theorem.

### Rung 1 — Proved

**Formally verified, on the pinned prover, with `--report_assumes error`, and spent on these lemma
families alone:**

* **`exactly_once`** — the headline. In every idle, non-terminated state a program reaches — after
  the boot drain and any sequence of external dispatches and `Terminate` calls — `log == trace`.
  Every message `dispatch` accepted, whether from a timer outside the loop or from a command inside
  it, was handed to `update` once and only once, and in the order it was accepted. The invariant it
  falls out of (`inv`) is the whole proof: while not terminated, `log` is exactly `trace` followed by
  the ring's unread contents, and an idle machine has drained its ring. `enqueue_spec`, `step_spec`,
  `loop_spec` and `dispatch_inv` are the single-transition halves it composes.
* **`in_order`.** In *every* reachable state — mid-drain, stalled, or terminated — `trace` is a
  prefix of `log`. Nothing is ever handed to `update` out of the order in which it was accepted;
  termination can only truncate the trace, never permute it.
* **`reentrant_no_loss`.** A `dispatch` made while the latch is set — from `update`'s command, from
  the post-drain `setState`, from a subscription's start, from an async command that completed
  synchronously — queues its message at the *back* of what is pending, logs it, and processes and
  paints nothing: the trace, the model, the paint and everything already waiting are untouched.
  With `exactly_once` the message is handed to `update` once the drain reaches it; with `in_order`,
  after everything accepted before it. `dispatch_latched` is the clause-for-clause fact underneath:
  under the latch, `dispatch` *is* `enqueue`. Since Phase 851 this lemma is also what justifies
  `Async.StartImmediate` as the Fable async-command start (`Prelude.fs`): the `setTimeout 1` hop
  upstream inherited protected the loop from a command dispatching back into it mid-message, and
  the latch already does that.
* **`terminated_absorbing`**, with `terminated_absorbing_boot` and `terminate_then_nothing`. Once
  `terminated`, no event from inside or outside changes what `update` saw, what was accepted, the
  model, or what was painted (`painted` and `renders` are frozen too: a terminated loop paints
  nothing further), and nothing clears the flag — the two guards at the head of `dispatch` and of
  `processMsgs`'s `while`, as a theorem. From any idle state, a `Terminate` from outside means
  nothing after it is ever processed or painted. The boot on a machine terminated before it ran
  still makes its unconditional boot paint, and processes nothing — and, since Phase 871, starts
  nothing and acquires nothing: `started` and `held` are frozen too.
* **`boot_drain_equiv`**, with `boot_single_is_dispatch`. The tail of `runWithDispatch` —
  `reentered <- true`, the sinks and the effects' start functions, the boot paint, the init effects
  through `dispatch'`, `processMsgs ()`, `reentered <- false` — is transcribed literally and then
  shown *equal* to the steady-state critical section run over the events raised before the boot
  paint, the boot paint, and then the events `init`'s effects raised, all under the latch; for a
  single message raised by `init`'s command and nothing before the paint it is the boot paint
  followed by `dispatch` itself (`booted s` — the paint with the latch released — is the state
  `dispatch` starts from, and the lemma asks that the paint did not terminate the machine). The
  hand-duplicated section and the one `dispatch` runs are one function. The source is not unified:
  the equivalence is proved, and the two copies stay.
* **`boot_paints_init_model`.** Whatever the dispatcher-handle sinks, the effect-controller sinks
  and the effects' start functions dispatch before the boot paint — any number of messages, a
  `Terminate`, both — none of it is handed to `update`, none of it moves the model, none of it
  paints; and the boot paint then hands the render hook **the model `init` returned**, once. It is
  stated over `preboot` and `boot_paint`, the two functions `boot` is built from, so it is a fact
  about the boot and not about a paraphrase of it. The prover's go-red is the old order: apply the
  pre-boot events with the latch clear and the boot's invariant no longer checks.
* **`terminated_holds_nothing`** (Phase 871), with `start_terminating_releases`. In every state a
  program reaches, a terminated program holds nothing: every handle the boot's starts acquired — an
  effect's `IDisposable`, the init model's subscriptions — and, since Phase 900, every handle a
  *message's* diff acquired, has been released, by the teardown or, for a start that terminated the
  program from inside its own start function, as soon as that start (900: that diff) returned. It is
  the invariant's new clause, so every single-transition lemma carries it: `terminate_spec` releases
  everything, `gated_spec` holds a boot start's handle only if the program is still running when the
  start returns, and `start_one_spec` (900) says the same of a diff's. The prover's go-reds are the
  two lines that fixed production: store the handle regardless (the registry before 871) and
  `gated_spec` no longer checks; leave `held` alone in `terminate` and `terminate_spec` no longer
  checks.
* **`diff_terminating_starts_nothing_after`** (Phase 900), with `start_one_terminating`,
  `start_all_no_term`, `start_all_terminated` and `start_one_terminated`. A message whose diff would
  start `before`, then a subscription `x` that calls `Terminate` from its start function, then
  `after`: `step` makes exactly `length before + 1` starts — all of `before`, because none of them
  terminates; `x`; none of `after` — the message's command does not run (it would have counted), the
  machine is terminated and holds nothing, and the new model is still assigned, as a `Terminate` from
  a command leaves it. `start_one_terminated` is the gate itself (a start reached once the program is
  terminated is not made, state unchanged); `start_one_terminating` the start that terminates
  (counted, and nothing held afterwards — `apply_evs_term_held`: a `Terminate` among a start's events
  releases everything and absorbs what follows). The go-red is the step before 900 — the flattened
  events applied by `apply_evs` — which starts `after` and the command whatever the flag says, and
  fails the lemma on the start count; the go-red on production is the three-case pin below.
* **`terminated_starts_nothing`** (Phase 871), with `gated_terminated`. A terminated program starts
  nothing. `gated_terminated` is the guard: a start the boot reaches once the program is terminated —
  an effect's start function, the subscription start, `init`'s command — is not made and the state
  is unchanged. `terminated_starts_nothing` is the boot a sink terminated: no effect starts, no
  subscription starts, `init`'s command does not run, `started` does not move, nothing is held — and
  the boot paint is still made, once, with the init model, because it is unconditional. An effect that
  terminates from its own start function stops every start after it by the same guard. The go-red is
  the boot before 871 — the start made whatever the flag says — and it fails `gated_terminated`,
  `terminated_starts_nothing` and `terminated_absorbing_boot`, each checked on its own.
* **`active_iff_not_terminated`**, with `fallback_breaks_encoding`. In every reachable state
  `DispatcherCore.active = not terminated`. The one site that sets `terminated` — since Phase 871 the
  single `teardown` — calls `MarkTerminated` in a `finally`, and `Wire` set `active` before anything
  could dispatch; the model carries the two cells in
  lockstep and the lemma says the lockstep is an invariant. The one production arm that drives them
  apart — `Dispatcher.fs`'s fallback for a `Terminate` with no callback wired, which clears `active`
  alone — is modelled as `fallback_terminate` and its result *computed*: `active` false, `terminated`
  false, a state in which `IDispatcher.Dispatch` refuses while the loop would still process.
* **`painted_is_model`** (Phase 851). In every idle, non-terminated state a program reaches,
  nothing is left unpainted and the model the render hook was last handed *is* the model —
  `not dirty /\ painted == OSome model`. This is what rendering once at the end of the drain has to
  establish that rendering after every `update` got for free: the last paint saw the last model. It
  falls out of the invariant (`inv_core` gains `not dirty ==> painted == OSome model`; `inv` gains
  `idle /\ not terminated ==> not dirty`) and is unconditional on the render oracle: a hook that
  dispatches re-dirties the model, and the drain paints again before it returns.
* **`render_once_per_drain`** (Phase 851). From an idle, non-terminated state, a `dispatch` that
  finishes without terminating hands the render hook the model **exactly once** — however many
  messages the drain processed, however many the commands re-dispatched — and a `dispatch` that
  terminates hands it nothing. **Conditional on `quiet render`**: the hook dispatches nothing
  synchronously, which is the React adapter's `setState`. Stated as a hypothesis rather than
  assumed of every hook, because a consumer's own `withSetState` may dispatch; under such a hook
  the drain re-opens and paints again, and the two unconditional theorems still hold. `loop_quiet`
  is the induction underneath: a paint is made only on an empty ring, a quiet hook leaves it empty,
  and the next pop exits — so a paint is the loop's last act.
* **The model is total, and the drain's non-termination is reported rather than hidden.**
  `processMsgs` is a `while` loop whose exit depends on the oracles eventually re-dispatching
  nothing, which nothing guarantees and production does not guarantee either (a `setState` that
  always dispatches never lets the drain return, as an `update` that does never did). The model
  bounds it with fuel, spent after a message is processed or a paint is made and before the next
  pop, and a drain that ran out leaves the latch *set* — exactly where production would be,
  mid-drain — so `run` feeds it no further external events. Every theorem above is partial
  correctness over every state the machine reaches, finished or not.

### Rung 2 — Differentially tested

Not proved. *Measured*, on every run of the gate.

* **The model agrees with production, on .NET.** `ElmishLoopProofOracleTests` in the platform pack
  runs the real `Program.runWithDispatch` with a **scripted `update` and a scripted `setState`**: a
  generated script says which messages a dispatcher-handle sink and an effect's start function
  raise *before the boot paint* (about two scripts in five raise some, and some of those raise
  `Terminate`), which messages `init`'s command raises during the boot drain, which each
  message's command raises (including `Terminate`), which messages the render hook raises when
  handed a model whose last message is a given id (the post-drain `setState` dispatch — Phase 851's
  case; about a third of the campaign has one fire, and some raise `Terminate` from the hook), which
  messages satisfy the termination predicate, and what the outside world dispatches afterwards
  through `IDispatcher` — and the same script is the extracted machine's two oracles. The two must
  agree on the messages `update` saw in order, on the messages `dispatch` accepted, on the final
  model (read off `update`'s output — the value `state` takes — since a hook that runs once per
  drain no longer sees every model), on `IsActive`, on how many times the hook was called, on
  the model it was last handed, and on the boot paint — the model the hook was handed *first*, and
  what `update` had seen by then, which the model reads off `boot_paint`. Four hundred scripts over
  ids `0..8`, replies and paints pointing forward only so every drain finishes. The pre-boot events
  are drawn from a second generator, so the scripts the campaign held before they existed are the
  same scripts with a `Pre` added. Since Phase 871 a third generator says whether the init model
  subscribes (about half the campaign) and what the subscription's start raises — `Terminate`
  included — and whether the terminate handler raises; and the two sides are compared on how many
  of the boot's gated starts ran (the effect's start function, the subscription's, `init`'s command)
  and on how many handles are still held (production counts each handle returned and each dispose).
  Since Phase 884 all of it is compared **per step** — the state after the boot and after every
  external event — and not only where the script ends. Since Phase 900 a fourth generator gives
  about a third of the messages subscriptions of their own (one or two each, each start raising up
  to two forward-pointing messages or `Terminate`), started by the diff after the message is first
  processed; keys only ever accumulate, so no diff stops anything and `held` on both sides counts
  every start that came up and was not released; and every message's command is a start, counted
  whether or not it raises anything, so a command run after its diff terminated the program is one
  start too many on production's side.
* **…and under Fable (Phase 884).** The loop every browser client runs is the Fable transpilation
  of `Program.runWithDispatch`, and it is now held to the model the same way. The script, its
  generator, both drivers, the skeleton and every comparison over the campaign are one host-neutral
  module (`src/ToolUp.Platform.Tests/Client/ElmishLoopDifferential.fs`) compiled into both packs,
  each comparison a ROW that each host turns into one case — so a new shape of script is a field
  and a draw, a new property is a row, and neither host can run a row the other does not. The Fable
  pack drives the transpiled loop beside `oracle/ElmishLoop.fs` compiled by Fable over the
  machine-integer `Prims` and run live; no verdicts are replayed. That it is the **same** corpus is
  asserted: the campaign's canonical rendering is pinned, and both hosts check the pin; the model's
  verdicts over it are pinned too, which holds the `int` shim to the `BigInteger` one over the loop
  model as Phase 850's corpus does for the ring. Shown red on the shipped code: with the paint made
  after every message in `Program.fs` (the pre-851 loop), the Fable agreement row failed on 259 of
  the 400 scripts. What does not run there is what is not the campaign — the hand-written production
  scenarios below, several of which read the process-wide `Console.Error`; they run on .NET.
* **The `log` comparison is what holds the two-flag encoding.** Production's log is recorded through
  `IDispatcher.IsActive` at the moment of each dispatch; the model's through its `terminated` cell.
  A state in which the two disagreed would log differently on the next dispatch, so
  `active_iff_not_terminated` is measured on production and not only proved on the model.
* **The campaign is known to have reached the clauses.** It asserts a counted number of scripts nest
  a re-dispatch under a sibling (the shape on which a nested drain changes the order), raise two or
  more messages from the boot drain, dispatch after a `Terminate` raised from *inside* a command,
  dispatch from outside after a `Terminate`, reach the termination predicate, and end still active —
  and that no drain stalled, so the fuel bound never decided an agreement. A second counted case
  does the same for the restated boot: scripts that dispatch from a sink, from an effect's start
  function, that raise `Terminate` before the boot paint, and that raise nothing before it. A third
  does it for Phase 871: scripts that terminate from a sink, from an effect's start, from a
  subscription's start, that subscribe at all, and that reach the termination predicate with a
  handler that raises. A fourth does it for Phase 900: scripts that ask for subscriptions from a
  message (measured 348 of 400), two from one message (250), that terminate from a subscription a
  message's diff starts (111), and that *reach* a diff which terminates with a subscription after
  the one that terminated it (25 — the shape the phase is about, counted on production's trace).
* **Termination is total, on production (Phase 871).** Five cases pin the phase's faults directly,
  and each was run red on the tree before it: a terminating message whose handler raises leaves
  `IsActive` false, a later dispatch reaches `update` zero times, and the subscription and the effect
  were disposed once, in the teardown's order; a sink that calls `Terminate` at boot means no effect,
  no subscription and no command starts, and the boot paint is still made once; an effect that calls
  `Terminate` from its own start function has its handle disposed and nothing after it starts; a
  subscription that does the same is stopped; and a handler that calls `Terminate` runs once on
  either route (bounded in the case; before the phase it recursed). Over the whole campaign, whenever
  production ends terminated it holds nothing (`terminated_holds_nothing`, run), and a handler that
  raises and one that returns produce the same run — which is what lets the model have no parameter
  for it.
* **A terminating diff ends there, on production (Phase 900).** Three cases pin the phase's three
  faults, one each, and each was run red on its own on the tree before the phase: a message whose
  model asks for `before`, `terminates` (which calls `Terminate` from its start function) and `after`
  — the subscription after the one that terminated does not start (the tree before started it); what
  the diff started before and including the terminating one is disposed as soon as the diff returns
  (before: nothing was, ever); and the message's command does not run (before: it did). A fourth
  case pins the effect start order as a contract: three effects attached `a`, `b`, `c` start `a`,
  `b`, `c`, the order `effectIds` reports.
* **Six of the theorems are also run on production directly.** `boot_paints_init_model`: a sink
  and an effect that each dispatch at boot, and an `init` command that does too, produce exactly two
  paints — the init model, then the model one drain built from all three messages in the order they
  were raised — with `update` handed nothing before the first. `boot_single_is_dispatch`: one
  message raised from `init`'s command versus the same message dispatched from outside produce the
  same trace, log, model, render count and painted model (with the boot paint quiet and nothing
  raised before it, which are the lemma's premises). `terminated_absorbing`: after a `Terminate`, further dispatches and `Terminate`s
  change nothing — paint included — and `IsActive` is false. `render_once_per_drain`: one external
  dispatch whose command fans out into a seventeen-message chain calls the hook once, with the
  model the drain ended on; and a terminating dispatch calls it not at all. `painted_is_model`: over
  the whole campaign, whenever production returns to the host still active, the model the hook was
  last handed is the model `update` last produced.
* **A subscription an early message asks for survives the boot.** The second thing the old boot
  order broke, pinned on production: an effect dispatches a message from its start function, the
  model that results asks for a subscription, and after the boot that subscription has been started
  once and not stopped.
* **The reporter is total, at every site it is reached from.** With a reporter that raises on every
  call, production is driven through a failing `update`, a failing command, a failing render hook,
  a failing sink, a failing effect start, a failing subscription start, a failing `init` command and
  a teardown whose disposables fail: no dispatch raises into its caller, every message is still
  handed to `update` in order, the boot returns with the latch released, and `Terminate` still
  disposes everything and terminates. Each of these escaped the drain with the latch set before the
  guard existed, after which every dispatch queued and nothing was ever processed again.
* **One faithful loop skeleton, nine go-reds.** The loop's scheduling skeleton is transcribed by
  hand over the production ring with the callees replaced by the script — the same abstraction the
  model makes, in F#. Faithful, it is asserted to *agree* with the model over the campaign. With one
  line moved each, it is asserted *caught*: the latch released *before* the drain instead of after
  it, so a dispatch from inside `update` recurses into a nested drain, processing a child before its
  waiting siblings and letting the outer `state <- model'` overwrite the model the nested drain
  built; the paint made after every `update`, which is the pre-851 loop (caught on the render count
  on every multi-message drain, and on the trace wherever the hook dispatches); one paint after
  the `while` with no pop after it, so a hook that dispatches leaves its message in the ring until
  some later external dispatch happens to drain it (caught on the trace); and the latch set after
  the sinks and the effects ran, which is the boot as it shipped until it was restated (caught on
  the boot paint, on every script that dispatches before it); every start made whatever the flag
  says, which is the boot before Phase 871 (caught on the start count); the handle of a start
  that terminated the program kept, which is the registry before 871 (caught on the held count); and
  Phase 900's three, the message arm before 900 — the rest of a diff started after one of its
  subscriptions called `Terminate` (caught on the start count), the terminating start's handle
  assigned to the stopped set (caught on the held count), and the command run after a terminating
  diff (caught on the start count). The difference each go-red measures is its one line. A
  differential that has never been shown to fail agrees with whatever it is shown.
* **The fallback arm, on production.** A `DispatcherCore` exposed without its terminate callback is
  driven through `Terminate`: `IsActive` goes false, the wiring defect is reported, the interface
  refuses a dispatch — the state `fallback_breaks_encoding` computes, reached on the shipped code.

### Rung 3 — Assumed, and stated

* **The two-flag encoding is faithful to production only where the model's sites are production's
  sites.** The model sets `active` and `terminated` together because the one `terminated <- true`
  in `Program.fs` — `teardown`'s, since Phase 871 — is followed by `MarkTerminated` in a `finally`,
  and `Wire` precedes every dispatch. While the teardown's own callees run, production has
  `terminated` set and `active` still set; only those callees can see it, and what they could do
  with it is refused (a dispatch meets `dispatch`'s guard; a `Terminate` finds the teardown under
  way). A second site added to either cell without the other would leave the theorem true of the
  model and false of the code. Mitigation: the `log` comparison above measures the encoding on production on every
  script; a divergence surfaces as a mismatch on the next dispatch after it.
* **The oracles abstract the callees, including their exceptions.** `update`, `subscribe`,
  `Subs.Fx.change` and `Cmd.exec` are one function returning a `reply` — since Phase 900 the model,
  the diff's starts one at a time (each the events its start function raises before it returns, and
  whether it returned a handle) and the command's events; until 900 the model and one flattened event
  list; since Phase 851 `setState` is a second oracle, a pure function of the model it is handed to
  the events it raises synchronously. An exception from `update` or `subscribe` is an oracle reply
  whose model is the old one (production leaves `state` unassigned) with no starts and no command; a
  start that throws is a start that holds nothing; a command that throws is a command carrying
  whatever it dispatched before the throw — the abstraction admits each, and the differential does
  not script them. Production marks the
  model dirty *before* the callees run for exactly this reason: a message that threw is still, to
  the model, a processed message, and its (unchanged) model is painted once at the end of the drain.
  That reply presumes the exception was *reported and the loop went on*, which is true only of a
  reporter that returns. The reporter is not a parameter of the model; production makes it total by
  construction — `runWithDispatch` calls the program's reporter only through a guard that catches
  what the reporter raises — and Rung 2 pins that at every site. The terminate handler's outcome is
  not a parameter either, for the same reason (Phase 871): the teardown calls it under its own guard
  and sets the flags regardless, and Rung 2 measures that a raising handler changes nothing. Nor is
  what the teardown's callees raise: the flag is set before any of them runs, so a dispatch they make
  is refused and a `Terminate` they call finds the teardown under way.
* **What the model abstracts about teardown and the starts (Phase 871, narrowed by 900).** What a
  released handle *does* when disposed — an effect's `Dispose`, a subscription's stop — is the
  effect's contract; the model counts handles and does not look inside them. Since Phase 900 the
  gate is per *subscription* wherever `Subs.Fx.change` runs — the boot's start and every message's
  diff — and `held` counts what both acquired; what remains at the block is `Cmd.exec`: a command's
  effects run together, so a command is one start and a `Terminate` raised by one of its effects does
  not stop the next effect of the *same command* from running (a command holds nothing, so nothing
  leaks; what is not claimed is that the later effect's dispatches are refused, which
  `terminated_absorbing` does say). What a diff *stops* is not counted: `held` never moves down
  except at teardown, so the model is exact for a program whose subscription keys only accumulate —
  the shape the differential scripts — and which keys a diff stops is `ElmishSub.fst`'s theorem. A
  stop function that calls `Terminate` is not modelled: the diff's stops run before its starts, and
  what a stop does is the effect's contract.
* **The render hook is quiet — for `render_once_per_drain` only.** The theorem is stated under the
  hypothesis that the hook dispatches nothing synchronously; the shipped React adapter (`React.fs`)
  hands the view to React and returns, and React 18 commits asynchronously. Every other theorem
  holds of a hook that dispatches, and the differential scripts one on about a third of its
  campaign so the unconditional theorems are measured on exactly the case the conditional one
  excludes. A consumer's `withSetState` that dispatches synchronously paints more than once per
  drain and is otherwise held by the same laws.
* **The bridges are hand-written.** A script's events cross as the model's `Msg` / `Term` cases and
  `XDispatch` / `XTerminate`; production's log is recorded by the driver, not by the loop. A defect
  in either would make the comparison compare the wrong thing. Mitigation: short and case-for-case,
  and the faithful skeleton beside them is a third implementation of the same abstraction that must
  agree with both.
* **The `OSome Placeholder` arm is unreachable, by the ring theorem.** `nextMsg.Value` being
  `Unchecked.defaultof` is a case the model's slot type makes explicit and `placeholder_unobserved`
  proves a well-formed ring never pops; the loop model's arm exists because the function is total.

### Rung 4 — Not claimed

Named because an unstated exclusion reads, to anyone who finds it later, as a claim that failed.

* **The `DispatchAsync` post-await recheck.** `Dispatcher.fs` re-reads `active` after the awaited
  block completes; whether a `Terminate` that lands between the await and the recheck, or between
  the recheck and the dispatch, can slip a message through is an *interleaving* question over two
  observations of one cell. This is the synchronous machine; the async path needs an interleaving
  model and is deferred to a later phase.
* **A subscription a message's diff STOPS, and a stop that terminates.** Phase 900 closed what 871
  named here — a subscription a message's diff starts after a `Terminate` in the same diff is now
  `diff_terminating_starts_nothing_after`, Rung 1 — and what it leaves is narrower: the model does
  not count the handles a diff *releases* (`stop_exactly_removed` says which keys, in the diff's own
  ladder; the loop's `held` moves down only at teardown), so a program whose diffs stop subscriptions
  is held to the model only through the campaign's shape, where keys accumulate; and a stop function
  that calls `Terminate` mid-diff is the effect's contract, not modelled.
* **Liveness of the drain.** That `processMsgs` returns at all depends on `update` eventually
  re-dispatching nothing. The model reports a drain that did not finish; it does not prove that any
  drain does, and production does not either.
* **React, timers and HMR.** What arrives from outside is modelled as a sequence of `IDispatcher`
  calls between drains. That the host actually delivers them between drains — that no external
  dispatch is delivered while a synchronous drain has not returned — is the single-threaded host's
  contract, assumed by the model's `run` and not by anything it proves. React's render scheduling,
  timer coalescing and HMR's `Terminate` / re-run are all instances of that contract, not of this
  theorem.
* **The `Cmd` combinators and the subscription effects.** What a command *does* between being
  handed `dispatch` and returning — `Cmd.OfAsync`, `Cmd.map`, `Cmd.batch` ordering — and what a
  subscription's start does, are the callees the oracle abstracts. The theorem is about what the
  loop does with what they raise, not about what they raise.

---

## The verified-implementation spike (Phase 850)

Every theorem above is about a **model** — a clause-for-clause transcription of the shipped F#,
held to production by a differential test. That tie is what a performance rewrite of the runtime
breaks on purpose, and the question the operator asked (2026-09-26) was whether the stronger shape
is reachable: a **verified implementation**, where the code the prover checked is the code that
runs, on .NET and in the browser. Three things stood between an extraction and production, none of
them a theorem. The spike took the ring — the smallest model — through all three on the pinned
release. Two stood; the third did not, and the blocker has a name.

**Layout — stood.** The F# backend prints a verbose, OCaml-shaped dialect: `begin … end`, `let … in`,
match arms and `if` at column 0 whatever their nesting. F# 8 rejects it (FS0058, 100 errors before
the compiler stops), and the two ways of not fixing it are both closed: Fable reads no
`--strict-indentation-` from an fsproj, and F# 10 refuses the verbose-syntax directive outright —
`#light "off"` is FS1205, "no longer supported" — so there is no per-file escape either. Fantomas
cannot parse the raw output. What works is `normalise-extraction.fsx`: a parser for exactly the
constructs the backend emits and a printer for indentation-clean F# — `begin`/`end` become
parentheses, every block starts a line at its depth, every parenthesis the extractor wrote is kept
so no precedence moves, and every atom is carried through verbatim. It is deterministic (the leg
byte-diffs its output), it refuses a shape it does not know rather than guessing, and it refuses its
own output (so a double pass cannot happen quietly). All eight committed extractions were re-emitted
through it; the oracle project dropped the strict-indentation opt-out and FS0058; every differential
host is green on .NET, unchanged.

**Representation — stood.** `Prims.int` is `BigInteger` in `oracle/Prims.fs` because the decoder
model needs the `uint64` ceiling; the Elmish models need only indices. `oracle/fable/Prims.fs` is the
same five names over `System.Int32`, and `oracle/fable/ToolUp.Remoting.Proofs.Oracle.Fable.fsproj`
compiles the committed `oracle/ElmishRing.fs` — the same bytes the .NET oracle compiles — against
it; `ToolUp.AI.Client.Tests` references that project, Fable transpiles both files with the rest of
the client tier, and the pack runs the ring model **directly** for the first time: it reproduces the
corpus the .NET host wrote (the two shims held to each other on 200 sequences), agrees with the
transpiled `Ring.fs` on a second campaign of 120 sequences the corpus never recorded, and catches
`BrokenRing`. The machine-integer shim is a stated assumption, on the ladder as
`elmish-fable-shim-machine-integers`: the theorems are about mathematical integers, and hold exactly
as long as no index or length exceeds `Int32.MaxValue`.

**The array-backed ring — did not stand.** The ring model's backing array is a slot **list**, and an
extraction of that shape is correct and slow; a verified implementation needs the F\* source over a
structure whose extraction is fast. On the pinned release (`v2026.09.06`) there is none the F#
backend can realise:

* **`FStar.Seq` is a list.** `ulib/FStar.Seq.Base.fst` declares `type seq a = MkSeq of list a`;
  `index` is `List.index`, `upd` rebuilds the prefix, `create` is a cons loop. It is a
  *specification* type — constant-time in Low\* only through KaRaMeL's C buffers — and the F#
  backend extracts it as exactly that: a probe module over `Seq.create` / `upd` / `index` /
  `slice` / `append` checked and extracted cleanly, and `--extract 'SeqProbe FStar.Seq.Base
  FStar.List.Tot.Base'` produced a list-backed `FStar_Seq_Base.fs` referencing `FStar_List_Tot_Base`.
  So rewriting the ring over `FStar.Seq` changes its spelling and not its asymptotics: re-proving the
  six theorems over it would have produced a second correct-and-slow model, which is the shape the
  question was asking to escape. It was not done, and that is the deliberate part of the answer.
* **The pinned `ulib` ships no mutable array or state module at all.** `FStar.ST`, `FStar.Ref`,
  `FStar.Array`, `FStar.HyperStack.ST`, `FStar.Monotonic.Heap` and `LowStar.Buffer` are absent from
  `lib/fstar/ulib/` (only the OCaml runtime stubs `FStar_ST.ml` / `FStar_Heap.ml` survive under
  `ulib/ml/app/`, and the F# backend has no runtime directory of its own). A mutable ring cannot be
  *stated* against this release without pulling the effect modules from elsewhere, and its theorems
  would move from linear arithmetic over indices into a heap logic. `FStar.ImmutableArray` is
  present but is an interface (`val t`, "implemented in OCaml by an array"), has no `upd`, and no
  F# realisation.
* **Machine integers buy nothing here.** A probe over `FStar.UInt32` extracts to references to a
  `FStar_UInt32` module — `add`, `mul`, `v`, `uint_to_t` — with every literal routed through
  `uint_to_t (Prims.parse_int "1")`: a hand-written shim per host, and no representation the
  machine-integer `Prims` above does not already give.

What "yes" would have needed is therefore not a better proof but a different toolchain: an F\*
release with a stateful array the F# backend can realise (the release does ship Pulse, under
`lib/fstar/pulse/`, but it targets C and Rust through KaRaMeL, not F#), or an axiomatised array
interface realised by hand on each host — at which point the ring's O(1) rests on unverified host
code and every theorem becomes conditional on the axioms, which is the refinement tie wearing a
different coat.

**The measurement.** The extraction that *can* be built, against the shipped `Ring.fs`, on both
hosts, with a local stopwatch (Phase 849's harness was in flight): capacity 10, the same 4,000-op
sequence at 65 % pushes (the ring grows through several doublings), outputs asserted equal before
timing. On .NET, `Ring.fs` 141–175 ns/op against the model's 177–187 µs/op — a ratio of
1,000–1,300×; under Fable on node, 466–480 ns/op against 366–377 µs/op — about 785× (two runs,
2026-09-28). **Corrected by Phase 884:** the figure first recorded here, 994 ns/op against 2.62 ms/op
(2,600×), was not over the same sequence — the Fable host's LCG drew 0 after its first few draws (see
Rung 2 of the Elmish runtime ladder), so that arm ran nearly 4,000 pushes and almost no pops and grew
the ring to thousands of slots, and its agreement assertion compared two all-but-empty lists. The
falsifier below said the arms ran "the same sequence"; it was not checked, and it was false. The falsifier is stated
because a measurement without one is not a measurement: both arms assert the same popped values
from the same sequence, so neither skipped its work; the per-op figures scale with the sequence
length and are dominated by the model's O(n) `set` / `nth` over a list of a few hundred slots, so a
ratio that vanished would mean the list model had stopped being a list; and the Fable arm, which
carries no `BigInteger`, is the *worse* ratio, so the gap is the representation and not the
arithmetic. The cases are `Phase 850 - the extracted ring against the shipped ring, measured
(informational)` in both packs; they assert agreement and print the figures, never a threshold.

**The answer, as policy.** The road to a verified runtime runs on the **refinement tie**: the F\*
model stays the specification, the shipped code stays the implementation, and a rewrite of the
runtime — the ring, the loop, anything Phase 849's budgets send someone after — is held to the model
by the differential on both hosts, which this phase made stronger on the browser side, not by
replacing the shipped code with an extraction. Recorded in [`../proofs.json`](../proofs.json) as
`verified-implementation-road`. What would reopen it is named above and is a toolchain event, not a
proof one; until then, the ladder's Rung 2 is the tie, and it is what the two go-red cases and the
grow-step floor exist to keep honest.

## Method, and where it comes from

The method is not new. An open-source F# wire decoder whose combinators were proved total in F\*
established it — a hand-written model over an abstract value, extracted and run as a differential
host against the production implementation — and this directory inherits its findings rather than
rediscovering them. They are recorded here because each one cost a build to learn and each will be
met again by anyone touching this file:

* Proof hints no longer exist; the pin, the `z3rlimit` margin and `--quake` replace them.
* The F# backend ships no runtime, so a `Prims` shim is required.
* The emitted F# uses pre-F#-8 indentation. Until Phase 850 the oracle project — and only that
  project — relaxed strict indentation and silenced `FS0058`; since 850 the leg re-lays the
  extraction out instead (`normalise-extraction.fsx`), and no project carries the flag.
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

And three from the later models — the disclosure fold (Phase 790), the first second module and so
the first to find out what the leg had assumed about there being one, and the tool gate (Phase 793):

* **A recursive ghost predicate that sits under `/\` must return `prop`, not `Type0`.** `each_rank_true
  ranked offset rest` as a `Tot Type0` conjunct fails with "Expected type Prims.prop but … has type
  Type0"; declaring the predicate `Tot prop` is the whole fix, and `True` / `False` / `==` / `==>`
  all sit happily inside it. A predicate that needs no `==` (a check on an `eqtype`) is better as a
  `bool` anyway.
* **`effect` is a keyword.** F\* reserves it for effect declarations, so a type named for the
  thing the third model is about would not parse (`Syntax error` at the type's first constructor,
  with nothing to say why). The model calls it `tool_effect`; the F# side keeps `ToolEffect`.
* **Nothing in the leg should name a module twice.** The first version of `check.ps1` carried the
  module name in the pin's `extract` flags *and* in the script's steps; the second module would have
  meant a second flag set. The pin now carries only `--codegen FSharp`, and the script appends
  `--extract <module>` per entry of one `$modules` list — source, committed oracle, host list and
  case floor — so a third model is one entry and no other edit.

And two from the model-input model (Phase 792), which confirmed the sentence above — one `$modules` entry, no flag change, no other edit to the leg:

* **`introduce … ==> …` no longer binds a name for the hypothesis.** The older `with h. e` spelling
  is a syntax error on the pinned release, which says so and names the fix: write `with e`, and the
  hypothesis is available in `e`'s proof context. Worth knowing because the two proof styles are
  otherwise indistinguishable in published examples, and the error arrives at parse time with a
  line number pointing at the `with`.
* **An F\* module whose name collides with a production module is a real hazard, not a cosmetic
  one.** This model is `ModelInput`, and so is the shipped module it is about; the extraction is a
  top-level F# module in the global namespace, so after the host's `open` of the production
  namespace the bare name resolves to production. A host that got this wrong would compare
  production with production and pass. The host therefore binds a module abbreviation *before* that
  `open`, where the name is unambiguous, and every model call site reads through it. Naming the
  model something else would also have worked and was rejected: the model should be named for what
  it models, and the alias documents the hazard at the one place it could bite.

---

And three from the taint-flow model (Phase 795), the first to carry mutually-recursive *definitions*
rather than only mutually-recursive proofs:

* **An attribute cannot precede `and`.** `[@@ noextract_to "FSharp"]` on the second binding of a
  mutually-recursive group is a bare syntax error, so a ghost predicate defined by mutual recursion
  cannot be marked ghost the usual way. The fix is better than the workaround: every such predicate
  here is *decidable*, so it returns `bool` and extracts like any other definition — and the
  differential host then computes its sample-adequacy guards with **the model's own predicates**
  instead of a second implementation of them that would be free to disagree. A predicate that
  genuinely cannot be decided (the flow lemma's "these two assignments agree away from this
  contributor", which quantifies over all sources) stays `prop`, and is not recursive, so the
  attribute goes where it always did.
* **A record whose fields are functions needs `noeq`.** F\* derives decidable equality for a record
  type by default and fails on the first arrow field, reporting it against the *field* rather than
  the type — which reads as a problem with the function until you notice every field is one.
* **The extractor silently renames a type that collides in scope.** `event` came out as `event1`,
  compiling perfectly and leaving the host referring to a name the model does not contain. Renamed at
  source, so the extraction and the `.fst` agree; worth a glance at the emitted top-level names after
  any new model, because nothing fails when this happens.

And four from the verified-implementation spike (Phase 850), the first to touch the leg's shape
rather than add a module to it:

* **`#light "off"` is gone.** F# 10 refuses the verbose-syntax directive (FS1205, "no longer
  supported"), so the OCaml-shaped layout the backend emits has no per-file escape; the flag the
  oracle project used to carry was the last one, and Fable reads none. A re-layout is the only route
  that reaches both hosts, and `normalise-extraction.fsx` is it.
* **A continuation line at the offside column is a sequence separator.** `(f a` newline `b)` with
  `b` under `f` parses as `f a; b`, silently; the normaliser therefore puts every continuation four
  columns in and lets only the permitted undentations — match arms, `else`, `then`, a let's body —
  sit at their construct's column. A record whose field holds a structural value is laid out one
  field per line with an explicit `;`, or the match in the first field swallows the second.
* **`FStar.Seq` extracts as a list, and the pinned `ulib` has no mutable array.** `seq` is
  `MkSeq of list`; `FStar.ST` / `FStar.Array` / `LowStar.Buffer` are not in the release; the F#
  backend ships no runtime for anything. A model that needs constant-time indexing has nowhere to
  get it from on this toolchain — the section above is the record.
* **The F# backend names a foreign module with underscores.** `FStar.Seq.Base.create` comes out as
  `FStar_Seq_Base.create`, `FStar.UInt32.add` as `FStar_UInt32.add`; a shim for either would be a
  module of that spelling. `Prims` is the one module whose name has no dots, which is why the shim
  never had to know this.

The 790 note above predicted a third model would be one `$modules` entry and no other edit. That
held for the leg itself. It is not the whole cost of a model: the oracle project gains a `<Compile>`,
`.fantomasignore` gains the generated extraction, and the differential host is registered in the test
pack like any other — four lines in four files, none of them the proof leg.

## When the byte-diff fails

It means a committed `oracle/*.fs` is not what the prover produces from the current `.fst` beside
it. That is the expected state after any model edit, and the fix is to copy the
fresh extraction over the committed one and commit the two together — `check.ps1` prints the exact
command and the first forty lines of the diff. It is *not* a state to resolve by editing the
extraction: the next run would simply report it again.

## Licence

Apache-2.0, like everything beside it. See [`LICENSE`](../LICENSE).
