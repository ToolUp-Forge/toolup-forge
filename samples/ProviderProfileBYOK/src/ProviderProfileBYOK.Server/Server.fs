// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ProviderProfileBYOK.Server

open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Server

// Phase 44 — the non-AI BYOK composition root.
//
// A deployment that holds a multi-provider BYOK catalogue and never
// composes the AI assistant. Neither this project nor its client half
// references `ToolUp.AI` in any tier; the settings surface comes from
// the platform, which is the whole point of the Phase 42.B decoupling.
//
// The composition act is ONE call. `withProviderProfile` registers the
// `IProviderProfile` DI singleton and mounts the `IProviderProfileApi`
// remoting handler on the same gate — an app that omits it mounts no
// route and resolves no store (GP 13).
//
// Compare `samples/MinimalApp`, which is this shape with the BYOK line
// removed.

[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.empty

    // The default blob-backed store, over whatever `IBlobStorage` the
    // deployment already has. A production composition would hand it the
    // same storage companion the rest of the app uses (S3, Azure Blob,
    // GCS); a local-disk root keeps the sample runnable with no cloud
    // account.
    let storage = LocalFileStorage.LocalFileStorage("./data") :> IBlobStorage

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.withProviderProfile (BlobProviderProfile.create storage)
    |> ServerApp.run