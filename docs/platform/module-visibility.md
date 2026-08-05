# Module visibility

Four mechanisms decide whether a registered module reaches a given user's
sidebar. They are easy to confuse, and picking the wrong one produces a
surface that looks right and is wrong in a way nobody notices until it
matters. This page is the map.

## The four mechanisms

| Mechanism | The question it answers | Authority | Where it is decided |
|---|---|---|---|
| **Visibility profile** | Is this module part of what this deployment *does*? | Operator / team owner, server-side | `IModuleVisibilityStore` → `ModuleVisibilityResolver` |
| **`NavRole`** | May this **role** see it? | Deployment (module author declares the gate) | `SidebarVisibility`, stage 2 |
| **`NeedsData`** | Is there anything to show yet? | The data | The shell, at render |
| **`UserSidebarPreferences`** | Does *this user* want it on their rail? | The user, per browser | `SidebarPreferences` (localStorage) |

The first three are access-shaped and resolve inside
`SidebarVisibility.visible`, which is the single definition site for
"which modules may this caller see" — the sidebar, the command palette,
the administration landing and the client route guard all run it, so a
module hidden from the rail is unreachable by URL by construction. The
fourth is a cosmetic overlay applied strictly afterwards and never
re-admits anything the fold removed.

### Which one do I want?

- *"Only owners should see the billing page."* → **`NavRole`**. It is a
  role statement, and it should hold no matter which team, deployment, or
  user you ask about.
- *"This team bought the analytics add-on, that one didn't."* → **none of
  these.** Entitlements are a separate commercial concern; a visibility
  profile is curation, not licensing, and using it as a licence check
  gives you an enforcement boundary that a determined user can walk
  around by URL unless you also opt into route hardening.
- *"We composed three forecasting modules and this deployment uses one."*
  → **visibility profile**. Exactly the case it exists for.
- *"I never use the audit log and I want it off my sidebar."* → **user
  preference** (the per-entry hide affordance). It changes nothing for
  anyone else and is not an access decision.

## Visibility profiles

A profile is an **allowlist or deny-list over registered module ids**,
stored server-side, resolved per caller, and shipped to the client on the
accessible-modules response.

```fsharp skip=fragment
// Surface only the two modules this deployment actually runs on.
ModuleVisibilityRule.Allow [ "SalesAnalysis"; "Forecast" ]

// Or: everything we compose, minus the one that isn't ready.
ModuleVisibilityRule.Deny [ "ExperimentalPlanner" ]
```

Two shapes rather than one because they say different things about a
module nobody has considered yet. An **allowlist is a commitment** — a
module added to the composition later stays out until someone decides
otherwise. A **deny-list is a subtraction** — a newly-composed module
surfaces by default. Neither answer is universally right, so the choice
is yours to make explicitly.

### Opting in

```fsharp skip=fragment
{ ServerConfig.defaults with
    ModuleVisibility = SurfacingModuleVisibility }
```

| Mode | What you get |
|---|---|
| `NoModuleVisibility` *(default)* | Nothing. No store in DI, no admin API mounted, no profile read, the resolution the client receives is always `None`. An existing deployment is byte-for-byte unchanged and pays nothing. |
| `SurfacingModuleVisibility` | Profiles are stored, resolved and applied to the sidebar, the command palette, the admin landing and the client route guard. Routes are untouched. |
| `EnforcedModuleVisibility` | The above, plus: an `/api/*` request under a route prefix declared by an excluded module is answered **404**. |

### The scope walk

Profiles resolve along the same scope set the feature-flag substrate
uses — `Platform`, `Team`, `User` — but they **compose rather than
override**:

```
Platform profile  →  Team profile  →  User profile
   (ceiling)          (narrows)        (narrows)
```

Every layer may only *remove*. A user-scoped profile cannot re-admit a
module the platform excluded. This is deliberately not the flag walk's
first-hit-wins rule: for a flag, an inner scope overriding an outer one
is the feature (a user opting into a beta the platform left off); for
visibility it would mean the least authoritative layer could widen the
operator's curation.

`ModuleVisibilityResolution.ContributingScopes` names every layer that
actually spoke, so "where did this come from" has an answer.

### Modules a profile never governs

