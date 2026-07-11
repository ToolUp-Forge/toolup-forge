// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactsCompose (Phase 520 wiring) ─────────────────────────────────
//
// Turns the introspectable `ServerConfig.FactStore` knob into real
// composition: when `EnabledFactStore`, folds an `IFactStore`
// (BlobFactStore over the composed `IBlobStorage` + `IEventStore`), its
// `IFactEvidenceSource` adapter, the `IFactDisclosureGate` egress gate,
// and the `IFactResolver` retrieval adapter into DI, so the fact tier is
// available to request handlers, the provenance graph, and fact-first
// retrieval (Phase 558 — `RAGCompose` picks the resolver + gate up into
// the `RetrievalPipeline` with zero extra config). `NoFactStore` (the
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
            // Phase 525 — the disclosure egress gate is registered with the
            // store, never separately: a deployment cannot compose the fact
            // tier without its egress doors armed. Dormant with no
            // classified facts (every Surfaceable fact passes — plan D17).
            .AddSingleton<IFactDisclosureGate>(
                Func<IServiceProvider, IFactDisclosureGate>(fun sp ->
                    FactDisclosureGate.create
                        (sp.GetRequiredService<IFactStore>())
                        (sp.GetRequiredService<IEventStore>()))
            )
            // Phase 558 — the concrete fact resolver closes the Phase 522
            // seam, registered with the store + gate so the fact tier is
            // one compose knob, never three. The metric registry (Phase
            // 519) is optional: a deployment with no grounding declarations
            // derives freshness under the `UntilSuperseded` default and
            // renders values verbatim.
            .AddSingleton<IFactResolver>(
                Func<IServiceProvider, IFactResolver>(fun sp ->
                    let registry =
                        match sp.GetService(typeof<Grounding.IMetricRegistry>) with
                        | :? Grounding.IMetricRegistry as r -> Some r
                        | _ -> None

                    FactStoreFactResolver.create (sp.GetRequiredService<IFactStore>()) registry)
            )

    /// Compose the grounding fact store per `ServerConfig.FactStore`.
    /// `EnabledFactStore` registers `IFactStore` (`BlobFactStore`) +
    /// `IFactEvidenceSource` + `IFactDisclosureGate` + `IFactResolver`
    /// into DI; `NoFactStore` returns the app unchanged. Insert once in
    /// the compose pipeline before `ServerApp.run` (and before a RAG
    /// compose, so the retrieval pipeline's DI pickup sees the fact tier):
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
                    // Phase 559 — declare the `query_facts` AI tool with
                    // the store (one compose knob, never two). The
                    // declaration rides `ServerApp.AITools`, which only
                    // the AI companion's compose reads into the live AI
                    // tool registry — so the tool arms exactly when both
                    // the fact store AND the AI companion are composed
                    // (GP 13): no fact store ⇒ never declared; no AI ⇒
                    // never registered, no route, no runtime cost.
                    AITools = app.AITools @ [ FactQueryTool.definition, FactQueryTool.execute ]
            }