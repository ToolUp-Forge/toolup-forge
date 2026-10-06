# Directory roles on `AccessContext`, role-claim mapping and interactive SSR sign-in

**Ships in:** ToolUp.Platform.Core (`AccessContext.TokenRoles`, `AuthenticatedUser.DirectoryRoles`, the
`ClaimMapping` role, admission and API-grant fields, `InteractiveSignIn`, three config keys), ToolUp.Platform.Server (the scoped `AccessContext` factory),
ToolUp.PublicRendering (`AudienceGate`, the page handler's sign-in redirect), ToolUp.AuthProviders.Oidc (role and group
claim projection, the admission gate, `OidcSsrSignIn`). On the 0.24.1 draft, which is already a breaking release.

Builds on [the 0.24.1 fail-closed change](989-gated-ssr-fails-closed.md): gated pages resolve the request principal,
and `ScopeGated` requires a held role. This note covers what Phase 987 adds on top of it, as amended by Phase 993
before the release: **role mapping grants page audiences by default, and API roles only by an explicit allow-list.**

## The default, in one paragraph

A role or group mapped from the token is a **directory role**. It satisfies `ScopeGated` page audiences and nothing
else: no `[<RequiresRole>]` gate reads it. It becomes an **API role** (`AuthenticatedUser.Roles`, which
`IAuthContext.HasRole` and therefore every `[<RequiresRole "…">]` reads) only when `ClaimMapping.ApiRoleGrants` — or
`TOOLUP_OIDC_API_ROLE_GRANTS` — names it. The allow-list is empty by default, so turning role mapping on lets the
identity provider's directory decide who **reads** published content without letting whoever administers that
directory grant **API privilege** by naming a role or group. `PlatformAdmin` can never be granted this way: the
platform-admin grant is resolved server-side from `IPlatformAdminStore`, and an allow-list naming it refuses to build.

## What changes

**1. `AccessContext` gains a field, `TokenRoles: string list`.** The roles the identity provider asserted for the
principal (`AuthenticatedUser.pageRoles` — its API roles plus its directory roles), carried for `AuthenticatedUser` and `TeamMember` subjects and empty otherwise.
`ScopeGated` reads it beside `ModulePermissions`; `canAccessModule` / `hasPermission` do not, so module RBAC is
unchanged. **Source-breaking:** every full record literal of `AccessContext` stops compiling (FS0764).

**2. `ClaimMapping` gains six fields:** `RolesClaim`, `GroupsClaim` (`string option`), `GroupAliases`
(`Map<string, string>`), `AllowedTenants` and `RequiredRoles` (`string list`), and `ApiRoleGrants` (`Set<string>`).
**Source-breaking** for every full record literal of `ClaimMapping`. `ClaimMapping.none` sets all six to "names
nothing", so a mapping built from it behaves exactly as before. `ClaimMapping.directoryRoles` names the conventional
`roles` and `groups` claims and grants no API role.

**3. `AuthenticatedUser` gains a field, `DirectoryRoles: string list`** (Phase 993) — the mapped roles and groups,
which page audiences read and API gates do not. **Source-breaking** for every full record literal of
`AuthenticatedUser`: add `DirectoryRoles = []`, or build from `AuthenticatedUser.anonymous` with a copy-and-update
expression. A provider that maps no directory claim leaves it empty; `AuthenticatedUser.pageRoles` is the union a page
audience reads.

**4. The OIDC provider maps role and group claims** when the mapping names them: a JSON string or an array of strings
becomes `AuthenticatedUser.DirectoryRoles`, groups through `GroupAliases`; a mapped value named in `ApiRoleGrants`
(matched AFTER aliasing, so an aliased group is allow-listed by its alias, never by its raw id) is also added to
`AuthenticatedUser.Roles`. An `ApiRoleGrants` entry that is blank, carries whitespace or a control character, or names
`PlatformAdmin` makes the provider refuse to build (`ClaimMapping.validateApiRoleGrants` says why). A claim of any other shape, or a group overage
(`_claim_names` naming the claim), **rejects the token** (`MappedClaimUnusable`). With `AllowedTenants` or
`RequiredRoles` set (matched against the directory roles and the API roles), a token outside the gate is rejected as `AdmissionRefused` (a new `JwtValidationError` case — an
exhaustive `match` over the type needs an arm for it). With no role claim named, nothing changes, including the
once-per-process warning about unmapped role claims, which now omits the claims the mapping does map.

