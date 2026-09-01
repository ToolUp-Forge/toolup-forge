# Migration — Phase 736 `ToolUp.OpenXml` paragraph-mark revision duplication fix

**Status:** behaviour-correcting, `ToolUp.OpenXml` only. No public API changed — no type, member or signature moved, and no `api-baselines/*.approved.txt` file is touched. **What changed is the bytes emitted for one shape**: a paragraph whose *paragraph mark* carries a pre-existing tracked change now emits **one** `w:ins` (or `w:del`) inside `w:pPr/w:rPr` instead of two.

**Consumer action:** none required. Read [Is this a regression?](#is-this-a-regression) if you diff emitted packages, or if you have a stored `.docx` produced by an earlier version.

## Why

[Phase 206](206-openxml-round-trip-fidelity-corpus.md)'s fidelity corpus surfaced a defect present since the layer shipped, and pinned it as data rather than fixing it. This phase fixes it.

`Import` captured a paragraph-mark revision **twice**:

* typed, on `ParagraphModel.MarkRevision` (via `paragraphMarkRevision`), and
* verbatim, inside the `w:pPr` payload on `ParagraphModel.RawProperties` — because `paragraphRawProperties` stripped only `w:sectPr`.

`Emit.buildParagraphProperties` then re-attached the verbatim payload **and** prepended a second element built from `MarkRevision`, so one source `w:ins` became two in the output.

Two consequences, and the second is the worse one:

* the emitted package **failed OOXML schema validation** — `w:rPr` (`CT_ParaRPr`) permits at most one `w:ins`, so the validator reported `Sch_UnexpectedElementContentExpectingComplex` at that path;
* the round trip was **not a fixpoint**. Re-importing the emitted package captured the mark typed again *and* carried both duplicates forward verbatim, so each further pass added one more — unbounded.

Runs were never affected: a run's mark is a `w:ins` / `w:del` element *wrapping* the run, not a child of its `w:rPr`, so there was no overlap to duplicate. That is why every fixture written before `tracked-changes` round-tripped clean and nothing in the suite caught it.

## The boundary decision — import strips, emit is unchanged

The duplicate had to be removed on exactly one side. **Import strips it.** The reasoning, recorded here because the phase asked for it:

**Phase 124's verbatim-preservation contract is not "keep the bytes"; it is "keep the parts the model does not decompose".** The layer already said so, and already acted on it: `paragraphRawProperties` stripped `w:sectPr` from the paragraph's payload precisely because section properties are modelled typed on `Section.RawProperties`, and its comment gave the reason as *duplication on emission*. The paragraph-mark revision is decomposed onto `MarkRevision` in exactly the same way, so it falls under exactly the same rule. Stripping it at import **applies the existing precedent**; it does not invent a second one, and there is now one sentence covering both cases instead of one rule and one exception.

**Emit-suppress would have fixed the emission and left the model wrong.** The duplication is not really an emission bug — it is a *model* carrying two representations of one fact. Suppressing at emit leaves that intact, so every other consumer of a `DocModel` (a diff, model equality, a second emitter, a validator, a future exporter) would each have to know which carrier wins. The invariant would then be a convention held inside one function rather than a property of the data, which is the shape defects like this one grow back in. Stripping at import makes it structural: after `Import`, the mark exists in exactly one place, and `Emit`'s existing "re-attach the payload, then lower the typed fields on top of it" logic is correct as written.

**Only the captured *kind* is stripped.** `EG_ParaRPrTrackChanges` is a sequence of optional elements, not a choice, so a `w:rPr` may legitimately carry a `w:ins` **and** a `w:del`; `MarkRevision` carries one of them. Import removes only the element the typed field took (`w:ins` for `Inserted`, `w:del` for `Deleted`) and leaves the other verbatim, so the fix removes a duplication without introducing a loss. Both survive the round trip, in schema order.

**A `w:rPr` left with no children stays, in place.** When the stripped element was the bag's only child, the empty `<w:rPr/>` is kept in the verbatim payload rather than removed. Position is the reason: `Emit` re-attaches the payload and re-uses whatever `w:rPr` it finds, so keeping it preserves the element's place in the `CT_PPr` sequence. Removing it would make `Emit` create and **append** a fresh one, which lands after any `w:pPrChange` — invalid. This is visible in `tracked-changes.model.txt` as `pPr <w:pPr><w:rPr /></w:pPr>`, and it is the honest reading: the bag was there, and everything the model does not carry typed is still in it.

The invariant is now stated on the type itself, on `ParagraphModel.RawProperties`, so a hand-built model has the rule where a reader meets it:

> This payload never carries a fact the model also carries typed. Emission re-attaches it verbatim and then lowers the typed fields on top, so anything present in both places is written twice.

## What changed

| File | Change |
|---|---|
| `src/ToolUp.OpenXml/Import.fs` | `paragraphRawProperties` takes the resolved `MarkRevision` and strips the element it captured, alongside the `w:sectPr` it already stripped |
| `src/ToolUp.OpenXml/DocModel.fs` | the single-carrier invariant stated on `ParagraphModel.RawProperties` / `MarkRevision` (doc comments only) |
| `src/ToolUp.OpenXml/Emit.fs` | comment only — names the invariant `buildParagraphProperties` depends on. **No behavioural change in `Emit`** |
| `src/ToolUp.OpenXml.Tests/Corpus/CorpusFixtures.fs` | the `tracked-changes` fixture's `KnownDefect` declaration removed, so it carries the ordinary clean-round-trip cases again |
| `src/ToolUp.OpenXml.Tests/RoundTripFidelityTests.fs` | the redline's pinned-defect case replaced by a schema-validity case, plus a new emit → import → emit fixpoint case over the redline |
| `src/ToolUp.OpenXml.Tests/Corpus/*.txt` | three goldens regenerated, two deleted (below) |

### The goldens that moved, and why each move is legitimate

Regenerated under `TOOLUP_APPROVE_OPENXML_GOLDENS` and re-verified without it. Nothing else in the corpus moved — the other five fixtures' nine goldens are byte-identical.

| Golden | Move |
|---|---|
| `tracked-changes.model.txt` | two `pPr` lines: the duplicated `w:ins` / `w:del` pair inside the verbatim payload is gone, leaving `<w:pPr><w:rPr /></w:pPr>`. The `mark [...]` line above each is unchanged — the typed carrier still holds the author and date |
| `tracked-changes.package.txt` | two lines deleted: the second `w:ins` (`w:id="102"`) and the second `w:del` (`w:id="104"`) |
| `tracked-changes-redline.package.txt` | the same two lines, inherited by the redline |
| `tracked-changes.validation.txt` | **deleted.** It recorded the two schema violations the duplication produced; the emission now validates, so there are none to pin |
| `tracked-changes-redline.validation.txt` | **deleted**, for the same reason |

The surviving element in each pair is the **renumbered** one (`w:id="3"` / `"5"`), not the source id. That is correct and worth noting: `Emit` assigns document-unique revision ids from one counter, and the stale source id was reaching the output only through the verbatim leak — where it was also a live uniqueness hazard, since the fixture's source ids deliberately collide across paragraphs.

**`ResidueReport` baselines are unchanged.** All six `*.residue.txt` files are byte-identical. Nothing became lossy: the stripped element is carried typed, so there is no loss to report.

## Is this a regression?

No — and this is the one thing worth being explicit about, because the change *removes* content from emitted documents.

The removed element was never information. It was the **same** revision, written a second time from the same source fact, and the OOXML schema forbids it: a package carrying it is invalid, and Word's tolerance of it is a leniency, not a contract. What the output now says is exactly what the input said — one paragraph-mark revision, with its author and its timestamp.

The concrete effects on a consumer:

* **An emitted package that previously failed OOXML validation now passes it.** If you validate emitted `.docx` files, expect two fewer violations per affected paragraph, and none from this cause.
* **`Import` → `Emit` is now a fixpoint on this shape.** A pipeline that round-trips a document repeatedly no longer grows the file on each pass.
* **A stored `.docx` produced by an earlier version is repaired by re-importing and re-emitting it** — the extra element is stripped on import like any other, and only one is written back. No migration tooling is needed and no data is lost.
* **If you diff emitted packages against stored expectations**, the paragraph-mark `w:rPr` for tracked-inserted or tracked-deleted paragraphs will have one child where it had two, and the surviving `w:id` is emission-assigned. Emission ids were never stable across versions (they are counter-assigned document-wide), so this is not a new instability.

One deliberate semantic consequence, stated because it is a behaviour change rather than a fix: `MarkRevision` is now genuinely the sole carrier, so a `Revisions` edit that **re-marks** an already-marked paragraph (say `DeleteParagraph` over a paragraph imported as inserted) replaces the captured mark rather than accumulating both. Previously the pre-existing element survived in the verbatim payload and was emitted alongside the new one — which is the duplication defect wearing a different hat, and produced the same invalid `w:rPr`. Pre-existing marks of the *other* kind, which the model does not capture, are untouched and still round-trip.

## The double-carrier sweep

The phase asked for a sweep of the rest of the layer for the same shape — a fact captured typed **and** left in a verbatim payload that emission re-attaches. **One instance existed, and it is the one fixed here.**

| Typed field | Verbatim payload | Emission | Verdict |
|---|---|---|---|
| `ParagraphModel.MarkRevision` | `ParagraphModel.RawProperties` (`w:pPr`) | payload re-attached **and** typed field lowered on top | **the defect** — fixed |
| `ParagraphModel.StyleId` (`w:pStyle`) | same `w:pPr` | `buildParagraphProperties` generates `w:pStyle` **only when the payload is absent** | clean |
| `NumberingRef` on `Block.ListItem` (`w:numPr`) | same `w:pPr` | `FromNumbering` consulted **only when the payload is absent** | clean |
| `RunFormatting.Bold` / `Italic` / `Underline` / `Strikethrough` / `StyleId` | `RunFormatting.RawProperties` (`w:rPr`) | `buildRunProperties` returns the payload verbatim, or generates from the booleans — never both | clean |
| `Run.Revision` | `RunFormatting.RawProperties` | a run's mark wraps the run; it is never a child of its `w:rPr` | no overlap by construction |
| `Section.RawProperties` (`w:sectPr`) | `ParagraphModel.RawProperties` | stripped at import since Phase 124; re-attached by `emitSection` | clean — the precedent this fix follows |
| `ParagraphModel.CommentIds` | `w:pPr` payload | anchors are `w:commentRangeStart` / `End` children of `w:p`, not of `w:pPr` | no overlap |
| `TableModel.RawProperties` / `RawGrid`, `TableRow.RawProperties`, `TableCell.RawProperties` | — | no typed field is derived from any of them | single carrier |
| `StylesModel.Styles`, `NumberingModel.Instances` | `RawXml` on each | `emitStyles` / `emitNumbering` use the payload verbatim when present, else generate | clean — the typed lists are read-side only |

The pattern everywhere except `buildParagraphProperties` is **"the payload wins; generate only in its absence"**, which cannot duplicate. `buildParagraphProperties` is the only place that composes a payload with a typed field, and it is the only place that could have had this defect. No further findings, and none recorded for later.

## Verification

* `dotnet fantomas` over the five changed `.fs` files — clean.
* The `OpenXml` pack: **121 tests, 0 failures**, run *without* the approve switch against the regenerated goldens.
* The fix was demonstrated by the instrument that pinned the defect. Before regeneration the pack reported exactly three failures, all golden-format diffs naming the removed duplicates — and the `tracked-changes` fixture's restored clean cases (fixpoint at the model altitude, and schema-valid emission) passed on that same run, which is the fix itself rather than a golden being told what to say.
* `dotnet build ToolUp.Forge.sln` clean; `dotnet run --project Build.fsproj -- VerifyAll` green.
* No `api-baselines/*.approved.txt` changed.

## Rollback

Revert `paragraphRawProperties` to its single-argument form (strip `w:sectPr` only), restore the `tracked-changes` `KnownDefect` declaration, restore the redline's pinned-defect case, and regenerate the goldens — the two `*.validation.txt` files return with them. Nothing outside `ToolUp.OpenXml` and its test project is involved.
