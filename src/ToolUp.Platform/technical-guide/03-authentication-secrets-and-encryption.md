# ToolUp.Platform Technical Guide — 03. Authentication, Secrets & Encryption

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 2. Multi-Tenancy, Teams & Access Control](02-multi-tenancy-and-access.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 4. Data & Storage Substrate →](04-data-and-storage-substrate.md)

---

## Authentication Providers

### The IAuthProvider contract

```fsharp
type IAuthProvider =
    abstract GetUser: RequestContext -> Async<AuthenticatedUser>
    abstract ValidateRequest: RequestContext -> Async<Result<AuthenticatedUser, string>>
    abstract IsCryptographicallyVerified: bool
```

Both request methods are async (so provider implementations can make network calls — JWKS discovery, token introspection, external directory lookup — without blocking). `GetUser` is lenient: returns `AuthenticatedUser.anonymous` on any failure. `ValidateRequest` is strict: returns `Error reason` on missing / invalid / expired credentials. The `RequestContext` parameter wraps a boxed `HttpContext`, so the interface lives in the shared (Fable-compatible) layer without referencing ASP.NET Core.

`IsCryptographicallyVerified` (Wave 19) is a fail-closed capability signal: a provider that proves identity cryptographically (verified JWT / OIDC / mTLS) returns `true`; one that trusts an unauthenticated request header (`HeaderAuthProvider`) returns `false`. The `header-auth-mode` startup validator refuses to boot an auth-requiring deployment whose provider reports `false`, unless `ServerConfig.AcceptHeaderAuthWhenAuthRequired` is set (behind-mTLS-proxy escape hatch). The check reads this capability, not the concrete type, so a subclass / wrapper can't evade it.

### AuthConfig — declarative provider shape

```fsharp
type KeySource =
    | StaticSecret of key: string
    | JwksDiscovery of issuerUrl: string
    | JwksExplicit of jwksUrl: string

type TokenLocation =
    | BearerHeader
    | Cookie of name: string
    | CustomHeader of name: string

type AuthConfig = {
    Issuer: string option
    Audience: string option
    KeySource: KeySource
    TokenLocation: TokenLocation
}
```

Every provider exposes a `fromConfig` factory that consumes `AuthConfig`, so deployments can wire auth from env vars / config without touching provider internals.

### HeaderAuthProvider (development)

Trusts the `X-User-Id` header directly. No validation. Used as the default when `ServerApp.withAuth None` (or an absent `withAuth` call) is used.

### StaticJwtAuthProvider (static-secret, production-ready for internal services)

Validates HS256 JWTs using only BCL types — no NuGet package dependencies. Validation chain: split token → enforce `alg=HS256` (rejects `alg:none` / RS256-confusion) → verify HMAC-SHA256 signature (constant-time comparison) → decode payload → require & check `exp` (60s clock-skew; a token with no `exp` is rejected — "no expiry" is never a safe default for a bearer credential) → check optional `nbf` (60s skew) → check issuer/audience → extract claims (`sub` → UserId, `name` → DisplayName, `email` → Email). The `exp`/`nbf` rules and clock-skew match `OidcAuthProvider` so the two providers validate token lifetime identically.

### OidcAuthProvider (production, SaaS IdPs)

Sub-companion at `src/AuthProviders/Oidc/`. BCL-only RS256 JWT validator with JWKS discovery. Works with any OIDC-compliant IdP — Clerk, Auth0, Azure AD, Keycloak, Okta, Google Identity.

Pipeline: extract token (per `TokenLocation`) → parse JWT → resolve JWKS URL (discovery cached with a 24h TTL, then re-resolved — bounded, so a provider that rotates `jwks_uri` is eventually picked up and a no-longer-used issuer is swept rather than pinning memory for the process lifetime) → fetch keys (10-min TTL, `kid`-miss triggers one refresh rate-limited to once/minute per URL) → construct `RSAParameters` from JWK `n`/`e` → `RSA.VerifyData` → check `exp` / `nbf` (60s clock-skew) / `iss` / `aud` → map claims to `AuthenticatedUser`. Both the JWKS and discovery caches are bounded: the fetch path opportunistically evicts entries past their TTL (no hosted service — the OIDC provider is a props-injected companion, so the sweep is driven inline from the JWKS-fetch path and is naturally throttled to fetch frequency). Denials are classified via an internal `JwtValidationError` DU; `ValidateRequest` stringifies them, `GetUser` coalesces to anonymous.

Activate via `TOOLUP_AUTH_MODE=oidc` + `TOOLUP_OIDC_ISSUER=<issuer URL>` + `TOOLUP_OIDC_AUDIENCE`. **`TOOLUP_OIDC_AUDIENCE` is effectively mandatory in authenticated modes:** with it unset the `aud` check is skipped and any token the issuer minted is accepted — including one issued for a different relying party on the same IdP (confused-deputy / token reuse). `OidcAudienceBindingValidator` refuses startup for an auth-requiring `Mode` + `oidc` + unset audience unless `ServerConfig.AcceptUnboundAudienceInAuthenticatedMode = true` (`TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE=1`) — intended only for a single-app issuer no other relying party shares.

**Secure-by-default note.** The implicit default `IAuthProvider` (when none is supplied to `ServerApp`) is `HeaderAuthProvider`, which trusts the `X-User-Id` header without cryptographic proof. This is safe only because `HeaderAuthProviderModeValidator` refuses startup in any auth-requiring `Mode` (and, per the previous section, is a security-class validator `SkipPreflight` cannot bypass). The intended production path is OIDC; `HeaderAuthProvider` is a dev convenience whose blast radius is contained by the preflight refusal, not by the default itself.

