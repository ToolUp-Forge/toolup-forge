# Surfaces — the auth, scope, and persistence model

A ToolUp deployment serves one or more **subject shapes** — anonymous sessions, authenticated users, team members acting within a team scope, and bearers of validated share tokens. The Platform models this with three terms that appear in every related doc:

- **Subject** — per-request: *who is acting?* The pivot for storage scope, persistence, audit attribution, and permission resolution.
- **`SurfaceProfile`** — per-deployment: *which shapes does this deployment support?* A non-empty list.
- **`SurfaceRequirement`** — per-route: *which subject kinds may reach this surface?* A set.

A single deployment can serve any combination of shapes concurrently. The same process can host a public landing page reachable to anonymous sessions, a private dashboard scoped to team members, and a share-token-gated public-submit endpoint — three different subjects flowing through the same middleware pipeline, each resolved per-request.

This page is the primary mental-model reference. The full design pass lives at [`docs/design/mixed-mode-platform.md`](../design/mixed-mode-platform.md) for readers wanting the why; this page is the what + how.

## The Subject model

`Subject` is a four-case DU resolved per-request by the scope-resolution middleware:

```fsharp
type Subject =
    /// Unauthenticated session-scoped subject. `SessionId` is the
    /// X-User-Id cookie value (or a freshly-generated GUID).
    | AnonymousSession of sessionId: string
    /// Authenticated user without active team scope. The "Individual"
    /// or "trial" shape.
    | AuthenticatedUser of userId: string
    /// Authenticated user acting within a team scope.
    | TeamMember of userId: string * teamId: string
    /// Anonymous reach into a persistent scope, gated by a validated
    /// share-token claim. The claim defines identity (via
    /// `AttributedHandle`) and authority bounds (resource kind, id,
    /// use-limit) together.
    | ClaimBearer of claim: ShareTokenClaim
```

A lightweight kind tag accompanies it for declarative use (surface requirements, sidebar visibility, metrics tagging):

```fsharp
type SubjectKind =
    | AnonymousKind
    | UserKind
    | TeamMemberKind
    | ClaimBearerKind
```

The two reasons this is a 4-case DU rather than a record-of-options:

- **The compiler enforces team-scope at the match site.** A handler that genuinely requires team scope writes `match ctx.Subject with TeamMember (uid, tid) -> …` and the compiler refuses the other three cases. Folding `TeamMember` into `AuthenticatedUser of userId * teamId option` would let careless handlers forget to check the option.
- **`ClaimBearer` is a first-class subject.** A share-token bearer produces a `StorageScope`, an audit-attributable identity, and a bounded permission envelope — exactly what other subjects produce. Modelling it as a fifth pipeline (the pre-66 `IPublicFormApi` shape) duplicates code that the 4-case DU unifies.

`AccessContext` carries the resolved subject per request:

```fsharp
type AccessContext = {
    UserId: string                              // session id / user id / synthetic claim id
    TeamId: string option                       // active team when subject is TeamMember
    Subject: Subject                            // the per-request resolved subject
    ModulePermissions: Map<string, ModulePermission list>
    PlatformRole: PlatformRole option
}

module AccessContext =
    let isAnonymous (ctx: AccessContext) =
        match ctx.Subject with AnonymousSession _ -> true | _ -> false
    let isAuthenticated ctx = not (isAnonymous ctx)
    let inTeamScope (ctx: AccessContext) =
        match ctx.Subject with TeamMember _ -> true | _ -> false
    let isClaimBearer (ctx: AccessContext) =
        match ctx.Subject with ClaimBearer _ -> true | _ -> false
    /// Stable string tag for log / metrics use.
    let kindLabel (ctx: AccessContext) : string =
        match Subject.kind ctx.Subject with
        | AnonymousKind -> "anonymous"
        | UserKind -> "user"
        | TeamMemberKind -> "team"
        | ClaimBearerKind -> "claim-bearer"
```

