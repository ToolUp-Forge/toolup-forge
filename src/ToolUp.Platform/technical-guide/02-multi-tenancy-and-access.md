# ToolUp.Platform Technical Guide — 02. Multi-Tenancy, Teams & Access Control

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 1. Architecture & Composition](01-architecture-and-composition.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 3. Authentication, Secrets & Encryption →](03-authentication-secrets-and-encryption.md)

---

## Platform Modes and Storage Scoping

Platform modes control the entire authentication and data isolation strategy. The mode is set once in both `ServerConfig.Mode` and `ClientConfig.Mode`.

### How scope resolution works

Every request resolves a `StorageScope` via `IStorageScopeResolver`:

```fsharp
type StorageScope = {
    ScopeId: string    // userId, teamId, or sessionId
    Container: string  // "user-abc", "team-xyz", "session-abc"
    Persist: bool      // false for Anonymous and AuthenticatedEphemeral
}

type IStorageScopeResolver =
    abstract Resolve: HttpContext -> StorageScope
```

Four implementations, one per mode:

| Resolver | Auth | Container pattern | Persist |
|----------|------|-------------------|---------|
| `AnonymousScopeResolver` | None (reads `X-User-Id` header) | `session-{sessionId}` | No |
| `AuthenticatedEphemeralScopeResolver` | Required (calls `ValidateRequest`) | `user-{userId}` | No |
| `AuthenticatedScopeResolver` | Required | `user-{userId}` | Yes |
| `TeamScopeResolver` | Required + active team lookup | `team-{teamId}` | Yes |

### Client-side session management

`UserSession.fs` mirrors the mode on the client:

- `Anonymous` mode: generates a session ID in `sessionStorage` (per-tab, lost on close), sends as `X-User-Id` header
- All authenticated modes: stores user ID in `localStorage` (persistent across tabs), sends `Authorization: Bearer <token>` header (with `X-User-Id` fallback until a token is available)

### Storage and eviction

- **Persistent modes** (Individual, Team): files stored in-memory AND persisted to `IBlobStorage`. On server restart, persisted files are reloaded via `loadPersistedFiles()`
- **Ephemeral modes** (Anonymous, AuthenticatedEphemeral): files in-memory only. A background timer evicts stores not accessed within `storeEvictionMinutes` (default 60 min)

## Team Management

Team management is entirely SDK-owned. Auth providers supply identity only — they never need to know about teams, memberships, or permissions.

### Storage layout

All team data lives under the `_platform` blob container:
- `teams/{teamId}.json` — team metadata (`TeamInfo`: teamId, name, createdAt)
- `memberships/{userId}.json` — list of `StoredMembership` records (teamId, role, joinedAt)
- `active-team/{userId}.txt` — plain text teamId of the user's currently selected team

### ITeamStore and TeamStore

`ITeamStore` (`Server/TeamManagement.fs`) is the replaceable backend for team metadata, memberships, and active-team tracking. Mirrors every other core SDK infrastructure interface so distributed deployments (Orleans grain, Akka.Persistence actor, Postgres-backed store, external directory) can drop in without patching consumers. Surface:
- `CreateTeam(teamId, name)` / `GetTeam(teamId)` / `ListTeams()`
- `AddMember(teamId, userId, role)` / `RemoveMember(teamId, userId)` / `ChangeMemberRole(teamId, userId, newRole)` / `GetTeamMembers(teamId)` / `GetMemberRole(teamId, userId)`
- `GetTeamsForUser(userId)` / `GetActiveTeam(userId)` / `SetActiveTeam(userId, teamId)`

All methods are `Async<_>` (GP 12 rule 2); identity is carried by value (GP 12 rule 1).

`TeamStore` is the SDK default implementation, backed by `IBlobStorage` under the `_platform` container. Registered in DI as `ITeamStore` in Team mode. Tests construct `TeamStore` directly and call its public members; production consumers depend on `ITeamStore`.

Removing a member also clears their active team if it was the removed team.

### Platform-level ToolUp.Remoting APIs

