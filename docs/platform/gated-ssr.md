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
- `ScopeGated roles` is `401` to an anonymous caller and `403` to a principal that holds none of the named roles. A principal holds a role when its `ModulePermissions` has an entry of that name with at least one permission, **or** its identity provider asserted it (`AccessContext.TokenRoles` — see [readers governed by the identity provider's groups](#readers-governed-by-the-identity-providers-groups)). **A principal with no configured permissions and no asserted role holds no role.** This differs from module access, where an empty permission map means unrestricted (GP 11): a page that names its roles has been restricted by its author. `ScopeGated []` admits any signed-in principal.
- `ClientGated relationship` is `401` to an anonymous caller and `403` unless the relationship is one of the principal's own scope ids (its user id, its active team id, or its resolved config scope).

Platform admins bypass the role / relationship gates. Non-`Public` pages are excluded from `sitemap.xml`, Atom feeds, and static export.

What a deployment's surfaces admit decides who can be signed in at all. On a deployment whose only surface is anonymous, every request resolves to an anonymous session, whatever credential it carries, so every gated page is `401`. Serve gated pages from a deployment with an authenticated surface (`individual`, `team`, or a mix with `anonymous`). Roles come from team permissions (or a service account's declared set), or from the identity provider's role and group claims when the deployment maps them; without a claim mapping, a `ScopeGated` page needs a team surface to be readable by anyone but a platform admin.

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

## Readers governed by the identity provider's groups

A report restricted to named groups in an organisation's directory — readable by exactly those people, who need not be app users or team members — is a `ScopeGated` page whose roles come from the token. Three pieces, all opt-in (GP 11):

1. **Map the claims.** `AuthConfig.ClaimMapping.RolesClaim` / `GroupsClaim` name the token's role and group claims (`ClaimMapping.directoryRoles` names the conventional `roles` and `groups`; for an env-composed deployment, `TOOLUP_OIDC_ROLES_CLAIM` / `TOOLUP_OIDC_GROUPS_CLAIM`). Their values become **directory roles** (`AuthenticatedUser.DirectoryRoles`), and the scoped `AccessContext` carries them as `TokenRoles` for a signed-in principal. `GroupAliases` renames a group's object id to the name a page uses. Module RBAC is not affected: a directory role grants page access, never a module permission.
   **A directory role gates pages and nothing else (Phase 993).** It does not satisfy `[<RequiresRole "…">]` on any API method, so whoever administers the directory decides who *reads* the reports without being able to grant *API privilege* by naming a role or group. A deployment that deliberately wants a directory role to be an API role too names it in `ClaimMapping.ApiRoleGrants` (`Set [ "report-publisher" ]`; env: `TOOLUP_OIDC_API_ROLE_GRANTS=report-publisher`), matched after `GroupAliases` — an aliased group is granted by its alias, never by its raw object id. The allow-list is empty by default. A blank or whitespace-bearing entry, or `PlatformAdmin` (resolved server-side from `IPlatformAdminStore`, never from a token), refuses startup.
2. **Optionally gate admission.** `AllowedTenants` (with `TenantIdClaim`) and `RequiredRoles` refuse a token at authentication — `401` — when its tenant is not listed or it holds none of the roles, so a multi-tenant app registration admits only the organisation you mean.
3. **Let a cold link sign in.** `OidcSsrSignIn.withInteractiveSignIn` mounts a server-side authorization-code sign-in (PKCE, `state`, nonce) and registers an `InteractiveSignIn`, so a browser that opens a gated page with no credential is redirected to sign in and returned to the page, instead of seeing a bare `401`.

### Worked example — Microsoft Entra app roles

In the Entra admin centre, on the app registration, define an app role (value `finance-reader`) and assign it to the security group whose members should read the reports; add `https://reports.example.com/auth/callback` as a web redirect URI. Entra then emits `roles: ["finance-reader"]` in the id_token of every member — and of nobody else. Granting or removing access is a change to the group in the directory; it takes effect on the reader's next sign-in, with no change to the app.

```fsharp
open ToolUp.Platform
open ToolUp.AuthProviders

// Placeholder ids — use your tenant id and the app registration's client id.
let tenantId = "00000000-0000-0000-0000-000000000000"
let clientId = "11111111-1111-1111-1111-111111111111"
let issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0"

let authConfig: AuthConfig = {
    Issuer = Some issuer
    Audience = Some clientId // the id_token's audience is the client id
    KeySource = JwksDiscovery issuer
    TokenLocation = BearerOrCookie AuthSession.CookieName
    ClockSkewSeconds = None
    AcceptedAlgorithms = None
    PreferOidWhenPresent = None
    ClaimMapping =
        Some {
            ClaimMapping.directoryRoles with
                UserIdClaim = Some "oid"
                TenantIdClaim = Some "tid"
                AllowedTenants = [ tenantId ]
        }
}

let signIn =
    OidcSsrSignIn.defaults
        $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize"
        $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token"
        clientId
        "https://reports.example.com/auth/callback"

ServerApp.empty
|> ServerApp.withAuth (OidcAuthProvider.fromConfig None authConfig)
|> OidcSsrSignIn.withInteractiveSignIn (new System.Net.Http.HttpClient()) signIn // before the page catch-all
|> PublicRenderingCompose.withPublicRendering (fun pr -> pr |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout)
|> ServerApp.run

// pages/q3-results.md  →  ---\naudience: scope:finance-reader\n---
```

A confidential client sets `ClientSecret = Some <secret from your secret store>` on the sign-in configuration; a public client relies on PKCE alone.

**Groups instead of app roles.** Map `GroupsClaim` (configure the app registration to emit the `groups` claim) and alias the group's object id to the name the page uses: `GroupAliases = Map [ "<group object id>", "finance-reader" ]`. Prefer app roles where you can, or emit only the groups assigned to the application: a user in more groups than fit in a token gets an **overage** (`_claim_names` instead of `groups`), and the provider rejects such a token rather than read it as "no groups" — the SDK does not call the directory to resolve it.

### What the sign-in does, and what it refuses

- `GET /auth/sign-in?returnUrl=…` seals `state`, a nonce, a PKCE verifier and the return URL into a short-lived DataProtection cookie and redirects to the authorization endpoint. A return URL that is not a local path becomes `/`.
- `GET /auth/callback` checks `state`, exchanges the code, validates the token through the registered `IAuthProvider` (the same rules as every later request — claim mapping and admission gate included), checks the nonce, sets the session cookie the provider reads and redirects back.
- The session cookie is `HttpOnly`, `SameSite=Lax` and `Secure` on HTTPS. **Lax rather than the SPA flow's Strict is the point**: the reader arrives by a cross-site navigation (a link in an email), which a `Strict` cookie is withheld from. `Lax` is still withheld from cross-site sub-requests and `POST`s, and mutating `/api` calls stay behind the CSRF middleware.
- Only a credential-less browser navigation (`GET` / `HEAD` accepting `text/html`) is redirected; an API-style fetch keeps the `401`. A signed-in reader who lacks the role gets `403`, never a redirect, so a sign-in cannot loop. A forged or expired `state`, an IdP error, a failed exchange, a refused token or a nonce mismatch ends on an error page with no session cookie.

## Readers outside the app — a publication audience

A `ScopeGated` page read through a directory role still treats the reader as an app user. It signs them in as an individual user, who reaches every `/api` route that declares nothing narrower (see the security notes), and the page reads the *reader's* scope. A **publication audience** is for readers who must never be app users: a client's board, an external group, readers of one team's reports (Phase 996).

1. **Name who the app's users are, then who the readers are.** `ClaimMapping.RequiredRoles` is the member rule. `ClaimMapping.PublicationReaders` names directory roles, matched after `GroupAliases` so the group's object id stays in configuration. A token the member rule refuses but a reader role admits is admitted as a **publication reader** (`AuthenticatedUser.Admission = PublicationReader`). The provider refuses to build with readers and no member rule.
2. **Publish the page to them.** `PageAudience.Publication { Readers = [ "report-readers" ]; PublishingScope = "team-<id>" }`, or `audience: publication:team-<id>:report-readers` in frontmatter. Only a holder of a named reader role is admitted. App members, team members and platform admins outside the group get `403`.
3. **Read the publishing scope, not the reader's.** Build the page body from `PublicationRead.latestNarrativeIn services audience slug tag access`. It reads the latest narrative a run tagged `tag` in `PublishingScope` once the audience admits the reader. It withholds a narrative that cites a fact no longer disclosable at `FactNarrativePublication`, and audits the read.

A publication reader gets `403` on every `/api` route that does not admit anonymous callers (the API, the assistant, module routes, team management). It also gets `403` on every page except `Public` pages and its own publications, and on search. Every decision on a publication page, and every page decision for a reader, is written to the event store under the publishing scope (source module `_publication`): the reader, the scope and the page. The sign-in is the same `OidcSsrSignIn.withInteractiveSignIn`. Migration and the full contract: [`docs/migrations/996-publication-audience.md`](../migrations/996-publication-audience.md).

## Worked example — gated reports beside a RAG team app

A team app that computes with AI and retrieval can publish what it computes, to an audience, from the same
deployment. `RAGCompose.withRAG` and `PublicRenderingCompose.withPublicRendering` are both
`ServerApp -> ServerApp`, so they stack on one pipeline, and the gated pages are judged against the same
`AccessContext` the team app's `/api` calls resolve: the same identity provider, the same team roles, the same
token roles.

```fsharp
open ToolUp.Platform
open ToolUp.PublicRendering
open ToolUp.RAG
open ToolUp.RAG.RAGCompose

ServerApp.empty
|> ServerApp.withConfig config            // team surface, PublicPath = the client bundle, PublicRendering enabled
|> ServerApp.withAuth authProvider        // one identity provider for the app and the pages
|> RAGCompose.withRAG factory providerProfile embedder (fun rag -> rag |> RAGServerApp.withTopK 8)
|> ToolUp.AuthProviders.OidcSsrSignIn.withInteractiveSignIn (new System.Net.Http.HttpClient()) signIn
|> PublicRenderingCompose.withPublicRendering (fun pr -> pr |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout)
|> ServerApp.run                          // the combined pipeline ends here

// pages/q3-results.md  →  ---\naudience: scope:finance-reader\n---
```

**End the pipeline in `ServerApp.run`, never `RAGServerApp.run`.** `RAGServerApp.run` (like
`PublicRenderingServerApp.run`) is a terminal: it composes RAG and starts the host, so nothing can be stacked
after it. Use the `with*` composers for every companion and call `ServerApp.run` once, last. Set base
configuration (`withConfig`, `withAuth`, `withStorage`) on the outer pipeline before the first composer; the
delegating helpers on `RAGServerApp` / `PublicRenderingServerApp` overwrite it if called inside a configurator.

How the routes divide on that pipeline:

| Request | Answered by |
|---|---|
| a static file under `PublicPath` (`/main.js`, `/logo.svg`) | static files, unchanged |
| `/api/*` — the platform's APIs and the AI / RAG endpoints `withRAG` mounts | the router; never the SPA shell |
| a content slug (`/about`, `/q3-results`) | the SSR page handler, gated by its `audience:` |
| `/sitemap.xml`, feeds, `/auth/sign-in`, `/auth/callback` | the router |
| an extensionless path no route claims (`/workspace/reports`) | the SPA shell (`PublicPath/index.html`) |

The shell is the last resort: composing either layer — PublicRendering, or the interactive sign-in
(`OidcSsrSignIn.withInteractiveSignIn` / `OidcSsrSignIn.register`) — registers `SpaFallbackPrecedence.RouterFirst`,
so the SPA fallback lets the router answer first and serves the shell only to a `GET` nothing answered. A gated page
therefore returns `401` / `403` (or the sign-in redirect) rather than the shell, even with a client bundle
shipped. Releases before 0.24.1 answered every extensionless `GET` with the shell once a bundle was present, so
no SSR page was reachable on such a deployment; see
[the 0.24.1 migration note](../migrations/988-spa-fallback-defers-to-ssr-routes.md). Keep page slugs distinct
from the client router's paths: a slug that names a client route is served by the page handler, not the SPA.

Either layer alone is sufficient, and composing both leaves one registration (`useRouterFirst` is idempotent).
A deployment that composes interactive sign-in without PublicRendering — an SPA that only needs the
server-side sign-in — therefore still reaches `/auth/sign-in` and `/auth/callback` with a client bundle shipped.

PublicRendering's companion guard still holds on this pipeline: a second `withPublicRendering` is refused at
compose time, whichever order it and `withRAG` ran in.

## Security notes

- **The gate is structural, not advisory.** `ClientGated` matches against the principal's *own* resolved scope ids, never a value the caller supplies. A principal cannot request another client's page by guessing the slug.
- **The gate fails closed.** A page route whose principal could not be resolved (no credentials, a credential the provider rejects, a resolver failure) is judged as anonymous, never as signed in. Releases before 0.24.1 did not resolve the principal on page routes; see [the 0.24.1 migration note](../migrations/989-gated-ssr-fails-closed.md).
- **A public page carries no session cookie.** Page routes resolve the principal but are not bound to an anonymous session, so a public page served to an anonymous visitor sets no cookie and stays cacheable by a CDN.
- **Cache hits re-run the gate.** A gated page cached for a scope is still authorization-checked per request using the stored audience, so a member who loses a role (or a different member in the same scope) is correctly denied on the next hit.
- **Nothing gated leaks to crawlers.** Sitemap, feeds, and static export emit only `Public` pages.
- **Without a member rule, every principal the issuer admits is an app user.** A signed-in principal in no team resolves to an individual-user subject, and the default `/api` route requirement (`userOrTeam`) admits it, team creation's route included. If your identity provider serves more people than your app's users, set `ClaimMapping.RequiredRoles`. A publication audience requires it, and confines its readers to their publications.
- **A malformed role claim fails closed.** A role or group claim that is not a string or an array of strings, or a group overage, rejects the token (`401`) rather than signing the reader in with no roles.

## See also

- [`docs/migrations/86-gated-ssr.md`](../migrations/86-gated-ssr.md) — the adoption / breaking-change summary.
- [`docs/migrations/989-gated-ssr-fails-closed.md`](../migrations/989-gated-ssr-fails-closed.md) — page routes resolve the principal, and `ScopeGated` requires a held role.
- [`docs/migrations/987-directory-roles-and-ssr-sign-in.md`](../migrations/987-directory-roles-and-ssr-sign-in.md) — `AccessContext.TokenRoles`, the `ClaimMapping` role and admission fields, and interactive sign-in.
- [`docs/migrations/988-spa-fallback-defers-to-ssr-routes.md`](../migrations/988-spa-fallback-defers-to-ssr-routes.md) — SSR pages and the SPA shell on one deployment.
- [`docs/migrations/996-publication-audience.md`](../migrations/996-publication-audience.md) — readers outside the app: `PageAudience.Publication`, `ClaimMapping.PublicationReaders`, confinement and the publication audit.
- [Claim mapping](../companions/auth-providers.md) — the identity half of `ClaimMapping`.
- [`docs/platform/dynamic-ssr.md`](dynamic-ssr.md) — data-bound content sources (the body of a `ClientGated` analytics page).
- [`docs/migrations/84-ssr-render-cache.md`](../migrations/84-ssr-render-cache.md) — the render cache gated pages compose with.
