# Auth provider companions

The Platform's `IAuthProvider` interface is identity-only — given an HTTP request, return the authenticated user. Provider companions translate from specific authentication mechanisms (OIDC, Clerk, static JWT, custom) to the SDK's `AuthenticatedUser` record.

This page is a cross-cutting overview of the shipped provider companions. For full details on the `IAuthProvider` contract + how authentication interacts with the SDK's authorisation model, see [`platform/auth.md`](../platform/auth.md).

## What's shipped

| Companion | Side | Description |
|---|---|---|
| `HeaderAuthProvider` (built into `ToolUp.Platform.Server`) | Server | Trusts `X-User-Id` HTTP header. Dev-only. |
| `StaticJwtAuthProvider` (built into `ToolUp.Platform.Server`) | Server | Validates HS256 JWTs. BCL-only, no external NuGet deps. |
| `ToolUp.AuthProviders.Oidc` | Server | Generic OIDC server-side validator. JWKS discovery + JWS signature verification (RS256 by default; opt in to RS384 / RS512 / ES256 / PS256 via `AuthConfig.AcceptedAlgorithms`). |
| `ToolUp.AuthProviders.Oidc.Client` | Client | OIDC sign-in UI: Authorization Code + PKCE flow. |
| `ToolUp.AuthProviders.EntraExternalId` | Server | Microsoft Entra External ID (CIAM): wraps `Oidc` with tenant-aware issuer construction + `oid`/`tid` claim mapping. |
| `ToolUp.AuthProviders.EntraExternalId.Client` | Client | Entra External ID sign-in UI: wraps `OidcClient` with `offline_access` scope default + sign-up / sign-in user-flow policy routing. |
| `ToolUp.AuthProviders.GoogleIdentity.Client` | Client | Google Identity Services: the branded sign-in button and opt-in One Tap. A UX layer over the Google OIDC preset, not a second way to sign in. |
| `ToolUp.AuthProviders.ClerkUI` | Client | Clerk sign-in UI; commercial product integration. |

Client- vs server-side: the OIDC stack ships as **two packages** because authentication has both ends. The server validates tokens; the client renders the sign-in UX. They share no code but share the OIDC protocol.

## Picking a provider

### `HeaderAuthProvider` (dev / local-only)

Use when:
- Local development; no real auth needed.
- Testing CI/CD where the test user identity is injected via the test harness.

Don't use when:
- Anything reachable from the internet. The header is trivially spoofable.

Setup:

```fsharp skip=fragment
// Default — no withAuth call needed; HeaderAuthProvider is the implicit default
ServerApp.empty
|> ServerApp.withConfig config
|> ...
|> ServerApp.run
```

The `HeaderAuthProviderModeValidator` `IConfigValidator` emits a `Warning` at preflight when running in authenticated mode without `withAuth` — flags the misconfiguration.

### `StaticJwtAuthProvider` (in-house JWT issuance)

Use when:
- You issue JWTs in-house (your own auth service generates them).
- HS256 is acceptable (symmetric signing key).
- You don't need JWKS discovery.

Setup:

```fsharp skip=fragment
let authProvider =
    StaticJwtAuthProvider(
        signingKey = "your-symmetric-signing-key",
        expectedIssuer = Some "https://your-issuer.example.com",
        expectedAudience = Some "your-app-id",
        clockSkewSeconds = 60
    ) :> IAuthProvider

ServerApp.empty
|> ServerApp.withAuth authProvider
|> ...
```

Token validation:
1. Signature (HS256 with `signingKey`).
2. `exp` claim — not expired (with `clockSkewSeconds` tolerance).
3. `iss` claim matches `expectedIssuer` (if set).
4. `aud` claim matches `expectedAudience` (if set).

Identity projection:
- `sub` claim → `UserId`.
- `name` claim → `DisplayName`.
- `email` claim → `Email`.

### `ToolUp.AuthProviders.Oidc` (production OIDC)

