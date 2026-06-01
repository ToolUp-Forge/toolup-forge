# ToolUp.Platform Technical Guide — 03. Authentication, Secrets & Encryption

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 2. Multi-Tenancy, Teams & Access Control](02-multi-tenancy-and-access.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 4. Data & Storage Substrate →](04-data-and-storage-substrate.md)

---

## Authentication Providers

### The IAuthProvider contract

```fsharp
type IAuthProvider =
    abstract GetUser: obj -> Async<AuthenticatedUser>
    abstract ValidateRequest: obj -> Async<Result<AuthenticatedUser, string>>
```

Both methods are async (so provider implementations can make network calls — JWKS discovery, token introspection, external directory lookup — without blocking). `GetUser` is lenient: returns `AuthenticatedUser.anonymous` on any failure. `ValidateRequest` is strict: returns `Error reason` on missing / invalid / expired credentials. The `obj` parameter is boxed `HttpContext`, kept as `obj` so the interface lives in the shared layer without referencing ASP.NET Core.

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

`src/ToolUp.Platform/Client/AuthUIProvider.fs` owns a mutable `Map<string, Handler>` where `Handler = obj -> ReactElement -> ReactElement`. Companions call `AuthUIProvider.register tag handler` at module load (top-level `do` binding) — the shell calls `AuthUIProvider.gate authUI mode shell` on every render and dispatches to the matching handler. Missing handler fails loud with a message naming the `.Client.props` that needs to be imported.

The same registry serves Clerk (`src/AuthProviders/ClerkUI/`) and the generic OIDC client (`src/AuthProviders/OidcClient/`) — the core SDK never references either companion's types, and removing a companion's `.Client.props` import from the consuming client `.fsproj` removes its code from the Fable bundle.

The registry also resolves the "companion imports AuthUIMode but AuthUIMode ships in core" chicken-and-egg — `OidcUIConfig` / `ClerkUIConfig` are declared in `SDK.ClientTypes.fs` (core) so `ClientConfig.AuthUI` always compiles, but the runtime handlers live only in the companions.

### OidcClient companion — Authorization Code + PKCE

`src/AuthProviders/OidcClient/` implements the OIDC Authorization Code + PKCE flow against any OIDC-compliant issuer. Works with Auth0, Keycloak, Okta, Azure AD, Google Identity, or any other conformant IdP.

Enable by importing `OidcClient.Client.props` in the consumer client `.fsproj` and setting `ClientConfig.AuthUI`:

```fsharp
let config = {
    ClientConfig.defaults with
        Mode = Individual
        AuthUI = OidcAuthUI {
            Issuer = "https://auth.example.com"
            ClientId = "<client id>"
            RedirectUri = "https://app.example.com/auth/callback"
            Scopes = [ "openid"; "profile"; "email" ]
            PostLogoutRedirectUri = Some "https://app.example.com"
        }
}
```

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

**Swapping for Clerk.** The companion pattern is symmetric. An app that was using Clerk (`AuthUI = ClerkAuthUI _` + `ClerkUI.Client.props` imported) can switch to OIDC by changing the import and the config; no SDK-level code changes. Both handlers can be registered simultaneously — the active `AuthUIMode` case decides which runs.

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
open EncryptedSecretStore

let oldKey = (parseMasterKey "<current base64>").Value
let newKey = (parseMasterKey (generateMasterKey())).Value

// Rotate one scope at a time; `inner` is the raw writable store
// (FileSecretStore), not the encrypted wrapper — the helper needs
// envelope bytes, not transparent-decrypted plaintext. The logger
// receives one event per key (Info / Debug / Warn depending on
// outcome) so the run is auditable.
let logger: ILogger = ConsoleLogger.ConsoleLogger()

let! outcomes =
    rotateScope (inner :> ISecretStore) oldKey newKey "user-alice" logger

let summary = summariseRotation outcomes
printfn "Rotated %d / %d (unchanged: %d, failed: %d)"
    summary.Rotated summary.Total summary.Unchanged summary.Failed
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

```fsharp
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

Three audit cases under `SourceModule = "_platform.audit"` (the SDK's standard audit module):
- `EncryptionKeyCreated` — auto-creation on first resolution.
- `EncryptionKeyRotated` — reserved for the future v2 rotation flow.
- `EncryptionKeyDestroyed` — `PerScopeKeyResolver.DestroyKey` invocation.

`UserId` on the payload is `"system"` for SDK-managed auto-creation; the authenticated actor for explicit destruction.


---

> [← Prev: 2. Multi-Tenancy, Teams & Access Control](02-multi-tenancy-and-access.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 4. Data & Storage Substrate →](04-data-and-storage-substrate.md)