A profile is stated over `ServerConfig.ModuleNames` — the modules the
deployment registers. The SDK's own admin built-ins carry `_sdk.` ids
that are deliberately absent from that list, exactly as they are absent
from the RBAC `Managed` list. The admission test is therefore
**"not governed, or selected"**: an id the profile was never stated over
is admitted unconditionally.

Without that, the first allowlist an operator saved would hide the
Platform Management surface they saved it from.

### Page-level narrowing

`ModuleVisibilityProfile.ExcludedEntryIds` accepts the same composite
sidebar-entry ids the per-user hide uses — a bare module id, or a
`{moduleId}{pageRoute}` page id — so an operator can curate at the
granularity a user already can. Exclusions accumulate across layers and
are never removed by an inner one.

## Route hardening

`EnforcedModuleVisibility` closes the gap where a curated deployment
still answers a bookmark to a module the operator removed from the
surface.

**It is hardening, not the authorization boundary.** The server-side
per-route guards — `SurfaceEnforcementMiddleware`, the per-module
permission guard, the `[<RequiresRole>]` / `[<TenantScoped>]`
classification — remain the enforcement. A profile can only subtract from
what they already permit.

Three things worth knowing before you switch it on:

1. **It answers 404, not 403.** The claim a profile makes is "this module
   is not part of this deployment's surface for you". A 403 would instead
   confirm the module exists and refuse it, which both contradicts the
   claim and hands a prober a module inventory.
2. **It only reaches routes a module declares.** Attribution comes from
   `ServerModule.RoutePrefixes`. A module that declares no prefixes is
   unaffected, because the Remoting route builder names the API *record
   type*, not the module — so its endpoints are indistinguishable at the
   path level from any other module's. If you want a module hardened,
   declare its prefixes:

   ```fsharp skip=fragment
   ServerModule.create "Forecast"
   |> ServerModule.withRoutePrefix "/api/forecast/"
   ```

3. **It costs a profile resolution per attributable request** (up to
   three blob reads). Requests under no declared prefix short-circuit
   before any I/O.

## Administration

The admin surface is `IModuleVisibilityApi`, mounted only when the
deployment opts in:

| Method | Notes |
|---|---|
| `GetResolvedVisibility` | What the server would apply to this caller right now. |
| `ListRegisteredModules` | The editor's candidate list, **derived from the composed module surface** — never a hand-maintained list, so a newly-composed module appears without anyone remembering to add it. |
| `GetProfile` | The profile at the caller's own admin scope. |
| `SetProfile` | Team mode requires Owner/Admin (`TeamRoles.canWriteTeamConfig`). Rejects ids naming no registered module. |
| `ClearProfile` | The layer stops contributing; resolution falls back to the outer scopes. |

No method accepts a scope from the wire — the handler derives it from the
authenticated `AccessContext`, the same posture the feature-flag admin
API takes, and the reason neither carries a scope parameter.

Every save and clear emits a `ModuleVisibilityProfileChanged` event to
`IEventStore` under the profile scope's slug, carrying the scope, the
action, and the user id that made it.

### Rejecting unknown ids

`SetProfile` refuses a rule naming a module the deployment does not
register. A typo'd id in an allowlist is not merely inert — it silently
shrinks the surface by one module and looks exactly like a deliberate
exclusion. `ExcludedEntryIds` is *not* checked, because a composite page
id names a route the server cannot see (pages are declared client-side);
an entry naming nothing is simply inert.

## What a curated-out module looks like to a user

The client route guard renders the SDK's denial view with wording chosen
from `NavigationDenial.NotInVisibilityProfile` — *"This page isn't part
of this workspace"*, not *"you don't have access"*. The distinction
matters: nobody withheld a permission, so sending the user to ask an
admin for one wastes both their time.

An anonymous caller sees `NotSignedIn` instead, as they do for every
denial — naming the specific gate to a caller with no identity narrates
the deployment's shape to the internet for no gain.

## See also

- [`docs/platform/modules.md`](modules.md) — the module contract itself.
- [`docs/platform/command-palette.md`](command-palette.md) — the palette
  derives its candidates from the same fold.
- `src/ToolUp.Platform.Client/Client/SidebarVisibility.fs` — the fold, and
  the reasoning behind its stage order.
- `src/ToolUp.Platform.Tests/InProcess/ModuleVisibilityContractTests.fs` —
  the pinned behaviour, including the cases this page describes in prose.
