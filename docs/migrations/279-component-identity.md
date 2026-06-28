# Stable component identity — `ComponentId` (Phase 279)

**Ships in:** `ToolUp.Platform.Core` (`ComponentId` type + `ComponentId` module),
`ToolUp.Platform.Server` (`ServerModule.ComponentId`, `ServerModule.withComponentId`,
`ServerApp.ModuleComponentIds`, a compose-time uniqueness check in `ServerApp.run`).

**Stability:** additive / opt-in. A deployment that declares no explicit ids is byte-for-byte
unchanged (GP 11) and pays nothing when the later identity / introspection surfaces are not composed
(GP 13).

## What this adds

Every **composed unit** of an application — a module, a companion selection, a datatype, a tool —
now resolves to a stable `ComponentId`: a value-typed `string` wrapper (portability rule 1 — identity
by value, never a live handle). The SDK was already string-id-addressed at its core (a module's
declared id is the real key, its display `Name` is cosmetic; `DataType.Id` and `AIToolDefinition.Name`
are stable strings; transactional sinks carry a `Kind`). `ComponentId` makes that identity a
first-class value and closes the one gap — **companions selected by CLR interface type**
(`IAuthProvider`, `IBlobStorage`, audit sinks, …), which carried no value-id of their own.

```fsharp
type ComponentId = ComponentId of string   // .Value : string

// Derivation — each kind is namespaced so a module "auth" can never
// collide with a companion slot "auth":
ComponentId.ofModule          "orders-service"            // module:orders-service
ComponentId.forCompanionSlot  "IAuthProvider"             // companion:IAuthProvider
ComponentId.forCompanionImpl  "IAuditSink" "SplunkHec"    // companion:IAuditSink/SplunkHec
ComponentId.forDataType       "SkuRow"                    // datatype:SkuRow
ComponentId.forTool           "summarise"                 // tool:summarise

// Duplicate detection (what compose runs):
ComponentId.findDuplicates : ComponentId seq -> ComponentId list
ComponentId.ensureUnique   : string -> ComponentId seq -> unit   // raises on a collision
```

Derivation is **deterministic** (a pure string projection), **independent of display name** (a module
keeps its id when `Name` is renamed), and **never positional** (a companion in a multi-impl list keeps
its id when the list is re-ordered — the id composes the interface slot with the impl's own sub-id,
the sink `Kind` / `Name`, not its index).

### Module `Id`/`Name` split — now formalised

- **Server.** `ServerModule` gains an optional `ComponentId` field, declared via
  `ServerModule.withComponentId "<declared-id>"`. `None` (the default) derives the id from `Name` at
  first registration (`ComponentId.ofModule Name`, GP 11). Declaring an explicit id makes the
  identity independent of the display `Name`, so a rename does not churn the id that telemetry
  correlation / hot-reload / config-diffing key against. The resolved id is accumulated onto
  `ServerApp.ModuleComponentIds`.
- **Client.** `ModuleDefinition.Id` already **is** the stable, declared identity, independent of
  `Name` — this phase only formalises that in the doc-comment. It lifts losslessly to a `ComponentId`
  via `ComponentId.ofModule Id`. **No client field changes** (GP 11) — the existing `Id` carries it.

### Duplicate ids fail at compose, not at runtime

`ServerApp.run` calls `ComponentId.ensureUnique "module composition"` over the resolved module ids
before anything binds. A duplicate resolved id (two modules declaring the same explicit id, or two
modules sharing a `Name` — already a latent defect, since `Name` is the permission / sidebar key)
now fails fast with a readable error naming the colliding id, instead of silently corrupting an
introspection surface downstream.

## Diff to apply

**Nothing for existing consumers** — additive and opt-in. Two optional adoptions:

1. **Pin a module's identity across a rename.** If you expect to rename a module's sidebar/header
   `Name`, declare its id once so the identity is stable:

   ```fsharp
   ServerModule.create "Orders"
   |> ServerModule.withComponentId "orders-service"   // id is now module:orders-service
   |> ServerModule.withGuardedApi ordersApi
   ```

2. **Address companion selections by value.** Use `ComponentId.forCompanionSlot` /
   `forCompanionImpl` when correlating telemetry / config-diffs against the companions a deployment
   composed. (The introspection surfaces that *read* these per-slot ids land in the follow-on
   identity/introspection phases; this phase ships the identity primitive + derivation they build on.)

## Verification

- `ToolUp.Forge.sln` builds clean.
- `dotnet run --project Build.fsproj -- VerifyAll` green; the `ComponentIdentity` Expecto pack covers
  derivation determinism, module-id stability across rename + re-order, default-derived determinism,
  and duplicate-id rejection at compose.

## Rollback

Remove the `ServerModule.withComponentId` call(s); the module reverts to its name-derived id. The
`ComponentId` type and derivations are inert when unused.
