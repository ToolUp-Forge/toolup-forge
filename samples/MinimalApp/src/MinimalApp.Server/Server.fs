// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module MinimalApp.Server

open ToolUp.Platform
open ToolUp.Platform.Server

// Phase 11.G canonical reference composition root.
// Anonymous mode, no AI, no domain modules — the absolute minimum a
// ToolUp.Platform consumer can ship. The four lines below demonstrate
// the `fromEnv` helper pattern end-to-end; the env-var contract is
// documented at toolup-forge/docs/platform/composition-roots.md.
//
// Compare with `templates/platformsdk-application/src/MyApp-Server/Server.fs`
// which `dotnet new platformsdk-application` produces — same shape,
// distinct sample so the canonical reference stays buildable in-tree.

[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.empty

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.run