`AccessContext.UserId` for the four subject kinds: the session id for `AnonymousSession`; the user id for `AuthenticatedUser` and `TeamMember`; the claim's `AttributedHandle` when set, otherwise a synthetic `"claim:" + tokenId`. The synthetic form deliberately does **not** leak the issuer's id to claim-bearer-served handlers.

## Declaring deployment Surfaces

A deployment declares the shapes it supports as a non-empty `SurfaceProfile list` on `ServerConfig`:

```fsharp
type SurfaceProfile =
    | Anonymous of AnonymousConfig
    | AuthenticatedUser of AuthenticatedUserConfig
    | Team of TeamConfig
    | ClaimBearer of ClaimBearerConfig

type ServerConfig = {
    // …existing fields…
    /// Non-empty list of subject shapes this deployment supports.
    /// Each entry wires its own substrate (a `Team _` entry registers
    /// `ITeamStore`; a `ClaimBearer _` entry auto-promotes
    /// `IShareTokenStore` to `BlobShareTokenStore` if unset).
    Surfaces: SurfaceProfile list
}
```

Each profile carries its shape-specific knobs:

| Profile | Knobs |
|---|---|
| `Anonymous` | `SessionEvictionMinutes: int option` (default `Some 60`); `Persistence: Persistence` (default `Ephemeral` — `Persistent` enables long-lived evergreen demos) |
| `AuthenticatedUser` | `Persistence: Persistence` — `Persistent` is the "Individual paid account" shape; `Ephemeral` is the "trial account" shape |
| `Team` | `Persistence: Persistence` (almost always `Persistent`); `Switching: NoSwitcher \| HeaderSwitcher` — `HeaderSwitcher` renders the multi-team header dropdown and runs the `TeamSwitched` reset flow |
| `ClaimBearer` | `DefaultLifetimeDays: int` (default 30); `DefaultUseLimit: int option` (default `Some 1`) |

### One-liner authoring for the common case

Single-shape deployments stay a single line. The `Surfaces` module ships the common forms:

```fsharp
module Surfaces =
    let anonymous = [ SurfaceProfile.anonymous ]
    let trial = [ SurfaceProfile.trial ]
    let individual = [ SurfaceProfile.individual ]
    let team = [ SurfaceProfile.team ]
    let multiTeam = [ SurfaceProfile.multiTeam ]
    let anonymousAndIndividual =
        [ SurfaceProfile.anonymous; SurfaceProfile.individual ]
    let anonymousAndTeam = [ SurfaceProfile.anonymous; SurfaceProfile.team ]
    let teamWithShareTokens =
        [ SurfaceProfile.team; SurfaceProfile.claimBearer ]
```

```fsharp
// Pure-Individual deployment, one line:
{ ServerConfig.defaults with Surfaces = Surfaces.individual }

// Pure-Anonymous deployment, one line:
{ ServerConfig.defaults with Surfaces = Surfaces.anonymous }

// Mixed Anonymous + Individual (public utility with a private admin), one line:
{ ServerConfig.defaults with Surfaces = Surfaces.anonymousAndIndividual }

// Multi-tenant SaaS with public landing + paid team dashboards + share links — three lines:
{ ServerConfig.defaults with
    Surfaces = [
        SurfaceProfile.anonymous
        SurfaceProfile.multiTeam
        SurfaceProfile.claimBearer
    ] }
```

Single-shape stays one line; mixed-shape is N lines where N matches the number of shapes the deployment actually serves, which is honest about what the deployment is doing.

### Env-var contract

The `fromEnv` helpers read the shape from `TOOLUP_PLATFORM_SURFACES`:

```bash
TOOLUP_PLATFORM_SURFACES=individual                       # pure-Individual
TOOLUP_PLATFORM_SURFACES=anonymous                        # pure-Anonymous
TOOLUP_PLATFORM_SURFACES=anonymous,individual             # mixed
TOOLUP_PLATFORM_SURFACES=team,claim_bearer                # team with share links
TOOLUP_PLATFORM_SURFACES=anonymous,multi_team,claim_bearer
```

