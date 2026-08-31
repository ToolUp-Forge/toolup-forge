# Authentication

The Platform separates **authentication** (who is the caller?) from **authorisation** (what can they do?). Auth providers are pluggable; the SDK owns the rest.

## `IAuthProvider`

Identity-only interface:

```fsharp skip=signature
type IAuthProvider =
    abstract GetUser: ctx: RequestContext -> Async<AuthenticatedUser>
    abstract ValidateRequest: ctx: RequestContext -> Async<Result<AuthenticatedUser, string>>
    /// Whether the identity this provider returns was verified by
    /// cryptography (a signed token) rather than trusted from the wire.
    /// `HeaderAuthProvider` reports false; the OIDC providers report true.
    abstract IsCryptographicallyVerified: bool
```

The parameter is `RequestContext`, not ASP.NET's `HttpContext`: the interface lives in
`ToolUp.Platform.Core` and must stay free of a server-framework dependency, so the SDK narrows the
request to the fields a provider actually reads before calling.

The two read methods differ in strictness, not in subject. `GetUser` is the **lenient** contract: it
returns the resolved identity, or the `anonymous` user when no credentials are present — which is why
it returns `AuthenticatedUser` rather than an option, and why a deployment whose `Surfaces` includes
an `Anonymous` profile needs no special case. `ValidateRequest` is the **strict** one: it runs the
pre-checks (token signature, expiry, issuer / audience) and returns `Error` with a reason on missing,
invalid, or expired credentials, which is what an authenticated scope resolver uses to short-circuit
the pipeline.

`IsCryptographicallyVerified` has no default, deliberately: identity verification is a security
property no provider should acquire by omission. The startup auth-mode validator reads it — not the
concrete type — and refuses to boot an auth-requiring deployment behind a provider reporting `false`.

