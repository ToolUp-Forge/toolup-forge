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
string is right" is settled by the type at every door rather than taken on trust, and since
Phase 821 at the one shipped site that assembles a disclosed fact as well (what remains, the scopes
the platform carries rather than resolves, is stated on the ladder); anything on the *outbound* side — the transport, what a model answers,
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

**The MVU runtime's evidence lives with the runtime.** The client tier's MVU runtime was forge's own
until it moved to the published `Fuaran.MVU` package. The models of its ring buffer, subscription
diff, dispatch loop and array ring, their extractions and both differential hosts left forge with it:
the published package carries the evidence for its queue, its keyed resource diff and its loop in its
own repository, and no claim in [`../proofs.json`](../proofs.json) names it.

**The machinery**, because a theorem about a model is worth what the tie to the code is worth:

| File | What it is |
|---|---|
| `RemotingDecode.fst` | the decoder model — `Decode.fs` clause for clause, each definition naming its F# counterpart, every refusal message reproduced verbatim |
| `DisclosureFold.fst` | the disclosure model — `DisclosureEgress.evaluate` and `PopulationDisclosure.fold` / `valuesWithheld` / `disclosedStats` clause for clause, each definition naming its F# counterpart |
| `ToolGate.fst` | the tool gate model — `ToolGate.decideDeclared`, `AIToolRegistry.ListAccessible`, `FindByName` and the dispatch re-check clause for clause, each definition naming its F# counterpart; RBAC and grant liveness taken from the host as predicates |
| `ModelInput.fst` | the model-input model — the closed value, its smart constructor, the assembly a caller folds over it, and `render` clause for clause, each definition naming its F# counterpart |
| `TaintFlow.fst` | the taint-flow model — the label lattice, `AssemblyLabelling.contributedBy` / `label`, `DisclosureTaintConfig.routineClears` and the derivation walk clause for clause, each definition naming its F# counterpart |
| `fstar-pin.json` | the pinned prover (an F\* release, which bundles Z3), with its hash, and three flag sets: the check, the one extraction route (Custard's F# backend, one whole-program run split per module — since **Phase 957** the only route the release has), and `--quake` |
| `check.ps1` | the whole proof leg, over a module list: resolve the pin; check each module; extract all five in ONE run of Custard's F# backend (`--codegen FSharp --custard_split`, every module a root); drop FS0025 from the `#nowarn` line Custard heads each module with and cut the Fable host's support stand-in out of the generated library; byte-diff every generated file and the stand-in against the committed copies; build Custard's project as generated, the .NET oracle and the Fable-host oracle; run each module's differential host (**Phase 957** — until it the eight first-order models went through the legacy printer and a layout normaliser, both retired) |
| `oracle/custard/` | **generated, Phase 957** — the whole program Custard's F# backend writes for the five models in one run: one F# module per model under the model's own name (`RemotingDecode.fs` … `ElmishRingArray.fs`), the support library `FStarCustard.fs` every module opens, and `Custard.fsproj` listing them in compile order. Every file is byte-held by the leg and the project is built as generated; committed so the repository never needs a prover to build. The one edit the leg makes to Custard's output (**Phase 1037**) is to drop FS0025 from the `#nowarn` line Custard heads each module with, so every extraction is under the incomplete-match rule; the other ids on that line are Custard's and stay. Until Phase 957 the eight first-order models were committed one level up, as the legacy printer's output re-laid by the normaliser, and the array ring alone lived here with a project of its own |
| `oracle/ToolUp.Remoting.Proofs.Oracle.fsproj` | the .NET oracle the hosts reference: the same committed files Custard's project lists, in Custard's order (the leg asserts the two lists agree), compiled under `oracle/Directory.Build.props`, which states FS0025 as an error |
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
* **The support library is the extractor's.** `Prims.int` is `bigint` and the operators, `strcat`
  and `string_of_int` the extraction calls come from the `FStarCustard.fs` Custard writes beside it —
  generated, byte-held and compiled as generated, so there is nothing hand-written between the
  prover's output and the host. (Until Phase 957 the legacy printer shipped no runtime and a
  nine-name hand-written `Prims.fs` stood in; its being small enough to read in a minute was the
  mitigation.) What is trusted is Custard's realisation of `Prims`, which is the extractor's own
  trust, stated once as `toolchain-trusted`.
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
* **The emitter.** The model is about the reader; what the writer emits is outside it. Until
  Phase 802 that left one narrowing unrefusable by construction: `writeInt64` compacted
  `2147483648L` into bytes that *are* a well-formed `int32 -2147483648`, and the information rule
  admits a 32-bit source into a 32-bit target. Phase 802 closed it at the writer — every integer
  keeps its format's top bit clear unless no wider format exists — so the value now arrives as a
  64-bit source and is refused. The rule itself is unchanged and still admits the same-width form,
  because a pre-802 peer still sends it; that is a wire-compatibility property of the writer, tested
  in the remoting suites, not a claim of this proof.
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
  refuses the pre-797 spellings, go-red pinned. Phase 821 carries the same instrument to the
  assembly side. The answer planner — the shipped caller that resolves and gates facts for the
  model outside the three tools — and its clause-planner twin take the `ResolvedScope` and reach
  the store and the gate only through their `ResolvedScope` members (a recording store and gate
  pin it, and pin that the string form kept for one release takes the string members, so the
  probe distinguishes the two). The one shipped construction of a `DisclosedFact`,
  `ModelInput.disclose`, writes `Scope` from `ResolvedScope.scopeId`, and a source scan over every
  non-test source refuses any other expression in that field — a literal, a derived identity, a
  copy-and-update — go-red pinned on a planted copy-and-update, and asserting it sees the
  sanctioned site so it cannot pass on a tree it no longer understands. `DisclosedFact.Scope`
  stays a string, because the model compares strings; what changed is that no shipped code
  writes any other string there. What it does NOT cover is stated on Rung 3.

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

