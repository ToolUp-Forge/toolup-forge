# Mixed-Mode Platform Model — Design

> **Status:** Initial design complete (all 6 phases). Ready for implementation planning.
> **Scope:** Replace forge's deployment-wide `PlatformMode` enum with a multi-axis model that allows a single deployment to serve anonymous + authenticated surfaces concurrently.
> **Backward-compat posture:** None. Every current consumer can be updated; the design optimises for the right shape, with migration plan delivered alongside.
> **Out of scope:** Implementation. This document is a design pass; the deliverable is the document itself.

---

## Executive summary

forge's deployment-wide `PlatformMode` enum (`Anonymous` / `AuthenticatedEphemeral` / `Individual` / `Team` / `MultiTeam`) prevents a single deployment from serving anonymous and authenticated surfaces concurrently. Partial workarounds exist today — `AnonymousRoutePrefixes`, `IPublicFormApi` with share tokens, peer-bearer route exemption — but each is a per-feature exemption rather than a generalised model.

This design replaces `PlatformMode` with three coordinated types:

- **`Subject`** (per-request) — a 4-case DU `AnonymousSession | AuthenticatedUser | TeamMember | ClaimBearer`. The single lever that determines storage scope, persistence, permissions, and audit attribution.
- **`SurfaceProfile list`** (per-deployment, replaces `ServerConfig.Mode`) — declares which subject shapes the deployment supports. A single-shape deployment is one entry; mixed-mode is two or more, with the same authoring idiom.
- **`SurfaceRequirement`** (per-route) — a set of `SubjectKind` values the route admits. Module-default + per-endpoint override.

Single-mode authoring stays one-line (`Surfaces = Surfaces.individual`). Mixed-mode is the same idiom with a list of pre-named helpers (`Surfaces.anonymousAndIndividual`, etc.). Share-token-gated anonymous access becomes a first-class subject shape (`ClaimBearer`), retiring the `AnonymousRoutePrefixes` per-feature pattern. Peer-bearer (federation) stays orthogonal — delegated authority is a different concern from "acting as a subject".

Migration shape: **clean cutover**, single coordinated bump across the SDK and every downstream consumer. Each consumer migrates with small touch sites; a consumer's current workaround (`Mode = Individual + DevDefaultUserId = "dev-admin"`, used to fake a public-utility surface inside a single-mode deployment) retires entirely because the new model serves its actual use case directly as `Surfaces = Surfaces.anonymousAndIndividual`.

The design lands before the OSS-public flip so the public release reflects the right model from day 1.

This document is the design pass; implementation begins from §5.1 Stream A.1.

---

## Phase 1 — Current state

This section reports forge's existing `PlatformMode` model **as it stands**, with `file:line` citations. No proposals are made here; that is Phase 2. The phase exists because the migration plan is only sound to the extent that the current state is fully mapped.

The reader should leave Phase 1 understanding (a) the type's shape and helpers, (b) every ServerConfig field that depends on Mode, (c) how each `PlatformMode` value selects scope resolution, auth enforcement, persistence routing, and client-side identity, (d) the partial mixed-mode primitives that already exist (`AnonymousRoutePrefixes`, share tokens, peer routes), (e) how each current consumer uses the model today.

### 1.1 The `PlatformMode` type and its helpers

`PlatformMode` is defined in [`StorageScope.fs:18-23`](../../src/ToolUp.Platform.Core/Shared/Types/StorageScope.fs):

```fsharp
type PlatformMode =
    | Anonymous              // No auth, no persistence
    | AuthenticatedEphemeral // Auth required, no persistence
    | Individual             // Auth required, persistent per-user
    | Team                   // Auth required, persistent per-team — single-team-per-user UX
    | MultiTeam              // Auth required, persistent per-team — switching is a normal user action
```

The doc-comment above the declaration ([`StorageScope.fs:6-17`](../../src/ToolUp.Platform.Core/Shared/Types/StorageScope.fs)) flags an important nuance: `Team` and `MultiTeam` **share an identical server-side data model** (each scope is a `team-{teamId}` container; the store always permits multi-membership). They differ in *deployment intent and client UX* — `Team` suppresses the header team-switcher and the `TeamSwitched` reset path; `MultiTeam` activates them. Server-side the two are interchangeable.

Two helpers, both in the `PlatformMode` module ([`StorageScope.fs:25-47`](../../src/ToolUp.Platform.Core/Shared/Types/StorageScope.fs)):

- `PlatformMode.requiresAuth` — `false` for `Anonymous`, `true` for the other four. Used by `AuthEnforcementMiddleware` to decide whether to reject unauthenticated requests, and by every Mode-aware ConfigValidator.
- `PlatformMode.isTeamScoped` — `true` for `Team` and `MultiTeam` only. Gates `ITeamStore` registration, team-switcher UI injection, and the shell-level `TeamSwitched` event handling.

Three companion types live in the same file:

- `StorageScope` ([`StorageScope.fs:51-55`](../../src/ToolUp.Platform.Core/Shared/Types/StorageScope.fs)) — the resolved per-request shape: `{ ScopeId: string; Container: string; Persist: bool }`. `Container` follows the convention `session-{id}` / `user-{id}` / `team-{id}`; `Persist` is `false` for `Anonymous` and `AuthenticatedEphemeral`, `true` otherwise. **The `Persist` flag is the actual lever that decides whether `IBlobStorage` writes happen** — the rest of the persistence story falls out of it.
- `ScopeResolutionRequest` ([`StorageScope.fs:65-80`](../../src/ToolUp.Platform.Core/Shared/Types/StorageScope.fs)) — input to a scope resolver: `User: AuthenticatedUser option`, `SessionId: string option`, `Headers: Map<string, string>`. Deliberately ASP.NET-Core-free so the resolver can be implemented in a sidecar (the portability contract).
- `ScopeResolutionError` ([`StorageScope.fs:84-93`](../../src/ToolUp.Platform.Core/Shared/Types/StorageScope.fs)) — `NotAuthenticated | NoActiveTeam | NotTeamMember of teamId | ScopeResolutionFailed of message`. Translated to 401/403/500 at the request boundary.

`AccessContext` ([`AccessContext.fs:9-27`](../../src/ToolUp.Platform.Core/Shared/Types/AccessContext.fs)) is the per-request capability record. It carries `UserId`, `TeamId: string option`, `Mode: PlatformMode` (copied verbatim from `ServerConfig.Mode` at request time, line 12), `ModulePermissions: Map<string, ModulePermission list>`, and `PlatformRole: PlatformRole option`. Three helpers branch on `Mode`:

- `AccessContext.configScope` ([`AccessContext.fs:75-92`](../../src/ToolUp.Platform.Core/Shared/Types/AccessContext.fs)) returns `Some StorageScope` for persistent config writes: team-scope for `Team`/`MultiTeam`, user-scope for `Individual`/`AuthenticatedEphemeral`, `None` for `Anonymous`.
- `AccessContext.flagScope` ([`AccessContext.fs:105-111`](../../src/ToolUp.Platform.Core/Shared/Types/AccessContext.fs)) mirrors `configScope` for feature-flag overrides.
- `AccessContext.canModifyPlatformConfig` ([`AccessContext.fs:127-128`](../../src/ToolUp.Platform.Core/Shared/Types/AccessContext.fs)) — `true` iff `PlatformRole = Some PlatformAdmin`. This is the only hard structural gate on platform-scope writes.

### 1.2 `ServerConfig` — every Mode-related field

`ServerConfig` is declared in [`SDK.Shared.fs:1269`](../../src/ToolUp.Platform.Core/Shared/SDK.Shared.fs) onward. The Mode-touching fields:

| Field | Line | Semantics |
|---|---|---|
| `Mode: PlatformMode` | 1277 | The deployment-wide mode. Drives every downstream selection. |
| `AnonymousRoutePrefixes: string list` | 1435 | Path prefixes (case-insensitive `StartsWith`) that bypass `AuthEnforcementMiddleware` and `CsrfMiddleware`. Forms publishable surfaces register here (Phase 21b). Validated at startup. |
| `AcceptShallowAnonymousRoutePrefix: bool` | 1461 | Escape hatch for the prefix validator's refusal of coarse paths (`/`, `/api`, 2-segment prefixes). Default `false`. |
| `PeerRoutePrefixes: string list` | 1454 | Path prefixes for `PeerBearerAuthMiddleware`. Peer-bearer routes are exempt from user auth; the bearer IS the authentication. Exempt from CSRF. Stamped `HttpContext.Items["PeerName"]`. Default empty. |
| `AcceptHeaderAuthInAuthenticatedMode: bool` | 1725 | Opt-in to running `HeaderAuthProvider` (client-supplied `X-User-Id`) in auth-requiring Mode. Default `false`; validator refuses startup unless `true`. Intended for mTLS-proxy deployments. |
| `AcceptPlaintextSecretsInAuthenticatedMode: bool` | 1740 | Opt-in to plaintext-secrets wrapper in auth-requiring Mode. Default `false`. |
| `AcceptNoRateLimitInAuthenticatedMode: bool` | ~1400 | Silences the rate-limit-missing validator for proxy-behind deployments. |
| `RateLimit: RateLimitConfig option` | 1392 | Per-team / per-user / per-IP partition depending on Mode. Default `None`. |
| `SseAuthMode: SseAuthMode` | 1681 | `QueryParamFallback` (default) or `CookieRequired`. The former allows the SSE carve-out in `AuthEnforcementMiddleware`; the latter routes SSE through normal auth. |
| `MaxSseConnectionsPerScope: int option` | 1706 | Concurrent SSE cap per scope id. Default `Some 10`. |
| `ShareTokenStore: ShareTokenStoreMode` | 1424 | `NoShareTokenStore` (default) or `EnabledShareTokenStore`. Powers anonymous-gated persistent writes via `BlobShareTokenStore`. |
| `PublicBaseUrl: string option` | 1493 | Base URL for share-link composition. Default `None`. |
| `AuditLog: AuditLogMode` | 1562 | `NoAuditLog` default; validator warns when paired with authenticated Mode. |
| `EventStore: EventStoreMode` | 1289 | `InMemoryOnly` default; opt-in to `PersistentBlobBacked`. |
| `Notifications: NotificationMode` | 1569 | `NotificationsAuto` default — infers in-memory vs no-op from feature presence. |
| `ReplicaCount: int` | 1762 | Operator-declared replica count. `JobSchedulerInstanceValidator` refuses startup when `> 1` and scheduler is in-process (unless `AcceptInProcessSchedulerInMultiInstance = true`). |

`ServerConfigOverrides` ([`SDK.Shared.fs:2048`](../../src/ToolUp.Platform.Core/Shared/SDK.Shared.fs)) carries the same shape with `option`-wrapped fields; the `referenceApp` defaults and `fromEnv` helpers compose `ServerConfig.defaults` with override layers.

### 1.3 Scope resolution — the four resolvers

The dispatch is at [`SDK.Server.fs:1208-1221`](../../src/ToolUp.Platform.Server/Server/SDK.Server.fs):

```fsharp
let scopeResolver: IStorageScopeResolver =
    match config.Mode with
    | Anonymous -> AnonymousScopeResolver()
    | AuthenticatedEphemeral -> AuthenticatedEphemeralScopeResolver()
    | Individual -> AuthenticatedScopeResolver()
    | Team | MultiTeam ->
        new TeamScopeResolver(
            teamStoreOpt.Value :> ITeamStore,
            new MemoryCache(MemoryCacheOptions()),
            resolvedNotificationChannel,
            resolvedLogger)
```

All four implementations live in [`StorageScopeResolver.fs`](../../src/ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs):

- **`AnonymousScopeResolver`** ([`StorageScopeResolver.fs:24-38`](../../src/ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs)) — reads `SessionId` (generates a GUID if absent), returns `Ok { ScopeId = sessionId; Container = $"session-{sessionId}"; Persist = false }`.
- **`AuthenticatedEphemeralScopeResolver`** ([`StorageScopeResolver.fs:42-54`](../../src/ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs)) — requires an authenticated user; returns `Ok { ScopeId = user.UserId; Container = $"user-{user.UserId}"; Persist = false }`. `Error NotAuthenticated` on missing credentials.
- **`AuthenticatedScopeResolver`** (Individual) ([`StorageScopeResolver.fs:57-69`](../../src/ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs)) — identical to the ephemeral resolver except `Persist = true`.
- **`TeamScopeResolver`** ([`StorageScopeResolver.fs:90-196`](../../src/ToolUp.Platform.Server/Server/Scope/StorageScopeResolver.fs)) — caches active-team pointer per user (5-min sliding TTL). On every request: (1) resolve active team from `ITeamStore.GetActiveTeam` (cache hit usually; line 154–167); on store failure surfaces `Error ScopeResolutionFailed`, not `None`, to avoid collapsing infrastructure errors into "no active team" semantics (line 172–173). (2) Verify membership uncached against `ITeamStore.GetMemberRole` (line 182–195) — defends against concurrent removal. Subscribes to `MembershipChanged` notifications and evicts the active-team cache on `Removed` and `ActiveTeamSet` kinds (line 104, 112-113).

Team-store registration is at [`SDK.Server.fs:1075-1087`](../../src/ToolUp.Platform.Server/Server/SDK.Server.fs):

```fsharp
let teamStoreOpt =
    if PlatformMode.isTeamScoped config.Mode then
        Some(TeamStore(resolvedBlobStorage, resolvedNotificationChannel))
    else None
```

— so only `Team`/`MultiTeam` get an `ITeamStore`. Non-team modes have `teamStoreOpt = None`; every team-CRUD route in `PlatformApiHandler` short-circuits with `Error "Team management not available in this mode"`.

