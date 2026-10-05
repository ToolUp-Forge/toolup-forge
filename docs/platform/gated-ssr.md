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

The handler runs `AudienceGate.evaluate` against the request's `AccessContext`. The SDK's `ScopeResolutionMiddleware` resolves the principal on page routes exactly as it does for `/api` calls: the configured `IAuthProvider` reads whatever credentials it reads (a bearer token, a session cookie), and the subject resolver turns the result into the request's subject. A request whose principal was not resolved, for any reason, is anonymous.

- `Public` serves unchanged.
- `Authenticated` is `401` to an anonymous caller, including one that presented an invalid or expired credential.
- `ScopeGated roles` is `401` to an anonymous caller and `403` to a principal that holds none of the named roles. A principal holds a role when its `ModulePermissions` has an entry of that name with at least one permission. **A principal with no configured permissions holds no role.** This differs from module access, where an empty permission map means unrestricted (GP 11): a page that names its roles has been restricted by its author. `ScopeGated []` admits any signed-in principal.
- `ClientGated relationship` is `401` to an anonymous caller and `403` unless the relationship is one of the principal's own scope ids (its user id, its active team id, or its resolved config scope).

Platform admins bypass the role / relationship gates. Non-`Public` pages are excluded from `sitemap.xml`, Atom feeds, and static export.

What a deployment's surfaces admit decides who can be signed in at all. On a deployment whose only surface is anonymous, every request resolves to an anonymous session, whatever credential it carries, so every gated page is `401`. Serve gated pages from a deployment with an authenticated surface (`individual`, `team`, or a mix with `anonymous`). Roles come from team permissions (or a service account's declared set), so a `ScopeGated` page needs a team surface to be readable by anyone but a platform admin.

## Pattern 1 — media-agency intranet (authenticated, per-team content)

A team-private handbook or dashboard. Mark pages `authenticated` (any member) or `scope:<role>` (role-restricted), and store team-private pages in the per-tenant overlay so each team sees only its own.

```fsharp
// File-backed page, visible to any signed-in member:
//   pages/handbook.md  →  ---\naudience: authenticated\n---
//
// A role-restricted editorial page:
//   pages/style-guide.md  →  ---\naudience: scope:editor\n---

open ToolUp.PublicRendering

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
- **The gate fails closed.** A page route whose principal could not be resolved (no credentials, a credential the provider rejects, a resolver failure) is judged as anonymous, never as signed in. Releases before 0.24.0 did not resolve the principal on page routes; see [the 0.24.0 migration note](../migrations/989-gated-ssr-fails-closed.md).
- **A public page carries no session cookie.** Page routes resolve the principal but are not bound to an anonymous session, so a public page served to an anonymous visitor sets no cookie and stays cacheable by a CDN.
- **Cache hits re-run the gate.** A gated page cached for a scope is still authorization-checked per request using the stored audience, so a member who loses a role (or a different member in the same scope) is correctly denied on the next hit.
- **Nothing gated leaks to crawlers.** Sitemap, feeds, and static export emit only `Public` pages.

## See also

- [`docs/migrations/86-gated-ssr.md`](../migrations/86-gated-ssr.md) — the adoption / breaking-change summary.
- [`docs/migrations/989-gated-ssr-fails-closed.md`](../migrations/989-gated-ssr-fails-closed.md) — page routes resolve the principal, and `ScopeGated` requires a held role.
- [`docs/platform/dynamic-ssr.md`](dynamic-ssr.md) — data-bound content sources (the body of a `ClientGated` analytics page).
- [`docs/migrations/84-ssr-render-cache.md`](../migrations/84-ssr-render-cache.md) — the render cache gated pages compose with.
