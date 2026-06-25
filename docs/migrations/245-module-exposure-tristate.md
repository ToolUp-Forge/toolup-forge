# Phase 245 (follow-up) — Module exposure becomes a tri-state (`Available | Hidden | Unavailable`)

**Ships in:** `ToolUp.Platform.Core` (`ModuleExposure`, `TeamPermissions`, `AccessContext`,
`PermissionApi`), `ToolUp.Platform.Server` (`IPermissionStore`, `PermissionStore`,
`computeAccessibleModules`, `HomeOverviewApiHandler`, `dataCatalogApiHandler`, `FileManagement`),
`ToolUp.Platform.Client` (`PermissionsAdminUI`, `MappingDataManagerUI`). **SDK 0.9.3.**

## What changes

The per-team exposure axis introduced in [Phase 245](245-per-team-module-exposure.md) was a
two-state boolean (exposed / hidden) that governed **sidebar visibility only** — explicitly *not* an
authorization boundary, and with no effect on the Home overview or data mapping. This follow-up
replaces that boolean with a **tri-state** so a team can mark a module not merely invisible but
genuinely *unavailable* ("not cleared to use it"), and extends enforcement to the Home page and the
Import & Map data flow.

```fsharp
[<RequireQualifiedAccess>]
type ModuleExposure =
    | Available    // default: in sidebar + Home, data mappable
    | Hidden       // off sidebar + Home, data STILL mappable (the old "exposed = false")
    | Unavailable  // off sidebar + Home, AND data mapping blocked
```

The two-state design admitted a dead combination (a module "unavailable but not hidden" was still
forced off the sidebar), so the boolean collapses into one DU per module per team and illegal states
are unrepresentable.

### New surface

- New DU `ModuleExposure` + module (`toToken` / `ofToken` / `isExposed` / `isMappable`).
- `TeamPermissions.Hidden: Set<string>` → **`Exposure: Map<string, ModuleExposure>`** (absence ⇒
  `Available`).
- `AccessContext.HiddenModules: Set<string>` → **`ModuleExposure: Map<string, ModuleExposure>`**, plus
  `AccessContext.exposureOf` and a new `AccessContext.isModuleAvailable` (mapping gate) alongside the
  retained `isModuleExposed` (sidebar/Home gate).
- `PermissionApi.SetModuleExposure` third arg **`bool` → `ModuleExposure`** (one method, all three
  states).
- `IPermissionStore.GetHiddenModules` → **`GetModuleExposure`** (returns the map);
  `SetModuleExposure` retyped to take `ModuleExposure`.

### Behaviour

| State | Sidebar | Home "Your tools" | Data mapping (Import & Map) |
|---|---|---|---|
| `Available` | shown | shown | mappable |
| `Hidden` | hidden | hidden | **still mappable** |
| `Unavailable` | hidden | hidden | **blocked** |

- **Home** (`HomeOverviewApiHandler`) now filters tool cards by `isModuleExposed`, matching the
  sidebar's visible set (this is the new behaviour Phase 245 omitted).
- **Mapping gate** (`Unavailable` only): `dataCatalogApiHandler.GetDataCatalog` drops a type whose
  every producer is unavailable; `FileManagement` detection/processing/reprocess skip those types —
  **the upload still succeeds and the file is stored**, it simply lands unrecognised rather than
  mapping into an unavailable module. The `MappingDataManagerUI` wizard hides those targets and skips
  saved-mapping auto-reuse for them.
- Platform admins acting on a team respect both `Hidden` and `Unavailable` (the "Show hidden modules"
  reveal still surfaces `Managed`, so it reveals both for navigation; the data gate stays
  server-enforced).

## Diff to apply

**Nothing, for almost every consumer.** Additive-default and back-compatible at the persistence
layer: a team with no exposure configured behaves exactly as before, and a `TeamPermissions` document
written before this change (legacy `hidden: string[]`) **migrates on read to the `Hidden` state**
(off the sidebar/Home, data still mappable — identical to the old "exposed = false"). New documents
dual-write the legacy `hidden` array, so a downgraded reader still hides them.

Source breaks affect only code that **names the changed members** (typically tests / custom stores):

```fsharp
// TeamPermissions record literal
{ Defaults = Map.empty; Members = Map.empty; Exposure = Map.empty }   // was: Hidden = Set.empty

// AccessContext record literal (factory `AccessContext.unrestricted` already does this)
{ ...; ModuleExposure = Map.empty; ... }                              // was: HiddenModules = Set.empty

// PermissionApi / IPermissionStore call sites
ps.SetModuleExposure(teamId, moduleName, ModuleExposure.Unavailable)  // was: ..., false
let! exposure = store.GetModuleExposure teamId                        // was: GetHiddenModules → Set<string>
```

A custom `IPermissionStore` implementation renames `GetHiddenModules` → `GetModuleExposure`
(returning the map) and retypes `SetModuleExposure`'s third arg.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean; `dotnet fable` on a `ToolUp.Platform.Client` consumer —
  compiles.
- Full Expecto suite — green, including the updated `Phase 245 — per-team module exposure` +
  `IPermissionStore` contract packs and the new `Phase 245 — ModuleExposure persistence migration`
  pack (legacy `hidden[]` → `Hidden`, dual-write back-compat).
- Public-API baselines for `ToolUp.Platform.{Core,Server,Client}` regenerated (intentional breaking
  surface change under the 0.x SemVer policy).

## Rollback

Persistence is back-compatible both directions (dual-written `hidden`), so a consumer can move off
0.9.3 without data migration. To revert the SDK feature, restore the boolean `Hidden` axis — but that
re-opens the dead-combination modelling and drops the Home + mapping gates.
