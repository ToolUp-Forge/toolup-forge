module MyApp.Server

open ToolUp.Platform
open ToolUp.Platform.Server

// ─── Composition root ───────────────────────────────────────────────────
//
// `ConsoleLogger.fromEnv` reads `TOOLUP_LOG_LEVEL` / `TOOLUP_TRACE_CATEGORIES`.
// `ServerConfig.fromEnv` reads the full `TOOLUP_*` env-var contract — including
// `TOOLUP_PLATFORM_SURFACES` which decides the auth shape (anonymous /
// individual / team / multiTeam / mixed). SAFER defaults to anonymous via
// the env var set in run.ps1; flip to individual / team for per-user or
// team-scoped variants without code changes.
//
// `ServerConfigOverrides.empty` keeps every override-able field at its
// `ServerConfig.defaults` value — no webhooks, no audit replication, no
// rate limiting, no security-hardening uplift. SAFER stays maximally
// lightweight. For the production-shape posture, switch to
// `ServerConfigOverrides.referenceApp` (audit + webhooks + default
// hardening) — see toolup-forge/docs/platform/composition-roots.md.

[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.empty

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.run