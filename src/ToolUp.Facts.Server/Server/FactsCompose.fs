// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
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

    // ─── Phase 623 — shared optional-substrate lookups ────────────────

    let private tryService<'T> (sp: IServiceProvider) : 'T option =
        match sp.GetService(typeof<'T>) with
        | :? 'T as service -> Some service
        | _ -> None

    /// The composed lineage store, or an `IEventStore`-backed view over
    /// the same events when a deployment did not enable
    /// `ServerConfig.Lineage` — the invalidation walk is always buildable
    /// (the same fallback `IGroundingCertificateIssuer` uses below).
    let private resolveLineage (sp: IServiceProvider) : ILineageStore =
        match tryService<ILineageStore> sp with
        | Some lineage -> lineage
        | None -> LineageStore.EventStoreLineageStore(sp.GetRequiredService<IEventStore>()) :> ILineageStore

    // Register the fact-store DI singletons (lazy factories over the
    // composed substrate).
    let private registerFactStore (services: IServiceCollection) : IServiceCollection =
        services
            // Phase 703 — the store is composed WITH the metric registry.
            // It was registry-less until now, and that was a wiring gap
            // rather than a choice: a registry-less store resolves a
            // Phase 701 `RegistryDirection` ordering to the refusal
            // "metric '…' is not registered", which in a composed
            // deployment that HAS registered the metric is not merely
            // unhelpful but untrue — the store simply could not see the
            // declaration. Task 703.C's "ordering comes from the
            // registry's direction-of-better" is only true once the store
            // holds the registry. The lookup is optional (`tryService`),
            // so a deployment with no grounding declarations composes
            // exactly as before, and a metric with no `CanonicalMethod`
            // declaration keeps the pre-566 selection byte-for-byte
            // (GP 11).
            .AddSingleton<IFactStore>(
                Func<IServiceProvider, IFactStore>(fun sp ->
                    BlobFactStore.createWithRegistry
                        (sp.GetRequiredService<IBlobStorage>())
                        (sp.GetRequiredService<IEventStore>())
                        (tryService<Grounding.IMetricRegistry> sp))
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
            // Phase 592 — purpose binding arms the same way, off an
            // optional `DisclosurePurposeConfig` registration (the
            // `withDisclosurePurposes` compose below); unregistered ⇒ the
            // facet is absent.
            .AddSingleton<IFactDisclosureGate>(
                Func<IServiceProvider, IFactDisclosureGate>(fun sp ->
                    let store = sp.GetRequiredService<IFactStore>()
                    let events = sp.GetRequiredService<IEventStore>()

                    let taint =
                        match sp.GetService(typeof<DisclosureTaintConfig>) with
                        | :? DisclosureTaintConfig as t -> Some t
                        | _ -> None

                    let purpose =
                        match sp.GetService(typeof<DisclosurePurposeConfig>) with
                        | :? DisclosurePurposeConfig as p -> Some p
                        | _ -> None

                    FactDisclosureGate.createConfigured taint purpose store events)
            )
            // Phase 558 — the concrete fact resolver closes the Phase 522
            // seam, registered with the store + gate so the fact tier is
            // one compose knob, never three. The metric registry (Phase
            // 519) is optional: a deployment with no grounding declarations
            // derives freshness under the `UntilSuperseded` default and
            // renders values verbatim.
            // Phase 623.B — the resolver is composed in its *reactive*
            // form: `IDataObjectStore` supplies the derived
            // `inputsChanged` signal that makes `UntilUpstreamChange`
            // real at the read path, and `IFactRecomputer` arms the
            // `OnQuery` recompute-at-read arm. Both are read back out of
            // DI as options, so a deployment missing either resolves
            // byte-for-byte the pre-623 projection (GP 11), and the
            // probe itself only runs for metrics that declare one of the
            // two policies (GP 13).
            .AddSingleton<IFactResolver>(
                Func<IServiceProvider, IFactResolver>(fun sp ->
                    FactStoreFactResolver.createReactive
                        (sp.GetRequiredService<IFactStore>())
                        (tryService<Grounding.IMetricRegistry> sp)
                        (tryService<IDataObjectStore> sp)
                        (tryService<IFactRecomputer> sp))
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

                    // Phase 706 — the question seam: one 67b call compiles
                    // point triples AND population triples, so superlative
                    // and aggregate questions reach a `UseAggregate` step
                    // instead of degrading to an unanswerable point lookup.
                    // A deployment with no provider refuses with the same
                    // typed missing-compiler reason it always did.
                    let compiler =
                        match sp.GetService(typeof<ToolUp.Platform.AI.IAIProvider>) with
                        | :? ToolUp.Platform.AI.IAIProvider as provider ->
                            AnswerPlanner.structuredQuestionCompiler provider registry
                        | _ -> AnswerPlanner.noQuestionCompiler

                    AnswerPlanner.createCompiling
                        (sp.GetRequiredService<IFactStore>())
                        (sp.GetRequiredService<IFactDisclosureGate>())
                        registry
                        (sp.GetRequiredService<IEventStore>())
                        compiler)
            )
            // Phase 708 — the fact-clause feeder, on the SAME knob again.
            // Phase 522 built the push path (facts resolved ahead of
            // vector search, merged at score 1.0 under the verbatim-
            // quoting contract) and Phase 558 wired its resolver in; what
            // was missing was anything that ever PRODUCED a clause, so the
            // path was dormant in every composed deployment and facts
            // reached the model only when it thought to call a tool. This
            // registration closes that loop: `RAGCompose` probes for the
            // seam exactly as it probes for the resolver, and a
            // deployment with no fact store registers neither (GP 13).
            //
            // One instance, registered under BOTH faces. The planner and
            // the recorder share the retained plans by construction —
            // registering two would give the recorder an empty retention
            // and make 708.B's "reuse, don't recompute" quietly false.
            .AddSingleton<AnswerPlanClausePlanner>(
                Func<IServiceProvider, AnswerPlanClausePlanner>(fun sp ->
                    AnswerPlanClausePlanner.create (sp.GetRequiredService<IAnswerPlanner>()))
            )
            .AddSingleton<IFactClausePlanner>(
                Func<IServiceProvider, IFactClausePlanner>(fun sp ->
                    sp.GetRequiredService<AnswerPlanClausePlanner>() :> IFactClausePlanner)
            )
            .AddSingleton<IPlannedAnswerRecorder>(
                Func<IServiceProvider, IPlannedAnswerRecorder>(fun sp ->
                    sp.GetRequiredService<AnswerPlanClausePlanner>() :> IPlannedAnswerRecorder)
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
            //
            // Phase 685 — the issuer logs each issuance through the
            // composed `IAuditLog`, so the audit trail becomes the
            // deployment's certificate log and a certificate stops being
            // unlisted. The log is looked up as an OPTION and not
            // required: a deployment somehow without one issues exactly as
            // it did before rather than failing to compose (GP 11 /
            // GP 13), and a deployment that never issues records nothing
            // either way.
            //
            // Note what this does NOT do: Phase 682's attested issuer
            // stays uncomposed here, deliberately (GP 13). Its logging
            // constructor exists for a composition root that wires it by
            // hand — the emission is on the issuer, not on this
            // registration, so the log's claim to enumerate issuance does
            // not depend on which path a deployment chose.
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

                    let store = sp.GetRequiredService<IFactStore>()
                    let gate = sp.GetRequiredService<IFactDisclosureGate>()

                    match tryService<IAuditLog> sp with
                    | Some audit -> GroundingCertificate.createIssuerAudited graph store gate events signer audit
                    | None -> GroundingCertificate.createIssuer graph store gate events signer)
            )

    // ─── Phase 623 — activate reactive recomputation ──────────────────
    //
    // Phase 561 shipped the substrate and wired none of it: the recompute
    // handler was never registered with a scheduler, so a job it enqueued
    // had no handler to dispatch to, and nothing ever told the fact tier a
    // data-object version had landed. The three registrations below are
    // what take it live, and they ride the SAME `EnabledFactStore` knob as
    // the store itself — one compose knob, never four.
    //
    //   1. `IFactRecomputer` — the deployment seam that actually
    //      recomputes a value. Registered with `TryAdd` semantics, so a
    //      deployment's own engine always wins and the default
    //      (`NoFactRecomputer`, recomputes nothing) is only the floor.
    //   2. The recompute job handler, registered with the scheduler
    //      through the Phase 623.A DI-deferred declaration — the handler
    //      needs `IFactStore` + `IFactRecomputer`, neither of which
    //      exists until the container is built.
    //   3. The `IDataObjectStore` decorator that reacts to a landed
    //      version (Phase 623.C). It self-gates on a declared non-`Manual`
    //      `RecomputePolicy`, so a fact deployment that declares none pays
    //      one boolean test per save.
    //
    // A `NoFactStore` deployment reaches none of this — `withFactStore`
    // returns before `registerFactStore` is ever composed into
    // `ServiceConfig` (GP 13).

    let private registerReactiveRecomputation (services: IServiceCollection) : IServiceCollection =
        // (1) The default recompute engine — TryAdd so a deployment-
        //     supplied `IFactRecomputer` registered anywhere in the
        //     compose chain takes precedence.
        services.TryAddSingleton<IFactRecomputer>(NoFactRecomputer())

        // (3) Decorate the composed data-object store. The inner store is
        //     taken from the descriptor already in the collection — never
        //     from the built provider, which by then resolves to the
        //     decorator itself. `ServiceConfig` runs after the SDK's core
        //     singletons are registered, so the descriptor is present for
        //     any deployment that has a data-object store at all; one that
        //     somehow does not is left untouched rather than failing.
        let innerDescriptor =
            services
            |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<IDataObjectStore>)
            |> Seq.tryLast

        match innerDescriptor with
        | Some descriptor when (descriptor.ImplementationInstance :? IDataObjectStore) ->
            let inner = descriptor.ImplementationInstance :?> IDataObjectStore

            services.AddSingleton<IDataObjectStore>(
                Func<IServiceProvider, IDataObjectStore>(fun sp ->
                    let scheduler () = tryService<IJobScheduler> sp

                    let registry () =
                        tryService<Grounding.IMetricRegistry> sp

                    let react =
                        ReactiveDataChange.reaction
                            (fun () -> sp.GetRequiredService<IFactStore>())
                            (fun () -> resolveLineage sp)
                            scheduler
                            registry

                    ReactiveDataChange.decorate
                        inner
                        (ReactiveDataChange.gate registry scheduler)
                        react
                        (sp.GetRequiredService<ILogger>()))
            )
            |> ignore
        | _ -> ()

        // (2) Register + schedule the recompute handler at startup, once
        //     the container that owns `IFactStore` / `IFactRecomputer`
        //     exists. `Trigger.Manual`: recompute jobs are fired on demand
        //     by `reactToDataChange`, never on a cadence.
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                DeferredScheduledJobDeclaration.hostedService
                    "Reactive fact recomputation"
                    [
                        DeferredScheduledJobDeclaration.create (fun provider ->
                            RecomputeJobHandler.declaration
                                (provider.GetRequiredService<IFactStore>())
                                (provider.GetRequiredService<IFactRecomputer>())
                                (provider.GetRequiredService<ILogger>()))
                    ]
                    sp)
        )
        |> ignore

        services

    /// Compose the grounding fact store per `ServerConfig.FactStore`.
    /// `EnabledFactStore` registers `IFactStore` (`BlobFactStore`) +
    /// `IFactEvidenceSource` + `IFactDisclosureGate` + `IFactResolver`
    /// into DI, and (Phase 623) activates reactive recomputation over it:
    /// the default `IFactRecomputer`, the recompute job handler on the
    /// composed scheduler, and the data-arrival hook that drives fact
    /// invalidation when a data-object version lands. `NoFactStore`
    /// returns the app unchanged — no registration, no hosted service, no
    /// decorator, no allocation (GP 13). Insert once in the compose
    /// pipeline before `ServerApp.run` (and before a RAG compose, so the
    /// retrieval pipeline's DI pickup sees the fact tier):
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> ServerApp.run
    /// ```
    ///
    /// Reactive recomputation stays dormant until a module's grounding
    /// declarations ask for it: a metric declaring
    /// `RecomputePolicy.Eager` recomputes off a scheduled job when its
    /// inputs change, `OnQuery` recomputes at the next read, and the
    /// default `Manual` surfaces the changed state only. Wire a real
    /// `IFactRecomputer` into DI to give any of them a value to compute.
    let withFactStore (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            // Phase 623 — the fact tier and its reactive activation are
            // one registration pass: the store first, then the recompute
            // engine + handler + data-arrival hook over it.
            let register (s: IServiceCollection) =
                registerReactiveRecomputation (registerFactStore s)

            let serviceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some register
                | Some existing -> Some(fun s -> register (existing s))

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
                    //
                    // Phase 703 — `query_metric_population` rides the same
                    // double gate beside it. The two are siblings, not
                    // alternatives: `query_facts` answers about a subject,
                    // `query_metric_population` ranks a metric across a
                    // population and summarises what it ranked over. One
                    // knob declares both, so no deployment can arm the
                    // point read and miss the population read.
                    //
                    // Phase 705 — and `list_metric_coverage` beside them,
                    // for a sharper version of the same argument. Both
                    // read tools take ids and neither can list them, so a
                    // deployment that armed the reads WITHOUT the
                    // discovery surface would leave the model guessing
                    // metric ids — and a guessed id is refused, not
                    // approximated. Arming discovery separately would make
                    // that misconfiguration possible; one knob makes it
                    // unreachable.
                    AITools =
                        app.AITools
                        @ [
                            FactQueryTool.definition, FactQueryTool.execute
                            PopulationQueryTool.definition, PopulationQueryTool.execute
                            CoverageTool.definition, CoverageTool.execute
                        ]
            }

    // ─── Phase 592 — purpose-bound disclosure (opt-in) ────────────────
    //
    // The "declared why" facet: a composition declares its purpose
    // taxonomy + per-surface allowed sets, and the Phase 525 gate then
    // requires every check's ambient `FactPurposeContext` claim to be in
    // the surface's allowed set — out-of-set or missing claims refuse
    // with the allowed set enumerated, and grants and denials both stamp
    // the claimed purpose + taxonomy version into the audit trail. A
    // separate, explicit opt-in on top of the fact store (the Phase 563
    // shape): a deployment that only wants the store is byte-for-byte
    // unchanged (GP 11 / GP 13).

    /// The per-purpose manifest projection: the generic, readable
    /// declaration the platform manifest carries (GP 1 — the typed
    /// config stays here in the facts companion).
    let private registeredPurposes (config: DisclosurePurposeConfig) : RegisteredPurpose list =
        config.Taxonomy.Purposes
        |> List.map (fun p -> {
            PurposeId = p.PurposeId
            Description = p.Description
            TaxonomyVersion = config.Taxonomy.Version
            AllowedSurfaces =
                config.AllowedBySurface
                |> Map.toList
                |> List.filter (fun (_, ids) -> List.contains p.PurposeId ids)
                |> List.map (fst >> FactEgressSurface.toString)
        })

    /// Compose purpose-bound disclosure (Phase 592): register the
    /// declared `DisclosurePurposeConfig` so the gate factory above arms
    /// the purpose facet, and project the taxonomy + per-surface allowed
    /// sets into the composition manifest beside the Phase 526 grounding
    /// declarations — the whole purpose regime is readable before any
    /// data flows. A `NoFactStore` deployment (or one that never calls
    /// this) is byte-for-byte unchanged (GP 11 / GP 13). Insert after
    /// `withFactStore`:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withDisclosurePurposes purposeConfig
    /// |> ServerApp.run
    /// ```
    ///
    /// Request handlers state the claim with `FactPurposeContext.claim`;
    /// with a declared taxonomy an unclaimed check refuses at every
    /// purpose-bound surface (default-deny-by-shape).
    let withDisclosurePurposes (config: DisclosurePurposeConfig) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register (s: IServiceCollection) =
                s.AddSingleton<DisclosurePurposeConfig>(config)

            let serviceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some(fun s -> register s)
                | Some existing -> Some(fun s -> register (existing s))

            {
                app with
                    Extensions = {
                        app.Extensions with
                            ServiceConfig = serviceConfig
                    }
            }
            |> ServerApp.withRegisteredPurposes (registeredPurposes config)

    // ─── Phase 683 — certificate-verified fact import (opt-in) ────────
    //
    // A separate, explicit opt-in on top of the fact store, and separate
    // for a sharper reason than symmetry with the two opt-ins around it:
    // the door needs KEY MATERIAL, and folding it into `withFactStore`
    // would compose a trust decision into every deployment that wanted a
    // fact base. The set of peers a deployment accepts facts from is
    // exactly the kind of thing that must be written down in one place and
    // read off the page (GP 13) — never acquired by default.
    //
    // An empty anchor list is legal and inert: the door composes and
    // refuses every import with `ImportUntrustedPeer`. That is a
    // deployment that has declared it trusts nobody, which is a different
    // statement from one that never composed a door at all — and the audit
    // trail tells them apart.

    /// Compose the certificate-verified fact import door with the peer
    /// anchors this deployment accepts facts from. Registers
    /// `IFactImportDoor` over the composed `IFactStore` + `IAuditLog`; a
    /// `NoFactStore` deployment (or one that never calls this) is
    /// byte-for-byte unchanged (GP 11 / GP 13). Insert after
    /// `withFactStore`:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withFactImport [ PeerTrustAnchor.create "partner-a" partnerKey ]
    /// |> ServerApp.run
    /// ```
    ///
    /// Each anchor carries one peer's public key — the whole of what
    /// offline verification needs — and, optionally, a ceiling narrowing
    /// what an import from that peer may disclose
    /// (`PeerTrustAnchor.withCeiling`). No key is discovered, so no key is
    /// implicitly trusted.
    let withFactImport (anchors: PeerTrustAnchor list) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register (s: IServiceCollection) =
                s.AddSingleton<IFactImportDoor>(
                    Func<IServiceProvider, IFactImportDoor>(fun sp ->
                        FactImport.create
                            (sp.GetRequiredService<IFactStore>())
                            anchors
                            (sp.GetRequiredService<IAuditLog>()))
                )

            let serviceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some(fun s -> register s)
                | Some existing -> Some(fun s -> register (existing s))

            {
                app with
                    Extensions = {
                        app.Extensions with
                            ServiceConfig = serviceConfig
                    }
            }

    // ─── Phase 684 — the grounding envelope sealed past boot (opt-in) ─
    //
    // Phase 657 seals the composition AT boot and says plainly that it
    // proves nothing about what happens afterwards. For the grounding tier
    // that gap is the live one: the declarations a later answer's
    // provenance is judged against — which metrics are registered, which
    // method a method-less query canonically resolves to, which purposes
    // may disclose at which surface — are free to move the instant the
    // preflight verdict lands, and nothing in the trail says they did.
    //
    // This composes the door. Grounding-relevant mutation stays possible
    // and stops being invisible: each becomes a typed, audited operation
    // carrying the before/after envelope digest, and `boot seal +
    // recorded chain ⇒ live envelope` is a computation an auditor runs
    // from the trail. Under `CompositionProfile.Verified` a mutation
    // arriving out of path is refused; under `Standard` the same findings
    // are recorded and the mutation lands.
    //
    // A separate, explicit opt-in on top of the fact store — the shape
    // every opt-in above it takes, and for the same reason: a deployment
    // that only wants the store is byte-for-byte unchanged (GP 11 /
    // GP 13).

    /// Compose the audited grounding-envelope mutation door and its
    /// continuity proof (Phase 684). Registers `IGroundingEnvelopeMutator`
    /// over the composed `IAuditLog`, sealed to the grounding envelope
    /// this app declares. A `NoFactStore` deployment (or one that never
    /// calls this) is byte-for-byte unchanged (GP 11 / GP 13).
    ///
    /// **Insert LAST among the grounding compose steps.** The envelope is
    /// sealed from the app AS IT STANDS at this call, so a metric,
    /// purpose, or disclosure declaration composed after it is outside
    /// the seal:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withDisclosurePurposes purposeConfig
    /// |> FactsCompose.withGroundingEnvelopeSeal CompositionProfile.Verified None
    /// |> ServerApp.run
    /// ```
    ///
    /// `observe` re-derives the envelope from whatever LIVE grounding
    /// state the deployment holds, and is the honest bound on the whole
    /// mechanism. `None` is the right answer for a composition whose
    /// grounding declarations are compose-time immutable — which is every
    /// composition this SDK ships: continuity is then continuous by
    /// construction, and what that proves is that the deployment has
    /// nothing that could drift, not that a drift check passed. A
    /// deployment holding mutable grounding state passes `Some` a
    /// function that reads it, and only then can the check catch
    /// anything.
    let withGroundingEnvelopeSeal
        (profile: CompositionProfile)
        (observe: (unit -> GroundingEnvelope) option)
        (app: ServerApp)
        : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let sealedEnvelope =
                GroundingEnvelope.ofComposition (ServerApp.compositionManifest app) app.RegisteredMetrics

            let register (s: IServiceCollection) =
                s.AddSingleton<IGroundingEnvelopeMutator>(
                    Func<IServiceProvider, IGroundingEnvelopeMutator>(fun sp ->
                        let auditLog = sp.GetRequiredService<IAuditLog>()

                        match observe with
                        | None ->
                            GroundingEnvelopeMutator.forImmutableComposition
                                profile
                                auditLog
                                GroundingEnvelopeMutator.PlatformScopeId
                                sealedEnvelope
                        | Some observeLive ->
                            GroundingEnvelopeMutator.create
                                profile
                                auditLog
                                GroundingEnvelopeMutator.PlatformScopeId
                                sealedEnvelope
                                observeLive)
                )

            let serviceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some(fun s -> register s)
                | Some existing -> Some(fun s -> register (existing s))

            {
                app with
                    Extensions = {
                        app.Extensions with
                            ServiceConfig = serviceConfig
                    }
            }

    // ─── Phase 707 — coverage narratives (opt-in) ─────────────────────
    //
    // A separate, explicit opt-in on top of the fact store — the shape
    // every opt-in above takes, and here for two reasons rather than one.
    //
    // The ordinary reason first: this WRITES to the deployment's knowledge
    // base. Folding it into `withFactStore` would mean that composing a
    // fact base silently started publishing documents into a retrieval
    // corpus, which is not a thing a storage knob may decide.
    //
    // The sharper reason is the second argument `withCoverageNarratives`
    // takes. A coverage narrative is a STANDING document, readable by
    // everyone who can retrieve from the scope it lands in, and the
    // disclosure gate judges it once — against the principal named here,
    // at commit time — rather than per reader at read time. That is a
    // deliberate and load-bearing narrowing: it means the named principal
    // must be a FLOOR on what the scope may see, not a service identity
    // that can see everything. There is no defensible default for that,
    // so there is no default.

    let private registerCoverageNarratives
        (options: CoverageNarrative.CoverageNarrativeOptions)
        (services: IServiceCollection)
        : IServiceCollection =
        // The inner store is taken from the descriptor already in the
        // collection, never from the built provider — by then `IFactStore`
        // resolves to this decorator and the factory would recurse. Same
        // shape (and same reason) as the Phase 623.C data-object
        // decoration above.
        let innerDescriptor =
            services
            |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<IFactStore>)
            |> Seq.tryLast

        let inner: (IServiceProvider -> IFactStore) option =
            match innerDescriptor with
            | Some descriptor when not (isNull (box descriptor.ImplementationFactory)) ->
                let factory = descriptor.ImplementationFactory
                Some(fun sp -> factory.Invoke sp :?> IFactStore)
            | Some descriptor when (descriptor.ImplementationInstance :? IFactStore) ->
                let instance = descriptor.ImplementationInstance :?> IFactStore
                Some(fun _ -> instance)
            | _ -> None

        match inner with
        // No fact store descriptor at all. Left untouched rather than
        // registering a decorator with nothing to decorate — a deployment
        // in that state has a composition defect the fact tier's own
        // registrations will surface far more clearly.
        | None -> services
        | Some resolveInner ->
            services.AddSingleton<IFactStore>(
                Func<IServiceProvider, IFactStore>(fun sp ->
                    CoverageNarrative.decorate (resolveInner sp) sp options (sp.GetRequiredService<ILogger>()))
            )

    /// Compose the coverage-narrative trigger (Phase 707): decorate the
    /// composed `IFactStore` so that assertion activity recomputes a
    /// metric's coverage and, when it has moved MATERIALLY (a cardinality
    /// band, the period reach, the method mix — not every assertion),
    /// commits one narrative per populated metric into the deployment's
    /// knowledge base through the ordinary ingestion path.
    ///
    /// A `NoFactStore` deployment (or one that never calls this) is
    /// byte-for-byte unchanged (GP 11 / GP 13), and a deployment that
    /// calls it without composing a knowledge base is inert: the trigger
    /// probes for `INarrativeIngestor` before it reads anything, so
    /// arming the knob with nowhere to commit costs one DI lookup per
    /// coalesced regeneration and no store work.
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withCoverageNarratives
    ///        (CoverageNarrative.CoverageNarrativeOptions.forScopes "coverage-reader" [ tenantScope ])
    /// |> ServerApp.run
    /// ```
    ///
    /// Insert AFTER `withFactStore` — it decorates what that registered,
    /// and finds nothing to decorate if it runs first.
    let withCoverageNarratives (options: CoverageNarrative.CoverageNarrativeOptions) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register = registerCoverageNarratives options

            let serviceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some register
                | Some existing -> Some(fun s -> register (existing s))

            {
                app with
                    Extensions = {
                        app.Extensions with
                            ServiceConfig = serviceConfig
                    }
            }

    // ─── Phase 563 — fact-base coherence checking (opt-in) ────────────
    //
    // A separate, explicit opt-in on top of the fact store: the standing
    // self-audit is NOT folded into `withFactStore`, so a deployment that
    // only wants the store is byte-for-byte unchanged (GP 11 / GP 13). The
    // registrations ride `Extensions.ServiceConfig`:
    //
    //   * an `IHostedService` that, once the container is built, resolves
    //     the composed substrate + the scheduler and schedules the
    //     coherence job on the opt-in `cadence` (mirrors the model-fit /
    //     DSR startup-registration pattern — the scheduler and the metric
    //     registry are only resolvable post-`Build`);
    //   * an `IHealthCheck` that re-scans the configured scope on each
    //     probe and reports `Degraded` when any finding stands.

    let private tryRegistry (sp: IServiceProvider) : Grounding.IMetricRegistry option =
        match sp.GetService(typeof<Grounding.IMetricRegistry>) with
        | :? Grounding.IMetricRegistry as r -> Some r
        | _ -> None

    let private registerCoherenceChecks
        (config: CoherenceConfig)
        (cadence: Trigger)
        (scopes: string list)
        (services: IServiceCollection)
        : IServiceCollection =
        services
            // The health probe surfaces current findings for
            // `config.HealthScope` — a fresh re-scan per call (GP 12 rule 4).
            .AddSingleton<HealthChecks.IHealthCheck>(
                Func<IServiceProvider, HealthChecks.IHealthCheck>(fun sp ->
                    CoherenceHealthCheck.create (sp.GetRequiredService<IFactStore>()) (tryRegistry sp) config)
            )
            // Schedule the standing check on the opt-in cadence. The
            // scheduler + metric registry are only resolvable from the built
            // container, so registration happens in a startup hosted service.
            .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                    { new Microsoft.Extensions.Hosting.IHostedService with
                        member _.StartAsync(_ct) =
                            let logger = sp.GetRequiredService<ILogger>()

                            match sp.GetService(typeof<IJobScheduler>) with
                            | :? IJobScheduler as scheduler ->
                                let handler =
                                    CoherenceJobHandler.create
                                        (sp.GetRequiredService<IFactStore>())
                                        (tryRegistry sp)
                                        (sp.GetRequiredService<INotificationChannel>())
                                        (sp.GetRequiredService<IEventStore>())
                                        config
                                        (fun () -> DateTime.UtcNow)
                                        logger

                                scheduler.RegisterHandler(CoherenceJobHandler.HandlerName, handler)

                                let effectiveScopes = if List.isEmpty scopes then [ "_platform" ] else scopes

                                for scopeId in effectiveScopes do
                                    let registration: JobRegistration = {
                                        ScopeId = scopeId
                                        Handler = CoherenceJobHandler.HandlerName
                                        Payload = ""
                                        Trigger = cadence
                                        Idempotency =
                                            Some {
                                                Key = sprintf "coherence-%s" scopeId
                                                TtlSeconds = 60 * 60 * 24 * 365
                                            }
                                        RetryPolicy = JobRetryPolicy.defaults
                                        ShardKey = None
                                        Precision = JobPrecision.Minute
                                        CreatedBy = "_facts.coherence"
                                        Tags = Map.ofList [ "origin", "fact-coherence" ]
                                    }

                                    match scheduler.Schedule registration |> Async.RunSynchronously with
                                    | Ok _ -> ()
                                    | Error err ->
                                        logger.Warn(
                                            sprintf
                                                "[Phase 563] Failed to schedule the coherence check in scope %s: %A"
                                                scopeId
                                                err
                                        )
                            | _ ->
                                logger.Warn(
                                    "[Phase 563] Coherence checking enabled but JobScheduler = NoJobScheduler — the standing check is not scheduled (the on-demand path and the /ready health probe still work). Pair with JobScheduler = InProcessJobScheduler."
                                )

                            System.Threading.Tasks.Task.CompletedTask

                        member _.StopAsync(_ct) =
                            System.Threading.Tasks.Task.CompletedTask
                    })
            )
        |> ignore

        services

    /// Compose the standing fact-base coherence check with an explicit
    /// `config` + `cadence` (a cron `Trigger`) + the `scopes` to sweep
    /// (empty ⇒ `["_platform"]`). Registers the coherence `IHealthCheck` +
    /// the scheduled sweep on top of an already-enabled fact store. A
    /// `NoFactStore` deployment (or one that never calls this) is
    /// byte-for-byte unchanged (GP 11 / GP 13). Insert after
    /// `withFactStore`:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withCoherenceChecksConfig cfg cadence [ "_platform" ]
    /// |> ServerApp.run
    /// ```
    let withCoherenceChecksConfig
        (config: CoherenceConfig)
        (cadence: Trigger)
        (scopes: string list)
        (app: ServerApp)
        : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register = registerCoherenceChecks config cadence scopes

            let serviceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some register
                | Some existing -> Some(fun s -> register (existing s))

            {
                app with
                    Extensions = {
                        app.Extensions with
                            ServiceConfig = serviceConfig
                    }
            }

    /// `withCoherenceChecksConfig` with `CoherenceConfig.defaults` and the
    /// default `_platform` sweep scope — the one-liner opt-in. `cadence` is
    /// the recurring `Trigger` the standing check runs on (e.g. a daily
    /// `CronTrigger`); the check is also fireable on demand and re-scanned
    /// by the `/ready` health probe.
    let withCoherenceChecks (cadence: Trigger) (app: ServerApp) : ServerApp =
        withCoherenceChecksConfig CoherenceConfig.defaults cadence [] app