`IAuthProvider` resolves authenticated identity only — the per-request `Subject` (`AnonymousSession` / `AuthenticatedUser` / `TeamMember` / `ClaimBearer`) is the SDK's job and falls out of `ISubjectResolver` against the auth provider's result. See [`surfaces.md`](surfaces.md#the-subject-model) for the subject model and the request-resolution flow.

`AuthenticatedUser` carries:
- `UserId: string` — stable identity (typically the OIDC `sub` claim or equivalent)
- `DisplayName: string option`
- `Email: string option`

That's the entire identity contract. Permissions and team membership are SDK concerns layered on top via `IPermissionStore` and `ITeamStore`; the auth provider doesn't know about them.

## Shipped providers

### `HeaderAuthProvider` (dev default)

Trusts `X-User-Id` HTTP header verbatim. No validation, no signature, no expiry. Safe only for local dev and tightly-network-gated demo deployments.

`HeaderAuthProvider` is a top-level module holding the type of the same name, so the constructor is spelled through it:

```fsharp skip=fragment
let authProvider = HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider
```

### `StaticJwtAuthProvider`

HS256 JWT validation. BCL-only (no external NuGet deps). Checks signature, expiry, optional issuer / audience. Extracts `sub`, `name`, `email` claims.

Suitable when JWT issuance is in-house and rotation is operationally managed. Not a real OIDC integration — there's no JWKS discovery or RS256 support; for that, use the OIDC companion.

It takes a `StaticJwtConfig` record rather than named constructor arguments:

```fsharp skip=fragment
let authProvider =
    StaticJwtAuthProvider.StaticJwtAuthProvider {
        Secret = "..."
        Issuer = Some "https://issuer.example.com"
        Audience = Some "my-app"
    }
    :> IAuthProvider
```

### `ToolUp.AuthProviders.Oidc` (server-side)

Generic OIDC server-side validator. Discovers JWKS via `.well-known/openid-configuration`, validates RS256 JWT bearer tokens against the discovered keys.

Works against any OIDC-compliant issuer — Auth0, Cognito, Keycloak, etc. Pair with the matching client-side provider (`ToolUp.AuthProviders.Oidc.Client`) for the Authorization Code + PKCE flow.

The companion exposes factory functions over an `AuthConfig`, not a constructor. `fromConfig` is the plain form; `fromConfigMetered` / `fromConfigHardened` add a metrics sink and the hardening options respectively:

```fsharp skip=fragment
open ToolUp.AuthProviders.OidcAuthProvider

let issuer = "https://your-issuer.example.com"

let authProvider =
    fromConfig
        None   // ILogger option
        {
            Issuer = Some issuer
            Audience = Some "your-client-id"
            KeySource = JwksDiscovery issuer
            TokenLocation = BearerHeader
            ClockSkewSeconds = None
            AcceptedAlgorithms = None   // defaults to [ RS256 ]
        }
```

Configuration via environment variables:
- `TOOLUP_OIDC_ISSUER` — required.
- `TOOLUP_OIDC_AUDIENCE` — required.
- `TOOLUP_OIDC_CLOCK_SKEW_SECONDS` — optional, default 60.

`OidcAuthValidator` (an `IConfigValidator`) probes the issuer's `.well-known/openid-configuration` at preflight and refuses to start if the issuer is unreachable. Set `ServerConfig.SkipPreflight = true` to bypass.

### `ToolUp.AuthProviders.Oidc.Client` (client-side)

Browser-side OIDC sign-in UI. Implements OAuth 2.0 Authorization Code + PKCE. Registers via the `AuthUIProvider` delegate registry; deployments select it through `ClientConfig.AuthUI`.

```fsharp skip=fragment
// Client.fs
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc

let oidcConfig: OidcUIConfig = {
    Issuer = "https://your-issuer.example.com"
    ClientId = "your-client-id"
    RedirectUri = "https://your-app.example.com/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = None
    ValidateIdToken = Some true
    BearerToken = None   // None = send the access token (the default)
}

Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        Surfaces = Surfaces.individual
        AuthUI = OidcAuthUI oidcConfig
        Handlers =
            { ClientHandlerRegistry.empty with
                AuthUIHandlers = [ OidcRegister.handler ]
                SignOutHandler = Some(OidcRegister.signOutHandler oidcConfig) } }
    modules
```

`OidcAuthUI` is protocol-named, not vendor-named, so it stays a first-class `AuthUIMode` case rather than moving to `ProviderAuthUI` (Phase 494). The `AuthUIHandlers` entry is **not optional**: since Phase 13a the companion exports a `handler` *value* instead of registering itself at module load, and `AuthUIProvider` fails loudly at startup if `AuthUI = OidcAuthUI _` is set with no handler carrying the `"oidc"` tag. `SignOutHandler` is optional — supply it to have the shell render a "Sign out" affordance wired to the issuer's end-session flow.

`OidcUIConfig.defaults issuer clientId redirectUri` fills the last three fields with `[ "openid"; "profile"; "email" ]` / `None` / `None` if you would rather not spell the record out. For a first-class preset per IdP (Entra workforce, Entra External ID, Auth0, Google) see `OidcPresets` in the same companion, and the [preset table](../companions/auth-providers.md#provider-presets).

### `ToolUp.AuthProviders.EntraExternalId{,.Client}` (Microsoft Entra External ID)

Opinionated wrapper around the generic OIDC pair for Microsoft Entra External ID (the customer-facing CIAM tier). The server companion constructs the v2.0 issuer URL from a tenant identifier (plus optional custom domain), applies the **`oid` > `sub` claim convention** (External ID's `oid` is constant per user per tenant; `sub` varies per app registration, so mapping `sub` -> `UserId` produces a different id every time the consumer adds a second app registration), and projects `tid` -> `TenantId`. The federated-IdP claim (`idp` — `google.com` / `apple.com` / `live.com` / `local` for the tenant's own user pool) is readable via `EntraExternalIdAuthProvider.readIdpClaim` for audit decorators that want per-IdP attribution.

The browser companion adds `offline_access` to the default scope set (External ID requires it for refresh-token issuance) and routes sign-up / sign-in through the configured user-flow policies when supplied.

The SSE auth caveat below applies unchanged to External-ID-issued tokens — the access token still rides in the `Authorization` header on the standard API path, and the SSE handshake follows whichever cookie/query-string fallback the deployment configured.

See [`docs/companions/auth-providers.md`](../companions/auth-providers.md) and the [Entra External ID invitations migration](../migrations/3d-entra-external-id-invitations.md) for the full operator playbook.

### `ToolUp.AuthProviders.ClerkUI` (client-side)

Wraps Clerk's React components and surfaces them through the `AuthUIProvider` registry — one provider among peers, registered under the `"clerk"` tag and selected through the vendor-neutral `ProviderAuthUI` config case:

```fsharp
open ToolUp.AuthProviders

{ ClientConfig.defaults with
    AuthUI = ClerkRegister.authUI { PublishableKey = BundleConstants.clerkPublishableKey }
    Handlers =
        { ClientHandlerRegistry.empty with
            AuthUIHandlers = [ ClerkRegister.handler ] } }
```

(The vendor-named `ClerkAuthUI` core case is deprecated — see [`docs/migrations/494-vendor-neutral-auth-ui.md`](../migrations/494-vendor-neutral-auth-ui.md).) The server still validates the bearer token via a separate provider (typically `StaticJwtAuthProvider` configured against Clerk's signing key, or a custom Clerk-specific impl).

Clerk is a commercial product with its own licence and pricing — this companion is a thin client-side integration shim, not a Clerk redistribution.

## Wiring an auth provider

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with Surfaces = Surfaces.individual }
|> ServerApp.withAuth oidcAuthProvider
|> ServerApp.addModules modules
|> ServerApp.run
```

Omit the `withAuth` call entirely for `HeaderAuthProvider` (the default). A deployment whose `Surfaces` includes any non-Anonymous profile without `withAuth` will run on the header provider in production — usually a misconfiguration. `HeaderAuthProviderModeValidator` (an `IConfigValidator`, keyed off `DeploymentConfig.requiresAnyAuth`) emits a `Warning` if the combination is detected; set `ServerConfig.SkipPreflight = true` to bypass. The compose-time `SurfaceCoherenceValidator` raises Rule 8 (Error) when a non-Anonymous `Surfaces` is declared and `ServerApp.withAuth` is never called — see [`security.md`](security.md#validator-coverage-as-a-hardening-lever) for the full validator rule list.

## Writing a new auth provider

A new provider lives in `src/AuthProviders/<Name>/` with its own `.fsproj`. Implement `IAuthProvider`, expose a `create` function, and (for the client side) register via `AuthUIProvider`.

```fsharp skip=fragment
module MyAuthProvider

open ToolUp.Platform

type MyAuthProvider(config: MyAuthConfig) =
    interface IAuthProvider with
        member _.GetUser(ctx) = async {
            // Read bearer token, validate, project to AuthenticatedUser
            let token = ctx.Request.Headers.["Authorization"].ToString().Replace("Bearer ", "")
            match validateToken token with
            | Ok claims ->
                return Some {
                    UserId = claims.Subject
                    DisplayName = claims.Name
                    Email = claims.Email
                }
            | Error _ -> return None
        }
        member _.ValidateRequest(ctx) = async {
            // Cheap pre-checks; return Error to short-circuit
            return Ok ()
        }
```

Provider rules:
- Never read environment variables directly. Accept config via the `create` function.
- Never log the bearer token, even at trace level. Log a hashed prefix if you must.
- Use `ISecretStore` for any provider-side secret (signing keys, client secrets, etc.) — never hardcode.
- Document the precision of clock-skew tolerance in the README.
- Author an `IConfigValidator` to verify the provider is reachable / correctly configured at preflight.
- Author an `IHealthCheck` for `/ready` participation.

For a complete example see `src/AuthProviders/Oidc/` (server-side) and `src/AuthProviders/OidcClient/` (client-side).

## SSE auth caveat

Server-Sent Events open a long-lived connection to `/api/notifications`. The browser's `EventSource` API does not allow custom request headers, so OIDC bearer tokens can't be sent the usual way. Three options:

1. **Query string** — append `?token=<bearer>` to the SSE URL. Server reads from query string. Risk: tokens land in server access logs. Mitigation: short-lived tokens, redacted logs.
2. **Cookie** — server sets a session cookie on sign-in; SSE reads it automatically. Risk: CSRF surface. Mitigation: `SameSite=Strict` + CSRF token on state-changing requests.
3. **Pre-handshake** — a brief `/api/auth/sse-handshake` POST exchanges the bearer token for a short-lived SSE-scoped opaque session ID; `EventSource` connects with that. Most secure; most plumbing.

`SseAuthModeValidator` (an `IConfigValidator`) emits a `Warning` when a deployment whose `Surfaces` requires authentication is configured without a documented SSE auth strategy. Deployments set `ServerConfig.SseAuthMode` explicitly to acknowledge the choice.

## Permissions + roles

Auth providers don't carry permissions; the SDK does:

- **`PlatformRole`** — deployment-wide. Today: `Member` (default) and `PlatformAdmin`. Bootstrap one admin via `TOOLUP_INITIAL_PLATFORM_ADMIN=<userId>`.
- **`TeamRole`** — per-team. `Owner`, `Admin`, `Member`. Set when a user joins a team; managed via `PlatformApi.ChangeMemberRole`.
- **`ModulePermission`** — per-team, per-module. `Read | Write | Admin | NoAccess`. Stored via `IPermissionStore`; default empty map = unrestricted.

Module API handlers go through `makePermissionGuardedApi` which checks the caller's `ModulePermissions` before invoking the API function. This is the only sanctioned authorisation choke-point. Modules do not check permissions themselves; the wrap is automatic via `ServerModule.withGuardedApi`.

Platform Admin paths (assigning admins, destroying encryption keys, writing to the Platform KB) gate on `PlatformRole.PlatformAdmin`. The audit log records every role assignment and revocation.

## Audit events emitted

Auth-related events under `_platform.audit`:

- `UserLoggedIn` — first authenticated request in a session (first-seen-this-session, not per-request).
- `RoleAssigned` / `RoleRevoked` — Platform Admin role changes.
- `TeamMemberAdded` / `TeamMemberRemoved` / `TeamMemberRoleChanged` — team membership changes.

Every event carries the actor's userId, the affected userId (if different), the resource Id, and a server-side timestamp. The audit-sink replication layer mirrors these to any configured external sinks (Splunk, Datadog, S3 archive).

## Hardening checklist for production

- `ServerConfig.RequireHttps = true` — registers `app.UseHttpsRedirection()`.
- `ServerConfig.TrustForwardedHeaders = true` — default-on. Behind a TLS-terminating proxy this is needed so secure-cookie scoping and `Url.IsAbsoluteUri` see the originating scheme. Direct-bind dev shells with no proxy opt out with `TOOLUP_TRUST_FORWARDED_HEADERS=0`.
- `ServerConfig.SecurityHeaders = StrictSecurityHeaders` — emits CSP, HSTS, X-Frame-Options, X-Content-Type-Options, Referrer-Policy.
- `ServerConfig.RateLimit` — configure a per-team / per-user / per-IP partition with a sensible fixed-window cap. `/health`, `/ready`, and `/api/notifications` are excluded by default.
- `ServerConfig.CorsConfig` — explicit allow-list for cross-origin browser callers; reject `*` for credentialed requests.
- `ServerConfig.MaxRequestBodyBytes` — explicit cap. Default is generous; tighten for production.
- Real auth provider (not `HeaderAuthProvider`). Real OIDC or Clerk or in-house JWT.
- `TOOLUP_INITIAL_PLATFORM_ADMIN` set for the bootstrap user.
- Encryption-at-rest decorator wired with `PerScopeKeyResolver` (for crypto-shred capability).

The `IConfigValidator` preflight runs all of these at boot; missing pieces surface as `Warning` or `Error` (the latter refuses to start). The default policy is operator-friendly: most hardening knobs default to off, and the validator nudges you to opt in rather than failing closed.

## See also

- [`../security/PLATFORM-SECURITY-RULES.md`](../security/PLATFORM-SECURITY-RULES.md) — §2 (authentication) and §3 (authorisation) of the versioned rule set, with an evidence pointer per rule and each rule marked always-on / default-on / opt-in.
- [`security.md`](security.md) — deployment-time threat surfaces and the per-archetype threat model.
