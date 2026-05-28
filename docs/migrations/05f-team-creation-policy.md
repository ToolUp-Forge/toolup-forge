# Phase 5f — Team creation policy + bootstrap team (consumer migration)

**What changes.** SDK adds `ServerConfig.TeamCreationPolicy` (DU; default `PlatformAdminOnly`) and gates `TeamApi.CreateTeam` in `Team` / `MultiTeam` mode on `IPlatformAdminStore.IsAdmin`. The pre-5f shape (any authenticated user may create a team and auto-Owner it) is preserved as the explicit opt-out `TeamCreationPolicy = AnyAuthenticatedUser`. A new `BootstrapTeam` first-boot module + env-var pair (`TOOLUP_INITIAL_TEAM_NAME`, optional `TOOLUP_INITIAL_TEAM_ID`) seeds the initial team so a closed-roster deployment isn't wedged at first boot. Two preflight validators refuse to start a deployment whose env / config combination would produce no usable team-creation path.

**Scope.** Affects only deployments running `Mode = Team` or `Mode = MultiTeam`. The Anonymous / AuthenticatedEphemeral / Individual modes don't register `ITeamStore`, so `CreateTeam` was already an `Error "Team management not available in this mode"` there; nothing changes for those modes.

**Default flip.** Pre-1.0 default change, policy-compliant per the workspace SemVer-on-0.x rule. Consumers that want the pre-5f open-membership shape add one line to their composition root (see "Opt-out" below).

## Behavioural diff

