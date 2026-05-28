module MyApp.Server

open ToolUp.Platform
open ToolUp.Platform.Server

// Phase 11.G — env-driven composition shape. `ConsoleLogger.fromEnv`
// reads `TOOLUP_LOG_LEVEL` / `TOOLUP_TRACE_CATEGORIES`;
// `ServerConfig.fromEnv` honours the full `TOOLUP_*` env-var contract
// documented in toolup-forge/docs/platform/composition-roots.md.
// `ServerConfigOverrides.empty` keeps every override-able field at its
// `ServerConfig.defaults` value — Anonymous template stays maximally
// lightweight.

[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.empty

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.run