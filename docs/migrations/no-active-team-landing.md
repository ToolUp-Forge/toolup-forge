# No-active-team landing gate — `ClientConfig.NoActiveTeamLandingModuleId`

**Forge commits:** `c81ce82` (field + `isAdminSidebarGroup` + shell sidebar filter),
`ed6a47b` (active-content-surface resolution + dev-identity boot-loader gate)

## What changes

A team-scoped deployment can now hold a signed-in user who has **no active team**
on a single landing module until a team is assigned, instead of dropping them into
the normal module set. Purely additive and opt-in: the field defaults to `None`, so
no consumer must act and behaviour is byte-identical when unset.

- **`ClientConfig.NoActiveTeamLandingModuleId: string option`** (default `None`).
  When `Some moduleId` AND the deployment declares a `Team` surface AND the caller
  has no active team (`ActiveTeamId = None` — the resolved `SubjectKind` is
  `UserKind`, the post-sign-in / pre-team-pick window):
  - the sidebar collapses to just the named landing module, and
  - the active **content** surface routes to that module too (not the default
    Home / first module), and
  - for a `PlatformRole.PlatformAdmin` caller, the admin / management sidebar
    groups stay visible (so an admin can still reach the team-assignment tools)
    and remain navigable.

  Once an active team upgrades the subject to `TeamMemberKind`, the gate is inert
  and the user is moved off the landing to the default surface.

- **`ClientConfig.isAdminSidebarGroup: string option -> bool`** — the admin /
  management group set kept visible to a team-less admin under the gate
  (`"Platform Admin"`, `"Platform Management"`, `"Team Management"`).

- **Why a deployment-wide gate and not per-module `Visibility`:** SDK-injected
  built-ins (the Data Manager, Team Manager, Settings) ship
  `Visibility.visibleToAuthenticated`, which admits `UserKind` — so a consumer
  cannot hide them from a no-team caller by editing only its own modules. This is
  a refined, opt-in, admin-aware revival of the Phase 55 team-mode-no-active-team
  blanket-hide.

- **Boot-loader gate now also fires for a configured dev identity.** The shell
  previously skipped the team / role / config boot fetches whenever
  `needsAuth && not hasToken`. A no-JWT header-auth dev build (`DevDefaultUserId`
  set, identity carried via `X-User-Id`) therefore never learned the caller's
  active team, so any team-aware client feature — including this gate — was blind
  in dev. The loaders now run when `DevDefaultUserId` is set, not only when a JWT
  is present. Production (real token) is unchanged.

GP 12 — this is UI shape only; the server-side authorisation classifier
(`[<TenantScoped>]`) and `SurfaceEnforcementMiddleware` remain the authoritative
gate. A no-team caller's tenant-scoped API calls are rejected regardless of the
client surface.

## Diff to apply

**Additive — existing consumers need no change.** A team-scoped deployment that
wants the gate adds a landing module and points the field at it:

```fsharp
open ToolUp.Platform

// 1. A small landing module shown while the caller has no active team.
//    `Visibility.visibleTo [ UserKind ]` hides it once a team is active.
let awaitingTeam : ErasedModule =
    ClientModule.create
        { Init = (fun () -> (), Cmd.none)
          Update = (fun _ m -> m, Cmd.none)
          Name = "Welcome"
          Icon = Icons.home }
    |> ClientModule.withId "AwaitingTeam"
    |> ClientModule.withGroup "Welcome"
    |> ClientModule.withVisibility (Visibility.visibleTo [ UserKind ])
    |> ClientModule.withFullWidthView (fun _ _ -> (* "you're not on a team yet" panel *) Html.none)
    |> ClientModule.register

// 2. Register it alongside the rest, then point the gate at its id.
let clientConfig =
    { ClientConfig.defaults with
        NoActiveTeamLandingModuleId = Some "AwaitingTeam" }
```

The admin-visible-when-no-team set is the SDK's `isAdminSidebarGroup` groups; place
any admin team-assignment surface in one of those groups (the SDK built-ins
already are).

## Verification steps

1. `dotnet build ToolUp.Forge.sln` — additive; build stays green.
2. `cd samples/MinimalClient && dotnet fable -o output --noCache` — Fable client
   tier compiles unchanged.
3. With the field `None` (default), confirm the sidebar + landing surface are
   byte-identical to before.
4. With the field set on a `Team`-surface deployment and **no active team**:
   non-admin sees only the landing (as both sidebar entry and content); admin sees
   the landing plus the admin / management groups and can navigate into them.
5. Assign / select a team → confirm the gate releases: the work modules appear and
   the active surface moves off the landing to the default.
6. In a no-JWT dev build (`DevDefaultUserId` set, header auth), confirm the team /
   role boot fetches now run (so a dev user with a team is **not** mis-gated).

## Rollback

Additive throughout. Revert the two forge commits; consumers that never set
`NoActiveTeamLandingModuleId` are unaffected. The dev-identity boot-loader change
only alters the no-JWT dev path (production always carries a token).
