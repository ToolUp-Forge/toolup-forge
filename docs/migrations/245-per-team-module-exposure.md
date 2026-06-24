# Phase 245 — Explicit per-team module exposure (sidebar visibility) + admin-respects-team-config

**Ships in:** `ToolUp.Platform.Core` (`TeamPermissions`, `AccessContext`, `PermissionApi`),
`ToolUp.Platform.Server` (`IPermissionStore`, `PermissionStore`, `computeAccessibleModules`,
scope-resolution middleware), `ToolUp.Platform.Client` (`PermissionsAdminUI`).

## What changes

Per-team module visibility becomes an explicit, durable **exposure** axis, orthogonal to the
RBAC permission level, and platform admins now **respect** a team's exposure when acting on that
team.

Two motivating defects in the prior model:

1. **Admins bypassed the filter.** `computeAccessibleModules` short-circuited to the full module
   list for platform admins *before* the per-module filter ran — so an operator (necessarily a
   platform admin) who hid a module on a team and viewed their own sidebar still saw it, and
   concluded the feature was broken.
2. **Overloaded semantics.** "Empty `ModulePermissions` map ⇒ unrestricted" + "no access ⇒ module
   absent from a non-empty map" made hiding a module an emergent property of map-emptiness rather
   than a deliberate, legible toggle.

### New surface

- `TeamPermissions` gains `Hidden: Set<string>` — module Ids deliberately hidden from this team's
  sidebar. **Absence ⇒ exposed** (the default).
- `AccessContext` gains `HiddenModules: Set<string>` (loaded for `TeamMember` subjects; empty
  otherwise) and a helper `AccessContext.isModuleExposed`.
- `PermissionApi` gains `SetModuleExposure: teamId * moduleId * exposed -> Async<Result<unit,string>>`.
- `IPermissionStore` gains `GetHiddenModules` + `SetModuleExposure`.
- `PermissionsAdminUI`'s **Modules** tab gains an "Expose in team" toggle and relabels the old
  bare "(no access)" cell.

### Behaviour

- **Sidebar visibility** now requires *exposed* **and** *permitted*: a hidden module disappears for
  every team member regardless of permission level. Exposure is a navigation concern — the per-route
  permission guard (`canAccessModule` / `hasPermission`) remains the authorization boundary, so a
  hidden module's API routes are still governed by permission, not by exposure.
- **Platform admins** keep full *permission* visibility (no RBAC intersection) but now respect the
  team's *exposure*. A teamless admin has an empty `HiddenModules`, so they still see every module —
  the prior "admin without a team sees everything" escape is preserved by construction. An admin can
  always un-hide a module via `PermissionsAdminUI` (an `_sdk.` module outside `Managed`, never
  exposure-filtered).

## Diff to apply

**Nothing, for almost every consumer.** The change is additive and default-exposed: a team with no
exposure configured, and every `TeamPermissions` document persisted before this phase, behaves
exactly as before (the hand-rolled JSON deserializer absorbs a missing `hidden` array to the empty
set). Reading code is unaffected.

The only source break is consumer code that **constructs a `TeamPermissions` record literal**
(typically tests) — add the field:

```fsharp
// Before
{ Defaults = Map.empty; Members = Map.empty }

// After
{ Defaults = Map.empty; Members = Map.empty; Hidden = Set.empty }
```

…and likewise any **`AccessContext` record literal** (test helpers) gains `HiddenModules = Set.empty`.
`AccessContext.unrestricted` already sets it, so callers using the factory need no change. A custom
`IPermissionStore` implementation must add `GetHiddenModules` and `SetModuleExposure`.

The one **behaviour change** to be aware of (not a compile break): a platform admin who is a member
of a team that has hidden modules will no longer see those modules in their own sidebar for that team
— this is the intended fix. Use the `PermissionsAdminUI` → Modules tab toggle (or no active team) to
see the full set.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- Full Expecto suite — green (new `Phase 245 — per-team module exposure` contract pack in
  `AnonymousModeContractTests.fs` + the `IPermissionStoreContract` exposure round-trip).
- `dotnet fable` on a client that references `ToolUp.Platform.Client` — compiles.

## Rollback

Additive and default-off: a consumer that does not configure any exposure is byte-for-byte
unaffected, so no rollback is required to stay on the new SDK version. To revert the SDK feature
itself, drop the `Hidden` field + the two `IPermissionStore` methods + the `isModuleExposed` filter
in `computeAccessibleModules` (restoring the unconditional admin branch) — but note that re-restores
the admin-bypass defect.