The platform exposes five sibling ToolUp.Remoting APIs auto-injected by `ServerApp.run` — originally a single `PlatformApi` umbrella, split for per-concern route prefixes and per-concern test surfaces:

- **`PlatformInfoApi`** — `GetPlatformInfo` returns the current mode and whether auth is required. Always-on; no auth gating because the client shell needs this before deciding whether to render a login affordance.
- **`TeamApi`** — wraps `TeamStore` with role-based access control: `CreateTeam` (caller becomes Owner + active team), `GetMyTeams`, `GetActiveTeam`, `GetTeamMembers` (read-only, all members), `AddTeamMember` / `RemoveTeamMember` / `ChangeMemberRole` (Owner/Admin gated), `SetActiveTeam` (membership-validated; invalidates the `IMemoryCache` entry so `TeamScopeResolver` picks up the change immediately).
- **`PermissionApi`** — `GetTeamPermissions` (Owner/Admin), `SetMemberPermissions`, `SetTeamDefaults`. Teams with no permission document configured behave unrestricted (every member can access every module). Admins use these to opt into RBAC for their team.
- **`AccessibilityApi`** — `GetAccessibleModules` returns the full set of managed modules + the subset the caller can access on their active team. UX filter, not a security boundary; the per-route guard is the actual enforcement.
- **`DataCatalogApi`** — `GetDataCatalog` returns every data type the running platform supports, paired with the producing modules. Surfaces in admin UIs and AI tool discovery.

### TeamScopeResolver caching

`TeamScopeResolver` resolves the active team synchronously (Giraffe handlers are synchronous). It uses `IMemoryCache` with a 5-minute sliding expiration to avoid hitting blob storage on every request. `SetActiveTeam` explicitly invalidates the cache entry.

### Per-userId membership write serialisation

Membership writes (`AddMember`, `RemoveMember`, `ChangeMemberRole`) follow a load-modify-save shape: read the user's `memberships/{userId}.json` blob, edit the in-memory list, write it back. Two concurrent writes for the same `userId` (admin double-submit, two admins inviting in parallel) without serialisation each read the same baseline and the second write loses the first's mutation.

`TeamStore` holds a `ConcurrentDictionary<string, SemaphoreSlim>` keyed by `userId`. Write methods wrap their body in `withUserLock userId (async { ... })` which acquires the per-user semaphore around the read-modify-write cycle. Reads (`GetMemberRole`, `GetTeamsForUser`, `GetTeamMembers`) are not gated — they tolerate concurrent writes seeing partial state.

The dictionary is **unbounded by design**. `userId` cardinality is bounded by registered users; a `SemaphoreSlim(1, 1)` is roughly 200 bytes; a sweeper would add complexity for negligible memory. If `userId` cardinality ever grows unbounded (anonymous-user fan-out at scale), revisit.

**GP 12 caveat — in-process only.** Two app instances sharing the same blob storage cannot coordinate via in-memory semaphores. The cross-instance race is the same lost-update class, just one layer up. The Phase 9c follow-up is `IBlobStorage.UploadIfMatch(etag)` for optimistic concurrency: every membership write reads the etag, mutates, and uploads conditionally; on conflict, retry the read-modify cycle. The semaphore covers single-instance correctness today and disappears cleanly when the etag path lands.

**Cross-user race not covered.** `RemoveMember` and `ChangeMemberRole` both call `IsLastOwner` which scans every membership blob — but this scan does not acquire other users' locks. Two admins simultaneously removing the second-to-last owner of the same team can both see `isLastOwner = false` and both proceed to remove. A team can end up with no owners. This is also a Phase 9c concern — solving it requires coordinating writes across users, which needs distributed primitives.

### Team-switching reset flow

`MultiTeam` mode supports in-session team switching: a single authenticated user is a member of many teams and can switch the active team without re-auth, with the entire UI (files, KB documents, AI conversations, per-team configs, sidebar RBAC) swapping to the new team's data. The same machinery activates in `Team` mode whenever `TeamApi.SetActiveTeam` is called (e.g. from an admin path) — only the header switcher is `MultiTeam`-only.

