// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── FactsCompose (Phase 520 wiring) ─────────────────────────────────
//
// Turns the introspectable `ServerConfig.FactStore` knob into real
// composition: when `EnabledFactStore`, folds an `IFactStore`
// (BlobFactStore over the composed `IBlobStorage` + `IEventStore`) and its
// `IFactEvidenceSource` adapter into DI, so the fact tier is available to
// request handlers and to the provenance graph. `NoFactStore` (the
// default) folds nothing — the composition is byte-for-byte unchanged
// (GP 11 + GP 13).
//
// The registrations are lazy factories: `IBlobStorage` / `IEventStore` are
// resolved from the built provider on first use, so this composes cleanly
// regardless of the order `ServerApp` registers its substrate.

module FactsCompose =

    // Register the fact-store DI singletons (lazy factories over the
    // composed substrate).
    let private registerFactStore (services: IServiceCollection) : IServiceCollection =
        services
            .AddSingleton<IFactStore>(
                Func<IServiceProvider, IFactStore>(fun sp ->
                    BlobFactStore.create (sp.GetRequiredService<IBlobStorage>()) (sp.GetRequiredService<IEventStore>()))
            )
            .AddSingleton<IFactEvidenceSource>(
                Func<IServiceProvider, IFactEvidenceSource>(fun sp ->
                    FactStoreEvidenceSource.create (sp.GetRequiredService<IFactStore>()))
            )

    /// Compose the grounding fact store per `ServerConfig.FactStore`.
    /// `EnabledFactStore` registers `IFactStore` (`BlobFactStore`) +
    /// `IFactEvidenceSource` into DI; `NoFactStore` returns the app
    /// unchanged. Insert once in the compose pipeline before
    /// `ServerApp.run`:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> ServerApp.run
    /// ```
    let withFactStore (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let serviceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some registerFactStore
                | Some existing -> Some(fun s -> registerFactStore (existing s))

            {
                app with
                    Extensions = {
                        app.Extensions with
                            ServiceConfig = serviceConfig
                    }
            }