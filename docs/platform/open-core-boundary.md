# The open-core boundary — what a shipped file may name

<!-- OSS-BOUNDARY-EXEMPT-FILE: this note documents the guard; it names no private vocabulary, but the marker keeps future examples safe. -->

This repository is Apache-2.0 and public. Everything in it is searchable, indexable and
quotable — including the parts nobody reads on purpose: a doc comment, a sample, a
`README.md` packed into a nupkg. So the rule is narrow and absolute:

> **A publicly-shipped artefact must not name a private project, product, repository or
> internal planning command.**

It is not a secrecy rule so much as a positioning one. A cross-reference from here to a
commercial layer tells a reader what sits on top of this substrate — which is a thing to
state deliberately, not to have inferred from a stray comment.

## What the guard checks

`ToolUp.Platform.Tests` carries two neutrality packs, both reading one token source:

| Pack | Scans |
|---|---|
| `SecondBindingNeutrality` (Phase 202) | the `samples/ToyTreeBinding/` reference binding |
| `OpenCoreVocabularyNeutrality` (Phase 477) | the whole publishable surface |

The publishable surface is **derived, never listed**, from the metadata the pack and
publish paths already use — `Directory.Build.props` conditions every packed item on
`IsPackable != false`, and the publish workflow packs every such project without naming
one. Concretely:

- every `.fs` whose nearest enclosing `src/**/*.fsproj` does not set
  `<IsPackable>false</IsPackable>` (server-compiled and Fable-delivered alike);
- each such project's own `README.md`, which is packed into its nupkg;
- `docs/**/*.md`;
- `README.md`, `CONTRIBUTING.md` and `SECURITY.md` at the repo root;
- `samples/**`, both `.fs` and `.md`.

Add a packable project and it is covered by the commit that adds it. Nothing here needs
editing.

## The vocabulary is not in this repository

A public repository that lists its own banned words publishes exactly what the guard
exists to keep out of it. The list is therefore loaded at run time from a source outside
the repository — a gitignored `neutrality-tokens.local.txt` at the repo root, or the
`TOOLUP_NEUTRALITY_TOKENS` environment variable. When neither exists the guard enforces a
single hardcoded, public-safe canary token and then **skips loudly**: reported as ignored
with a message, never as a silent pass. The scanning mechanism stays provable in public
CI; the policy stays private.

## How a token is matched

Matching is case-insensitive. Beyond that:

- A token whose first (or last) character is alphanumeric must sit on a **word boundary**
  at that end. `Concord` does not fire inside `concordance`; a token still fires inside
  `vendor.token.model-spec`, because `.` and `-` are not word characters. This is what
  keeps ordinary English out of the report — a gate that cries wolf is a gate people
  learn to step over.
- Internal whitespace in a **multi-word** token matches any run of whitespace, so a phrase
  wrapped across two comment lines is still caught. Prefer the multi-word phrase form
  whenever the bare verb is also ordinary English.
- A token beginning `re:` is a raw .NET regex — the escape hatch for a boundary the
  default rule gets wrong.

## When a citation is legitimate

Two cases are sanctioned, and both are declared **in the tree** by a marker comment rather
than by a path list that the next rename silently breaks:

```text
OSS-BOUNDARY-EXEMPT: <reason>        this block only
OSS-BOUNDARY-EXEMPT-FILE: <reason>   the whole file
```

A **block** is a run of consecutive non-blank lines — a doc comment and the declaration under
it, a markdown paragraph, the rows of a table. The marker may sit anywhere in the block, so
it can go on a line that will hold a comment even when the offending line will not (a table
row is the case this exists for). A blank line ends the block, so the scope stays visible and
a block marker can never quietly cover a file.

1. **The guard's own data.** The token module and the neutrality packs hold the vocabulary
   as assertion data.
2. **A citation of a public, Apache-licensed specification** this repository conforms to,
   by name and URL — including where that specification's repository slug happens to share
   a name with a private layer. The token ban protects a private ecosystem, not conformance
   to a public contract.

Always give a reason. An exemption without one is indistinguishable from a leak somebody
silenced.

## Fixing a failure

The failure names the file, the 1-based line and the token that fired. Three remedies, in
order of preference:

1. **Neutralise the wording.** Most hits are a comment that names something by its
   internal name when a generic one would read better anyway — "a downstream consumer",
   "the commercial composition root".
2. **Move the citation.** Planning context belongs in a private-side planning document;
   refer to it by number from here, if at all.
3. **Mark it exempt, with a reason** — only for the two sanctioned cases above.