**Server side** is straightforward: every `/api/*` request resolves a fresh `StorageScope` keyed to the current active team via `ScopeResolutionMiddleware` → `TeamScopeResolver`. `TeamApi.SetActiveTeam` persists the new team to `active-team/{userId}.txt` and invalidates the resolver's 5-min cache via `resolver.InvalidateUser(userId)`. The next request reads the new active team and resolves a `team-{newTeamId}` scope. KB / RAG / config / permissions / AI conversations / file storage all live in the team-scoped blob containers, so no cross-team leakage at the storage layer.

**Client side** propagation goes through three pieces:

1. **`mutable shellDispatch: (Msg -> unit) option`** in `SDK.Client.fs`. Captured once at `init` time via `Cmd.ofEffect (fun dispatch -> shellDispatch <- Some dispatch)` — same pattern the notification subscriber uses. Lets non-shell-update code dispatch shell messages.
2. **`ClientModuleContext.OnTeamSwitched: (string -> unit) option`**. Built by `buildOnTeamSwitched mode`: in team-scoped modes (`Team` / `MultiTeam`) it returns `Some (fun teamId -> shellDispatch |> Option.iter (fun d -> d (TeamSwitched teamId)))`; otherwise `None`. Handed to every module via `ClientModuleContext` at init / re-init.
3. **`Msg.TeamSwitched of string`** handler in the shell. Clears `ModuleStates`, `ModuleConfigs`, `PlatformConfig`, `ResolvedFlags`, `AccessibleModules`; sets `ActiveTeamId = Some newTeamId`; fires `loadAccessibleModules` + `loadAllConfigs` + `loadResolvedFlags` + `loadMyTeams` in `Cmd.batch`. The async `ConfigsLoaded` / `FlagsLoaded` follow-ups re-init the active module — exactly the bootstrap path. No new re-init code; the bootstrap pattern doubles as the switch pattern.

The built-in `TeamManagerUI` invokes `ctx.OnTeamSwitched` from `ActiveTeamSwitched(_, Ok())` and `TeamCreated(Ok _)`. The header switcher dispatches `TeamSwitched` directly after `teamApi.SetActiveTeam` succeeds. Both routes converge on the same shell handler.

**Custom team-management UIs.** Apps that replace `TeamManagerUI` via `ExternalTeamManager` must invoke `ctx.OnTeamSwitched |> Option.iter (fun f -> f teamId)` after a successful switch / create — the built-in pattern. Otherwise the server-side switch persists but the client UI keeps the previous team's data, exactly the bug `MultiTeam` is designed to avoid.

**Module re-init handles the per-module state.** `ModuleStates = Map.empty` evicts the prior team's state across every module. After re-init, each module's `init` runs against the new `ClientModuleContext` and re-fetches its own data: `FileManagerUI.ListFiles`, AI assistant's `ListConversations`, KnowledgeBase's `GetDocuments`, every analytical module's pristine state. `ProcessedDataContext` is re-derived by `computeProcessedData` on the next render against empty module state. No module-level handling required.

**Multi-team header switcher.** The shell's `view` builds a dropdown component when `config.Mode = MultiTeam && model.MyTeams.Length >= 2` and merges it with `chrome.HeaderAction` (apps' own header content). The dropdown renders each team in `MyTeams`; clicking a non-active team calls `teamApi.SetActiveTeam` then dispatches `TeamSwitched`. `MyTeams` is loaded at boot (`MyTeamsLoaded`) and refreshed on `TeamSwitched` so newly-created teams appear in the dropdown without a page reload.

## Access Control

`AccessContext` is resolved per-request as a scoped DI service:

```fsharp
[<RequireQualifiedAccess>]
type ModulePermission =
    | Read
    | Write
    | Admin

type AccessContext = {
    UserId: string
    TeamId: string option
    Mode: PlatformMode
    ModulePermissions: Map<string, ModulePermission list>
    PlatformRole: PlatformRole option
}
```

