# `@toolup/tailwind` — canonical Tailwind v4 contract

Single source of truth for the Tailwind toolchain across every ToolUp client app.

## Why this exists

A consumer-side `npm-check-updates -u` silently majored `tailwindcss`
v3 → v4; a half-finished migration left the CSS/PostCSS in v4 form
against a v3 dep and the consumer app rendered unstyled. With multiple
consumer apps, N hand-maintained Tailwind configs is N drift surfaces.
This package collapses them to one.

Not an npm-registry package — the SDK ships only via NuGet and has no npm
publish path. The contract is **embedded by copy** and enforced by
[`../check-drift.ps1`](../check-drift.ps1), the same way the SDK's
"Powered by ToolUp" favicon is propagated to consumers.

## Files

| File | Role |
|---|---|
| `index.css` | The canonical block: `@import "tailwindcss"` + `@source "./output"` (Fable output; see below) + `@theme` token contract + the official v3→v4 compatibility base layer. Delimited by `>>> @toolup/tailwind canonical <<<` markers. |
| `deps.json` | Exact toolchain pin (`tailwindcss` + `@tailwindcss/vite` = `4.3.0`, no `^`) and the v3/PostCSS deps + config files v4 makes obsolete. |
| `../check-drift.ps1` | Fails if any consumer's marked region or pinned deps differ. Wire into precommit/CI. |

## How a consumer adopts it

1. Copy the marked region of `index.css` verbatim to the top of the
   app's `src/.../index.css`; put **only** the app's brand
   `:root { --color-*: … }` overrides + app-specific CSS (fonts, AG Grid,
   keyframes) *after* the end marker.
2. Set client `package.json` devDeps to exactly `deps.json` →
   `devDependencies`; remove the `deps.json` → `remove.devDependencies`;
   delete `deps.json` → `remove.files`.
3. Copy the canonical `vite-fable-tailwind.mjs` next to `vite.config.mts`
   and add **both** plugins:
   `import tailwindcss from "@tailwindcss/vite"` +
   `import fableTailwindGitignore from "./vite-fable-tailwind.mjs"` →
   `plugins: [fableTailwindGitignore(), tailwindcss(), …]`.
   **`fableTailwindGitignore()` MUST precede `tailwindcss()`.** The old
   `content` globs and `tailwind.config.js` / `postcss.config.js` are
   deleted. **v4 does NOT auto-detect classes from the module graph in
   `vite build`** — see [Fable `output/` needs `@source` + the canonical
   Vite plugin](#fable-output-needs-source--the-canonical-vite-plugin)
   below; this is the single most important difference from v3 for ToolUp
   Fable apps, and the canonical `index.css` carries the `@source` line.
4. Run `npm install` (commit the regenerated lockfile) and
   `check-drift.ps1` (must pass).

Per-app branding is then a ~6-line `:root` block; the toolchain version
lives in exactly one place.

## Fable `output/` needs `@source` + the canonical Vite plugin

This is the load-bearing correction to the earlier (wrong) assumption that
v4 "auto-detects content from the module graph".

**What v4 actually does in `vite build`:** `@tailwindcss/vite`'s build
transform compiles the CSS, then scans for class candidates with
`@tailwindcss/oxide`'s filesystem `Scanner` over the Vite root (plus any
`@source`). It does **not** use Vite's module graph in build mode (that is
serve-only), and the oxide scan **honours `.gitignore`**.

**Why that breaks ToolUp Fable apps:** every class the SDK shell renders
(`bg-sidebar`, `bg-brand`, `text-brand-dark`, `border-border`, … ~hundreds)
exists *only* in Fable's compiled output under
`output/fable_modules/ToolUp.Platform.Client.*/…` (the shell ships inside
the `ToolUp.Platform.Client` NuGet package, not the app source tree). The
repo `.gitignore` excludes `output/`, so oxide's auto-scan never sees them
→ the shell renders unstyled. (The app's own module classes live in
non-ignored `src/Modules/**` and are unaffected.)

