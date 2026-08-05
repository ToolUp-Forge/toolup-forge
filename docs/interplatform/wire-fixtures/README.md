# Federation-seam conformance corpus

The executable half of [`../FEDERATION_WIRE.md`](../FEDERATION_WIRE.md). An implementation in any
language certifies against a named conformance profile by running the vectors this corpus lists for
it.

## Layout

```
manifest.json            the authoritative enumeration: families, profile partition,
                         every vector with its kind and digest
emit.mjs                 a second, dependency-free emitter (see below)
peer-surface/            what one deployment serves, consumes and stands behind
aggregate-surface/       what a group faces the world as
pinned-exchange/         what a consumer recorded of a counterparty's label
attestation/             signed bilateral agreement records
contract-invocation/     the data plane: request, response, errors, job poll
host-envelope/           what a host offers a module it will run
model-execution/         fitting a model against a counterparty's data, without the data moving
```

## Reading a fixture

**A fixture file's bytes are the document.** No trailing newline, no framing, no wrapper — so the
`sha256` the manifest records is a digest of the file itself, with no convention to interpret
first. The documents are canonical JSON per §3 of the specification: minified, member order fixed
by the shape, optionals present as `null`.

`manifest.json` is the exception: it is a hand-formatted index for humans, LF-terminated, and is
not itself a specified wire shape.

## Certifying

1. Pick a profile — `participant`, `gateway`, `module-host`, `participant-data-host` or
   `participant-modeller` — and read the families `manifest.json` maps it to.
2. For every vector in those families, do what its `kind` says:
   - `round-trip` — decode and re-encode; the bytes must be identical.
   - `hash` — round-trip, and reproduce the stamp by recomputation (for an attestation, the
     manifest's `digest` over the length-prefixed signing input, which is **not** the JSON).
   - `reject` — your reader must refuse it, with the refusal class the manifest names.
3. Report the profile. A conformance claim without one is unfalsifiable.

Certifying a subset is not certifying. Assert that the number of vectors you ran equals the number
the manifest enumerates, and prove once that a mutated fixture makes your harness go red — a
conformance suite is exactly the kind of code that passes by doing nothing.

## The second emitter

```
node emit.mjs            # check every round-trip and hash document against the committed bytes
node emit.mjs --write    # rewrite them
```

`emit.mjs` is written against the specification alone, in a different language and runtime from the
reference implementation, and reproduces the documents from unsorted, unstamped input models by
applying the ordering, encoding and stamping rules as stated. It exists because one emitter cannot
tell the protocol from its own accidents — whatever it does becomes "the format" by default. **A
divergence between the two emitters is a specification defect**, not a bug in either.

It deliberately does not emit the reject vectors: those are documents an implementation must
*refuse*, so reproducing their bytes would prove nothing.

## Regenerating

The corpus is emitted from running code and is never hand-edited. A change to any specified field,
ordering, encoding or stamp updates the specification, the emitter and this corpus **in the same
commit** — see §9 of the specification, and Appendix B for the regeneration command.