`ModulePermission` has hierarchy `Admin ⊇ Write ⊇ Read` encoded in `ModulePermission.implies`. `[<RequireQualifiedAccess>]` is mandatory because `Admin` collides with `TeamRole.Admin` (different concept — module perm vs team role).

Helpers:
- `canAccessModule moduleName ctx` — true when `ModulePermissions` is empty (unrestricted default) OR the module is a key in the map with at least one permission.
- `hasPermission moduleName required ctx` — honours the hierarchy: `Admin` satisfies anything, `Write` satisfies `Read` or `Write`, `Read` satisfies only `Read`.

**Enforcement (Phase 4):**
- `makePermissionGuardedApi moduleName api` wraps a module's ToolUp.Remoting handler with a `canAccessModule` check. Denials raise `UnauthorizedAccessException`, translated to HTTP 403 by the error handler.
- `ScopeResolutionMiddleware` loads the user's effective permissions from `IPermissionStore` on every team-scoped request and stashes them in `HttpContext.Items["ToolUp.ModulePermissions"]`. The `AccessContext` DI factory reads from Items synchronously — the async resolution has already run.

#### Async ↔ Task adaptation

`ScopeResolutionMiddleware` runs on every `/api/*` request and calls three `Async<_>`-returning interfaces (`ScopeRequestExtractor.fromHttpContext`, `IStorageScopeResolver.Resolve`, `IPermissionStore.GetEffectivePermissions`). The middleware itself is a `task { }` because ASP.NET Core's pipeline returns `Task`, and each call adapts via:

```fsharp
let runAsync (computation: Async<'a>) : Task<'a> =
    Async.StartImmediateAsTask computation
```

`Async.StartImmediateAsTask` runs the async computation **on the current thread** synchronously up to the first true async point. `Async.StartAsTask` (the previous spelling) instead schedules the computation onto the thread pool, adding a per-request thread-pool round-trip per call. Across three calls per request, that's three context switches that contribute zero work — the cache lookups inside `Resolve` and `GetEffectivePermissions` complete synchronously most of the time, so the round-trip is pure overhead.

The interfaces themselves stay on `Async<_>` (GP 12 rule 2 — async at every boundary). The Task adaptation lives only in the middleware, where ASP.NET Core needs a `Task` either way. Non-ASP.NET consumers (tests, the harness, future hosts) see the `Async<_>` surface unchanged.

#### Bounded parallel team reads

`TeamStore.ListTeams` / `GetTeamsForUser` / `GetTeamMembers` need to download every team / membership blob and filter — there is no team-members index blob today (`team-members/{teamId}.json` would avoid the scan; tracked as a separate optimisation). The reads use `Async.Parallel(comps, maxDegreeOfParallelism = 32)` rather than `Async.Sequential` so the per-blob round-trip latency parallelises. The cap of 32 keeps a 1000-blob list from saturating a cloud backend's connection pool. On the local-filesystem `LocalFileStorage`, parallelism is effectively bounded by disk concurrency anyway; on Azure / S3 / GCS, the cap matters.

**Permission persistence:** `IPermissionStore` + blob-backed `PermissionStore` store one JSON document per team at `_platform/permissions/{teamId}.json`. Contains `Defaults` (team-wide per-module grants) + `Members` (per-user overrides). `GetEffectivePermissions(userId, teamId)` merges — user entries win, defaults apply where absent.

**Permissive default:** empty `ModulePermissions` map means unrestricted. RBAC is opt-in per team — deployments that don't configure permissions preserve the "everyone can use everything" semantic.

## Per-Team Configuration

Configuration is the companion to access control: RBAC decides *who* can see a module, config decides *how* it behaves once visible. Both live under `_platform` blob storage, both are scoped via `AccessContext`, and both are gated through the same ToolUp.Remoting surface with identical role checks.

### Storage layout

Per-scope module config lives under `_platform/config/{scopeId}/{moduleKey}.json`. The filename is deliberately not versioned — writes are last-write-wins; history lives in `IEventStore`, not the config blob. A single JSON object per module keeps the blob count bounded (`O(scopes × modules)`, not `O(fields)`), which matters on S3/GCS where listing is priced per object.