**Known limitation — roles/groups are not mapped.** `OidcAuthProvider` maps only `sub` / `name` / `email`; `AuthenticatedUser.Roles` and `TenantId` stay empty. The SDK permission model is team-membership driven (`PlatformRole` / `TeamRole` set when a user joins a team), and a configurable claim-mapper that projects IdP role/group claims onto that model is a tracked roadmap item. A brownfield Auth0 / Keycloak migration whose users already carry `roles` / `groups` / `realm_access` / `resource_access` claims will see those silently dropped — users authenticate but land role-less. The provider emits a one-time `Warn` per process the first time such a claim is seen so the limitation is discoverable in the log rather than surfacing as "everyone is a plain member after migration" with no explanation.

## Sign-in UI companions

The server-side `IAuthProvider` only validates tokens the browser hands it — it does not obtain them. Sign-in UI (how the user actually gets a token in the first place) is handled by client-side companion packages that register with `AuthUIProvider` in the core SDK.

### Delegate registry

`src/ToolUp.Platform/Client/AuthUIProvider.fs` owns a tag-keyed `Map<string, AuthUIHandler>` where `AuthUIHandler = obj -> ReactElement -> ReactElement`. Companions export a `(tag, handler)` value (e.g. `ClerkRegister.handler`, `OidcRegister.handler`); consumers add them to `ClientConfig.Handlers.AuthUIHandlers` at compose time, and the shell calls `AuthUIProvider.gate authUI subjectKind shell` on every render, dispatching to the handler matching the active mode's tag. A missing handler fails loud (at `Client.run` validation, and defensively per-render) with a message naming the handler value that needs to be wired.

Selection in `ClientConfig.AuthUI` mirrors the registry's own keying: the vendor-neutral `ProviderAuthUI (tag, config)` case selects any registered companion by tag, with the provider-specific config payload handed to the handler (which unboxes it — the same sanctioned erasure boundary the handler signature already carries). Companions export typed smart constructors so consumers never box by hand — a Clerk deployment writes `AuthUI = ClerkRegister.authUI { PublishableKey = key }`. The protocol-named `OidcAuthUI` case remains as a typed convenience for the OIDC companion; the vendor-named `ClerkAuthUI` case is deprecated in favour of the neutral form (see [`docs/migrations/494-vendor-neutral-auth-ui.md`](../../../docs/migrations/494-vendor-neutral-auth-ui.md) and the design note [`docs/platform/auth-ui-vendor-neutrality.md`](../../../docs/platform/auth-ui-vendor-neutrality.md)).

The same registry serves Clerk (`src/AuthProviders/ClerkUI/`), the generic OIDC client (`src/AuthProviders/OidcClient/`), and any third-party sign-in companion — peers under the same tag-based contract. The core SDK never references any companion's types, and removing a companion's handler entry + import removes its code from the Fable bundle.

The registry also resolves the "companion imports AuthUIMode but AuthUIMode ships in core" chicken-and-egg — `OidcUIConfig` / `ClerkUIConfig` are declared in `SDK.ClientTypes.fs` (core) so `ClientConfig.AuthUI` always compiles, but the runtime handlers live only in the companions.

### OidcClient companion — Authorization Code + PKCE

`src/AuthProviders/OidcClient/` implements the OIDC Authorization Code + PKCE flow against any OIDC-compliant issuer. Works with Auth0, Keycloak, Okta, Azure AD, Google Identity, or any other conformant IdP.

Enable by importing `OidcClient.Client.props` in the consumer client `.fsproj` and setting `ClientConfig.AuthUI`:

```fsharp
open ToolUp.AuthProviders.Oidc

let oidcConfig: OidcUIConfig = {
    Issuer = "https://auth.example.com"
    ClientId = "<client id>"
    RedirectUri = "https://app.example.com/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = Some "https://app.example.com"
    ValidateIdToken = Some true
    BearerToken = None   // None = send the access token (the default)
    SecondaryFlow = None // None = the single-button sign-in screen (the default)
}

let config = {
    ClientConfig.defaults with
        Surfaces = Surfaces.individual
        AuthUI = OidcAuthUI oidcConfig
        Handlers = {
            ClientHandlerRegistry.empty with
                AuthUIHandlers = [ OidcRegister.handler ]
                SignOutHandler = Some(OidcRegister.signOutHandler oidcConfig)
        }
}
```

The `Handlers.AuthUIHandlers` entry is **not optional**. Since Phase 13a the companion exports a `handler` *value* rather than registering itself at module load, so `AuthUIProvider` fails loudly at startup when `AuthUI = OidcAuthUI _` is set with no handler carrying the `"oidc"` tag. `SignOutHandler` is optional — supply it to have the shell render a "Sign out" affordance wired to the issuer's end-session flow. `OidcUIConfig.defaults issuer clientId redirectUri` fills the last three fields if you would rather not spell the record out.

**Flow:**

1. **Sign-in**: `OidcAuthUI.OidcShell` (the handler component) checks whether the current URL is `RedirectUri`. If not, and no access token is stashed, it renders `SignInScreen`. The button calls `OidcClient.beginSignIn` which generates a PKCE `code_verifier` + `code_challenge` + CSRF `state` + OIDC `nonce`, stashes the verifier/state/nonce in `sessionStorage` (tab-scoped), and redirects to the issuer's `authorization_endpoint` with query parameters.

