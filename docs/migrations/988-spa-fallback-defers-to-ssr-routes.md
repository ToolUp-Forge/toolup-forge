# The SPA shell no longer shadows server-rendered pages

**Ships in:** ToolUp.Platform.Server (`SpaFallbackMiddleware`, new `SpaFallbackPrecedence`),
ToolUp.PublicRendering (`composePublicRendering` registers it). Additive public surface. Behaviour change for
deployments that compose `ToolUp.PublicRendering` **and** ship a client bundle (`ServerConfig.PublicPath`
containing `index.html`), on the 0.24.0 draft.

**Affected versions:** every release that ships `withPublicRendering` (Phase 80c) on a deployment that also
serves a SPA bundle. **Upgrade to 0.24.0.** A deployment with no client bundle on disk (a pure SSR site, or
development with Vite serving the client) was never affected, and a SPA deployment that does not compose
PublicRendering behaves exactly as before.

## What was wrong

The SPA shell fallback runs straight after `UseStaticFiles`, ahead of scope resolution and the router, and
answers every extensionless `GET` outside `/api`, `/dev` and the probes with `PublicPath/index.html`. A
server-rendered page is an extensionless `GET` too. So once a client bundle was shipped, every SSR slug was
answered with the SPA shell: public pages, `Authenticated` / `ScopeGated` / `ClientGated` pages (which then
returned `200` with the shell instead of `401` / `403` or the sign-in redirect), and the interactive SSR
sign-in routes (`/auth/sign-in`, `/auth/callback`). Nothing failed loudly: the shell rendered, and the
client router showed its own not-found view.

## What changes

`SpaFallbackPrecedence` says which answers an extensionless `GET` first:

```fsharp
type SpaFallbackPrecedence =
    | ShellFirst   // the shell answers every candidate path (the default, unchanged)
    | RouterFirst  // the router answers first; the shell is served only where nothing did
```

`composePublicRendering` (and so `withPublicRendering` and `PublicRenderingServerApp.run`) registers
`RouterFirst`. Under it the fallback runs the rest of the pipeline first and serves the shell only to a `GET`
that nothing downstream answered (no response started, status `404`). So:

- an SSR slug is served by the page handler, a gated page returns its `401` / `403` / sign-in redirect, and
  the sign-in routes are reached;
- a path no route claims (a client-router deep link such as `/workspace/reports`) still gets the shell;
- `/api`, `/dev`, the probes and paths with an extension are never served the shell, in either order, and
  static files are still served upstream of the fallback.

A composition of your own that serves extensionless server routes alongside a SPA bundle opts in the same
way:

```fsharp
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

let services (s: IServiceCollection) = SpaFallbackPrecedence.useRouterFirst s
```

`useRouterFirst` is idempotent.

## What to check

Under `RouterFirst`, a SPA deep link passes through the full middleware stack before the shell is served,
where it used to be answered before scope resolution. A `PostMiddleware` that writes its own 404 page now
answers SPA deep links with that page; register it only for paths the client router does not own.

## Rollback

Pin 0.23.x. There is no switch back to `ShellFirst` while PublicRendering is composed, because that order
makes every SSR page unreachable.
