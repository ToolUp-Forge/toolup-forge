# Migration — Phase 148: self-referencing canonical for every page body

**Status:** one net-new opt-in capability. A deployment that does not call `withSelfCanonical` renders the `<head>` byte-for-byte as pre-148 (GP 11). No consumer action required.

## What changes

Before Phase 148 the SDK emitted a `<link rel="canonical">` only for a `Narrative`-bodied page that carried an explicit `CanonicalUrl`; `Markdown` / `Html` bodies — and `Narrative` bodies without one — got no canonical. A self-referencing canonical (the page's own absolute URL) is the cheapest defence against duplicate-content dilution from query-string variants (`?utm_*`, `?page=`, faceted params, trailing-slash variants), and is the SEO tag most consistently recommended for large generated catalogs.

Phase 148 adds:

- **`NarrativeLayout.canonicalFor baseUrl page`** — a pure helper producing `<link rel="canonical" href="{baseUrl}/{slug}">` for ANY body kind (trailing-slash-normalised; the root / `index` slug canonicalises to `{baseUrl}/`).
- **`NarrativeLayout.headTagsWith (selfCanonicalBaseUrl: string option) page`** — `headTags` with an optional self-referencing canonical prepended (for layouts that drive their head off this helper). `headTagsWith None` is byte-for-byte `headTags`.
- **`PublicRenderingServerApp.withSelfCanonical`** (compose, default off) — auto-injects the self-referencing canonical into the rendered `<head>` for **every** page **without editing any layout**, by wrapping the resolved `IPublicContentApi` so each resolved page gains a `head:canonical` frontmatter key (the Phase 111 envelope) which the page handler's existing head-injection step emits before `</head>`.

An explicit canonical always wins — a `Narrative` `CanonicalUrl` or a `head:canonical` envelope short-circuits the self-referencing one, so a page is never double-canonicalised.

**Multi-site origin (Phase 145).** When a `SiteRegistry` is active, the canonical origin for a satellite-host page is that satellite's `BaseUrl` (not the default-site `PublicBaseUrl`), so a page served on a satellite host self-canonicalises to that host — consistent with the per-site sitemap origins.

## Diff to apply

None required. To **opt a site into self-referencing canonicals**:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config        // PublicBaseUrl drives the default-site origin
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withSelfCanonical)             // ← opt in
|> ServerApp.run
```

The default-site origin is `ServerConfig.PublicBaseUrl` (set via `withConfig`); each `withSite` satellite uses its own `PublicSiteDef.BaseUrl`.

A layout that already emits its own hardcoded `<link rel="canonical">` (not via `headTags` / an explicit `CanonicalUrl` / a `head:canonical` envelope) should **not** also enable `withSelfCanonical`, since the SDK cannot detect an arbitrary layout-emitted canonical and would emit a second one. Layouts that drive their head off `NarrativeLayout.headTags` / `headTagsWith` are safe.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `PublicRenderingTests.fs` (`PublicRendering — Phase 148 self-referencing canonical`):
  - `canonicalUrlFor`: slug → `base/slug`; `index` / root → `base/`; trailing-slash-on-base normalised; nested slug.
  - `hasExplicitCanonical`: Narrative `CanonicalUrl` and `head:canonical` envelope both count; plain Markdown false.
  - `enrichPage`: adds `head:canonical` for `Markdown` / `Html` / `Narrative` bodies; defers to an explicit canonical.
  - `wrap`: enriches `GetPage` / `GetPageInContext`, leaves `ListPages` unenriched (no per-entry canonical pollution of sitemap / listings).
  - enriched page → `PageHeadInjection` emits the canonical before `</head>`.
  - `headTagsWith None` == `headTags` (byte-for-byte); `headTagsWith (Some base)` prepends the self-canonical for a non-Narrative page; explicit canonical still wins.
- A deployment that never calls `withSelfCanonical` — existing page-handler / head tests stay green (no extra canonical emitted).

## Rollback

Revert the Phase 148 commit. `withSelfCanonical` is additive and default-off; the `canonicalFor` / `headTagsWith` / `SelfCanonical.wrap` surface is new and uncalled by existing layouts. No data migration, no cache purge (the `head:canonical` envelope is computed per-resolve, never persisted).
