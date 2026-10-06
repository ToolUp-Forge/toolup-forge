# Gated SSR pages resolve the request principal and fail closed

**Ships in:** ToolUp.Platform.Server (`ScopeResolutionMiddleware`, the scoped `AccessContext` factory),
ToolUp.PublicRendering (`AudienceGate`). No public signature changes. Behaviour-breaking for deployments that
publish audience-gated pages (Phase 86), on the 0.24.1 draft.

**Affected versions:** every release that ships gated SSR, 0.5.22 through 0.23.x. **Upgrade to 0.24.1.** A
deployment that publishes no page with an `audience:` other than `public` is unaffected by the security
change, and needs no action.

## What changes

**1. Page routes resolve the request principal.** `ScopeResolutionMiddleware` used to resolve the principal
for `/api/*` and `/dev/*` only. It now resolves it for every request that reaches it, so the audience gate
judges a page request against the caller's real principal, read by the configured `IAuthProvider` from
whatever credential it accepts (a bearer token, a session cookie). Two differences from an API call keep a
public page cheap and cacheable: a page route is not bound to an anonymous session (no `Set-Cookie`), and an
anonymous visitor to a deployment with no anonymous surface is not reported as a resolver downgrade.

**2. A request with no resolved principal is anonymous.** When no subject was resolved, the scoped
`AccessContext` factory now returns an anonymous context with no permissions and no platform role, whatever
the deployment's surfaces are. Previously the result depended on the surfaces. Every consumer of
`AccessContext` outside `/api` is affected the same way: navigation entries marked authenticated, preview-link minting, request-time content sources and the
render-cache key now see the caller's actual principal.

**3. `ScopeGated` requires a held role.** A principal holds a role when its `ModulePermissions` has an entry
of that name with at least one permission. A principal with an empty permission map holds no role and
receives `403`; the gate previously reused `AccessContext.canAccessModule`, which reads an empty map as
unrestricted. `canAccessModule` itself is unchanged: for module
access an empty map still means unrestricted (GP 11), so a deployment with no RBAC configured keeps working.
The difference is deliberate. A page that names its roles has been restricted by its author, and a principal
granted no permissions has been granted none of those roles. `ScopeGated []` still admits any signed-in
principal.

`ClientGated` is unchanged in definition and is now judged against the real principal: an anonymous caller
receives `401`, and a principal whose own scope ids do not include the relationship receives `403`.

## What to do

- **Upgrade.** No code change is needed to adopt the fix.
- **Check `ScopeGated` readers.** A reader who reached a `scope:<role>` page only because their permission map
  was empty is now refused. Grant the role: a team member needs an entry for `<role>` in the team's
  permissions (`IPermissionStore.SetMemberPermissions`, or team defaults); a service account needs it in its
  declared set; a platform admin needs nothing. On a deployment with no team surface only platform admins can
  hold a role, so use `audience: authenticated` there instead.
- **Check the deployment's surfaces.** A deployment whose only surface is anonymous signs nobody in, so every
  gated page is `401`. Serve gated pages from a deployment with an authenticated surface.
- **Expect per-principal render-cache entries.** A signed-in reader's page renders are cached under that
  reader's scope, as the cache was designed to do. Previously every caller on an authenticated deployment
  shared one entry.

## Verification

`ToolUp.Platform.Tests` → `GatedSsr (Phase 86)` → `Phase 989 - gated pages through the composed pipeline`
drives real requests through the SDK's scope-resolution middleware, its `AccessContext` factory and the page
handler, on an authenticated, an anonymous and a mixed deployment, with no credentials, an invalid token, a
valid token without the role, a valid token with the role, and a session cookie, for `Authenticated`,
`ScopeGated` and `ClientGated` pages. Every cell returns `200`, `401` or `403` as the audience implies, and no
refused response carries the page body.

## Rollback

Pin the previous release. There is no configuration switch that restores the earlier behaviour.
