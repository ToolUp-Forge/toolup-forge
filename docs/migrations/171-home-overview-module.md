# Phase 171 — optional Home / Overview landing module

**Forge commit:** `e9fff7b` (AI-side probe wired in the same commit; `ToolUp.AI.Server`)

## What changes

A new **opt-in SDK built-in Home module** can become the deployment's default
landing surface (instead of the first registered module) and summarises the
deployment: data-producing tools with per-tool record counts (scope-correct),
the active AI provider/model, and light deployment context.

Four additive surfaces, all off by default:

- **`ToolUp.Platform.Core`** — `IHomeOverviewApi` + the view types, plus an
  optional `IActiveAiProbe` DI seam so `Platform.Server` reports the active AI
  model without taking any `ToolUp.AI` dependency (GP 1).
- **`ToolUp.Platform.Server`** — `IObjectCounter` capability +
  `IDataCatalog.CountObjects` (native-count fast-path, list-and-count fallback
  — GP 12), `HomeOverviewApiHandler` aggregation, route **auto-mounted**.
- **`ToolUp.AI.Server`** — `ActiveAiProbe` over `IAIProviderFactory`, registered
  automatically in `AICompose` (no consumer action; only fills the
  active-AI card when an AI app is composed).
- **`ToolUp.Platform.Client`** — the Home module (tool cards + counts +
  active-AI + context + click-through via `NavigationRequest`) and the
  `ClientConfig.HomeModule` knob.

### The knob

```fsharp
type HomeModuleMode =
    | NoHomeModule                          // default — off
    | EnabledHomeModule                     // SDK built-in, default branding
    | ConfiguredHomeModule of HomeModuleConfig   // built-in + custom Name/Icon
    | ExternalHomeModule of ErasedModule    // bring your own landing module
```

`ClientConfig.HomeModule` defaults to **`NoHomeModule`** — unlike the admin
built-ins, it does *not* auto-inject. Existing deployments keep their
first-registered module as the landing surface and are byte-for-byte unchanged
(GP 13). When enabled, the module is injected at the **head** of the sidebar and
becomes the default landing surface unless `ActiveModule` names another.

## Diff to apply

**Additive and opt-in — existing consumers need no change.** A deployment that
wants the landing surface sets one field on `ClientConfig`:

```fsharp
open ToolUp.Platform

let clientConfig =
    { ClientConfig.defaults with
        // ...
        HomeModule = EnabledHomeModule }          // or ConfiguredHomeModule { Name = "Home"; Icon = myIcon }
```

The server `IHomeOverviewApi` route is auto-mounted — no server-side wiring is
required. The per-tool record counts are scope-correct out of the box via the
default store's `ListObjects |> List.length` fallback; a store that implements
the optional `IObjectCounter` gets the cheap-count fast-path automatically.

## Verification steps

1. `dotnet build ToolUp.Forge.sln` — additive; build stays green.
2. `cd samples/MinimalClient && dotnet fable -o output --noCache` — the Core
   contract is Feliz-free, so the Fable client tier compiles unchanged.
3. With `HomeModule = NoHomeModule` (default), confirm the sidebar + landing
   surface are byte-identical to 0.5.x — no Home entry, first module lands.
4. With `EnabledHomeModule`, confirm: Home is the first sidebar entry and the
   default landing surface; tool cards show scope-correct record counts;
   clicking a card navigates to that module; the active-AI card populates only
   when an AI app (`AIServerApp.run`) is composed.

## Rollback

Additive throughout. Revert the single forge commit; consumers that never set
`ClientConfig.HomeModule` away from `NoHomeModule` are unaffected.
