# BrandKit visual-snapshot + theming contract tests (Phase 197)

**What changes.** `ToolUp.BrandKit` gains a golden-markup and theming-contract test pack. **No
production source changed** — the package's emitted markup, class hooks and public surface are
byte-for-byte what they were, and a deployment that never runs the test pack is unaffected (GP 13).
What changes is that a regression in the emitted DOM, a renamed `bk-*` class hook, or a `--bk-*`
token that has quietly stopped corresponding to anything BrandKit emits now **fails a test** instead
of shipping silently.

**Scope.** Test tier only, inside `ToolUp.Platform.Tests`. Nothing to adopt: no package version
moves, no consumer code changes, no new dependency. This document exists for the contributor who
changes a BrandKit primitive or layout and meets the new gate.

## What the pack pins

Three families, all in `src/ToolUp.Platform.Tests/InProcess/BrandKitLayoutTests.fs`, alongside the
Phase 92 render-shape tests that were already there.

1. **Golden markup snapshots** — every public rendering function in `ToolUp.BrandKit` (the `Text`,
   `Wordmark`, `Card`, `Pill`, `Persona`, `Icon` and `PageChrome` primitives) and each of the seven
   layouts, in full-slot and every-optional-slot-omitted form. 48 cases, each rendered to an HTML
   string and compared against a committed baseline at
   `src/ToolUp.Platform.Tests/Support/brandkit-markup.approved.txt` (one line per case:
   `<case-id>` TAB `<markup>`). A structural change — an element swapped, an attribute dropped, a
   wrapper added — fails the case with a character-indexed diff.

2. **Token / class-hook contract** — a table mirroring the variable table in
   [`brandkit-tokens.md`](../brandkit-tokens.md), made executable. Every `--bk-*` `[<Literal>]` in
   `ToolUp.BrandKit.Tokens` is read **by reflection**, so a token added to the module with no entry
   in the table fails the parity case with no edit to the test needed; the reverse direction catches
   a table entry for a token the module no longer declares. The same set is checked against
   `HostThemeTokens.brandKitVars`, so the hosted-theme projection cannot drift from the primitive
   token set. Per token, the class hook(s) a consumer attaches the value to are asserted to be
   emitted by the primitive the docs name — which is what makes "documented but dead" detectable.

3. **Theming isolation** — the article layout is rendered twice under two contrasting brand
   palettes (a light-paper set and a dark set, every token differing), projected into a `:root`
   block via `HostThemeTokens`. The two documents must be **byte-identical outside that block**, and
   the stripped markup must contain no hex/`rgb()`/`hsl()` colour, no `font-family` declaration and
   no CSS-variable reference. That is the "ships zero opinionated styling" claim as an assertion
   rather than a sentence.

## Making an intentional change

A deliberate markup change is a **one-line baseline edit**. The failure prints the approved and
rendered windows around the first differing character, so the corrected line can usually be pasted
straight in. To regenerate the whole file instead:

```bash
# from the repo root, after building the pack
TOOLUP_APPROVE_BRANDKIT=1 dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll \
    --filter "ToolUp.Platform.Tests.ToolUp.BrandKit"
```

Regeneration rewrites every line, so **review the diff before committing** — it should contain only
the cases your change was meant to touch. Adding a primitive or a layout means adding it to
`snapshotCases` in the same commit; the completeness case fails on a baseline line with no case and
on a case with no baseline line, in both directions.

Adding a `--bk-*` token means adding a row to the token contract table (and to the variable table in
[`brandkit-tokens.md`](../brandkit-tokens.md)) in the same commit, naming the class hook the token
themes and a render that carries it.

## Verification

```bash
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll
```

Or, while iterating on BrandKit alone:

```bash
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll \
    --filter "ToolUp.Platform.Tests.ToolUp.BrandKit"
```

Note the filter path is joined with `.` and must start at the pack root — a filter that matches
nothing reports `0 tests run … Success!`, so read the **count**, not the exit code.

## Rollback

Delete the baseline file and revert the test file. Nothing else is affected: the pack references no
production code it did not already reference, and no shipped assembly changed.

## Two findings recorded rather than silently corrected

Writing the token contract surfaced two places where a doc comment has drifted behind the code. Both
are documentation-only, neither changes behaviour, and both are recorded here rather than patched in
passing so the correction is a deliberate act with its own review.

- **`Tokens.fs`'s module header says BrandKit "emits `var(--bk-<name>)` references in inline styles
  where CSS-class hooks alone don't suffice". It emits none.** Every `--bk-*` token is consumed by
  the *consumer's* stylesheet through a `bk-*` class hook; the only inline styles the package emits
  are the two per-call values [`brandkit-tokens.md`](../brandkit-tokens.md) already documents
  correctly under "Inline-style references" — a wordmark's emphasis colour and a persona's optional
  ring. The pack asserts the actual contract in both directions: the class hooks are emitted, and no
  rendered markup contains a `var(--bk-…)` reference at all. So the header describes a shape that
  was never built, and introducing it later would now be a visible, deliberate change.

- **`brandkit-tokens.md` records `--bk-font-ui`, `--bk-ink` and `--bk-paper` as "(consumer body)".**
  That was accurate at Phase 81, before Phase 92's `LayoutShell` gave the document body a
  BrandKit-emitted hook. `.bk-page` is that hook, so those three tokens are live rather than dead,
  and the contract table binds them to it.

## See also

- [`brandkit-tokens.md`](../brandkit-tokens.md) — the CSS variable contract the token table mirrors
- [`platform/layouts.md`](../platform/layouts.md) — the layout library's class-hook contract
- [`platform/testing-conventions.md`](../platform/testing-conventions.md) — why the packs run
  sequenced, and how `VerifyAll` reaches each one
