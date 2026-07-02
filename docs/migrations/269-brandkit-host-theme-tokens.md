# Migration 269 — brandkit → hosted-tree theme-token bridge (`HostThemeTokens`)

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

A Phase 110-hosted typed-tree resolved its own theme tokens with no bridge to the deployment's brand,
so a hosted view ignored the tenant's palette entirely. This phase ships a neutral projection that
flows the deployment's brand into the CSS-variable token bag a hosted renderer consumes, so one
deployment theme drives both Feliz modules and hosted-tree modules.

- **New file** `src/ToolUp.BrandKit/HostThemeTokens.fs` (namespace `ToolUp.BrandKit`):
  - `HostThemeTokens` — a neutral `{ Variables: Map<string,string> }` of CSS-variable name → value.
  - `ofBrandKitValues : Map<string,string> -> HostThemeTokens` — projects supplied values onto the
    canonical BrandKit primitive set (the `Tokens.fs` `--bk-*` names); blank/absent primitives are
    omitted so the renderer falls back to its own defaults.
  - `withPaletteOverrides : (string*string) list -> HostThemeTokens -> HostThemeTokens` — layers the
    Phase 223 per-tenant palette (per-tenant override **wins**; adds palette-only vars like
    `--color-brand` / `--pos` / `--neg`). Scope-bound (GP 4): a pure per-call fold over the caller's
    own resolved overrides, so a tenant's palette can never leak into another's — there is no shared
    mutable state.
  - `toDeclarations` / `toRootCss` — render a **deterministic (sorted)** `:root { … }` block so a
    Phase 197 visual snapshot of a hosted view is byte-stable and a brandkit change to the view is
    caught by the existing snapshot gate. An empty bag renders `""` (GP 13).

`HostThemeTokens` takes the per-tenant palette as a plain `(name, value) list` — exactly the shape
`ToolUp.Platform.Branding.PaletteOverrides` already carries — so `ToolUp.BrandKit` keeps its minimal
`Giraffe.ViewEngine`-only dependency graph and **never pulls `ToolUp.Platform.Core`**. `ClientHostBridge.fs`
is the conceptual client-side consumption point and is **not modified** — a hosted renderer (SSR or
CSR) consumes the neutral `Map<string,string>` the consumer passes in.

## How to adopt (opt-in)

```fsharp
// Base brandkit values the deployment themes (bk custom-property names → values):
let tokens =
    HostThemeTokens.ofBrandKitValues (Map [ Tokens.AccentVar, "#2563eb"; Tokens.InkVar, "#111827" ])
    // Layer the active tenant's Phase 223 palette (overrides win, scope-bound):
    |> HostThemeTokens.withPaletteOverrides branding.PaletteOverrides

// A hosted renderer injects the deterministic :root block so its tree paints with the brand:
let rootCss = HostThemeTokens.toRootCss tokens   // ":root { --bk-accent: …; --color-brand: …; }"
```

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostThemeTokens"
```

## Rollback

Delete `src/ToolUp.BrandKit/HostThemeTokens.fs` + its `<Compile>` entry, delete
`InProcess/HostThemeTokensTests.fs` + its `<Compile>` and `Program.fs` registration. No runtime impact.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in client-tree-hosting theme bridge. No
current matrix consumer hosts a typed-tree UI; a deployment that wires no theme bridge is
byte-for-byte unchanged (GP 11/13).
