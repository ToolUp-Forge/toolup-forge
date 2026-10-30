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

## The published Fuaran libraries (Phase 1127)

**Ruling (2026-10-27).** This repository may depend on, and name in any shipped file, the
published Apache-2.0 Fuaran libraries — the `Fuaran.Core.*`, `Fuaran.Compute.*` and
`Fuaran.UI.*` packages, their public repositories and their public specifications. The bare
`Fuaran` token is therefore not a banned word here. The first adopter is the fact browse's
view slice, which carries a `Fuaran.Core.Column` table and refines it with
`Fuaran.Compute.DataFrame` (see [Facts](facts.md)).

**The licence line.** The permission reaches exactly as far as a reader can follow it: a
library that is BOTH Apache-2.0 AND published on nuget.org with a public repository. A
Fuaran repository that is private — even one destined to be public — is still not cited,
because a public file pointing at it names something a reader cannot open. Nothing else in
the rule above changes: commercial layers and private repositories stay out of every
shipped file.

**Why the direction is allowed.** The dependency runs one way, from this SDK to a public
library, the way it runs to FSharp.Core or Feliz. Those libraries take no dependency on this
repository and know nothing of it, so no reader learns from the reference what sits above
this substrate. Three mechanics keep it that way:

- **nuget.org only.** Each pin names a version nuget.org serves, so a released ToolUp
  package restores anonymously with its Fuaran dependencies. A version built on one machine
  is never pinned.
- **One version property per producing repository** in `Directory.Packages.props`
  (`FuaranCoreVersion`, `FuaranComputeVersion`), because the Core and Compute repositories
  release independently. One property spanning both could never be right for long.
- **Through the package graph, never by source.** No project here references Fuaran
  sources, so the published contract is the only thing consumed.

### The second instance: the fact presentation builders (Phase 1129)

`ToolUp.Facts.Presentation` draws a fact series as a Fuaran-UI node, so it depends on
`Fuaran.UI` (the node vocabulary and its pre-emit validator) and `Fuaran.UI.Charts` (the
chart lowering a built chart is checked against). It is the ruling's second adopter, under
the same licence line: both packages are Apache-2.0, published on nuget.org, with a public
repository, and a released ToolUp package that carries them restores anonymously.

- **One pin for the producing repository.** `Fuaran.UI` and `Fuaran.UI.Charts` release
  together from the Fuaran-UI repository, so both ride one property, `FuaranUIVersion`,
  beside the Core and Compute pins. A later Fuaran-UI package joins that property, never a
  new one.
- **The pin agrees with the Core pin.** The Fuaran-UI release depends on Fuaran.Core and
  Fuaran.Compute packages; the pinned release is one built against the Fuaran.Core version
  pinned above, so the graph resolves to one Core version and nothing runs against a
  Core release it was not compiled against.
- **Fable-clean.** Both packages ship their sources under `fable/`, so the builders compile
  in a browser as well as on a server.

## Resolving beside the published Fuaran-UI tier

**The hazard.** A consumer composing the ToolUp packages with Fuaran-UI pages resolves ONE
version of each Fuaran substrate package for its whole graph: the highest floor any package
declares. When a floor here (`FuaranCoreVersion`, `FuaranComputeVersion`) is ahead of the floor
the newest published Fuaran.UI release was built against, NuGet lifts every such consumer onto a
substrate its UI tier was not compiled for. Below 1.0 a substrate minor may remove what the UI
tier calls, and the consumer fails to build or run: FS0193 or a `TypeLoadException` on .NET, and
the UI tier's own sources failing in its Fable client. This repository's own build cannot see it,
because it compiles its Fuaran consumption only against itself.

**The rule** (`dotnet run --project Build.fsproj -- VerifyFuaranCoResolution`, a `VerifyAll` leg
in the fast and full lanes; `FuaranCoResolution.fs`). Fuaran repositories release their packages
in lockstep, so the rule compares per family (`Fuaran.Core`, `Fuaran.Compute`): the floor pinned
here against the lowest floor any package of the snapshotted Fuaran.UI release declares in that
family.

