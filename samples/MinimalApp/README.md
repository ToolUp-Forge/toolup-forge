# MinimalApp

Canonical reference for [Phase 11.G `fromEnv` helpers](../../docs/migrations/11g-fromenv-helpers.md). Anonymous mode, no AI, no domain modules — the absolute minimum a ToolUp.Platform consumer can ship.

```bash
cd samples/MinimalApp
dotnet build src/MinimalApp.Server/MinimalApp.Server.fsproj
dotnet run --project src/MinimalApp.Server/MinimalApp.Server.fsproj
# Server boots on TOOLUP_PORT or default 5000; serves the SDK platform routes.
```

## What this demonstrates

`Server.fs` is 11 executable lines and reads as a manifest: which substrate helpers does this app use, and how do they compose? A pre-11.G hand-written reference composition root that this collapses ran to roughly 980 lines of env-var dispatch.

```fsharp
[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.empty

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.run
```

`ConsoleLogger.fromEnv ()` reads `TOOLUP_LOG_LEVEL` + `TOOLUP_TRACE_CATEGORIES`. `ServerConfig.fromEnv` honours the full `TOOLUP_*` env-var contract documented at [`toolup-forge/docs/platform/composition-roots.md`](../../docs/platform/composition-roots.md). The override record stays `empty` because the Anonymous-mode sample doesn't need the reference-app posture (`webhooks` / `audit` / `default security hardening`); production deployments use `ServerConfigOverrides.referenceApp`.

## Scope

Server-only sample by design. The client-side helpers (`BundleConstants` + `ClientConfigDefaults.fromBundleConstants`) require a Vite-driven Fable build pipeline to verify their `[<Emit>]` cross-project propagation; the canonical client-side verification rides on:

- `dotnet new platformsdk-application` — same composition shape, ships as a runnable scaffold via the templates. Full server + client + Vite + AG Grid Enterprise + Clerk end-to-end.

## See also

- [Composition roots doc](../../docs/platform/composition-roots.md) — full five-step pattern + `Wiring.fs` sidecar convention.
- [Migration doc](../../docs/migrations/11g-fromenv-helpers.md) — consumer migration walkthrough with before/after diffs.
- [Platform modes doc](../../docs/platform/platform-modes.md) — env-var matrix per `PlatformMode`.