2. **Callback**: When the issuer redirects back to `RedirectUri`, `OidcShell` detects the match on first mount and calls `OidcClient.handleCallback`. It reads `code` and `state` from the query string, validates `state` against the stashed value (CSRF protection), POSTs `grant_type=authorization_code` + `code_verifier` to the issuer's `token_endpoint`, persists the access token via `UserSession.setAuthToken`, stashes the refresh token under `"toolup-oidc-refresh-token"` in `localStorage`, and calls `history.replaceState` to strip the callback query string (reload safety).

3. **Sign-out**: `OidcClient.signOut` clears `UserSession` + refresh token + PKCE scratch state, then redirects to the issuer's `end_session_endpoint` (if the discovery doc exposes one) with `post_logout_redirect_uri`. If the issuer has no end-session endpoint, the companion falls back to a local-only reload at `window.location.origin`.

4. **Refresh**: `OidcClient.refreshAccessToken` POSTs `grant_type=refresh_token` to the token endpoint. Honours rotated refresh tokens returned by the issuer. Not called automatically yet — the timer that fires before `expires_in` elapses is a Phase 3b follow-up. Apps that observe a 401 from the server can invoke it manually in the meantime.

**PKCE via WebCrypto.** The companion uses browser-native `crypto.getRandomValues` + `crypto.subtle.digest('SHA-256', ...)` — the BCL's `System.Security.Cryptography` doesn't compile to Fable. This is why the companion has zero npm dependencies: discovery, token exchange, and PKCE all use browser-native APIs.

**Discovery caching.** `OidcDiscovery.fetchDiscovery` fetches `{issuer}/.well-known/openid-configuration` once per issuer URL and caches the result in-memory (not `localStorage` — a page reload should re-fetch so configuration rotations take effect).

