# Theming — one theme for Feliz views and Fuaran-UI trees

A client surface is drawn one of two ways: a Feliz view styled by the shell's Tailwind utilities, or a Fuaran-UI tree
lowered through the shell's tree seam and drawn by the Fuaran-UI renderer. Both take their colours, radii and spacing
from the same `:root` tokens, so a deployment's brand, and a team's palette, reach both without a second theme.

## What styles a tree surface

The renderer emits structural markup with `fuaran-*` class hooks and no style of its own. Two stylesheets style it:

| Stylesheet | Where it comes from | What it does |
|---|---|---|
| `fuaran-reference.css` | the `Fuaran.UI.Renderer` package's `content/` folder | the layout and look of every node kind, written against `--fuaran-*` variables |
| `fuaran-bridge.css` | `client-styling/` in this repository, copied beside the app's CSS entry | answers the `--fuaran-*` variables with the shell's tokens |

Both are imported once, by the app's CSS entry. A lowered tree emits no `<style>` element, so a page with several tree
surfaces carries the rules once.

## Importing them

The canonical Tailwind block (`client-styling/tailwind/index.css`) imports both, straight after Tailwind:

```css
@import "tailwindcss";
@import "@nuget/fuaran.ui.renderer/content/fuaran-reference.css" layer(base);
@import "./fuaran-bridge.css" layer(base);
```

The reference stylesheet stays in the package rather than being copied: restore puts it in the NuGet global packages
folder, at a path that names the user and the version. `client-styling/vite-fuaran-content.mjs` is a Vite plugin that
reads the client project's `obj/project.assets.json` and maps `@nuget/<package-id>/<path>` to the folder restore chose
for that package. So the stylesheet is always the one that ships with the renderer the client compiled against, and a
`FuaranUIVersion` bump moves both together. The package id is written in lower case, as NuGet stores it. Copy the
plugin beside `vite.config.mts` and add `nugetContent()` to `plugins`. Vite's CSS `@import` and `@tailwindcss/vite`
both honour the alias it adds.

**A consumer with its own CSS entry** adds the same two `@import` lines at the top of that entry, copies
`fuaran-bridge.css` beside it and `vite-fuaran-content.mjs` beside `vite.config.mts`, and adds `nugetContent()` to its
Vite plugins.

The `dotnet new` templates and `samples/MinimalClient` carry the same imports. `client-styling/check-drift.ps1` holds
every consumer's copies of the bridge and the plugin to their canonical sources.

Both imports sit in Tailwind's `base` layer, so every utility, and every rule an app writes after the canonical block,
still wins over them. The reference stylesheet is also a demo page's sheet and sets the `body`'s margin, background,
colour, font size and line height; the bridge puts those back, so importing it leaves the shell's page as it was.

## What the bridge maps

A `--fuaran-*` variable is bridged only where the shell has a token that means the same thing:

| Renderer token | Shell token |
|---|---|
| `--fuaran-tone-default-bg` / `-fg` / `-border` | `--surface` / `--text` / `--color-border` |
| `--fuaran-tone-subdued-bg` / `-fg` / `-border` | `--color-bg-light` / `--muted` / `--color-separator` |
| `--fuaran-tone-brand-fg` | `--color-brand` |
| `--fuaran-tone-brand-hover-fg`, `-active-fg` | `--color-brand-dark` |
| `--fuaran-tone-success-fg`, `--fuaran-tone-critical-fg` | `--pos`, `--neg` |
| `--fuaran-radius-sm` / `-md` / `-lg`, `--fuaran-radius-full` | `--radius`, `--radius-pill` |
| `--fuaran-space-xs` … `-xl` | 1, 2, 3, 4 and 6 steps of Tailwind's `--spacing` |

Derived from those:

- The brand's soft backgrounds and borders are mixed from the brand over the surface (`color-mix`), so a non-blue
  brand shows no blue tints.
- A destructive control's hover and pressed colours are darkened from `--neg`.
- The focus ring and a focused field's border use the brand.

Left at the reference values: the warning and info tones, the typography scale, and the focus ring's width, offset and
style. A host without the shell's tokens (no canonical Tailwind block) leaves the bridged variables invalid, and the
reference stylesheet's own `var(--x, fallback)` sites draw its defaults.

## A deployment's palette

A team's palette (`Branding.PaletteOverrides`) is written to `:root` as `--color-brand`, `--pos`, `--neg` and the rest,
and the bridge reads those, so a tree follows the team switch with nothing more.

A host that themes through BrandKit's `--bk-*` tokens builds a `HostThemeTokens` bag. Its projections
(`ofBrandKitValues`, `withPaletteOverrides`) also set the `--fuaran-*` tokens each brand token answers.
`HostThemeTokens.fuaranBridge` is that table: the shell's rows are the plain lines of `fuaran-bridge.css` (a test
holds the two together), and the `--bk-*` rows carry the BrandKit primitives. A `--fuaran-*` variable named in the
palette itself wins over one bridged from it. Emit the bag's `:root` block (`HostThemeTokens.toRootCss`) after the
stylesheets, so the deployment's values win over the reference defaults and the bridge.

## Holding the class contract

`TreeStylesheetTests` in `src/ToolUp.AI.Client.Tests` renders a surface built from the tree authoring helpers,
collects every `fuaran-*` class in its markup, and requires a rule for each in the restored reference stylesheet. A
renderer version that renames or adds a class without the stylesheet goes red there rather than in a browser.

## What it costs

The stylesheet rides the CSS entry, so it is in the first load of every page. The figures and the decision not to move
it into the renderer's chunk are in [budgets](budgets.md#the-fuaran-ui-stylesheet).
