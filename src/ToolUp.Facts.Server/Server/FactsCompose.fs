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
            // Phase 562 — taint propagation arms only when a deployment
            // registers a `DisclosureTaintConfig` in DI (the optional-
            // registry pattern below); unregistered ⇒ the plain gate,
            // byte-identical to the pre-562 composition (GP 11 / GP 13).
            .AddSingleton<IFactDisclosureGate>(
                Func<IServiceProvider, IFactDisclosureGate>(fun sp ->
                    let store = sp.GetRequiredService<IFactStore>()
                    let events = sp.GetRequiredService<IEventStore>()

                    match sp.GetService(typeof<DisclosureTaintConfig>) with
                    | :? DisclosureTaintConfig as taint -> FactDisclosureGate.createWithTaint taint store events
                    | _ -> FactDisclosureGate.create store events)
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
            // Phase 560 — the grounded answer planner rides the same
            // knob: question → (subject, metric, period) triples →
            // typed PlanStep resolution, recorded into the answer's
            // provenance chain. The registry supplies the vocabulary
            // (none composed ⇒ every triple refuses honestly as
            // unrecognised); the 67b structured-output compiler arms
            // only when a deployment registers an `IAIProvider` in DI —
            // otherwise questions refuse with the typed
            // missing-compiler reason (GP 9 / GP 13, no cost and no
            // guessing without the substrate).
            .AddSingleton<IAnswerPlanner>(
                Func<IServiceProvider, IAnswerPlanner>(fun sp ->
                    let registry =
                        match sp.GetService(typeof<Grounding.IMetricRegistry>) with
                        | :? Grounding.IMetricRegistry as r -> Some r
                        | _ -> None

                    let compiler =
                        match sp.GetService(typeof<ToolUp.Platform.AI.IAIProvider>) with
                        | :? ToolUp.Platform.AI.IAIProvider as provider ->
                            AnswerPlanner.structuredCompiler provider registry
                        | _ -> AnswerPlanner.noCompiler

                    AnswerPlanner.create
                        (sp.GetRequiredService<IFactStore>())
                        (sp.GetRequiredService<IFactDisclosureGate>())
                        registry
                        (sp.GetRequiredService<IEventStore>())
                        compiler)
            )
            // Phase 565 — the grounding-certificate issuer rides the same
            // knob. It seals an answer's provenance chain (Phase 524) with
            // the composed `IArtefactSigner` (Phase 40): a signed,
            // third-party-checkable "this number came from these facts,
            // under these disclosure policies", verifiable offline against
            // the deployment public key. The signer is optional (GP 13): no
            // signing substrate composed ⇒ issuance refuses with
            // `SigningUnavailable`, never throws. The provenance graph is
            // built over the composed `ILineageStore` when present, else an
            // `IEventStore`-backed lineage view over the same store — always
            // buildable so a certificate can be issued the moment a signer
            // is present.
            .AddSingleton<IGroundingCertificateIssuer>(
                Func<IServiceProvider, IGroundingCertificateIssuer>(fun sp ->
                    let events = sp.GetRequiredService<IEventStore>()

                    let lineage =
                        match sp.GetService(typeof<ILineageStore>) with
                        | :? ILineageStore as l -> l
                        | _ -> LineageStore.EventStoreLineageStore(events) :> ILineageStore

                    let graph =
                        ProvenanceGraph.createWithFacts lineage (sp.GetRequiredService<IFactEvidenceSource>())

                    let signer =
                        match sp.GetService(typeof<ToolUp.ArtefactSigning.IArtefactSigner>) with
                        | :? ToolUp.ArtefactSigning.IArtefactSigner as s -> Some s
                        | _ -> None

                    GroundingCertificate.createIssuer
                        graph
                        (sp.GetRequiredService<IFactStore>())
                        (sp.GetRequiredService<IFactDisclosureGate>())
                        events
                        signer)
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