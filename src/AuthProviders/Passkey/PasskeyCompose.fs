// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyCompose

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Compose.Server
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyConfigValidator
open ToolUp.AuthProviders.Passkey.PasskeyHost
open ToolUp.AuthProviders.PasskeyAuthProvider
open ToolUp.Platform.Compose

// ─── The passkey companion: `withPasskey` ─────────────────────────────
//
// The passkey ceremonies are a companion composed onto a `ServerApp`, not a
// record that wraps one. Its options are the `PasskeyConfig` record;
// `withPasskey config` registers the companion with
// `ServerApp.withCompanion`, so it is applied once, when the deployment
// resolves, to the deployment as every builder left it:
//   • sets the app `IAuthProvider` to `PasskeyAuthProvider` (validates
//     the minted platform session JWTs on every request),
//   • registers the `PasskeyConfigValidator` startup preflight,
//   • contributes the ceremony routes, and
//   • registers the `PasskeyRuntime` DI singleton (Fido2 verifier +
//     credential/challenge stores) built over the resolved
//     `IBlobStorage`.
//
// Core carries no Fido2NetLib reference (GP 13): a deployment that never
// composes the passkey companion pays nothing and the default composition
// is unchanged.
//
//     ServerApp.empty
//     |> ServerApp.withStorage storage
//     |> PasskeyCompose.withPasskey config
//     |> ServerApp.run

/// The passkey companion's identity: the slot `withPasskey` composes under
/// and the owner of its contributions.
let passkeyCompanion: ComponentId =
    ComponentId.forCompanionSlot "ToolUp.AuthProviders.Passkey"

let private composePasskey (config: PasskeyConfig) (app: ServerApp) : ServerApp =
    let runtimeServiceConfig (services: IServiceCollection) =
        services.AddSingleton<PasskeyRuntime>(
            Func<IServiceProvider, PasskeyRuntime>(fun sp ->
                let blobs = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                PasskeyRuntime.create config blobs)
        )

    app
    |> ServerApp.withAuth (PasskeyAuthProvider(config) :> IAuthProvider)
    |> ServerApp.withConfigValidator (PasskeyConfigValidator(config) :> ConfigValidation.IConfigValidator)
    |> ServerApp.addContribution (Contribution.routes passkeyCompanion [ PasskeyHost.routes ])
    |> ServerApp.addContribution (Contribution.service passkeyCompanion runtimeServiceConfig)
    |> ServerApp.withAuditEvents (AuditEventFamily.registration PasskeyAuditEvents.PasskeyAuditEvent.family)

/// **Sets** the passkey companion: the auth provider, preflight validator,
/// ceremony routes and runtime DI singleton `config` describes are composed
/// once, when the deployment resolves, onto the deployment as every builder
/// left it — so a `ServerApp` builder applied after this call still reaches
/// it. A second call replaces the first.
let withPasskey (config: PasskeyConfig) (app: ServerApp) : ServerApp =
    ServerApp.withCompanion passkeyCompanion (composePasskey config) app