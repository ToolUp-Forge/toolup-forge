# Migration — Phase 86: Gated, tenant-scoped & audience-targeted SSR

**Status:** additive, opt-in. `PublicPage.Audience` defaults to `Public`, so a deployment serving only public pages is **byte-for-byte unchanged** (GP 11). Gating activates per-page via the `audience:` frontmatter key.

## What changes

`ToolUp.PublicRendering` pages can now be **authenticated, scope-isolated, and audience-targeted** — the enabler for an intranet CMS and client-view portals. Before Phase 86 the companion served anonymous public content only.

New public surface (in `ToolUp.PublicRendering`):

| Symbol | Where | Purpose |
|---|---|---|
| `PageAudience` | `Shared/PublicContentTypes.fs` | `Public \| Authenticated \| ScopeGated of roles \| ClientGated of relationship` |
| `PublicPage.Audience` | `Shared/PublicContentTypes.fs` | New field, defaults `Public` |
| `PageAudience.parse` | `Shared/PublicContentTypes.fs` | Parse the `audience:` frontmatter value |
| `PublicPage.isPublic` | `Shared/PublicContentTypes.fs` | Exclusion predicate for sitemap / feeds / export |
| `AudienceGate.evaluate` | `Server/AudienceGate.fs` | The pure authorization decision (`Allow` / `RequireAuthentication` / `Forbidden`) |

## The `audience:` frontmatter key

```yaml
---
title: Team Handbook
audience: authenticated      # any signed-in principal (401 to anonymous)
---
```

Recognised values: `public` (default) · `authenticated` · `scope:editor,admin` (holds one of the named roles) · `client:acme` (the principal whose own scope is `acme`). Anything unrecognised → `Public` (fail-open per GP 11 — a typo never silently hides a page).

## Authorization model

`AudienceGate.evaluate` runs **after** the SDK's existing scope-resolution + auth middleware, reusing the same surface that gates module routes — no new auth machinery:

- **`Public`** → always served (unchanged).
- **`Authenticated`** → `401` to anonymous, `200` to any principal.
- **`ScopeGated roles`** → `401` anon; `403` to a principal that lacks all named roles (role check via `AccessContext.canAccessModule`, so an unconfigured/unrestricted principal passes per GP 11); platform admins bypass.
- **`ClientGated relationship`** → `403` unless `relationship` is one of the principal's own scope ids (user / team / claim scope) — structural per GP 4; platform admins bypass.

The gate runs **before rendering** (a forbidden request never pays the render cost) and **on render-cache hits too**: `RenderedPage` now carries the page's audience, so a gated page cached per-scope ([Phase 84](84-ssr-render-cache.md)) still enforces role differentiation *within* a scope (two team members where only one holds the gating role) on every hit. Denied responses carry `X-Robots-Tag: noindex` + `Cache-Control: no-store`.

## Tenant-scoped content

`PublicContentApiImpl` gains a tenant-scoped overlay tier between the public `_public` overlay and the content sources: an authenticated principal's own `StorageScope` is consulted, so a team / user serves private pages no other tenant can read (GP 4 — the scope id comes from the resolved `AccessContext`, never caller input). Anonymous requests have no config scope and skip the tier entirely (byte-for-byte pre-86).

## Crawler / export exclusion

Non-`Public` pages are excluded from `sitemap.xml`, Atom feeds, and static export by default, so a gated slug never leaks to a crawler or to disk. Static export (which has no per-request principal) emits only `Public` pages.

## Breaking change — `PublicPage` construction

`PublicPage` gains a required `Audience` field. Code that constructs a `PublicPage` record literal must add `Audience = PageAudience.Public` (or another audience). The SDK's own construction sites, the `MarkdownContentLoader` (which parses `audience:`), and the publisher / bridge are updated. Custom construction sites in consumer code need the one-field addition.

## Verification

- Builds clean; `dotnet run --project Build.fsproj -- VerifyAll` — the `GatedSsr (Phase 86)` suite covers `PageAudience.parse`, the `AudienceGate.evaluate` matrix (anon / authenticated / role-gated / client-gated × allowed / denied + the platform-admin bypass), the handler authorization pre-check (`401` / `403` / `200`), sitemap exclusion, and cross-tenant isolation over a real blob entity store.
- A deployment using only `Public` pages produces identical output to pre-86.

## Rollback

Set every page's audience back to `Public` (or remove the `audience:` keys). With all pages `Public`, the gate is a no-op `Allow` and the tenant overlay tier is never reached (anonymous requests skip it; authenticated requests just see one more miss). The field and `AudienceGate.fs` remain inert.

## See also

- [`docs/platform/gated-ssr.md`](../platform/gated-ssr.md) — intranet + client-portal patterns.
- [`docs/migrations/84-ssr-render-cache.md`](84-ssr-render-cache.md) — the render cache the gate composes with (audience stored per-entry).