Accepted tokens: `anonymous` / `anonymous_persistent` / `trial` / `individual` / `team` / `multi_team` / `claim_bearer`. The parser is comma- / semicolon- / space-separated. Unrecognised tokens log a warning listing the valid set and fall back to `ServerConfig.defaults.Surfaces`. The client reads the same shape from the `__TOOLUP_PLATFORM_SURFACES__` Vite define (via `BundleConstants.platformSurfaces`); see [`composition-roots.md`](composition-roots.md) for the full env-var matrix and the `ServerConfigOverrides` precedence rule (an explicit `overrides.Surfaces = Some _` wins over the env var).

### Substrate auto-wiring

Declaring a profile in `Surfaces` wires the substrate that profile needs at composition time:

- `Team _` present → `ITeamStore` is registered (default `BlobTeamStore` persisting team metadata and memberships to the `_platform` blob container).
- `ClaimBearer _` present and no explicit `ShareTokenStore = NoShareTokenStore` → `IShareTokenStore` auto-promotes to `BlobShareTokenStore`. Explicitly setting `NoShareTokenStore` while declaring a `ClaimBearer` surface is refused at startup by `SurfaceCoherenceValidator`.
- `Anonymous _` present → no extra substrate; the SDK's session-keyed in-memory file store already supports it.

Deployments composing only `Anonymous` in `Surfaces` don't need an auth provider — `ServerApp.withAuth` becomes optional. Deployments with any other shape require it; the composition-root validator catches the omission at startup.

## Per-route SurfaceRequirements

A route declares which subject kinds may reach it via `SurfaceRequirement`:

```fsharp
type SurfaceRequirement = {
    /// Which subject kinds may invoke this surface. The middleware
    /// rejects subjects whose `kind` is not in this set.
    AcceptedSubjects: Set<SubjectKind>
}
```

Named helpers cover the common cases:

| Helper | Accepts | Use for |
|---|---|---|
| `SurfaceRequirement.public_` | all four kinds | Landing pages, `/health`, public status boards |
| `SurfaceRequirement.authenticated` | `User` + `TeamMember` + `ClaimBearer` | Any handler that needs *some* authority but treats a share-token bearer as authenticated for its bounded scope |
| `SurfaceRequirement.userOrTeam` | `User` + `TeamMember` | Standard authenticated handlers; the strict global default |
| `SurfaceRequirement.teamScoped` | `TeamMember` only | Team CRUD, team-config, team-data endpoints |
| `SurfaceRequirement.anonymousOnly` | `Anonymous` only | Sign-up flows that should 302 authenticated users elsewhere |
| `SurfaceRequirement.claimBearerOnly` | `ClaimBearer` only | Share-token-gated submit endpoints |

Modules carry a `DefaultSurfaceRequirement`; individual endpoints can override:

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

When no requirement is declared anywhere (no module default, no route override), the platform applies `SurfaceRequirement.userOrTeam`. Fail-closed is the rule — a forgotten declaration produces a 403 the operator notices, not a silent public exposure.

### Client-side `Visibility`

Client modules declare a `Visibility: SubjectKind -> bool` predicate; the shell's sidebar filter hides modules whose predicate returns `false` for the current subject:

```fsharp
module ClientModule =
    let visibleToAuthenticated = function
        | UserKind | TeamMemberKind -> true
        | _ -> false
    let visibleToAll _ = true
    let teamScopedOnly = function TeamMemberKind -> true | _ -> false
```

This solves the public-utility-with-admin sidebar pathology — modules control their own visibility per subject kind rather than the shell applying a blanket "hide everything for anonymous" filter.

### Request resolution flow

