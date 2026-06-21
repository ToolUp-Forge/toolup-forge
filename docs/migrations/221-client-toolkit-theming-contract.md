# Migration — Phase 221: client-toolkit theming contract

**Type:** additive, backward-compatible (no consumer action required).

## What changed

The canonical `client-styling/tailwind/index.css` gained a client-toolkit token block
(`--surface`, `--text-strong`/`--text`/`--muted`, `--pos`/`--neg`, `--radius`/`--radius-pill`/`--shadow`),
and the client toolkit (`Toolup.UIToolkit.Tokens`, `Kpi`, `Data`, `StateViews`, `Forms`, `Typography`,
`Layout`) + the shell `Sidebar` now read those tokens instead of hardcoding `rounded-lg` / `bg-white` /
`text-gray-*` / `text-green-600` / `text-red-600`. See [`../client-toolkit-tokens.md`](../client-toolkit-tokens.md).

## Do I need to do anything?

**No.** Token defaults reference Tailwind's own scale, so with no overrides the surface renders exactly
as before. The one consumer-visible mechanic: the **canonical block changed**, so `check-drift.ps1`
will flag your pasted copy until you re-paste the updated `@toolup/tailwind` block (a docs-level
re-paste; zero visual change).

## To adopt a custom skin

Override any token in a `:root` *after* the canonical block (see the worked example in
`client-toolkit-tokens.md`). Every toolkit component re-skins at runtime.

## Rollback

Revert the token block + the component edits; no data or API surface is affected.

## Notes

- Neutral ramp consolidated to three tokens (`--text-strong`/`--text`/`--muted`); a handful of
  `text-gray-700`/`-800` sites now resolve to the nearest of the three (sub-shade only).
- Type-scale tokens, pill background fills, and AgChart colours are follow-ups (Typography pass /
  Phase 222 `ChartPalette`).