* **Scope resolution — retired to Rung 1 by Phases 797 and 821, with this residual.** Until
  Phase 797 this bullet read: the scope is a caller-supplied string, compared and never derived,
  so everything above was conditional on that string being the one the caller is entitled to. At
  every fact door that is now settled by the type (Rung 1, last bullet). Phase 797 left one
  residual of the assembly's shape — a caller assembling a `ModelInput` *outside* the doors
  writes the `DisclosedFact.Scope` string itself — and Phase 821 discharged it: the one shipped
  construction writes the resolver's scope and a source scan refuses any other (Rung 1, last
  bullet). A test fixture may still write any string, by design: the differential feeds the
  model's own. What remains is the platform's *carrying*. Behind the doors, the scopes the platform
  *carries* rather than resolves are still strings unless they ride the platform's carrier, and
  Phase 818 names them: a job scheduled with a resolver-minted scope now runs under it. Since Phase
  935 the scope is written down as a token the platform's `ScopeCarrier` issued from a
  `ResolvedScope` and bound to the work it was issued for, and re-minted through the internal
  carried mint, whose one caller (the carrier) is pinned. So both shipped schedulers, the in-process
  default and a companion outside the server tier, run such a job under its scope across a restart,
  and a publication grant's target consent is restored into the scope it was given in. Since Phase
  930 the job-admin API schedules under the request's resolved scope, and the reactive recompute
  path runs under the scope a data write was made under whenever that write was made on a request
  that resolved its shard (the resolver's own value rides the change; nothing is built from the
  write's string). Still carried as a string: the
  reactive recompute of a write made off the request path or into another shard, imports, coherence
  sweeps and knowledge-base dependency records.
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
  constructors in the same sequence. Since Phase 821 the assumption is narrower: the fold's
  *shape* is the caller's, but the scope string each folded fact carries is not — the one shipped
  construction writes it from a `ResolvedScope`, scan-pinned.
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

## The eight models move to Custard (Phase 957)

> The four Elmish models this section moved — `ElmishRing`, `ElmishSub`, `ElmishLoop` and
> `ElmishRingArray` — later left forge with the runtime they modelled (see the top of this file), and
> so did the sections that recorded them: the runtime's and the dispatch loop's ladders, the
> verified-implementation spike and its two later answers. Their corpus and the Fable host's
> oracle went too. What follows is kept as the record of the move, as written.

Phase 955 kept the eight first-order models on the legacy printer and named the trigger for moving
them: a release in which every model's Custard project builds as generated. This section is that
move, dated 2026-10-29, on the pin it moved to (`v2026.10.11`, commit `fb6ea91e`). Phase 955's table
and decision above stand as written, marked where this release makes a sentence of theirs false.

**The pin.** `v2026.10.11` is the first weekly release carrying the two fixes Phase 955 reported —
[FStarLang/FStar#4626](https://github.com/FStarLang/FStar/pull/4626) (a record field's value printed
past its label) and [#4627](https://github.com/FStarLang/FStar/pull/4627) (a point-free polymorphic
function definition eta-expanded on the F# backend) — both ancestors of the release tag by commit
ancestry (`cd33b5dd`, `a72e1036`); there was no release between 2026-09-27 and 2026-10-11. The
Windows archive's SHA-256 (`de1f0bfc…d7b85`) was computed locally and equals the digest the release
publishes. The release still bundles Z3 4.13.3, and all nine models check on it over three cold runs
under `--quake 3` with no flag change.

**What the release took away, and why 957.A could not run as written.** The phase asked for the
legacy route to be run first on the new pin, three cold runs, expecting every extraction
byte-identical. On this release that run is impossible: upstream
[#4653](https://github.com/FStarLang/FStar/pull/4653) (merged 2026-10-10) *made Custard the only
extraction backend*. `--extract` is an unrecognised option, `--custard_backend` is gone, and
`--codegen` takes `OCaml | FSharp | KrmlC | KrmlRust | C | Plugin` — Custard's targets — so
`--codegen FSharp`, the spelling this directory had used since Phase 787, now names Custard's F#
backend. The deprecation Phase 955 put on the ladder as a risk happened one release later than the
sentence predicting it. So the migration is not a choice this phase made on green; it is the only
route the pinned prover has, and the measurement below is what says the route is sound. The last
green run of the legacy route is Phase 955's, on `v2026.09.27`, recorded above.

### The nine models on Custard, on `v2026.10.11`

Every model through `--codegen FSharp --custard_split`, rooted as a module (`--custard_entry_module`),
in ONE whole-program run over all nine sources (the table in Phase 955 was per module, and rooted
every definition by name; since upstream #4617 a module root emits generic definitions too, so
naming them is no longer needed):

| Model | a module root emits | .NET 10, project as generated | Fable, over the stand-in |
|---|---|---|---|
| `RemotingDecode` | 72 of 72 | builds | not compiled under Fable (its host is .NET) |
| `DisclosureFold` | 14 of 14 | builds | not compiled under Fable |
| `ToolGate` | 9 of 9 | builds | not compiled under Fable |
| `ModelInput` | 28 of 28 | **builds** (FS0058 gone: #4626) | not compiled under Fable |
| `TaintFlow` | 36 of 36 | builds | not compiled under Fable |
| `ElmishRing` | 15 of 15 | builds | transpiles, runs live in the Fable pack |
| `ElmishSub` | 12 of 12 | builds | not compiled under Fable (the Fable pack replays its corpus) |
| `ElmishLoop` | 26 of 26 | builds | transpiles, runs live in the Fable pack |
| `ElmishRingArray` | 13 of 13 | builds | transpiles, runs live in the Fable pack |

The counts are top-level `let` and `and` bindings in the generated module. Custard emits what the
program reaches: the legacy printer's `uu___is_*` discriminators and `__proj__*` projectors are not
definitions of the model and are not emitted; every definition the model declares is. `RemotingDecode`
emits all 72 where Phase 955 saw 7 refused (#4627); `ModelInput`'s project builds where Phase 955 saw
FS0058 (#4626). All nine modules also transpile under Fable when compiled, as a measurement, against a
copy of the support library cut to the parts Fable accepts; the three the Fable pack *runs* are the
ones its project compiles.

**What the names became, and what the hosts bind.** `--custard_split` writes one F# module per F\*
module under its own name and refers across modules as `ElmishRing.push`, so a host that bound
`ElmishRing.push` before binds `ElmishRing.push` still; the mangled globals Phase 955 measured
(`elmishRing_succ`, `elmishRingArray_push`) are the single-file spelling, which this leg no longer
uses. What did move, and every host bridge was rewritten for: an inductive with ONE constructor and
named fields is an F# record (`pair` / `triple` / `quad` are `{ first; second; … }`, `ElmishSub`'s
`diff_result` and `TaintFlow`'s `derivation` are records, not `Diff (…)` / `DNode (…)`); a record with
ONE field is collapsed to that field (`TaintFlow.routine` is the `list string` it wrapped); a
definition the legacy printer thunked for the value restriction (`as_int32 ()`) is eta-expanded over
its value argument instead (`as_int32 v`); and `Prims.int` is `bigint` on both hosts. Case counts are
unchanged on every host (10 / 7 / 8 / 10 / 12 / 22 / 41 on .NET), the go-red cases still go red, and
the corpus under `tests/elmish-proof-corpus/` did not move: the .NET host's verdicts were already
computed over `BigInteger`, and `bigint` is the same integer.

**The one model edit.** Custard emits no projectors, so the matches Phase 1035's normaliser completed
are gone with it — but one incomplete match is the model's own: `RemotingDecode.item_at` matched a
list whose `[]` case its refinement (`position < count xs`) excludes, and F\* erases the refinement,
so the F# compiler sees a match with no `[]` arm (FS0025, an error under `oracle/Directory.Build.props`).
It is respelled over `List.Tot.hd` / `List.Tot.tl`, which Custard realises in the support library, with
the same statement, the same refinement and the same theorems (`lemma_item_at_smaller`,
`lemma_element_of_characterised` and everything above them check unchanged). The rule this sets: a
match F\* leaves incomplete because it erased the refinement that made it total is completed in the
MODEL by a respelling that changes no theorem, never by a `#nowarn` and never by hand in the output.

**The route change in the leg.** One Custard invocation replaces eight legacy extractions, one Custard
extraction and the normaliser: `--ext fly_deps=0` lets the run take all nine sources (the default
dependency mode admits one file), every module is a root, and `--custard_split --odir` writes the
modules, `FStarCustard.fs` and `Custard.fsproj` into one directory, now committed as `oracle/custard/`.
The byte-diff is over every file Custard wrote AND over the directory listing — a model that came to
reach a `ulib` definition would make Custard write a tenth module, which a fixed list would not see.
The one edit stays (the FS0025 token off each module's `#nowarn` line, Phase 1037). The .NET oracle
compiles the committed files in Custard's order and the leg asserts its list equals Custard's. The
module list's ORDER is now load-bearing for the bytes as well as for the imports: Custard orders the
files it writes by the command line, and a reordered list moves declarations between files.

**The Fable host's stand-in is derived, not written.** Custard's support library does not transpile
(Fable refuses `System.Int128` / `System.UInt128` arithmetic, `TextWriter.Write` / `Flush`, `exit`, the
`UTF8Encoding` constructor), and it is one module, so the refusal is of the whole file. The three
Elmish extractions the Fable pack compiles reach only its `Prims` block, so the leg CUTS that block
out of the generated file — verbatim, under a fixed header — writes it to `oracle/fable/FStarCustard.fs`
and byte-holds it. Phase 955's stand-in was hand-written and empty; Phase 850's `oracle/fable/Prims.fs`
was a hand-written shim over machine integers, and the `assumed` row `elmish-fable-shim-machine-integers`
existed because it was. Both are gone: the Fable host now runs the prover's integers, and the row is
restated as `tested` — the corpus replay and the two fingerprint pins hold Fable's transpilation of
each model to the .NET run of it, which is what they always measured once there was no shim to differ.

**What retired.** `normalise-extraction.fsx` (759 lines), `oracle/Prims.fs` and `oracle/fable/Prims.fs`
(the two shims), the eight legacy extractions under `oracle/`, and the array ring's own generated
project. What joined the trusted base is nothing new: Custard's F# backend was already trusted for the
array ring (`elmish-array-ring-extractor-trusted`), and now carries the eight as well, with
`toolchain-trusted` naming it. What left the trusted base is 759 lines of this repository's own
layout parser and two hand-written runtimes.

**Shown red.** A tampered generated module fails the byte-diff; a tampered `FStarCustard.fs` fails it;
a tampered derived stand-in fails it; a module file added under `oracle/custard/` that Custard did not
write fails the listing check; and each host's committed go-red case still catches the bridge or
model it was written to catch.

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
  extraction out instead (`normalise-extraction.fsx`), and no project carries the flag. Since Phase
  957 there is nothing to re-lay: Custard's output compiles as written, and the normaliser is gone.
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
  that reaches both hosts, and `normalise-extraction.fsx` is it (retired by Phase 957 with the printer
  it served).
* **A continuation line at the offside column is a sequence separator.** `(f a` newline `b)` with
  `b` under `f` parses as `f a; b`, silently; the normaliser therefore puts every continuation four
  columns in and lets only the permitted undentations — match arms, `else`, `then`, a let's body —
  sit at their construct's column. A record whose field holds a structural value is laid out one
  field per line with an explicit `;`, or the match in the first field swallows the second.
* **`FStar.Seq` extracts as a list, and the pinned `ulib` has no mutable array.** `seq` is
  `MkSeq of list`; `FStar.ST` / `FStar.Array` / `LowStar.Buffer` are not in the release; the F#
  backend ships no runtime for anything. A model that needs constant-time indexing has nowhere to
  get it from on this toolchain — the section above is the record. **Corrected by Phase 955:** on
  the release of 2026-09-27 it has: `Pulse.Lib.Vec`, realised as a .NET array by Custard's F#
  backend. `FStar.Seq` still extracts as a list, on either route.
* **The F# backend names a foreign module with underscores.** `FStar.Seq.Base.create` comes out as
  `FStar_Seq_Base.create`, `FStar.UInt32.add` as `FStar_UInt32.add`; a shim for either would be a
  module of that spelling. `Prims` is the one module whose name has no dots, which is why the shim
  never had to know this.

And six from the second spike (Phase 955), the first to add a second ROUTE to the leg:

* **A Pulse module checks with the flags every other model uses.** The Pulse library is on the
  release's own include path; `--report_assumes error`, `--z3rlimit 60` and `--quake 3` apply
  unchanged. Keeping the diagram lemmas pure — over the existing list model, called from the Pulse
  functions — kept every query small: the slowest used half a percent of the limit.
* **`--custard_entry_module` roots nothing polymorphic.** It emits the monomorphic definitions of a
  module and is silent about the rest, which for a generic model is an empty file and exit 0. Name
  the definitions (`--custard_entry M.f`); they come out generic.
* **Custard writes a project, so the byte-diff is over three files.** The support library and the
  `.fsproj` are the prover's output as much as the module is. Its output is deterministic and
  LF-only, so it is compared as written, with no normalising step to hide a change in.
* **Custard's layout is not yet to be trusted unseen.** One of the eight existing models comes out
  with a `match` in a record field laid out offside (FS0058) — the shape the normaliser's own note
  above says it had to learn. The leg builds a Custard module's project as generated for that
  reason.
* **`SizeT` promises 16 bits and no more.** An array that grows needs its new size shown to fit,
  and without a platform assumption only sizes below 2^16 are. The choice is a ceiling in the code
  or an assumption in the statement; the array ring takes the ceiling and says so.
* **A proof that passes first time is the one to break on purpose.** Every function of the array
  ring checked on its first run. Ten one-line mutants were then run through the prover and each was
  refused, which is the only evidence that the statements constrain anything.

And seven from the migration (Phase 957), the first to take every model through one extractor run:

* **The legacy printer is gone, not deprecated.** `v2026.10.11` has no `--extract`, no
  `--custard_backend` and no `--codegen Custard`; `--codegen FSharp` IS Custard's F# backend. A pin
  move across that boundary is a route change whether or not the phase that moves it intended one.
* **One run takes many files only under `--ext fly_deps=0`.** The default dependency mode refuses a
  second source on the command line ("only one file can be provided"); with the extension off, nine
  sources and nine `--custard_entry_module` roots are one whole-program run, and the output is
  byte-identical to the same run driven from a tenth module that `open`s the nine.
* **`--custard_split` keeps the names.** Per-module output under the module's own name, cross-module
  references as `Module.name`, no `open` between generated modules; the mangled globals are the
  single-file spelling only. What still moves: one-constructor inductives become records, one-field
  records collapse to the field, unit-thunked definitions are eta-expanded over a real argument.
* **The command line's order is the files' order.** Custard writes `Custard.fsproj` in the order the
  roots were given and relocates declarations by that order, so the same module list in a different
  order moves bytes the diff holds. The list in `check.ps1` is the order, and it is the only one.
* **Custard emits reachable `let`s; mutual `and`s ride with their group.** Counting `^let` under-counts
  a module with mutually recursive definitions (`taint_of` and its three siblings are `and`-bound);
  the hosts bind them unchanged.
* **An erased refinement is the model's FS0025.** Custard emits no projectors, so Phase 1035's
  completion of them is moot — but a match whose missing arm a refinement excluded is incomplete in
  F# whatever the printer, and the fix is a respelling in the `.fst` that changes no theorem
  (`item_at` over `hd` / `tl`), never a `#nowarn`.
* **Derive the stand-in, do not write it.** A hand-written stand-in for a generated library is an
  assumption on the ladder for as long as it exists; a stand-in the leg cuts from the generated file
  and byte-holds is a projection, and the ladder row it carried becomes a test.

The 790 note above predicted a third model would be one `$modules` entry and no other edit. That
held for the leg itself. It is not the whole cost of a model: the oracle project gains a `<Compile>`
(where Custard's project puts it), the differential host is registered in the test pack like any
other, and `.fantomasignore` already covers `oracle/custard/` — three lines in two files, none of them
the proof leg.

## When the byte-diff fails

It means a committed file under `oracle/custard/` is
not what the prover produces from the current `.fst` files. That is the expected state after any
model edit, and the fix is to copy the fresh file over the committed one and commit the two together
— `check.ps1` prints the exact command and the first forty lines of the diff. A file Custard wrote
that has no committed counterpart is reported the same way, and means a model now reaches a
definition outside the five; say so here before committing it. It is *not* a state to resolve by editing the
extraction: the next run would simply report it again.

## Licence

Apache-2.0, like everything beside it. See [`LICENSE`](../LICENSE).
