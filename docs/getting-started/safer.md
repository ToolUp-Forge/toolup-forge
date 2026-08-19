# Getting started with ToolUp.SAFER

`ToolUp.SAFER` is a minimal F# full-stack starter template (`S`erver + `A`nywhere + `F`able + `E`lmish + `R`emoting). It mirrors the [SAFE Stack](https://safe-stack.github.io/) get-started experience and produces a runnable F# full-stack app in ~200 lines of consumer code that demonstrates the in-tree Elmish + ToolUp.Remoting primitives via a Tiny Chat demo.

> **Positioning.** SAFER is **an option**, not the recommended starter. For production multi-tenant + auth + persistence, reach for [`platformsdk-solution`](../../templates/platformsdk-solution/) instead. SAFER's value is the side-by-side comparison with SAFE Stack — if you're landing here from SAFE Stack docs and want to learn the ToolUp shape with no auth / persistence / multi-tenancy in the way, SAFER is the path.

## Lineage

The SAFER acronym riffs on [SAFE Stack](https://safe-stack.github.io/) (Saturn + Azure + Fable + Elmish) by Compositional IT. SAFER replaces the original `S` (Saturn) and `A` (Azure) with `S`erver (the SDK's `ServerApp.run` composition root) and `A`nywhere (the SDK's host-portability seam: web / worker / dispatcher / serverless), and adds `R` (ToolUp.Remoting) — the type-safe RPC transport that's the load-bearing differentiator over the original acronym. SAFER stands alongside SAFE rather than replacing it — both ship F# full-stack apps; SAFER bundles the in-tree ToolUp Platform improvements.

`ToolUp.Templates.SAFER` is also distinct from Dzoukr's [`SAFEr.Template`](https://www.nuget.org/packages/SAFEr.Template/) — a personal SAFE-variant on NuGet; different scope, different stack assumptions, different maintainer. The `toolup-` short-name prefix and the `ToolUp.Templates.*` package-id family keep the two unambiguous.

## Install

```powershell
dotnet new install ToolUp.Templates.SAFER
```

The template installs from nuget.org directly — no feed wiring or authentication needed. A local-feed nupkg also installs directly:

```powershell
dotnet new install ../local-nuget-feed/ToolUp.Templates.SAFER.0.4.4.nupkg
```

## Scaffold

```powershell
dotnet new toolup-safer -o MyApp
cd MyApp
pwsh ./run.ps1
```

`run.ps1` runs the full happy path: `dotnet tool restore` → `npm install` → `dotnet build` → `dotnet fable` → starts server + Vite in parallel → opens the browser at the chat UI.

Standard switches:
- `-SkipBuild` / `-SkipFable` — fast iteration loops.
- `-NoOpenBrowser` — skip the browser open.
- `-ServerPort N` / `-VitePort N` — override default ports.

> **Naming note.** Pass `-o <Name>` with a CamelCase or alphanumeric name (`MyApp`, `Chat`, `SaferTest`). Hyphenated names (`my-app`, `safer-test`) diverge between the on-disk filenames (preserved verbatim) and the `.sln` project references (id-safe-form with `-` rewritten to `_`), so the scaffold won't build. This is a known template defect tracked at the time of writing.

## What you get

A single-channel chat room in ~200 lines of consumer code:

- **No auth** — anonymous mode only (`Surfaces.individual` or post-Phase-66 equivalent).
- **No persistence** — server holds messages in an in-memory ring buffer (50 messages, FIFO eviction); restarts wipe it.
- **No multi-tenancy** — single global channel.
- **Two-tab sync** — open two tabs, type in one, message appears in the other within 2 seconds (polling-based in v1; SSE upgrade documented in the template's "How to extend" section).
- **Optimistic UI** — sender sees their message immediately; reconciles when the server's confirmed copy arrives.
- **Retry policy** — `Cmd.OfRemoting.callWithRetry` with 3 attempts, 250 ms initial delay, exponential backoff.
- **Structured error banner** — `Program.withErrorReporter` + `ErrorContext` (module id + correlation id + exn) drives a dismissible red banner on send failure.
- **Hot-reload survival** — SSE subscription cleans up and re-establishes across hot reloads via `EffectHandle.programLifetime` (when SSE is wired in the extension path).

## What this teaches

The template's README walks through each in-tree-fork primitive with `file:line` citations:

| Mechanic | Primitive demonstrated |
|---|---|
| Boot loader splash | `Prefetch.onAllReady` — multi-source data gating |
| Send-message command | `Cmd.OfRemoting.callWithRetry` with `Cmd.RetryPolicy` — retry-as-data (GP 12 rule 3) |
| Optimistic UI on send | One line of model state for the local-then-confirmed pattern |
| SSE message subscription (extension) | `EffectHandle.programLifetime` — lifetime-aware subscription, disposed on hot-reload + page-leave |
| SSE callback safety (extension) | `IDispatcher.IsActive` — no-op against torn-down loops |
| Send-failure red banner | `Program.withErrorReporter` + structured `ErrorContext` |

The scaffold has a "SAFE Stack ↔ SAFER comparison table" mapping `Saturn` → `ServerApp`, `Cmd.OfAsync` → `Cmd.OfRemoting`, ad-hoc subscriptions → `EffectHandle`, `(string * exn)` → structured `ErrorContext`, etc. If you arrived from SAFE Stack docs, that table is the highest-leverage section.

## When to choose SAFER vs `platformsdk-solution`

| Use case | Reach for |
|---|---|
| Production multi-tenant SaaS | `platformsdk-solution` |
| First-time exploration / SAFE-Stack-familiar onboarding | `toolup-safer` |
| Authenticated app (OIDC / Clerk / Entra) | `platformsdk-solution` |
| Schema-driven forms + workflows | `platformsdk-solution` |
| In-memory chat demo / no-auth utility | `toolup-safer` |
| Single-page module learning the in-tree primitives | `toolup-safer` |

## How to extend

The template's README lists natural next steps (typing indicators, multi-room, message persistence, SSE upgrade, auth, multi-tenancy). Each is sketched as "a future template" candidate rather than baked into SAFER — pushing past those would dilute the "minimal" pitch and overlap `platformsdk-solution`.

## See also

- [SAFE Stack get-started](https://safe-stack.github.io/docs/quickstart/) — the canonical SAFE quickstart; SAFER mirrors its shape and many of its idioms.
- [`templates/platformsdk-solution/`](../../templates/platformsdk-solution/) — the production-multi-tenant scaffold.
- [`docs/platform/architecture.md`](../platform/architecture.md) — SDK composition-root + `ServerApp` / `ClientConfig` model.
- [`docs/platform/surfaces.md`](../platform/surfaces.md) — Subject / SurfaceProfile / SurfaceRequirement model; SAFER's anonymous surface composes against this.
- [`NOTICE.md`](../../NOTICE.md) — upstream credit for the Fable.Remoting (Zaid Ajaj, MIT) and Fable.Elmish (Eugene Tolmachev + community, Apache 2.0) projects ToolUp's transport and runtime were forked from.
