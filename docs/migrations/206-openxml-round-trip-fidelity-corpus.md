# Migration — Phase 206 OpenXml round-trip fidelity regression corpus

**Status:** test-tier only. No public API changed, no runtime code changed, no emitted bytes changed. A consumer that upgrades sees a byte-for-byte identical `ToolUp.OpenXml` (GP 11) and pays nothing for the corpus, which ships in no package (GP 13). **No consumer action is required.**

The one thing worth reading even so is [What the corpus found](#what-the-corpus-found): the pass surfaced a real fidelity defect in `Emit`, which this phase *pins* rather than fixes.

## Why

`Package.openRead` → `Import` → `DocModel` → `Emit` is lossy in ways that do not announce themselves. A run property bag that stops being re-attached, a numbering reference that survives with the wrong instance, a section's properties landing on the wrong section, a revision losing its author — each leaves a document that still opens, still reads plausibly, and is quietly wrong. Until this phase, round-trip fidelity rested on a handful of hand-written assertions over one fixture; none of those failures would have reddened the build.

The corpus replaces that with six `.docx` fixtures, each round-tripped and pinned by committed textual goldens.

## What ships

Everything is under `src/ToolUp.OpenXml.Tests/`:

| Path | What it is |
|---|---|
| `Corpus/CorpusFixtures.fs` | the six fixture builders + the corpus declaration |
| `Corpus/Goldens.fs` | the three projections and the compare-or-regenerate gate |
| `Corpus/*.txt` | the committed goldens |
| `RoundTripFidelityTests.fs` | the `RoundTripFidelity` test list |

The pack is also now reached by `dotnet run --project Build.fsproj -- VerifyAll` (a `TestPack` entry named `OpenXml`). It was in the solution but in no aggregator, so its assertions ran only when someone invoked the project directly — which is to say, a fidelity regression could land green.

### The fixtures

| Fixture | Pins |
|---|---|
| `styled-runs` | bold / italic / underline / strikethrough / character style, combined formatting, and the tab + break characters the model normalises into run text |
| `numbering-lists` | two numbering instances at three indent levels, plus a numbered *heading* (heading classification wins; the numbering reference rides the verbatim `w:pPr`) |
| `section-breaks` | three sections — two closed by an intermediate `w:sectPr` on the section's last paragraph, one by the body-level `w:sectPr`; the middle section is landscape so a misattached `sectPr` is visible rather than plausible |
| `tables` | `w:tblPr` / `w:tblGrid` / `w:trPr` / `w:tcPr`, a spanned cell, a styled paragraph in a cell, and a nested table |
| `mixed-residue` | content outside the model's vocabulary — content control, hyperlink, bookmark, inline drawing — so the residue baseline pins real entries rather than only "none" |
| `tracked-changes` | pre-existing `w:ins` / `w:del` on runs *and* on paragraph marks, from two reviewers at two instants, with deliberately colliding source revision ids |

**The fixtures are built, not committed as binaries.** A `.docx` is an OPC zip whose entry timestamps and compression are not byte-stable, so a committed package could be neither diffed in review nor re-verified — and a fixture nobody can read is a fixture nobody can correct. Building them from `CorpusFixtures.fs` keeps the corpus minimal, deterministic, reviewable in an ordinary diff, and free of any provenance question: nothing in it is copied from a real document, and author attribution uses neutral placeholders throughout.

Every fixture is additionally asserted to be **valid OOXML at source**. A malformed fixture would pin the layer's behaviour against markup Word cannot produce, and its goldens would look entirely plausible; that case caught two real defects in the fixtures themselves while this phase was being written.

### The goldens

Three per fixture (a fourth where a defect is declared):

| Golden | Projection |
|---|---|
| `<fixture>.model.txt` | the **re-imported** `DocModel` — every section, block, run, revision mark and verbatim property bag |
| `<fixture>.package.txt` | the emitted package's parts and full OpenXml element tree, attribute values included |
| `<fixture>.residue.txt` | the **first** import's `ResidueReport` — kind, model address, disposition, reason |
| `<fixture>.validation.txt` | (declared defects only) the schema violations the emission produces |

Namespace declarations are stripped from verbatim payloads before they reach a golden: they are re-derived on every serialisation and carry no document content, so pinning them would churn every file on an OpenXml SDK bump for a difference that says nothing about fidelity. Everything else is pinned exactly.

The residue baseline is the leg that makes losses honest. A newly-dropped element either appears there — and the build fails — or is absorbed silently, and once the baseline is committed there is no third option.

## Regenerating the goldens

**Regenerate only for an intentional, reviewed change to the import/emit format — never to clear a red.** A red golden is the corpus doing its job; the question it asks is "did you mean this?", and the answer belongs in the same PR as the change.

```powershell
$env:TOOLUP_APPROVE_OPENXML_GOLDENS = "1"
dotnet run --project src/ToolUp.OpenXml.Tests/ToolUp.OpenXml.Tests.fsproj
$env:TOOLUP_APPROVE_OPENXML_GOLDENS = $null
```

**The regeneration run FAILS by design.** Every case that rewrote a golden reports that it rewrote one and verified nothing, and a separate case reports that the switch is armed at all. So a regeneration can never read as green, and a run that inherited the variable from a shell cannot pass — the failure mode the api-baseline gate warns about (`approve mode passes trivially, so its green proves nothing`) is closed here by construction rather than by discipline.

Re-run **without** the variable to verify against the files just written, then review the diff and commit the goldens alongside the code that moved them.

A missing golden is never created silently: it is a named failure pointing at this procedure. An absent golden pins nothing, so creating one as a side effect of a test run would quietly convert a gap into a green.

## What the corpus found

The tracked-changes fixture surfaced a real defect in `Emit`, present since the layer shipped:

> **A pre-existing paragraph-mark revision is emitted twice.** `Import` captures the mark on `ParagraphModel.MarkRevision` **and** leaves it inside the verbatim `w:pPr` carried on `RawProperties` (`paragraphRawProperties` strips only `w:sectPr`). `Emit` then re-attaches that verbatim payload *and* prepends a second element built from `MarkRevision`, so `w:pPr/w:rPr` ends up with two `w:ins` (or two `w:del`) where the source had one.

Two consequences: the emitted package **fails OOXML schema validation**, and the round trip is **not a fixpoint** — each pass adds another duplicate, without bound. Runs are unaffected, because a run's mark sits outside its `w:rPr`; that is why every fixture written before this one round-tripped clean and nothing in the suite caught it.

**Update — the defect is FIXED, by [Phase 736](736-openxml-emit-paragraph-mark-revision-duplication-fix.md).** `Import` now strips the mark it captures typed, so the fixture below carries no `KnownDefect` and its two `*.validation.txt` goldens are gone. The rest of this section is kept as written, because it is what the corpus found and how it declared it.

**This phase pins the defect; it does not fix it.** The corpus's job is to make today's fidelity measurable so tomorrow's drift fails the build, and the phase is scoped test-only — production sources are unchanged. The pin is not an exemption, and is deliberately not a comment:

- the defect is declared as data, on the fixture, in `Corpus/CorpusFixtures.fs`;
- the fixture's two clean-round-trip cases are **replaced** by cases asserting the defect is still *exactly* present, so the corpus reddens if the loss widens **and** if it is fixed without the declaration and goldens being updated alongside;
- the schema violations are themselves a golden, so a new violation appearing beside the known ones cannot be absorbed into "still invalid";
- the duplicate is visible in plain text in `tracked-changes.package.txt`, where a reviewer meets it before the test output does.

Fixing it is a change to `Import` or `Emit` plus a reviewed golden regeneration — which is exactly the shape such a fix should have, and is now a change that cannot be made silently in either direction.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `OpenXml` pack runs 84 tests, 0 failures.
- The goldens were demonstrated to *detect* drift rather than merely to exist: an in-progress change to the `mixed-residue` fixture reddened all three of its goldens with a line-level diff before the pass was finished.
- No `api-baselines/*.approved.txt` changed — a test project adds no public SDK surface.

## Rollback

Delete `src/ToolUp.OpenXml.Tests/Corpus/`, `src/ToolUp.OpenXml.Tests/RoundTripFidelityTests.fs`, their `<Compile>` entries, the `RoundTripFidelityTests.tests` line in `Program.fs`, and the `OpenXml` `TestPack` entry in `Build.fs`. Nothing outside the test project depends on any of it.
