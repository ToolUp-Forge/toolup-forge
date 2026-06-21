# Migration — Phase 222: client-surface colour-drift purge

**Type:** additive, backward-compatible (no consumer action required).

## What changed

`AgChart.ChartPalette` previously froze the SDK brand (`#59229D`) into the chart series palette and
`accentColor`. A consumer (or per-team) override of `--color-brand` therefore re-skinned the shell and
toolkit but **not** the charts — they kept the old purple. `ChartPalette.refreshFromTheme ()` now
resolves `--color-brand` from the live CSS theme (to a literal string, as AG Charts requires) on first
chart render and updates the brand slots — but only where they are still at the built-in default, so an
explicit consumer palette override set at boot is preserved.

The `#59229D` fallback equals the SDK brand default, so a no-theme / no-DOM context renders exactly as
before (GP 11). Non-brand chart colours (the categorical series accents, marker magenta, axis black)
are unchanged — they are not brand-derived and so do not drift.

## Do I need to do anything?

**No.** Charts now follow `--color-brand` automatically. To keep a fully custom chart palette, set
`AgChart.ChartPalette.fills` / `.strokes` / `.accentColor` at boot as before — `refreshFromTheme` skips
slots you have changed.

## Guard

The `ClientToolkitThemingTests` (Phase 224) assert no new bare brand-hex (`#59229D`) literal reappears
in the toolkit/`Client/UI` source outside the sanctioned `ChartPalette` fallback, so the drift cannot
silently return.

## Rollback

Remove `refreshFromTheme` + its call in `AgChart.options`; the frozen literals resume.