```
                ┌────────────────────────────────────┐
                │  ShareTokenAuthMiddleware          │
                │  reads ?token= / X-Share-Token     │
                │  validates via IShareTokenStore    │
                │  stashes claim on HttpContext.Items│
                └─────────────────┬──────────────────┘
                                  ↓
                ┌────────────────────────────────────┐
                │  ScopeResolutionMiddleware         │
                │  calls ISubjectResolver:           │
                │    claim?     → ClaimBearer        │
                │    user+team? → TeamMember         │
                │    user?      → AuthenticatedUser  │
                │    else       → AnonymousSession   │
                │  stashes Subject + StorageScope    │
                └─────────────────┬──────────────────┘
                                  ↓
                ┌────────────────────────────────────┐
                │  SurfaceEnforcementMiddleware      │
                │  looks up the route's              │
                │  SurfaceRequirement, checks the    │
                │  Subject's kind against            │
                │  AcceptedSubjects                  │
                └─────────────────┬──────────────────┘
                                  ↓
                ┌────────────────────────────────────┐
                │  CsrfMiddleware                    │
                │  skips when AcceptedSubjects       │
                │  contains AnonymousKind or         │
                │  ClaimBearerKind (no session       │
                │  to bind a nonce against)          │
                └─────────────────┬──────────────────┘
                                  ↓
                            Handler (ctx.Subject)
```

The middleware response codes:

| Subject kind | Surface accepts? | Response |
|---|---|---|
| `AnonymousKind` | no | **401** `{"error":"authentication_required"}` |
| `UserKind` | yes | 200 (pass) |
| `UserKind` | no — surface needs `TeamMemberKind` | **403** `{"error":"team_required","hint":"select_team"}` |
| `TeamMemberKind` | yes | 200 (pass) |
| `TeamMemberKind` | no — surface is `anonymousOnly` | **403** `{"error":"authenticated_subject_not_admitted"}` |
| `ClaimBearerKind` | yes | 200 (pass) |
| `ClaimBearerKind` | no | **403** `{"error":"claim_bearer_not_admitted"}` |

The `hint: "select_team"` on team-required 403 lets the client UI render an actionable "Select a team to continue" panel instead of a generic error.

### Persistence routing

`StorageScope.Persist` is the per-request lever and falls out of the resolved subject + the matching profile:

| Subject | `ScopeId` | `Container` | `Persist` |
|---|---|---|---|
| `AnonymousSession sid` | `sid` | `session-{sid}` | from `AnonymousConfig.Persistence` (default `Ephemeral`) |
| `AuthenticatedUser uid` | `uid` | `user-{uid}` | from `AuthenticatedUserConfig.Persistence` (`Persistent` for individual; `Ephemeral` for trial) |
| `TeamMember (_, tid)` | `tid` | `team-{tid}` | from `TeamConfig.Persistence` (almost always `Persistent`) |
| `ClaimBearer claim` | `claim.ScopeId` | `claim.ScopeId` | always `true` (a share-link is meaningless against ephemeral storage) |

The session-keyed in-memory file store evicts entries after `AnonymousConfig.SessionEvictionMinutes` (default 60). Persistent containers stream through `IBlobStorage` (default `LocalFileStorage` on disk under `data/`) and reload on restart via `loadPersistedFiles()`.

## Mixed-mode deployment archetypes

Five archetypes drawn from real consumer deployment shapes. Each is a complete authoring example.

### Pure-Individual internal-tools deployment

Authenticated multi-module tool, no public surfaces. The most common shape.

```fsharp
let config = ServerConfig.fromEnv logger ServerConfigOverrides.referenceApp
// referenceApp declares Surfaces = Some Surfaces.individual

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth (AuthProvider.fromEnv logger OidcAuthProvider.fromConfig)
|> ServerApp.addModules modules
|> ServerApp.run
```

`TOOLUP_PLATFORM_SURFACES=individual`. Every route inherits the module's default `userOrTeam` requirement; every client module declares `visibleToAuthenticated`.

### Federation deployment pair (two-app)

Two cooperating deployments addressing each other via peer-bearer routes. Surface model is `Surfaces.individual` on each side; peer-bearer authentication is orthogonal to the Subject model — peer requests carry delegated authority from another deployment, not "a subject acting on its own behalf", and so flow through the `PeerBearerAuthMiddleware` pipeline unchanged.

