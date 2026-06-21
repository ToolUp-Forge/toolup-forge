# Migration — Phase 223: per-tenant palette extension

**Type:** additive, backward-compatible (no consumer action required).

## What changed

[Phase 5e](05e-per-tenant-branding.md) gave each team a single `primaryColor` that set the unread
`--brand-primary` custom property. Phase 223 widens the per-team allow-list to the full palette and
injects it as the theming tokens the whole client surface actually reads:

| `_platform` config key | `:root` token driven |
|---|---|
| `primaryColor` | `--color-brand` (+ legacy `--brand-primary`) |
| `brandDarkColor` | `--color-brand-dark` |
| `sidebarColor` | `--color-sidebar` |
| `posColor` | `--pos` |
| `negColor` | `--neg` |

Combined with the Phase 221 token contract, a team's colour edit now re-skins the **whole client
surface** (sidebar, brand accents, toolkit cards/labels, status deltas, charts) live on team switch.

## The boundary (by construction)

The override set is **colours only** — `Branding.PaletteOverrides` is a list of validated
`(cssVar, hex)` pairs, and the `_platform` schema exposes only these colour keys + logo/appName. There
is no field through which a team admin can set a font-family or a component shape, and no free-form CSS
is ever accepted (a value must pass `Branding.isHexColour` or it is dropped). Fonts and component
recipes stay platform-fixed.

## No-clobber semantics

Injection is **override-aware**: the shell sets only the tokens a team explicitly set, and *removes*
any token a previous team set but the current one didn't. A team that leaves a colour blank inherits
the deployment's base theme — it never overwrites the app default (or another team's) with an SDK
default. So a single-tenant deployment, or a team customising nothing, renders byte-for-byte as before.

## Do I need to do anything?

**No.** Additive — new optional `_platform` fields + a record field defaulting to `[]`. The Platform
Defaults admin tab gains the four colour fields automatically on team-scoped deployments.

## Rollback

Revert the `BrandingKeys` additions, the `Branding.PaletteOverrides`/`paletteVarMap` additions, the
`BrandedHeader` palette loop, and the schema fields. Phase 5e's single-`primaryColor` behaviour resumes.
