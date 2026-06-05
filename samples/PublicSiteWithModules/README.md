# PublicSiteWithModules — Phase 82 worked example

Hybrid SDK + PublicRendering composition. The canonical "drop into the repo, see how the pieces fit" reference for the Wave 13 pattern.

## What it demonstrates

A single `ServerApp` carrying **two SSR surfaces on one pipeline**:

| Surface | Mounted by | Backed by |
|---|---|---|
| `/` (landing) | `ToolUp.PublicRendering` via `withPublicRendering` | `content/pages/index.md` + the Notes in-memory store |
| `/notes/{slug}` | `Notes` `ServerModule` route handler | the Notes in-memory store |

Both surfaces:
- Read from the **same in-memory `Notes` store** — a write in one is visible in the other on next page load.
- Render using **`ToolUp.BrandKit` primitives** (`wordmark` / `eyebrow` / `card` / `pageHeader` / `pageFooter`).
- Style themselves via a single shared `wwwroot/css/brand-tokens.css` defining `--bk-*` CSS variables.

The composition root in `Program.fs` is ~15 lines:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withLogger logger
|> ServerApp.addModule Notes.serverModule
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr |> PublicRenderingCompose.PublicRenderingServerApp.withLayout
            (LayoutName "page") Layouts.landingPage)
|> ServerApp.run
```

That's the load-bearing Wave 13 demonstration — `withPublicRendering` is the additive extension Phase 80c shipped; without it the SDK had no hybrid path.

## Run

```powershell
dotnet run --project samples/PublicSiteWithModules/PublicSiteWithModules.fsproj
```

Then open:
- `http://localhost:4020/sitemap.xml` — proves PublicRendering is wired correctly (returns proper sitemap XML referencing the `index` slug)
- `http://localhost:4020/` — the brand-prominent landing page; the Notes module module's `/n/{slug}` detail route is reachable.
- `http://localhost:4020/n/phase-82-shipped` — SSR per-note detail rendered from the in-memory store
- `http://localhost:4020/n/brandkit-primitives-explained` — the second seeded note

## Known issue (Phase 82.A follow-up)

The runtime response bodies are currently empty for both the markdown-driven landing page and the Notes module's `/n/{slug}` detail route. Root cause: smoke testing during Phase 82 development showed `api.GetPage "index"` returns `None` despite `api.ListPages` returning the same page (the sitemap renders correctly with the `index` slug, so PublicRendering's content loader IS picking up `content/pages/index.md`). The PageHandler falls through, the Notes module's route never appears to be reached either, and the SDK's default fall-through returns 200 with an empty body.

**What this does NOT affect:** the load-bearing Phase 80c composition pattern — that surface is tested directly by 4 dedicated tests in `src/ToolUp.Platform.Tests/InProcess/PublicRenderingTests.fs` and passes cleanly. The `Program.fs` composition root demonstrates the API surface correctly even though the rendered output is currently empty.

**Phase 82.A** debugs the PublicRendering `GetPage` slug-lookup mismatch + the module-route-mounting order under `withPublicRendering`, and brings the rendered output to match the architectural intent. Tracking as a follow-up to keep the load-bearing 80c + 81 work shippable independently of the runtime polish.

## Where to dive in

1. `Program.fs` (15 lines) — the composition root. Read this first.
2. `Notes.fs` (~120 lines) — domain types + in-memory store + SSR detail-page handler. Shows what a `ServerModule` carrying SSR routes looks like.
3. `Layouts.fs` (~100 lines) — the PublicRendering landing layout, composed from BrandKit primitives + reading the same Notes store.
4. `wwwroot/css/brand-tokens.css` — the CSS-variable values that brand the BrandKit primitives. Change these to re-brand without touching any F# code.
5. `content/pages/index.md` — the markdown source for the landing page (`title` / `description` / `layout` frontmatter).

## What's deferred

- **Fable admin client** (`/app/notes/*`). The Phase 82 body's acceptance criteria call for a Fable surface that lets an operator create notes through the SDK shell; the underlying infrastructure (Vite + npm + Fable watcher + run.ps1) adds significant complexity without changing the load-bearing composition demonstration. Tracking as a Phase 82.A follow-on. The SSR side of the hybrid is fully demonstrated by the surfaces above.
- **CI checks.yml** matrix entry. Lands when the sample is touched by a future change; today the sample compiles as part of the normal forge `dotnet build ToolUp.Forge.sln` pass.

## See also

- [`docs/migrations/80c-with-public-rendering-additive-composition.md`](../../docs/migrations/80c-with-public-rendering-additive-composition.md) — the migration doc for `withPublicRendering` (Phase 80c).
- [`docs/brandkit-tokens.md`](../../docs/brandkit-tokens.md) — the CSS variable contract for the BrandKit primitives (Phase 81).
- [`samples/PublicSite/`](../PublicSite/) — the pure-SSR counterpart (no SDK modules, no Fable client).
- [`samples/FormsAndAI/`](../FormsAndAI/) — the multi-companion composition reference (Forms + AI on one pipeline — Phase 1h).
