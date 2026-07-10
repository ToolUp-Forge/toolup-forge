// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyCompose

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Server
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyConfigValidator
open ToolUp.AuthProviders.Passkey.PasskeyHost
open ToolUp.AuthProviders.PasskeyAuthProvider

// ─── PasskeyServerApp composition root ───────────────────────────────
//
// Mirrors `PeerServerApp` (InterPlatform): wraps a base `ServerApp` and,
// at `run`, wires the passkey companion in one fold —
//   • sets the app `IAuthProvider` to `PasskeyAuthProvider` (validates
//     the minted platform session JWTs on every request),
//   • registers the `PasskeyConfigValidator` startup preflight,
//   • mounts the ceremony routes onto the SDK route chain via
//     `ComposeExtensions.Handlers`, and
//   • registers the `PasskeyRuntime` DI singleton (Fido2 verifier +
//     credential/challenge stores) built over the resolved
//     `IBlobStorage`.
//
// Core carries no Fido2NetLib reference (GP 13): a deployment that never
// composes `PasskeyServerApp` pays nothing and the default composition
// is unchanged.

/// Wraps a base `ServerApp` with the passkey companion configuration.
type PasskeyServerApp = {
    Base: ServerApp
    Config: PasskeyConfig
}

/// Start a passkey companion over a base `ServerApp` and its config.
let create (config: PasskeyConfig) (baseApp: ServerApp) : PasskeyServerApp = { Base = baseApp; Config = config }

/// Replace the base `ServerApp` (for chaining SDK `ServerApp.with*`
/// helpers before `run`).
let withBase (baseApp: ServerApp) (app: PasskeyServerApp) : PasskeyServerApp = { app with Base = baseApp }

/// Replace the passkey configuration.
let withConfig (config: PasskeyConfig) (app: PasskeyServerApp) : PasskeyServerApp = { app with Config = config }

/// Compose + run. Folds the auth provider, preflight validator, ceremony
/// routes, and runtime DI singleton into the base app, then delegates to
/// `ServerApp.run`.
let run (app: PasskeyServerApp) =
    let config = app.Config

    let runtimeServiceConfig (services: IServiceCollection) =
        services.AddSingleton<PasskeyRuntime>(
            Func<IServiceProvider, PasskeyRuntime>(fun sp ->
                let blobs = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                PasskeyRuntime.create config blobs)
        )

    let baseApp =
        app.Base
        |> ServerApp.withAuth (PasskeyAuthProvider(config) :> IAuthProvider)
        |> ServerApp.withConfigValidator (PasskeyConfigValidator(config) :> ConfigValidation.IConfigValidator)

    let baseExt = baseApp.Extensions

    let mergedExt: ComposeExtensions = {
        baseExt with
            Handlers = baseExt.Handlers @ [ PasskeyHost.routes ]
            ServiceConfig =
                match baseExt.ServiceConfig with
                | None -> Some runtimeServiceConfig
                | Some baseFn -> Some(fun s -> runtimeServiceConfig (baseFn s))
    }

    ServerApp.run { baseApp with Extensions = mergedExt }