```fsharp
let config = {
    ServerConfig.defaults with
        Surfaces = Surfaces.individual
        AcceptHeaderAuthWhenAuthRequired = true       // localhost-testing waivers
        AcceptPlaintextSecretsWhenAuthRequired = true
        AcceptQueryParamSseAuthWhenAuthRequired = true
        PeerRoutePrefixes = [ "/api/peer/" ]
}

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth authProvider
|> ServerApp.withRegisteredPeers peers
|> ServerApp.addModules modules
|> ServerApp.run
```

`SurfaceCoherenceValidator` knows to skip routes registered under `PeerRoutePrefixes` — they don't traverse `SurfaceEnforcementMiddleware`, so they don't need a `SurfaceRequirement`.

### Pure-Anonymous public portal

Single module, minimal composition, fully public.

```fsharp
let config = { ServerConfig.defaults with Surfaces = Surfaces.anonymous }

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.addModules [ singleModule.serverModule ]
|> ServerApp.run
```

Each module declares `DefaultSurfaceRequirement = SurfaceRequirement.public_` server-side and `Visibility = visibleToAll` client-side. No `withAuth` needed (`Anonymous`-only deployments make `withAuth` optional). `TOOLUP_PLATFORM_SURFACES=anonymous`.

Operators enabling AI on a pure-Anonymous deployment own the cost-control surface — see [Mixed-mode threat surface](security.md) for the rate-limit, BYOK, and per-IP-gating guidance.

### Public-utility-with-admin

The shape that motivates this whole redesign: a public calculator / lookup tool with a small private admin surface in the same process.

```fsharp
let config = {
    ServerConfig.defaults with
        Surfaces = Surfaces.anonymousAndIndividual
}

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth authProvider
|> ServerApp.addModules modules
|> ServerApp.run
```

Calculator modules:
- Server: `DefaultSurfaceRequirement = SurfaceRequirement.public_`
- Client: `Visibility = visibleToAll`

Admin / config modules:
- Server: inherits the module default `userOrTeam`
- Client: `Visibility = visibleToAuthenticated`

Anonymous visitors see only the calculator pages in the sidebar; signed-in admins see calculators plus admin pages. Sign-in lifts the visitor from `AnonymousSession` to `AuthenticatedUser` mid-session; an optional `IAnonymousSessionMigrator` can carry guest-built calculator state across the transition.

### Public landing + team SaaS + share links

The full mixed-mode case — three concurrent shapes in one process.

```fsharp
let config = {
    ServerConfig.defaults with
        Surfaces = [
            SurfaceProfile.anonymous
            SurfaceProfile.multiTeam
            SurfaceProfile.claimBearer
        ]
}

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth authProvider
|> ServerApp.withSubjectMigrator FormDraftMigrator.instance   // optional
|> ServerApp.addModules modules
|> ServerApp.run
```

- Anonymous visitors reach the landing module (`SurfaceRequirement.public_`).
- Signed-in team members reach the team dashboard (`SurfaceRequirement.userOrTeam` / `teamScoped`).
- Share-token bearers reach `/api/forms/public/submit` and `/r/{token}` (`SurfaceRequirement.claimBearerOnly`).

The header team-switcher renders for `TeamMember` subjects only (`MultiTeam` `Switching = HeaderSwitcher`); guest drafts migrate into the new team's container on sign-in via the optional `FormDraftMigrator`.

## Old → new mapping

For readers familiar with the retired `PlatformMode` enum, the migration is mechanical. The full migration guide for downstream consumers lives at [`docs/migrations/0.X.0-platform-mode-to-surfaces.md`](../migrations/0.X.0-platform-mode-to-surfaces.md); the table below is the at-a-glance form.

### `PlatformMode` values

