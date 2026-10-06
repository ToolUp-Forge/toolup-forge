# Directory roles on `AccessContext`, role-claim mapping and interactive SSR sign-in

**Ships in:** ToolUp.Platform.Core (`AccessContext.TokenRoles`, the `ClaimMapping` role and admission fields,
`InteractiveSignIn`, two config keys), ToolUp.Platform.Server (the scoped `AccessContext` factory),
ToolUp.PublicRendering (`AudienceGate`, the page handler's sign-in redirect), ToolUp.AuthProviders.Oidc (role and group
claim projection, the admission gate, `OidcSsrSignIn`). On the 0.24.0 draft, which is already a breaking release.

Builds on [the 0.24.0 fail-closed change](989-gated-ssr-fails-closed.md): gated pages resolve the request principal,
and `ScopeGated` requires a held role. This note covers what Phase 987 adds on top of it.

## What changes

**1. `AccessContext` gains a field, `TokenRoles: string list`.** The roles the identity provider asserted for the
principal (`AuthenticatedUser.Roles`), carried for `AuthenticatedUser` and `TeamMember` subjects and empty otherwise.
`ScopeGated` reads it beside `ModulePermissions`; `canAccessModule` / `hasPermission` do not, so module RBAC is
unchanged. **Source-breaking:** every full record literal of `AccessContext` stops compiling (FS0764).

**2. `ClaimMapping` gains five fields:** `RolesClaim`, `GroupsClaim` (`string option`), `GroupAliases`
(`Map<string, string>`), `AllowedTenants` and `RequiredRoles` (`string list`). **Source-breaking** for every full record
literal of `ClaimMapping`. `ClaimMapping.none` sets all five to "names nothing", so a mapping built from it behaves
exactly as before. `ClaimMapping.directoryRoles` names the conventional `roles` and `groups` claims.

**3. The OIDC provider maps role and group claims** when the mapping names them: a JSON string or an array of strings
becomes `AuthenticatedUser.Roles`, groups through `GroupAliases`. A claim of any other shape, or a group overage
(`_claim_names` naming the claim), **rejects the token** (`MappedClaimUnusable`). With `AllowedTenants` or
`RequiredRoles` set, a token outside the gate is rejected as `AdmissionRefused` (a new `JwtValidationError` case — an
exhaustive `match` over the type needs an arm for it). With no role claim named, nothing changes, including the
once-per-process warning about unmapped role claims, which now omits the claims the mapping does map.

**4. Two config keys:** `TOOLUP_OIDC_ROLES_CLAIM` and `TOOLUP_OIDC_GROUPS_CLAIM`, read by `AuthProvider.fromEnv`.
Unset maps no role claim.

**5. Interactive sign-in for gated pages, opt-in.** `OidcSsrSignIn.withInteractiveSignIn` mounts
`/auth/sign-in` and `/auth/callback` (authorization code with PKCE, `state` and a nonce) and registers an
`InteractiveSignIn`. With one registered, a gated page answers a credential-less browser navigation with a `302` to
the sign-in instead of `401`. The callback sets the session cookie with `SameSite=Lax`, so a link opened from another
site carries it; nothing changes for a deployment that does not compose it.

## What to do

- **Fix record literals.** Add `TokenRoles = []` to any `AccessContext` literal (or build from
  `AccessContext.unrestricted subject` with a copy-and-update expression), and build any `ClaimMapping` from
  `{ ClaimMapping.none with UserIdClaim = …; TenantIdClaim = … }` so a later field does not break it again. A
  hand-built `AccessContext` for a request should carry `AccessContext.tokenRolesFor subject user.Roles`.
- **Exhaustive matches over `JwtValidationError`** gain an `AdmissionRefused reason` arm.
- **To govern pages by directory groups**, follow [the worked example](../platform/gated-ssr.md#readers-governed-by-the-identity-providers-groups):
  name the claims, optionally gate admission, and compose the interactive sign-in.
- **Before naming a role claim on an existing deployment**, check what the IdP puts in it: a malformed value or a
  group overage now rejects that user's token. Also note that a mapped role becomes visible to
  `[<RequiresRole "<name>">]` on API methods through `AuthenticatedUser.Roles` — a role gate that used to deny
  everyone now admits holders of a same-named directory role.

## Verification

`ToolUp.Platform.Tests` → `GatedSsr (Phase 86)`:

- `Phase 987 — role and group claims (ClaimMapping)` drives the shipped `applyValidatedClaimMapping` and
  `applyAdmission`: string and array claims, aliases, every malformed shape, the overage refusal, both admission
  rules, and that a token role satisfies `ScopeGated` without granting a module permission.
- `Phase 987 — directory roles through the composed pipeline` sends RS256 tokens minted by the mock OIDC issuer
  through the real `OidcAuthProvider`, scope-resolution middleware, `AccessContext` factory and page handler: no token
  `401`, a non-member holding the role `200`, the wrong role `403`, no role `403`, a malformed claim `401`; a role
  granted then removed changes access with no change to the app; and the sign-in redirect applies to a
  credential-less navigation only, never to a `403`.
- `Phase 987 — interactive SSR sign-in` runs the whole flow against the mock issuer — page, sign-in, IdP, callback,
  page — and checks the cookie attributes, the no-loop `403`, a forged `state`, a missing state cookie and a foreign
  return URL.

## Rollback

Pin the previous release. Unsetting the role claims (and not composing `OidcSsrSignIn`) restores the pre-987
behaviour on 0.24.0 except for the record-literal changes, which are source-level.