Use when:
- You have an OIDC provider — Auth0, Cognito, Keycloak, Azure AD, Google Workspace, etc.
- Tokens are signed with one of the algorithms in the whitelist (RS256 by default; see [Supported JWS algorithms](#supported-jws-algorithms)).
- You want JWKS auto-discovery (signing keys fetched from the provider; rotation handled).

Setup:

```fsharp skip=fragment
open ToolUp.AuthProviders.Oidc

let authProvider =
    OidcAuthProvider(
        issuer = "https://your-issuer.example.com",
        audience = "your-client-id"
    ) :> IAuthProvider

ServerApp.empty
|> ServerApp.withAuth authProvider
|> ...
```

Configuration via environment variables (read by the provider at startup):
- `TOOLUP_OIDC_ISSUER` — required.
- `TOOLUP_OIDC_AUDIENCE` — required.
- `TOOLUP_OIDC_CLOCK_SKEW_SECONDS` — optional; default 60.
- `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` — optional; the reachability-probe deadline in milliseconds (default 5000). Raise it for a cold-start-slow tier whose first outbound HTTPS call exceeds 5s, instead of disabling the probe entirely. Range-guarded — a non-numeric / non-positive / absurd value (> 300000) is rejected at preflight rather than silently defaulted. The probe-timeout error message names this var as the lever.

`OidcAuthValidator` `IConfigValidator` probes `.well-known/openid-configuration` at preflight; refuses startup if unreachable. `ServerConfig.SkipPreflight = true` bypasses.

Pair with `ToolUp.AuthProviders.Oidc.Client` for the browser-side sign-in flow.

#### Wiring `IMetricsSink`

When the deployment registers a real `IMetricsSink` (default-shipped Prometheus sink under `MetricsEndpoint = EnabledMetricsEndpoint`, or the `OtelMetricsSink` companion), construct the auth provider via the metered overloads so the auth pipeline emits `toolup.auth.validate.*` counters tagged `provider=oidc` (or `provider=entra-external-id`) alongside the SDK's other observability metrics:

```fsharp skip=fragment
open ToolUp.Platform.Metrics
open ToolUp.AuthProviders

// Resolve the sink from the SDK's DI container after `compose` registers it.
let metrics: IMetricsSink option =
    services.GetService<IMetricsSink>() |> Option.ofObj

// Production shorthand:
let auth = OidcAuthProvider.fromConfigMetered (Some logger) metrics authConfig

// Or, for the env-driven dispatcher:
let auth =
    AuthProvider.fromEnvMetered
        logger
        metrics
        ToolUp.AuthProviders.OidcAuthProvider.fromConfigMetered

ServerApp.empty
|> ServerApp.withAuth auth
|> ...
```

The non-metered constructors (`fromConfig` / `fromConfigWith` / `AuthProvider.fromEnv`) remain unchanged and elide emission. Each provider instance binds its own sink in its closure — there is no module-level mutable state. See [the auth-metrics DI migration doc](../migrations/9e-A-auth-metrics-di.md) for the full migration shape (Entra mirrors this pattern via `createMetered` / `fromEnvMetered`).

#### Supported JWS algorithms

The provider's signature-verification step dispatches on the JWT header's `alg` field. The deployment-trusted set is controlled by `AuthConfig.AcceptedAlgorithms` — `None` resolves to `[ RS256 ]` (the historical default; every existing consumer is byte-for-byte unchanged). Operators opt in to additional algorithms explicitly:

```fsharp skip=fragment
let authConfig = {
    Issuer = Some issuerUrl
    Audience = Some audience
    KeySource = JwksDiscovery issuerUrl
    TokenLocation = BearerHeader
    ClockSkewSeconds = None
    AcceptedAlgorithms = Some [ RS256; ES256 ]   // ← e.g. Cognito-shape interop
}
```

| `alg` | Crypto | JWK shape | Typical issuers |
|---|---|---|---|
| `RS256` | RSA + SHA-256 + PKCS#1 v1.5 | `{ "kty": "RSA", "n": "...", "e": "AQAB" }` | OIDC ecosystem default — Entra / Azure AD / Auth0 / Okta / Keycloak / Google / Clerk |
| `RS384` | RSA + SHA-384 + PKCS#1 v1.5 | Same as RS256 (key shape is hash-agnostic) | Hardened RSA deployments wanting a stronger hash |
| `RS512` | RSA + SHA-512 + PKCS#1 v1.5 | Same as RS256 | Rarely issued in practice; supported for completeness |
| `ES256` | ECDSA over P-256 + SHA-256 (IEEE-P1363 signature transport, not DER) | `{ "kty": "EC", "crv": "P-256", "x": "...", "y": "..." }` | AWS Cognito (some configurations), Firebase Auth federated paths, dynamic-client OIDC flows |
| `PS256` | RSA + SHA-256 + PSS padding | Same RSA key shape as RS256 (PSS is a signature-side choice) | Sites that ban PKCS#1 v1.5 by policy |

Rejected by design:

- `HS256` (and other symmetric MAC variants) — OIDC's trust model forbids sharing symmetric secrets with browser-side parties. Use `StaticJwtAuthProvider` for in-house HS256 issuance instead.
- `EdDSA` / `Ed25519` — deferred until customer demand surfaces.

An inbound token whose `alg` is not in the configured `AcceptedAlgorithms` is rejected with `UnsupportedAlgorithm "<name>"` even if its signature would verify against the JWKS — the trust set is operator-owned, not auto-widened by the SDK. See [`docs/migrations/3-A-oidc-algorithm-whitelist.md`](../migrations/3-A-oidc-algorithm-whitelist.md) for the security rationale + the per-IdP opt-in matrix.

#### Key revocation: what the window actually is

**By default, a signing key the issuer has revoked keeps validating tokens on a
given instance for up to 10 minutes while the issuer is reachable — and for as
long as JWKS fetches keep failing.** The second half is the one that surprises
people: once a refresh is failing, the provider prefers serving the
last-known-good key set over failing every sign-in, so the window is bounded by
provider *availability*, not by the TTL.

That is a deliberate availability-over-revocation default, and it matches how
mainstream OIDC libraries behave: an issuer blip should not take your
application down. It is the wrong default if your threat model includes signing-
key compromise. Three levers change it, all on `OidcHardening` and all off by
default (GP 11) — a deployment that keeps `OidcHardening.defaults` is
byte-for-byte the pre-Phase-463 provider:

| Lever | What it bounds | Cost |
|---|---|---|
| `JwksCacheTtl = Some ts` | The ordinary window, while the issuer is healthy. `Some TimeSpan.Zero` disables the JWKS cache outright — every validation re-fetches, and nothing is served from cache, stale fallback included. | One JWKS round-trip per validated request at zero; proportionally fewer as the TTL rises. |
| `FailClosedOnStaleJwks = true` | The **outage** window. A failing refresh surfaces the error instead of serving keys that may have been revoked since the last success. | Sign-in fails while your IdP is unreachable. This is the trade, stated plainly. |
| `JwksEvictionSignal = Some …` | The window **across instances**. A fetch failure on one instance publishes a `CustomNotification`; subscribed siblings evict their own entry for that URL. | One notification per failed fetch. Needs a distributed `INotificationChannel` companion — the in-process default reaches only the publishing process. |

Without the third lever the fleet-wide window is not "the TTL" — it is the TTL
measured independently per instance, with no instance able to observe another's
trouble. Wiring it is two steps, because the OIDC provider is a props-injected
companion the SDK composition root does not reference, so nothing in the SDK can
subscribe on its behalf:

```fsharp skip=fragment
open ToolUp.AuthProviders
open ToolUp.AuthProviders.OidcJwksCacheTypes

// A stable identity for THIS instance. Machine name is the container / pod
// name under every orchestrator the SDK targets; the pid disambiguates
// several instances colocated on one host.
let instanceId =
    sprintf "%s/%d" System.Environment.MachineName System.Environment.ProcessId

let provider =
    OidcAuthProvider.fromConfigHardened
        (Some logger)
        { OidcAuthProvider.OidcHardening.defaults with
            JwksCacheTtl = Some(System.TimeSpan.FromMinutes 2.0)
            FailClosedOnStaleJwks = true
            JwksEvictionSignal = Some { Channel = channel; OriginReplicaId = instanceId } }
        authConfig

// The receiving half — same channel, same id, once at compose time.
OidcJwksCache.subscribeToEvictions channel instanceId (Some logger)
|> Async.RunSynchronously
|> ignore
```

`OidcJwksCache.evictUrl jwksUrl` is the manual lever for an operator who has
just revoked a key and does not want to wait out the TTL on the instance they
are looking at.

**What none of this bounds: per-token revocation.** This provider does not
perform token introspection (RFC 7662) — revocation is observed only through the
key set. A token whose individual grant was revoked, while its signing key
remains published, keeps validating until its `exp`, regardless of every setting
above. Keep access-token lifetimes short; that is the control that applies here.

### `ToolUp.AuthProviders.Oidc.Client` (browser sign-in)

Browser-side counterpart to the OIDC server provider. Implements OAuth 2.0 Authorization Code + PKCE.

Setup:

The companion exports a **handler** the consumer registers; there is no `register` side effect to call, and the OIDC config rides `AuthUIMode` rather than a separate provider value.

```fsharp skip=fragment
open ToolUp.AuthProviders.Oidc.OidcRegister

let oidcConfig =
    OidcUIConfig.defaults
        "https://your-issuer.example.com"
        "your-client-id"
        "https://your-app.example.com/callback"
    // Scopes default to [ "openid"; "profile"; "email" ].

Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        AuthUI = OidcAuthUI oidcConfig
        Handlers = {
            ClientHandlerRegistry.empty with
                AuthUIHandlers = [ OidcRegister.handler ]
                SignOutHandler = Some(OidcRegister.signOutHandler oidcConfig)
        } }
    modules
```

#### Provider presets

`OidcPresets` returns a fully-formed `OidcAppConfig` per known identity provider, so the per-provider knobs a consumer would otherwise hand-roll (and get wrong once) live as code. Each preset's descriptive metadata is derived from its `PresetKind` — `PresetKind.label` / `.issuerForm` / `.autoAddedScopes` / `.notes` / `.expectsDecodableAccessToken` — and the `OidcCoherenceValidator` renders it at preflight.

| Preset | Inputs | What the preset encodes |
|---|---|---|
| `OidcPresets.generic` | `issuer + clientId + redirectUri` | No provider quirks. Spec-minimum scopes (`openid profile email`); `ValidateIdToken = Some true` (no provider knowledge to judge the boundary by, so the safer default). For Okta, Keycloak, custom OIDC. |
| `OidcPresets.entraWorkforce` | `tenantId + clientId + redirectUri` | Workforce v2.0 issuer; the load-bearing `api://{clientId}/access_as_user` scope; `offline_access`. |
| `OidcPresets.entraExternalId` | `tenantSubdomain + clientId + redirectUri` | CIAM `*.ciamlogin.com` v2.0 issuer; `offline_access`; `ValidateIdToken = Some true`. |
| `OidcPresets.entraExternalIdWithDomain` | `tenantSubdomain + customDomain + clientId + redirectUri` | As above, with a custom CIAM domain replacing the `*.ciamlogin.com` host. |
| `OidcPresets.auth0` | `domain + clientId + redirectUri` | Tenant URL **with** trailing slash (Auth0's `iss` claim shape); `offline_access`. Tokens stay opaque unless an `audience` extra parameter is passed. |
| `OidcPresets.google` | `clientId + redirectUri` | Fixed issuer `https://accounts.google.com` — no tenant parameter. Spec-minimum scopes; `ValidateIdToken = Some true`. Refresh tokens ride the `access_type=offline` **authorize parameter**, not an `offline_access` scope. Access tokens are always opaque. |

##### Worked example — Google sign-in

Google needs no tenant, region, or custom-domain input, so the preset takes only the two values the app registration gives you:

```fsharp skip=fragment
open ToolUp.AuthProviders.Oidc.OidcAppConfig
open ToolUp.AuthProviders.Oidc
open ToolUp.AuthProviders.Oidc.OidcRegister

// One declaration; both sides project from it.
let googleCfg: OidcAppConfig =
    OidcPresets.google
        "1234567890-abcdefg.apps.googleusercontent.com"   // OAuth 2.0 Client ID
        "https://app.example.com/auth/callback"           // an Authorised redirect URI

Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        AuthUI = OidcAuthUI(OidcAppConfig.toClientConfig googleCfg)
        Handlers = {
            ClientHandlerRegistry.empty with
                AuthUIHandlers = [ OidcRegister.handler ]
        } }
    modules
```

Server side, bind the issuer and audience from the same value rather than restating them:

```fsharp skip=fragment
open ToolUp.AuthProviders

let authConfig = {
    Issuer = Some googleCfg.Issuer          // https://accounts.google.com
    Audience = Some googleCfg.Audience      // the OAuth client id
    KeySource = JwksDiscovery googleCfg.Issuer
    TokenLocation = BearerHeader
    ClockSkewSeconds = None
    AcceptedAlgorithms = None               // RS256 — what Google signs with
}

ServerApp.empty
|> ServerApp.withAuth (OidcAuthProvider.fromConfig (Some logger) authConfig)
|> ServerApp.withConfigValidator (OidcCoherenceValidator googleCfg :> IConfigValidator)
|> ...
```

**Refresh tokens are an authorize-parameter concern.** Google ignores `offline_access`, which is why the preset does not request it. Ask for offline access explicitly, via the extras channel:

```fsharp skip=fragment
// `prompt=consent` matters as much as `access_type=offline`: Google issues a
// refresh token only on a user's FIRST consent for a given client, so a
// re-authorising user silently gets none without a consent re-prompt.
OidcClient.beginSignInWithExtras
    (OidcAppConfig.toClientConfig googleCfg)
    [ "access_type", "offline"; "prompt", "consent" ]
```

The same channel carries `hd` for Workspace-domain restriction (`[ "hd", "example.com" ]`) — treat it as a hint that shapes the account chooser, and verify the `hd` claim server-side rather than trusting the parameter.

**Caveat: Google access tokens are always opaque.** Unlike Auth0 — where a dashboard-configured API audience flips the access token to a decodable JWT — Google has no such knob, so `PresetKind.expectsDecodableAccessToken Google` is `false` and `classifyStoredToken` always reports `OpaqueToken`. The `authConfig` above therefore validates the **`id_token`**, which is a real JWT the client sends as its bearer; a follow-up SDK change adds a first-class opaque-token bearer strategy so this is a declared choice rather than a wiring convention. Until then, keep `ValidateIdToken = Some true` (the preset's default) so a tampered id_token is caught at the callback as well as at the server.

The sign-in button in the app's header invokes the OIDC flow:
1. Redirect to `{Issuer}/authorize` with PKCE challenge.
2. User authenticates at the issuer.
3. Issuer redirects back to `{RedirectUri}` with auth code.
4. Client exchanges code for tokens (with PKCE verifier).
5. Bearer token persists in `localStorage`; sent on every API request.

Token refresh: the client checks `exp` on the access token; when within 5 minutes of expiry, calls `{Issuer}/token` with the refresh token. No manual intervention.

#### Client-side `id_token` validation (opt-in)

By default, the callback handler binds the returned `id_token` to *this* sign-in attempt via nonce validation (mandatory; on by default since Cluster B1), then trusts the id_token's signature / `iss` / `aud` / `exp` until the server validates them on the next protected request. Opt in to immediate client-side validation by setting `OidcUIConfig.ValidateIdToken = Some true`:

```fsharp skip=fragment
let oidcConfig = {
    OidcUIConfig.defaults issuer clientId redirectUri with
        ValidateIdToken = Some true
}
```

With this enabled, after the nonce check the callback handler:
1. Fetches the issuer's JWKS via OIDC discovery (`{Issuer}/.well-known/openid-configuration` → `jwks_uri`). Cached in `sessionStorage` with a 10-minute TTL — short enough to follow a key rotation, long enough that a multi-page session doesn't refetch on every navigation. Mirrors the server-side cache TTL.
2. Verifies the RS256 signature against the JWK matching the JWT header's `kid` via WebCrypto (`crypto.subtle.verify`). Pure browser-native; no npm deps.
3. Validates `iss` equals `OidcUIConfig.Issuer`, `aud` contains `OidcUIConfig.ClientId`, and `exp` is in the future (60s clock-skew tolerance — mirrors the server-side default).
4. On any failure: clears local state and returns a typed `AuthError` (`MalformedIdToken` / `IdTokenSignatureInvalid` / `IdTokenIssuerInvalid` / `IdTokenAudienceInvalid` / `IdTokenExpired`).

The pipeline is defence-in-depth — the server's `OidcAuthProvider.ValidateRequest` is the authoritative gate, but failing fast at the callback shortens the time between "issuer issued a bad token" and "user sees a clear error." `ValidateIdToken` defaults to `None` (off) in the 0.3.x line; the default flips to `true` in a coordinated minor bump once consumers have adopted. Algorithm dispatch currently supports RS256 only (the universal OIDC default); wider algorithm support (ES256 / RS384 / RS512 / PS256) lands as an additive follow-on alongside the server-side algorithm-list expansion.

See [migration: 3b-A-oidc-id-token-validation](../migrations/3b-A-oidc-id-token-validation.md) for the consumer-side rollout.

### `ToolUp.AuthProviders.EntraExternalId` (Microsoft Entra External ID)

Opinionated wrapper around `ToolUp.AuthProviders.Oidc` for the Microsoft Entra External ID identity service (the customer-facing CIAM tier — distinct from workforce Entra ID / Azure AD). Bakes in three pieces of External-ID-specific knowledge that are easy to get wrong with raw OIDC config:

1. **Issuer URL construction.** Built from a `tenant` parameter (plus an optional `customDomain` override) as `https://<tenant>.ciamlogin.com/<tenant>/v2.0`. Always v2.0 — the v1.0 endpoint exists but rejects the bound audience format used by current app registrations.
2. **Claim mapping.** Projects `oid` -> `AuthenticatedUser.UserId` (more stable than `sub` in External ID; `sub` varies per app registration, `oid` is constant per user per tenant) and `tid` -> `AuthenticatedUser.TenantId`. The federated-IdP claim (`idp`) is readable via `EntraExternalIdAuthProvider.readIdpClaim` for audit decorators that want per-IdP attribution.
3. **User-flow policies.** Optional `signUpPolicyId` / `signInPolicyId` route the corresponding affordances through External ID's policy endpoints (`oauth2/v2.0/authorize?p=<policyId>`); absent, the default authorize endpoint is used.

Use when:
- Targeting Entra External ID for customer-facing sign-in.
- You want refresh-token issuance (`offline_access` is in the default scope set; the generic OIDC defaults omit it).

Setup:

```fsharp skip=fragment
open ToolUp.AuthProviders

// Inline:
let config = {
    Tenant = "contoso"
    CustomDomain = None
    Audience = "5e2c1f...client-id"
    ClockSkewSeconds = None
    SignUpPolicyId = Some "B2C_SignUp"
    SignInPolicyId = None
}

let authProvider = EntraExternalIdAuthProvider.create None config

// Or from env vars (see below):
let authProvider =
    EntraExternalIdAuthProvider.fromEnv None
    |> Option.defaultWith (fun () -> failwith "TOOLUP_ENTRA_EXTERNAL_ID_TENANT not set")

ServerApp.empty
|> ServerApp.withAuth authProvider
|> ...
```

Configuration via environment variables:
- `TOOLUP_ENTRA_EXTERNAL_ID_TENANT` — required.
- `TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE` — required.
- `TOOLUP_ENTRA_EXTERNAL_ID_CUSTOM_DOMAIN` — optional.
- `TOOLUP_ENTRA_EXTERNAL_ID_SIGN_UP_POLICY` — optional.
- `TOOLUP_ENTRA_EXTERNAL_ID_SIGN_IN_POLICY` — optional.
- `TOOLUP_ENTRA_EXTERNAL_ID_CLOCK_SKEW_SECONDS` — optional; default 60.

`EntraExternalIdAuthValidator` `IConfigValidator` probes `<issuer>/.well-known/openid-configuration` at preflight; refuses startup if unreachable. `ServerConfig.SkipPreflight = true` bypasses (same posture as `OidcAuthValidator`).

The generic OIDC pair remains independently usable — consumers wanting raw OIDC for non-Entra providers don't import this companion.

Pair with `ToolUp.AuthProviders.EntraExternalId.Client` for the browser-side sign-in flow.

### `ToolUp.AuthProviders.EntraExternalId.Client` (Entra External ID sign-in UI)

Browser-side counterpart to the External ID server provider. Wraps `ToolUp.AuthProviders.Oidc.Client` with Entra-aware defaults:

- `openid profile email offline_access` scope set (the OIDC defaults omit `offline_access`; External ID requires it for refresh-token issuance).
- Optional "Sign up" affordance routed through the configured sign-up policy when `SignUpPolicyId` is set.
- `ValidateIdToken = Some true` — client-side id_token validation (signature + iss + aud + exp via WebCrypto) runs on every callback. The generic `OidcUIConfig.defaults` leaves this `None` for back-compat with pre-3b.A consumers; Entra is a customer-facing CIAM surface where defence-in-depth at the boundary is worth the small cost of the WebCrypto verify per sign-in. Opt out via `ValidateIdToken = Some false` on the projected `OidcUIConfig` (regression investigation, intentional fallback during a JWKS-fetch outage).

Wired via the SDK's `CustomAuthUI` extension point (no edit to `AuthUIMode` required):

```fsharp skip=fragment
open ToolUp.AuthProviders.EntraExternalId

let entraConfig =
    EntraExternalIdClientConfig.create
        "<tenant>"          // External ID tenant
        "<client-id>"       // App-registration client id
        "https://app.example.com/auth/callback"

Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        AuthUI = CustomAuthUI { Wrap = EntraExternalIdAuthUI.wrap entraConfig } }
    modules
```

Setup walkthrough — Entra portal:

1. **Create an External ID tenant.** Entra portal -> External Identities -> Create external tenant. Note the tenant name (the short form, not the GUID).
2. **Register the app.** External Identities -> Applications -> New registration. Single-page application; redirect URI matches the `RedirectUri` you wire client-side.
3. **Enable ID + access tokens** in the app registration's Authentication blade.
4. **User-flow policies.** Identity providers -> User flows -> create separate sign-up and sign-in flows if you want them split. Note the policy id for `SignUpPolicyId` / `SignInPolicyId`.
5. **Federated identity providers** (optional) — Identity providers -> add Google / Apple / Facebook / Microsoft consumer accounts. The federated provider's identifier surfaces as the `idp` claim on issued tokens.
6. **API permissions.** Application -> API permissions -> add at least `openid` / `profile` / `email` / `offline_access` (Microsoft Graph delegated).
7. **Claim mapping.** Under the user-flow blade, ensure `oid`, `tid`, `email`, and `idp` are emitted on the issued tokens (External ID emits these by default).

For the full operator playbook including federated-IdP wiring and the invitation flow, see [`docs/migrations/3d-entra-external-id-invitations.md`](../migrations/3d-entra-external-id-invitations.md).

### `ToolUp.AuthProviders.GoogleIdentity.Client` (Google branded button + One Tap)

#### When to bother

**Google sign-in already works without this package.** `ToolUp.AuthProviders.Oidc.Client` plus
`OidcPresets.google` — the [worked example](#worked-example--google-sign-in) above — is a complete
Google sign-in: redirect, PKCE, callback, session, sign-out. Nothing about it is provisional, and a
deployment that never installs this companion is not missing a feature.

This companion exists for the two things that specifically require loading Google's own JavaScript
library, and it is worth the vendor script only if you want one of them:

- **Google's rendered branded button.** Google's brand guidelines ask for their button rather than a
  look-alike, and only the GIS library renders it.
- **One Tap.** The prompt offering a returning visitor their Google account without a click.

Everything else — the session, the bearer, sign-out, the server side — is unchanged either way. If
neither of those is a requirement, stop at the redirect flow and skip this section.

#### Composition

```fsharp skip=fragment
open ToolUp.AuthProviders.GoogleIdentity
open ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityConfig

// The only required input: Google's issuer is a fixed constant, so
// there is no tenant, region, or custom domain to get wrong.
let googleUi =
    GoogleIdentityUIConfig.create "1234567890-abcdefg.apps.googleusercontent.com"

Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        AuthUI = GoogleIdentityRegister.authUI googleUi
        Handlers = {
            ClientHandlerRegistry.empty with
                AuthUIHandlers = [ GoogleIdentityRegister.handler ]
                SignOutHandler = Some(GoogleIdentityRegister.signOutHandler googleUi)
        } }
    modules
```

The server side is **unchanged from the redirect flow** — same `OidcAuthProvider`, same issuer, same
audience. This companion adds no server-side identity code, because it changes how the user reaches a
session, not what the session is.

**One Tap is off unless asked for.** Auto-prompting a deployment's visitors is a product decision the
SDK does not make on its behalf (GP 11), so the default composition renders the button and nothing
else:

```fsharp skip=fragment
let googleUiWithOneTap = GoogleIdentityUIConfig.withOneTap googleUi
```

`AutoSelect` (signing a returning visitor straight back in, no click) is a further opt-in on top, and
only reachable when One Tap is on.

#### Content-Security-Policy

The GIS library is fetched from `accounts.google.com`, injects an iframe, calls back to Google's
origin, and installs its own stylesheet. Under an enforced CSP all four are blocked unless the policy
was widened — and the failure is silent: the header is emitted, the app boots green, and the button
never renders for anyone.

Widen it by composition rather than by hand-editing a header:

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withCspContributor (GoogleIdentityServicesCspContributor())
|> ServerApp.withConfigValidator (
    GoogleIdentityCspValidator.GoogleIdentityCspValidator(serverConfig, services) :> IConfigValidator)
```

The second line is a startup preflight. Registering it is how a deployment *declares* that it renders
the Google button — nothing on the server can observe a client-tier composition by itself, since
`ClientConfig` never reaches that process — and it then warns if no contributor covers Google's
origins. It matches on the host rather than on our own type, so a deployment that widened its policy
its own way is not nagged; it warns rather than aborting, because the policy may be terminated at a
proxy this app cannot see. A redirect-flow deployment needs none of these origins and never registers
it.

#### What the session is

GIS returns exactly one value: an `id_token` JWT signed by Google. There is no access token, and
decisively no refresh token — the credential flow has no token endpoint to exchange against.

The bridge validates the credential's `iss` / `aud` / `exp` (and `nonce`, when one was configured),
then stores it through the **same** `OidcTokenStore.persistTokens` call the redirect flow makes. That
is the whole of the "one session" guarantee: `classifyStoredToken`, `signOut` and the pre-expiry
refresh timer all take the projected `OidcUIConfig`, so a GIS session and a redirect-flow session are
the same session to everything downstream. There is no parallel session machinery. A refused
credential raises the ordinary `AuthError` cases (`IdTokenIssuerInvalid`, `IdTokenAudienceInvalid`,
`IdTokenExpired`, `NonceMismatch`), so one error screen serves both entry points.

Two consequences follow from Google's flow rather than from any SDK choice, and both are worth
knowing before adopting:

1. **The bearer is a real JWT.** `classifyStoredToken` reports `FreshJwt` for a GIS session where the
   Google *redirect* flow reports `OpaqueToken` — Google's access tokens are always opaque, its
   id_tokens never are. The server validates it against Google's JWKS with no extra wiring.
2. **There is no refresh token, so the session cannot be renewed silently.** The refresh timer arms
   as usual, finds nothing to refresh at expiry, and the shell returns to the sign-in screen — a
   re-prompt roughly hourly. A deployment needing long-lived sessions uses the redirect flow with
   `access_type=offline`, which is where Google issues refresh tokens; the two can be composed
   together off one client id.

Sign-out clears Google's auto-select state *before* the shared OIDC sign-out. The ordering is
load-bearing: reversed, One Tap can sign the visitor straight back in on the next page load and
sign-out looks broken.

### `ToolUp.AuthProviders.ClerkUI` (Clerk integration)

Wraps Clerk's React components and surfaces them through the `AuthUIProvider` registry — one provider among peers: it registers under the `"clerk"` tag and is selected through the vendor-neutral `ProviderAuthUI` config case like any other sign-in companion. Clerk is a commercial product — this companion is a thin integration shim, not a Clerk redistribution.

Setup:

```fsharp skip=fragment
open ToolUp.AuthProviders

Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        AuthUI = ClerkRegister.authUI { PublishableKey = BundleConstants.clerkPublishableKey }
        Handlers =
            { ClientHandlerRegistry.empty with
                AuthUIHandlers = [ ClerkRegister.handler ] } }
    modules
```

`ClerkRegister.authUI` is the companion's typed smart constructor for the neutral case — it returns `ProviderAuthUI ("clerk", box cfg)`. (The vendor-named `ClerkAuthUI` core case is deprecated; see [`docs/migrations/494-vendor-neutral-auth-ui.md`](../migrations/494-vendor-neutral-auth-ui.md).)

Server-side, validate the bearer token via:
- `StaticJwtAuthProvider` with Clerk's signing key.
- Or a custom `IAuthProvider` that calls Clerk's verification API.

Configuration via environment / Vite env:
- `CLERK_PUBLISHABLE_KEY` — required client-side.
- Server-side signing key — see Clerk's docs.

`#if DEBUG` in the consuming app typically controls Clerk activation — debug skips sign-in, release enables it.

## Directory companions

A *directory* companion is not an `IAuthProvider`. It implements `IUserDirectory`, a separate substrate the SDK's team-management surface uses to look people up rather than to sign them in: a typeahead over the identity provider's directory (`SearchUsers`), a reverse batch lookup that turns stored user ids into names and emails in the admin tables (`ResolveUsers`), and a branded invitation email (`NotifyInvitation`).

They are optional in the strongest sense — a deployment that registers none gets `Ok []` from the typeahead handler, the invite form degrades to a plain email input, and the invite still lands via the pending-by-email store with the invitee told out of band. Register one with `ServerApp.withUserDirectory`, independently of which `IAuthProvider` signs users in.

| Companion | Directory | Auth model |
|---|---|---|
| `ToolUp.AuthProviders.EntraDirectory` | Microsoft Graph (Entra / Azure AD) | App-only via `DefaultAzureCredential` — managed identity in production, `az login` locally. |
| `ToolUp.AuthProviders.GoogleDirectory` | Google Workspace (Admin SDK Directory API + Gmail API) | Service-account JSON + domain-wide delegation, read from `ISecretStore`. |

### `ToolUp.AuthProviders.GoogleDirectory` (Google Workspace directory)

The Google analogue of `EntraDirectory`: same three capabilities, same degradation semantics, a materially different auth model. BCL `HttpClient` throughout — no Google client SDK in the dependency graph.

Google Workspace has no managed-identity equivalent for these APIs. The only application-scoped path is **domain-wide delegation**: a service account, authorised by a Workspace super-admin, may impersonate users in the domain for an explicit list of OAuth scopes. Every call therefore runs *as some user*, and the two capabilities need two different subjects — directory reads impersonate a Workspace admin (only an admin may list the directory), while the invitation email impersonates the sender mailbox (Gmail sends as whoever the token impersonates). Tokens are minted with an RS256-signed JWT-bearer grant and cached per subject and scope set.

Scopes requested, and no others:

- `https://www.googleapis.com/auth/admin.directory.user.readonly` — always.
- `https://www.googleapis.com/auth/gmail.send` — only when `SenderUserId` is set. Send-only; it grants no mailbox read.

Wiring. The service-account JSON comes from the deployment's `ISecretStore`, never an environment variable or a file path — a delegated key can impersonate any user in the domain, so it belongs behind an audited secret backend. That is also why this companion ships no `fromEnv`:

```fsharp skip=fragment
open ToolUp.AuthProviders

let directoryConfig = {
    GoogleDirectoryConfig.defaults with
        Domain = "example.com"
        ImpersonatedAdmin = "directory-reader@example.com"
        SenderUserId = Some "invites@example.com"
}

let directory = GoogleDirectory.create secretStore directoryConfig

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withUserDirectory (Some directory)
|> ServerApp.run
```

`CredentialScopeId` / `CredentialSecretKey` default to `_platform` / `google_directory_service_account`; the stored value is the key file's contents verbatim.

Ship the pair alongside it. `GoogleDirectoryConfigValidator.create` performs a real token exchange per requested scope at preflight — which is exactly where Google enforces the delegation, so a mint is proof the grant exists. A missing credential or an ungranted directory scope aborts startup; an ungranted *Gmail* scope is a `Warning`, because invitation email degrading is a real loss but not a reason to refuse to boot. `GoogleDirectoryHealth.create` adds a readiness probe that makes a live authenticated directory call.

Query shape. Google's `query` parameter ANDs multiple `field:value` prefix terms — there is no OR — so matching display name *or* email is two requests (`name:'…'`, `email:'…'`) merged and de-duplicated by id. `EntraDirectory` expresses the same intent as one OData filter with `or` clauses; the observable behaviour is the same. A minimum prefix of 2 characters is enforced server-side.

Errors follow `EntraDirectory` exactly: transient directory failures (and a credential that will not load, and a delegation that was never granted) surface as `Error "directory unavailable: …"` under the typeahead input; mail-send failures as `Error "notification unavailable: …"`, which the invite handler swallows. `SenderUserId = None` disables the mail path with no other consequence. An id `ResolveUsers` cannot find is skipped, never an error.

The permission-grant walkthrough — both consoles, in order, with the PowerShell for the Cloud half — is in the companion's own [README](https://github.com/ToolUp-Forge/toolup-forge/blob/main/src/AuthProviders/GoogleDirectory/README.md).

## Writing a custom provider

For an auth mechanism not covered by the shipped companions (LDAP / SAML / proprietary / etc.):

```fsharp skip=fragment
module MyAuthProvider

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth

type MyAuthProvider(config: MyAuthConfig) =
    interface IAuthProvider with
        member _.GetUser(ctx: RequestContext) = async {
            // Unwrap the opaque `RequestContext` to the underlying
            // `HttpContext` at one site per impl.
            let httpCtx = RequestContext.value ctx :?> HttpContext
            let token = httpCtx.Request.Headers.["Authorization"].ToString().Replace("Bearer ", "")
            match validateToken token with
            | Ok claims ->
                return {
                    UserId = claims.Subject
                    DisplayName = claims.Name
                    Email = claims.Email
                    TenantId = None
                    Roles = []
                }
            | Error _ -> return AuthenticatedUser.anonymous
        }
        member _.ValidateRequest(ctx: RequestContext) = async {
            let httpCtx = RequestContext.value ctx :?> HttpContext
            // Cheap pre-checks + validate the bearer token; return
            // `Ok user` on success or `Error reason` on failure.
            return Error "not implemented"
        }
```

Pair with an `AuthUIProvider` if you need a client-side sign-in UI. Register via `withAuth` on `ServerApp`.

See [`platform/auth.md`](../platform/auth.md) for the full authoring guide + production hardening checklist.

## How auth interacts with the SDK

The auth provider is identity-only. The SDK adds:

- **`AccessContext`** — userId + teamId + mode + permissions + platform role. Resolved per-request by `ScopeResolutionMiddleware`.
- **`PlatformRole`** — `Member` (default) or `PlatformAdmin` (deployment-wide admin).
- **`TeamRole`** — `Owner` / `Admin` / `Member`. Per-team.
- **`ModulePermissions`** — per-team, per-module. `Read | Write | Admin | NoAccess`. Empty map = unrestricted.

Module API handlers wrap in `makePermissionGuardedApi` which checks `ModulePermissions` before invoking. The auth provider doesn't see permissions; the SDK does.

SSE has its own auth caveat — `EventSource` can't send custom headers, so OIDC bearer tokens need an alternative path (query string with short-lived tokens, session cookie, or a pre-handshake POST). See [`platform/auth.md`](../platform/auth.md) "SSE auth caveat".

### Where the client keeps the bearer token (Phase 133)

By default (`ClientConfig.AuthTokenStorage = ClientCookieAndLocalStorage`) the client writes the JWT to `localStorage` **and** a `document.cookie`. Neither can be `HttpOnly` — only a server `Set-Cookie` can be — so both stores are reachable by any injected script, and an XSS escalates directly to bearer-token theft. This is acceptable only for dev / the EventSource-handshake path, or for a SPA that accepts the exposure and ships a strict CSP (the Phase 9j generator / Phase 129 header baseline) as the mitigation. **Do not describe the JS cookie as "equivalent to localStorage for security" — name the exposure: a JS-readable bearer is XSS-exfiltratable.**

The production-shape alternative is the **server-set HttpOnly cookie**. Set `ServerConfig.AuthCookieIssuance = EnabledAuthCookieIssuance` and `ClientConfig.AuthTokenStorage = ServerSetHttpOnlyCookie`: the client POSTs its freshly-acquired JWT once to `POST /api/auth/session`, the server validates it through the registered `IAuthProvider`, and reflects it into an `HttpOnly; Secure; SameSite=Strict` cookie. The JWT never enters `localStorage` or a JS-readable cookie; the browser sends the cookie automatically for SSE and same-origin XHR. Pair with `TokenLocation = BearerOrCookie "toolup-auth-token"` (the reflect call reads the header; every later request + SSE reads the cookie) and the `IAuthBridge` refresh model. See [`docs/migrations/133-httponly-auth-cookie.md`](../migrations/133-httponly-auth-cookie.md) for the full adoption recipe and the OidcClient-PKCE compatibility caveat.

## Hardening checklist for production

- Real auth provider (not `HeaderAuthProvider`).
- `ServerConfig.AuthCookieIssuance = EnabledAuthCookieIssuance` + `ClientConfig.AuthTokenStorage = ServerSetHttpOnlyCookie` + `TokenLocation = BearerOrCookie "toolup-auth-token"` — move the JWT out of JS-readable storage into a server-set HttpOnly cookie (Phase 133). For paths that must keep a JS-readable bearer, ship a strict CSP instead.
- `ServerConfig.RequireHttps = true`.
- `ServerConfig.TrustForwardedHeaders = true` (default; opt out with `TOOLUP_TRUST_FORWARDED_HEADERS=0` only on a direct-bind dev shell).
- `ServerConfig.SecurityHeaders = StrictSecurityHeaders`.
- `ServerConfig.CorsConfig` — explicit allow-list for browser callers.
- `TOOLUP_INITIAL_PLATFORM_ADMIN` set for the bootstrap admin user.
- OIDC issuer trusted at the network layer (TLS pin if possible).
- `IConfigValidator` preflight runs at boot; refuses to start on `Error` outcomes.
