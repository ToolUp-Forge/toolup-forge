# BrandKit token docs describe the real theming contract (Phase 738)

**Status:** documentation-only — **no emitted markup, no public surface and no behaviour changes.
Nothing to adopt.**

## What changes

Two statements about how `ToolUp.BrandKit` themes its output were wrong. Both are corrected at
source; neither was ever true of the shipped code, so no consumer's rendering changes.

1. **`Tokens.fs`'s module header claimed the package "emits `var(--bk-<name>)` references in inline
   styles where CSS-class hooks alone don't suffice".** It emits none — that shape was never built.
   Theming rides the `bk-*` class hooks the primitives and layouts emit, which a consumer styles in
   its own stylesheet; the `--bk-*` literals are the vocabulary of that contract plus a programmatic
   handle (`HostThemeTokens.ofBrandKitValues` projects supplied values onto the set), never something
   BrandKit substitutes into markup. The header now states that, names the hook that carries each
   token, and says explicitly that no `var(--bk-…)` reference is emitted. `Text.fs`'s header carried
   the same claim in the same words and is corrected identically.

2. **`docs/brandkit-tokens.md` recorded `--bk-font-ui`, `--bk-ink` and `--bk-paper` as "(consumer
   body)".** Accurate at [Phase 81](../brandkit-tokens.md), overtaken by
   [Phase 92](../platform/layouts.md)'s `LayoutShell`, which gives the document body the
   BrandKit-emitted `.bk-page` hook. All three are carried by that hook, so they are live tokens
   rather than tokens the consumer has to wire to a body it classes itself.

Both drifts were found and recorded — rather than patched in passing — by
[Phase 197](197-brandkit-visual-snapshot-theming-contract.md), whose contract pack pins the *real*
contract in both directions: every documented hook is asserted to be emitted by the primitive that
names it, and a separate case fails if a `var(--bk-` reference ever appears in rendered markup. This
phase is the deliberate correction that recording was deferred to.

Alongside those two, `docs/brandkit-tokens.md` gained a **"Carried by (class hook)"** column
mirroring the 197 pack's token/hook table row for row, so the doc and the executable contract can be
read against each other; `Tokens.cssVar`'s doc comment now says what it is (a helper for a consumer
building its own rules) and that no BrandKit primitive calls it; and the minimal consumer stylesheet
gained the `--bk-font-ui` definition and the `.bk-page` rule it was missing, so the sample actually
wires the three corrected tokens.

## Why it is a phase and not a drive-by edit

The header comment is the package's stated contract in an OSS-published source file. 197 deliberately
scoped itself out of production files, so the correction gets the same review and migration trail as
any contract-statement change — and the 197 pack is the regression net proving nothing behavioural
moved.

## How to adopt

Nothing. No package version moves, no API changes, no consumer code changes, no new dependency. If
you had read the old header and written a consumer expecting BrandKit to substitute token values into
inline styles, that consumer was already relying on behaviour the package never had — the class-hook
route described above is, and always was, the way a `--bk-*` value reaches rendered output.

## Verification

The correction is **textual**, and the proof of that is that the Phase 197 gate passes *unchanged*:

```bash
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll
```

Or, while iterating on BrandKit alone:

```bash
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll \
    --filter "ToolUp.Platform.Tests.ToolUp.BrandKit"
```

Two things specifically must hold, and both did:

- `src/ToolUp.Platform.Tests/Support/brandkit-markup.approved.txt` is **byte-identical** — no
  baseline was regenerated in this phase, and `TOOLUP_APPROVE_BRANDKIT` was never set. A changed
  baseline here would mean emitted markup moved, which is the one thing this phase must not do.
- The token/class-hook contract cases and the zero-`var()` case pass as they stood, with no edit to
  the test file.

Note the Expecto filter path is joined with `.` and must start at the pack root — a filter that
matches nothing reports `0 tests run … Success!`, so read the **count**, not the exit code.

## Rollback

Revert the three files (`src/ToolUp.BrandKit/Server/Tokens.fs`,
`src/ToolUp.BrandKit/Server/Text.fs`, `docs/brandkit-tokens.md`) and delete this document. Since only
comments and prose changed, a revert restores the incorrect claims and nothing else — no assembly,
no baseline, no consumer.

## SDK adoption

⛔ **N-A across all consumers** — documentation-only. No emitted markup, no public surface and no
behaviour changed, so there is no consumer-side action to take or defer.

## See also

- [`brandkit-tokens.md`](../brandkit-tokens.md) — the corrected CSS variable contract
- [`197-brandkit-visual-snapshot-theming-contract.md`](197-brandkit-visual-snapshot-theming-contract.md)
  — the pack that pins the real contract and recorded both drifts
- [`269-brandkit-host-theme-tokens.md`](269-brandkit-host-theme-tokens.md) — `HostThemeTokens`, the
  projection that consumes the same canonical token set
- [`platform/layouts.md`](../platform/layouts.md) — the Phase 92 layout library's class-hook contract
