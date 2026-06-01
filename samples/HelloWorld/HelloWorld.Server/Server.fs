// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module HelloWorld.Server

open ToolUp.Platform
open ToolUp.Platform.Server

// Phase 66 Stream F.5 — HelloWorld composition root.
// Authenticated single-user deployment using the canonical
// `Surfaces.individual` shape. `ServerConfigOverrides.referenceApp`
// declares `Some Surfaces.individual` so `fromEnv` pins this app to
// the Individual deployment unless the operator overrides via
// TOOLUP_PLATFORM_SURFACES.
//
// Compare with samples/MinimalApp — that sample uses
// `ServerConfigOverrides.empty`, which falls through to
// `ServerConfig.defaults.Surfaces = Surfaces.anonymous`.

[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.referenceApp

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.run