| Verdict | When | Effect |
|---|---|---|
| aligned | at or behind the UI tier's floor | passes |
| ahead, compatible | ahead within one compatibility range (a patch below 1.0, a minor from 1.0) | warns |
| ahead, breaking | ahead across a minor below 1.0, or a major from 1.0 | **refused**, naming both versions |
| ahead, declared | ahead, breaking, with an open lead below for exactly that family and floor | passes, and prints the lead |
| behind | the UI tier ahead of this repository across a breaking boundary | warns; the co-build probe decides |
| lead not needed | an open lead no comparison used (the UI tier caught up, or the floor moved) | **refused**: record it closed |
| snapshot behind a pin | a `Fuaran.UI*` pin newer than the snapshotted release | **refused**: refresh the snapshot |

A family the UI tier does not depend on is out of scope.

**The snapshot.** `fuaran-ui-floors.json` at the repository root records the newest published
Fuaran.UI release and every `Fuaran.*` floor its tier packages declare (`Fuaran.UI`,
`Fuaran.UI.Charts`, `Fuaran.UI.Renderer`, `Fuaran.UI.Renderer.Server`). The gate reads it and
never fetches. Its one writer is `dotnet run --project Build.fsproj -- RefreshFuaranUiFloors`
(`--release <v>` for a named release), which reads nuget.org and refuses a version nuget.org does
not serve. Refresh it when a Fuaran.UI release ships, and commit the file.

**The co-build probe** (`dotnet run --project Build.fsproj -- VerifyFuaranCoBuild`, a `VerifyAll`
leg in the full lane). A scratch consumer composing the fact tier (`ToolUp.Facts.Server`,
`ToolUp.Facts.Client`) beside the snapshotted `Fuaran.UI` and `Fuaran.UI.Renderer.Server` on the
server and `Fuaran.UI.Renderer` in the client restores, builds on .NET, renders and encodes a page,
and Fable-compiles its client. It packs this tree's closure at a throwaway version, restores into
a package folder private to the checkout, and clears every inherited package source, so it sees
this tree's packs and nuget.org and nothing else. It runs on every full lane rather than on a
release tag, because the hazard is introduced by the commit that moves a floor. `--ui-version <v>`
probes another published Fuaran.UI; `--toolup-version <v>` restores a published ToolUp release
instead of this tree.

### Declared leads

A lead is a decision to stay ahead of the published UI tier, recorded here, never a silent pass.
Each row names the family, the floor declared ahead, the Fuaran.UI release it was declared
against and that release's floor, its status (`open`, or `closed by Fuaran.UI <version>`), and the
consumer shapes that break until it closes. The rule reads this table: an open row excuses
exactly its family at exactly its floor, a closed row excuses nothing, and an open row no
comparison needs is refused until it is recorded closed. A new floor is a new row. An open lead
reaches the release notes through the migration note of the release that declares it, which the
CHANGELOG generator links (for the open row below,
[1133-fuaran-co-resolution](../migrations/1133-fuaran-co-resolution.md)).

| Family | Floor here | Fuaran.UI release | Its floor | Status | Broken until closed |
|---|---|---|---|---|---|
| `Fuaran.Core` | `0.40.2` | `0.97.0` | `0.38.1` | closed by Fuaran.UI 0.98.1 | Every consumer composing the fact tier beside Fuaran-UI pages on Fuaran.UI 0.97.0 or older: the Core minors after 0.38 removed entities the UI tier calls (`Fuaran.Core.Canon` among them), so the server failed with FS0193 or a `TypeLoadException` for `Fuaran.Core.Canon`, and the Fable client failed compiling the UI tier's own sources. Recorded after the fact: this pin shipped in 0.35.0 before the rule existed. |
| `Fuaran.Compute` | `0.42.0` | `0.99.1` | `0.41.0` | open | None observed. `Fuaran.Compute.DataFrame` 0.42.0 ships Fable sources byte-identical to 0.41.0's, and the co-build probe passes beside Fuaran.UI 0.99.1. Closes with the first Fuaran.UI release built on `Fuaran.Compute.DataFrame` 0.42.0 or later. |

The first row is the worked example. The 0.35.0 release pinned `Fuaran.Core.Column` 0.40.2 while
every published Fuaran.UI was built on Core 0.38.1, and Fuaran.UI 0.98.1, built on Core 0.40.2,
closed it. Both ends are pinned in `ToolUp.Platform.Build.Tests` (the rule) and reproduced by the
probe (`--toolup-version 0.35.0 --ui-version 0.97.0` fails, `--ui-version 0.98.1` passes).

