# Gated SSR — authenticated, tenant-scoped & audience-targeted pages

`ToolUp.PublicRendering` serves anonymous public content by default. Phase 86 adds a per-page **audience** so the same SSR engine can power an authenticated intranet CMS or a per-client analytics portal, with structural tenant isolation (GP 4). This guide covers the two main patterns.

Audience is opt-in: a page that declares no `audience:` is `Public` and served exactly as before (GP 11).

## The audience model

```fsharp
type PageAudience =
    | Public                              // anonymous-visible (default)
    | Authenticated                       // any signed-in principal
    | ScopeGated of roles: string list    // holds one of the named roles
    | ClientGated of relationship: string // the principal whose scope == relationship
```

Set it from frontmatter:

| Frontmatter | Audience |
|---|---|
| (absent) / `audience: public` | `Public` |
| `audience: authenticated` | `Authenticated` |
| `audience: scope:editor,admin` | `ScopeGated ["editor"; "admin"]` |
| `audience: client:acme` | `ClientGated "acme"` |

The handler runs `AudienceGate.evaluate` after the SDK's scope + auth middleware: `Public` serves unchanged; `Authenticated` is `401` to anonymous; `ScopeGated` is `403` to a principal lacking every named role (checked against the same `AccessContext.canAccessModule` surface that gates module routes); `ClientGated` is `403` unless the relationship matches one of the principal's own scope ids. Platform admins bypass the role / relationship gates. Non-`Public` pages are excluded from `sitemap.xml`, Atom feeds, and static export.

## Pattern 1 — media-agency intranet (authenticated, per-team content)

A team-private handbook or dashboard. Mark pages `authenticated` (any member) or `scope:<role>` (role-restricted), and store team-private pages in the per-tenant overlay so each team sees only its own.

```fsharp
// File-backed page, visible to any signed-in member:
//   pages/handbook.md  →  ---\naudience: authenticated\n---
//
// A role-restricted editorial page:
//   pages/style-guide.md  →  ---\naudience: scope:editor\n---

ServerApp.empty
|> ServerApp.withConfig config            // auth + scope resolver already configured
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout)
|> ServerApp.run
```

Tenant-private runtime-authored pages go through the `IEntityStore<PublicPage>` overlay under the **author's own scope** — a team-A member writing `dashboard` stores it under `team-A`, and only a team-A principal resolves it. Team B requesting `dashboard` never sees team A's page (structural isolation, GP 4).

## Pattern 2 — client-view analytics portal (per-client gated pages)

A page per client showing *that client's* analytics. Model it as a `ClientGated` page whose body is a [`NarrativeFromData`](dynamic-ssr.md) projection driven by the viewing principal's scope, cached per-client via [Phase 84](84-ssr-render-cache.md).

```fsharp
// A content source claiming /portal/{client}, gated to that client.
let clientPortal =
    ContentSource.ofRoute "portal/{client}" (fun captures ctx -> async {
        // ctx is the resolved AccessContext — pull THIS principal's analytics.
        let! doc = buildClientNarrative ctx
        return Some (Narrative doc)
    })
```

Make the rendered page `ClientGated "<client>"` (via frontmatter on a file page, or by writing the page through the overlay with the audience set) so only the matching client — and platform admins — can load it. Because the render cache keys by scope and stores the audience, the expensive projection runs once per client per TTL window, and the gate still runs on every cache hit.

## Security notes

- **The gate is structural, not advisory.** `ClientGated` matches against the principal's *own* resolved scope ids, never a value the caller supplies. A principal cannot request another client's page by guessing the slug.
- **Cache hits re-run the gate.** A gated page cached for a scope is still authorization-checked per request using the stored audience, so a member who loses a role (or a different member in the same scope) is correctly denied on the next hit.
- **Nothing gated leaks to crawlers.** Sitemap, feeds, and static export emit only `Public` pages.

## See also

- [`docs/migrations/86-gated-ssr.md`](../migrations/86-gated-ssr.md) — the adoption / breaking-change summary.
- [`docs/platform/dynamic-ssr.md`](dynamic-ssr.md) — data-bound content sources (the body of a `ClientGated` analytics page).
- [`docs/migrations/84-ssr-render-cache.md`](../migrations/84-ssr-render-cache.md) — the render cache gated pages compose with.