### Schema declaration

Modules declare their config in their shared types (`ConfigTypes.fs` — Fable-compatible so the same schema is reused by the admin UI):

```fsharp
type ConfigFieldKind =
    | Bool
    | Int of min: int option * max: int option
    | Float of min: float option * max: float option
    | String of maxLen: int option
    | Choice of options: string list

type ConfigFieldSchema = {
    Key: string
    DisplayName: string
    Description: string option
    Kind: ConfigFieldKind
    DefaultJson: string
    Required: bool
}

type ModuleConfigSchema = {
    ModuleKey: string
    DisplayName: string
    Description: string option
    Fields: ConfigFieldSchema list
}
```

Values persist as JSON-encoded strings (`"true"`, `42`, `"hello"`). That choice lets the store stay schema-agnostic; validation happens at the handler boundary in `ConfigHandler.fs` where both the schema and the incoming payload are in scope.

### Server flow

1. `ServerConfig.ModuleConfigs: ModuleConfigSchema list` lists app-level schemas; each `ServerModule.withConfig` adds a module-scoped schema. `ServerApp.run` concatenates both into the handler's registry. An empty list is legal; the reserved `_platform` entry is always surfaced by `ListModules` regardless.
2. `ServerApp.run` (and its `AIServerApp` / `RAGServerApp` wrappers) wires `configApiHandler` into the ToolUp.Remoting surface alongside the five platform APIs (`PlatformInfoApi`, `TeamApi`, `PermissionApi`, `AccessibilityApi`, `DataCatalogApi`), `fileManagementApi`, and any AI/RAG companions.
3. Each request reaches the handler with a resolved `AccessContext`. `AccessContext.configScope` returns `Some scopeId` for every non-Anonymous mode; Anonymous requests get `None` and every handler method short-circuits to `Error "Not available in Anonymous mode"`.
4. `SaveModuleConfig` runs the payload through per-field validation (numeric range, string length, choice membership) before calling `IConfigStore.SetValues`. Invalid payloads return `Error` without touching storage.
5. Writes are gated by `TeamRoles.canWriteTeamConfig` — the same predicate that guards `SetMemberPermissions`. Read is available to anyone in the scope.

### Client flow — prefetch and re-init

`SDK.Client.run` owns the shell-level config state and a strict Elmish re-init protocol:

```text
page load ─► prepareModules injects TeamConfigUI ─► init dispatches ConfigsLoaded
   │                                                        │
   │                                                        ▼
   │                            loadAllConfigs ─► for each registered module ─► GetModuleConfig
   │                                                        │
   ▼                                                        ▼
Model.ModuleConfigs : Map<string, Map<string, string>>  ─── merged into Model on ConfigsLoaded
   │
   ▼
ConfigsLoaded ─► evict active module's state ─► re-init with fresh ClientModuleContext
```

`ClientModuleContext` carries `{ ModuleKey; Config; PlatformConfig; User; ... }`. A module that doesn't need config keeps `Init = ClientModule.withUnitInit oldInit` — the adapter drops the context and calls the old `unit -> _ * Cmd<_>` shape. A module that does need config takes the context directly and reads from `ctx.Config`.

**Why re-init and not a `Msg` broadcast?** Module state often mirrors config at boot (`Ledger.Currency` derives from a config field on `Init`). A broadcast would force every module to handle `ConfigChanged` in `update` and rederive state in-place — more surface for subtle bugs than a clean re-init. The downside (transient "Loading…" flash for the active module) is acceptable because config changes are rare.

**Why prefetch all modules instead of lazy-loading?** Cost is one round-trip per module at shell boot, usually <10 calls. Lazy loading would either block module activation (poor UX) or force every `Init` to handle a `None` config gracefully (extra branching in every module). The prefetch pattern also matches how `GetAccessibleModules` already runs at shell boot.

### TeamConfigUI admin form

