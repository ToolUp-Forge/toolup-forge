# Platform modes

`PlatformMode` controls authentication, data scoping, and persistence across the entire stack. Set the same mode in both `ServerConfig.Mode` and `ClientConfig.Mode`.

| Mode | Auth required | Data scoped to | Persistent | Typical use |
|---|---|---|---|---|
| `Anonymous` | No | Session (per-tab) | No | Dev, demos, public tools |
| `AuthenticatedEphemeral` | Yes | User | No | Trial accounts, compliance-sensitive analysis where nothing should persist |
| `Individual` | Yes | User | Yes | Single-user paid accounts |
| `Team` | Yes | Team | Yes | Multi-user organisations, one team per user (no switcher UI) |
| `MultiTeam` | Yes | Active team | Yes | Users belong to many teams and switch between them in-session |

## How mode flows through the stack

### Server side

`ServerApp.run` does the following based on `ServerConfig.Mode`:

1. Registers the appropriate `IStorageScopeResolver`:
   - `Anonymous` → `AnonymousScopeResolver` (scope per session)
   - `AuthenticatedEphemeral` → `AuthenticatedEphemeralScopeResolver` (scope per user, no persistence)
   - `Individual` → `AuthenticatedScopeResolver` (scope per user, persisted)
   - `Team` / `MultiTeam` → `TeamScopeResolver` (scope per active team, persisted; caches active team lookups with 5-minute sliding expiration)
2. In `Team` / `MultiTeam` mode, also registers `ITeamStore` (default: blob-backed `TeamStore` persisting team metadata and memberships to `_platform` blob container; replaceable for distributed backends).
3. Each request resolves a `StorageScope` (`{ ScopeId; Container; Persist }`) via the scope resolver.
4. `SessionFileStore` uses `scope.Persist` to decide whether to persist files to `IBlobStorage` or keep them in-memory only.
5. `AccessContext` (userId, teamId, mode, permissions, platform role) is resolved per-request via DI.

### Client side

`Client.run` does the following:

1. Calls `UserSession.configure mode` during initialisation.
2. `Anonymous` uses `sessionStorage` for user ID (per-tab, lost on tab close).
3. All other modes use `localStorage` for user ID (persists across tabs / sessions).
4. `Anonymous` attaches `X-User-Id` header to API calls.
5. Authenticated modes attach `Authorization: Bearer <token>` header (falls back to `X-User-Id` until an auth token is available).

## Configuring a mode

Both server and client must agree.

**Recommended (Phase 11.G `fromEnv` helpers):**

```fsharp
// Server — full env-var contract documented per substrate in composition-roots.md.
let logger = ConsoleLogger.fromEnv ()
let config = ServerConfig.fromEnv logger ServerConfigOverrides.referenceApp

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth (AuthProvider.fromEnv logger ToolUp.AuthProviders.OidcAuthProvider.fromConfig)
|> ServerApp.addModules modules
|> ServerApp.run
```

```fsharp
// Client — reads __TOOLUP_MODULE__ + AG_GRID_LICENSE + Clerk key from BundleConstants.
Client.run
    (ClientConfigDefaults.fromBundleConstants {
        ClientConfigOverrides.referenceApp with
            AppName = Some "MyApp"
            Mode = Some Individual
    })
    modules
```

Mode flows through `TOOLUP_PLATFORM_MODE` (`anonymous` / `authephemeral` / `individual` / `team` / `multiteam`) — server reads it via `ServerConfig.fromEnv`. The client opts in to the same mode via `ClientConfigOverrides.Mode = Some Individual` (etc.); the SDK can't read a Vite-time env var into the bundled client, so client-side mode stays declarative.

If server and client disagree on mode, authentication / scope resolution misbehaves silently.

**Hand-rolled (advanced — non-standard env-var schemes, custom dispatch):**

```fsharp
// Server
let config = {
    ServerConfig.defaults with
        Port = 5000
        Mode = Individual   // or Team, AuthenticatedEphemeral, Anonymous
}

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth authProvider
|> ServerApp.addModules modules
|> ServerApp.run
```

Consumers that deviate from the standard `TOOLUP_*` env var contract roll their own dispatch. See [`composition-roots.md`](composition-roots.md) for the helper vs hand-roll choice.

## Auth providers per mode

The `IAuthProvider` interface supplies identity only. The SDK owns permissions and team management.

- **`Anonymous`** — typically no auth provider; the `X-User-Id` header value is taken as identity. The `HeaderAuthProvider` shipped default works for this. Not safe for any deployment exposed beyond a trusted network.
- **`AuthenticatedEphemeral` / `Individual` / `Team` / `MultiTeam`** — needs a real auth provider:
  - `ToolUp.AuthProviders.Oidc` — generic OIDC server-side validator (JWKS discovery, RS256 JWT). Pair with `ToolUp.AuthProviders.Oidc.Client` for the client-side Authorization Code + PKCE flow.
  - `ToolUp.AuthProviders.ClerkUI` — client-side Clerk sign-in UI; the server still validates the bearer token.
  - `StaticJwtAuthProvider` — HS256 JWT validation, BCL-only (no external dependencies). Suitable when JWT issuance is in-house.
  - Custom — implement `IAuthProvider.GetUser` (extract user from request) and `ValidateRequest` (validate or reject).

See [auth.md](auth.md) for the full auth-provider authoring guide.

## Team and MultiTeam mode specifics

`Team` and `MultiTeam` share an identical server-side data model — `team-{teamId}` blob containers, `ITeamStore`, `TeamScopeResolver`, `PlatformApi.SetActiveTeam`. The distinction is **deployment intent + client UX**:

- **`Team`**: one team per user. The client doesn't render a header switcher; admins assign each user's team at sign-up. The server still permits the multi-membership data shape — the single-team convention is enforced by deployment process, not by a guard.
- **`MultiTeam`**: users belong to many teams and switch between them in-session. The shell renders a header dropdown (visible when `MyTeams.Length >= 2`) and runs a `TeamSwitched` reset path on every switch — clears `ModuleStates` / `ModuleConfigs` / `ResolvedFlags` / `AccessibleModules`, refetches them against the new team, and re-inits the active module. KB documents, AI conversations, file lists, sidebar RBAC, and per-team configs all swap to the new team.

Both modes:
- `TeamStore` persists to blob storage: `teams/{teamId}.json`, `memberships/{userId}.json`, `active-team/{userId}.txt`.
- `PlatformApi` (auto-injected) exposes team CRUD: `CreateTeam`, `GetMyTeams`, `AddTeamMember`, `RemoveTeamMember`, `ChangeMemberRole`, `GetTeamMembers`, `SetActiveTeam`, `GetActiveTeam`.
- Role-based access: `Owner`, `Admin`, `Member`. Only Owner / Admin can add / remove members or change roles.
- All file / config / KB / AI-conversation data is scoped to the active team's container (`team-{teamId}`); switching teams shows different data.
- A `Team`-mode user with no active team yet: `GetAccessibleModules` returns `Accessible = []`. Sidebar shows only the auto-injected `TeamManagerUI` until they create or join a team.

### Team-switching reset flow

Triggered by `SetActiveTeam` or by a server-initiated membership change (`MembershipChanged` event, SSE-routed to affected user):

1. Client receives `TeamSwitched (Some newTeamId)` Msg.
2. Shell handler clears `ModuleStates`, `ModuleConfigs`, `PlatformConfig`, `ResolvedFlags`, `AccessibleModules`.
3. `Cmd.batch` runs `loadAccessibleModules` + `loadAllConfigs` + `loadResolvedFlags` + `loadMyTeams` against the new scope.
4. Async config / flag handlers re-init the active module — same path as bootstrap.
5. Each module's `init` fetches its own data fresh against the new scope.

Modules need no special handling — `Map.empty` on `ModuleStates` evicts the prior team's state; re-init runs against an empty slate.

## Anonymous mode caveats

**AI is designed for authenticated platform modes.** Deployments running in `Anonymous` mode (no sign-in, public / demo) typically should NOT enable AI access.

Rationale: LLM API calls cost money per request. Without an authenticated identity, a deployment cannot attribute calls per user, enforce per-user rate limits, or apply per-tenant cost ceilings. A public Anonymous-mode deployment with AI enabled is a wide-open cost surface — anyone with the URL can drive arbitrary token consumption against the deployment's API key.

`AIServerApp.run` does not refuse to start when `ServerConfig.Mode = Anonymous`. Legitimate exceptions exist (single-user local dev, demos with strong network-level rate limiting, BYOK-only deployments where every user supplies their own API key). Deployments choosing to enable AI in Anonymous mode accept the cost-control responsibility and should layer their own rate limiting via `ServerConfig.RateLimit`, IP gating at the proxy, or BYOK-only provider configuration.

## Storage + eviction

- **Persistent modes** (`Individual`, `Team`, `MultiTeam`): files stored in-memory AND persisted to `IBlobStorage` (default `LocalFileStorage` on disk under `data/`). On server restart, persisted files are reloaded via `loadPersistedFiles()`.
- **Ephemeral modes** (`Anonymous`, `AuthenticatedEphemeral`): files in-memory only. Evicted after `storeEvictionMinutes` (default 60 min) by a background timer.

## Mixing platform admin with team scope

`PlatformRole.PlatformAdmin` is a deployment-wide admin role distinct from per-team `TeamRole`. Platform Admins can:
- Manage role assignments via `PlatformAdminApi.AssignPlatformAdmin` / `RevokePlatformAdmin`.
- Write to the Platform Knowledge Base via `IPlatformKnowledgeApi` (the platform-wide RAG-visible content layer).
- Reach the encryption-key destroy endpoint without `TOOLUP_ADMIN_TOKEN`.
- View the deployment-wide health monitor.

Bootstrap: `TOOLUP_INITIAL_PLATFORM_ADMIN=<userId>` assigns the named user as Platform Admin on first startup, only if the admin list is empty. One-shot — once any admin exists, subsequent restarts no-op even with the env var set.

The SDK auto-injects three modules under a "Platform Admin" sidebar group (gated client-side by the user's `PlatformRole`): role management, health monitoring, Platform KB administration. The shell's role filter hides the entire group from non-admin callers.

## Choosing a mode for a new deployment

Quick decision tree:
- **Is the app public / demo / no sign-in?** → `Anonymous`. Avoid enabling AI without per-IP rate limiting.
- **Sign-in but no need to keep user data?** → `AuthenticatedEphemeral`. Trial accounts, compliance-sensitive temporary analysis.
- **Single-user accounts, data persists?** → `Individual`. Personal-finance trackers, training analysis, hobby apps.
- **Multi-user organisations, each user belongs to one team?** → `Team`. Most internal-tool deployments.
- **Users belong to many teams, switch in-session?** → `MultiTeam`. Agency tooling, consultant-with-many-clients shapes.

Modes are not arbitrary tiers — they reflect actual auth + persistence shapes. Picking the wrong one wastes work either way: too restrictive and you fight the SDK; too permissive and you carry unused infrastructure.