**Refresh token storage and XSS.** Refresh tokens live in `localStorage` under `"toolup-oidc-refresh-token"`. This is vulnerable to XSS (same attack surface as Clerk's browser-side model). Mitigation options:
- Pair short `expires_in` on access tokens (e.g. 5–15 min) with a tight CSP that disallows inline script + unknown script sources.
- For stronger isolation, migrate to a backend-for-frontend (BFF) flow where the refresh token lives in an HttpOnly cookie on a same-origin auth-relay service and the browser only ever sees short-lived access tokens. Tracked as a Phase 3b follow-up.

**Multi-tab semantics.** PKCE verifier and state use `sessionStorage` (tab-scoped) so two concurrent sign-in flows in two tabs cannot collide. The access token and refresh token live in `localStorage` so a successful sign-in in one tab applies across all tabs.

**Redirect URI matching.** `OidcClient.isCallbackUrl` compares `window.location.pathname` against the configured `RedirectUri`'s pathname — query string is ignored so `?code=…&state=…` does not perturb the match. Apps must register the full `RedirectUri` at the issuer (including the path) and ensure the app is served at that path; typically `{app-origin}/auth/callback`.

**Provider quirks.**
- Some issuers (Okta, Auth0) rotate the refresh token on each refresh; the companion honours `refresh_token` in the refresh response when present.
- Some issuers do not expose `end_session_endpoint` in discovery (Clerk uses its own React SDK for sign-out). The companion degrades to local sign-out + origin reload in that case.
- For multi-audience tokens (e.g. Azure AD with both `api://…` and Graph scopes), the server-side `OidcAuthProvider` accepts any configured audience — the client does not need to do anything special.

**Swapping for Clerk.** The companion pattern is symmetric — Clerk is one provider among peers. An app that was using Clerk (`AuthUI = ClerkRegister.authUI { PublishableKey = … }` — the neutral `ProviderAuthUI ("clerk", _)` form — with `ClerkRegister.handler` in `Handlers.AuthUIHandlers`) can switch to OIDC by changing the handler entry and the config; no SDK-level code changes. Both handlers can be registered simultaneously — the active `ClientConfig.AuthUI` value decides which runs.

## Build-time constants

`ToolUp.Platform.BundleConstants` exposes typed Fable accessors over the consumer's Vite `define` block. Each `define` substitutes a literal into the bundle at build time; the accessor wraps the `typeof X === 'string'` guard so an unwired define doesn't fail with a runtime `ReferenceError`. The convention maps a JS identifier `__NAME__` (double-underscore-bracketed) to an F# value `BundleConstants.name` (lower-camel-case, no underscores).

Two return shapes coexist. The Phase 11.G accessors (Clerk + AG Grid + module filter + platform-surfaces) return a raw `string` that is `""` when unwired; consumers gate on `String.IsNullOrEmpty`. The Phase 16e accessors (Entra + OIDC overrides) return `string option` with `None` when the define is the empty string, the literal JS `'undefined'` string (what Vite emits when a missing `process.env.X` value is `JSON.stringify`'d), or the literal placeholder `__NAME__` (what survives in the bundle when Vite didn't substitute at all). The two-shape split is intentional: Phase 11.G accessors predate the option-typed convention and would be a breaking change to migrate; Phase 16e and onwards adopt `string option` because pattern-matching on `Some` removes the empty-string ambiguity for "set, but to the empty value" callers.

The notifications-disabled accessor (Phase 58) returns `bool` directly — its define is `JSON.stringify`'d as a literal `true` / `false`, not a string, so the wrapping shape differs.

### Typed accessor table

| Phase | Vite define | F# accessor | Type | None / empty when |
|---|---|---|---|---|
| 11.G | `__TOOLUP_MODULE__` | `BundleConstants.moduleFilter` | `string` | unwired → `""` (no module filter applied) |
| 11.G | `__AG_GRID_LICENSE__` | `BundleConstants.agGridLicense` | `string` | unwired → `""` (Community-tier overlays) |
| 11.G | `__CLERK_PUBLISHABLE_KEY__` | `BundleConstants.clerkPublishableKey` | `string` | unwired → `""` (Release Clerk builds should fail loud) |
| 11.G | `__TOOLUP_PLATFORM_SURFACES__` | `BundleConstants.platformSurfaces` | `string` | unwired → `""` (falls back to `Surfaces.anonymous`) |
| 58 | `__TOOLUP_NOTIFICATIONS_DISABLED__` | `BundleConstants.notificationsDisabledExplicitly` | `bool` | unwired → `false` (EventSource opens; the defensive 404-fallback closes for the session) |
| 16e | `__ENTRA_TENANT_ID__` | `BundleConstants.entraTenantId` | `string option` | `""` / `'undefined'` / `__ENTRA_TENANT_ID__` literal → `None` |
| 16e | `__ENTRA_CLIENT_ID__` | `BundleConstants.entraClientId` | `string option` | `""` / `'undefined'` / `__ENTRA_CLIENT_ID__` literal → `None` |
| 16e | `__OIDC_ISSUER_OVERRIDE__` | `BundleConstants.oidcIssuerOverride` | `string option` | `""` / `'undefined'` / `__OIDC_ISSUER_OVERRIDE__` literal → `None` |
| 16e | `__OIDC_AUDIENCE_OVERRIDE__` | `BundleConstants.oidcAudienceOverride` | `string option` | `""` / `'undefined'` / `__OIDC_AUDIENCE_OVERRIDE__` literal → `None` |

### Wiring the defines in `vite.config.mts`

The `platformsdk-solution` template (emitted by `dotnet new platformsdk-solution`) ships a `define:` block with every accessor's mapping pre-declared. A consumer overrides only the env vars they need:

```ts
import { defineConfig } from "vite";

export default defineConfig({
  // ...
  define: {
    __TOOLUP_MODULE__: JSON.stringify(process.env.TOOLUP_MODULE ?? ""),
    __AG_GRID_LICENSE__: JSON.stringify(process.env.AG_GRID_LICENSE ?? ""),
    __CLERK_PUBLISHABLE_KEY__: JSON.stringify(process.env.CLERK_PUBLISHABLE_KEY ?? ""),
    __TOOLUP_PLATFORM_SURFACES__: JSON.stringify(process.env.TOOLUP_PLATFORM_SURFACES ?? ""),
    __TOOLUP_NOTIFICATIONS_DISABLED__: JSON.stringify(process.env.TOOLUP_NOTIFICATIONS_DISABLED === "true"),
    __ENTRA_TENANT_ID__: JSON.stringify(process.env.ENTRA_TENANT_ID ?? ""),
    __ENTRA_CLIENT_ID__: JSON.stringify(process.env.ENTRA_CLIENT_ID ?? ""),
    __OIDC_ISSUER_OVERRIDE__: JSON.stringify(process.env.OIDC_ISSUER_OVERRIDE ?? ""),
    __OIDC_AUDIENCE_OVERRIDE__: JSON.stringify(process.env.OIDC_AUDIENCE_OVERRIDE ?? "")
  }
});
```

A define omitted from the block produces the unset-shape behaviour described in the table above — there is no requirement to declare every define; a deployment that doesn't use Entra simply leaves the two Entra defines out.

### Fail-loud-on-placeholder behaviour

The three substitution failure modes the `option` accessors collapse to `None` are not theoretical — each has been observed in production:

1. **Empty string.** A consumer wires `JSON.stringify(process.env.ENTRA_TENANT_ID ?? "")` and forgets to set `ENTRA_TENANT_ID` in the deployment's env. The bundle contains `""`. Without the `option` shape, a downstream `OidcUIConfig.defaults issuer ""` call constructs a config that fails opaquely at the IdP. With the `option` shape, the consumer matches `Some` and fails loud at composition time.
2. **Literal `'undefined'` string.** A consumer wires `JSON.stringify(process.env.ENTRA_TENANT_ID)` (no `??` fallback) and forgets to set the env var. Node returns `undefined`; `JSON.stringify(undefined)` returns... `undefined` (no quotes — JS, not JSON), but if it's later coerced to a string it becomes the literal four-character string `"undefined"`. The bundle ends up with the literal `'undefined'`. The `option` shape catches this even though the value is technically non-empty.
3. **Literal placeholder.** A consumer's `vite.config.mts` doesn't declare the define at all, but a downstream tool (a CI patch step, a Helm chart, a sed script) attempts to substitute `__ENTRA_TENANT_ID__` in the bundle and fails silently. The bundle ends up with the literal placeholder string still in place. The `option` shape catches this too.

Consumers reading any of the four Phase 16e accessors should `match` on `Some`/`None` rather than testing for empty / placeholder values on a raw string — the SDK has already done that filtering.

## Secret Storage and Encryption

`ISecretStore` is a scope-aware key-value store for credentials (API keys, connection strings, signing secrets). In-process implementations: `FileSecretStore` (JSON files on disk), `EnvironmentSecretStore` (read-only env-var-backed), `EncryptedSecretStore` (AES-GCM envelope wrapper over any inner store). Cloud-KMS companion implementations: `AzureKeyVaultSecretStore`, `AwsSecretsManagerSecretStore`, `VaultSecretStore` (HashiCorp Vault KV v2) — Phase 2a; `GcpSecretManagerSecretStore` — Phase 2b. Scope discipline mirrors `IBlobStorage` — `_platform` for deployment-level secrets, `user-{userId}` / `team-{teamId}` for per-tenant secrets.

### Cloud secret-manager companions (Phase 2a + 2b)

The four cloud-secret-manager companions implement `ISecretStore` against managed cloud secret services. All four are independent NuGet packages; a deployment imports exactly one. Activation is via env-driven switch in the composition root, mirroring the `TOOLUP_BLOB_STORAGE` pattern from Phase 2.

| Companion | Package | Activation env vars | Backing API |
|---|---|---|---|
| Azure Key Vault | `ToolUp.Secrets.AzureKeyVault` | `TOOLUP_SECRET_STORE=azure-key-vault` + `TOOLUP_AZURE_KEY_VAULT_URL` | `Azure.Security.KeyVault.Secrets` + `DefaultAzureCredential` |
| AWS Secrets Manager | `ToolUp.Secrets.AwsSecretsManager` | `TOOLUP_SECRET_STORE=aws-secrets-manager` + `TOOLUP_AWS_SECRETS_REGION` | `AWSSDK.SecretsManager` + AWS SDK credential chain |
| HashiCorp Vault | `ToolUp.Secrets.HashiCorpVault` | `TOOLUP_SECRET_STORE=vault` + `VAULT_ADDR` + `VAULT_TOKEN` + optional `VAULT_NAMESPACE` | BCL `HttpClient` against KV v2 HTTP API; no vendor SDK |
| GCP Secret Manager | `ToolUp.Secrets.GcpSecretManager` | `TOOLUP_SECRET_STORE=gcp-secret-manager` + `TOOLUP_GCP_PROJECT_ID` | `Google.Cloud.SecretManager.V1` + Application Default Credentials |

Each companion sanitises scope IDs and keys into its vendor's allowed-character set, then stores secrets under a `toolup/{scopeId}/{key}` (or vendor-equivalent) prefix. The prefix carries the scope explicitly, so the vendor's audit log (Azure Key Vault diagnostic logs, AWS CloudTrail, Vault audit log) records which ToolUp scope each request touched — cross-scope reads are visible at the audit layer.

**IAM / policy minimums** ship in each companion's `README.md`. The pattern is consistent: get / set / delete / list capabilities scoped to the `toolup/*` (or `toolup-*`) name prefix. `ListSecrets` on AWS Secrets Manager is account-wide by design — the companion filters client-side by name prefix.

**Cloud-KMS-native at-rest encryption.** All three vendors encrypt secrets at rest with their own KMS (Azure Key Vault: HSM-backed AES-256; AWS Secrets Manager: KMS-managed CMK; Vault: barrier key, typically auto-unsealed via cloud KMS / HSM in production). Wrapping a cloud companion in `EncryptedSecretStore` would add a redundant envelope — the composition root deliberately does NOT wrap cloud-KMS companions, and the Phase 6l.E plaintext-secrets validator recognises `TOOLUP_SECRET_STORE ∈ { azure-key-vault | aws-secrets-manager | vault }` as equivalent to `EncryptedSecretStore` for the master-key gate.

**Soft-delete behaviour differs per vendor** and is documented in each companion's `README.md`:
- Azure Key Vault: soft-delete with 90-day default retention. Re-creating a name during the window requires explicit purge.
- AWS Secrets Manager: scheduled deletion with 7-30 day recovery window. `GetSecretValue` returns "not found" immediately; re-create during the window fails with `InvalidRequestException`.
- HashiCorp Vault: metadata-delete wipes ALL versions immediately; re-create is unconstrained.

The `ISecretStore` contract test pack (`ISecretStoreContract.tests`) passes against every companion when the activating env vars are set, with each pack using GUID-suffixed scope IDs to dodge cross-test soft-delete collisions. CI bindings live in `src/ToolUp.Platform.Tests/InProcess/{AzureKeyVault,AwsSecretsManager,HashiCorpVault}SecretStoreTests.fs` and skip cleanly (single `pending` test) when the activating env var is unset.

### In-process implementations

### At-rest encryption

`EncryptedSecretStore` wraps an inner store to encrypt values before they land on disk. Envelope format: `base64("TOEN" || nonce (12) || ciphertext (N) || tag (16))`. The magic prefix lets the wrapper distinguish its own envelopes from legacy plaintext; non-envelope values pass through on read, enabling gradual migration (the next `SetSecret` for each key promotes it to encrypted form).

The master key is a 32-byte AES-256 value supplied via the `TOOLUP_SECRETS_MASTER_KEY` environment variable, base64-encoded. When unset, the wrapper passes values through unchanged and emits a startup stderr warning — acceptable for dev, should be set in production.

Generate a fresh key once at deployment setup:

```fsharp
printfn "%s" (EncryptedSecretStore.generateMasterKey())
```

Then set the env var and restart:

```bash
export TOOLUP_SECRETS_MASTER_KEY="<generated-base64-value>"
```

### Key rotation

Rotating the master key requires decrypting every existing envelope under the old key and re-encrypting under the new one. The `EncryptedSecretStore.rotateScope` helper automates this per scope:

```fsharp
open ToolUp.Platform
open ToolUp.Platform.Secrets
open EncryptedSecretStore

let oldKey = (parseMasterKey "<current base64>").Value
let newKey = (parseMasterKey (generateMasterKey())).Value

// Rotate one scope at a time; `inner` is the raw writable store
// (FileSecretStore), not the encrypted wrapper — the helper needs
// envelope bytes, not transparent-decrypted plaintext. The logger
// receives one event per key (Info / Debug / Warn depending on
// outcome) so the run is auditable.
let logger: ILogger = ConsoleLogger.ConsoleLogger()

// `rotateScope` is asynchronous, so the run sits in an `async` block.
let rotate = async {
    let! outcomes =
        rotateScope (inner :> ISecretStore) oldKey newKey "user-alice" logger

    let summary = summariseRotation outcomes

    printfn "Rotated %d / %d (unchanged: %d, failed: %d)"
        summary.Rotated summary.Total summary.Unchanged summary.Failed
}

Async.RunSynchronously rotate
```

Per-secret outcomes:
- `Rotated key` — decrypted under old key, re-encrypted under new, written back.
- `Unchanged (key, reason)` — value is plaintext (legacy), already under the new key (idempotent re-run), or doesn't decrypt (corruption). Not touched.
- `Failed (key, reason)` — decrypt succeeded but the re-encrypt write failed.

Rotation is **idempotent** — running it twice produces `Rotated=0, Unchanged=N` on the second run. Safe to re-run after a partial failure.

Rotation **scopes**: `ISecretStore.ListKeys` enumerates keys within one scope. The SDK does not enumerate scopes; operators list their known scope containers (typically `_platform` plus all `user-*` / `team-*` container names surfaced by `IBlobStorage.List` or direct filesystem enumeration under `FileSecretStore`'s data directory) and rotate each.

After rotation, update `TOOLUP_SECRETS_MASTER_KEY` to the new value and restart the server. The old key must be available during rotation but should be retired once the deployment confirms no stale envelopes remain (next `Unchanged` count is zero for envelopes).

## Blob storage encryption at rest (Phase 22)

Cloud blob-storage providers (Azure Blob, AWS S3, GCS) encrypt at rest by default at the provider's storage layer. The Phase 22 surface covers two concerns the cloud-default doesn't address:

1. **Surface what the cloud is doing.** Companion `IConfigValidator` impls run at startup preflight — `S3EncryptionAtRestValidator`, `AzureBlobEncryptionAtRestValidator`, `GcsEncryptionAtRestValidator`. Each calls the provider's "is encryption configured?" API and returns `Ok` / `Warning` / `Error`. Misconfigured deployments fail loudly at boot, not on first blob op.
2. **Optional app-level envelope encryption** for deployments that need cryptographic separation between tenants beyond what the bucket-level cloud-default provides.

### Decision matrix

| Deployment shape | Recommended setup |
|---|---|
| Single-tenant, single-instance, cloud storage | Cloud default + companion validator. No app-level encryption needed. |
| Multi-tenant on shared cloud storage | Cloud default + companion validator. Application-level scope isolation handles tenant separation. |
| Multi-tenant + cryptographic separation requirement (independent practices, agencies, regulated sector) | Cloud default + `EncryptedBlobStorage` + `PerScopeKeyResolver`. |
| Single-user / dev / air-gapped on local disk | `LocalFileStorage` un-wrapped. The `LocalFileStorageEncryptionAtRestValidator` emits a startup `Warning` so the deployment is reminded encryption is provider's-default-only. |
| BYOK / formal key custody (regulated, KMS-required) | Cloud default + `EncryptedBlobStorage` + KMS-backed resolver (Phase 22a sub-companion, deferred). |

### `EncryptedBlobStorage` decorator

Wraps any `IBlobStorage` and applies AES-GCM envelope encryption to `Upload` / `Download`. `List` / `Delete` / `Exists` / `GetMetadata` pass through unchanged — they don't touch ciphertext bytes.

Envelope layout (raw bytes, no base64): `[Magic:4 "TOBL"][KeyIdLen:1][KeyId:N UTF-8][Nonce:12][Tag:16][Ciphertext:M]`.

```fsharp
open ToolUp.Platform.BlobStorage

let storage = LocalFileStorage.LocalFileStorage("data") :> IBlobStorage
let secrets = FileSecretStore.FileSecretStore() :> ISecretStore
let resolver = SingleKeyResolver.create secrets

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage storage
|> ServerApp.withEncryptedBlobStorage resolver
|> ServerApp.run
```

Every downstream consumer (`DataObjectStore`, `EventStore`, `JobStore`, `WebhookRegistry`, `TeamStore`, `PermissionStore`, `NotificationAddressBook`, etc.) receives the wrapped instance via the existing `IBlobStorage` registration — encryption is transparent to module code.

### Resolver choice

The resolver is the policy point. Three implementations ship today; a fourth (KMS-backed) is reserved for Phase 22a.

#### `SingleKeyResolver` (default)

One platform-wide AES-256 key shared across all scopes. Persisted at `_platform/encryption/master.key` via `ISecretStore`; auto-created on first resolution. Use case: deployments where one cryptographic boundary is enough.

#### `PerScopeKeyResolver` (opt-in)

One key per `StorageScope.ScopeId`, persisted at `_platform/encryption/scopes/{scopeId}.key`. Auto-created per scope on first resolution; in-memory cache with 5-minute sliding TTL.

```fsharp skip=fragment
let cache = MemoryCache(MemoryCacheOptions()) :> IMemoryCache
let auditLog = ... // resolved from DI in compose; None for tests
let resolver = PerScopeKeyResolver.create secrets cache auditLog

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage storage
|> ServerApp.withEncryptedBlobStorage (resolver :> IBlobEncryptionKeyResolver)
|> ServerApp.run
```

Use case: multi-tenant deployments hosting independent practices, agencies, or businesses on a single instance.

What this enables:
- **Defence in depth.** A misrouted blob read from team A under team B's resolver fails to decrypt — application-layer scope-isolation bugs no longer leak plaintext.
- **Crypto-shredding for tenant offboarding.** `DestroyKey scopeId` removes the key permanently; all blobs encrypted under it become undecryptable in one operation. Clean answer to GDPR right-to-be-forgotten and contract-termination workflows.
- **BYOK groundwork.** Per-scope keys are the substrate every customer-managed-key story builds on; the same resolver shape can resolve from a customer-supplied KMS endpoint instead of `ISecretStore`.

#### Custom resolvers

Deployments needing per-`(scopeId, userId)` keying inside a Team-mode scope, or BYOK against a customer-supplied endpoint, write a custom `IBlobEncryptionKeyResolver` against the same interface. The interface is six-rule portable (Phase 9c) so a custom resolver can target any persistence backend without changing the decorator.

### Key rotation

`KeyId` lives in the envelope header — the decryptor reads the envelope, looks up the historical key by id via `ResolveKeyById`, and decrypts. New writes use the resolver's *current* key (`ResolveKey`).

V1 resolvers ship with stable `KeyId`s (`_platform/master/v1`, `_platform/scopes/{scopeId}/v1`). True rotation requires a v2 resolver that writes new uploads under `KeyId = ".../v2"` while keeping `ResolveKeyById` answering for `.../v1` reads. The interface supports this; a default v2-rotating resolver is a follow-up.

### Crypto-shredding (tenant offboarding)

`PerScopeKeyResolver.DestroyKey scopeId actorUserId` removes the cache entry and deletes the persisted key from `ISecretStore`. After this:
- Existing blobs in that scope return `Error KeyDestroyed` on read (the decorator surfaces this as a 410-shaped error to the API boundary).
- New uploads create a fresh key (re-runs the auto-create path); previously-uploaded blobs remain undecryptable forever.

The canonical admin path is the `POST /api/_platform/encryption/destroy-scope-key/{scopeId}` endpoint, gated by `TOOLUP_ADMIN_TOKEN` env var + `X-Admin-Token` request header. Auth is intentionally cross-tenant — team-level RBAC doesn't apply because tenant offboarding is a deployment-admin operation.

#### Timing contract: minute-grain replica-fanout time, not instant (Phase 22b)

**On the replica that serves the destroy request, the shred is complete when `DestroyKey` returns. Across the rest of the fleet it completes at minute grain.** The original Phase 22 framing — "instant tenant offboarding" — described the single-replica case and read as a fleet-wide promise, which it never was: `DestroyKey` evicts the *local* `IMemoryCache` entry and deletes the *shared* `ISecretStore` secret, and neither of those reaches a sibling replica's already-warm cache. Before Phase 22b that cache kept the destroyed key usable for up to the full 5-minute sliding TTL, so a shred that returned success on replica A went on serving that tenant's plaintext on replica B for minutes.

Phase 22b closes the window by broadcasting rather than waiting for it to expire. After the local eviction and the persistent delete succeed, `DestroyKey` publishes a `KeyDestroyedEnvelope` — `(ScopeId, KeyId, RequestedBy, RequestedAt)` plus the originating replica's id — as a `CustomNotification` on `NotificationKind.PlatformReservedScope`. Every replica subscribes at startup (compose calls `WireToChannel` once the channel resolves); on receipt each one evicts its own cache entry for that scope and records an `EncryptionKeyDestroyAcknowledged` audit event. The publish happens strictly *after* the persistent delete, so a replica that evicts on the broadcast cannot repopulate from a secret that is still present.

**The propagation window is the active `INotificationChannel` companion's fanout latency — the SDK promises nothing tighter than the interface's own precision contract, which is minute grain.** Two consequences to hold onto:

| Composed channel | Fanout reach | Effective shred window on sibling replicas |
|---|---|---|
| `InMemoryNotifications` / `NoNotifications` (in-process default) | the publishing process only | unchanged — up to the 5-minute sliding TTL |
| `RedisNotifications` (or another distributed companion) | every subscribed replica | the companion's pub/sub delivery latency, typically sub-second, contractually minute-grain |

- **A single-replica deployment needs no distributed channel.** There is no sibling cache to evict, so the broadcast is a harmless no-op and the shred really is complete on return (GP 11 / GP 13 — the fanout costs a deployment that cannot use it nothing).
- **A multi-replica deployment needs one, and two preflight validators say so.** `PerScopeKeyResolverDistributedValidator` fails startup with `Error` when the operator has declared more than one replica alongside a fanout that cannot reach a sibling. `KeyDestroyAckCoverageValidator` (Phase 22b) emits a `Warning` for the shape that does *not* declare it — a `Team` / `MultiTeam` deployment with `PerScopeKeyResolver` and an in-process channel — because with an in-process channel a fleet-wide gap and a fleet of one produce byte-identical evidence: zero acknowledgements either way.

#### `WireToChannel`: optional on one replica, REQUIRED on more (Phase 458)

**This is the whole rule, and it is enforced at startup rather than left to convention:**

| Replica count | `WireToChannel` | Distributed channel companion | What preflight does |
|---|---|---|---|
| 1 (declared or defaulted) | optional | not needed | nothing — the fanout is a correct no-op |
| more than 1, **declared** | **required** | **required** | `Error`, startup refused |
| more than 1, undeclared but `Team` / `MultiTeam` shaped | required in fact | required in fact | `Warning` (a legitimate one-replica Team deployment must still boot) |

`compose` calls `WireToChannel` for every `PerScopeKeyResolver` it composes, so a deployment built through `ServerApp.withEncryptedBlobStorage` satisfies the wiring half automatically and only has to choose a channel companion. An *unwired* resolver in practice means one built and driven outside `compose` — bespoke admin tooling, a custom composition root, a test.

Three things changed in Phase 458, each closing a way the requirement could be true and unenforced:

- **The declared replica count is read from `ServerConfig.ReplicaCount` as well as `TOOLUP_REPLICA_COUNT`** (the greater of the two wins, so a stale env var can only raise the count). It previously read the environment *only*, while all six sibling topology validators read the config field — so a deployment declaring `{ config with ReplicaCount = 3 }` in code tripped every other multi-instance check and silently skipped this one. It was the single hard-`Error` guard in that set, and the one bypassable by configuring in the ordinary way.
- **An unwired resolver is refused too, not just an in-process channel.** Wiring and channel choice are two independent ways for the broadcast to reach nobody; the validator now names which one is missing.
- **An unwired `DestroyKey` is no longer silent.** The first one per process logs a security-class `ILogger.Warn` naming the up-to-five-minute staleness window, the remedy, and the affected scope; every one is counted. The count — not just the log — is what the `/dev/inspect` panel below reports, because a resolver built with `PerScopeKeyResolver.create` has no logger to warn through (`createWithLogger` supplies one).

**Confirming the wiring without reading compose code:** the `/dev/inspect` **"Crypto-shred fanout"** panel reports `WiredToChannel`, the staleness window that applies when it is `false`, and `UnwiredDestroyKeyCalls` — shreds that already published no broadcast, which on a multi-replica deployment is a list of tenants that stayed decryptable on every sibling. The panel is registered only when the composed resolver is a `PerScopeKeyResolver`; a `SingleKeyResolver` has no shred and gets no panel.

That last point is why the acknowledgement event is per replica and names the replica that made it (`AcknowledgedBy`, defaulting to `{machine-name}/{process-id}` — the pod identity in a container deployment). Auditing a shred means counting acknowledgements against expected replicas; a single undifferentiated "destroyed" row cannot answer that. `AcknowledgedAt - RequestedAt` gives the measured fanout delay per replica, so the window above is observable in the trail rather than merely asserted here.

Ordering is deliberately not part of the contract (portability rule 5, Phase 9c): eviction is idempotent and order-insensitive, so a distributed companion may fan out per shard with no cross-shard sequencing promise. Every replica converges on "evicted" regardless of the order envelopes arrive in.

### Performance budget

- AES-GCM throughput is hundreds of MB/s on a single core; the cipher itself is rarely the bottleneck.
- Resolver lookup dominates. `SingleKeyResolver` caches the master key in-memory after first resolution — steady-state cost is one map lookup per blob op.
- `PerScopeKeyResolver` caches per `scopeId` (5-min sliding TTL) — same in-memory cost. First-touch per scope hits `ISecretStore` once.
- KMS-backed resolvers (Phase 22a) carry a network round-trip per cache miss; document the latency budget per provider in their respective companion `README`s.

### What cloud-provider encryption gives you and what it doesn't

| Capability | Cloud default | + `EncryptedBlobStorage` + `PerScopeKeyResolver` |
|---|---|---|
| Bytes encrypted at rest | ✓ | ✓ (twice, layered) |
| Single deployment-wide key | ✓ | — |
| Per-tenant cryptographic separation | — | ✓ |
| Crypto-shredding for tenant offboarding | — | ✓ |
| Defence-in-depth against application-layer scope-isolation bugs | — | ✓ |
| BYOK per tenant | — | ✓ via custom resolver |
| At-rest protection of pre-uploaded backup snapshots | depends on backup tooling | ✓ for blobs the SDK manages |

### Audit emission

Four audit cases under `SourceModule = "_platform.audit"` (the SDK's standard audit module):
- `EncryptionKeyCreated` — auto-creation on first resolution.
- `EncryptionKeyRotated` — reserved for the future v2 rotation flow.
- `EncryptionKeyDestroyed` — `PerScopeKeyResolver.DestroyKey` invocation, on the replica that served it.
- `EncryptionKeyDestroyAcknowledged` (Phase 22b) — one per *other* replica, when its subscription handler evicts the destroyed key. Carries `AcknowledgedBy` / `OriginReplicaId` and both instants, so the fanout is auditable per replica. The originating replica does not self-acknowledge.

`UserId` on the payload is `"system"` for SDK-managed auto-creation; the authenticated actor for explicit destruction. On the acknowledgement it stays the *requester* carried across from the envelope, not the acknowledging replica — so "who crypto-shredded this tenant" returns one actor across every replica's row.


---

> [← Prev: 2. Multi-Tenancy, Teams & Access Control](02-multi-tenancy-and-access.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 4. Data & Storage Substrate →](04-data-and-storage-substrate.md)