| Old `PlatformMode` | New `Surfaces` helper | Notes |
|---|---|---|
| `Anonymous` | `Surfaces.anonymous` | `SurfaceProfile.anonymous` carries the default `SessionEvictionMinutes = Some 60` |
| `AuthenticatedEphemeral` | `Surfaces.trial` | `SurfaceProfile.trial` = `AuthenticatedUser { Persistence = Ephemeral }` |
| `Individual` | `Surfaces.individual` | `SurfaceProfile.individual` = `AuthenticatedUser { Persistence = Persistent }` |
| `Team` | `Surfaces.team` | `SurfaceProfile.team` = `Team { Persistence = Persistent; Switching = NoSwitcher }` |
| `MultiTeam` | `Surfaces.multiTeam` | `SurfaceProfile.multiTeam` = `Team { Persistence = Persistent; Switching = HeaderSwitcher }` |

### `ServerConfig` fields

| Old field | New field / replacement |
|---|---|
| `Mode: PlatformMode` | `Surfaces: SurfaceProfile list` |
| `AnonymousRoutePrefixes: string list` | per-route `SurfaceRequirement.claimBearerOnly` / `.anonymousOnly` / `.public_` declarations on the route handlers |
| `AcceptShallowAnonymousRoutePrefix: bool` | dropped (the prefix-list validator retires; coherence is checked declaratively at startup) |
| `AcceptHeaderAuthInAuthenticatedMode` | `AcceptHeaderAuthWhenAuthRequired` |
| `AcceptPlaintextSecretsInAuthenticatedMode` | `AcceptPlaintextSecretsWhenAuthRequired` |
| `AcceptNoRateLimitInAuthenticatedMode` | `AcceptNoRateLimitWhenAuthRequired` |
| `AcceptQueryParamSseAuthInAuthenticatedMode` | `AcceptQueryParamSseAuthWhenAuthRequired` |
| `AcceptUnboundAudienceInAuthenticatedMode` | `AcceptUnboundAudienceWhenAuthRequired` |

### `AccessContext`

| Old | New |
|---|---|
| `ctx.Mode = Anonymous` | `AccessContext.isAnonymous ctx` |
| `ctx.Mode \|> PlatformMode.requiresAuth` | `AccessContext.isAuthenticated ctx` |
| `ctx.Mode = Team \|\| ctx.Mode = MultiTeam` | `AccessContext.inTeamScope ctx` |
| `ctx.Mode \|> string \|> _.ToLower()` (for log tagging) | `AccessContext.kindLabel ctx` |
| `match ctx.Mode with Team \| MultiTeam -> match ctx.TeamId with Some t \| None` | `match ctx.Subject with TeamMember (uid, tid) -> …` (the compiler enforces the team) |

### Composition root helpers

| Old | New |
|---|---|
| `PlatformMode.requiresAuth config.Mode` | `DeploymentConfig.requiresAnyAuth config` |
| `PlatformMode.isTeamScoped config.Mode` | `DeploymentConfig.hasTeamScope config` |
| _(no equivalent)_ | `DeploymentConfig.hasClaimBearer config` |
| _(no equivalent)_ | `DeploymentConfig.hasAnonymous config` |
| `IStorageScopeResolver` 5-arm dispatch | single `ISubjectResolver` interface (Default: `DefaultSubjectResolver`) |

### Env-var contract

| Old | New |
|---|---|
| `TOOLUP_PLATFORM_MODE=anonymous` | `TOOLUP_PLATFORM_SURFACES=anonymous` |
| `TOOLUP_PLATFORM_MODE=authephemeral` | `TOOLUP_PLATFORM_SURFACES=trial` |
| `TOOLUP_PLATFORM_MODE=individual` | `TOOLUP_PLATFORM_SURFACES=individual` |
| `TOOLUP_PLATFORM_MODE=team` | `TOOLUP_PLATFORM_SURFACES=team` |
| `TOOLUP_PLATFORM_MODE=multiteam` | `TOOLUP_PLATFORM_SURFACES=multi_team` |
| _(no mixed-mode form)_ | `TOOLUP_PLATFORM_SURFACES=anonymous,individual` etc. |