The built-in admin UI lives in `Client/TeamConfigUI.fs`. It is auto-injected in every non-Anonymous mode via `TeamConfigMode` (mirrors `DataManagerMode` / `TeamManagerMode`). Key implementation notes:

- **Local React state for draft values.** Each field's working value is held in `React.useState`, not the Elmish model. Typing in a text input does not dispatch. Save and Clear are the only entry points to Elmish — matches the pattern documented in `UIToolkit.Forms.Input` and `AIAssistantUI.MessageInput`.
- **Per-field JSON encoding in `jsonFromDisplay`.** Bool gets literal `"true"` / `"false"`; Int/Float pass through as trimmed numeric literals; String/Choice get wrapped via `Fable.Core.JS.JSON.stringify` so quotes and escapes round-trip cleanly.
- **Status banner dismissed via `Cmd`.** `DismissStatus` updates `Model.Status` map; no hidden timeouts. Admins see the "Saved." banner until they dismiss it or switch modules. Prevents the common bug where a banner vanishes mid-read.
- **`[<ReactComponent>]` PascalCase name.** `ModuleForm` must be PascalCase so react-refresh picks up edits during dev without a full reload — a convention enforced by Fable.React's runtime check, not the type system.
- **`Id = "_sdk.TeamConfig"`.** The `_sdk.` prefix is reserved for SDK built-ins; application-owned modules are RBAC-tracked by their own Ids and can never collide. See the "Reserved namespaces" note below.

### Reserved namespaces

Two namespaces are load-bearing and should not be reused by applications:
- `_platform` — reserved for shell/platform-owned config keys (locale, timezone, date format). Always surfaced by `ConfigHandler.ListModules` even when `ServerConfig.ModuleConfigs` is empty.
- `_sdk.*` — reserved for SDK-built-in module Ids (currently `_sdk.TeamConfig`; future built-ins follow the same convention).

An application declaring `ModuleConfigSchema.ModuleKey = "_platform"` will work — the server doesn't prevent it — but overlapping with a future SDK default would silently clobber it. Applications should use their own keys (`app.locale`, `acme.pricing`, etc.) if they want platform-wide defaults.

### Relationship to `IAIConfigResolver`

The AI companion already persists per-user / per-team AI preferences via `IUserAIConfigStore` (Phase 0c). These are distinct surfaces on purpose:
- `IConfigStore` is schema-driven, admin-editable, validated, and surfaces in `TeamConfigUI`. Appropriate for display-facing module behaviour.
- `IUserAIConfigStore` holds opaque API config (provider choice, label-based routing, encrypted API keys). Access-controlled via the Claude/OpenAI companion's own flows.

Apps that want to expose AI preferences in the admin UI register their own `ModuleConfigSchema` and write a bridge that reads `IConfigStore` in their own `SystemPromptBuilder`. `ToolUp.AI` intentionally doesn't couple to `IConfigStore` so deployments can opt out of either surface independently.

## PlatformAdmin profiles

The platform ships a deployment-wide admin surface (`PlatformAdminUI`) gated on `PlatformRole.PlatformAdmin` and `canModifyPlatformConfig`. Two profiles select which widgets it composes — orthogonal to `ClientConfig.PlatformAdmin` (which gates whether the surface appears at all):

```fsharp
type PlatformAdminProfile =
    | StandardPlatformAdminProfile
    | PublicUtilityPlatformAdminProfile
```

| Profile | What it composes | When to use |
|---|---|---|
| `StandardPlatformAdminProfile` (default) | The shipped `PlatformAdmin` tabs — Admins management + Settings (`PlatformKnowledgeBase` toggle, runtime config). | Authenticated multi-tenant apps where the platform-admin job is team/role management + per-deployment configuration. |
| `PublicUtilityPlatformAdminProfile` | Standard tabs **plus** a "Public utility" section with four widgets: Traffic, Rate-limit events, Ad-unit configuration, Premium users. | Anonymous-first deployments running ads + rate-limiting + selective premium upgrades (the public-utility class). |

