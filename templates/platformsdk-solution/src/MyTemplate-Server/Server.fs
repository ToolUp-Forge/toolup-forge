module MyTemplate.Server

open ToolUp.Platform
open ToolUp.Platform.Server

// Phase 11.G — env-driven composition shape. `ConsoleLogger.fromEnv`
// reads `TOOLUP_LOG_LEVEL` / `TOOLUP_TRACE_CATEGORIES`;
// `ServerConfig.fromEnv` honours the full `TOOLUP_*` env-var contract
// documented in toolup-forge/docs/platform/composition-roots.md. To
// switch from Anonymous to Individual / Team mode at runtime, set
// `TOOLUP_PLATFORM_MODE=individual` (etc.); the override record stays
// empty unless the deployment opts into the reference-app posture
// (`ServerConfigOverrides.referenceApp` — webhooks, audit, default
// hardening).
[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.empty

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.run