`AccessContext` is composed per-request by a scoped factory at [`SDK.Server.fs:1224-1262`](../../src/ToolUp.Platform.Server/Server/SDK.Server.fs); the `Mode` field on line 1259 is copied verbatim from `config.Mode` (i.e. constant across the deployment's lifetime).

### 1.4 The auth pipeline — three middlewares branching on Mode

Three middlewares in [`Middleware.fs`](../../src/ToolUp.Platform.Server/Server/Middleware.fs):

**`ScopeResolutionMiddleware`** ([`Middleware.fs:27-214`](../../src/ToolUp.Platform.Server/Server/Middleware.fs)) — runs for `/api/*` and `/dev/*` only (line 45-46). Extracts the authenticated user via `IAuthProvider` (line 74-75), calls `IStorageScopeResolver.Resolve` (line 87), stores both in `HttpContext.Items["ToolUp.User"]` and `HttpContext.Items["ToolUp.StorageScope"]` (line 88, 92). Loads module permissions for team scopes (line 177–184), resolves `PlatformRole` for authenticated users (line 195–202). Fires `UserLoggedIn` audit event on first request per session (line 124–130) and the `PendingInvitationHandler` on first request for invited users (line 161–168).

**`AuthEnforcementMiddleware`** ([`Middleware.fs:255-329`](../../src/ToolUp.Platform.Server/Server/Middleware.fs)) — the canonical exemption-set. Carve-outs:

- `requiresAuth = PlatformMode.requiresAuth config.Mode` (line 256) — `false` short-circuits the whole gate; `Anonymous` deployments never enter the rejection path.
- **SSE carve-out** (line 267-270, 296) — `/api/ai/events` and `/api/notifications` are exempt iff `SseAuthMode = QueryParamFallback`. Production deployments running `CookieRequired` route SSE through normal auth (an `IAuthBridge` writes the JWT to `document.cookie` so `EventSource` carries it).
- **CSRF token endpoint** (line 307) — `path = Csrf.TokenPath` is exempt, because the token must be reachable before any credentials exist.
- **Anonymous-route prefixes** (line 277-281) — case-insensitive `StartsWith` against `config.AnonymousRoutePrefixes`.
- **Peer-bearer routes** (line 288-289) — `PeerRouteRegistry.isPeerRoute config.PeerRoutePrefixes path`. `PeerBearerAuthMiddleware` runs ahead of this middleware and either rejects the request or stamps `HttpContext.Items["PeerName"]`.

On all exemptions failing AND `requiresAuth && not authenticated` → 401 JSON (line 314-325).

**`CsrfMiddleware`** ([`CsrfMiddleware.fs:1-150`](../../src/ToolUp.Platform.Server/Server/CsrfMiddleware.fs)) — stateless signed double-submit (DataProtection-sealed, 12-hour lifetime). Exempts the CSRF token endpoint, peer-bearer routes, and anonymous-route prefixes (line 142-143). `requiresValidation` triggers on state-changing methods (POST/PUT/PATCH/DELETE), `/api/` paths, not exempt (line 130-147).

### 1.5 Anonymous-route mechanisms — the existing partial mixed-mode primitives

Three primitives, layered:

**(a) `AnonymousRoutePrefixes` + validator.**

[`AnonymousRoutePrefixValidator`](../../src/ToolUp.Platform.Server/Server/AnonymousRoutePrefixValidator.fs) refuses to start the server when a prefix is dangerously coarse: empty / `/`, missing leading `/`, equals `/api` or `/api/`, fewer than 3 segments, or under 12 chars after `/api/` (line 48-74). The escape hatch is `AcceptShallowAnonymousRoutePrefix = true`. Example legitimate prefix: `/api/forms/public` (3 segments, ~17 chars).

A prefix-matched path bypasses both `AuthEnforcementMiddleware` and `CsrfMiddleware` — i.e. it accepts unauthenticated mutating requests. The deployment relies on the handler itself imposing an authorisation gate (a share token, a captcha, a network boundary, etc.).

**(b) `IPublicFormApi` + `IShareTokenStore` + `PublicEmbed`.**

The Forms companion ships a complete anonymous-reach-into-authenticated-deployment stack:

- [`PublicFormApi.fs`](../../src/ToolUp.Forms.Core/Shared/PublicFormApi.fs) — two methods: `GetSchemaByToken` and `SubmitWithToken`. Identity model: **no `AccessContext`**. Identity is the validated `ShareTokenClaim` (`claim.ScopeId` becomes effective storage scope; `claim.AttributedHandle` becomes `Author = InvitedRespondent`).
- [`PublicFormApiHandler.fs`](../../src/ToolUp.Forms.Server/Server/PublicFormApiHandler.fs) — per-request builder. Validates the share token via `IShareTokenStore.Validate` (no `UsedCount` increment yet), runs the submission, then calls `MarkUsed` only if the submission persisted (line 95-97) — bad submissions don't burn a use.
- [`IShareTokenStore`](../../src/ToolUp.Platform.Server/Server/IShareTokenStore.fs) (54 lines) — five methods: `Issue`, `Validate`, `MarkUsed`, `Revoke`, `ListByResource`. `Validate` is stateless re-read; `MarkUsed` is atomic.
- [`ShareTokenStore.fs`](../../src/ToolUp.Platform.Server/Server/ShareTokenStore.fs) (463 lines) — default `BlobShareTokenStore` implementation. Wire format `{tokenId}.{base64url(payload)}.{base64url(hmac)}`; signed payload carries `{ TokenId, ScopeId, ResourceKind, ResourceId }` (line 117-122). HMAC-SHA256 signing key persisted in `ISecretStore` under `_platform`, auto-generated on first use (line 171-189). Storage layout: `_platform/share-tokens/{scopeId}/{tokenId}.json` plus a resource index at `_platform/share-tokens/{scopeId}/_by-resource/{sha256(kind|id)}/{tokenId}.ref`.
- [`PublicEmbed.fs`](../../src/ToolUp.Forms.Client/Client/PublicEmbed.fs) — Fable client that mounts at `/r/{token}`, boots without an authenticated user, and renders the form against `IPublicFormApi`.

**Net of the three:** `IPublicFormApi` is the only path that today allows anonymous-mode writes to land in *persistent* storage (a `team-{teamId}` container) inside an otherwise-authenticated deployment. The path is gated by an HMAC-signed token, not by user auth.

**(c) `PeerBearerAuthPrefixes` + `PeerRouteRegistry`.**

[`PeerRouteRegistry`](../../src/ToolUp.Platform.Server/Server/PeerRouteRegistry.fs) is the substrate for inter-deployment federation (Phase 37). `PeerBearerAuthMiddleware` validates a bearer-token-shaped peer credential and stamps `HttpContext.Items["PeerName"]`. The user-auth path is irrelevant for these routes — the peer name IS the identity. Federation-shaped consumers use this for site↔site delegated authority.

The three primitives are **decoupled**: a deployment can use any combination. They are *not* unified under a single concept; each is a distinct middleware exemption.

### 1.6 Persistence routing — `Persist` is the lever

The whole persistence story turns on `StorageScope.Persist`:

- `Persist = false` (`Anonymous`, `AuthenticatedEphemeral`) → in-memory only. `SessionFileStore` keeps files in-process; a background timer evicts them after `storeEvictionMinutes` (default 60). No `IBlobStorage` writes.
- `Persist = true` (`Individual`, `Team`, `MultiTeam`) → writes flow through `IDataObjectStore` (default `LocalFileStorage` on disk under `data/`); `loadPersistedFiles()` reloads them on restart.

Today, the **only** path that produces a `Persist = true` scope inside an otherwise-anonymous-reachable request is the share-token-gated `PublicFormApiHandler` flow — and it does so by computing the scope from the validated `claim.ScopeId`, not from `StorageScope` resolution. The normal request path produces a single deployment-wide persistence shape.

### 1.7 Client-side flow

Mode flows through the client via [`UserSession.fs`](../../src/ToolUp.Platform.Client/Client/UserSession.fs) (350 lines):

- `UserSession.configure mode` is called once in `Client.run` (line 42). After that, `getMode()` returns the constant.
- Storage selection (line 157): `Anonymous` → `sessionStorage` (per-tab, lost on tab close); all others → `localStorage` (persistent across tabs / sessions).
- Identity header pair (line 315-323): `Anonymous` attaches `X-User-Id`; authenticated modes attach `Authorization: Bearer <JWT>` (falling back to `X-User-Id` until a token is available).

[`AuthUIProvider.fs`](../../src/ToolUp.Platform.Client/Client/AuthUIProvider.fs) branches on Mode to decide which sign-in UI component to mount; [`SDK.Client.fs`](../../src/ToolUp.Platform.Client/Client/SDK.Client.fs) (1915 lines) reads Mode in composition-time decisions about sidebar group visibility (team-manager UI, etc.) and in the bootstrap `init` choice between session-id vs token-derived identity.

`ClientModuleContext.Mode` ([`ConfigTypes.fs:118`](../../src/ToolUp.Platform.Core/Shared/Types/ConfigTypes.fs)) is the per-module read of Mode, available to every module's `init`/`update`.

### 1.8 Validators that branch on Mode

Forge's `IConfigValidator` substrate runs preflight checks at startup. The Mode-aware ones:

| Validator | Default severity | Refusal condition | Escape hatch |
|---|---|---|---|
| `AnonymousRoutePrefixValidator` | Error | Prefix < 3 segments or < 12 chars after `/api/` | `AcceptShallowAnonymousRoutePrefix = true` |
| `HeaderAuthProviderModeValidator` | Error | `HeaderAuthProvider` + auth-requiring Mode | `AcceptHeaderAuthInAuthenticatedMode = true` |
| `RateLimitModeValidator` | Warning | Auth-requiring + HTTPS, no `RateLimit` | `AcceptNoRateLimitInAuthenticatedMode = true` |
| `AuditLogModeValidator` | Warning | Auth-requiring + `NoAuditLog` | (none — dev/demo legitimate) |
| `SseAuthModeValidator` | Warning | Auth-requiring + `QueryParamFallback` | (production: switch to `CookieRequired`) |
| `EncryptedSecretStoreModeValidator` | Error | Plaintext-secrets wrapper + auth-requiring Mode without master key | `AcceptPlaintextSecretsInAuthenticatedMode = true` |
| `OidcAudienceBindingValidator` | Error | OIDC + auth-requiring Mode without `TOOLUP_OIDC_AUDIENCE` | (none — must set audience) |
| `MaxRequestBodyBytesValidator` | Warning | Auth-requiring + HTTPS + body cap > 50 MB + no rate limit | (none) |
| `SecurityHeadersValidator` | Warning | Auth-requiring without basic security headers | (none — set CSP/HSTS/X-Frame-Options) |
| `AutoBootstrapDevAdminModeValidator` | Error | `AutoBootstrapDevAdmin` set in auth-requiring Mode | (explicit waiver) |
| `ForwardedHeadersTrustValidator` | Error | OIDC without `TrustForwardedHeaders` | (none — must trust forwarded headers) |
| `TeamCreationPolicyValidator` | Warning/Error | Non-team Mode + non-`Disabled` team-creation policy | (must set to `Disabled`) |

Every Error-severity validator carries an explicit operator-override flag. The pattern is "default-strict, escape-by-name" — there is no "force start anyway" global switch. Each escape hatch is a separate `ServerConfig` field with a documenting comment.

### 1.9 Mode-aware modules

Three categories of code branch on `Mode` *outside* the middleware and validator layers:

**(a) Team-CRUD gates.** [`PlatformApiHandler.fs`](../../src/ToolUp.Platform.Server/Server/PlatformApiHandler.fs) guards `CreateTeam`, `AddTeamMember`, `RemoveTeamMember`, `SetActiveTeam`, `SetMemberRole`, `DeleteTeam`, `GetTeams`, `GetMembers` with `if teamStoreOpt.IsNone then Error "Team management not available in this mode"` (per-method, line 214, 246, 279, 310, 344, 386, 414, 449). The onboarding endpoint (line 472–499) constructs an `AccessContext` and branches: if Team/MultiTeam without an active team, returns a different schema (line 485-486); if `Anonymous`, every Managed module is hidden from onboarding (line 497).

**(b) AI write gates.** [`AISettingsHandler.fs:138-159`](../../src/ToolUp.AI.Server/Server/AISettingsHandler.fs) branches on `accessContext.Mode`: Team/MultiTeam writes require `TeamId` + Owner/Admin role; other modes are unrestricted; the `Anonymous` short-circuit at line 165-169 returns "AI configuration is not available in this mode" because `accessContext` resolves no config scope (`configScope = None`).

**(c) Diagnostic / health surfaces.** `DevDiagnosticsHandler`, `DiagnosticBundleHandler`, `ServiceStatusBoardApiHandler`, `HealthMonitorApiHandler`, `UsageQueryApi` all report Mode info; some restrict access based on `PlatformRole.PlatformAdmin` (which itself is only resolvable in authenticated modes).

**(d) AI in Anonymous mode.** No hard refusal. The `Anonymous`-mode caveat documented in [`surfaces.md` "Pure-Anonymous public portal"](../platform/surfaces.md#pure-anonymous-public-portal) is operator-policy, not enforced — `AIServerApp.run` accepts a deployment with `Surfaces = Surfaces.anonymous`. The deployment owns the cost-control responsibility.

### 1.10 Consumer snapshot

A survey across known downstream consumers, reported here by deployment archetype rather than name:

**Pure-Individual internal-tools deployment** — `Mode = Individual` resolved via `ServerConfig.fromEnv` (env-supplied). Multiple commercial modules. No `AnonymousRoutePrefixes`, no `PeerBearerAuthPrefixes`, no `IPublicFormApi`, no `IShareTokenStore`. Pure single-mode. No consumer-side branches on `Mode` beyond the declaration.

**Federation deployment (two-app pair)** — `Mode = Individual` on each side, plus three localhost-testing waivers (`AcceptHeaderAuthInAuthenticatedMode`, `AcceptPlaintextSecretsInAuthenticatedMode`, `AcceptQueryParamSseAuthInAuthenticatedMode`). Peer routes registered separately via `RegisteredPeer` list. A handful of modules per side. No anonymous surfaces; federation is peer-bearer, orthogonal to Mode.

**Pure-Anonymous public portal** — `Mode = Anonymous`. Single module. Minimal composition; no auth, no AI, no DataManager. The whole app is publicly accessible by design.

**Public-utility-with-admin (currently using a workaround)** — `Mode = Individual` in code, but the consumer file comment explicitly documents this as a **workaround**: the app was designed as a public utility (Anonymous mode) but the SDK's Anonymous-mode accessibility filter hides all user modules from the sidebar, so v0 ships as Individual with `DevDefaultUserId = Some "dev-admin"` pending an SDK fix. Multiple calculator modules. This is the clearest in-tree case of an app that genuinely wants `Anonymous` for public surfaces *and* a small private admin surface, and is currently mis-served.

**Template-scaffolded apps** — apps scaffolded from the SDK templates are independently-redeployable; each declares its own Mode at composition time. Aggregate is not load-bearing for the migration plan because each is independently re-scaffoldable.

**Cross-consumer summary.** Across known downstream consumers and the four declared modes used in code:
- `Anonymous` — 1 deployment in production use; 1 deployment wants it but uses a workaround (the public-utility-with-admin shape above).
- `Individual` — every other in-tree deployment, including the public-utility-with-admin shape currently mis-served by Individual mode.
- `AuthenticatedEphemeral` — 0 consumers.
- `Team` — 0 consumers.
- `MultiTeam` — 0 consumers.

**One consumer has a load-bearing mixed-mode requirement today** (public calculator + private admin) and is achieving it via Individual-mode-with-dev-admin. The remaining pure-Individual consumers (internal-tools deployment, federation pair) would migrate trivially. The pure-Anonymous portal is similarly trivial. **No consumer uses Team / MultiTeam / AuthenticatedEphemeral in production**, which means the migration plan does not have to defend a heavily-used branch of the existing model — only `Anonymous` and `Individual` carry real load.

### 1.11 What's already half-supporting mixed-mode (synthesis)

Pulling threads together, forge already carries the following primitives that point toward mixed-mode without unifying under one concept:

1. **Per-prefix middleware exemption.** `AnonymousRoutePrefixes` and `PeerRoutePrefixes` are independent prefix-match exemptions on `AuthEnforcementMiddleware` and `CsrfMiddleware`. They prove the architecture can express "this part of the deployment uses different auth", but each is a *distinct* exemption type with no shared abstraction.
2. **Identity-by-claim instead of by-user.** The share-token path establishes a precedent for routing requests through a `ShareTokenClaim` instead of an `AuthenticatedUser`. The result is a scope derived from claim metadata, not from the auth subject. This is the architectural piece a mixed-mode model needs.
3. **Stateless scope resolution as a contract.** `IStorageScopeResolver` is already an interface taking a pure `ScopeResolutionRequest` and returning `Result<StorageScope, ScopeResolutionError>`. Today it's selected once at composition time, but the contract supports per-request dispatch.
4. **`AccessContext` carries Mode as a value, not as a singleton.** Every Mode-aware handler reads `accessContext.Mode`, not a static. If `Mode` is replaced by a richer per-request shape, the handler-side change is mechanical.
5. **`StorageScope.Persist` as the persistence lever.** The flag is already the single decision point. A mixed-mode deployment that runs an ephemeral resolver for some requests and a persistent resolver for others already has the storage substrate in place.
6. **Default-strict validators with named escape hatches.** The pattern for opting into looser configurations (`AcceptHeaderAuthInAuthenticatedMode`, etc.) is established and replicable.

### 1.12 What's load-bearing for the redesign — invariants the new model must preserve

Drawn from the same survey:

1. **The persistence lever is per-request, not per-deployment.** `StorageScope.Persist` already varies per request (the share-token path produces `Persist = true` even in an `Anonymous`-shaped deployment). The new model can — and must — formalise this.
2. **`Team` and `MultiTeam` are server-equivalent.** The split is client-UX-only. A multi-axis design can collapse the storage-side distinction.
3. **Anonymous-mode handlers operate without an `AccessContext` in some places** (share-token path) **and with an `AccessContext` in others** (normal anonymous requests get an `AccessContext` with `UserId = "anonymous"`). This inconsistency is a soft spot — the new model needs to pick one.
4. **The `PlatformMode.requiresAuth` / `isTeamScoped` helpers are read in dozens of places**. Replacing them is largely mechanical, but the new model must offer equivalent one-call predicates so consumer code doesn't grow conditional logic.
5. **The five `PlatformMode` values are not orthogonal.** `Team`/`MultiTeam` share server-side data model; `Individual`/`AuthenticatedEphemeral` share auth shape but differ in persistence; `Anonymous` is the odd one out. The 5-case enum hides 3 actually-independent axes (Identity / Persistence / Teamship — or some refinement thereof).
6. **Default-deny + named escape hatch is the established posture.** Every loosening of a security default has an explicitly-named opt-in flag. The new model should not introduce silent loosening as part of richer expressiveness.
7. **Single-mode authoring must remain one-line.** Today a consumer writes `Mode = Individual` and gets a fully-coherent deployment. Anything richer must offer the same shorthand for the common case (Phase 2 design constraint).
8. **The middleware pipeline order is fixed** (`ScopeResolution` → `AuthEnforcement` → `Csrf` → handler). The new model should slot in at the resolution layer; the enforcement and CSRF layers should largely fall out from the resolved shape.

---

## Phase 2 — Proposed abstraction

This section pressure-tests the three-axis starting sketch (`Identity × Persistence × Teamship`), explains why a **Subject-first** re-pivot is better, and proposes the replacement types. Open questions surfaced inline.

### 2.1 Pressure-testing the three-axis sketch

The sketch frames the problem as three orthogonal axes:

```fsharp
type Identity = NoIdentity | UserIdentity | TeamIdentity
type Persistence = Ephemeral | Persistent
type Teamship = NoTeamContext | SingleTeamFixed | MultiTeamSwitchable
```

Pressure-testing reveals three problems:

**Problem 1 — The axes are under-constrained: many of the 3×2×3 = 18 combinations are nonsense.**

- `Identity = NoIdentity` + `Persistence = Persistent` only makes sense via a share-token-style claim. The "anonymous reaches persistent storage" path exists today (Phase 1 §1.5 — `IPublicFormApi`), but the axes don't carry the claim that makes it safe. The combination is meaningful, but only when a *fourth* concept (the claim) is present.
- `Identity = UserIdentity` (defined in the sketch as "authenticated, NOT in team context") + `Teamship = SingleTeamFixed` is self-contradictory.
- `Identity = TeamIdentity` + `Teamship = NoTeamContext` is similarly contradictory.

A type-system-level guarantee that impossible states are unrepresentable is a forge value (Elmish purity, `Result` over exceptions, etc.). The three-axis sketch fails it.

**Problem 2 — Persistence isn't actually per-request; it's per-shape.**

The current `StorageScope.Persist` flag varies per-request, but its value is *determined by* which shape (Mode) is in force. An anonymous-session resolver always emits `Persist = false`; a team-scope resolver always emits `Persist = true`. There is no shape today for which Persist genuinely flips request-to-request.

The mixed-mode goal isn't to let one *shape* be both ephemeral and persistent. It's to let one *deployment* host multiple shapes with different persistence policies. So persistence belongs **inside** the shape declaration, not as a free orthogonal axis.

**Problem 3 — The `Teamship` axis conflates a deployment property and a UX property.**

`SingleTeamFixed` vs `MultiTeamSwitchable` is purely a client-UX distinction (Phase 1 §1.1 — the server-side data model is identical). Promoting it to a top-level axis exaggerates its weight; it belongs as a sub-field of the team-shape config.

Meanwhile the *real* axis hiding in `Teamship` is "does this deployment have team scopes at all?". That's better expressed as "is `Team` one of the shapes the deployment supports?" — a presence-in-set question, not a tri-state axis.

### 2.2 The Subject-first re-pivot

The replacement model takes a different starting point: **the per-request question is "who/what is this request acting as?"**. The answer to that question (a `Subject` value) determines storage scope, persistence policy, audit attribution, and what the request is allowed to do. The deployment-level question becomes "which subjects can my deployment legally serve?".

The shift unlocks four things at once:

1. **Impossible states become unrepresentable.** A `Subject` value is one of a fixed set of constructors; each constructor carries its own coherent data. There's no nonsense "user without identity but with persistent storage" combination.
2. **Persistence becomes a shape property, not a free axis.** Each shape declaration in the deployment carries its persistence policy. Adding a new shape (e.g. `ClaimBearer`) means adding a new constructor with its own policy.
3. **The share-token / anonymous-with-claim path becomes first-class.** It's a fourth subject kind, not an exception to the auth pipeline.
4. **The team-switcher UX flag becomes a field on `TeamConfig`**, not a top-level axis — its right semantic weight.

### 2.3 `Subject` — the per-request identity type

Proposed:

```fsharp
/// Who/what a request is acting AS. Resolved per-request by the
/// scope-resolution middleware from auth state + deployment config.
/// The Subject is the load-bearing pivot: storage scope, persistence,
/// permissions, and audit attribution all derive from it.
type Subject =
    /// Unauthenticated session-scoped subject. `SessionId` is the
    /// X-User-Id cookie value (or a freshly-generated GUID).
    | AnonymousSession of sessionId: string
    /// Authenticated user without active team scope. The "Individual"
    /// or "trial" shape.
    | AuthenticatedUser of userId: string
    /// Authenticated user acting within a team scope. The Team /
    /// MultiTeam shape collapses here — the difference is UX-only.
    | TeamMember of userId: string * teamId: string
    /// Anonymous reach into a persistent scope, gated by a validated
    /// share-token claim. The claim defines both identity (via
    /// `AttributedHandle`) and authority bounds (via `ResourceKind` /
    /// `ResourceId` / `UseLimit`).
    | ClaimBearer of claim: ShareTokenClaim
```

A lightweight kind tag for declarative use (surface requirements, capability checks):

```fsharp
type SubjectKind =
    | AnonymousKind
    | UserKind
    | TeamMemberKind
    | ClaimBearerKind

module Subject =
    let kind = function
        | AnonymousSession _ -> AnonymousKind
        | AuthenticatedUser _ -> UserKind
        | TeamMember _ -> TeamMemberKind
        | ClaimBearer _ -> ClaimBearerKind
```

**Why this shape (and not other candidates):**

- *Why not `AuthenticatedUser of userId * teamId: string option`?* Collapsing `AuthenticatedUser` and `TeamMember` into one constructor with optional team makes pattern matches sloppy — handlers that genuinely require team scope have to re-check the option. The 4-case DU lets the compiler enforce "this handler requires team scope" at the match site.
- *Why include `ClaimBearer` as a Subject and not as a separate auth-scheme primitive?* Share-token-gated access produces a `StorageScope`, an audit-attributable identity (the `AttributedHandle`), and a bounded permission envelope — exactly what other Subjects produce. Modelling it as a first-class Subject means the same downstream code (scope resolution, audit, permissions) works uniformly; no parallel pipeline for claim-bearer flows.
- *Why `userId * teamId` as fields on `TeamMember`?* They're both load-bearing. The handler needs the user (for permission lookup, audit, AI-conversation ownership) and the team (for storage scope). Carrying both pre-resolved avoids re-reading `AccessContext` to find one or the other.

**The storage scope falls out:**

```fsharp
module Subject =
    let storageScope (persistencePolicy: PersistencePolicy) = function
        | AnonymousSession sid ->
            { ScopeId = sid; Container = $"session-{sid}"; Persist = persistencePolicy.Anonymous = Persistent }
        | AuthenticatedUser uid ->
            { ScopeId = uid; Container = $"user-{uid}"; Persist = persistencePolicy.User = Persistent }
        | TeamMember (_, tid) ->
            { ScopeId = tid; Container = $"team-{tid}"; Persist = persistencePolicy.Team = Persistent }
        | ClaimBearer claim ->
            { ScopeId = claim.ScopeId; Container = claim.ScopeId; Persist = true }  // claim → always persistent
```

Note `ClaimBearer.Container = claim.ScopeId` directly — the claim carries the resolved container (matching today's `BlobShareTokenStore` semantics, [Phase 1 §1.5](#15-anonymous-route-mechanisms--the-existing-partial-mixed-mode-primitives)).

### 2.4 `SurfaceProfile` — the deployment-level shape declaration

A deployment declares a non-empty *list* of supported surface profiles. Each profile is a closed-DU constructor carrying that profile's specific knobs:

```fsharp
type Persistence = Ephemeral | Persistent

type TeamSwitchingUX =
    /// One team per user; no header switcher in the client shell.
    | NoSwitcher
    /// Users belong to many teams and switch in-session; the shell
    /// renders the header dropdown and runs `TeamSwitched` reset.
    | HeaderSwitcher

type AnonymousConfig = {
    /// Ephemeral by definition for the normal anonymous flow.
    /// ClaimBearer is the path to persistent anonymous access.
    SessionEvictionMinutes: int option   // default Some 60
}

type AuthenticatedUserConfig = {
    Persistence: Persistence
}

type TeamConfig = {
    /// Whether persistence is on for the team scope. Almost always
    /// Persistent; Ephemeral exists for trial-team scenarios.
    Persistence: Persistence
    /// Team-switcher UX. Server-side identical.
    Switching: TeamSwitchingUX
}

type ClaimBearerConfig = {
    /// Wires `IShareTokenStore` substrate. Future fields: per-token
    /// rate-limit policy, default lifetimes, audit retention.
    DefaultLifetimeDays: int   // default 30
    DefaultUseLimit: int option   // default Some 1
}

/// One supported shape in the deployment's surface list.
type SurfaceProfile =
    | Anonymous of AnonymousConfig
    | AuthenticatedUser of AuthenticatedUserConfig
    | Team of TeamConfig
    | ClaimBearer of ClaimBearerConfig
```

The deployment declares its supported surfaces as a list field on `ServerConfig`:

```fsharp
type ServerConfig = {
    // …all the existing fields except `Mode: PlatformMode`…

    /// Non-empty list of subject shapes this deployment supports.
    /// Each entry wires its own substrate (e.g. `Team _` registers
    /// `ITeamStore`; `ClaimBearer _` registers `IShareTokenStore`).
    /// Validated at startup — at least one entry; no duplicate
    /// constructors.
    Surfaces: SurfaceProfile list
}
```

**Why a list, not a set:** declaration order is meaningful when the SDK later constructs convenience surfaces (the auth-provider UI may want to lead with `Team` over `Individual` if both are present; the public landing-page renderer reads the first entry that has `Anonymous`).

**Why not a record of `option`s** (`{ Anonymous: AnonymousConfig option; Team: TeamConfig option; … }`)?

The option-record form is easier to read at a glance ("is this deployment public? `Anonymous = Some _`") but worse to *author* — every config requires the user to know about the four fields they're explicitly opting out of. List form keeps the unused shapes out of sight.

**Single-shape and mixed-shape are uniform:** a single-shape deployment is just a one-element list; mixed-mode is two or more elements. There is no "first-class single-mode" vs "second-class mixed-mode" — they're the same shape, the same validators, the same middleware.

**Three follow-on validations** that fall out of the list shape (Phase 3 § ⇒ `Compose/`):

- `Surfaces` is non-empty (deployment serves some subject).
- No duplicate constructors (you can't have two `Team` entries with different configs — pick one).
- `Team _` and `Anonymous _` may coexist; `AuthenticatedUser _` and `Team _` may coexist (freemium pattern: free users are `AuthenticatedUser`, paid users on teams are `TeamMember`).

### 2.5 `SurfaceRequirement` — the per-route gate

A `SurfaceRequirement` declares which Subject kinds are allowed to reach a route. It's a set:

```fsharp
type SurfaceRequirement = {
    /// Which subject kinds may invoke this surface. The middleware
    /// rejects subjects whose `kind` is not in this set.
    AcceptedSubjects: Set<SubjectKind>
}

module SurfaceRequirement =
    /// Everyone can reach this surface — anonymous, users, team
    /// members, claim bearers. Use for landing pages, public health
    /// endpoints, etc.
    let public_ = {
        AcceptedSubjects =
            Set.ofList [AnonymousKind; UserKind; TeamMemberKind; ClaimBearerKind]
    }

    /// Authenticated subjects only — rejects anonymous sessions but
    /// accepts users, team members, and claim bearers (the bearer of
    /// a share token is treated as authenticated for the bounded
    /// scope the claim grants).
    let authenticated = {
        AcceptedSubjects =
            Set.ofList [UserKind; TeamMemberKind; ClaimBearerKind]
    }

    /// User-or-team-member only — rejects anonymous AND claim
    /// bearers. Use for surfaces that require a real auth session
    /// (sign-in lifecycle, sensitive admin actions).
    let userOrTeam = {
        AcceptedSubjects = Set.ofList [UserKind; TeamMemberKind]
    }

    /// Team-scoped only — must have an active team. Use for team-
    /// CRUD, team-data, team-config endpoints.
    let teamScoped = { AcceptedSubjects = Set.ofList [TeamMemberKind] }

    /// Anonymous sessions only. Use for sign-up flows that should
    /// 302 authenticated users away.
    let anonymousOnly = { AcceptedSubjects = Set.ofList [AnonymousKind] }

    /// Claim-bearer only. Use for share-token-gated submit endpoints
    /// where a user landing without a token should be rejected.
    let claimBearerOnly = { AcceptedSubjects = Set.ofList [ClaimBearerKind] }
```

**Set-shaped over closed-DU**. The original sketch's five `SurfaceRequirement` cases (`AnonymousOnly | AuthenticatedAnyShape | IndividualOnly | TeamRequired | AnyShape`) are subsets of the same set of subject kinds. Modelling them as named subsets is more flexible (consumers can compose their own — e.g. `userOrClaimBearer`) and more inspectable (the SDK can render the set in error messages). The named module helpers cover the common cases by hand so consumers don't have to know the set construction.

**Why is `ClaimBearer` in the `authenticated` set?** A share-token claim is a *delegated authentication* — the original issuer (a team admin) granted the bearer bounded authority. From the surface's perspective, a bearer is authenticated for that bounded scope. Surfaces that don't want claim-bearers (e.g. team-admin CRUD) use `userOrTeam` instead, which excludes them.

**Module-level default + per-endpoint override.** Modules declare a `DefaultSurfaceRequirement`; endpoints within the module can override. The safe default for modules that don't declare one is `userOrTeam` (least permissive that still serves authenticated apps).

```fsharp
type ServerModule = {
    Name: string
    DefaultSurfaceRequirement: SurfaceRequirement
    Endpoints: EndpointDescriptor list
}

and EndpointDescriptor = {
    Path: string
    Method: HttpMethod option
    SurfaceRequirement: SurfaceRequirement option   // None = use module default
}
```

### 2.6 The single-line authoring story

The new model's mandate (§Constraint 2 / 6) is **single-mode authoring must stay a one-liner** while mixed-mode gets a clean idiom too. Helpers in a `Surfaces` module make this work:

```fsharp
module SurfaceProfile =
    /// AnonymousConfig with default eviction window.
    let anonymous = Anonymous { SessionEvictionMinutes = Some 60 }
    /// AuthenticatedUser, ephemeral storage (= old AuthenticatedEphemeral).
    let trial = AuthenticatedUser { Persistence = Ephemeral }
    /// AuthenticatedUser, persistent storage (= old Individual).
    let individual = AuthenticatedUser { Persistence = Persistent }
    /// Team scope, no switcher (= old Team).
    let team = Team { Persistence = Persistent; Switching = NoSwitcher }
    /// Team scope, header switcher (= old MultiTeam).
    let multiTeam = Team { Persistence = Persistent; Switching = HeaderSwitcher }
    /// Share-token claim bearer with default lifetime + use-limit.
    let claimBearer =
        ClaimBearer { DefaultLifetimeDays = 30; DefaultUseLimit = Some 1 }

module Surfaces =
    /// Single-shape deployment (the common case).
    let anonymous = [SurfaceProfile.anonymous]
    let trial = [SurfaceProfile.trial]
    let individual = [SurfaceProfile.individual]
    let team = [SurfaceProfile.team]
    let multiTeam = [SurfaceProfile.multiTeam]

    /// Common mixed-mode pairings.
    let anonymousAndIndividual =
        [SurfaceProfile.anonymous; SurfaceProfile.individual]
    let anonymousAndTeam =
        [SurfaceProfile.anonymous; SurfaceProfile.team]
    let teamWithShareTokens =
        [SurfaceProfile.team; SurfaceProfile.claimBearer]
```

**Authoring examples:**

*Pure-Individual (internal-tools shape) — one line:*

```fsharp
{ ServerConfig.defaults with Surfaces = Surfaces.individual }
```

*Pure-Anonymous (public-portal shape) — one line:*

```fsharp
{ ServerConfig.defaults with Surfaces = Surfaces.anonymous }
```

*Mixed Anonymous + Individual (public-utility-with-admin shape) — one line:*

```fsharp
{ ServerConfig.defaults with Surfaces = Surfaces.anonymousAndIndividual }
```

*Multi-tenant SaaS with public landing + paid team dashboards + survey share-tokens — three-line block:*

```fsharp
{ ServerConfig.defaults with
    Surfaces = [
        SurfaceProfile.anonymous
        SurfaceProfile.multiTeam
        SurfaceProfile.claimBearer
    ] }
```

Single-mode is still one line. Mixed-mode is N lines where N matches the number of shapes, which is honest about what the deployment is doing.

### 2.7 `AccessContext` under the new model

The replacement field is `Subject`, not `Mode`. Two helper accessors maintain consumer-side ergonomics:

```fsharp
type AccessContext = {
    /// Stable identity string. For anonymous, the session id. For
    /// user / team-member, the user id. For claim-bearer, the
    /// `AttributedHandle` if set, else the issuer's id (kept stable
    /// across the claim's lifetime).
    UserId: string
    /// Active team, when the subject is in a team scope.
    TeamId: string option
    /// Per-request resolved subject. Replaces the old `Mode` field.
    Subject: Subject
    ModulePermissions: Map<string, ModulePermission list>
    PlatformRole: PlatformRole option
}

module AccessContext =
    /// True when the subject is `AnonymousSession`.
    let isAnonymous (ctx: AccessContext) =
        match ctx.Subject with AnonymousSession _ -> true | _ -> false

    /// True when the subject is authenticated (User | TeamMember | ClaimBearer).
    let isAuthenticated ctx = not (isAnonymous ctx)

    /// True when the subject is `TeamMember`.
    let inTeamScope (ctx: AccessContext) =
        match ctx.Subject with TeamMember _ -> true | _ -> false

    /// True when the subject is `ClaimBearer`.
    let isClaimBearer (ctx: AccessContext) =
        match ctx.Subject with ClaimBearer _ -> true | _ -> false

    /// Convenience — for migration of old `Mode`-pattern code. Returns
    /// a stable string label per subject kind ("anonymous", "user",
    /// "team", "claim-bearer") suitable for log / metrics tagging.
    let kindLabel ctx =
        match Subject.kind ctx.Subject with
        | AnonymousKind -> "anonymous"
        | UserKind -> "user"
        | TeamMemberKind -> "team"
        | ClaimBearerKind -> "claim-bearer"
```

Most existing handler code that branches on `ctx.Mode` mechanically rewrites to a match on `ctx.Subject` — see Phase 5 migration plan.

### 2.8 What this subsumes (and what stays separate)

| Existing primitive | Subsumed by new model? | How |
|---|---|---|
| `PlatformMode` enum | **Replaced wholesale.** | Five values map to `SurfaceProfile` constructors (one-to-many for `Team`/`MultiTeam`). |
| `AnonymousRoutePrefixes` (+ validator + escape hatch) | **Subsumed.** | Routes declare `SurfaceRequirement` instead of opting into a string-prefix-match exemption. Prefix list, validator, and `AcceptShallowAnonymousRoutePrefix` flag all retire. |
| `PeerBearerAuthPrefixes` / `PeerRouteRegistry` / `PeerBearerAuthMiddleware` | **Stays separate.** | Peer-bearer auth is machine-to-machine federation, a different auth scheme. Modelling it as a fifth subject kind (`PeerKind`) was considered and rejected — peer requests are not "a subject acting on its own behalf", they're another deployment delegating. The cost of unifying outweighs the small symmetry gain. Federation deployments continue to use the existing peer route + bearer infrastructure unchanged. |
| `IPublicFormApi` (the contract) | **Stays.** | The Forms public-form API itself is a Forms concern. Its position as "an exception to the auth pipeline" goes away — the API's routes simply declare `SurfaceRequirement.claimBearerOnly`. |
| `IShareTokenStore` (the substrate) | **Stays — promoted.** | Today: optional opt-in via `ShareTokenStore = EnabledShareTokenStore`. New model: presence of `ClaimBearer _` in `Surfaces` wires the substrate. The interface, blob layout, HMAC scheme — all unchanged. |
| `PublicEmbed` (the Fable component) | **Stays unchanged.** | Mounts at `/r/{token}`, boots without an authenticated user, hits routes that declare `SurfaceRequirement.claimBearerOnly`. The component code doesn't change. |
| `SseAuthMode` (`QueryParamFallback` / `CookieRequired`) | **Stays.** | Orthogonal to surface profile — describes how SSE handshakes prove identity. Validator stays; the "warn in authenticated Mode" wording becomes "warn when any non-Anonymous surface is supported". |
| Per-mode validators (12 in Phase 1 §1.8) | **Rewritten mechanically.** | Each validator's `PlatformMode.requiresAuth` / `isTeamScoped` predicate becomes a check on `Surfaces`. Severity + escape hatch flags stay. See Phase 3 § "Validators". |
| `AccessContext.Mode` | **Replaced by `Subject`.** | All consumers updated. |
| `PlatformMode.requiresAuth` / `isTeamScoped` helpers | **Replaced.** | New helpers on `ServerConfig` (e.g. `DeploymentConfig.requiresAnyAuth`, `supportsAnonymous`, `hasTeamScope`) read the `Surfaces` list. Existing call sites rewrite to these helpers. |
| `ClientConfig.Mode` | **Replaced by `Surfaces`.** | Same shape on the client side. The client's `UserSession.configure` switches storage policy per Subject (sessionStorage for `AnonymousSession`, localStorage otherwise) the same way it does today — driven by per-request Subject, not deployment-wide Mode. |

### 2.9 Naming choices and alternatives weighed

| Concept | Proposed | Rejected | Why |
|---|---|---|---|
| Per-request shape | `Subject` | `EffectiveShape`, `Identity`, `Principal` | `Subject` is the canonical security-terminology word for "who's acting"; `Principal` is .NET-specific and means something narrower; `EffectiveShape` is vague. |
| Deployment-level supported set | `Surfaces` | `Capabilities`, `Modes`, `SupportedShapes` | `Surfaces` reads naturally in code (`Surfaces = Surfaces.individual`); `Modes` resurrects the old vocabulary and implies one-pick; `Capabilities` is a record-of-bools association we deliberately moved away from. |
| Per-shape config carrier | `SurfaceProfile` | `Mode`, `Shape`, `Configuration` | `SurfaceProfile` distinguishes from `SurfaceRequirement` (route-level) and reads as "a profile for one supported surface"; `Shape` collides with the per-request `Subject`. |
| Per-route gate | `SurfaceRequirement` | `AuthRequirement`, `RouteRequirement`, `RouteAccess` | Pairs naturally with `SurfaceProfile`; `AuthRequirement` understates (surfaces require shape, not just auth presence). |
| Anonymous→authenticated promotion | `AnonymousSession` | `Anonymous`, `NoUser` | `AnonymousSession` highlights that the session is the carrier; `Anonymous` collides with the surface profile constructor name. |
| Trial-mode shape | `AuthenticatedUser { Persistence = Ephemeral }` | `Trial`, `AuthenticatedEphemeral` | Carrying Persistence as a field on `AuthenticatedUserConfig` avoids creating two near-identical constructors; the `SurfaceProfile.trial` helper preserves the old name where convenience demands it. |
| Team UX flag | `TeamSwitchingUX = NoSwitcher | HeaderSwitcher` | reuse `PlatformMode.Team`/`MultiTeam` distinction at this layer | A two-case sub-DU on `TeamConfig` is clearer than smuggling the UX flag into a parent type's constructor. |

### 2.10 Open questions for operator review

These are decisions I'm deliberately surfacing rather than picking:

1. **Should `Anonymous` profiles support `Persistence = Persistent`?**
   I've defaulted to `Ephemeral` only for `AnonymousConfig` on the basis that persistent-anonymous access is what `ClaimBearer` exists for. But a deployment could conceivably want session-scoped *persistent* storage — long-lived anonymous demos that survive tab close. If the use case is real, `AnonymousConfig` grows a `Persistence` field. If not, the constraint stays tight and the `ClaimBearer` path is the only way to combine "anonymous" with "persistent".

2. **Where does `AccessContext.UserId` get its value when `Subject = ClaimBearer`?**
   Three options: (a) `claim.AttributedHandle` when set, else `claim.IssuedBy` (today's `IPublicFormApi` shape); (b) a synthetic `"claim:" + tokenId` (no leak of issuer or recipient); (c) make `AccessContext.UserId` an option type. Option (a) preserves audit attribution semantics, but couples the field meaning to the claim's contents; (c) is cleanest but ripples through every downstream consumer reading `UserId`. I lean (a); flagging for confirmation.

3. **Should peer-bearer routes be modelled as a fifth subject kind (`PeerKind`)?**
   Subsumption argument: it would unify all auth schemes under one type and let federation routes declare `SurfaceRequirement` symmetrically. Counter-argument: peer requests are *delegated* (one deployment acting on behalf of another), not "a subject acting on its own behalf"; conflating them obscures rather than clarifies. I've kept peer-auth as a separate concern. Flagging in case the operator wants the symmetry.

4. **Should `SurfaceProfile.ClaimBearer` carry the share-token-store backend choice?**
   Today `ShareTokenStore: ShareTokenStoreMode` is a separate `ServerConfig` field that names the backend (default `BlobShareTokenStore`). I've kept it separate (mechanical migration). Alternative: roll it into `ClaimBearerConfig` so all share-token knobs sit together. Cleaner-looking but means an existing distinct concern is now expressed in two places (the substrate field + the surface presence). I lean keep-separate.

5. **Auto-promotion from `AnonymousSession` to `AuthenticatedUser` mid-session.**
   Today: anonymous users use `sessionStorage`; signing in switches to `localStorage` and the SDK starts attaching `Authorization: Bearer`. The data in sessionStorage is naturally discarded. Question: should the new model offer a *hook* for deployments that want to migrate anonymous-session data (e.g. a guest-built form draft) into the user's account on sign-in? Today this is hand-rolled per app. I haven't designed it into Phase 2; flagging as a possible Phase 3 subsystem addition (the design extends cleanly — `IAnonymousSessionMigrator` interface invoked by middleware on first authenticated request following an anonymous session).

6. **Surface-requirement default for handlers that don't declare one.**
   I've proposed `userOrTeam` as the safest default ("don't accidentally expose a surface to anonymous"). Alternative: `authenticated` (admit claim-bearers by default — cleaner for the share-token use case). Different defaults change what a forgotten declaration leaks. I lean strict (`userOrTeam`); flagging.

7. **Should the model carry a name for the "this deployment has any auth at all" question?**
   Today `PlatformMode.requiresAuth` is read in many places. The new helper `DeploymentConfig.requiresAnyAuth (config) = config.Surfaces |> List.exists (fun p -> match p with Anonymous _ -> false | _ -> true)` is a fine equivalent. The question is whether the helper deserves a one-word name (`isAuthenticatedDeployment`?) or stays as a predicate. Bikeshed-tier; flagging for completeness.


## Phase 3 — Subsystem changes

### 3.0 Resolutions to Phase 2's open questions

Phase 2 surfaced seven open questions. Resolutions, picking for platform flexibility + robustness:

1. **`Anonymous` profiles support `Persistence = Persistent`** — Yes. `AnonymousConfig` carries a `Persistence` field, defaults `Ephemeral`. Use case: long-lived demo surfaces (evergreen utilities) where session-keyed data should survive server restarts. The privacy/GC cost is bounded by `SessionEvictionMinutes`. Closes the door on the public-utility-with-admin workaround pattern.

2. **`AccessContext.UserId` for `ClaimBearer`** — `claim.AttributedHandle` when `Some`; otherwise synthetic `"claim:" + tokenId`. **Not** `IssuedBy` — that would leak issuer identity to handlers that have no business knowing it. The synthetic form keeps audit attribution stable per-token without identifying the issuer.

3. **Peer-bearer as a fifth subject kind** — No. Keeping peer-bearer separate. Federation is delegated authority (deployment B → deployment A), not a subject acting on its own behalf; conflating obscures rather than clarifies. The peer pipeline stays as it is today (federation deployments' needs unchanged).

4. **`ClaimBearerConfig` carrying the share-token-store backend** — Split. `ClaimBearerConfig` carries the policy fields (default lifetime, default use limit). The backend choice stays in a separate `ServerConfig.ShareTokenStore` field so a deployment can switch from `BlobShareTokenStore` to a future `RedisShareTokenStore` without restructuring its `Surfaces` list. **Robustness lever**: when `ClaimBearer _` is present in `Surfaces` and `ShareTokenStore` is unset, the SDK auto-promotes to `BlobShareTokenStore` (one-line authoring still works); the new `SurfaceCoherenceValidator` refuses startup if a `ClaimBearer` surface exists with `NoShareTokenStore` explicitly set.

5. **Anonymous → user migration hook** — Yes, design it in. New optional `IAnonymousSessionMigrator` interface invoked by the middleware on first authenticated request following an anonymous session in the same browser. Default impl: no-op. Deployments that want guest-data migration implement and wire via `ServerApp.withSubjectMigrator`. Details in §3.7.

6. **Default `SurfaceRequirement` for handlers without a declaration** — Strict. `SurfaceRequirement.userOrTeam` (admits `UserKind` and `TeamMemberKind`; rejects `AnonymousKind` and `ClaimBearerKind`). Fail-closed beats fail-open: a forgotten declaration that admits anonymous is a security hole; a forgotten declaration that produces a 403 is a deploy-time defect the operator notices immediately. Modules that want broader access opt-in explicitly.

7. **Predicate naming for "deployment has any auth"** — `DeploymentConfig.requiresAnyAuth`, plus the per-shape predicates `supportsAnonymous` / `supportsUser` / `supportsTeam` / `supportsClaimBearer`. Five short names, no central type.

The rest of Phase 3 takes these decisions as load-bearing.

### 3.1 Subject resolution and the new middleware pipeline

Today's pipeline (Phase 1 §1.4): `PeerBearerAuthMiddleware → ScopeResolutionMiddleware → AuthEnforcementMiddleware → CsrfMiddleware`. The mode lives in `ServerConfig.Mode`; scope resolution dispatches once at composition; CSRF and auth-enforcement carry separate exemption lists.

New pipeline:

```
PeerBearerAuthMiddleware           (unchanged — runs first; federation routes hand off here)
  ↓
ShareTokenAuthMiddleware           (NEW — validates ?token= query param or X-Share-Token header)
  ↓
ScopeResolutionMiddleware          (UPDATED — calls ISubjectResolver, stashes Subject + StorageScope)
  ↓
SurfaceEnforcementMiddleware       (REPLACES AuthEnforcementMiddleware — reads route's SurfaceRequirement)
  ↓
CsrfMiddleware                     (UPDATED — exemption derived per-route from SurfaceRequirement)
  ↓
Handler                            (sees ctx.Subject; matches on it)
```

**`ShareTokenAuthMiddleware`** (new) — extracts the token from `?token=...` query or `X-Share-Token` header, validates via `IShareTokenStore.Validate`, stashes the `ShareTokenClaim` on `HttpContext.Items["ToolUp.ShareTokenClaim"]`. Failure modes (`Malformed` / `InvalidSignature` / `Expired` / `Revoked`) flow into 401 with a `WWW-Authenticate` hint. If no token is present, this middleware is a no-op pass-through. **The middleware does not call `MarkUsed`** — that's the handler's responsibility after a successful operation (matching today's Forms semantics).

**`ISubjectResolver`** (new — replaces `IStorageScopeResolver` at the interface level):

```fsharp
type ScopeResolutionRequest = {
    User: AuthenticatedUser option       // populated by IAuthProvider.GetUser
    SessionId: string option             // X-User-Id header or generated GUID
    Claim: ShareTokenClaim option        // NEW — populated by ShareTokenAuthMiddleware
    Headers: Map<string, string>
}

type ISubjectResolver =
    abstract Resolve: ScopeResolutionRequest -> Async<Result<Subject, ScopeResolutionError>>
```

Resolution algorithm (the single per-request decision):

```
1. If request.Claim = Some c → return Subject.ClaimBearer c
   (Claim presence dominates; a request that bears a valid share token is a claim-bearer subject
   regardless of any other auth state.)

2. Else if request.User = Some u AND u is not anonymous:
   2a. If deployment supports Team scope AND user has an active team selected
       → return Subject.TeamMember (u.UserId, activeTeamId)
   2b. Else if deployment supports AuthenticatedUser
       → return Subject.AuthenticatedUser u.UserId
   2c. Else
       → return Error UnsupportedSubject UserKind
       (deployment has no shape for this user — should be impossible if validators ran)

3. Else (anonymous session):
   3a. If deployment supports Anonymous
       → return Subject.AnonymousSession sessionId
   3b. Else
       → return Error UnsupportedSubject AnonymousKind
```

The resolver is one stateless component (the per-mode dispatch in today's `SDK.Server.fs:1208-1221` collapses to one match). Default impl: `DefaultSubjectResolver` in `ToolUp.Platform.Server`. Pluggable for federation / sidecar resolution by implementing the interface.

**Caching:** today's `TeamScopeResolver` caches active-team pointers (5-min sliding TTL). `DefaultSubjectResolver` keeps that cache for step 2a (the team-membership probe). Per-request cache invalidation paths (the `MembershipChanged` notification subscription) carry over unchanged. The cache key is `userId`; the cache value is `(activeTeamId option, role option)`.

**Per-request `StorageScope`:**

```fsharp
let storageScope (cfg: ServerConfig) (subject: Subject) : StorageScope =
    match subject with
    | AnonymousSession sid ->
        let anonProfile =
            cfg.Surfaces |> List.pick (function Anonymous c -> Some c | _ -> None)
        { ScopeId = sid
          Container = $"session-{sid}"
          Persist = anonProfile.Persistence = Persistent }
    | AuthenticatedUser uid ->
        let userProfile =
            cfg.Surfaces |> List.pick (function AuthenticatedUser c -> Some c | _ -> None)
        { ScopeId = uid
          Container = $"user-{uid}"
          Persist = userProfile.Persistence = Persistent }
    | TeamMember (_, tid) ->
        let teamProfile =
            cfg.Surfaces |> List.pick (function Team c -> Some c | _ -> None)
        { ScopeId = tid
          Container = $"team-{tid}"
          Persist = teamProfile.Persistence = Persistent }
    | ClaimBearer claim ->
        { ScopeId = claim.ScopeId
          Container = claim.ScopeId
          Persist = true }
```

Note: `List.pick` is safe — `SurfaceCoherenceValidator` (§3.8) refuses startup if a resolved subject's profile is missing. The resolution algorithm above guarantees the subject kind matches a profile in `Surfaces` before reaching this point.

**`SurfaceEnforcementMiddleware`** — replaces `AuthEnforcementMiddleware`. Reads a per-process registry of route → `SurfaceRequirement` populated at composition time by `addModules`. Matching strategy:

1. Look up exact `(path, method)` match in the registry.
2. Else look up longest-prefix match against module-registered prefixes; use that module's `DefaultSurfaceRequirement`.
3. Else use `SurfaceRequirement.userOrTeam` (the strict global default).

Response codes:

| Subject kind | Surface accepts? | Response |
|---|---|---|
| `AnonymousKind` | No | **401** — `{"error":"authentication_required","status":401}` |
| `UserKind` | Yes | 200 (pass) |
| `UserKind` | No — surface requires `TeamMemberKind` | **403** — `{"error":"team_required","status":403,"hint":"select_team"}` |
| `TeamMemberKind` | Yes | 200 (pass) |
| `TeamMemberKind` | No — surface requires `AnonymousKind` only | **403** — `{"error":"authenticated_subject_not_admitted","status":403}` |
| `ClaimBearerKind` | Yes | 200 (pass) |
| `ClaimBearerKind` | No | **403** — `{"error":"claim_bearer_not_admitted","status":403}` |

The `hint` field on team-required 403 lets the client UI render an actionable "Select a team to continue" panel instead of a generic error.

**`CsrfMiddleware` carve-out, derived not declared:** today's `CsrfMiddleware` reads `config.AnonymousRoutePrefixes` and the peer-route registry to decide which routes skip CSRF. New form: the middleware reads the route's `SurfaceRequirement` from the same registry as `SurfaceEnforcementMiddleware` and skips CSRF when:

- The route's `AcceptedSubjects` contains `AnonymousKind` (anonymous requests can't carry the double-submit token correlation), OR
- The route's `AcceptedSubjects` contains `ClaimBearerKind` (claim-bearer is its own authority envelope — CSRF re-check is redundant), OR
- The route is a peer-bearer route (unchanged from today).

The `AcceptShallowAnonymousRoutePrefix` flag retires; the new `SurfaceCoherenceValidator` checks declarative requirements at startup, not string prefixes at request time.

**SSE auth carve-out:** today's `QueryParamFallback` exemption on `/api/ai/events` and `/api/notifications` stays — the EventSource API can't carry custom headers, so the cookie-vs-query-param choice is real and orthogonal to the surface model. The `SseAuthMode` field stays on `ServerConfig`. Validator wording updates from "warn in authenticated Mode" to "warn when any non-Anonymous surface is supported".

### 3.2 Persistence routing

Single, uniform wire-up. `IBlobStorage` is always wired (every non-pure-anonymous deployment needs it; even pure-anonymous deployments need it when `AnonymousConfig.Persistence = Persistent`). `SessionFileStore` reads `StorageScope.Persist` per request — exactly as today, just now the flag can vary per-request within a single deployment.

Two new wrinkles:

**Per-shape eviction policies.** `AnonymousConfig.SessionEvictionMinutes` (defaults `Some 60`) governs ephemeral-anonymous eviction. `AuthenticatedUserConfig` gains an analogous `SessionEvictionMinutes` field for the trial pattern (`Persistence = Ephemeral`). The eviction timer is one process-wide background service that reads each shape's policy on iteration; per-scope last-touched timestamps stay in the in-memory map. (No fanout to multiple timers — one wakeup per minute walks all ephemeral scopes.)

**Container-namespace collision safety.** A mixed-mode deployment serving `Anonymous` and `Team` is bytes-correct against collisions because containers are namespaced by prefix: `session-{id}` vs `team-{id}` vs `user-{id}`. A claim-bearer claim's `ScopeId` (which becomes the container directly without a prefix) MUST therefore be namespaced by the issuer to a non-colliding prefix — today's `BlobShareTokenStore` issues claims against `team-{teamId}` or `user-{userId}` containers (the issuer's own scope), so collisions are syntactically impossible. New validator rule: `IShareTokenStore` implementations must guarantee `ShareTokenIssueRequest.ScopeId` matches an existing namespaced container; the default `BlobShareTokenStore` enforces this in `Issue`. Custom implementations document the same guarantee.

**Shared `IBlobStorage` across shapes.** All four shapes share one `IBlobStorage` instance, partitioned by container prefix. This is already the case (today's `Anonymous` deployments wire `LocalFileStorage` too; the file store keeps them in an in-memory map but `IBlobStorage` is the substrate). No change.

**Cross-shape data isolation.** Hard structural: a request with `Subject.AnonymousSession sid` can ONLY produce a `session-{sid}` container path; a request with `Subject.TeamMember (uid, tid)` can ONLY produce `team-{tid}`. The middleware-stashed `StorageScope` is the SINGLE writable container; handlers that synthesise a path bypass the substrate. This is true today and stays true.

### 3.3 Module API

Module declarations gain a `DefaultSurfaceRequirement` field. Routes can override.

```fsharp
type ServerModule = {
    Name: string
    DefaultSurfaceRequirement: SurfaceRequirement
    RouteHandlers: RouteHandler list
    // …existing fields (data types, AI tools, jobs, etc.)…
}

and RouteHandler = {
    Path: string
    Method: HttpMethod
    SurfaceRequirement: SurfaceRequirement option    // None = inherit module default
    Handle: HttpContext -> AccessContext -> Async<HttpResponse>
}
```

Fluent builder:

```fsharp
ServerModule.create "Forms"
|> ServerModule.withDefaultRequirement SurfaceRequirement.userOrTeam
|> ServerModule.addRoute (
    RouteHandler.create "/api/forms/list" GET listFormsHandler)
|> ServerModule.addRoute (
    RouteHandler.create "/api/forms/public/submit" POST publicSubmitHandler
    |> RouteHandler.withRequirement SurfaceRequirement.claimBearerOnly)
|> ServerModule.addRoute (
    RouteHandler.create "/api/forms/admin/delete" DELETE deleteFormHandler
    |> RouteHandler.withRequirement SurfaceRequirement.teamScoped)
```

This consolidates the four moving parts in today's Forms wiring:
- `ServerConfig.AnonymousRoutePrefixes = ["/api/forms/public"]` → declared inline at route registration.
- `IPublicFormApi` registration → still a Forms-specific contract, but its routes carry `SurfaceRequirement.claimBearerOnly`.
- CSRF exemption for the prefix → derived automatically (claim-bearer routes are CSRF-skip).
- Validator for the prefix → retires.

**Client-side modules** carry an analogous visibility predicate:

```fsharp
type ClientModule = {
    // …existing fields…
    Visibility: SubjectKind -> bool   // shell-level sidebar filter
}

module ClientModule =
    /// Default: visible to users and team-members. Hides from anonymous + claim-bearer.
    let visibleToAuthenticated = function
        | UserKind | TeamMemberKind -> true
        | _ -> false

    /// Always visible.
    let visibleToAll _ = true

    /// Visible only when in team scope.
    let teamScopedOnly = function TeamMemberKind -> true | _ -> false
```

The shell's sidebar filter calls `module.Visibility (Subject.kind ctx.Subject)` for each module and hides those that return `false`. Solves the public-utility-with-admin anonymous-mode-sidebar-filtering pathology — modules now control their own visibility per subject kind, rather than the shell applying a blanket "hide everything in Anonymous" filter.

**SDK built-in modules — surface requirement assignments:**

| Built-in module | DefaultSurfaceRequirement | Notes |
|---|---|---|
| `PlatformApi` (team CRUD, etc.) | `userOrTeam` | Per-endpoint overrides: team-CRUD endpoints require `teamScoped`. |
| `PlatformAdminApi` | `userOrTeam` | Authority gated by `PlatformRole` check (not by surface — admin role is per-user). |
| `HealthMonitor` (basic readiness) | `public_` | `/health` and `/ready` openly reachable. |
| `HealthMonitor` (admin dashboard) | `userOrTeam` | Authority gated by `PlatformRole`. |
| `DiagnosticBundle` | `userOrTeam` | Authority gated by `PlatformRole`. |
| `ServiceStatusBoard` | `public_` | Public status page. |
| `UsageQuery` / `UsageDashboard` | `userOrTeam` | Authenticated only. |
| `DataIngestionAdmin` | `userOrTeam` | Authority gated by `PlatformRole`. |
| `AI` (settings + assist) | `userOrTeam` | Anonymous-mode AI gating becomes a deployment-policy decision: if `Anonymous` is in `Surfaces` AND the deployment wants public AI, the operator declares `AI` module visibility as `visibleToAll` (and accepts the cost surface). |
| `KnowledgeBase` | `userOrTeam` | |
| `Forms` (admin) | `userOrTeam` | |
| `Forms` (public-submit endpoints) | `claimBearerOnly` | Per-route override. |
| `Scheduling` | `userOrTeam` | |

### 3.4 Composition root API

The `withMode` builder retires; `Surfaces` lives on `ServerConfig`. The whole composition pipeline gets one new optional step (`withSubjectMigrator`):

```fsharp
ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with Surfaces = Surfaces.individual }
|> ServerApp.withAuth authProvider
|> ServerApp.withSubjectMigrator migrator         // OPTIONAL — only if anonymous→user data migration is wanted
|> ServerApp.addModules modules
|> ServerApp.run
```

For mixed-mode, the same shape:

```fsharp
ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with
                            Surfaces = Surfaces.anonymousAndIndividual }
|> ServerApp.withAuth authProvider
|> ServerApp.withSubjectMigrator FormDraftMigrator.instance   // migrate guest drafts on sign-in
|> ServerApp.addModules modules
|> ServerApp.run
```

Multi-shape with claim-bearer:

```fsharp
ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with
                            Surfaces = [
                                SurfaceProfile.anonymous
                                SurfaceProfile.multiTeam
                                SurfaceProfile.claimBearer
                            ] }
|> ServerApp.withAuth authProvider
|> ServerApp.addModules modules
|> ServerApp.run
```

The `withAuth` requirement: a deployment with ONLY `Anonymous` in `Surfaces` doesn't need an auth provider — `withAuth` becomes optional. A deployment with any other shape requires it. The composition-root validator catches the omission at startup.

**`fromEnv` helpers.** The Phase 11.G `ServerConfig.fromEnv` helper reads `TOOLUP_PLATFORM_MODE`; the new contract reads `TOOLUP_PLATFORM_SURFACES` (a comma-separated list: `anonymous,individual`, or `team,claim_bearer`, etc.). The old env var name is retired (no aliasing — clean cutover); migration guide carries the substitution. The `referenceApp` defaults in `ServerConfigOverrides` declare `Surfaces = Surfaces.individual` (matching today's behaviour).

### 3.5 Permission system integration

Permission resolution stays orthogonal to surface resolution. The change is in how `ModulePermissions` and `PlatformRole` get populated per Subject kind:

| Subject | `ModulePermissions` lookup | `PlatformRole` lookup | Notes |
|---|---|---|---|
| `AnonymousSession` | Not loaded — always `Map.empty` | Always `None` | Anonymous has no permission record. Authority is "what the surface admits". |
| `AuthenticatedUser` | Per-user permission record loaded from `IPermissionStore` | Loaded from `IPlatformAdminStore` if the user holds the role | Most-restrictive scope; the user owns their own data and any permissions they've been granted. |
| `TeamMember (uid, tid)` | Per-team permission record loaded from `IPermissionStore` keyed by `teamId + userId` | Loaded from `IPlatformAdminStore` (platform role transcends team) | Adds team-role check on top (`Owner` / `Admin` / `Member`). |
| `ClaimBearer claim` | Not loaded — always `Map.empty` | Always `None` | The claim itself IS the permission envelope. The handler reads `claim.ResourceKind` + `claim.ResourceId` + `claim.UseLimit` to decide what's allowed. |

New helper:

```fsharp
module AccessContext =
    /// Returns the share-token claim if the subject is a claim-bearer.
    /// Use this in handlers that need to inspect claim-bounded authority
    /// (resource kind / id / use limit / attribution).
    let claim (ctx: AccessContext) =
        match ctx.Subject with ClaimBearer c -> Some c | _ -> None

    /// True iff the subject is acting in a team scope.
    /// Equivalent to `ctx.TeamId.IsSome` but matches on `Subject` for
    /// clarity at the read site.
    let inTeamScope (ctx: AccessContext) =
        match ctx.Subject with TeamMember _ -> true | _ -> false
```

Team-role checks within a team scope (today's `Owner` / `Admin` / `Member` discriminator) stay structurally identical — the change is the pattern-match shape:

```fsharp
// Before (today):
match ctx.Mode with
| Team | MultiTeam when ctx.TeamId.IsSome ->
    let teamId = ctx.TeamId.Value
    let! role = teamStore.GetMemberRole(teamId, ctx.UserId)
    requireRole role TeamRole.Owner
| _ -> requireUnrestricted ctx

// After (new model):
match ctx.Subject with
| TeamMember (userId, teamId) ->
    let! role = teamStore.GetMemberRole(teamId, userId)
    requireRole role TeamRole.Owner
| _ -> requireUnrestricted ctx
```

`AISettingsHandler` (Phase 1 §1.9) rewrites this way mechanically. The team-role check semantics are unchanged.

### 3.6 Audit and observability

Audit-log entries gain a richer subject discriminator. Today's `Mode: string` field is replaced:

```fsharp
type AuditSubject =
    | AnonymousAudit of sessionId: string
    | UserAudit of userId: string
    | TeamAudit of userId: string * teamId: string
    | ClaimAudit of
        tokenId: string
        * attributedHandle: string option
        * resourceKind: string
        * resourceId: string

type AuditEvent = {
    // …existing fields (Timestamp, EventKind, SourceModule, Outcome, …)…
    Subject: AuditSubject
    // (the existing `UserId: string option` field retires — it's now in Subject)
}
```

JSON shape: `Subject` serialises as a tagged object (`{"kind":"team","userId":"...","teamId":"..."}`). Existing `IAuditSink` implementations migrate to read the new shape; the substrate update is a single `IAuditSink` contract version bump (sinks declare `SchemaVersion: int` so Splunk/Datadog/S3-archive sinks can negotiate).

**Metrics tagging.** Every metric carrying a `mode` tag today gets a `subject_kind` tag (`anonymous` / `user` / `team` / `claim`) AND, for team subjects, a `team_id` tag (which today is inferred via storage-scope lookup). OpenTelemetry sink updates the tag set in one place.

**Trace attribution.** Each request span gains:
- `subject.kind` — one of the four kind strings.
- `subject.user_id` — when known.
- `subject.team_id` — when in team scope.
- `subject.claim_id` — for claim-bearers (the `tokenId`, not the full claim payload).
- `subject.session_id` — for anonymous sessions (already low-entropy enough to log).

`X-Request-Id` middleware (today's `RequestIdMiddleware`) is unchanged. Per-request correlation stays the same.

**Audit-event volume implications.** A mixed-mode deployment with high anonymous traffic produces a higher rate of `AnonymousAudit` events. Today's `AuditLogModeValidator` warns when authenticated mode + `NoAuditLog`; the new wording is "warn when any non-`Anonymous` surface is in `Surfaces` + `NoAuditLog`". Pure-anonymous deployments can sensibly skip audit; mixed deployments cannot.

### 3.7 Anonymous → user session migration hook

New optional interface, wired via `ServerApp.withSubjectMigrator`:

```fsharp
type IAnonymousSessionMigrator =
    /// Called by the middleware on the first authenticated request that
    /// follows an anonymous session in the same browser. The migrator
    /// has access to both scopes (the anonymous `session-{sid}` and the
    /// now-authenticated `user-{uid}` / `team-{tid}`) via the injected
    /// IBlobStorage; it copies whatever data it owns into the user's
    /// persistent scope.
    abstract Migrate:
        anonymousSessionId: string
        * authenticatedSubject: Subject
        -> Async<Result<MigrationSummary, MigrationError>>

and MigrationSummary = {
    ItemsMigrated: int
    BytesMigrated: int64
    Modules: string list
}

and MigrationError =
    | NotEligible of reason: string         // session id already migrated, no data to migrate, etc.
    | InfrastructureFailed of message: string
    /// Partial success: some items migrated, others failed. Per OQ5
    /// resolution (option b): the partial summary is exposed so the
    /// operator's runbook can decide whether to retry the failed
    /// items, surface to the user, or abandon. LastSeenAnonymousSessionId
    /// updates regardless — preventing migration thrash — because the
    /// failed items are not generically retriable (item-specific
    /// failure modes need item-specific recovery).
    | PartialFailure of
        partial: MigrationSummary *
        failedItems: int *
        lastError: string
```

**Trigger detection.** The user's persistent record (in `_platform/users/{userId}.json`) gains a `LastSeenAnonymousSessionId: string option` field. On the first authenticated request following a sign-in:

1. Middleware reads the inbound `X-User-Id` (the anonymous session cookie from the pre-sign-in tab).
2. Middleware compares against the user's `LastSeenAnonymousSessionId`. If they differ AND the inbound session id has writable data, the migrator is invoked.
3. On `Ok summary`, the user's record updates `LastSeenAnonymousSessionId` to the current value (prevents double-migration on the next request).
4. On `Error NotEligible`, the record updates too (no retry).
5. On `Error PartialFailure`, the record updates (no thrash). The partial summary is surfaced to the operator via audit-log entry + structured warning; the runbook owns recovery of the failed items.
6. On `Error InfrastructureFailed`, the record does NOT update (retry on next request). Logged at warning level.

**Default migrator.** A `NoOpAnonymousSessionMigrator` ships as the default. Deployments without `withSubjectMigrator` get it.

**Composability.** Multiple migrators can compose via a small combinator:

```fsharp
module AnonymousSessionMigrator =
    /// Combine N migrators; runs each sequentially, accumulating
    /// summaries. Bails on the first InfrastructureFailed.
    let compose (migrators: IAnonymousSessionMigrator list) : IAnonymousSessionMigrator = …
```

The Forms companion ships `FormDraftMigrator` for "guest drafts → user account"; the AI companion can ship `ConversationDraftMigrator` for "guest AI conversation → user account"; etc. Each is independently registered, composed at composition time.

### 3.8 Validators — rewritten plus one new

The 12 existing Mode-aware validators (Phase 1 §1.8) rewrite mechanically. Each `PlatformMode.requiresAuth config.Mode` becomes `DeploymentConfig.requiresAnyAuth config`. Each `PlatformMode.isTeamScoped config.Mode` becomes `DeploymentConfig.hasTeamScope config`. Escape-hatch flags rename slightly (drop `InAuthenticatedMode` suffix):

| Old name | New name |
|---|---|
| `AcceptHeaderAuthInAuthenticatedMode` | `AcceptHeaderAuthWhenAuthRequired` |
| `AcceptPlaintextSecretsInAuthenticatedMode` | `AcceptPlaintextSecretsWhenAuthRequired` |
| `AcceptNoRateLimitInAuthenticatedMode` | `AcceptNoRateLimitWhenAuthRequired` |
| `AcceptShallowAnonymousRoutePrefix` | (retires — no prefix list anymore) |
| `AcceptQueryParamSseAuthInAuthenticatedMode` | `AcceptQueryParamSseAuthWhenAuthRequired` |

Semantics: "when auth is required" now means "when `Surfaces` contains any non-`Anonymous` profile" — the predicate `DeploymentConfig.requiresAnyAuth`. A mixed-mode deployment with `[Anonymous _; Team _]` is auth-requiring (the team surface needs it); the validator runs.

**`AnonymousRoutePrefixValidator` retires entirely.** Its job is subsumed by the new `SurfaceCoherenceValidator` (below) + the per-route declarative `SurfaceRequirement`.

**New `SurfaceCoherenceValidator`.** Runs at startup, refuses to start the server when any of the following is true:

1. `config.Surfaces` is empty.
2. `config.Surfaces` contains duplicate profile constructors (two `Team _` entries, etc.).
3. Any module's `DefaultSurfaceRequirement` declares an `AcceptedSubjects` containing a `SubjectKind` no `SurfaceProfile` in `config.Surfaces` produces.
   - Example: module declares `teamScoped` requirement; deployment has `Surfaces = [SurfaceProfile.individual]`. The module is unreachable.
   - Error message: `"Module 'Forms' requires team scope but deployment Surfaces declares no Team profile. Add SurfaceProfile.team to Surfaces or change the module's DefaultSurfaceRequirement."`
4. Any route's per-endpoint `SurfaceRequirement` is similarly inconsistent.
5. `config.Surfaces` contains `ClaimBearer _` but `config.ShareTokenStore = NoShareTokenStore` was explicitly set.
6. `config.ShareTokenStore ≠ NoShareTokenStore` but `config.Surfaces` contains no `ClaimBearer _` (warning, not error — substrate is wired but no surface accepts claim-bearers; legitimate during a feature roll-back).
7. `config.Surfaces` contains only `Anonymous _` but `ServerApp.withAuth` was called with a non-`HeaderAuthProvider` auth provider (warning — auth provider is unreachable; legitimate during development).
8. `config.Surfaces` contains any non-`Anonymous _` but no auth provider was registered (error).

The validator is the central "did the operator declare a coherent deployment?" gate. Failures are operator-actionable with a remediation suggestion baked into the error message.

**Auto-promotion of `ShareTokenStore` default.** Not a validator behaviour per se, but a composition-time fix-up: when `Surfaces` contains `ClaimBearer _` and `ShareTokenStore` is unset (defaults to `NoShareTokenStore`), the composition root auto-promotes it to `EnabledShareTokenStore BlobShareTokenStore.create`. Logged at info level so operators can see the auto-promotion in startup output. The override is set-explicit to disable: `ShareTokenStore = NoShareTokenStore` (validator then refuses — see rule 5 above).

### 3.9 Subsystem-change summary (cross-cut)

A condensed map of where every change lands in forge:

| Subsystem | Files touched (approx) | Change shape |
|---|---|---|
| Core types | `Types/StorageScope.fs`, `Types/AccessContext.fs`, new `Types/Subject.fs`, new `Types/SurfaceProfile.fs`, new `Types/SurfaceRequirement.fs` | Add `Subject` DU + helpers; retire `PlatformMode`; add `SurfaceProfile` + `SurfaceRequirement`; `AccessContext.Mode` → `AccessContext.Subject`. |
| `ServerConfig` | `Shared/SDK.Shared.fs` (around line 1269-2150) | `Mode: PlatformMode` → `Surfaces: SurfaceProfile list`. Rename escape-hatch flags. Drop `AnonymousRoutePrefixes` + `AcceptShallowAnonymousRoutePrefix`. |
| Scope/subject resolution | `Server/Scope/StorageScopeResolver.fs` → rename to `SubjectResolver.fs`. New `ShareTokenAuthMiddleware`. | Collapse 4 resolvers to one `DefaultSubjectResolver`. Add share-token middleware. |
| Middleware pipeline | `Server/Middleware.fs`, `Server/CsrfMiddleware.fs` | Replace `AuthEnforcementMiddleware` with `SurfaceEnforcementMiddleware`. Update CSRF carve-out logic to derive from per-route `SurfaceRequirement`. |
| Module API | `Server/ServerModule.fs`, `Server/Compose/BuildRouteHandlers.fs`, `Client/ClientModule.fs` | Add `DefaultSurfaceRequirement` to module record; add per-route override; client gains `Visibility: SubjectKind -> bool`. |
| Composition root | `Server/ServerApp.fs`, `Server/SDK.Server.fs`, `Client/SDK.Client.fs` | Drop `withMode`. Add `withSubjectMigrator`. Replace per-mode resolver dispatch with single `ISubjectResolver` wiring. |
| Validators | 12 existing validator files + new `SurfaceCoherenceValidator.fs`, retire `AnonymousRoutePrefixValidator.fs` | Mechanical rewrite of predicates; new validator centralises surface coherence. Rename escape-hatch fields. |
| Permission integration | `Server/PlatformApiHandler.fs`, `Server/AISettingsHandler.fs`, every team-CRUD handler | Mechanical rewrite of `ctx.Mode` matches to `ctx.Subject` matches. |
| Audit/observability | `Shared/AuditTypes.fs`, `Server/AuditDispatcher.fs`, every `IAuditSink` impl | `AuditEvent.Subject: AuditSubject`. Sinks declare `SchemaVersion: int`. OpenTelemetry sink adds `subject_kind` / `team_id` tags. |
| Anonymous→user migration | New `Server/IAnonymousSessionMigrator.fs`, new middleware step | Optional substrate; default no-op. Composability combinator. |
| Forms (consumer of the API) | `Forms.Server/PublicFormApiHandler.fs`, `Forms.Server/FormsCompose.fs` | Routes declare `claimBearerOnly`; module-level `withSubjectMigrator` adds `FormDraftMigrator` as a follow-on. |
| Client identity flow | `Client/UserSession.fs`, `Client/AuthUIProvider.fs`, `Client/ClientConfigFromBundleConstants.fs` | `ClientConfig.Mode` → `ClientConfig.Surfaces`. Storage selection (sessionStorage vs localStorage) keys off resolved `Subject.kind`. |
| `fromEnv` helpers | `Server/ServerConfigFromEnv.fs`, `Client/ClientConfigFromBundleConstants.fs` | `TOOLUP_PLATFORM_MODE` → `TOOLUP_PLATFORM_SURFACES` (comma-separated). |

The bulk of the change is mechanical (Subject-for-Mode pattern-match rewrite, validator-predicate updates). The genuinely new code is `SubjectResolver`, `ShareTokenAuthMiddleware`, `SurfaceCoherenceValidator`, `IAnonymousSessionMigrator`, the per-shape `RateLimitConfig` primitive (§3.10), and the `RevokeOnIssuerRemoved` companion (§3.11). Everything else is migration of existing code to the new types.

### 3.10 Per-shape rate-limit policies

Per OQ1 resolution (operator-overridden from "defer" to "implement now"), `RateLimitConfig` evolves from a single deployment-wide policy to a default-plus-overrides shape that admits per-shape rates.

**Design.** A `RateLimitPolicy` describes the rate; the partition key is *implied by the subject kind* rather than carried on the policy. This avoids the foot-gun of "did I pick the right partition key for this subject?" — for each subject kind there is one natural partition (IP for anonymous, userId for user, teamId for team-member, tokenId for claim-bearer).

```fsharp
/// A rate envelope: how many requests per window, with queueing.
/// Partition key is implied by the subject kind that applies the
/// policy — see RateLimitPolicy.partitionFor.
type RateLimitPolicy = {
    PermitLimit: int       // max permits per window
    Window: TimeSpan       // sliding window
    QueueLimit: int        // queued waiters; 0 = reject immediately on saturation
}

/// Mapping of partition keys per subject kind. Documented contract
/// for readers; not directly authored.
module RateLimitPolicy =
    let partitionFor (subject: Subject) : string =
        match subject with
        | AnonymousSession _ -> sprintf "ip:%s" (clientIp ctx)
        | AuthenticatedUser uid -> sprintf "user:%s" uid
        | TeamMember (_, tid) -> sprintf "team:%s" tid
        | ClaimBearer claim -> sprintf "token:%s" claim.TokenId

/// Deployment-wide rate-limit configuration. Default applies to every
/// subject kind unless overridden in PerShape. PerShape keys not present
/// fall through to Default.
type RateLimitConfig = {
    /// Default policy applied to every subject kind without an override.
    /// None disables rate-limiting deployment-wide unless PerShape
    /// lists overrides.
    Default: RateLimitPolicy option
    /// Per-subject-kind overrides. Sparse map; absent keys use Default.
    PerShape: Map<SubjectKind, RateLimitPolicy>
}

module RateLimitConfig =
    /// No rate-limiting on any subject kind.
    let none = { Default = None; PerShape = Map.empty }

    /// One policy applied uniformly across every subject kind. The
    /// natural single-mode form; equivalent to today's RateLimitConfig.
    let uniform (policy: RateLimitPolicy) =
        { Default = Some policy; PerShape = Map.empty }

    /// Different policies per subject kind, no fallback. Each kind must
    /// be listed explicitly. Use when subject-kind-specific behaviour
    /// is the point and a fallback would hide misconfiguration.
    let perShape (m: Map<SubjectKind, RateLimitPolicy>) =
        { Default = None; PerShape = m }

    /// Default policy + per-kind overrides. The common mixed-mode shape.
    let withOverrides (defaultPolicy: RateLimitPolicy) (overrides: Map<SubjectKind, RateLimitPolicy>) =
        { Default = Some defaultPolicy; PerShape = overrides }
```

**Middleware integration.** Today's `RateLimitingMiddleware` reads one policy and partitions on team/user/IP based on `AccessContext`. New shape: reads `Subject` from `HttpContext.Items["ToolUp.Subject"]`, picks the policy via `config.RateLimit.PerShape |> Map.tryFind (Subject.kind subject) |> Option.orElse config.RateLimit.Default`, partitions via `RateLimitPolicy.partitionFor`. Same `System.Threading.RateLimiting` substrate underneath; the change is in policy lookup.

**Authoring shorthand.** Single-mode deployments still write one line:
```fsharp
{ ServerConfig.defaults with
    RateLimit = RateLimitConfig.uniform { PermitLimit = 100; Window = TimeSpan.FromMinutes 1.0; QueueLimit = 0 } }
```

Mixed-mode with per-kind:
```fsharp
{ ServerConfig.defaults with
    Surfaces = [SurfaceProfile.anonymous; SurfaceProfile.team]
    RateLimit = RateLimitConfig.withOverrides
                    { PermitLimit = 100; Window = TimeSpan.FromMinutes 1.0; QueueLimit = 0 }   // default
                    (Map.ofList [
                        AnonymousKind, { PermitLimit = 10; Window = TimeSpan.FromMinutes 1.0; QueueLimit = 0 }
                        TeamMemberKind, { PermitLimit = 500; Window = TimeSpan.FromMinutes 1.0; QueueLimit = 0 }
                    ]) }
```

**Validator behaviour.** `RateLimitModeValidator` (Phase 1 §1.8) updates:
- Warning when `requiresAnyAuth config = true` AND `RateLimit = RateLimitConfig.none` (no policy anywhere — same wording as today, generalised).
- Warning when `PerShape` contains a key for a `SubjectKind` not in `Surfaces` (declared policy for a subject the deployment doesn't serve — likely a misconfiguration).
- No new error severities; the existing `AcceptNoRateLimitWhenAuthRequired` escape hatch still applies to the "no policy at all" case.

**Excluded endpoints stay excluded.** `/health`, `/ready`, `/api/notifications` (SSE), `/api/ai/events` (SSE) — today's exclusion list (Phase 1 §1.2 `RateLimit` row) carries over verbatim. The exclusions are pipeline-level, not policy-level.

### 3.11 `RevokeOnIssuerRemoved` companion

Per OQ2 resolution (operator-overridden from "document only" to "implement now"), forge ships an opt-in companion that revokes share-token claims when their issuing user is removed from the team they issued under.

**Substrate change — one new method on `IShareTokenStore`.** The decorator needs to enumerate claims issued by a specific user; today's interface has `ListByResource` (by resource kind/id) but no by-issuer lookup. Add:

```fsharp
type IShareTokenStore =
    // …existing methods (Issue, Validate, MarkUsed, Revoke, ListByResource)…

    /// Enumerate all non-revoked claims issued by the given user under
    /// the given scope. Returns empty if the issuer has no live claims.
    /// Stateless re-read; no caching obligation on impls.
    abstract ListByIssuer:
        scopeId: string * issuerUserId: string
        -> Async<ShareTokenClaim list>
```

`BlobShareTokenStore` impl: scans `_platform/share-tokens/{scopeId}/` and filters claims with matching `IssuedBy`. Listing is bounded — the per-scope set of live claims is small (~100s in pessimistic deployments). For future high-volume cases, a sidecar index could be added; not a v1 concern.

Contract-test addition: `IShareTokenStoreContract` gains a `ListByIssuer` round-trip test (issue 3 claims as user A, 2 as user B; list by A returns 3; revoke one of A's; list by A returns 2).

**The decorator itself.** New companion under `src/ShareTokenStoreDecorators/RevokeOnIssuerRemoved/` (following the existing flat-companion convention — see `src/Storage/`, `src/AuditSinks/`, etc.):

```fsharp
namespace ToolUp.ShareTokens.RevokeOnIssuerRemoved

/// Decorates an inner IShareTokenStore. Subscribes to MembershipChanged
/// notifications; on Removed kind, enumerates the removed user's live
/// claims under the affected team's scope and revokes each.
type RevokeOnIssuerRemovedStore(
    inner: IShareTokenStore,
    notificationChannel: INotificationChannel,
    logger: ILogger
) =
    // Construction subscribes to the MembershipChanged notification key.
    // On Removed { teamId; userId }, calls inner.ListByIssuer(scopeId =
    // $"team-{teamId}", issuerUserId = userId), then inner.Revoke for
    // each. Logs every revocation at info level with the token id.
    // Idempotent: re-receiving the same Removed event finds zero
    // remaining claims and no-ops.

    interface IShareTokenStore with
        // Pass-through for every method except the subscribe-side
        // bookkeeping in the constructor.
```

**Wiring.** Composition-root API:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withShareTokenStoreDecorator RevokeOnIssuerRemoved.wrap   // OPTIONAL — opt-in
|> …
```

`ServerApp.withShareTokenStoreDecorator` accepts `IShareTokenStore -> IShareTokenStore` so future decorators (per-token rate-limit, per-claim audit, etc.) can compose. Composition order: decorators apply outside-in (the last `withShareTokenStoreDecorator` call wraps the others).

**Behavioural semantics worth nailing.**
- *Race vs. in-flight token validation.* If a `Validate` call lands between membership removal and the revoke, the validation may succeed against a soon-to-be-revoked claim. Acceptable — the revocation is the substrate's reaction to the membership change; one extra successful validation is bounded loss. The bearer's next call will fail with `RevokedToken`.
- *Claim under a non-team scope.* If a user issued a claim under their own `user-{uid}` scope (e.g. a personal dashboard share-link), and the user is "removed" from their own scope — there's no such operation. The decorator only reacts to team `MembershipChanged.Removed`. User-scope claims are unaffected.
- *Claim audit.* Each revocation produces an audit event (`SourceModule = ShareTokenTypes.AuditSourceModule`, action = `Revoked`, actor = `"system:RevokeOnIssuerRemoved"`). Audit trail attributes the revocation to the decorator, not to a user — clear signal in forensic review.

**Validator coherence.** `SurfaceCoherenceValidator` rule additions:
- Warning when `withShareTokenStoreDecorator` registers `RevokeOnIssuerRemoved` but `ClaimBearer _` is not in `Surfaces` (decorator wired but unreachable — same shape as the existing share-token-store-unreachable warning).
- Warning when the decorator is wired but no `Team _` is in `Surfaces` (the decorator only acts on team `MembershipChanged`; without team scope, it's structurally inert).

**Effort.** ~1.5-2 days incremental (companion authoring + `IShareTokenStore.ListByIssuer` substrate addition + contract-test row + decorator unit tests + composition-root wiring + docs).

## Phase 4 — Edge cases and risk analysis

Pressure-testing the design against scenarios the type signatures alone don't catch. The bias is toward enumerating concrete failure modes — naming them gives operators a vocabulary for the runbooks they'll write.

### 4.1 Edge-case enumeration

**E1 — Anonymous → Authenticated mid-session (the migration moment).**

A browser tab operating as `Subject.AnonymousSession sid` completes a sign-in and the next request carries `Authorization: Bearer <token>`. `SubjectResolver` resolves the new subject (`AuthenticatedUser uid`, or `TeamMember (uid, tid)` if the user has an active team).

What can go wrong:

- *Double-migration race.* Concurrent first-authenticated requests both observe `LastSeenAnonymousSessionId ≠ currentSid` and both invoke the migrator. Mitigation: the middleware acquires a per-`userId` exclusive lock around the migration call; second concurrent request blocks until the first completes, then observes the updated `LastSeenAnonymousSessionId` and skips. Lock is a process-local `SemaphoreSlim`; distributed deployments use the existing `IDistributedLock` substrate (if wired) or accept the small overlap (idempotent migrators handle re-application).
- *Wrong-account migration.* User Bob explores anonymously, then accidentally signs in as Alice. Bob's anonymous data migrates to Alice. Mitigation: this is fundamentally a user error and the platform cannot detect intent. The migrator interface returns a `MigrationSummary` so deployments can surface "We migrated 3 form drafts. [Discard]" in the UI; the deployment owns the discard path.
- *Partial migration failure.* Migrator succeeds on 5 items, fails on the 6th. Mitigation: `MigrationSummary` reports `ItemsMigrated`; on `Error InfrastructureFailed`, `LastSeenAnonymousSessionId` does NOT update — the migrator retries on the next request. Migrators must be idempotent (the substrate enforces nothing; this is a contract on impls, documented in the interface).
- *Sign-out followed by sign-in as same user.* Anonymous session id doesn't change across sign-out/in (sessionStorage vs localStorage selection switches with Subject, but the X-User-Id header doesn't necessarily). Mitigation: `LastSeenAnonymousSessionId` matches → migrator skips. Correct.
- *Sign-in to a deployment that doesn't support `AuthenticatedUser`.* `SubjectResolver` returns `Error UnsupportedSubject UserKind`. The deployment has only `Anonymous` in `Surfaces` but somehow received a bearer token — perhaps a stale token from a previous deployment shape. The middleware responds 401 ("subject not supported"); the client should clear the token and revert to anonymous. Edge case worth a validator warning at startup if `withAuth` is set but no non-`Anonymous` surface is in `Surfaces` (the auth provider is unreachable in practice).

**E2 — Team switching mid-session.**

User in `MultiTeam` deployment switches from team T1 to team T2. `Subject` changes from `TeamMember (uid, T1)` to `TeamMember (uid, T2)`. The shell's `TeamSwitched` reset path runs (existing behaviour from Phase 1 §1.1).

What can go wrong:

- *In-flight requests against T1.* A request arrives before the switch and resolves to `TeamMember (uid, T1)`. The switch happens during the handler. The in-flight request continues with the T1 subject — the server doesn't re-resolve mid-handler. Subsequent requests resolve to T2. Correct behaviour; no leakage (the T1 request never touches T2 data because it was scoped to T1's container at resolution time).
- *Membership revocation race.* Admin removes user from T1 while user has T1 selected. `MembershipChanged` notification fires; `TeamScopeResolver` evicts the active-team cache. Next request: `TeamScopeResolver.Resolve` lookups now-empty active team, returns `Error NoActiveTeam` or finds another team the user is in. If found → `Subject.TeamMember (uid, otherTeam)`. If not found → `Subject.AuthenticatedUser uid` (deployment supports it) or `Error UnsupportedSubject UserKind` (it doesn't). The client should treat the resulting 403 as a signal to refresh the user's team list and prompt re-selection.
- *User removed from every team in a Team-only deployment.* Subject becomes `Error UnsupportedSubject UserKind` — every route 403s. The client renders a "you've been removed from every team; contact an administrator" panel. Worth surfacing the `team_required` hint with a specific sub-state (`no_teams_available`) in the response.

**E3 — Cross-shape data leakage scenarios.**

- *Anonymous request synthesising a team path.* Not possible — `Subject.AnonymousSession sid` produces `Container = session-{sid}`; handlers receive `StorageScope` as a parameter and have no API to construct arbitrary `team-{tid}` paths against `IBlobStorage`. The substrate enforces by construction.
- *Forged share-token claim.* HMAC signature on the token's payload (Phase 1 §1.5) defends against forgery; signing key lives in `ISecretStore`. Forged tokens fail `Validate` with `InvalidSignature`. Existing defence.
- *Claim with `ScopeId` pointing to another team.* The signed claim's `ScopeId` is fixed at issue time and signed; tampering invalidates the HMAC. A team admin can only issue claims into their own team's scope (server-side check at `Issue`). Existing defence.
- *Race: user added to a new team, immediately switches, sees stale data.* `TeamScopeResolver` invalidates the membership cache on `MembershipChanged`. Stale data is bounded to ≤5 minutes (the cache TTL) under network partition; under normal conditions ≤100ms (notification propagation).
- *Handler with a `_` wildcard that admits the wrong subject kind.* A team-CRUD handler with the pattern:
  ```fsharp
  match ctx.Subject with
  | TeamMember (uid, tid) -> doTeamThing uid tid
  | _ -> Error "Not in team scope"
  ```
  is safe. A handler with:
  ```fsharp
  match ctx.Subject with
  | TeamMember (uid, tid) -> doTeamThing uid tid
  | _ -> doTeamThing "anonymous" "default"   // ← dangerous fallback
  ```
  leaks across shapes. Mitigation: enable `[<RequireQualifiedAccess>]` on `Subject`, plus a Fantomas linting rule (or Roslyn analyser) that flags `_` wildcards followed by team-mutating calls. Phase 5 migration plan adds the lint as a follow-on.

**E4 — Anonymous abuse / rate-limiting in mixed-mode.**

A mixed-mode deployment with high anonymous traffic must defend the authenticated cost surface (AI tokens, storage write rate) from anonymous abuse. Today's `RateLimit` partitions by team / user / IP. In the new model:

- Anonymous subjects partition by **IP** (no user id to key on).
- Authenticated user subjects partition by **userId**.
- Team-member subjects partition by **teamId**.
- Claim-bearer subjects partition by the **token's scope** (today's per-token rate-limit, already in `BlobShareTokenStore`).

What can go wrong:

- *Anonymous abuse via session-id rotation.* An attacker rotates `X-User-Id` to evade per-session limits. IP-based partition catches them. Without IP partition, anonymous limits are bypassable.
- *Cost asymmetry.* An anonymous deployment with AI enabled exposes the API key to unlimited tokens. The `Anonymous` AI caveat (Phase 1 §1.9) now applies to mixed-mode deployments too: if `Anonymous` is in `Surfaces` AND the AI module's surface requirement admits `AnonymousKind`, the operator owns the cost ceiling.
- *Per-shape rate-limit overrides.* New consideration: should `RateLimit` carry per-shape policies? E.g. `{ Anonymous = 10rps/IP; User = 100rps/userId; Team = 500rps/teamId }`. Today it's one policy; mixed-mode reveals that per-shape rates are sometimes wanted. Recommendation: ship as one policy for the first cut, surface per-shape policies as a follow-on phase if real deployments demand it. Most consumers won't need this complexity initially.

**E5 — Share-token-gated anonymous access (the claim-bearer lifecycle).**

- *Claim issuer leaves the team.* The claim survives (issued by the org, not the person). Optional behaviour: deployments can wrap `IShareTokenStore` with a "revoke on issuer-removed" decorator; ships as a companion (`ToolUp.ShareTokens.RevokeOnIssuerRemoved` or similar). Default: claims persist.
- *Claim used in parallel.* `MarkUsed` is atomic (per the interface contract); `UseLimitExceeded` is raised the moment the counter would exceed. Parallel requests are serialised by the underlying store's `MarkUsed` semantics.
- *Claim wire-format collision with anonymous session id.* Both are passed in URL/header. Distinct mechanisms: anonymous session uses `X-User-Id` (or sessionStorage cookie); share token uses `?token=` query OR `X-Share-Token` header. No collision.
- *Claim survives a token-id collision.* Wire format includes `tokenId.base64(payload).base64(hmac)`; the HMAC binds the payload (which includes the token id). A collision (two tokens with the same id but different payloads) is statistically impossible (GUID generation) but if forged, the HMAC fails — only one of the two could be the legitimate one.
- *Claim against a now-disabled `ClaimBearer` surface.* Operator removes `ClaimBearer _` from `Surfaces` in a deploy. Old claims continue to validate at the substrate level, but the `SurfaceEnforcementMiddleware` 403s every claim-bearer subject (no surface admits `ClaimBearerKind`). Result: existing tokens stop working without explicit revocation. Mitigation: the validator (Phase 3 §3.8 rule 6) warns at startup when `ShareTokenStore ≠ NoShareTokenStore` AND `ClaimBearer _` is not in `Surfaces` — operators see the warning and can decide whether to drain claims or restore the surface.

**E6 — Capabilities ↔ Surface requirements consistency.**

The new `SurfaceCoherenceValidator` (Phase 3 §3.8) catches static inconsistencies at startup. Runtime scenarios:

- *Module declares `teamScoped`; deployment supports Team*; user signs in but has no active team. Subject resolves to `AuthenticatedUser uid`. Surface enforcement rejects (`team_required` hint). Client renders team-onboarding flow. Correct.
- *Module declares `teamScoped`; deployment doesn't support Team.* Validator refuses startup (Phase 3 §3.8 rule 3). Caught before the first request.
- *Module declares `claimBearerOnly`; deployment supports `ClaimBearer` but a regular user reaches the route.* Surface enforcement 403s. Client should never have rendered the route — the client-side `Visibility` filter hides claim-bearer-only modules from non-claim subjects.
- *Per-endpoint override conflicts with module default.* Endpoint-level requirement is more specific; it wins. Validator audits each endpoint's requirement is reachable from the deployment's `Surfaces`.

**E7 — `Anonymous + Persistent` semantics.**

The Q1 resolution permits `AnonymousConfig.Persistence = Persistent`. Edge cases:

- *Eviction in persistent-anonymous mode.* `SessionEvictionMinutes` still applies; the eviction timer deletes from both the in-memory map AND the persistent backing. A timer iteration with a stalled backend (e.g. S3 slow) backs up; the design accepts the eviction lag (eventual consistency for cleanup).
- *Session id reuse across sessions.* A user's `X-User-Id` may be cleared (browser cookie wiped) and re-issued with the same GUID (collision is statistically impossible but theoretically possible). The persistent backing would be visible to whoever reissued. Mitigation: the session id is GUID-strength; collisions are not a defended-against concern.
- *Audit volume.* Persistent anonymous data implies more audit events with `AuditSubject.AnonymousAudit sid`. The `AuditLogModeValidator` warning still applies (non-Anonymous-only deployment + NoAuditLog).

**E8 — Composition order: substrate auto-wiring.**

The composition root auto-wires `ITeamStore` when `Team _` is in `Surfaces`, `IShareTokenStore` when `ClaimBearer _` is in `Surfaces` (default `BlobShareTokenStore`). What if the operator explicitly registers a non-default impl?

- Operator calls `ServerApp.withTeamStore customStore` BEFORE `Surfaces` is set with `Team _`. Composition root uses `customStore` (explicit wins).
- Operator calls `ServerApp.withTeamStore customStore` AFTER `withConfig`. Composition root replaces the auto-wired default. (The fluent API is order-tolerant — final wiring happens at `run`.)
- Operator omits `withTeamStore` entirely with `Team _` in `Surfaces`. Composition root auto-wires `TeamStore` default. ✅

This requires the composition root's `run` step to defer substrate selection to "after the user has called every builder method, before serving requests". Today's `SDK.Server.fs` already does this for several substrates; the pattern extends.

**E9 — Federation deployments.**

A federation deployment today uses `Mode = Individual` + peer-bearer routes. New model: `Surfaces = Surfaces.individual` + peer-bearer routes unchanged (peer-bearer is orthogonal — Phase 2 §2.8). Migration is mechanical.

What can go wrong:

- *Per-shape SurfaceRequirement on peer routes.* Peer routes don't go through `SurfaceEnforcementMiddleware` — they short-circuit via `PeerBearerAuthMiddleware`. They have no `SurfaceRequirement`; the `SurfaceCoherenceValidator` knows to skip them (they're registered separately via `PeerRouteRegistry`).
- *A federated request that's ALSO a claim-bearer.* Doesn't happen — `PeerBearerAuthMiddleware` runs first; if it stamps `PeerName`, subsequent middlewares skip. The two paths are mutually exclusive.

**E10 — Multi-deployment shape evolution.**

A deployment ships with `Surfaces = [Individual]`. The operator wants to add `Anonymous` for a public landing page. Migration:

1. Update the consumer's composition root: `Surfaces = [SurfaceProfile.individual; SurfaceProfile.anonymous]`.
2. Add a landing module with `Visibility = visibleToAll` (or whatever).
3. Redeploy.

What can go wrong:

- *Existing authenticated users see the new anonymous landing on first load.* The shell renders the most-specific subject's view (TeamMember > User > Anonymous). Existing users skip the anonymous landing automatically.
- *Existing data unchanged.* Containers (`user-{}`, `team-{}`) keep their data; adding `Anonymous` doesn't touch them.
- *Validator runs.* New surface coherence check catches any consumer-side module misconfiguration (e.g. a third-party module forgot to declare its requirement).

**E11 — Backwards-compatibility test: rebuilding Phase 1's public-utility-with-admin workaround under the new model.**

Today: `Mode = Individual` + `DevDefaultUserId = "dev-admin"`. The workaround was needed because Anonymous-mode hid every module from the sidebar.

New model: `Surfaces = [SurfaceProfile.anonymous; SurfaceProfile.individual]`. The public calculator modules declare `Visibility = visibleToAll` and `DefaultSurfaceRequirement = SurfaceRequirement.public_`. Admin modules declare `Visibility = visibleToAuthenticated` and `DefaultSurfaceRequirement = SurfaceRequirement.userOrTeam`.

Result: public users see the calculator; signed-in admins see calculator + admin views. The workaround retires; the design serves the actual use case.

**E12 — Anonymous user issues a share-token claim.**

Per Phase 2 §2.3, the `ClaimBearer` subject is gated by share-token validation. The opposite question: can an anonymous user *issue* a claim? The `IShareTokenStore.Issue` interface requires an `IssuedBy: string` field — today populated from `AccessContext.UserId`. Anonymous subjects have `UserId = sessionId`, so technically they could.

Should they? Almost never — claim issuance is an administrator-level operation. Mitigation: the routes that issue claims (e.g. Forms' "issue invite token" admin endpoint) declare `userOrTeam` or `teamScoped` requirement. Anonymous users can't reach them. Discipline is at the route level, where it belongs.

### 4.2 Risk analysis

**R1 — Wrong abstraction discovered post-ship.**

*What could go wrong:* the 4-case `Subject` DU turns out to need a fifth case (e.g. service-account identity for automation, distinct from peer-bearer). Or the `SurfaceProfile` list shape proves limiting (e.g. consumers want per-tenant per-shape policies, which the list-of-shapes can't express).

*Reversibility:*
- Code-side: high — every consumer is under the operator's control; rollback is a coordinated `git revert` + redeploy across siblings.
- Data-side: zero migration. Storage containers (`session-{}`, `user-{}`, `team-{}`) keep their layout. New fields (`LastSeenAnonymousSessionId`) are additive and harmless if unread.
- API-side: any consumer pinned to the new model has a `git revert` path. The OSS-public-launch implication is that **the new API MUST be considered breaking from 0.x; rolling forward post-public is the harder problem**. Phase 5 sequences this against the public flip.

*Mitigation:* the design pass itself; ship the design doc as a versionable artifact under `docs/design/` so the rationale is recoverable; add the 4-case DU with `[<RequireQualifiedAccess>]` so adding a fifth case later is a non-breaking change.

**R2 — Performance overhead from per-request resolution.**

*Concern:* today's `ServerConfig.Mode` is a constant; new `ISubjectResolver.Resolve` runs per request.

*Reality:* the resolution is one pattern-match + (conditionally) one cached `ITeamStore.GetActiveTeam` call. Cache hit is the common case (5-min TTL). Cold cache: one blob read (~10ms typical, S3 P50). Net overhead per request: negligible (~µs hot, ~ms cold).

*The team-membership probe is uncached on purpose* (Phase 1 §1.3 step 2 — defends against concurrent removal). That's an extra `ITeamStore.GetMemberRole` per team-scoped request. This already runs today; no regression.

*Mitigation:* contract-test the resolver with a benchmark suite (Phase 5 test strategy). Set a per-request budget (e.g. p99 < 5ms hot-cache). Surface budget violations in the `Diagnostics` module.

**R3 — Reversibility of post-public-flip changes.**

The OSS-public launch is imminent (memory note: "target OSS public flip within ~1 week"). Once forge is public, breaking API changes cost mindshare. The mixed-mode redesign is a breaking change.

*The choice:* land it before public flip (one big breaking change consumers absorb during a single bump), OR land it after (a clean 0.2.0 with a published migration guide).

Both are defensible. The case for "before public flip":
- Public release reflects the right model from day 1.
- No "wait, this still uses the old `PlatformMode`?" confusion in early adopters' first read.
- The migration guide is fresh in the operator's head.

The case for "after public flip":
- Public launch is its own coordination effort; bundling a major API rewrite delays the launch.
- 0.2.0 with a separate announcement gives the redesign its own communications moment.
- Early adopters (probably none in the public week) absorb the change with the rest.

Phase 5 takes a position. Genuine open question to surface to the operator — they have insight into the launch window I don't.

**R4 — Security: larger attack surface in mixed-mode.**

*Concern:* mixed-mode deployments host anonymous + authenticated surfaces in one process. A vuln in the anonymous endpoint could pivot.

*Defences inherent to the design:*
- `SurfaceEnforcementMiddleware` checks every `/api/*` route. No handler is reachable by a subject kind the route doesn't admit.
- `StorageScope` is computed from Subject; handlers can't synthesise a path of another shape.
- CSRF carve-out is per-route declarative (not a prefix list), so adding an anonymous-reachable route doesn't accidentally widen the CSRF exemption.
- The `SurfaceCoherenceValidator` refuses startup on module/surface mismatches.

*Residual risk:* handler bugs (e.g. SQL injection, path traversal in a file-write handler) are not addressed by the surface model. The existing defences (param validation, prepared statements, path canonicalisation) still apply.

*Mitigation:* document the threat model explicitly in `docs/platform/security.md` (new section: "Mixed-mode threat surface"). Cover: rate-limiting per shape; AI cost ceilings per shape; audit visibility per shape.

**R5 — Operator confusion / learning curve.**

*Concern:* `PlatformMode = Individual` is one identifier the operator picks from a known list. `Surfaces = [SurfaceProfile.individual; SurfaceProfile.anonymous; SurfaceProfile.claimBearer]` is more to think about.

*Counter-evidence:* single-mode shorthand (Phase 2 §2.6) keeps the simple case one line. The operator who wants `Mode = Individual` writes `Surfaces = Surfaces.individual`. No regression in cognitive load for that path.

*Mixed-mode authoring is genuinely harder.* Operators who want public + private surfaces have to think about each one. This is *correct* — they're declaring two distinct things. The model surfaces the complexity that was hidden before.

*Mitigation:* documentation. `docs/platform/surfaces.md` walks the mental model with diagrams. Sample apps demonstrate the patterns. The error messages from `SurfaceCoherenceValidator` are actionable (Phase 3 §3.8).

**R6 — Audit volume in mixed-mode.**

*Concern:* a deployment serving high anonymous traffic produces high audit-event volume on the anonymous subject; sinks can be overwhelmed.

*Mitigation:* the `IAuditSink` substrate already supports batching; sinks declare their own back-pressure. New consideration: the `AuditDispatcher` carries a per-subject-kind sampling policy (default: 100% authenticated, 10% anonymous) — sample the anonymous traffic for forensic visibility without paying full price. Configurable per deployment. New `ServerConfig` field: `AuditSamplingPolicy`. Default = no sampling (every event); operators opt in.

Phase 3 §3.6 didn't surface this — adding it here. The change is additive; `IAuditSink` impls don't change.

**R7 — Downstream consumer churn.**

*Concern:* every existing downstream consumer references `Mode = …`. Updating them all is a chore.

*Reality:* each consumer edit is a one-line substitution in its composition root. The migration plan (Phase 5) bundles the sweep as one PR per consumer.

*Mitigation:* the sweep is an explicit Phase 5 deliverable. Sample apps demonstrating multi-surface patterns (`mixed-mode-survey`, `freemium-tool`) ship alongside.

### 4.3 Downstream deployment-shape implications

The mixed-mode model enables / improves several deployment shapes.

**Shapes that improve under the new model:**

| Shape | Today (workaround) | Mixed-mode form |
|---|---|---|
| public survey + admin | `Mode = Team` + `AnonymousRoutePrefixes = ["/api/forms/public"]` + share tokens | `Surfaces = [team; claimBearer]`; respondent surface declares `claimBearerOnly`; admin surface inherits `userOrTeam`. The anonymous-route-prefix complexity disappears. |
| appointment booking | `Mode = Team` | `Surfaces = [team; claimBearer]` if booking links are shared via token; otherwise unchanged. |
| team-scoped tracker | `Mode = Team` | Unchanged (no anonymous surface). |
| per-user tool | `Mode = Individual` | `Surfaces = Surfaces.individual` — trivial rename. |

**New deployment shapes the design enables:**

- **marketing site with admin** — public marketing pages + private CMS admin. `Surfaces = [anonymous; team]`. Anonymous landing module + team-scoped CMS module.
- **freemium tool** — anonymous "try it" + authenticated "save your work" tier. `Surfaces = [anonymous; individual]` with optional `IAnonymousSessionMigrator` to migrate guest drafts on sign-up.
- **community forum** — anonymous read + authenticated post + team-scoped moderation. `Surfaces = [anonymous; individual; team]`.
- **public utility + private config** — public calculator + private team admin. `Surfaces = [anonymous; team]`. Anonymous-mode persistent storage if calculations should survive.
- **shareable dashboard** — team owns a dashboard, generates share links for external viewers. `Surfaces = [team; claimBearer]`. Read-only public view; team admins manage.

The documentation additions (under a new "Mixed-mode patterns" section):

- How to declare `Surfaces` for the common pairings.
- How to write module `Visibility` predicates that match operator intent.
- How to wire `IAnonymousSessionMigrator` for the freemium pattern.
- How to think about per-shape AI cost ceilings.
- The audit-volume / rate-limit considerations for mixed-mode.

### 4.4 Documentation needs

**For OSS readers picking up the codebase cold:**

The mental model needs to land in three terms:

- **Subject** — per-request, "who's acting?". The pivot for storage, persistence, audit, permissions.
- **SurfaceProfile** — per-deployment, "which shapes does this deployment support?". A list; non-empty.
- **SurfaceRequirement** — per-route, "which shapes can reach this surface?". A set of subject kinds.

These three terms appear in every doc page and form the vocabulary. New file:

- `docs/platform/surfaces.md` (replaces `platform-modes.md`) — the primary mental-model doc. Diagram of the request-resolution flow (Subject resolution → surface check → handler). Worked examples of single-mode and mixed-mode authoring. The mode-to-surface mapping table for old-PlatformMode-readers.

**Updates to existing OSS docs:**

- `docs/platform/architecture.md` — add the Subject section; remove the PlatformMode references.
- `docs/platform/composition-roots.md` — updated `withConfig` examples.
- `docs/platform/auth.md` — updated request-resolution flow; updated examples for each Subject kind.
- `docs/platform/storage.md` — `StorageScope` flow under mixed-mode.
- `docs/platform/modules.md` — module API including `DefaultSurfaceRequirement` and `Visibility`.
- `docs/platform/portability-rules.md` — `ISubjectResolver` is the new portable interface; add its contract.
- New: `docs/platform/security.md` — explicit "Mixed-mode threat surface" section (R4 mitigation).

**Migration guide for consumers:**

`docs/migrations/0.X.0-platform-mode-to-surfaces.md` — the consumer-facing migration doc. Includes:

- Old → new mapping table:

  | Old `PlatformMode` | New `Surfaces` helper |
  |---|---|
  | `Anonymous` | `Surfaces.anonymous` |
  | `AuthenticatedEphemeral` | `Surfaces.trial` |
  | `Individual` | `Surfaces.individual` |
  | `Team` | `Surfaces.team` |
  | `MultiTeam` | `Surfaces.multiTeam` |

- `ServerConfig` field changes (drop `Mode`; add `Surfaces`; drop `AnonymousRoutePrefixes` + `AcceptShallowAnonymousRoutePrefix`; rename `Accept*InAuthenticatedMode` flags).
- Env-var rename (`TOOLUP_PLATFORM_MODE` → `TOOLUP_PLATFORM_SURFACES`; new value format: comma-separated list).
- `AccessContext.Mode` → `AccessContext.Subject` (with `kindLabel` helper for log-only callsites).
- Pattern-match rewrite examples (5 worked examples covering the common shapes).
- Validator escape-hatch flag renames.
- The downstream consumer sweep (one section per affected consumer).

**Composition-root samples:**

Every sample under `samples/` updates to the new model. The `HelloWorld` sample becomes the canonical "single-mode `Surfaces.individual`" example. A new `samples/MixedMode/` (or similar) demonstrates `[anonymous; team; claimBearer]` end-to-end.

**For the public flip (R3):**

If the redesign lands BEFORE the public flip, the launch announcement leads with "mixed-mode platform model" as a headline feature. If AFTER, a separate "0.2.0 — mixed-mode platform model" announcement carries the migration guide as its central artifact.

## Phase 5 — Implementation strategy + migration plan

### 5.0 Position taken — clean cutover, before public flip

Per the operator's R3 directive ("land before public flip"), combined with the no-backward-compat-required constraint (§Constraints 1), the migration shape is **clean cutover, single coordinated bump across the SDK and every consumer**. The old `PlatformMode` enum, `AnonymousRoutePrefixes`, and the per-mode resolver dispatch all go in one commit per affected file; no parallel-old-API window. The risk of a parallel API would be operators copying old patterns from cached search results post-public; clean cutover is the cleaner story.

Resource allocation and scheduling are the operator's call. The §5.8 effort estimate is the engineering input to that call; the rest of Phase 5 details the per-stream work without making timing assumptions.

### 5.1 SDK migration: order of operations

Six streams; A and B are serial-within-themselves but parallelisable across each other once core types land. Streams C–F mostly parallelise.

#### Stream A — Core types and the request pipeline (serial; gates everything)

1. **A.1 — Add `Subject`, `SubjectKind`, `SurfaceProfile`, `SurfaceRequirement` types** (new files in `src/ToolUp.Platform.Core/Shared/Types/`). Delete `PlatformMode` and its helpers in the same commit. Update `AccessContext.Mode` → `AccessContext.Subject` + add `AccessContext.claim` / `AccessContext.inTeamScope` / `AccessContext.kindLabel`. Update `StorageScope` doc-comments (the type itself doesn't change). Compile-fails everywhere downstream — this is the load-bearing commit; subsequent steps fix the breakage.
2. **A.2 — `ServerConfig` migration** (`Shared/SDK.Shared.fs`). Drop `Mode: PlatformMode`; add `Surfaces: SurfaceProfile list`. Drop `AnonymousRoutePrefixes` + `AcceptShallowAnonymousRoutePrefix`. Rename `Accept*InAuthenticatedMode` → `Accept*WhenAuthRequired`. Update `ServerConfigOverrides` mirror fields. Update `ServerConfig.defaults` + `ServerConfigOverrides.referenceApp`.
3. **A.3 — `ISubjectResolver` + `DefaultSubjectResolver`** (replaces `IStorageScopeResolver` dispatch in `Server/Scope/`). One resolver implementing the resolution algorithm from Phase 3 §3.1. Keep `TeamScopeResolver`'s active-team cache (extracted to a helper).
4. **A.4 — `ShareTokenAuthMiddleware`** (new file in `Server/`). Reads `?token=` / `X-Share-Token`; calls `IShareTokenStore.Validate`; stashes claim on `HttpContext.Items["ToolUp.ShareTokenClaim"]`.
5. **A.5 — `SurfaceEnforcementMiddleware`** (replaces `AuthEnforcementMiddleware` in `Server/Middleware.fs`). Reads route → `SurfaceRequirement` registry. Response-code matrix from Phase 3 §3.1.
6. **A.6 — Update `ScopeResolutionMiddleware` to call `ISubjectResolver` + stash `Subject` and `StorageScope`. Update `CsrfMiddleware` carve-out derivation. Pipeline wiring in `SDK.Server.fs` updates.
7. **A.7 — Composition-root API.** Drop `withMode`; add `withSubjectMigrator`. Substrate auto-wiring (`ITeamStore` when `Team _` in `Surfaces`; `IShareTokenStore` when `ClaimBearer _`).
8. **A.8 — `fromEnv` helpers.** Update `ServerConfigFromEnv.fs` + `ClientConfigFromBundleConstants.fs` to read `TOOLUP_PLATFORM_SURFACES` (comma-separated list). Helper functions for parsing; clear error messages on malformed input.

**Stream A completion criterion:** `dotnet build ToolUp.Forge.sln` succeeds, the HelloWorld sample boots in `Surfaces.individual`, and a curl against `/api/health` returns 200.

#### Stream B — Internal call-site rewrites (parallel with A.3+ once A.1/A.2 land)

9. **B.1 — Validator rewrites.** 12 existing validators (Phase 1 §1.8) rewrite their `PlatformMode.requiresAuth` / `isTeamScoped` predicates to `DeploymentConfig.*`. Retire `AnonymousRoutePrefixValidator.fs`.
10. **B.2 — `SurfaceCoherenceValidator`** (new). 8 refusal rules from Phase 3 §3.8. Contract tests against the rules. Auto-promotion of `ShareTokenStore` default.
11. **B.3 — Module API extensions.** Add `DefaultSurfaceRequirement` to `ServerModule`; add per-route `SurfaceRequirement option`. Add `Visibility: SubjectKind -> bool` to `ClientModule`. Sidebar filter in client shell uses `Visibility`.
12. **B.4 — SDK built-in modules.** Surface-requirement assignments per Phase 3 §3.3 table (13 built-in modules). Client-side `Visibility` defaults set to `visibleToAuthenticated` for team-scoped + admin modules; `visibleToAll` for public health/status endpoints.
13. **B.5 — Pattern-match rewrites.** Every `match ctx.Mode with` site rewrites to `match ctx.Subject with`. Touches: `PlatformApiHandler.fs`, `AISettingsHandler.fs`, `PlatformAdminApiHandler.fs`, `HealthMonitorApiHandler.fs`, `DiagnosticBundleHandler.fs`, `DevDiagnosticsHandler.fs`, `UsageQueryApi.fs`, `ServiceStatusBoardApiHandler.fs`, several `Compose/` files. ~50 sites across the SDK; mostly mechanical.
14. **B.6 — Forms companion update.** `PublicFormApiHandler` routes declare `claimBearerOnly` via the new module API; `FormsCompose` drops the explicit `AnonymousRoutePrefixes` addition.
15. **B.7 — Audit subsystem.** `AuditEvent.Subject: AuditSubject` (replaces `Mode` string + `UserId option`). `IAuditSink.SchemaVersion: int` contract addition. Sinks: SplunkHec, DatadogLogs, S3Archive — each gets a one-line schema-version declaration and the deserialiser switch. OpenTelemetry sink (`Metrics/OpenTelemetry/`) tag updates.
16. **B.8 — Client identity flow.** `UserSession.fs` storage selection keys off resolved Subject kind (today's deployment-wide Mode → per-request Subject; the client receives the subject kind in the first authenticated response or via `ClientConfig.Surfaces`). `AuthUIProvider.fs` branches on Subject kind for the sign-in UI mount.

**Stream B completion criterion:** `dotnet build ToolUp.Forge.sln` clean; HelloWorld + all forge sample boots green; the three test suites (`Platform.Tests`, `Forms.Tests`, `Scheduling.Tests`) green.

#### Stream C — Net-new substrate

Per OQ1/OQ2 operator overrides, Stream C is no longer "deferrable to 0.2.0" — both the per-shape rate-limit primitive and the `RevokeOnIssuerRemoved` companion ship in the public-flip release.

17. **C.1 — `IAnonymousSessionMigrator`** (interface + `NoOpAnonymousSessionMigrator` default + middleware integration). `LastSeenAnonymousSessionId` field on the user record; `SemaphoreSlim`-per-userId double-migration guard.
18. **C.2 — `ServerConfig.AuditSamplingPolicy`.** Per-shape sampling rate; default no sampling. `AuditDispatcher` reads the policy per event and skips deterministically (hash on event id).
19. **C.3 — Per-shape `RateLimitConfig`** (per §3.10, OQ1 resolution). Evolve `RateLimitConfig` to `{ Default; PerShape }` shape. Add `RateLimitPolicy.partitionFor` helper. Update `RateLimitingMiddleware` to read subject kind and pick policy. Update `RateLimitModeValidator` with the two new warning rules. Update `RateLimitConfig` authoring helpers (`none`, `uniform`, `perShape`, `withOverrides`).
20. **C.4 — `IShareTokenStore.ListByIssuer` substrate addition** (per §3.11, OQ2 resolution). Add the method to the interface; implement in `BlobShareTokenStore`; extend `IShareTokenStoreContract` with the round-trip test.
21. **C.5 — `RevokeOnIssuerRemoved` companion** (per §3.11). New `src/ShareTokenStoreDecorators/RevokeOnIssuerRemoved/` package. Subscribes to `MembershipChanged` notifications; revokes the leaver's claims under the affected scope. Audit-event attribution. Tests covering the race, the no-team-scope-affected case, and idempotency.
22. **C.6 — `ServerApp.withShareTokenStoreDecorator`** composition-root API. Accepts `IShareTokenStore -> IShareTokenStore`; composable; ordering documented. `SurfaceCoherenceValidator` rules updated to warn on decorator-wired-but-no-`ClaimBearer`-surface or decorator-wired-but-no-`Team`-surface.

#### Stream D — Tests (parallel with B)

23. **D.1 — `ISubjectResolver` contract test pack** (new file in `Platform.Tests/Contracts/`). Tests every resolution path: anonymous, user-no-team, user-with-team, claim-bearer, unsupported-subject failures. Reusable across alternative resolver impls.
24. **D.2 — `SurfaceEnforcementMiddleware` response-code matrix tests** (`InProcess/`). The 7-row table from Phase 3 §3.1; one test per row.
25. **D.3 — `SurfaceCoherenceValidator` tests** (`InProcess/`). 10 refusal/warning rules (8 original + 2 added for `RevokeOnIssuerRemoved` coherence); one test per rule + a happy-path test.
26. **D.4 — CSRF carve-out tests.** Route declaring `anonymousOnly` skips CSRF; route declaring `userOrTeam` requires CSRF; per-endpoint override case.
27. **D.5 — Existing validator tests update.** 12 validator test files rewrite to match the new predicate shapes. Mechanical.
28. **D.6 — `IAnonymousSessionMigrator` tests.** Default no-op behaviour; trigger-detection logic; double-migration guard; composition combinator; `PartialFailure` round-trip.
29. **D.7 — `RateLimitConfig` per-shape tests.** Single-policy fallback to `Default`; per-shape override applied; partition-key dispatch per `RateLimitPolicy.partitionFor`; `PerShape`-without-`Default` rejects on unknown kind.
30. **D.8 — `IShareTokenStoreContract.ListByIssuer` round-trip test** + `RevokeOnIssuerRemoved` integration tests (subscribe, react to `MembershipChanged.Removed`, enumerate claims, revoke each, audit emission).

**Stream D completion criterion:** all three Expecto runners exit 0 with new + updated tests included. Net test count increases by ~30–40.

#### Stream E — Consumer migrations (parallel with B once Stream A completes)

Per-repo migration shapes detailed in §5.3.

#### Stream F — Documentation (parallel with everything; lands last in the commit log)

25. **F.1 — `docs/platform/surfaces.md`** (new, replaces `platform-modes.md`). The mental-model doc: Subject, SurfaceProfile, SurfaceRequirement; request-resolution flow diagram; mixed-mode authoring examples; the old→new mapping table.
26. **F.2 — `docs/platform/security.md`** (new section if file exists, new file otherwise). Mixed-mode threat surface; per-shape rate-limiting guidance; AI-cost-ceiling considerations.
27. **F.3 — Update existing docs.** `architecture.md`, `auth.md`, `storage.md`, `composition-roots.md`, `modules.md`, `portability-rules.md`. Drop `platform-modes.md` after replacement is reviewed (one commit).
28. **F.4 — `docs/migrations/0.X.0-platform-mode-to-surfaces.md`** (new) — the consumer migration guide.
29. **F.5 — Sample updates.** HelloWorld → `Surfaces.individual`. New `samples/MixedMode/` demonstrating `[anonymous; team; claimBearer]`.

#### Inter-stream dependency

```
A.1 (core types)
  ↓
A.2-A.8 (pipeline) ──→ Stream A complete
  ↓
[B] [D] (parallel) ──→ green tests
  ↓
[C] [E] [F] (parallel) ──→ all repos migrated + docs landed
  ↓
Public flip
```

A is the critical path. B + D parallelise (once A.1/A.2 land). C, E, F can run in parallel once Stream B's module API additions (B.3, B.4) are in.

### 5.2 SDK internals — clean cutover, not staged

Clean cutover. No parallel-old-API window. The reasoning (Phase 5 §5.0): a parallel API would let new contributors copy stale patterns from cached search results post-public; one coordinated commit per file is the cleaner shape. The old `PlatformMode` enum, `AnonymousRoutePrefixes`, `AcceptShallowAnonymousRoutePrefix`, the per-mode resolver dispatch, the prefix-validator — all retire in the same Phase wave.

The retirement order within a single commit per affected file:
- Delete `PlatformMode` declaration (Stream A.1).
- Every `match PlatformMode with` site rewrites to `match Subject with` (Stream B.5; touches ~50 sites across the SDK).
- Every `PlatformMode.requiresAuth` / `isTeamScoped` call rewrites to the `DeploymentConfig` helpers (Stream B.1).
- Every `ServerConfig.Mode = …` literal in samples, defaults, env-var helpers rewrites to `Surfaces = …` (Stream A.2 + A.8).
- Drop `Anonymous*RoutePrefix*` machinery (Stream A.6 + B.1).

Old `PlatformMode` deletion happens in the same commit as the addition of `Subject` (Stream A.1) — no transitional state.

### 5.3 Per-consumer-repo migration shapes

The repo-by-repo migration plan, drawing on Phase 1 §1.10's consumer survey.

#### Pure-Individual internal-tools deployment

**Current state**: `Mode = Individual` resolved via `ServerConfig.fromEnv`. Multiple commercial modules. No anonymous surfaces, no peer routes, no IPublicFormApi.

**New composition root:**

```fsharp
let config = ServerConfig.fromEnv logger ServerConfigOverrides.referenceApp
// referenceApp defaults now declare Surfaces = Surfaces.individual

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth (AuthProvider.fromEnv logger ToolUp.AuthProviders.OidcAuthProvider.fromConfig)
|> ServerApp.addModules modules
|> ServerApp.run
```

**Touch sites:** the `ServerConfig.fromEnv` call inherits the new defaults transparently. **Env-var change**: `TOOLUP_PLATFORM_MODE=individual` → `TOOLUP_PLATFORM_SURFACES=individual` in deployment manifests. **Client**: `Mode = Some Individual` → `Surfaces = Some Surfaces.individual` (one line). No consumer-side `Mode`-pattern-match logic to rewrite.

**Effort:** ~0.5 day. Mechanical.

#### Federation deployment pair (two-app)

**Current state**: both apps `Mode = Individual` + three localhost-testing waivers + peer-bearer routes via `RegisteredPeer` list.

**New composition root (both apps, parallel structure):**

```fsharp
let config = {
    ServerConfig.defaults with
        Surfaces = Surfaces.individual
        AcceptHeaderAuthWhenAuthRequired = true       // renamed flag
        AcceptPlaintextSecretsWhenAuthRequired = true // renamed flag
        AcceptQueryParamSseAuthWhenAuthRequired = true // renamed flag
        PeerRoutePrefixes = [...]   // unchanged — peer-bearer is orthogonal
}

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth …
|> ServerApp.withRegisteredPeers peers   // unchanged
|> ServerApp.addModules modules
|> ServerApp.run
```

**Federation-specific considerations:** peer-bearer middleware is orthogonal to the surface model (Phase 2 §2.8). `PeerRoutePrefixes`, `RegisteredPeer`, the `PeerRouteRegistry`, and `PeerBearerAuthMiddleware` all stay unchanged. The `SurfaceCoherenceValidator` knows to skip peer routes (they don't go through `SurfaceEnforcementMiddleware`).

**Client (each side of the pair):** `Mode = Individual` → `Surfaces = Surfaces.individual`.

**Touch sites:** four files (server + client on each side of the pair) + the three accept-flag renames in two server files.

**Effort:** ~1 day (more than the single-app internal-tools deployment because of the parallel structure).

#### Pure-Anonymous public portal

**Current state**: `Mode = Anonymous`, single module, minimal composition.

**New composition root:**

```fsharp
let config = { ServerConfig.defaults with Surfaces = Surfaces.anonymous; PublicPath = "public" }

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.addModules [singleModule.serverModule]
|> ServerApp.run
```

**Quirks:** the module's `Visibility = visibleToAll` and `DefaultSurfaceRequirement = SurfaceRequirement.public_` need to be set. Today the module relies on the deployment being `Mode = Anonymous` for everything to "just work"; the new model makes the public-reachability explicit. This is a 2-line change in the module's `Server.fs` + `ClientView.fs`.

**Touch sites:** 2 composition-root files + 2 module files = 4 files.

**Effort:** ~0.5 day. Trivial.

#### Public-utility-with-admin (workaround retires)

**Current state**: `Mode = Individual` (workaround) + `DevDefaultUserId = "dev-admin"` + the three localhost-testing waivers. Multiple calculator modules.

**The "workaround retires" migration.** This consumer's existence motivates the design pass — it *wants* mixed-mode. The migration is the most consequential because it's the one where the consumer's behaviour materially changes.

**New composition root:**

```fsharp
let config = {
    ServerConfig.defaults with
        Surfaces = Surfaces.anonymousAndIndividual   // ← the design's headline
        // The three Accept*WhenAuthRequired waivers stay (still localhost-testing).
}

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth HeaderAuthProvider.instance   // still needed for the Individual surface
|> ServerApp.addModules modules
|> ServerApp.run
```

**Module-level work.** Each calculator module needs:
- `DefaultSurfaceRequirement = SurfaceRequirement.public_` (the calculator is publicly reachable).
- `Visibility = visibleToAll` (calculator pages render for both subjects).

The admin / config modules (if any) declare `userOrTeam` requirement + `visibleToAuthenticated` visibility. From the Phase 1 audit, the app appears to be calculator-only today (no separate admin module yet); future admin work declares the strict requirement.

**The `DevDefaultUserId` field retires.** It was a workaround for the Anonymous-mode sidebar-filter pathology, which the new `Visibility: SubjectKind -> bool` per-module mechanism replaces. The deployment instructions simplify: anonymous users get the calculator; signed-in admins get extra options.

**Touch sites:** 2 composition-root files + per-module files (one per calculator).

**Effort:** ~1 day. More than the other deployment archetypes because the *behaviour* genuinely changes (not just a rename). Recommend operator validates the public-calculator UX before merging.

#### Template-scaffolded apps (bulk migration)

**Strategy:** for apps scaffolded from the SDK templates, two paths:

- **Re-scaffold from updated templates** — the cleanest, since the templates carry the new `Surfaces = …` declarations natively.
- **In-place migration** — for scaffolded apps the operator wants to keep iterating on, the migration is identical to the per-consumer pattern above: update the `Surfaces` declaration; per-module-side add `DefaultSurfaceRequirement` + `Visibility`.

**Recommendation:** update templates + re-scaffold, so each scaffold is a clean reference for adopters.

**Effort:** ~1 day for the template updates (one-line `Surfaces` substitution + the Visibility additions).

#### Per-consumer migration commit pattern

Each consumer migration lands as a single commit per repo: `feat(platform): migrate to Surfaces` (or similar).

### 5.4 Built-in module surface-requirement assignments (recap)

Already enumerated in Phase 3 §3.3 — 13 SDK built-in modules with their `DefaultSurfaceRequirement` and per-route override sites. Treat that table as the implementation checklist. One commit per module-folder is the natural unit (avoids any one commit getting too large).

### 5.5 Template + sample-app sweep

The SDK templates and sample apps each get:

1. Composition-root updates: `Mode = X` → `Surfaces = Surfaces.X` (or the mixed-mode list).
2. `DefaultSurfaceRequirement` for the app's module(s).
3. `Visibility` for client-side modules (where relevant).
4. Doc updates referencing the new `SurfaceCoherenceValidator` failure modes.
5. A "See also" pointer to `docs/platform/surfaces.md`.

Shapes worth specifically reviewing because they're affected most by the redesign:
- public survey + admin — moves from `Mode = Team + AnonymousRoutePrefixes` to `Surfaces = [team; claimBearer]`. Complexity drops noticeably.
- appointment booking — same pattern if it uses share-tokens.
- marketing site — moves to `Surfaces = [anonymous; team]`.
- single-mode per-user / team apps — field-name update only.

**Effort:** ~1 day total (one PR; per-app changes are small).

### 5.6 Test strategy

Test categories, per the workspace convention (Expecto runners, contract test packs):

- **Contract tests** (`Platform.Tests/Contracts/`): new `ISubjectResolverContract`. Existing `IShareTokenStoreContract` gets two new tests (the auto-promotion-on-`ClaimBearer`-surface case + the unreachable-substrate warning). External impls validate against the same conformance bar.
- **In-process tests** (`Platform.Tests/InProcess/`): tests for `SurfaceEnforcementMiddleware` (the 7-row response-code matrix from Phase 3 §3.1), `SurfaceCoherenceValidator` (8 rules), `CsrfMiddleware` carve-out derivation, `ISubjectResolver` resolution algorithm (5 paths). Each is one Expecto module per concern.
- **Updated existing tests:** 12 validator tests + the existing PlatformApiHandler / AISettingsHandler / etc tests that branch on Mode. Rewrites are mechanical.
- **New tests for new substrate:** `IAnonymousSessionMigrator` (default behaviour + composition combinator + double-migration guard).
- **Sample-boot tests:** the HelloWorld sample boots and serves `/api/health` returning 200 under `Surfaces.individual`. New: the `samples/MixedMode/` sample boots and routes requests correctly per subject.
- **Per-consumer smoke tests** (where a consumer ships them): existing Expecto suites on the pinned-consumer side. Already exist; the rewrites preserve coverage.

**Net test count:** +~30–40 tests (most in `InProcess/` for the new middleware/validator behaviour); 0 deleted tests (the old ones rewrite, they don't retire).

**Test ordering for CI:** the existing three-suite Expecto runner pattern (per forge `CLAUDE.md` Build pipeline section) is the gate. Each suite must exit 0; `dotnet build ToolUp.Forge.sln` clean is the cross-suite gate.

**Fable verification:** `samples/HelloWorld/` Fable compile is part of CI (per CLAUDE.md). The MixedMode sample joins the Fable verification set.

### 5.7 Sequencing relative to OSS-public launch

Per the operator's R3 directive: **mixed-mode lands before public flip.** The full design (all six streams) ships in the public-flip release; the OQ1/OQ2 substrate (per-shape rate-limit primitive + `RevokeOnIssuerRemoved` companion) is part of that scope per the operator's OQ resolutions.

Two structural consequences worth recording:

- **0.x is the right version band for the redesign.** The public flip is the SDK's first non-pre-release moment; landing the new model with it means contributors' first read sees the right vocabulary, and there is no "0.x had `PlatformMode`, 0.2.0 had `Surfaces`" disconnect in cached search results.
- **The design doc itself is the artifact that justifies the schedule.** Reviewers (early adopters, contributors, the eventual co-owner reviewer per the public-launch governance) can read this file to understand why the SDK shipped what it shipped. The doc lives at `docs/design/mixed-mode-platform.md` post-public.

### 5.8 Effort estimate

Per stream, in focused engineering-days. Updated to include OQ1/OQ2 work (per-shape rate-limit primitive + `RevokeOnIssuerRemoved` companion).

| Stream | Description | Estimate (days) |
|---|---|---|
| A | Core types + pipeline + composition root | 6.5–8 |
| B | Internal rewrites + module API + Forms + audit + client | 8.5–12 |
| C | New substrate (migrator, sampling, per-shape rate-limit, `ListByIssuer`, `RevokeOnIssuerRemoved` companion) | 5–6.5 |
| D | Tests (incl. rate-limit + `ListByIssuer` + decorator integration) | 5.5–7.5 |
| E | Consumer migrations (downstream consumers + template/sample sweep) | 3–4 |
| F | Documentation + samples | 3–4 |
| **Total (sequential)** | | **31.5–42** |
| **Total (2 parallel streams once A.1/A.2 land)** | | **~20–27** |

**What's serial vs parallelisable:**
- Strictly serial: A.1 → A.2 → A.3 → A.4 → A.5 → A.6 → A.7 → A.8. The pipeline must compose end-to-end before anything else builds.
- Once A.1/A.2 land: B + D parallelise.
- Once B.3/B.4 land: E + F parallelise with the rest of B.
- Stream C tasks parallelise individually once A.1 is in (C.1 / C.2 / C.3 / C.4 / C.5 / C.6 have no inter-dependencies beyond C.4 blocking C.5).

**Risk-adjusted estimate.** Per-stream estimates above are nominal. Add 20% for unforeseen integration friction (the Fable boundary, cross-package test setup, audit-sink schema-version negotiation, the `INotificationChannel` subscribe-side wiring on `RevokeOnIssuerRemoved`). Risk-adjusted full design: ~38–50 days sequential, ~24–32 parallel.

**Alternative landing shape — Stream C subset.** If the public-flip schedule pins force scope reduction at the last moment, the OQ1/OQ2 substrate (C.3–C.6) is the most-deferrable Stream C content — its consumers are forward-compatible (the deprecated single-policy `RateLimitConfig` shape can coexist with the new shape during a brief overlap; the decorator is opt-in by nature). The migrator (C.1) and audit-sampling (C.2) are tighter to the core change and should land with the rest of the design.

### 5.9 Coordination

The migration produces:

- **Migration doc** at `docs/migrations/0.X.0-platform-mode-to-surfaces.md` (Stream F.4).
- **Per-consumer adoption** — each downstream consumer migrates in its own repo, with one commit per migration.

Concretely: the design doc lands now; the SDK-side commits land next; consumer-side migrations follow.

## Phase 6 — Decision log, glossary, open questions

### 6.1 Decision log

Every non-obvious design decision taken across Phases 2–5, with rationale and the alternatives weighed. Ordered roughly by depth of consequence.

**D1 — Reject the three-axis (Identity × Persistence × Teamship) sketch in favour of Subject-first.**
*Rationale:* the three-axis sketch's 3×2×3 = 18 combinations contain genuine nonsense states (`NoIdentity × Persistent` without a claim; `UserIdentity × SingleTeamFixed` self-contradictory). Subject-first makes impossible states unrepresentable.
*Alternatives weighed:* keep the three axes with documentation discouraging the bad combinations; introduce constraint types that whitelist valid combinations. Both rejected as paper-over-the-cracks.
*Surfaced in:* §2.1, §2.2.

**D2 — `Subject` as a 4-case DU, not a single record with optional fields.**
*Rationale:* a flat `{ UserId: string option; TeamId: string option; ClaimId: string option }` shape pushes the discrimination to runtime null-checks; pattern-matching on the DU lets the compiler verify exhaustiveness at each handler site.
*Alternatives weighed:* `AuthenticatedUser of userId * teamId: string option` (collapses two cases into one with an optional team) — rejected because handlers that genuinely require team scope have to re-check the option, defeating the safety win.
*Surfaced in:* §2.3.

**D3 — `ClaimBearer` promoted to first-class subject.**
*Rationale:* today's `IPublicFormApi` path is the only mixed-mode shape actually shipping. Treating its identity model as a separate subject case (rather than a special-cased exemption) means downstream concerns — storage scope, audit, permissions, observability — reuse the same machinery as the other subjects.
*Alternatives weighed:* keep `ClaimBearer` as a special-case auth scheme that bypasses the Subject model; treat `AttributedHandle` as a flavour of `AuthenticatedUser`. Both rejected — the first reinvents the wheel; the second loses the use-limit / revocation semantics.
*Surfaced in:* §2.3, §2.8.

**D4 — `Surfaces: SurfaceProfile list`, not record-of-options.**
*Rationale:* a record `{ Anonymous: AnonymousConfig option; Team: TeamConfig option; … }` reads at a glance but is verbose to author — every config requires acknowledging the unused shapes.
*Alternatives weighed:* the record form; a set (no order); a fluent builder.
*The list form preserves order* (downstream UI selection, sample-code presentation) and stays compact. Authoring helpers (`Surfaces.individual` = `[SurfaceProfile.individual]`) make single-shape declarations one-line.
*Surfaced in:* §2.4.

**D5 — `SurfaceRequirement` as `Set<SubjectKind>`, not closed-DU.**
*Rationale:* the original sketch's five DU cases (`AnonymousOnly | AuthenticatedAnyShape | …`) are subsets of the same set. Modelling as named subsets gives consumers compositional flexibility (`userOrClaimBearer`) without bloating the DU.
*Alternatives weighed:* closed-DU; predicate function (`Subject -> bool`); a 16-case DU enumerating every subset. All rejected.
*Surfaced in:* §2.5.

**D6 — Collapse `Team` and `MultiTeam` into one `TeamConfig` with a `Switching` field.**
*Rationale:* the two values have identical server-side data models; the distinction is client-UX only. Carrying it as a sub-field of `TeamConfig` puts the UX flag at its honest weight.
*Alternatives weighed:* keep two separate `SurfaceProfile` constructors. Rejected — the substrate would have to dedupe them.
*Surfaced in:* §2.4.

**D7 — `Anonymous` profiles may opt into persistence.**
*Rationale:* long-lived demo surfaces (evergreen tools) want session-keyed data that survives restart. The cost is bounded by `SessionEvictionMinutes`. Default stays `Ephemeral`; opt-in via `Persistence = Persistent`.
*Alternatives weighed:* force-`Ephemeral` for anonymous; require `ClaimBearer` for any persistent anonymous reach. Rejected — adds claim-issuance ceremony to use cases that don't need it.
*Surfaced in:* §3.0 Q1.

**D8 — `ClaimBearer.UserId` derives from `claim.AttributedHandle` when set; synthetic `"claim:" + tokenId` otherwise.**
*Rationale:* preserves audit attribution when the issuer set an `AttributedHandle`; falls back to a privacy-respecting handle that doesn't leak `claim.IssuedBy` to downstream handlers.
*Alternatives weighed:* always use `IssuedBy` (rejected — leaks issuer identity); always synthetic (rejected — loses real attribution); make `AccessContext.UserId` optional (rejected — ripples through every consumer).
*Surfaced in:* §3.0 Q2.

**D9 — Peer-bearer stays separate from the Subject model.**
*Rationale:* peer-bearer is delegated authority (deployment B → deployment A), structurally different from a subject acting on its own behalf. Conflating them obscures the semantic.
*Alternatives weighed:* a fifth `PeerKind` subject case; an `is_peer: bool` field on `AuthenticatedUser`. Both rejected.
*Surfaced in:* §3.0 Q3.

**D10 — `ShareTokenStore` backend choice stays separate from `ClaimBearerConfig`; auto-promotes on surface presence.**
*Rationale:* backend choice (default `BlobShareTokenStore`, future Redis/etc) is an implementation choice distinct from "is claim-bearer supported in this deployment". Keep them as two fields; auto-promote the backend when `ClaimBearer _` is in `Surfaces` to keep single-line authoring working.
*Alternatives weighed:* roll the backend into `ClaimBearerConfig` (rejected — coupling); keep manual two-step (rejected — single-line authoring breaks).
*Surfaced in:* §3.0 Q4.

**D11 — `IAnonymousSessionMigrator` designed in, not deferred to a follow-on.**
*Rationale:* a robustness gap if omitted — the "guest form draft lost on sign-in" class of defect is exactly what mixed-mode invites. Default no-op impl is cheap; opt-in for deployments that care.
*Alternatives weighed:* defer to 0.2.0; require consumer apps to hand-roll. Both rejected — the substrate-level hook is one-shot work and pays back on every mixed-mode deployment.
*Surfaced in:* §3.0 Q5, §3.7.

**D12 — Default `SurfaceRequirement` for handlers without a declaration: `userOrTeam` (strict).**
*Rationale:* fail-closed. A forgotten requirement that admits anonymous is a security hole; one that rejects what should be admitted is a 403 the operator notices immediately.
*Alternatives weighed:* `authenticated` (more permissive — admits claim-bearers by default). Rejected on the same fail-closed reasoning.
*Surfaced in:* §3.0 Q6.

**D13 — Clean cutover, no parallel-old-API window.**
*Rationale:* a deprecation window would let new contributors copy stale patterns from cached search results post-public-flip. One coordinated commit per file is cleaner.
*Alternatives weighed:* type aliases (`PlatformMode = Subject` etc.); deprecation attributes; staged release with both APIs. All rejected.
*Surfaced in:* §5.0, §5.2.

**D14 — Subject-aware `ClientModule.Visibility` per-module, not shell-blanket-filter.**
*Rationale:* fixes the public-utility-with-admin pathology (Anonymous mode hides every module from the sidebar). Each module owns its own visibility per subject kind.
*Alternatives weighed:* keep the blanket shell filter with explicit per-module opt-out; ship a "force-show in anonymous" SDK flag. Both rejected — they patch the symptom; the per-module predicate fixes the root cause.
*Surfaced in:* §3.3, §4.1 E11.

**D15 — `AuditEvent.Subject: AuditSubject` (discriminated), not flat string.**
*Rationale:* downstream sinks query by subject kind; discriminated shape lets Splunk/Datadog write structured queries. Flat string would require parsing.
*Alternatives weighed:* flat string with structured suffix; richer free-form metadata bag. Rejected on query-ability.
*Surfaced in:* §3.6.

**D16 — `IAuditSink.SchemaVersion: int` contract bump (not silent schema change).**
*Rationale:* sinks need to negotiate. A silent schema change risks downstream Splunk dashboards / Datadog alerts breaking with no signal.
*Alternatives weighed:* silent change with a release-notes entry. Rejected — too easy to miss.
*Surfaced in:* §3.6.

**D17 — `AuditSamplingPolicy` as a new `ServerConfig` field, not per-sink.**
*Rationale:* central policy is discoverable and uniform across sinks. A per-sink policy would let sinks disagree about sampling, producing confusing audit gaps.
*Alternatives weighed:* per-sink policy; no sampling primitive at all. The latter rejected because mixed-mode + high anonymous traffic could overwhelm sinks.
*Surfaced in:* §4.2 R6.

**D18 — CSRF carve-out derived per-route from `SurfaceRequirement`, not from a separate prefix list.**
*Rationale:* duplicating the surface declaration in a CSRF-skip list is drift-prone. Derive it.
*Alternatives weighed:* keep the separate list; runtime introspection of route metadata. The derived-from-`SurfaceRequirement` form requires nothing new.
*Surfaced in:* §3.1.

**D19 — `SurfaceCoherenceValidator` is one new validator covering 8 rules; existing 12 validators rewrite their predicates.**
*Rationale:* centralising the coherence checks in one validator gives a single error-message owner; the existing 12 validators' purposes (rate-limit, audit log, OIDC binding, etc.) are unchanged.
*Alternatives weighed:* spread the coherence rules across the 12 existing validators. Rejected — coherence is its own concern.
*Surfaced in:* §3.8.

**D20 — Surface enforcement returns `403 team_required` (with hint) for `UserKind`-but-needs-`TeamMemberKind`, not generic `403`.**
*Rationale:* lets the client UI render an actionable "select a team" panel instead of a generic error. Tiny semantic addition; meaningful UX win.
*Alternatives weighed:* generic 403 for all rejections; richer per-rejection codes. The 7-row table from §3.1 is the chosen middle.
*Surfaced in:* §3.1.

**D21 — Ship per-shape rate-limit primitive with the redesign (not defer).**
*Rationale:* operator override of the recommended-defer call (OQ1). Mixed-mode deployments where anonymous + authenticated subjects share one process need distinct rates per subject kind; deferring forced operators to choose between over-restrictive (defending anonymous) and over-permissive (convenient for authenticated). The new `RateLimitConfig { Default; PerShape }` shape ships with the rest of the design. Single-mode authoring stays one-line via `RateLimitConfig.uniform`. Partition key implied by subject kind — no foot-gun "did I pick the right partition" decision pushed onto operators.
*Alternatives weighed:* defer to a follow-on (recommended call, rejected by operator); explicit per-policy partition keys (rejected — invites mismatch with subject kind); separate top-level fields per kind on `ServerConfig` (rejected — verbose).
*Surfaced in:* §3.10, §6.3 OQ1.

**D22 — Ship `RevokeOnIssuerRemoved` companion (opt-in) with the redesign (not document-only).**
*Rationale:* operator override of the recommended-document-only call (OQ2). Operator's call reflects that the per-deployment decorator-authoring cost (50 lines + tests + INotificationChannel wiring + audit semantics) outweighed the maintenance-surface saving of leaving it to consumer code. Substrate addition: one new method on `IShareTokenStore` (`ListByIssuer`) is needed for the decorator to enumerate the leaver's claims. Contract test pack adds a `ListByIssuer` round-trip case. The decorator itself ships as `src/ShareTokenStoreDecorators/RevokeOnIssuerRemoved/` following the existing flat-companion convention.
*Alternatives weighed:* document the pattern only (recommended call, rejected by operator); maintain an issuer→tokenId index inside the decorator without a substrate change (rejected — needs claim-issuance hooks that don't exist); enumerate all claims and filter (rejected — unbounded scan).
*Surfaced in:* §3.11, §6.3 OQ2.

### 6.2 Glossary

Domain-specific terms used in the document, alphabetised.

- **`AccessContext`** — per-request capability record. Carries the resolved `Subject`, permissions, platform role. Replaces `AccessContext.Mode` with `AccessContext.Subject` under the new model.
- **`AnonymousSession`** — `Subject` constructor for an unauthenticated request scoped by a session id (default: browser tab via `sessionStorage`).
- **`AuthenticatedUser`** — `Subject` constructor for an authenticated user not currently in a team scope (Individual or trial shape).
- **`ClaimBearer`** — `Subject` constructor for a request carrying a validated `ShareTokenClaim`; anonymous reach into a persistent scope, bounded by claim semantics.
- **Container** — the `IBlobStorage` path prefix that namespaces a scope's data: `session-{sid}`, `user-{uid}`, `team-{tid}`, or the claim's `ScopeId`.
- **Ephemeral** — `Persistence = Ephemeral`; data is in-memory only, evicted on a timer.
- **forge** — `toolup-forge`, the OSS SDK (this repo). Apache 2.0; about to flip public.
- **Mixed-mode deployment** — a deployment whose `Surfaces` list contains two or more `SurfaceProfile` entries; serves multiple subject kinds from one process.
- **`PlatformMode`** — the retired enum. Pre-redesign vocabulary; do not reuse in post-design code.
- **Peer-bearer** — the federation auth scheme (`PeerBearerAuthMiddleware`). Orthogonal to the Subject model; stays unchanged.
- **Persistent** — `Persistence = Persistent`; data writes to `IBlobStorage`; survives restart.
- **`ServerConfig.Surfaces`** — the new field replacing `ServerConfig.Mode`; non-empty list of `SurfaceProfile`.
- **`ShareTokenClaim`** — the HMAC-signed envelope describing what a token is good for (scope, resource kind / id, use limit, expiry, attribution).
- **`StorageScope`** — resolved per-request storage location: `{ ScopeId; Container; Persist }`.
- **`Subject`** — the per-request 4-case DU; the new model's pivot.
- **`SubjectKind`** — lightweight tag for declarative use (in `SurfaceRequirement.AcceptedSubjects`); the four constructors of `Subject` map to four kinds.
- **`SurfaceProfile`** — per-shape config carrier in the `Surfaces` list. Four constructors: `Anonymous`, `AuthenticatedUser`, `Team`, `ClaimBearer`.
- **`SurfaceRequirement`** — per-route set of admitted `SubjectKind`s. Module-default + per-endpoint override.
- **`TeamMember`** — `Subject` constructor for an authenticated user acting within a team scope; carries `userId` and `teamId` as separate fields.

### 6.3 Open questions — resolved

All six open questions surfaced in Phases 3–5 were resolved by the operator on 2026-05-26. (The Phase 2 §2.10 set was resolved in §3.0; this set is the second pass.) The original questions and the resolutions, for the record:

**OQ1 — Per-shape rate-limit policies in mixed-mode. — RESOLVED: implement now.**

§4.1 E4 noted that mixed-mode deployments may want different rate limits per subject kind. Today's `RateLimitConfig` is one policy.

*Question:* ship a per-shape primitive now, or defer?

*Recommended call had been:* defer.

*Operator resolution:* implement now. Captured in §3.10 design; reflected in Stream C tasks (C.3) and §5.8 effort. Decision-log entry D21.

**OQ2 — Issuer-removed claim revocation companion. — RESOLVED: ship companion.**

§4.1 E5 noted that share-token claims survive an issuer leaving their team. An `IShareTokenStore` decorator (`RevokeOnIssuerRemoved`) could opt-in revoke.

*Question:* ship the companion, or document only?

*Recommended call had been:* document only.

*Operator resolution:* ship the companion. Captured in §3.11 design; reflected in Stream C tasks (C.4–C.6) and §5.8 effort. Decision-log entry D22. Includes a one-method substrate addition (`IShareTokenStore.ListByIssuer`).

**OQ3 — Handler `match Subject` wildcard linter. — RESOLVED: deferred as a follow-up cleanup item.**

§4.1 E3 flagged dangerous wildcard fallbacks on Subject pattern-matches.

*Operator resolution:* punt (recommended). Track as a follow-up cleanup item post-design; do not block Stream B.

**OQ4 — Auth provider unreachable: warning vs error. — RESOLVED: warning.**

§4.1 E1's `Surfaces = [Anonymous _]` + `withAuth …` case.

*Operator resolution:* warning (recommended). §3.8 rule 7 stays as a warning; development workflow tolerated.

**OQ5 — `MigrationSummary` partial-success representation. — RESOLVED: option (b).**

§3.7's `IAnonymousSessionMigrator.Migrate` partial-success shape.

*Operator resolution:* add `PartialFailure of partial: MigrationSummary * failedItems: int * lastError: string` to `MigrationError` (recommended). Reflected in §3.7 inline; `LastSeenAnonymousSessionId` updates on partial failure to prevent thrash.

**OQ6 — Scheduling. — RESOLVED: schedule now.**

*Operator resolution:* schedule the implementation work as the next action after the design pass closes (recommended).

### 6.4 Closing note

This document is the design pass. It is intended to be self-contained — a future implementer (or AI session) picking it up cold should be able to execute Stream A.1 from §5.1 without needing access to the conversation that produced it. The file:line citations throughout Phase 1 anchor the current-state claims; the F# type sketches in Phases 2–3 are illustrative, not implementation; Phase 5 gives the migration ordering.

Subsequent commits to this file should preserve its phase structure (Phase 1 = current state; Phase 2 = abstraction; etc.) and timestamp any material revisions in the Status line. If the abstraction shape changes after implementation begins, update the document; don't let the design drift away from the code that ships.