| Surface | Pre-5f | Post-5f (default) | Post-5f (opt-out) |
|---|---|---|---|
| Any authenticated user calls `CreateTeam` in Team / MultiTeam mode | `Ok team` (caller becomes Owner) | `Error "Team creation requires Platform Admin"` + audit `TeamCreationDenied` | `Ok team` (unchanged) |
| Platform Admin calls `CreateTeam` | `Ok team` | `Ok team` | `Ok team` |
| `TeamManagerUI` Create form | always rendered | hidden for non-admin callers | always rendered |
| `TeamApi.GetTeamCreationPolicy` | (didn't exist) | returns `PlatformAdminOnly` | returns `AnyAuthenticatedUser` |
| Startup with empty admin store + no `TOOLUP_INITIAL_PLATFORM_ADMIN` in Team mode | boots | `TeamCreationPolicyValidator` aborts startup | boots (validator inert under `AnyAuthenticatedUser`) |
| Startup with `TOOLUP_INITIAL_TEAM_NAME` set + `TOOLUP_INITIAL_PLATFORM_ADMIN` unset | boots, env vars ignored | `BootstrapTeamValidator` aborts startup | same — validator is independent of the policy field |

## What consumers must do

### 1. Single-tenant deployments running Team / MultiTeam mode (recommended)

Add `TOOLUP_INITIAL_PLATFORM_ADMIN=<userId>` to the deployment environment if not already set, and optionally `TOOLUP_INITIAL_TEAM_NAME=<displayName>` to seed the first team at boot. The bootstrap-admin path was already supported via `PlatformAdminStore.bootstrap` (Phase 4b); Phase 5f reuses it.

```bash
export TOOLUP_INITIAL_PLATFORM_ADMIN="auth0|6612a0..."
export TOOLUP_INITIAL_TEAM_NAME="Acme"
# optional — auto-generated 8-char hex if omitted
export TOOLUP_INITIAL_TEAM_ID="acme0001"
```

On first boot:
1. `PlatformAdminStore.bootstrap` assigns the named user as the initial admin.
2. `BootstrapTeam.bootstrap` creates the team, adds the admin as Owner, and sets the admin's active team.
3. The named admin signs in, lands as Owner of the bootstrap team, can `CreateTeam` to create more teams.

Idempotent: subsequent restarts with the env vars still set no-op (the admin list / team store is no longer empty). Wipe blob storage and restart to re-bootstrap.

### 2. Open-membership deployments (community tools, signup-forward SaaS)

Pin the pre-5f shape:

```fsharp
let serverConfig = {
    ServerConfig.defaults with
        Mode = Team
        TeamCreationPolicy = AnyAuthenticatedUser
        // ... other deployment settings
}
```

That's the entire migration. The validators are inert under `AnyAuthenticatedUser`; no env vars need setting.

### 3. Multi-tenant SaaS (case-by-case)

Decide whether tenants self-provision (`AnyAuthenticatedUser`) or are operator-onboarded (`PlatformAdminOnly`). If the latter, set `TOOLUP_INITIAL_PLATFORM_ADMIN` for the operator account and skip `TOOLUP_INITIAL_TEAM_NAME` (each tenant gets its own team created via the admin UI, not at first boot).

## Preflight messages and how to satisfy them

### `TeamCreationPolicyValidator` (`team-creation-policy-wedge`)

```
Mode = Team with TeamCreationPolicy = PlatformAdminOnly, but the Platform Admin list is empty and TOOLUP_INITIAL_PLATFORM_ADMIN is unset. No caller can satisfy the CreateTeam gate, so the deployment will boot into an unusable state (every Team-mode user lands on an empty shell with no way to create a team). Fix: set TOOLUP_INITIAL_PLATFORM_ADMIN=<userId> so the first admin is seeded automatically (recommended, secure default), or set TeamCreationPolicy = AnyAuthenticatedUser on ServerConfig (preserves the pre-5f open-membership shape).
```

**Fix path A (recommended):** export `TOOLUP_INITIAL_PLATFORM_ADMIN=<userId>` and restart.
**Fix path B:** set `TeamCreationPolicy = AnyAuthenticatedUser` on `ServerConfig` and restart.

### `BootstrapTeamValidator` (`bootstrap-team-env-pair`)

```
TOOLUP_INITIAL_TEAM_NAME = 'Acme' is set but TOOLUP_INITIAL_PLATFORM_ADMIN is unset. BootstrapTeam needs both: the team-name env var tells it which team to create, and the admin env var tells it which user to add as Owner. A team without an Owner is unmanageable, so the bootstrap path refuses to create one. Fix: set TOOLUP_INITIAL_PLATFORM_ADMIN=<userId>, or unset TOOLUP_INITIAL_TEAM_NAME to skip the bootstrap entirely.
```

**Fix path A:** export `TOOLUP_INITIAL_PLATFORM_ADMIN=<userId>` and restart.
**Fix path B:** unset `TOOLUP_INITIAL_TEAM_NAME` and restart (skip the bootstrap).

Both validators respect `ServerConfig.SkipPreflight = true` (escape hatch). Skipping is not recommended — both surfaces represent deployment-time configuration mistakes the operator wants to know about, not transient infrastructure outages.

## Audit trail

A non-admin `CreateTeam` deny emits `TeamCreationDenied` under the `_platform` scope:

```json
{
  "EventType": "TeamCreationDenied",
  "SourceModule": "_platform.audit",
  "Payload": {
    "UserId": "auth0|abc",
    "AttemptedName": "Marketing"
  }
}
```

The audit fires before any team id is minted (the gate short-circuits), so the deny path produces no team-creation side effect. Repeated denials from one actor are a clear refusal-trail signal.

The `BootstrapTeam` path emits the standard `TeamCreated` + `MemberAdded` audit pair with `Actor = "_bootstrap"` — matches the `PlatformAdminAssigned` bootstrap actor convention from Phase 4b.

## Rollback

The default flip is the only behaviour change; one-line revert:

```fsharp
let serverConfig = {
    ServerConfig.defaults with
        Mode = Team
        TeamCreationPolicy = AnyAuthenticatedUser   // pin pre-5f shape
}
```

The `BootstrapTeam` substrate is opt-in (fires only when the env var is set), so unsetting `TOOLUP_INITIAL_TEAM_NAME` skips the bootstrap path entirely. The two preflight validators are inert under `AnyAuthenticatedUser` policy + unset env vars; no further action needed.

The audit case `TeamCreationDenied` and the API method `TeamApi.GetTeamCreationPolicy` are additive — they remain on the wire surface but go quiet under the open-membership shape.

## Files touched (forge SDK side)

- `src/ToolUp.Platform.Core/Shared/Types/TeamTypes.fs` — `TeamCreationPolicy` DU + `TeamApi.GetTeamCreationPolicy`.
- `src/ToolUp.Platform.Core/Shared/SDK.Shared.fs` — `ServerConfig.TeamCreationPolicy` field + default.
- `src/ToolUp.Platform.Core/Shared/AuditTypes.fs` — `TeamCreationDeniedPayload` + `AuditEvent.TeamCreationDenied`.
- `src/ToolUp.Platform.Server/Server/AuditLog.fs` — serialise / decode the new case.
- `src/ToolUp.Platform.Server/Server/PlatformApiHandler.fs` — `CreateTeam` gate + `GetTeamCreationPolicy` handler.
- `src/ToolUp.Platform.Server/Server/Infra/BootstrapTeam.fs` — **new**: first-boot bootstrap module.
- `src/ToolUp.Platform.Server/Server/TeamCreationPolicyValidator.fs` — **new**: wedge-detector validator.
- `src/ToolUp.Platform.Server/Server/BootstrapTeamValidator.fs` — **new**: env-pair-coherence validator.
- `src/ToolUp.Platform.Server/Server/SDK.Server.fs` — wire `BootstrapTeam.bootstrap` + register both new validators.
- `src/ToolUp.Platform.Client/Client/TeamManagerUI.fs` — load policy + admin status, gate the Create form.
- `src/ToolUp.Platform.Tests/InProcess/TeamCreationPolicyTests.fs` — **new**: contract tests.

## Verification steps

Per consumer adopting Phase 5f:

1. **Build:** `dotnet build` clean.
2. **Tests:** `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj --no-build -- --filter-test-list "Phase 5f"` — all 20 tests pass.
3. **Boot diff (Team / MultiTeam consumer):**
    - Set `TOOLUP_INITIAL_PLATFORM_ADMIN=<your-sub>` + `TOOLUP_INITIAL_TEAM_NAME=<name>`.
    - Start the server; confirm the startup log carries `[BootstrapTeam] Bootstrapped team '<name>' (id …) with Owner <your-sub>`.
    - Sign in as `<your-sub>`; confirm `TeamManagerUI` lists the bootstrap team as your active team.
    - Sign in as a different (non-admin) user; confirm `TeamManagerUI` renders the team list without a Create form, and a direct `CreateTeam` call returns `Error "Team creation requires Platform Admin"`.
4. **Preflight refusal:**
    - Start a Team-mode server with `TOOLUP_INITIAL_PLATFORM_ADMIN` unset (and default policy `PlatformAdminOnly`); confirm startup aborts with the `team-creation-policy-wedge` Error.
    - Start with `TOOLUP_INITIAL_TEAM_NAME` set but `TOOLUP_INITIAL_PLATFORM_ADMIN` unset; confirm startup aborts with the `bootstrap-team-env-pair` Error.
5. **Open-membership opt-out:**
    - Set `ServerConfig.TeamCreationPolicy = AnyAuthenticatedUser`.
    - Confirm pre-5f behaviour preserved: any authenticated user can `CreateTeam` and lands as Owner.