The two compose: the public-utility profile is additive. Apps that switch from Standard → PublicUtility do not lose any widget; they gain four.

### Server-side endpoints

The public-utility widgets read from three deployment-wide endpoints, all Platform-Admin gated:

| Route | Method(s) | Substrate | Mount condition |
|---|---|---|---|
| `/api/_platform/admin/ad-units[/{slotId}]` | GET / POST / PUT / DELETE | `IEntityStore<AdSlotEntity>` | `ServerConfig.EntityStore = EnabledEntityStore` |
| `/api/_platform/admin/rate-limits` | GET (with `?count=N`) | `IRateLimitStore.GetRecentDecisions` | Always mounted (default `InMemoryRateLimitStore` returns empty list) |
| `/api/_platform/admin/premium-users` | GET | `IUserClaims.ListPremiumUsers` | Always mounted (default `NoOpUserClaims` returns `Ok []`) |

Premium grant / revoke writes use the Phase 62 endpoints (`POST` / `DELETE /api/_platform/users/{userId}/premium`) — the public-utility widget composes against the existing surface rather than duplicating it.

### AdUnit CRUD storage shape

`AdSlotConfig` (in `SDK.Shared.fs`) lacks the `Id` / `Type` / `Version` fields `IEntityStore` requires. `AdUnitConfigApi` wraps it in an internal `AdSlotEntity` record at the persistence boundary — clients see the public `AdSlotConfig` shape; the store sees `AdSlotEntity` keyed on `SlotId`. The entity type is registered lazily on first request because `EntityRegistry.Register` is idempotent under concurrent access. Writes emit `AdSlotConfigCreated` / `AdSlotConfigUpdated` / `AdSlotConfigDeleted` audit events under the `_platform.ads.config` scope.

### Worked example — public-utility deployment

```fsharp
// Client-side composition (Client.fs):
let clientConfig = {
    ClientConfig.defaults with
        Mode = Anonymous
        AdPanel = EnabledAdPanel { DefaultAdClientId = "ca-pub-XXXXXXXX" }
        PremiumModel = AnonymousFirst
        PlatformAdmin = DefaultPlatformAdmin
        PlatformAdminProfile = PublicUtilityPlatformAdminProfile
}

// Server-side composition (Server.fs):
let serverConfig = {
    ServerConfig.defaults with
        Mode = Anonymous
        EntityStore = EnabledEntityStore   // unlocks ad-unit CRUD
        RateLimits = [ /* policies */ ]    // populates the rate-limit event log
        AdAnalytics = EnabledAdAnalytics   // pairs with AdPanel for impression/click capture
        // IUserClaims swapped in for a Clerk-backed provider in production
}
```

An operator with the platform-admin role lands on the `PlatformAdmin` view and sees two standard tabs (Admins + Settings) plus a "Public utility" section with the four widgets. Each widget gracefully degrades when its substrate is absent: `RateLimits = []` shows "no recent decisions", `IUserClaims = NoOpUserClaims` shows "no premium users", `EntityStore = NoEntityStore` 404s the ad-unit CRUD endpoint and the widget surfaces a substrate-stub explaining the missing endpoint.

### Six-rule portability — `IRateLimitStore.GetRecentDecisions`

The Phase 61 read extension to `IRateLimitStore` ([Phase 56 substrate](../../ToolUp.Platform.Server/Server/IRateLimitStore.fs)) honours all six portability rules: identity by value (`InboundRateLimitKey` is a serialisable DU over `string`); async at the boundary; retry / supervision as data (no callbacks; semantic errors flow through `RateLimitStoreError`); stateless between calls (read derives from store state); no cross-shard ordering promise (events return newest-first within the store's retention buffer); precision n/a (the read carries no timing semantics — it returns timestamps the writer set). The same audit is reproduced inline in `RateLimitEventApi.fs` so a future Redis / Azure-Table store implementor can verify compliance at the read-path call site.

---

> [← Prev: 1. Architecture & Composition](01-architecture-and-composition.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 3. Authentication, Secrets & Encryption →](03-authentication-secrets-and-encryption.md)