**Why `@source "./output"` alone is not enough:** `@source` *does* override
the **repo-level** `.gitignore` (so `output/Client.js`, `output/Modules/**`
etc. become scannable). But **Fable also writes
`output/fable_modules/.gitignore` whose content is `**`+`/`+`*`**, and
`@source` does **not** override a `.gitignore` nested *inside* the sourced
tree. The shell subtree stays excluded. Minimal isolated repro of this
exact `@tailwindcss/{node,oxide}` behaviour:
`tw4-source-nested-gitignore-repro/` (driver makes the same
`compile()` + `Scanner.scan()` + `build()` calls the vite plugin makes).

Two facts that previously misled investigation, now settled:

- `@source inline("…")` is **not** inert — it is scanner-independent and
  works. Earlier "inert" reports were a confound: probing a non-utility
  token (`zztopdiag` generates nothing *by design*) and/or testing a CSS
  without the `@theme --color-*` tokens that make `bg-sidebar` a *valid*
  utility in the first place.
- oxide only consults `.gitignore` when the scan base is a **descendant**
  of the dir holding `.git`/`.gitignore` (true for every ToolUp client:
  `.git` at repo root, Vite root at `src/<App>-Client`).

**The required pattern (validated by a real `vite build` on a reference
consumer emitting `.bg-sidebar` / `.bg-brand` autonomously):**

1. The canonical `index.css` carries `@source "./output";` (already in the
   marked region — every ToolUp Fable client uses `dotnet fable -o output`
   with the Vite root at the client dir, so the path is universal and
   drift-checked).
2. The canonical **`vite-fable-tailwind.mjs`** plugin
   (`fableTailwindGitignore()`, placed *before* `tailwindcss()` in
   `vite.config.mts`) empties `output/fable_modules/.gitignore` at build
   start AND re-empties it whenever Fable-watch rewrites it. This covers
   **`vite` (dev) and `vite build`** with no SDK repack and no
   FAKE-target coupling. A one-shot build step canNOT hold in
   `dotnet fable watch` — Fable regenerates the nested ignore on every
   recompile — which is why this is a Vite plugin, not a build script
   step. Validated: a clean `vite build` on a reference consumer emptied
   `**`+`/`+`*` → `` itself and emitted `.bg-sidebar` / `.bg-brand` with
   no manual step. The file is canonical (copied verbatim, drift-checked).

Do **not** instead un-ignore `output/` repo-wide (it is a build artifact),
hand-maintain an `@source inline(...)` safelist of shell classes (it
silently rots whenever the SDK's shell classes change), or wire the
neutralisation as a one-shot FAKE/build-script step (it cannot survive
`dotnet fable watch` in dev — the failure mode that surfaced this).

## Known residual deltas (intentionally NOT masked)

The compat base layer restores v4's changed *element defaults*
(border-color, ring width/colour, placeholder colour, button cursor,
dialog margin) so the jarring breakages are neutral. It deliberately does
**not** mask two minor cosmetic shifts — the correct fix is the migration
plan's Phase 5 (class-name modernisation), not a fragile shim:

- **shadow / rounded / blur scale renamed one notch** (`shadow` ≈ old
  `shadow-sm`, `rounded` ≈ old `rounded-sm`, …). A one-step visual nudge
  on the SDK shell's + apps' existing class names.
- **`space-y-*` / `divide-*` selector changed.** Layouts relying on the
  old `> :not([hidden]) ~ :not([hidden])` selector with inline children
  may shift; migrate those sites to flex/grid `gap`.

## Guardrails

- `update-all.ps1 -Npm` `--reject`s `tailwindcss,@tailwindcss/postcss,@tailwindcss/vite,postcss,autoprefixer,vite` and is opt-in — a routine update can't bump the toolchain.
- `check-drift.ps1` catches any consumer that hand-edits the contract.
- Toolchain version changes happen here, then re-stamp + re-run the check.
