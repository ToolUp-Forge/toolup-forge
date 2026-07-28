# Phase 580 — client-shell module identity gate (consumer migration)

**What changes.** The client shell keys per-module state (`Model.ModuleStates`) by `ClientModule.Definition.Id` in a `Map`. Until now nothing checked that ids were unique, so two registered modules sharing an id silently collapsed into one entry: the second module's `Init` state overwrote the first's, and every later `update` for either module ran against whichever `Model` won. From Phase 580 a duplicate id is a **fatal compose-time defect** — `Client.prepareModules` (reached from `Client.run` / `Client.program`, and from the companion composers that build their own `Program`) raises before the shell's `Program` is constructed, naming every colliding module's display `Name` and the colliding id. This mirrors the server's `ComponentId.ensureUnique` gate in `ServerApp.run` (Phase 279) and the Phase 579 duplicate-query-handler rejection.

Alongside it, the previously-invisible id **derivation** is now inspectable: `ClientModule.create` derives `Id` from `Name` with spaces stripped unless the consumer chains `ClientModule.withId`, and nothing surfaced which ids came from where.

**Scope.** A composition with unique ids is unaffected — same modules, same order (GP 11). The check is purely structural over the composed list; the SDK names no module (GP 9). Nothing to opt into and nothing to configure.

**Who is affected.** Only a deployment already shipping a silent state collapse. Its symptom before 580 was a runtime one — a module rendering another module's state, or losing its own on every message — which is exactly what makes it worth failing at startup instead.

## New public surface

| Surface | Tier | Purpose |
|---|---|---|
| `ModuleIdentity.deriveId` / `originOf` / `componentIdOf` / `row` / `table` / `collisions` / `render` / `ensureUnique` | `ToolUp.Platform.Core` (`Shared/ModuleIdentity.fs`, Fable-packed) | The tier-shared name→id derivation, derived-vs-explicit classification, and the duplicate gate |
| `ModuleIdOrigin` (`DerivedFromName` \| `ExplicitlyDeclared`), `ModuleIdentityRow` | `ToolUp.Platform.Core` | The table's row type + origin classification |
| `Client.moduleIdentityTable : ErasedModule list -> ModuleIdentityRow list` | `ToolUp.Platform.Client` | The composed module-id table, typed |
| `Client.moduleIdentityReport : ErasedModule list -> string` | `ToolUp.Platform.Client` | The same table rendered, one line per module |

All additive. No existing signature changed; no record gained a field.

## What the failure looks like

```
client module composition: duplicate module id(s) detected — module id
"ChannelAnalysis" (module:ChannelAnalysis) is registered by 2 modules:
"Channel Analysis", "Channel Performance". The shell keys per-module state by
the module id, so all but one registration would be silently collapsed into a
single state entry and every module sharing the id would run its update against
whichever Model won. Give each module a distinct id: chain
`ClientModule.withId "<distinct-id>"` before `register` (or rename the module —
an id left unset is derived from Name with spaces stripped). Composed module-id
table:
  ChannelAnalysis   module:ChannelAnalysis   (derived from Name, name="Channel Analysis")
  ChannelAnalysis   module:ChannelAnalysis   (derived from Name, name="Channel Performance")
  _sdk.DataManager  module:_sdk.DataManager  (explicitly declared, name="Data Manager")
```

Every collision is reported, not just the first, and the full composed table is appended — so one refresh names the whole misconfiguration *and* shows which other ids the SDK derived for you.

## How to find and fix a collision

The message names both display names and the id. Two shapes account for essentially every case:

1. **Two modules whose names derive the same id.** `ClientModule.create` strips spaces, so `"Channel Analysis"` and `"ChannelAnalysis"` are different display names that derive one id — the invisible-derivation hazard in its sharpest form. Fix: pin one of them.

   ```fsharp
   ClientModule.create { Init = init; Update = update; Name = "Channel Analysis"; Icon = icon }
   |> ClientModule.withId "ChannelAnalysisDetail"   // ← distinct, and now rename-stable
   |> ClientModule.withView view
   |> ClientModule.register
   ```

2. **A consumer module colliding with an SDK built-in.** The gate runs on the *composed* list (consumer modules plus the built-ins `prepareModules` injects), so a module registered as `_sdk.HealthMonitor` collides with the SDK's own. Fix: the `_sdk.*` / `_ai.*` prefixes are reserved — pick your own.

Declaring an explicit id is worth doing on its own: an id left derived changes when the display `Name` is renamed, which churns the identity RBAC, telemetry correlation, and `AIMessageRequest.ActiveModule` key against. `ClientModule.withId` makes the module rename-stable, exactly as `ServerModule.withComponentId` does server-side.

## Auditing the derivation

To see which ids the SDK derived rather than you declaring them, log the table from your composition root:

```fsharp
Browser.Dom.console.log (Client.moduleIdentityReport modules)
// or, for the fully-composed set including SDK built-ins:
Browser.Dom.console.log (Client.moduleIdentityReport (Client.prepareModules config modules))
```

The boot line also now carries the split — `moduleIds=derived:4,explicit:1` — so the counts are visible without asking for the table. The table itself stays off the console by default (GP 13: a deployment that never asks pays nothing).

## The cross-tier identity law

`ServerModule.Name` is documented as "must match the client `ClientModule.Definition.Id`", and both tiers now lift that token through the same `ComponentId.ofModule` derivation. A client module displayed as `"Channel Analysis"` derives the id `"ChannelAnalysis"`, which is the `ServerModule.Name` it pairs with, and both resolve to `module:ChannelAnalysis`. If your server and client module names have drifted apart, the table is the fastest way to see it.

## Verification

1. Boot the client: with unique ids, composition and every existing module behave exactly as before.
2. Register two modules with the same id (or two names that derive one id): `Client.run` must refuse before the first render, naming both display names.
3. Test pack: `InProcess/ModuleIdentityTests.fs` in `ToolUp.Platform.Tests` covers the derivation, the derived-vs-explicit classification, the duplicate rejection (including multi-collision reporting), and the cross-tier law against the real `ServerApp.addModules` resolution.

## Rollback

There is no opt-out flag — a rejected composition is a genuine defect and the fix is to give each module a distinct id. If you need to unblock immediately, chain `ClientModule.withId` on one of the colliding modules; behaviour then matches whichever module's state the pre-580 `Map` fold happened to keep (the last one registered).
