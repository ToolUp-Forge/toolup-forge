<!--
SPDX-License-Identifier: Apache-2.0
Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
-->

# `proofs/` — the decoder totality theorem

This directory holds one theorem and the machinery that keeps it honest.

**The theorem.** Given a parsed `Value`, no combinator in `ToolUp.Remoting.Decode` and no decoder
built from them can diverge, throw, or reach a state that is neither an accept nor a named
refusal — and *which* of the two is characterised structurally, so the failure classification is
exhaustive.

**The machinery**, because a theorem about a model is worth what the tie to the code is worth:

| File | What it is |
|---|---|
| `RemotingDecode.fst` | the model — `Decode.fs` clause for clause, each definition naming its F# counterpart, every refusal message reproduced verbatim |
| `fstar-pin.json` | the pinned prover (an F\* release, which bundles Z3), with its hash |
| `check.ps1` | the whole proof leg: resolve the pin, check, extract, byte-diff, build, run the differential host |
| `oracle/RemotingDecode.fs` | **generated** — the model extracted to F#, committed so the repository never needs a prover to build |
| `oracle/Prims.fs` | the nine-name runtime the extraction needs, because F\*'s F# backend ships none |
| [`../proofs.json`](../proofs.json) | the ladder below, declared as **data** — hand-authored, never generated, so a registry can read what a human decided rather than parse this prose |

```powershell
pwsh ./proofs/check.ps1            # the whole leg, once
pwsh ./proofs/check.ps1 -Runs 3    # three cold checks with --quake 3, what CI runs
pwsh ./proofs/check.ps1 -SkipHost  # the proof half only
```

Nothing else in the repository depends on any of it. `dotnet build`, `VerifyAll` and every other CI
job compile the *committed* extraction like ordinary source; the prover is a ~200 MB download this
one script fetches on demand into a gitignored directory. That is deliberate: a contributor with no
interest in proofs should never install one.

---

## The claims ladder

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

**This is the only place in this repository where the words "formally verified" are spent, and they
are spent on the combinator layer alone.** What a machine has checked, on the pinned prover, with
`--report_assumes error` so that an `assume` or an `admit` would fail the leg rather than quietly
weaken the result:

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

---

## When the byte-diff fails

It means `oracle/RemotingDecode.fs` is not what the prover produces from the current
`RemotingDecode.fst`. That is the expected state after any model edit, and the fix is to copy the
fresh extraction over the committed one and commit the two together — `check.ps1` prints the exact
command and the first forty lines of the diff. It is *not* a state to resolve by editing the
extraction: the next run would simply report it again.

## Licence

Apache-2.0, like everything beside it. See [`LICENSE`](../LICENSE).
