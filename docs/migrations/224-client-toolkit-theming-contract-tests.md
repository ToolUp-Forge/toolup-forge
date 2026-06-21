# Migration — Phase 224: client-toolkit theming-contract tests

**Type:** test-only, no production behaviour change (GP 13).

## What changed

Added `ClientToolkitThemingTests` (registered in `Program.fs` `allTests`) — a textual source-contract
pack over the Phase 221/222 theming work, in the same style as `AriaPropTests` (the Fable-only client
tier can't be rendered by the .NET Expecto runner, so the contract is asserted over source text). It
pins:

1. **No-leak** — no hardcoded `text-gray-*` / `rounded-lg` / `text-green-600` / `text-red-*` survives
   in a tokenised toolkit file (each must be a `text-[var(--…)]` / `rounded-[var(--radius)]` /
   `bg-[var(--surface)]` reference).
2. **Token emission** — every theming token (`--surface` / `--text-strong` / `--text` / `--muted` /
   `--pos` / `--neg` / `--radius`) is referenced by at least one toolkit file.
3. **Drift guard (Phase 222)** — the brand hex `#59229D` appears only in `AgChart.fs`'s sanctioned
   `ChartPalette` fallback, nowhere else under `Client/UI`.

The render-isolation assertion (render under two `:root` token sets, assert the only delta is the token
values) needs a DOM/jsdom and is deferred to a Fable-side harness; the no-leak contract is its .NET
stand-in.

## Found a real leak on first run

The pack immediately caught a `hover:text-red-600` left on the dismiss button in `StateViews.fs`
(a hover state the Phase 221 sweep had skipped) — now `hover:text-[var(--neg)]/80`, so the hover state
themes too. Exactly the silent-regression class this contract exists to prevent.

## Do I need to do anything?

No — test-tier only; absent the pack, production is unchanged.
