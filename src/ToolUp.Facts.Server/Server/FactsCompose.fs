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

// ─── Phase 888 — the blob fact store's multi-replica scale guard ─────
//
// `BlobFactStore` is correct at any size and distributed-ready, but its
// population read, its asserts and its whole-store walks read every fact
// in the scope, and every replica pays that read separately. Past a size
// the right answer is the indexed companion, `ToolUp.FactStores.Postgres`,
// behind the same `IFactStore` contract. This guard says so at startup
// rather than leaving an operator to find out from latency.
//
// It speaks only when BOTH hold: more than one replica is configured
// (`ServerConfig.ReplicaCount > 1`), and the composed store is the blob
// default. A single-replica deployment does not register it at all, so its
// composition is byte-for-byte unchanged (GP 11). It warns above
// `BlobFactStoreScale.WarnAboveFacts` facts in any one scope, and warns
// again, in stronger terms, above `BlobFactStoreScale.RefuseAboveFacts`.
//
// **Warn-only in this release (operator decision, 2026-09-29).** Phase 888
// shipped the upper threshold as a startup REFUSAL. It is a warning for
// now: both thresholds and the advice to move to the indexed companion
// stand, and nothing this guard reports stops a deployment starting. The
// upper constant keeps its name so no caller breaks.
//
// The count is the census `BlobFactStore` keeps (`_facts/{factId}.json`,
// one blob per fact): one `List` per scope, no download.

/// The fact counts at which the blob fact store's scale guard speaks, and
/// the pure verdict it reaches (Phase 888).
module BlobFactStoreScale =

    /// Above this many facts in one scope, with more than one replica, the
    /// guard warns and names the indexed companion.
    [<Literal>]
    let WarnAboveFacts = 50_000

    /// Above this many facts in one scope, with more than one replica, the
    /// guard's warning becomes the stronger one. Sized at the grounding
    /// plane's stated population (300,000 subjects), where one blob-store
    /// population read is 300,000 blob reads per replica per question.
    /// Warn-only in this release (operator decision, 2026-09-29): Phase 888
    /// refused startup here, and the name is kept from then.
    [<Literal>]
    let RefuseAboveFacts = 300_000

    /// The package the guard names as the remedy.
    [<Literal>]
    let Remedy = "ToolUp.FactStores.Postgres"

    /// The blob layout's fact prefix — `BlobFactStore` writes each fact at
    /// `_facts/{factId}.json`, so listing it counts the scope's facts.
    [<Literal>]
    let FactsPrefix = "_facts/"

    /// The guard's verdict over per-scope fact counts. `Ok` below the warn
    /// threshold or at one replica; `Warning` above it, and a stronger
    /// `Warning` above the upper threshold — never `Error` in this release
    /// (operator decision, 2026-09-29). The largest scope decides, and is
    /// named.
    let verdict
        (replicaCount: int)
        (warnAboveFacts: int)
        (refuseAboveFacts: int)
        (counts: (string * int) list)
        : ConfigValidation.ValidationResult =
        match counts |> List.sortByDescending snd |> List.tryHead with
        | _ when replicaCount <= 1 -> ConfigValidation.ValidationResult.Ok
        | None -> ConfigValidation.ValidationResult.Ok
        | Some(scope, count) when count > refuseAboveFacts ->
            ConfigValidation.ValidationResult.Warning(
                sprintf
                    "ReplicaCount = %d and scope '%s' holds %d facts in BlobFactStore, above the %d-fact upper threshold. Every replica reads the whole scope for a population read, an assert and every whole-store walk, so each question costs %d blob reads per replica. Move to %s (PostgresFactStoreCompose.withPostgresFactStore) behind the same IFactStore contract, or run a single replica. This check warns and does not refuse startup in this release."
                    replicaCount
                    scope
                    count
                    refuseAboveFacts
                    count
                    Remedy
            )
        | Some(scope, count) when count > warnAboveFacts ->
            ConfigValidation.ValidationResult.Warning(
                sprintf
                    "ReplicaCount = %d and scope '%s' holds %d facts in BlobFactStore, above the %d-fact warning threshold (the stronger warning is at %d). Population reads and asserts read the whole scope on every replica. Plan the move to %s (PostgresFactStoreCompose.withPostgresFactStore), which indexes each read."
                    replicaCount
                    scope
                    count
                    warnAboveFacts
                    refuseAboveFacts
                    Remedy
            )
        | Some _ -> ConfigValidation.ValidationResult.Ok

/// Which `IFactStore` implementation the composition registered — a DI
/// marker `withFactStore` sets to the blob default and
/// `withFactStoreImplementation` replaces, so the scale guard can tell the
/// blob store from a replacement even when a decorator wraps it.
type internal FactStoreBackend = { Backend: string }

/// Startup guard (Phase 888): warns a multi-replica deployment whose blob
/// fact store holds more facts in one scope than the blob layout serves
/// well, in stronger terms past the upper threshold, naming
/// `ToolUp.FactStores.Postgres` as the remedy. Warn-only in this release
/// (operator decision, 2026-09-29) — it never refuses startup. Registered
/// by `FactsCompose.withFactStore` only when `ServerConfig.ReplicaCount > 1`.
type BlobFactStoreScaleValidator
    /// The guard over `storage`'s census of the scopes `scopes` enumerates,
    /// at explicit thresholds. `isBlobStore` is whether the composed store
    /// is the blob default — the guard stands down for a replacement.
    (
        replicaCount: int,
        storage: IBlobStorage,
        scopes: unit -> Async<string list>,
        isBlobStore: bool,
        warnAboveFacts: int,
        refuseAboveFacts: int
    ) =

    /// The guard at the stated thresholds (`BlobFactStoreScale`).
    new(replicaCount: int, storage: IBlobStorage, scopes: unit -> Async<string list>, isBlobStore: bool) =
        BlobFactStoreScaleValidator(
            replicaCount,
            storage,
            scopes,
            isBlobStore,
            BlobFactStoreScale.WarnAboveFacts,
            BlobFactStoreScale.RefuseAboveFacts
        )

    interface ConfigValidation.IConfigValidator with
        member _.Name = "blob-fact-store-scale"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            if replicaCount <= 1 || not isBlobStore then
                return ConfigValidation.ValidationResult.Ok
            else
                try
                    let! scopeIds = scopes ()

                    let! counts =
                        scopeIds
                        |> List.distinct
                        |> List.map (fun scope -> async {
                            let! names = storage.List(scope, BlobFactStoreScale.FactsPrefix)
                            return scope, List.length names
                        })
                        |> BlobFanOut.run

                    return BlobFactStoreScale.verdict replicaCount warnAboveFacts refuseAboveFacts (List.ofArray counts)
                with ex ->
                    return
                        ConfigValidation.ValidationResult.Warning(
                            sprintf
                                "ReplicaCount = %d with BlobFactStore, and the fact count could not be read (%s), so the multi-replica scale guard could not decide. Above %d facts in one scope, compose %s."
                                replicaCount
                                ex.Message
                                BlobFactStoreScale.WarnAboveFacts
                                BlobFactStoreScale.Remedy
                        )
        }


/// Phase 946 — the blob fact store a composition built, once its
/// `IFactStore` has been resolved, for the `/dev/inspect` index inspector.
type internal ComposedBlobFactStore() =
    member val Instance: BlobFactStore option = None with get, set