`TOOLUP_PLATFORM_MODE` is retired with no aliasing — the migration is a clean cutover, not a parallel-old-API window.

### Client side

| Old | New |
|---|---|
| `ClientConfigOverrides.Mode: PlatformMode option` | `ClientConfigOverrides.Surfaces: SurfaceProfile list option` |
| `__TOOLUP_PLATFORM_MODE__` Vite define | `__TOOLUP_PLATFORM_SURFACES__` Vite define |
| `BundleConstants.platformMode` accessor | `BundleConstants.platformSurfaces` accessor |
| Per-deployment `UserSession.configure mode` | Per-request `UserSession.configure (Subject.kind ctx.Subject)` — `sessionStorage` for `AnonymousKind`, `localStorage` otherwise |

### Module-level

| Old | New |
|---|---|
| `IPublicFormApi` "exception to the auth pipeline" | regular routes carrying `SurfaceRequirement.claimBearerOnly` |
| Shell sidebar's `Mode = Anonymous`-conditional filter | per-module `Visibility: SubjectKind -> bool` predicate |
| `ServerConfig.ShareTokenStore = EnabledShareTokenStore` opt-in | declaring `ClaimBearer _` in `Surfaces` auto-promotes `BlobShareTokenStore` |

## Choosing a Surfaces shape for a new deployment

A short decision tree:

- **Public, no sign-in?** → `Surfaces.anonymous`. AI requires explicit cost-control plumbing — see [Mixed-mode threat surface](security.md).
- **Sign-in, single-user accounts, persistent data?** → `Surfaces.individual`.
- **Sign-in, single-user accounts, ephemeral storage (trial / compliance-sensitive)?** → `Surfaces.trial`.
- **Multi-user organisations, one team per user, no switcher UI?** → `Surfaces.team`.
- **Multi-user organisations, users in many teams, switch in-session?** → `Surfaces.multiTeam`.
- **Public surfaces alongside a private admin / dashboard?** → `Surfaces.anonymousAndIndividual` (or `.anonymousAndTeam`).
- **Public landing + paid team SaaS + share-token-gated submissions?** → `[anonymous; multiTeam; claimBearer]`.
- **Federation deployment pair?** → `Surfaces.individual` on each side, plus the unchanged `PeerRoutePrefixes` / `RegisteredPeer` wiring.

Shapes are not arbitrary tiers — they reflect actual auth + persistence boundaries. Pick the smallest set that honestly covers the surfaces this deployment serves; mixed-mode is no more first-class than single-shape, but it's no less either.

## Related references

- [`architecture.md`](architecture.md) — composition roots, the request pipeline, how modules plug in.
- [`auth.md`](auth.md) — `IAuthProvider` authoring, the request-resolution flow per subject kind.
- [`storage.md`](storage.md) — `StorageScope` flow under mixed-mode.
- [`composition-roots.md`](composition-roots.md) — the `fromEnv` helpers, the env-var matrix, and the override precedence rule.
- [`modules.md`](modules.md) — module API including `DefaultSurfaceRequirement` and the client-side `Visibility` predicate.
- [`portability-rules.md`](portability-rules.md) — `ISubjectResolver` and `IShareTokenStore.ListByIssuer` against the six rules.
- [`security.md`](security.md) — the "Mixed-mode threat surface" section (per-shape rate-limit guidance, AI-cost-ceiling considerations).
- [`docs/migrations/0.X.0-platform-mode-to-surfaces.md`](../migrations/0.X.0-platform-mode-to-surfaces.md) — the consumer migration guide (pattern-match rewrite examples, field renames, validator escape-hatch renames).
- [`docs/design/mixed-mode-platform.md`](../design/mixed-mode-platform.md) — the full design pass: decision log, edge cases, risk analysis, the per-shape rate-limit and `RevokeOnIssuerRemoved` companion designs.