**5. Three config keys:** `TOOLUP_OIDC_ROLES_CLAIM` and `TOOLUP_OIDC_GROUPS_CLAIM`, read by `AuthProvider.fromEnv`
(unset maps no role claim), and `TOOLUP_OIDC_API_ROLE_GRANTS` — a comma-separated allow-list, each entry trimmed. Unset
grants no API role. An empty entry (`a,,b`, a trailing comma), an entry carrying whitespace, `PlatformAdmin`, or an
allow-list set while neither role nor group claim is named **refuses startup**, naming the variable.

**6. Interactive sign-in for gated pages, opt-in.** `OidcSsrSignIn.withInteractiveSignIn` mounts
`/auth/sign-in` and `/auth/callback` (authorization code with PKCE, `state` and a nonce) and registers an
`InteractiveSignIn`. With one registered, a gated page answers a credential-less browser navigation with a `302` to
the sign-in instead of `401`. The callback sets the session cookie with `SameSite=Lax`, so a link opened from another
site carries it; nothing changes for a deployment that does not compose it.

## What to do

- **Fix record literals.** Add `TokenRoles = []` to any `AccessContext` literal (or build from
  `AccessContext.unrestricted subject` with a copy-and-update expression), and build any `ClaimMapping` from
  `{ ClaimMapping.none with UserIdClaim = …; TenantIdClaim = … }` so a later field does not break it again. Add
  `DirectoryRoles = []` to any `AuthenticatedUser` literal. A hand-built `AccessContext` for a request should carry
  `AccessContext.tokenRolesFor subject (AuthenticatedUser.pageRoles user)`.
- **Exhaustive matches over `JwtValidationError`** gain an `AdmissionRefused reason` arm.
- **To govern pages by directory groups**, follow [the worked example](../platform/gated-ssr.md#readers-governed-by-the-identity-providers-groups):
  name the claims, optionally gate admission, and compose the interactive sign-in.
- **Before naming a role claim on an existing deployment**, check what the IdP puts in it: a malformed value or a
  group overage now rejects that user's token.
- **API roles from the directory are opt-in, one role at a time.** A mapped role does NOT satisfy
  `[<RequiresRole "<name>">]` until `ApiRoleGrants` (or `TOOLUP_OIDC_API_ROLE_GRANTS`) names it. Name only roles whose
  assignment in the directory you are willing to treat as an API grant — whoever can assign that role or group in
  the directory can then call those APIs. Grant `PlatformAdmin` through `IPlatformAdminStore`, never through the
  directory.
- **A custom `IAuthProvider` or claims mapper** that fills `AuthenticatedUser.Roles` keeps working unchanged: `Roles`
  still means API roles and still reaches page audiences. Put roles that should gate pages only in `DirectoryRoles`.

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

`ToolUp.Platform.Tests` → `Phase 993 — directory roles gate pages; API roles only by allow-list`:

- where a mapped role lands with and without the allow-list, and through a group alias (granted by the alias, never by
  the raw id);
- through the composed pipeline with real tokens: a directory `PlatformAdmin` / `Publisher` satisfies the matching
  `ScopeGated` pages (`200`) and is refused at `[<RequiresRole "PlatformAdmin">]` and `[<RequiresRole "Publisher">]`
  (`403`) with no allow-list; allow-listing `Publisher` admits exactly that API method; a group aliased to an
  allow-listed name is admitted only through the alias;
- the fail-closed configuration: refused entries, the provider refusing to build, and `AuthProvider.fromEnv`
  refusing startup on a malformed `TOOLUP_OIDC_API_ROLE_GRANTS`.

## Rollback

Pin the previous release. Unsetting the role claims (and not composing `OidcSsrSignIn`) restores the pre-987
behaviour on 0.24.1 except for the record-literal changes, which are source-level.