/// Phase 946 — the registered form of `BlobFactStoreScaleValidator`: an
/// instance (the compose-time preflight refuses a factory registration)
/// that builds the guard when it runs, from the instance registrations of
/// the service collection it was registered into. Last registration wins,
/// as in a built provider; a deployment whose blob storage is not an
/// instance registration gets the guard's "could not decide" warning.
type internal BlobFactStoreScaleGuard(replicaCount: int, services: IServiceCollection) =

    member private _.LastInstance<'T when 'T: not struct>() : 'T option =
        services
        |> Seq.filter (fun d -> d.ServiceType = typeof<'T> && not d.IsKeyedService)
        |> Seq.tryLast
        |> Option.bind (fun d ->
            match d.ImplementationInstance with
            | :? 'T as instance -> Some instance
            | _ -> None)

    interface ConfigValidation.IConfigValidator with
        member _.Name = "blob-fact-store-scale"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member this.Validate() = async {
            let scopes () =
                match this.LastInstance<IScopeEnumerator>(), this.LastInstance<TeamManagement.ITeamStore>() with
                | Some enumerator, _ -> enumerator.ListScopes()
                | None, Some teams -> (ScopeEnumeration.fromTeamStore teams).ListScopes()
                | None, None -> async { return ScopeEnumeration.wellKnownContainers }

            let isBlob =
                match this.LastInstance<FactStoreBackend>() with
                | Some marker -> marker.Backend = "blob"
                | None -> true

            match this.LastInstance<IBlobStorage>() with
            | Some storage ->
                let guard =
                    BlobFactStoreScaleValidator(replicaCount, storage, scopes, isBlob)
                    :> ConfigValidation.IConfigValidator

                return! guard.Validate()
            | None when replicaCount <= 1 || not isBlob -> return ConfigValidation.ValidationResult.Ok
            | None ->
                return
                    ConfigValidation.ValidationResult.Warning(
                        sprintf
                            "ReplicaCount = %d with BlobFactStore, and IBlobStorage is not registered as an instance, so the multi-replica scale guard could not count the facts. Above %d facts in one scope, compose %s."
                            replicaCount
                            BlobFactStoreScale.WarnAboveFacts
                            BlobFactStoreScale.Remedy
                    )
        }

/// Phase 986 — a deployment's explicit question-compiler choice, registered
/// by `FactsCompose.withQuestionCompiler`. Its presence outranks every
/// derived compiler, and it is what tells the startup guard below that a
/// compiler-less planner was chosen rather than forgotten.
[<Sealed>]
type internal ExplicitQuestionCompiler(compiler: QuestionCompiler) =
    member _.Compiler = compiler

/// Phase 986 — warns at startup when the composed answer planner has no
/// question compiler. The planner then refuses every question, so fact
/// clause planning (the push door) never places a fact in the prompt and
/// only the fact tools reach the store — a state that used to be silent.
/// An instance (the compose-time preflight refuses a factory), deciding
/// when it runs from the service collection as finally assembled, so the
/// AI tier may be composed before or after the fact tier.
type internal FactQuestionCompilerGuard(services: IServiceCollection) =

    member private _.Has(serviceType: Type) =
        services
        |> Seq.exists (fun d -> d.ServiceType = serviceType && not d.IsKeyedService)

    interface ConfigValidation.IConfigValidator with
        member _.Name = "fact-question-compiler"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member this.Validate() = async {
            let wired =
                this.Has typeof<ExplicitQuestionCompiler>
                || this.Has typeof<ToolUp.Platform.AI.IAIProvider>
                || this.Has typeof<ToolUp.AI.IAIProviderFactory>

            return
                if wired then
                    ConfigValidation.ValidationResult.Ok
                else
                    ConfigValidation.ValidationResult.Warning
                        "The fact tier is composed but its answer planner has no question compiler, so fact clause planning (the push door: facts placed in the prompt ahead of retrieved passages) refuses every question and never pushes a fact; only the fact tools reach the store. Remedy: compose the AI tier (the planner then compiles through its IAIProviderFactory), register an IAIProvider, or supply a compiler with FactsCompose.withQuestionCompiler. To leave the push door off on purpose, compose FactsCompose.withQuestionCompiler AnswerPlanner.noQuestionCompiler."
        }

module FactsCompose =

    /// Phase 1052 — the Facts companion's identity; every registration it
    /// makes is a `Contribution.service` under it.
    let private owner: ComponentId = ComponentId.forCompanionSlot "ToolUp.Facts"

    let private contribute (register: IServiceCollection -> IServiceCollection) (app: ServerApp) : ServerApp =
        ServerApp.addContribution (Contribution.service owner register) app

    let private tryService<'T> = ServiceComposition.tryService<'T>

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
        let composedBlob = ComposedBlobFactStore()

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
                    // A fact read that faults is logged through the composed
                    // logger as well as failing the read.
                    let store =
                        BlobFactStore(
                            sp.GetRequiredService<IBlobStorage>(),
                            sp.GetRequiredService<IEventStore>(),
                            tryService<Grounding.IMetricRegistry> sp,
                            (fun () -> DateTime.UtcNow),
                            FactSurfaceOptions.defaults,
                            FactIndexOptions.defaults,
                            tryService<ILogger> sp
                        )
                        :> IFactStore

                    // Phase 946 — remember the blob store this composition
                    // built, for the /dev/inspect index inspector below.
                    match store with
                    | :? BlobFactStore as blob -> composedBlob.Instance <- Some blob
                    | _ -> ()

                    store)
            )
            // Phase 946 — the fact store's index check (Phase 890) on
            // /dev/inspect. Resolving the composed IFactStore first builds
            // it, so the inspector samples the blob store this composition
            // built; a replacement (`withFactStoreImplementation`) never
            // builds one, and the inspector samples nothing.
            .AddSingleton<DevDiagnosticsHandler.IIndexConsistencyInspector>(
                Func<IServiceProvider, DevDiagnosticsHandler.IIndexConsistencyInspector>(fun sp ->
                    { new DevDiagnosticsHandler.IIndexConsistencyInspector with
                        member _.Inspect(scopeId) = async {
                            sp.GetService<IFactStore>() |> ignore

                            match composedBlob.Instance with
                            | Some blob -> return! blob.IndexConsistencyCheck(scopeId, 20)
                            | None -> return []
                        }
                    })
            )
            .AddSingleton<IFactEvidenceSource>(
                Func<IServiceProvider, IFactEvidenceSource>(fun sp ->
                    FactStoreEvidenceSource.create (sp.GetRequiredService<IFactStore>()))
            )
            // The provenance graph, composed on the SAME knob as the
            // evidence source that feeds it. It was constructed ad hoc at
            // every call site — the Phase 524 traversal, Phase 560's
            // `AnswerPlanProvenance.chainForMessage`, and the certificate
            // issuer below all rebuilt `lineage + evidence source` by
            // hand — so a deployment that had composed the fact tier
            // still had no `IProvenanceGraph` to resolve, and an answer
            // surface wanting a one-call "show the working" API had to
            // re-derive the join.
            //
            // Registered with the same `AddSingleton` shape as its
            // siblings here, so a deployment that registers its own
            // graph later in the compose chain still wins by last-wins;
            // this is the floor, not an override.
            //
            // Built through `createWith` rather than `createWithFacts`
            // because the artifact leg is an OPTION the graph already
            // models: `IArtifactProvenanceSource` (Phase 646) is filled
            // by `ModelArtifactProvenance.source` over an
            // `IModelRegistry` and is not registered by any forge
            // compose today, so `tryService` resolves `None` and the
            // graph is byte-for-byte the `createWithFacts` one it
            // replaces (GP 11). A deployment that DOES register the
            // source gets the artifact hops with no second knob.
            .AddSingleton<IProvenanceGraph>(
                Func<IServiceProvider, IProvenanceGraph>(fun sp ->
                    ProvenanceGraph.createWith
                        (resolveLineage sp)
                        (tryService<IFactEvidenceSource> sp)
                        (tryService<IArtifactProvenanceSource> sp))
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

                    let taint = tryService<DisclosureTaintConfig> sp

                    let purpose = tryService<DisclosurePurposeConfig> sp

                    // Phase 675 — declassification budgets arm the same
                    // way, off an optional `DeclassificationBudgetConfig`
                    // registration (the `withDeclassificationBudgets`
                    // compose below); unregistered ⇒ the facet is absent
                    // and the gate is byte-for-byte the pre-675 one.
                    let budgets = tryService<DeclassificationBudgetConfig> sp

                    let gate =
                        FactDisclosureGate.createConfiguredWithBudgets taint purpose budgets store events

                    // The per-check read fan-out: an optional
                    // `DisclosureFanOut` registration (the
                    // `withDisclosureFanOut` compose below) replaces the
                    // gate's default bound.
                    match tryService<DisclosureFanOut> sp, gate with
                    | Some bound, (:? FactDisclosureGate as concrete) -> concrete.WithFanOut bound
                    | _ -> gate)
            )
            // Phase 1125 — the held form of whichever gate is composed last
            // (`withTeamOutputVisibility` replaces the gate above), resolved
            // through DI rather than registered beside it, so the two can
            // never disagree: the platform gate is its own held form, and a
            // decorating gate is judged through its id form, never bypassed.
            // In-process doors that have just read their facts (the fact
            // browse) take this; every other door keeps the id form.
            .AddSingleton<IHeldFactDisclosureGate>(
                Func<IServiceProvider, IHeldFactDisclosureGate>(fun sp ->
                    FactDisclosureGate.held (sp.GetRequiredService<IFactDisclosureGate>()))
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
                    let registry = tryService<Grounding.IMetricRegistry> sp

                    // Phase 706 — the question seam: one 67b call compiles
                    // point triples AND population triples, so superlative
                    // and aggregate questions reach a `UseAggregate` step
                    // instead of degrading to an unanswerable point lookup.
                    // A deployment with no provider refuses with the same
                    // typed missing-compiler reason it always did.
                    //
                    // Phase 986 — the compiler is chosen in this order: a
                    // deployment's explicit choice
                    // (`withQuestionCompiler`, which is also how one opts
                    // out); a registered `IAIProvider` (the pre-986 route,
                    // unchanged); else the AI tier's `IAIProviderFactory`,
                    // resolved per plan under the planning principal — the
                    // registration a standard AI composition actually
                    // makes, so the push door fires without any extra
                    // wiring. Nothing composed ⇒ the typed refusal, and
                    // the `fact-question-compiler` startup warning names
                    // the dormant door.
                    let store = sp.GetRequiredService<IFactStore>()
                    let gate = sp.GetRequiredService<IFactDisclosureGate>()
                    let events = sp.GetRequiredService<IEventStore>()

                    let fixedCompiler compiler =
                        AnswerPlanner.createCompiling store gate registry events compiler

                    match tryService<ExplicitQuestionCompiler> sp with
                    | Some explicitChoice -> fixedCompiler explicitChoice.Compiler
                    | None ->
                        match tryService<ToolUp.Platform.AI.IAIProvider> sp with
                        | Some provider -> fixedCompiler (AnswerPlanner.structuredQuestionCompiler provider registry)
                        | None ->
                            match tryService<ToolUp.AI.IAIProviderFactory> sp with
                            | Some factory ->
                                AnswerPlanner.createOverProviderFactory store gate registry events factory
                            | None -> fixedCompiler AnswerPlanner.noQuestionCompiler)
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

                    // The composed graph, not a second one built here.
                    // Both are registered on this same knob, so the
                    // resolve cannot fail — and a deployment that swapped
                    // the graph now has its certificates walk the graph
                    // it swapped in, which is what registering one is for.
                    let graph = sp.GetRequiredService<IProvenanceGraph>()

                    let signer = tryService<ToolUp.ArtefactSigning.IArtefactSigner> sp

                    let store = sp.GetRequiredService<IFactStore>()
                    let gate = sp.GetRequiredService<IFactDisclosureGate>()

                    match tryService<IAuditLog> sp with
                    | Some audit -> GroundingCertificate.createIssuerAudited graph store gate events signer audit
                    | None -> GroundingCertificate.createIssuer graph store gate events signer)
            )
            // Phase 985 — the narrative grounding gate rides the store knob
            // too: a grounded narrative run composed anywhere finds a gate
            // whenever the fact tier is, and finds none (and refuses every
            // narrative) when it is not. Declared reference kinds are every
            // `ICitableReferenceKind` the deployment registered, and a
            // `calc:` reference is derived again against the calculations
            // `withNarrativeCalculations` registered (none, no kind).
            .AddSingleton<INarrativeGroundingGate>(
                Func<IServiceProvider, INarrativeGroundingGate>(fun sp ->
                    FactNarrativeGroundingGate.createWithCalculations
                        (tryService<NarrativeCalculations> sp
                         |> Option.defaultValue NarrativeCalculations.empty)
                        (sp.GetRequiredService<IFactStore>())
                        (sp.GetRequiredService<IFactDisclosureGate>())
                        (tryService<Grounding.IMetricRegistry> sp)
                        (sp.GetServices<ICitableReferenceKind>() |> List.ofSeq)
                        (sp.GetRequiredService<IGroundingCertificateIssuer>()))
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

    // Phase 888 — the blob store's multi-replica scale guard. Registers
    // nothing at one replica (GP 11). The scopes it counts come from the
    // composed `IScopeEnumerator`, else from the team store, else the two
    // well-known containers.
    let private registerBlobScaleGuard (replicaCount: int) (services: IServiceCollection) : IServiceCollection =
        if replicaCount <= 1 then
            services
        else
            services.TryAddSingleton<FactStoreBackend>({ Backend = "blob" })

            // Phase 946 — an INSTANCE, as the preflight requires (it reads
            // `ImplementationInstance` at compose time and refuses a factory,
            // which made a multi-replica composition raise). The guard is
            // built when it runs, from the instance registrations of the
            // collection it was registered into — the platform registers the
            // blob storage, the team store and this backend marker as
            // instances — so it sees the composition as finally assembled.
            services.AddSingleton<ConfigValidation.IConfigValidator>(
                BlobFactStoreScaleGuard(replicaCount, services) :> ConfigValidation.IConfigValidator
            )

    // Once per collection: validator names are unique at preflight, and a
    // composition that applies the fact tier twice must not refuse to start
    // over a duplicate warning.
    let private registerQuestionCompilerGuard (services: IServiceCollection) : IServiceCollection =
        let present =
            services
            |> Seq.exists (fun d -> not d.IsKeyedService && (d.ImplementationInstance :? FactQuestionCompilerGuard))

        if present then
            services
        else
            services.AddSingleton<ConfigValidation.IConfigValidator>(
                FactQuestionCompilerGuard(services) :> ConfigValidation.IConfigValidator
            )

    /// The fact-table change signal, dormant until a reader registers its
    /// demand (`FactTableChangeSignal.demand`); it raises across processes
    /// through the composed notification channel.
    let private registerTableChangeSignal (services: IServiceCollection) : IServiceCollection =
        services.TryAddSingleton<IFactTableChangeSignal>(
            Func<IServiceProvider, IFactTableChangeSignal>(fun sp ->
                let demanded = lazy (FactTableChangeSignal.demanded sp)
                FactTableChangeSignal.create (tryService<INotificationChannel> sp) (fun () -> demanded.Value))
        )

        services

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
        match ServiceComposition.captureDescriptor<IDataObjectStore> services with
        | Some captured when (captured.Descriptor.ImplementationInstance :? IDataObjectStore) ->
            services.AddSingleton<IDataObjectStore>(
                Func<IServiceProvider, IDataObjectStore>(fun sp ->
                    let scheduler () = tryService<IJobScheduler> sp

                    let registry () =
                        tryService<Grounding.IMetricRegistry> sp

                    // Phase 985 — grounded narratives react to the same
                    // arrival, after the recompute reaction (so an Eager
                    // recompute is scheduled before the narrative job that
                    // waits for it), and arm the decorator on their own.
                    let narratives () =
                        tryService<GroundedNarrativeRegistry> sp

                    // A reader of the fact-table change signal hears a
                    // landed data object as a change of any table of its
                    // scope, after the recompute and narrative reactions.
                    let tableChangeDemanded = lazy (FactTableChangeSignal.demanded sp)

                    let react =
                        ReactiveDataChangeComposition.combine
                            (ReactiveDataChangeComposition.combine
                                (ReactiveDataChange.reaction
                                    (fun () -> sp.GetRequiredService<IFactStore>())
                                    (fun () -> resolveLineage sp)
                                    scheduler
                                    registry)
                                (GroundedNarrativeTrigger.reaction
                                    (fun () -> sp.GetRequiredService<IFactStore>())
                                    (fun () -> resolveLineage sp)
                                    scheduler
                                    narratives))
                            (FactTableChangeSignal.reaction (fun () -> sp.GetRequiredService<IFactTableChangeSignal>()))

                    ReactiveDataChange.decorate
                        (captured.Resolve sp)
                        (ReactiveDataChangeComposition.either
                            (ReactiveDataChangeComposition.either
                                (ReactiveDataChange.gate registry scheduler)
                                (GroundedNarrativeTrigger.armed scheduler narratives))
                            (fun () -> tableChangeDemanded.Value))
                        (ReactiveDataChange.requestScope (fun () ->
                            tryService<Microsoft.AspNetCore.Http.IHttpContextAccessor> sp))
                        react
                        (sp.GetRequiredService<ILogger>()))
            )
            |> ignore
        | _ -> ()

        // (2) Register + schedule the recompute handler at startup, once
        //     the container that owns `IFactStore` / `IFactRecomputer`
        //     exists. `Trigger.Manual`: recompute jobs are fired on demand
        //     by `reactToDataChange`, never on a cadence.
        //     Exempt from the profile matrix (Phase 1051), like (3) below: a
        //     scheduler registration, placed by the scheduler's own gate.
        (services
         |> ProcessProfileGate.exempt "Reactive fact recomputation" ProcessProfileGate.SchedulerRegistrationReason)
            .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
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

        // Phase 985 — the grounded-narrative job handler, registered with
        // the scheduler at startup only when a run registry and a run are
        // composed; otherwise the start is a DI lookup and nothing else.
        services
        |> ProcessProfileGate.addExempt
            "Grounded-narrative handler registration"
            ProcessProfileGate.SchedulerRegistrationReason
            GroundedNarrativeTrigger.hostedService
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
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withFactStore (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            // Phase 623 — the fact tier and its reactive activation are
            // one registration pass: the store first, then the recompute
            // engine + handler + data-arrival hook over it.
            let register (s: IServiceCollection) =
                registerReactiveRecomputation (registerTableChangeSignal (registerFactStore s))
                // Phase 888 — the backend marker and, for a multi-replica
                // deployment only, the blob store's scale guard.
                |> registerBlobScaleGuard app.Config.ReplicaCount
                // Phase 986 — the dormant-push-door warning.
                |> registerQuestionCompilerGuard
                // The undescribed-declarations report (information only).
                |> FactDeclarationsValidatorRegistration.register

            {
                app with
                    // The fact tools are declared with the store (one
                    // compose knob, never two). The declaration rides
                    // `ServerApp.AITools`, which only the AI companion's
                    // compose reads into the live AI tool registry — so the
                    // tools arm exactly when both the fact store AND the AI
                    // companion are composed (GP 13): no fact store ⇒ never
                    // declared; no AI ⇒ never registered, no route, no
                    // runtime cost.
                    //
                    // They are siblings, not alternatives, and one knob
                    // declares them all. `query_facts` answers about a
                    // subject; `query_metric_population` ranks a metric
                    // across a population and summarises what it ranked
                    // over; `aggregate_metric` sums a metric the registry
                    // declares additive by group, so a total is the
                    // server's arithmetic over gated facts, never the
                    // model's. They take ids and none can list them, so the
                    // discovery surfaces ride the same knob:
                    // `list_metric_coverage` lists by metric and
                    // `list_fact_tables` by table. A deployment that armed
                    // the reads without discovery would leave the model
                    // guessing ids — and a guessed id is refused, not
                    // approximated. Arming discovery separately would make
                    // that misconfiguration possible; one knob makes it
                    // unreachable. The list is the grounded narrative's
                    // (`NarrativeGrounding.factTools`), so the two cannot
                    // drift apart.
                    AITools = app.AITools @ NarrativeGrounding.factTools
            }
            |> contribute register

    /// **Adds** the deployment's authorised narrative calculations: the
    /// grounding gate then derives every `calc:` reference a narrative cites
    /// again against them, and admits it only when it holds. Give a grounded
    /// run the `calculate` tool from the same value
    /// (`NarrativeCalculations.tools`). Empty calculations change nothing, as
    /// does a deployment with no fact store; registering calculations twice
    /// is refused.
    let withNarrativeCalculations (calculations: NarrativeCalculations) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore when NarrativeCalculations.isEmpty calculations -> app
        | EnabledFactStore ->
            app
            |> contribute (fun s ->
                if s |> Seq.exists (fun d -> d.ServiceType = typeof<NarrativeCalculations>) then
                    ServiceComposition.refuse (
                        CompositionRefusal.InvalidDeclaration(
                            "FactsCompose.withNarrativeCalculations",
                            "narrative calculations are already registered; declare them all in one NarrativeCalculations value"
                        )
                    )
                else
                    s.AddSingleton<NarrativeCalculations>(calculations))

    /// Declare what `list_fact_tables` lists: the cap on the distinct members
    /// a level may hold and still be named (`FactTablesOptions.MemberCap`, `0`
    /// turns member listing off). Call it after `withFactStore`, which declares
    /// the tool; the options are captured here as a value and the executor
    /// reads the container for nothing. With no fact store composed it changes
    /// nothing, and calling it before `withFactStore` is refused.
    let withFactTablesOptions (options: FactTablesOptions) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let declared (definition: AIToolDefinition, _) = definition.Name = FactTablesTool.Name

            if not (List.exists declared app.AITools) then
                ServiceComposition.refuse (
                    CompositionRefusal.MissingDependency(
                        "FactsCompose.withFactTablesOptions",
                        FactTablesTool.Name,
                        "call FactsCompose.withFactStore first, which declares it"
                    )
                )
            else
                {
                    app with
                        AITools =
                            app.AITools
                            |> List.map (fun (definition, executor) ->
                                if declared (definition, executor) then
                                    definition, FactTablesTool.executeFor options
                                else
                                    definition, executor)
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

            app
            |> contribute register
            |> ServerApp.withRegisteredPurposes (registeredPurposes config)

    // ─── Phase 675 — declassification budgets (opt-in) ────────────────
    //
    // A separate, explicit opt-in on top of the fact store — the shape
    // every opt-in around it takes, and here because it needs a LEDGER.
    // Folding it into `withFactStore` would compose durable, stateful
    // accounting into every deployment that wanted a fact base.
    //
    // The honesty framing this mechanism is sold under lives in full in
    // `DeclassificationBudget.fs` and is restated on the helper below,
    // because a compose helper is where a deployment decides what to
    // believe about it. Its four points, since they decide whether this
    // knob is the control a reader thinks it is:
    //
    //   * The accounting bounds **questions asked**, not information
    //     disclosed. A declassification routine is a DETERMINISTIC
    //     transform, and summing charges over deterministic answers
    //     bounds nothing formally.
    //   * Only a routine that draws calibrated noise from an
    //     `INoiseMechanism` (Phase 481) earns a differential-privacy
    //     claim, and only such a routine may declare a chargeable
    //     epsilon — refused here, at registration, not in prose.
    //   * Composition is **basic (sequential)**: charges add (Dwork &
    //     Roth, Theorem 3.16). No advanced composition is offered.
    //   * **Collusion is out of scope.** Two contributing parties that
    //     share answers are one adversary with two budgets.

    /// Register a declassification-budget config the deployment built
    /// itself, arming the gate's budget facet. The general form of the
    /// helper below, and — since Phase 679 — the one entry point that
    /// can carry the AMENDMENT facet, because a countersigned amendment
    /// needs a registry and a roster the two-argument helper has no way
    /// to receive:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withDeclassificationBudgetConfig (
    ///        DeclassificationBudgetConfig.create
    ///            (BlobPrivacyBudgetLedger blob)
    ///            [ DeclassificationBudget.countedCrossings "aggregate-over-k" 500 ]
    ///        |> DeclassificationBudgetConfig.withAmendments amendments)
    /// |> ServerApp.run
    /// ```
    ///
    /// A separate entry point rather than a widened
    /// `withDeclassificationBudgets`: that signature is public surface a
    /// consumer binds against, and adding a parameter to it would delete
    /// the existing token for every deployment already composing
    /// budgets. The two share one body, so there is no second
    /// registration path to drift.
    ///
    /// A `NoFactStore` deployment is byte-for-byte unchanged (GP 11 /
    /// GP 13).
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withDeclassificationBudgetConfig (config: DeclassificationBudgetConfig) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register (s: IServiceCollection) =
                s.AddSingleton<DeclassificationBudgetConfig>(config)

            app |> contribute register

    /// Compose declassification budgets (Phase 675): register the
    /// declared budgets + the ledger they account through, so the gate
    /// factory above arms the budget facet. Each declaration names a
    /// Phase 562 declassification routine by operation id; a crossing of
    /// that routine reserves its charge before the disclosure verdict and
    /// settles it after, **per contributing party** (Phase 674). An
    /// exhausted ceiling denies with the same typed, audited refusal
    /// shape a policy denial takes (GP 6) — never a silent allow.
    ///
    /// A `NoFactStore` deployment (or one that never calls this) is
    /// byte-for-byte unchanged (GP 11 / GP 13). Insert after
    /// `withFactStore`:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withDeclassificationBudgets
    ///        (BlobPrivacyBudgetLedger blob)
    ///        [ DeclassificationBudget.countedCrossings "aggregate-over-k" 500 ]
    /// |> ServerApp.run
    /// ```
    ///
    /// **This is an accounting control, not a differential-privacy
    /// guarantee.** `countedCrossings` bounds how many times a routine
    /// may be crossed — a count of questions asked, which is exactly what
    /// the number is for a deterministic transform. `chargedEpsilon` is
    /// available only to a routine that names the `INoiseMechanism` it
    /// draws calibrated noise from, and the registration below REFUSES
    /// the combination otherwise rather than documenting against it.
    /// Charges compose sequentially (Σεᵢ, Dwork & Roth Theorem 3.16); no
    /// advanced composition is offered, and collusion between
    /// contributing parties is out of scope — two parties that share
    /// answers are one adversary with two budgets.
    ///
    /// Raises at compose time on an unenforceable declaration (an
    /// epsilon charge with no noise mechanism, a ceiling at or below
    /// zero, two budgets for one routine). Loud and early rather than at
    /// the first disclosure: a privacy control that fails late fails
    /// after something has already been disclosed.
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withDeclassificationBudgets
        (ledger: IPrivacyBudgetLedger)
        (budgets: DeclassificationBudget list)
        (app: ServerApp)
        : ServerApp =
        // Validated HERE, at compose, not inside the DI factory: a
        // factory throws at first resolve, which is after the deployment
        // believes it booted clean.
        withDeclassificationBudgetConfig (DeclassificationBudgetConfig.create ledger budgets) app

    /// Bound how many fact reads one disclosure check holds in flight. A
    /// deployment that does not compose this runs the gate at
    /// `DisclosureFanOut.DefaultMaxConcurrentReads`; a bound below 1 is
    /// raised to 1. A `NoFactStore` deployment is unchanged.
    ///
    /// ```fsharp skip=fragment
    /// ServerApp.create config
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withDisclosureFanOut 8
    /// ```
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withDisclosureFanOut (maxConcurrentReads: int) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let bound: DisclosureFanOut = {
                MaxConcurrentReads = max 1 maxConcurrentReads
            }

            let register (s: IServiceCollection) = s.AddSingleton<DisclosureFanOut>(bound)

            app |> contribute register

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
    /// (`PeerTrustAnchor.withCeiling`) and the set of attestation levels it
    /// admits (`PeerTrustAnchor.withAdmissibleLevels`). No key is
    /// discovered, so no key is implicitly trusted.
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
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

            app |> contribute register

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
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
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

            app |> contribute register

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
        match
            ServiceComposition.captureDescriptor<IFactStore> services
            |> Option.map _.Resolve
        with
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
    ///        (CoverageNarrative.CoverageNarrativeOptions.forScopes "coverage-reader" [ tenantScopeId ])
    /// |> ServerApp.run
    /// ```
    ///
    /// Insert AFTER `withFactStore` — it decorates what that registered,
    /// and finds nothing to decorate if it runs first.
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withCoverageNarratives (options: CoverageNarrative.CoverageNarrativeOptions) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register = registerCoverageNarratives options

            app |> contribute register

    // ─── Phase 563 — fact-base coherence checking (opt-in) ────────────
    //
    // A separate, explicit opt-in on top of the fact store: the standing
    // self-audit is NOT folded into `withFactStore`, so a deployment that
    // only wants the store is byte-for-byte unchanged (GP 11 / GP 13). The
    // registrations are `Contribution.service` values under the companion's id:
    //
    //   * an `IHostedService` that, once the container is built, resolves
    //     the composed substrate + the scheduler and schedules the
    //     coherence job on the opt-in `cadence` (mirrors the model-fit /
    //     DSR startup-registration pattern — the scheduler and the metric
    //     registry are only resolvable post-`Build`);
    //   * an `IHealthCheck` that re-scans the configured scope on each
    //     probe and reports `Degraded` when any finding stands.

    // Phase 889 — the store a coherence sweep reads. When delegate facts are
    // composed it is the delegated store itself, so the sweep takes the
    // delegated path (`CoherenceCheck.findings`) whatever decorates it above;
    // the sweep only reads, so bypassing an assertion-hook decorator costs
    // nothing. Otherwise it is the composed store, exactly as before.
    let private coherenceStore (sp: IServiceProvider) : IFactStore =
        match tryService<IDelegatedFactWalks> sp with
        | Some walks -> walks :> IFactStore
        | None -> sp.GetRequiredService<IFactStore>()

    /// A declared principal's scope, resolved through the platform's
    /// `DeclaredPrincipalResolver` — the resolver seam declared work takes, so
    /// a sweep never reads a shard nobody resolved.
    let private resolveDeclared
        (sp: IServiceProvider)
        (principal: DeclaredPrincipal)
        : Async<Result<ResolvedScope, string>> =
        async {
            match tryService<DeclaredPrincipalResolver> sp with
            | None ->
                return
                    Error
                        "this deployment composes no DeclaredPrincipalResolver — compose the server through the platform, whose scope-resolution registration provides it"
            | Some resolver ->
                let! resolved = resolver.Resolve(DeclaredPrincipal.user principal)

                return
                    resolved
                    |> Result.mapError (fun e ->
                        sprintf
                            "the declared principal '%s' lacks %s"
                            principal.UserId
                            (DeclaredPrincipal.missingGrant e))
        }

    let private registerCoherenceChecks
        (config: CoherenceConfig)
        (cadence: Trigger)
        (principals: DeclaredPrincipal list)
        (services: IServiceCollection)
        : IServiceCollection =
        // The health probe surfaces current findings for the scope the
        // declared health principal resolves to — resolved and re-scanned per
        // call (GP 12 rule 4). No principal, no probe.
        match config.HealthPrincipal with
        | Some principal ->
            services.AddSingleton<HealthChecks.IHealthCheck>(
                Func<IServiceProvider, HealthChecks.IHealthCheck>(fun sp ->
                    CoherenceHealthCheck.create
                        (coherenceStore sp)
                        (tryService<Grounding.IMetricRegistry> sp)
                        config
                        (fun () -> resolveDeclared sp principal))
            )
            |> ignore
        | None -> ()

        services
            // Schedule the standing check on the opt-in cadence. The
            // scheduler + metric registry are only resolvable from the built
            // container, so registration happens in a startup hosted service
            // — exempt from the profile matrix (Phase 1051): a scheduler
            // registration, placed by the scheduler's own gate.
            .AddSingleton<HostedServiceExemption>(
                {
                    Service = "Fact coherence check scheduling"
                    Reason = ProcessProfileGate.SchedulerRegistrationReason
                }
            )
            .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                    { new Microsoft.Extensions.Hosting.IHostedService with
                        // The schedule calls are awaited here, in the hosted
                        // start, never run synchronously: a slow scheduler
                        // delays this start step, not the thread that is
                        // composing the container.
                        member _.StartAsync(ct) =
                            let logger = sp.GetRequiredService<ILogger>()

                            match tryService<IJobScheduler> sp with
                            | Some scheduler ->
                                let handler =
                                    CoherenceJobHandler.create
                                        (coherenceStore sp)
                                        (tryService<Grounding.IMetricRegistry> sp)
                                        (sp.GetRequiredService<INotificationChannel>())
                                        (sp.GetRequiredService<IEventStore>())
                                        config
                                        (fun () -> DateTime.UtcNow)
                                        logger

                                scheduler.RegisterHandler(CoherenceJobHandler.HandlerName, handler)

                                let scheduleAll = async {
                                    for principal in principals do
                                        match! resolveDeclared sp principal with
                                        | Error reason ->
                                            logger.Warn(sprintf "The coherence check was not scheduled: %s" reason)
                                        | Ok scope ->
                                            let scopeId = scope.ScopeId

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

                                            match! scheduler.Schedule(scope, registration) with
                                            | Ok _ -> ()
                                            | Error err ->
                                                logger.Warn(
                                                    sprintf
                                                        "Failed to schedule the coherence check in scope %s: %A"
                                                        scopeId
                                                        err
                                                )
                                }

                                Async.StartAsTask(scheduleAll, cancellationToken = ct) :> System.Threading.Tasks.Task
                            | None ->
                                logger.Warn(
                                    "Coherence checking enabled but JobScheduler = NoJobScheduler — the standing check is not scheduled (the on-demand path and the /ready health probe still work). Pair with JobScheduler = InProcessJobScheduler."
                                )

                                System.Threading.Tasks.Task.CompletedTask

                        member _.StopAsync(_ct) =
                            System.Threading.Tasks.Task.CompletedTask
                    })
            )
        |> ignore

        services

    /// Compose the standing fact-base coherence check with an explicit
    /// `config` + `cadence` (a cron `Trigger`) + the declared `principals`
    /// whose scopes to sweep — each resolved through the platform's
    /// `DeclaredPrincipalResolver` when the sweep is scheduled, and the job
    /// runs under the scope it resolved to (empty ⇒ no sweep is scheduled: a
    /// sweep never reads a shard nobody resolved). Registers the coherence
    /// `IHealthCheck` when `config.HealthPrincipal` names one, and the
    /// scheduled sweep, on top of an already-enabled fact store. A
    /// `NoFactStore` deployment (or one that never calls this) is
    /// byte-for-byte unchanged (GP 11 / GP 13). Insert after
    /// `withFactStore`:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withCoherenceChecksConfig cfg cadence [ sweepPrincipal ]
    /// |> ServerApp.run
    /// ```
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withCoherenceChecksConfig
        (config: CoherenceConfig)
        (cadence: Trigger)
        (principals: DeclaredPrincipal list)
        (app: ServerApp)
        : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register = registerCoherenceChecks config cadence principals

            app |> contribute register

    /// `withCoherenceChecksConfig` with `CoherenceConfig.defaults`, sweeping
    /// and health-probing the scope `principal` resolves to — the one-liner
    /// opt-in. `cadence` is the recurring `Trigger` the standing check runs on
    /// (e.g. a daily `CronTrigger`); the check is also fireable on demand and
    /// re-scanned by the `/ready` health probe.
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withCoherenceChecks (cadence: Trigger) (principal: DeclaredPrincipal) (app: ServerApp) : ServerApp =
        withCoherenceChecksConfig
            {
                CoherenceConfig.defaults with
                    HealthPrincipal = Some principal
            }
            cadence
            [ principal ]
            app

    // ─── Phase 887 — the default fact-table writer (opt-in) ───────────
    //
    // Declared fact tables (`ServerModule.declareFactTables`) are written
    // through `IFactTableWriter`. This registers the default writer — over
    // the composed `IFactStore`, so a declared table is usable before any
    // dedicated table store is composed — and binds every declared table to
    // it. A separate opt-in on top of the fact store: a deployment that only
    // wants the store is byte-for-byte unchanged (GP 11 / GP 13).

    /// Compose the default `IFactTableWriter` and bind every declared fact
    /// table to it (`BindAllFactTables DefaultFactTableWriter.Destination`).
    ///
    /// Requires the fact store (`ServerConfig.FactStore = EnabledFactStore`,
    /// composed with `withFactStore`). Under `NoFactStore` this returns the
    /// app unchanged and binds nothing, exactly as `withFactStore` does — so
    /// a composition declaring a `Required` table then refuses to start at
    /// the fact-table preflight, naming the table, rather than failing here.
    ///
    /// The writer resolves the table registry and the metric registry from
    /// DI when first used, so modules may be added before or after this call.
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withFactTableWriter (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register (s: IServiceCollection) =
                s.AddSingleton<IFactTableWriter>(
                    Func<IServiceProvider, IFactTableWriter>(fun sp ->
                        let writer =
                            DefaultFactTableWriter.create
                                (sp.GetRequiredService<IFactStore>())
                                (sp.GetRequiredService<IBlobStorage>())
                                (sp.GetRequiredService<IEventStore>())
                                (tryService<Grounding.IFactTableRegistry> sp
                                 |> Option.defaultValue Grounding.FactTableRegistry.empty)
                                (tryService<Grounding.IMetricRegistry> sp)

                        // A committed run raises its table's change, for a
                        // reader that keeps what it read.
                        match tryService<IFactTableChangeSignal> sp with
                        | Some signal -> FactTableChangeSignal.raisingWriter signal writer
                        | None -> writer)
                )

            app
            |> contribute register
            |> ServerApp.bindFactTables (Grounding.BindAllFactTables DefaultFactTableWriter.Destination)

    // ─── Phase 986 — the fact tier as one composition, and its compiler ─

    /// The whole fact tier in one call: turn the fact store on
    /// (`ServerConfig.FactStore = EnabledFactStore`), then `withFactStore`
    /// and `withFactTableWriter` over it. Because it sets the knob itself,
    /// it does not depend on a `withConfig` having run first.
    ///
    /// For a RAG app, hand it to `RAGServerApp.withFacts`, which applies it
    /// when the app is composed — after every `withConfig`, wherever the
    /// call sits in the pipeline:
    ///
    /// ```fsharp
    /// RAGServerApp.create factory providerProfile embedder
    /// |> RAGServerApp.withFacts FactsCompose.withFactTier
    /// |> RAGServerApp.withConfig config
    /// ```
    ///
    /// What it arms: the fact tools (`query_facts` and its siblings, once
    /// the AI tier is composed) and the fact store's push door — a
    /// question compiled into a fact clause ahead of vector retrieval —
    /// which compiles through the AI tier's `IAIProviderFactory` unless a
    /// compiler is chosen with `withQuestionCompiler`.
    let withFactTier (app: ServerApp) : ServerApp =
        {
            app with
                Config = {
                    app.Config with
                        FactStore = EnabledFactStore
                }
        }
        |> withFactStore
        |> withFactTableWriter

    /// Choose the answer planner's question compiler explicitly (Phase
    /// 986). It outranks a registered `IAIProvider` and the AI tier's
    /// `IAIProviderFactory`, which the planner otherwise compiles through.
    /// Opt the push door out deliberately — and silence the startup
    /// warning a compiler-less planner raises — with
    /// `withQuestionCompiler AnswerPlanner.noQuestionCompiler`.
    ///
    /// Registers the choice whatever the fact-store knob says; it has an
    /// effect only where the fact tier is composed, before or after this
    /// call. The last choice registered wins.
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withQuestionCompiler (compiler: QuestionCompiler) (app: ServerApp) : ServerApp =
        let register (s: IServiceCollection) =
            s.AddSingleton<ExplicitQuestionCompiler>(ExplicitQuestionCompiler compiler)

        app |> contribute register

    // ─── Phase 888 — a replacement IFactStore behind the same knob ─────
    //
    // `withFactStore` composes the blob default. A deployment that has
    // outgrown it swaps in another `IFactStore` — the indexed companion
    // `ToolUp.FactStores.Postgres` is the shipped one — without losing a
    // single registration built over the store: the evidence source, the
    // disclosure gate, the resolver, the provenance graph, reactive
    // recomputation and the fact tools all resolve `IFactStore` from DI, so
    // they follow the replacement.

    /// Replace the composed `IFactStore` with `factory`'s store, keeping
    /// every registration built over it (Phase 888). `backend` names the
    /// implementation for introspection and for the blob store's
    /// multi-replica scale guard, which stands down once the blob store is
    /// no longer the one composed.
    ///
    /// Requires the fact store (`ServerConfig.FactStore = EnabledFactStore`);
    /// under `NoFactStore` this returns the app unchanged, exactly as
    /// `withFactStore` does. Insert straight AFTER `withFactStore` and
    /// before any knob that decorates the store (`withCoverageNarratives`):
    /// the replacement removes every `IFactStore` registration made before
    /// it, so a later `withFactStore` would put the blob store back and an
    /// earlier decorator would be dropped:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withFactStoreImplementation "postgres" (fun sp -> myStore sp)
    /// |> ServerApp.run
    /// ```
    ///
    /// A companion normally wraps this in its own builder
    /// (`PostgresFactStoreCompose.withPostgresFactStore`).
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withFactStoreImplementation
        (backend: string)
        (factory: IServiceProvider -> IFactStore)
        (app: ServerApp)
        : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register (s: IServiceCollection) =
                s.RemoveAll<IFactStore>() |> ignore

                s.AddSingleton<IFactStore>(Func<IServiceProvider, IFactStore>(factory))
                |> ignore

                s.RemoveAll<FactStoreBackend>() |> ignore
                s.AddSingleton<FactStoreBackend>({ Backend = backend })

            app |> contribute register
    // ─── Phase 889 — delegate facts (opt-in) ──────────────────────────
    //
    // A declared fact table can hold its metric populations as delegates:
    // the table is kept as rows (one image per committed run), the fact tier
    // holds one record per delegate plus exactly what an answer quoted, and
    // a read of a delegated metric is pushed down to the table. Everything is
    // a DECORATION of what the composition already registered — the fact
    // store and the table writer — so the tools, the planner, the disclosure
    // gate and the coverage narrative resolve the same `IFactStore` they
    // always did. A composition that never calls this is byte-for-byte
    // unchanged (GP 11 / GP 13).

    let private registerDelegateFacts (tableIds: string list) (services: IServiceCollection) : IServiceCollection =
        match
            ServiceComposition.captureDescriptor<IFactStore> services
            |> Option.map _.Resolve
        with
        // No fact store to decorate: left untouched, as the coverage
        // narrative does — the fact tier's own registrations surface that
        // composition defect far more clearly than a decorator would.
        | None -> services
        | Some resolveInner ->
            let resolveWriter =
                ServiceComposition.captureDescriptor<IFactTableWriter> services
                |> Option.map _.Resolve

            // The delegate records, resolved once from the composed table and
            // metric registries. A table that cannot be delegated fails the
            // first resolution, naming the table, rather than serving reads
            // over a declaration it does not understand.
            let delegatesOf (sp: IServiceProvider) : DelegateFact list =
                let tables =
                    tryService<Grounding.IFactTableRegistry> sp
                    |> Option.defaultValue Grounding.FactTableRegistry.empty

                match DelegateFacts.resolve tables (tryService<Grounding.IMetricRegistry> sp) tableIds with
                | Ok delegates -> delegates
                | Error reason ->
                    ServiceComposition.refuse (
                        CompositionRefusal.InvalidDeclaration("FactsCompose.withDelegateFacts", reason)
                    )

            services.AddSingleton<DelegatedFactStore>(
                Func<IServiceProvider, DelegatedFactStore>(fun sp ->
                    DelegatedFactStore.create
                        (resolveInner sp)
                        (sp.GetRequiredService<IBlobStorage>())
                        (delegatesOf sp)
                        (tryService<Grounding.IMetricRegistry> sp)
                        (fun () -> DateTime.UtcNow))
            )
            |> ignore

            services.AddSingleton<IFactStore>(
                Func<IServiceProvider, IFactStore>(fun sp -> sp.GetRequiredService<DelegatedFactStore>() :> IFactStore)
            )
            |> ignore

            services.AddSingleton<IDelegatedFactWalks>(
                Func<IServiceProvider, IDelegatedFactWalks>(fun sp ->
                    sp.GetRequiredService<DelegatedFactStore>() :> IDelegatedFactWalks)
            )
            |> ignore

            services.AddSingleton<IFactTableWriter>(
                Func<IServiceProvider, IFactTableWriter>(fun sp ->
                    let delegated = sp.GetRequiredService<DelegatedFactStore>()

                    DelegatedFactStore.writer
                        (resolveWriter |> Option.map (fun resolve -> resolve sp))
                        (sp.GetRequiredService<IBlobStorage>())
                        (sp.GetRequiredService<IEventStore>())
                        (tryService<Grounding.IFactTableRegistry> sp
                         |> Option.defaultValue Grounding.FactTableRegistry.empty)
                        (tryService<Grounding.IMetricRegistry> sp)
                        delegated.Delegates
                        // The COMPOSED store at commit time, so the refresh
                        // reaches whatever decorates it.
                        (fun () -> tryService<IFactStore> sp)
                        (fun () -> DateTime.UtcNow))
            )

    /// Hold the named declared fact tables as delegate facts (Phase 889):
    /// each table is bound to the delegate destination and kept as rows;
    /// each of its columns becomes one delegate record in the fact tier; and
    /// a point or population read of a delegated metric is answered from the
    /// table, minting as ordinary facts only the rows it returns.
    ///
    /// Requires the fact store (`ServerConfig.FactStore = EnabledFactStore`);
    /// under `NoFactStore` this returns the app unchanged. Insert AFTER
    /// `withFactStore` (and after `withFactStoreImplementation`, which drops
    /// every registration made before it) — it decorates what they
    /// registered. `withFactTableWriter` may come before or after: tables
    /// bound here are held by the delegate writer, and every other table is
    /// handed to the writer that composed it.
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withConfig { ServerConfig.defaults with FactStore = EnabledFactStore }
    /// |> ServerApp.addModules [ salesModule ]
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withFactTableWriter
    /// |> FactsCompose.withDelegateFacts [ "sku-sales" ]
    /// ```
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withDelegateFacts (tableIds: string list) (app: ServerApp) : ServerApp =
        match app.Config.FactStore, tableIds with
        | NoFactStore, _
        | _, [] -> app
        | EnabledFactStore, _ ->
            let register = registerDelegateFacts tableIds

            let bound =
                tableIds
                |> List.distinct
                |> List.fold
                    (fun a tableId ->
                        ServerApp.bindFactTables (Grounding.BindFactTable(tableId, DelegateFact.Destination)) a)
                    app

            bound |> contribute register

    // ─── Phase 896 — team output visibility (opt-in) ──────────────────
    //
    // Module permission governs USE; the team's policy governs who sees the
    // OUTPUT modules published. This knob arms the second axis at the one
    // place every egress door already passes: the disclosure gate decides a
    // `Restricted` fact for the viewer — the requester as the platform
    // resolved them, or the least-privileged viewer when there is none.
    //
    // The team's level is read through `ITeamOutputVisibilitySource`, whose
    // implementation lives with the per-team policy record that also holds
    // the team's conversation level — at the platform tier since Phase
    // 936, and registered here, so a deployment without the AI assistant
    // honours each team's choice. A deployment that registers its own
    // source keeps it; with none at all every team sits at the default.
    //
    // Self-contained on purpose: the gate registered by `withFactStore` is
    // decorated in place (`FactDisclosureGate.WithViewerAwareness`), so the
    // registration above is untouched and every facet it composed (taint,
    // purpose, budgets) is kept.

    /// The viewer-aware facet over the composed substrate.
    let private viewerAwareFacet
        (settings: TeamOutputVisibilitySettings)
        (sp: IServiceProvider)
        : ViewerAwareDisclosure =
        let source = tryService<ITeamOutputVisibilitySource> sp
        let teams = tryService<ToolUp.Platform.TeamManagement.ITeamStore> sp

        {
            Resolver = None
            Sources = {
                OutputVisibility =
                    fun teamId ->
                        match source with
                        | Some source -> source.Current teamId
                        | None -> async.Return(Ok settings.Default)
                TeamRole =
                    fun teamId userId ->
                        match teams with
                        | Some teams -> teams.GetMemberRole(teamId, userId)
                        | None -> async.Return None
                IsTeam =
                    fun scopeId ->
                        match teams with
                        | Some teams -> async {
                            let! team = teams.GetTeam scopeId
                            return team.IsSome
                          }
                        | None -> async.Return false
            }
        }

    /// Compose team output visibility (Phase 896): a team may limit who
    /// sees the `Restricted` output its modules publish to the whole team,
    /// the team's admins, or platform admins — the same three levels as
    /// conversation visibility. `defaultLevel` is the level a team starts
    /// with, `allowed` the levels a team owner may choose from; the
    /// declaration is validated here, and an empty allowed set or a default
    /// outside it fails composition.
    ///
    /// Arms: the settings (read by the store that holds each team's
    /// choice), the platform's `ITeamOutputVisibilitySource` over that
    /// store and the startup check that the two deployment defaults do not
    /// conflict (Phase 936), the gate's viewer-aware facet, and the
    /// middleware that establishes the request's viewer. The declaration
    /// is recorded on the app, so the composition manifest projects it. `Surfaceable` facts stay
    /// visible to every viewer the scope admits and `Internal` facts are
    /// never disclosed, exactly as before; module permission is not
    /// consulted, because permission governs use, not sight.
    ///
    /// **Not composing this is the default**, and so is composing it with
    /// `TeamVisible` as the default and no team choosing otherwise: every
    /// check is then decided exactly as before (GP 11). A `NoFactStore`
    /// deployment is unchanged. Insert after `withFactStore`:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withTeamOutputVisibility TeamVisible [ TeamVisible; TeamAdmins ]
    /// |> ServerApp.run
    /// ```
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withTeamOutputVisibility
        (defaultLevel: TeamVisibilityLevel)
        (allowed: TeamVisibilityLevel list)
        (app: ServerApp)
        : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let settings =
                match TeamOutputVisibilitySettings.create defaultLevel allowed with
                | Ok settings -> settings
                | Error message ->
                    ServiceComposition.refuse (
                        CompositionRefusal.InvalidDeclaration("FactsCompose.withTeamOutputVisibility", message)
                    )

            let register (s: IServiceCollection) =
                s.RemoveAll<TeamOutputVisibilitySettings>() |> ignore
                s.AddSingleton<TeamOutputVisibilitySettings>(settings) |> ignore

                // Phase 936 — the platform's source over the per-team record,
                // and the startup check that the two deployment defaults do
                // not conflict (an instance: preflight refuses a factory).
                s.TryAddSingleton<ITeamOutputVisibilitySource>(
                    Func<IServiceProvider, ITeamOutputVisibilitySource>(fun sp ->
                        TeamPolicyStore.TeamPolicyOutputVisibilitySource(sp) :> ITeamOutputVisibilitySource)
                )

                s
                |> Seq.filter (fun d -> d.ImplementationInstance :? TeamPolicyStore.TeamVisibilityDefaultsValidator)
                |> List.ofSeq
                |> List.iter (s.Remove >> ignore)

                s.AddSingleton<ConfigValidation.IConfigValidator>(
                    TeamPolicyStore.TeamVisibilityDefaultsValidator(settings, s) :> ConfigValidation.IConfigValidator
                )
                |> ignore

                match ServiceComposition.captureDescriptor<IFactDisclosureGate> s with
                | None ->
                    ServiceComposition.refuse (
                        CompositionRefusal.MissingDependency(
                            "FactsCompose.withTeamOutputVisibility",
                            "IFactDisclosureGate",
                            "compose FactsCompose.withFactStore first"
                        )
                    )
                | Some inner ->
                    s.Remove inner.Descriptor |> ignore

                    s.AddSingleton<IFactDisclosureGate>(
                        Func<IServiceProvider, IFactDisclosureGate>(fun sp ->
                            match inner.Resolve sp with
                            | :? FactDisclosureGate as gate -> gate.WithViewerAwareness(viewerAwareFacet settings sp)
                            | other ->
                                ServiceComposition.refuse (
                                    CompositionRefusal.UnsupportedImplementation(
                                        "FactsCompose.withTeamOutputVisibility",
                                        "IFactDisclosureGate",
                                        $"{other.GetType().FullName}, not the platform's FactDisclosureGate"
                                    )
                                ))
                    )

            let establishViewer (builder: Microsoft.AspNetCore.Builder.IApplicationBuilder) =
                Microsoft.AspNetCore.Builder.UseExtensions.Use(
                    builder,
                    Func<
                        Microsoft.AspNetCore.Http.HttpContext,
                        Func<System.Threading.Tasks.Task>,
                        System.Threading.Tasks.Task
                     >(
                        FactViewerContext.middleware
                    )
                )

            {
                app with
                    Extensions.PreMiddleware = app.Extensions.PreMiddleware @ [ establishViewer ]
                    TeamOutputVisibility = Some settings
            }
            |> contribute register
    // ─── Phase 897 — team-to-team fact publication (opt-in) ───────────
    //
    // A consolidated view is built by PUBLISHING, never by reading across
    // teams. This knob registers the publication service — grants, the two
    // consents, the one cross-scope write seam — and the publication and
    // refresh job handlers on the composed scheduler. The target tables are
    // declared here, each naming the level of its hierarchy the origin team
    // occupies; a declaration with a defect fails at startup, naming it.
    //
    // Self-contained on purpose: it decorates nothing and replaces nothing,
    // so the store, the gate, the writer and every facet composed above are
    // untouched. A deployment that does not call it is unchanged, and one
    // that calls it but records no grant writes nothing anywhere.

    /// Compose team-to-team fact publication (Phase 897). `config` declares
    /// the consolidation tables publications may be written into and the
    /// signing profile: `FactPublicationConfig.create targets` for recorded
    /// provenance, `FactPublicationConfig.signed` for the regulated profile,
    /// which additionally needs an `IArtefactSigner` and an
    /// `IArtefactVerifier` composed.
    ///
    /// Requires the fact store, and the table writer for the tables it
    /// names; owner acts read the team's roles from the composed team store.
    /// A `NoFactStore` deployment is unchanged. Insert after
    /// `withFactTableWriter`:
    ///
    /// ```fsharp
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withFactStore
    /// |> FactsCompose.withFactTableWriter
    /// |> FactsCompose.withFactPublication (FactPublicationConfig.create [ FactPublicationTarget.create "group-sales" "region" ])
    /// |> ServerApp.run
    /// ```
    /// **Adds** its registrations as a contribution under the Facts companion's id; a later `withExtensions` keeps them.
    let withFactPublication (config: FactPublicationConfig) (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let register (s: IServiceCollection) =
                s.AddSingleton<IFactPublication>(
                    Func<IServiceProvider, IFactPublication>(fun sp ->
                        let teams = tryService<ToolUp.Platform.TeamManagement.ITeamStore> sp

                        // Phase 935 — grants persist when the platform's
                        // scope carrier is composed (it is, beside the
                        // DataProtection key ring, in every composition).
                        let create =
                            match tryService<ScopeCarrier> sp with
                            | Some carrier -> FactPublication.createDurable carrier
                            | None -> FactPublication.createWith

                        create
                            config
                            (sp.GetRequiredService<IFactStore>())
                            (sp.GetRequiredService<IBlobStorage>())
                            (sp.GetRequiredService<IEventStore>())
                            (sp.GetRequiredService<IFactDisclosureGate>())
                            (tryService<Grounding.IFactTableRegistry> sp
                             |> Option.defaultValue Grounding.FactTableRegistry.empty)
                            (tryService<Grounding.IMetricRegistry> sp)
                            (tryService<IFactTableWriter> sp)
                            (fun teamId userId ->
                                match teams with
                                | Some teams -> teams.GetMemberRole(teamId, userId)
                                | None -> async.Return None)
                            (tryService<ToolUp.ArtefactSigning.IArtefactSigner> sp)
                            (tryService<ToolUp.ArtefactSigning.IArtefactVerifier> sp)
                            (fun () -> DateTime.UtcNow))
                )
                |> ignore

                // The handlers are registered, never scheduled: a publication
                // is scheduled by the source team's owner under the source's
                // minted scope, and a refresh by the target's under its own
                // (`FactPublicationJobs.schedulePublication` / `scheduleRefresh`).
                // Exempt from the profile matrix (Phase 1051): a handler
                // registration, placed by the scheduler's own gate.
                (s
                 |> ProcessProfileGate.exempt
                     "Fact publication handler registration"
                     ProcessProfileGate.SchedulerRegistrationReason)
                    .AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                        Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                            ProcessProfileGate.atStart (fun () ->
                                // Resolving the service here surfaces a
                                // declaration defect at startup, not at the
                                // first publication.
                                let publication = sp.GetRequiredService<IFactPublication>()

                                match tryService<IJobScheduler> sp with
                                | Some scheduler ->
                                    scheduler.RegisterHandler(
                                        FactPublicationJobs.PublishHandler,
                                        FactPublicationJobs.publishHandler publication
                                    )

                                    scheduler.RegisterHandler(
                                        FactPublicationJobs.RefreshHandler,
                                        FactPublicationJobs.refreshHandler publication
                                    )
                                | None -> ()))
                    )
                |> ignore

                s

            app |> contribute register