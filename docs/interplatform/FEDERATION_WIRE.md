# Federation-seam wire specification — moved

The normative text and its conformance corpus are no longer in this repository. They live in their
own public home:

**https://github.com/fuaran-ui/fuaran-federation-spec** — Apache-2.0.

- `FEDERATION_WIRE.md` — the normative text (§1–§9 + Appendix A).
- `wire-fixtures/` — the executable conformance corpus. `manifest.json` is the authoritative
  enumeration; do not count vectors from any prose description, including this one.

## Why it moved rather than being copied

A specification owned by one of its implementations cannot be conformed to by the others on equal
terms. Contract sovereignty for this seam sits with the specification, and this repository is an
**emitter** certifying against it — the first one, but structurally not a privileged one. The
normative text names no implementation, and that is a property worth keeping rather than a detail of
where the file happens to sit.

The corpus also had two homes once before, in an earlier specification, and the second drifted for
three days while CI gated against it. One home removes that class by construction instead of by
remembering to republish.

## What this repository still holds

- `src/InterPlatform/` — the emitters.
- `cross-runtime/` — the non-F# peer drivers, which are about *this* implementation's interop and
  are not part of the specification.
- The conformance harness in `src/ToolUp.Platform.Tests/InProcess/`, which resolves the corpus from
  the specification home and certifies against it on every test run.

## Getting the corpus

The harness looks for a directory named `fuaran-federation-spec` containing `wire-fixtures/manifest.json`,
in this order: the `TOOLUP_FEDERATION_SPEC_DIR` environment variable, a checkout inside this
repository (gitignored — this is what CI does), then a bounded search of this repository's ancestors.

```
git clone https://github.com/fuaran-ui/fuaran-federation-spec.git
```

When it is absent the conformance leg **fails** and prints the remedy. To run without it, set
`TOOLUP_FEDERATION_SPEC_OPTIONAL=1` — which declines the leg deliberately and says so in the run
output, rather than skipping it silently. A conformance suite that quietly does nothing when its
corpus is missing is indistinguishable from one that passed.

## Regenerating

`TOOLUP_EMIT_WIRE_FIXTURES=1` still regenerates the corpus from the live emitters — but it now
writes into the specification home, which is a **different repository**. A shape change is therefore
two commits in two repositories, pushed together: the emitter change here, and the regenerated
corpus there. Leaving the second unpushed is invisible to every other implementation and to CI.
