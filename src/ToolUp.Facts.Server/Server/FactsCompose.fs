// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open Fuaran.Facts
open ToolUp.Facts
open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── FactsCompose (Phase 520 wiring) ─────────────────────────────────
//
// Turns the introspectable `ServerConfig.Stores.FactStore` knob into real
// composition: when `FeatureMode.Enabled`, folds an `IFactStore`
// (BlobFactStore over the composed `IBlobStorage` + `IEventStore`), its
// `IFactEvidenceSource` adapter, the `IFactDisclosureGate` egress gate,
// and the `IFactResolver` retrieval adapter into DI, so the fact tier is
// available to request handlers, the provenance graph, and fact-first
// retrieval (Phase 558 — `RAGCompose` picks the resolver + gate up into
// the `RetrievalPipeline` with zero extra config). `FeatureMode.Disabled` (the
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
// (`ServerConfig.MultiInstance.ReplicaCount > 1`), and the composed store is the blob
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
/// marker the composed store sets to the blob default and
/// `withFactStoreImplementation` replaces, so the scale guard can tell the
/// blob store from a replacement even when a decorator wraps it.
type internal FactStoreBackend = { Backend: string }

/// Startup guard (Phase 888): warns a multi-replica deployment whose blob
/// fact store holds more facts in one scope than the blob layout serves
/// well, in stronger terms past the upper threshold, naming
/// `ToolUp.FactStores.Postgres` as the remedy. Warn-only in this release
/// (operator decision, 2026-09-29) — it never refuses startup. Registered
/// by `FactsCompose.withOptions` only when `ServerConfig.MultiInstance.ReplicaCount > 1`.
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
/// by `FactsComposeOptions.QuestionCompiler`. Its presence outranks every
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
                        "The fact tier is composed but its answer planner has no question compiler, so fact clause planning (the push door: facts placed in the prompt ahead of retrieved passages) refuses every question and never pushes a fact; only the fact tools reach the store. Remedy: compose the AI tier (the planner then compiles through its IAIProviderFactory), register an IAIProvider, or supply a compiler as FactsComposeOptions.QuestionCompiler. To leave the push door off on purpose, set QuestionCompiler = Some AnswerPlanner.noQuestionCompiler."
        }

/// Refuses to start a composition whose tolerance triggers name a table or metric
/// no module declares. It reads the registry when it runs, because a registration
/// runs before the modules' declarations are folded into one.
type internal ToleranceTriggerGuard(triggers: ToleranceTrigger list, services: IServiceCollection) =
    interface ConfigValidation.IConfigValidator with
        member _.Name = "fact-tolerance-triggers"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let tables =
                services
                |> Seq.filter (fun d -> d.ServiceType = typeof<Grounding.IFactTableRegistry> && not d.IsKeyedService)
                |> Seq.choose (fun d ->
                    match d.ImplementationInstance with
                    | :? Grounding.IFactTableRegistry as registry -> Some registry
                    | _ -> None)
                |> Seq.tryLast

            match ToleranceTrigger.undeclared tables triggers with
            | [] -> return ConfigValidation.ValidationResult.Ok
            | problems ->
                return
                    ConfigValidation.ValidationResult.Error(
                        "FactsComposeOptions.ToleranceTriggers: " + String.concat "; " problems
                    )
        }

/// Warns at startup when withholding surfaces are composed over a disclosure
/// configuration that cannot withhold at them (`DisclosureReachFinding`). A
/// report, never a refusal: a plain configuration is a legitimate choice.
/// Warning when the deployment requires authentication, an Info line in the
/// log otherwise; `TOOLUP_ACCEPT_PLAIN_DISCLOSURE` silences it. An instance
/// deciding when it runs from the service collection as finally assembled,
/// so the surfaces may be composed before or after the fact tier.
type internal DisclosureReachGuard(requiresAuth: bool, services: IServiceCollection) =

    member private _.Instances<'T when 'T: not struct>() : 'T list =
        services
        |> Seq.filter (fun d -> d.ServiceType = typeof<'T> && not d.IsKeyedService)
        |> Seq.choose (fun d ->
            match d.ImplementationInstance with
            | :? 'T as instance -> Some instance
            | _ -> None)
        |> List.ofSeq

    interface ConfigValidation.IConfigValidator with
        member _.Name = "fact-disclosure-reach"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member this.Validate() = async {
            let accepted =
                match
                    ConfigResolution.tryValue ConfigKeys.Names.acceptPlainDisclosure
                    |> Option.map _.ToLowerInvariant()
                with
                | Some("1" | "true" | "yes" | "on") -> true
                | _ -> false

            let config =
                this.Instances<DisclosureTaintConfig>()
                |> List.tryLast
                |> Option.defaultValue DisclosureTaintConfig.empty

            match accepted, DisclosureReachFinding.assess config (this.Instances<WithholdingSurface>()) with
            | false, Some message when requiresAuth -> return ConfigValidation.ValidationResult.Warning message
            | false, Some message ->
                this.Instances<ILogger>()
                |> List.tryLast
                |> Option.iter (fun logger -> logger.Info message)

                return ConfigValidation.ValidationResult.Ok
            | _ -> return ConfigValidation.ValidationResult.Ok
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
            //
            // The blob store this composition builds is its own singleton, so
            // the index inspector below can tell it from a replacement.
            .AddSingleton<BlobFactStore>(
                Func<IServiceProvider, BlobFactStore>(fun sp ->
                    // A fact read that faults is logged through the composed
                    // logger as well as failing the read.
                    BlobFactStore(
                        sp.GetRequiredService<IBlobStorage>(),
                        sp.GetRequiredService<IEventStore>(),
                        tryService<Grounding.IMetricRegistry> sp,
                        (fun () -> DateTime.UtcNow),
                        FactSurfaceOptions.defaults,
                        FactIndexOptions.defaults,
                        tryService<ILogger> sp
                    ))
            )
            .AddSingleton<IFactStore>(
                Func<IServiceProvider, IFactStore>(fun sp -> sp.GetRequiredService<BlobFactStore>() :> IFactStore)
            )
            // The fact store's index check on /dev/inspect. It samples the
            // blob store this composition built when that is the composed
            // `IFactStore`; a replacement (`withFactStoreImplementation`)
            // is not this inspector's, and it samples nothing.
            .AddSingleton<DevDiagnosticsHandler.IIndexConsistencyInspector>(
                Func<IServiceProvider, DevDiagnosticsHandler.IIndexConsistencyInspector>(fun sp ->
                    { new DevDiagnosticsHandler.IIndexConsistencyInspector with
                        member _.Inspect(scopeId) = async {
                            match sp.GetService<IFactStore>() with
                            | :? BlobFactStore as blob when obj.ReferenceEquals(blob, sp.GetService<BlobFactStore>()) ->
                                return! blob.IndexConsistencyCheck(scopeId, 20)
                            | _ -> return []
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
            // (`TeamOutputVisibility` replaces the gate above), resolved
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

                    GroundingCertificate.createIssuerAudited
                        graph
                        store
                        gate
                        events
                        signer
                        (ToolUp.Platform.Config.Background.resolveAuditLog sp))
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
    // what take it live, and they ride the SAME `FeatureMode.Enabled` knob as
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
    // A `FeatureMode.Disabled` deployment reaches none of this — `withOptions`
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

    // Once per collection, like the question-compiler guard above.
    let private registerDisclosureReachGuard (requiresAuth: bool) (services: IServiceCollection) : IServiceCollection =
        let present =
            services
            |> Seq.exists (fun d -> not d.IsKeyedService && (d.ImplementationInstance :? DisclosureReachGuard))

        if present then
            services
        else
            services.AddSingleton<ConfigValidation.IConfigValidator>(
                DisclosureReachGuard(requiresAuth, services) :> ConfigValidation.IConfigValidator
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

    let private registerReactiveRecomputation
        (serverConfig: ServerConfig)
        (services: IServiceCollection)
        : IServiceCollection =
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
        //     by `reactToDataChange`, never on a cadence. Once at start,
        //     like (3) below: a scheduler registration, placed by the
        //     scheduler's own unit.
        //
        //     A recompute and a grounded-narrative run are platform work
        //     scheduled under the tenant's own scope, so both handlers are
        //     declared protected: the job API stops their jobs for a
        //     platform administrator only.
        services
        |> JobApiHandler.declareProtected RecomputeJobHandler.HandlerName
        |> JobApiHandler.declareProtected GroundedNarrativeTrigger.HandlerName
        |> ignore

        Hosting.register
            services
            (Hosting.deferredJobs "reactive-fact-recomputation" [
                DeferredScheduledJobDeclaration.create (fun provider ->
                    RecomputeJobHandler.declaration
                        (provider.GetRequiredService<IFactStore>())
                        (provider.GetRequiredService<IFactRecomputer>())
                        (provider.GetRequiredService<ILogger>()))
            ])
        |> ignore

        // The grounded-narrative job handler, registered with the scheduler
        // at startup only when a run registry and a run are composed;
        // otherwise the start is a DI lookup and nothing else.
        Hosting.register
            services
            (BackgroundUnit.create
                "grounded-narrative-handler-registration"
                OnceAtStart
                GroundedNarrativeTrigger.hostedService)
        |> ignore

        services

    /// Whether the fact store is already composed onto `app`: its tools are
    /// declared with it, so a second composition finds them.
    let private storeComposed (app: ServerApp) : bool =
        let declared =
            app.AITools |> List.map (fun (definition, _) -> definition.Name) |> Set.ofList

        NarrativeGrounding.factTools
        |> List.forall (fun (definition, _) -> declared.Contains definition.Name)

    /// The fact store, its gate, resolver and planner, and reactive
    /// recomputation over it: `IFactStore` (`BlobFactStore`),
    /// `IFactEvidenceSource`, `IFactDisclosureGate`, `IFactResolver`, the
    /// default `IFactRecomputer`, the recompute job handler and the
    /// data-arrival hook that drives fact invalidation when a data-object
    /// version lands. Reactive recomputation stays dormant until a metric
    /// declares `RecomputePolicy.Eager` or `OnQuery`. Composed once: an app
    /// that already composes it is returned unchanged.
    let private composeStore (app: ServerApp) : ServerApp =
        if storeComposed app then
            app
        else
            // The fact tier and its reactive activation are one registration
            // pass: the store first, then the recompute engine, handler and
            // data-arrival hook over it.
            let register (s: IServiceCollection) =
                registerReactiveRecomputation app.Config (registerTableChangeSignal (registerFactStore s))
                // The backend marker and, for a multi-replica deployment
                // only, the blob store's scale guard.
                |> registerBlobScaleGuard app.Config.MultiInstance.ReplicaCount
                // The dormant-push-door warning.
                |> registerQuestionCompilerGuard
                // The withholding-surfaces-over-a-plain-configuration finding.
                |> registerDisclosureReachGuard (DeploymentConfig.requiresAnyAuth app.Config)
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
            |> ServerApp.withAuditEvents (AuditEventFamily.registration FactsAuditEvents.FactsAuditEvent.family)

    /// Registers `value` as the one `'T` of the composition; a second
    /// registration, from a second `withOptions` setting the same field, is
    /// refused naming the field.
    let private registerOnce<'T when 'T: not struct> (field: string) (reason: string) (value: 'T) (app: ServerApp) =
        app
        |> contribute (fun s ->
            if s |> Seq.exists (fun d -> d.ServiceType = typeof<'T>) then
                ServiceComposition.refuse (
                    CompositionRefusal.InvalidDeclaration("FactsComposeOptions." + field, reason)
                )
            else
                s.AddSingleton<'T>(value))

    /// The authorised narrative calculations: the grounding gate derives every
    /// `calc:` reference a narrative cites against them. Empty ones change nothing.
    let private applyNarrativeCalculations (calculations: NarrativeCalculations) (app: ServerApp) =
        if NarrativeCalculations.isEmpty calculations then
            app
        else
            registerOnce
                "NarrativeCalculations"
                "narrative calculations are already registered; declare them all in one NarrativeCalculations value"
                calculations
                app

    /// The pipeline profile `evaluate_pipeline` reads every pipeline against.
    let private applyPipelineProfile (profile: RegisteredPipelineProfile) (app: ServerApp) =
        registerOnce
            "PipelineProfile"
            "a pipeline profile is already registered; a deployment registers one"
            profile
            app

    /// The declared tools ride `ServerApp.AITools` beside the fact tools, so
    /// they arm where the AI companion is composed. The names were checked
    /// against the app's when the options were refused or admitted.
    let private declareTools tools (app: ServerApp) =
        match tools with
        | [] -> app
        | tools -> {
            app with
                AITools = app.AITools @ tools
          }

    /// The registered methods: one tool per card and `describe_method`, and the
    /// profile whose fragment (`MethodProfile.Fragment`) the system prompt serves.
    let private applyMethods (profile: MethodProfile) (app: ServerApp) =
        if profile.Methods.IsEmpty then
            app
        else
            app
            |> declareTools (profile.Tools |> List.map (fun t -> t.Definition, t.Executor))
            |> registerOnce
                "Methods"
                "a method profile is already registered; register every method in one MethodRegistry.register call"
                profile

    /// The dataset models and `describe_dataset_model`, answering each caller
    /// from its projection of each model under the declared resolver.
    let private applyDatasetModels (declared: DatasetModelsDeclaration) (app: ServerApp) =
        if declared.Registry.Models.IsEmpty then
            app
        else
            app
            |> declareTools [
                DatasetModelTools.definition, DatasetModelTools.executor declared.Registry declared.Resolver
            ]
            |> registerOnce
                "DatasetModels"
                "a dataset model registry is already registered; register every model in one DatasetModelRegistry.register call"
                declared.Registry

    /// What `list_fact_tables` lists, captured as a value: the executor reads
    /// the container for nothing.
    let private applyFactTables (options: FactTablesOptions) (app: ServerApp) = {
        app with
            AITools =
                app.AITools
                |> List.map (fun (definition, executor) ->
                    if definition.Name = FactTablesTool.Name then
                        definition, FactTablesTool.executeFor options
                    else
                        definition, executor)
    }

    // ─── The gate's facets ────────────────────────────────────────────
    //
    // Purpose binding, declassification budgets and the read fan-out are
    // each an optional registration the gate factory above reads, so a
    // deployment that sets none composes the plain gate (GP 11 / GP 13).

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

    /// Purpose-bound disclosure: the gate requires every check's ambient
    /// `FactPurposeContext` claim to be in the surface's allowed set, and the
    /// taxonomy and allowed sets are projected into the composition manifest,
    /// readable before any data flows. Request handlers state the claim with
    /// `FactPurposeContext.claim`; an unclaimed check refuses at every
    /// purpose-bound surface.
    let private applyDisclosurePurposes (config: DisclosurePurposeConfig) (app: ServerApp) =
        app
        |> contribute (fun s -> s.AddSingleton<DisclosurePurposeConfig>(config))
        |> ServerApp.withRegisteredPurposes (registeredPurposes config)

    /// Declassification budgets: a crossing of a budgeted routine reserves its
    /// charge before the disclosure verdict and settles it after, per
    /// contributing party, and an exhausted ceiling denies with the typed,
    /// audited refusal a policy denial takes.
    ///
    /// **An accounting control, not a differential-privacy guarantee.** It
    /// bounds questions asked, not information disclosed; only a routine that
    /// draws calibrated noise from an `INoiseMechanism` may declare a chargeable
    /// epsilon, and `DeclassificationBudgetConfig.create` refuses the
    /// combination otherwise. Charges compose sequentially (Dwork & Roth,
    /// Theorem 3.16), and collusion between contributing parties is out of
    /// scope: two parties that share answers are one adversary with two budgets.
    let private applyDeclassificationBudgets (config: DeclassificationBudgetConfig) (app: ServerApp) =
        app
        |> contribute (fun s -> s.AddSingleton<DeclassificationBudgetConfig>(config))

    /// The bound on the fact reads one disclosure check holds in flight.
    let private applyDisclosureFanOut (maxConcurrentReads: int) (app: ServerApp) =
        let bound: DisclosureFanOut = {
            MaxConcurrentReads = max 1 maxConcurrentReads
        }

        app |> contribute (fun s -> s.AddSingleton<DisclosureFanOut>(bound))

    /// The certificate-verified fact import door over the composed `IFactStore`
    /// and `IAuditLog`, accepting facts from the anchored peers. Each anchor
    /// carries one peer's public key, the whole of what offline verification
    /// needs; no key is discovered, so none is implicitly trusted. An empty list
    /// composes a door that refuses every import with `ImportUntrustedPeer`.
    let private applyFactImport (anchors: PeerTrustAnchor list) (app: ServerApp) =
        app
        |> contribute (fun s ->
            s.AddSingleton<IFactImportDoor>(
                Func<IServiceProvider, IFactImportDoor>(fun sp ->
                    FactImport.create
                        (sp.GetRequiredService<IFactStore>())
                        anchors
                        (sp.GetRequiredService<IAuditLog>()))
            ))

    // ─── The grounding envelope sealed past boot ──────────────────────
    //
    // The boot seal proves nothing about what happens afterwards, and for the
    // grounding tier that gap is the live one: the declarations a later
    // answer's provenance is judged against are free to move the instant the
    // preflight verdict lands. This composes the door through which such a
    // mutation becomes a typed, audited operation carrying the before/after
    // envelope digest, so `boot seal + recorded chain ⇒ live envelope` is a
    // computation an auditor runs from the trail. Under
    // `CompositionProfile.Verified` a mutation arriving out of path is
    // refused; under `Standard` it is recorded and lands.

    /// The audited grounding-envelope mutation door, sealed to the envelope
    /// the app declares AS IT STANDS: `withOptions` applies it after every
    /// other field, so the seal covers what the options compose. `Observe`
    /// is the honest bound on the mechanism: `None` is right for a
    /// composition whose grounding declarations are immutable once composed,
    /// which is every composition this SDK ships, and then continuity holds by
    /// construction rather than by a passed check.
    let private applyGroundingEnvelopeSeal (declared: GroundingEnvelopeSealDeclaration) (app: ServerApp) =
        let sealedEnvelope =
            GroundingEnvelope.ofComposition (ServerApp.compositionManifest app) app.RegisteredMetrics

        app
        |> contribute (fun s ->
            s.AddSingleton<IGroundingEnvelopeMutator>(
                Func<IServiceProvider, IGroundingEnvelopeMutator>(fun sp ->
                    let auditLog = sp.GetRequiredService<IAuditLog>()

                    match declared.Observe with
                    | None ->
                        GroundingEnvelopeMutator.forImmutableComposition
                            declared.Profile
                            auditLog
                            GroundingEnvelopeMutator.PlatformScopeId
                            sealedEnvelope
                    | Some observeLive ->
                        GroundingEnvelopeMutator.create
                            declared.Profile
                            auditLog
                            GroundingEnvelopeMutator.PlatformScopeId
                            sealedEnvelope
                            observeLive)
            ))

    // ─── Coverage narratives ──────────────────────────────────────────
    //
    // Separate from the store for two reasons. It WRITES to the deployment's
    // knowledge base, which a storage knob may not decide. And a coverage
    // narrative is a STANDING document, judged by the disclosure gate once,
    // against the principal its options name, at commit time, rather than per
    // reader at read time: that principal must be a floor on what the scope
    // may see, so there is no default for it.

    /// Decorates the composed `IFactStore` so that assertion activity
    /// recomputes a metric's coverage and, when it has moved materially (a
    /// cardinality band, the period reach, the method mix), commits one
    /// narrative per populated metric into the knowledge base. Inert where no
    /// knowledge base is composed: the trigger probes for `INarrativeIngestor`
    /// before it reads anything.
    let private applyCoverageNarratives (options: CoverageNarrative.CoverageNarrativeOptions) (app: ServerApp) =
        app
        |> contribute (fun services ->
            // The inner store is taken from the descriptor already in the
            // collection, never from the built provider, where `IFactStore`
            // resolves to this decorator and the factory would recurse.
            match
                ServiceComposition.captureDescriptor<IFactStore> services
                |> Option.map _.Resolve
            with
            // No fact store descriptor at all: left untouched rather than
            // registering a decorator with nothing to decorate.
            | None -> services
            | Some resolveInner ->
                services.AddSingleton<IFactStore>(
                    Func<IServiceProvider, IFactStore>(fun sp ->
                        CoverageNarrative.decorate (resolveInner sp) sp options (sp.GetRequiredService<ILogger>()))
                ))

    // ─── Fact-base coherence checking (opt-in) ────────────
    //
    // A separate, explicit opt-in on top of the fact store: the standing
    // self-audit is NOT folded into the store, so a deployment that
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

    // The store a coherence sweep reads. When delegate facts are
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
                            principal.UserId.Value
                            (DeclaredPrincipal.missingGrant e))
        }

    let private registerCoherenceChecks
        (serverConfig: ServerConfig)
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

        // Schedule the standing check on the opt-in cadence. The scheduler +
        // metric registry are only resolvable from the built container, so
        // registration happens in a startup hosted service — once at start:
        // a scheduler registration, placed by the scheduler's own unit. The
        // check runs under each declared principal's tenant scope, so its
        // handler is declared protected: the job API stops it for a
        // platform administrator only.
        JobApiHandler.declareProtected CoherenceJobHandler.HandlerName services
        |> ignore

        Hosting.register
            services
            (BackgroundUnit.create "fact-coherence-check-scheduling" OnceAtStart (fun sp ->
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
                }))
        |> ignore

        services

    /// The standing coherence check: the health probe when the config names a
    /// health principal, and the sweep on the schedule's cadence under each
    /// declared principal's scope (none schedules no sweep).
    let private applyCoherenceChecks (schedule: CoherenceCheckSchedule) (app: ServerApp) =
        app
        |> contribute (registerCoherenceChecks app.Config schedule.Config schedule.Cadence schedule.Principals)

    // ─── The default fact-table writer ────────────────────────────────

    /// The default `IFactTableWriter`, over the composed `IFactStore` so a
    /// declared table is usable before any dedicated table store is composed,
    /// with every declared fact table bound to it. The writer resolves the
    /// table and metric registries when first used, so modules may be added
    /// before or after. A declared `Required` table with no writer refuses to
    /// start at the fact-table preflight, naming the table.
    let private applyFactTableWriter (app: ServerApp) =
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

    /// Whether `app` already binds its declared tables to a writer: the
    /// default writer, or the delegate writer for a delegated table.
    let private writerComposed (app: ServerApp) : bool =
        app.FactTableBindings
        |> List.exists (fun binding ->
            match binding with
            | Grounding.BindAllFactTables destination -> destination = DefaultFactTableWriter.Destination
            | Grounding.BindFactTable(_, destination) -> destination = DelegateFact.Destination)

    /// Decorates the composed `IFactTableWriter`, captured from the collection.
    /// The options were refused when no writer is composed, so a missing
    /// descriptor is a composition that removed it, and is refused by name.
    let private decorateWriter
        (field: string)
        (decorate: IServiceCollection -> CapturedService<IFactTableWriter> -> IServiceCollection)
        (app: ServerApp)
        =
        app
        |> contribute (fun services ->
            match ServiceComposition.captureDescriptor<IFactTableWriter> services with
            | None ->
                ServiceComposition.refuse (
                    CompositionRefusal.MissingDependency(
                        "FactsComposeOptions." + field,
                        "IFactTableWriter",
                        "set FactTableWriter = true, or hold the tables it watches as DelegateFacts"
                    )
                )
            | Some captured -> decorate services captured)

    /// The once-per-delivery narrative trigger: a successful commit of a table
    /// a run names enqueues that run through `GroundedNarrativeTrigger`. The
    /// firing rule is `DeliveryTrigger`'s.
    let private applyDeliveryTrigger (runsFor: string -> string list) (app: ServerApp) =
        app
        |> decorateWriter "DeliveryTrigger" (fun services captured ->
            services.AddSingleton<IFactTableWriter>(
                Func<IServiceProvider, IFactTableWriter>(fun sp ->
                    DeliveryTrigger.decorate
                        runsFor
                        (fun () -> tryService<IJobScheduler> sp)
                        (fun () -> tryService<Microsoft.AspNetCore.Http.IHttpContextAccessor> sp)
                        (sp.GetRequiredService<ILogger>())
                        (captured.Resolve sp))
            ))

    /// Tolerance triggers: a unit of work that moved a trigger's table asks
    /// whether its metric moved beyond the tolerance, and enqueues the run when
    /// any selected subject did. A trigger naming a table or metric no module
    /// declares is refused by the compose-time preflight, once every module has
    /// declared its tables.
    let private applyToleranceTriggers (triggers: ToleranceTrigger list) (app: ServerApp) =
        app
        |> decorateWriter "ToleranceTriggers" (fun services captured ->
            services.AddSingleton<ConfigValidation.IConfigValidator>(
                ToleranceTriggerGuard(triggers, services) :> ConfigValidation.IConfigValidator
            )
            |> ignore

            services.AddSingleton<IFactTableWriter>(
                Func<IServiceProvider, IFactTableWriter>(fun sp ->
                    ToleranceTrigger.decorate
                        triggers
                        (fun () -> tryService<Grounding.IFactTableRegistry> sp)
                        (fun () -> tryService<IFactStore> sp)
                        (fun () -> tryService<IJobScheduler> sp)
                        (fun () -> tryService<Microsoft.AspNetCore.Http.IHttpContextAccessor> sp)
                        (sp.GetRequiredService<ILogger>())
                        (captured.Resolve sp))
            ))

    /// The answer planner's explicit question compiler. It outranks a
    /// registered `IAIProvider` and the AI tier's `IAIProviderFactory`; the
    /// last choice registered wins.
    let private applyQuestionCompiler (compiler: QuestionCompiler) (app: ServerApp) =
        app
        |> contribute (fun s -> s.AddSingleton<ExplicitQuestionCompiler>(ExplicitQuestionCompiler compiler))

    // ─── A replacement IFactStore behind the same knob ────────────────
    //
    // The blob default suits a store of modest size; a deployment that has
    // outgrown it swaps in another `IFactStore` (the indexed companion
    // `ToolUp.FactStores.Postgres` is the shipped one) without losing a single
    // registration built over the store: the evidence source, the disclosure
    // gate, the resolver, the provenance graph, reactive recomputation and the
    // fact tools all resolve `IFactStore` from DI, so they follow it.

    /// Removes every `IFactStore` registration made before it and registers
    /// the replacement, renaming the backend marker the blob store's
    /// multi-replica scale guard reads, so the guard stands down.
    let private applyStoreImplementation (replacement: FactStoreReplacement) (app: ServerApp) =
        app
        |> contribute (fun s ->
            s.RemoveAll<IFactStore>() |> ignore

            s.AddSingleton<IFactStore>(Func<IServiceProvider, IFactStore>(replacement.Factory))
            |> ignore

            s.RemoveAll<FactStoreBackend>() |> ignore
            s.AddSingleton<FactStoreBackend>({ Backend = replacement.Backend }))

    // ─── Delegate facts (opt-in) ──────────────────────────
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
                        CompositionRefusal.InvalidDeclaration("FactsComposeOptions.DelegateFacts", reason)
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

    /// Holds the named declared fact tables as delegate facts: each table is
    /// bound to the delegate destination and kept as rows, each of its columns
    /// becomes one delegate record in the fact tier, and a point or population
    /// read of a delegated metric is answered from the table, minting as
    /// ordinary facts only the rows it returns. It decorates the composed store
    /// and writer, so it applies after the store's replacement and the default
    /// writer; tables bound here are held by the delegate writer, and every
    /// other table is handed to the writer that composed it.
    let private applyDelegateFacts (tableIds: string list) (app: ServerApp) =
        match tableIds with
        | [] -> app
        | tableIds ->
            tableIds
            |> List.distinct
            |> List.fold
                (fun a tableId ->
                    ServerApp.bindFactTables (Grounding.BindFactTable(tableId, DelegateFact.Destination)) a)
                app
            |> contribute (registerDelegateFacts tableIds)

    // ─── Team output visibility (opt-in) ──────────────────
    //
    // Module permission governs USE; the team's policy governs who sees the
    // OUTPUT modules published. This knob arms the second axis at the one
    // place every egress door already passes: the disclosure gate decides a
    // `Restricted` fact for the viewer — the requester as the platform
    // resolved them, or the least-privileged viewer when there is none.
    //
    // The team's level is read through `ITeamOutputVisibilitySource`, whose
    // implementation lives with the per-team policy record that also holds
    // the team's conversation level — at the platform tier, and
    // registered here, so a deployment without the AI assistant
    // honours each team's choice. A deployment that registers its own
    // source keeps it; with none at all every team sits at the default.
    //
    // Self-contained on purpose: the gate the store registers is
    // decorated in place (`FactDisclosureGate.WithViewerAwareness`), so the
    // registration above is untouched and every facet it composed (taint,
    // purpose, budgets) is kept.

    /// The viewer-aware facet over the composed substrate.
    let private viewerAwareFacet
        (settings: TeamOutputVisibilitySettings)
        (sp: IServiceProvider)
        : ViewerAwareDisclosure =
        let source = tryService<ITeamOutputVisibilitySource> sp
        let teams = tryService<ToolUp.Platform.Config.TeamManagement.ITeamStore> sp

        {
            Resolver = None
            Sources = {
                OutputVisibility =
                    fun teamId ->
                        match source with
                        | Some source -> source.Current(TeamId teamId)
                        | None -> async.Return(Ok settings.Default)
                TeamRole =
                    fun teamId userId ->
                        match teams with
                        | Some teams -> teams.GetMemberRole(TeamId teamId, UserId userId)
                        | None -> async.Return None
                IsTeam =
                    fun scopeId ->
                        match teams with
                        | Some teams -> async {
                            let! team = teams.GetTeam(TeamId scopeId)
                            return team.IsSome
                          }
                        | None -> async.Return false
            }
        }

    /// Team output visibility: a team may limit who sees the `Restricted`
    /// output its modules publish to the whole team, the team's admins, or
    /// platform admins, the same three levels as conversation visibility.
    /// Arms the settings (read by the store that holds each team's choice), the
    /// platform's `ITeamOutputVisibilitySource` over that store and the startup
    /// check that the two deployment defaults do not conflict, the gate's
    /// viewer-aware facet, and the middleware that establishes the request's
    /// viewer; the declaration is recorded on the app, so the composition
    /// manifest projects it. `Surfaceable` facts stay visible to every viewer
    /// the scope admits and `Internal` facts are never disclosed; module
    /// permission is not consulted, because permission governs use, not sight.
    /// Composing it with `TeamVisible` as the default and no team choosing
    /// otherwise decides every check exactly as before (GP 11).
    let private applyTeamOutputVisibility (declared: TeamOutputVisibilityDeclaration) (app: ServerApp) =
        let settings =
            match TeamOutputVisibilitySettings.create declared.Default declared.Allowed with
            | Ok settings -> settings
            | Error message ->
                ServiceComposition.refuse (
                    CompositionRefusal.InvalidDeclaration("FactsComposeOptions.TeamOutputVisibility", message)
                )

        let register (s: IServiceCollection) =
            s.RemoveAll<TeamOutputVisibilitySettings>() |> ignore
            s.AddSingleton<TeamOutputVisibilitySettings>(settings) |> ignore

            // The platform's source over the per-team record, and the
            // startup check that the two deployment defaults do
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
                        "FactsComposeOptions.TeamOutputVisibility",
                        "IFactDisclosureGate",
                        "compose the fact store with FactsCompose.withOptions, which registers it"
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
                                    "FactsComposeOptions.TeamOutputVisibility",
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
    // ─── Team-to-team fact publication (opt-in) ───────────
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
    // untouched. A deployment that does not set it is unchanged, and one
    // that sets it but records no grant writes nothing anywhere.

    /// Team-to-team fact publication: the publication service (grants, the
    /// two consents, the one cross-scope write seam) and the publication and
    /// refresh job handlers on the composed scheduler. The signed profile
    /// (`FactPublicationConfig.signed`) also needs an `IArtefactSigner` and an
    /// `IArtefactVerifier` composed; owner acts read the team's roles from the
    /// composed team store.
    let private applyFactPublication (config: FactPublicationConfig) (app: ServerApp) =
        let register (s: IServiceCollection) =
            s.AddSingleton<IFactPublication>(
                Func<IServiceProvider, IFactPublication>(fun sp ->
                    let teams = tryService<ToolUp.Platform.Config.TeamManagement.ITeamStore> sp

                    // Grants persist when the platform's
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
                            | Some teams -> teams.GetMemberRole(TeamId teamId, UserId userId)
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
            // Once at start: a handler registration, placed by the
            // scheduler's own unit.
            Hosting.register
                s
                (BackgroundUnit.create "fact-publication-handler-registration" OnceAtStart (fun sp ->
                    Hosting.atStart (fun () ->
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
                        | None -> ())))
            |> ignore

            s

        app |> contribute register
    // ─── The options record, composed once ───────────────────────────

    /// **Adds** a composed withholding surface for the startup disclosure
    /// finding (`DisclosureReachFinding`): `name` is what the finding calls it
    /// and `surface` the egress door it asks the gate at. The seam a builder
    /// that arms a surface calls; it composes no store, and a deployment
    /// composing none is never told anything. A root states its own as
    /// `FactsComposeOptions.WithholdingSurfaces`. Idempotent per name; a
    /// `FeatureMode.Disabled` deployment is unchanged.
    let addWithholdingSurface (name: string) (surface: FactEgressSurface) (app: ServerApp) : ServerApp =
        WithholdingSurfaces.contribute name surface app

    /// The tool names an app declares, with the fact tools the store declares.
    let private declaredToolNames (app: ServerApp) : Set<string> =
        app.AITools @ NarrativeGrounding.factTools
        |> List.map (fun (definition, _) -> definition.Name)
        |> Set.ofList

    let private applyIf (field: 'T option) (apply: 'T -> ServerApp -> ServerApp) (app: ServerApp) =
        match field with
        | Some value -> apply value app
        | None -> app

    /// Compose the fact tier as one value: the fact store per
    /// `ServerConfig.Stores.FactStore` (or turned on by `EnableStore`) and
    /// every field of `options` over it, in one fixed order whatever order
    /// the record is written in. `FactsComposeOptions.defaults` composes the
    /// store alone.
    ///
    /// ```fsharp skip=fragment
    /// ServerApp.empty
    /// |> ServerApp.withStorage blob
    /// |> FactsCompose.withOptions {
    ///     FactsComposeOptions.defaults with
    ///         FactTableWriter = true
    ///         DelegateFacts = [ "sku-sales" ]
    ///         TeamOutputVisibility = Some(TeamOutputVisibilityDeclaration.create TeamVisible [ TeamVisible; TeamAdmins ])
    /// }
    /// |> ServerApp.run
    /// ```
    ///
    /// A contradictory record is refused ONCE, here, before anything
    /// composes, raising `CompositionRefused` with the first of
    /// `FactsComposeOptions.refusals` (read against what the app already
    /// composes), named by its field. Where the store is not enabled the app
    /// is returned unchanged, except that a `QuestionCompiler` is registered
    /// whatever the knob says (GP 11 / GP 13). The store is composed once: a
    /// later `withOptions` composes only its own fields over it, so a
    /// companion builder may add to a root's options. Insert before a RAG
    /// compose, so the retrieval pipeline's DI pickup sees the fact tier, and
    /// after the modules whose grounding declarations a
    /// `GroundingEnvelopeSeal` is to cover.
    /// **Adds** its registrations as contributions under the Facts companion's id; a later `withExtensions` keeps them.
    let withOptions (options: FactsComposeOptions) (app: ServerApp) : ServerApp =
        let app =
            if options.EnableStore then
                {
                    app with
                        Config = {
                            app.Config with
                                Stores = {
                                    app.Config.Stores with
                                        FactStore = FeatureMode.Enabled
                                }
                        }
                }
            else
                app

        match app.Config.Stores.FactStore with
        | FeatureMode.Disabled -> app |> applyIf options.QuestionCompiler applyQuestionCompiler
        | FeatureMode.Enabled ->
            match FactsComposeOptions.refusalsOver (writerComposed app) (declaredToolNames app) options with
            | refusal :: _ -> ServiceComposition.refuse refusal
            | [] ->
                app
                |> composeStore
                |> applyIf options.StoreImplementation applyStoreImplementation
                |> applyIf options.QuestionCompiler applyQuestionCompiler
                |> applyIf options.DisclosurePurposes applyDisclosurePurposes
                |> applyIf options.DeclassificationBudgets applyDeclassificationBudgets
                |> applyIf options.DisclosureFanOut applyDisclosureFanOut
                |> (if options.FactTableWriter then applyFactTableWriter else id)
                |> applyDelegateFacts options.DelegateFacts
                |> applyIf options.CoverageNarratives applyCoverageNarratives
                |> applyIf options.DeliveryTrigger applyDeliveryTrigger
                |> (if options.ToleranceTriggers.IsEmpty then
                        id
                    else
                        applyToleranceTriggers options.ToleranceTriggers)
                |> applyIf options.TeamOutputVisibility applyTeamOutputVisibility
                |> applyIf options.FactPublication applyFactPublication
                |> applyIf options.FactImport applyFactImport
                |> applyIf options.CoherenceChecks applyCoherenceChecks
                |> applyIf options.NarrativeCalculations applyNarrativeCalculations
                |> applyIf options.PipelineProfile applyPipelineProfile
                |> declareTools options.SpecTools
                |> applyIf options.Methods applyMethods
                |> applyIf options.DatasetModels applyDatasetModels
                |> applyIf options.FactTables applyFactTables
                |> fun app ->
                    options.WithholdingSurfaces
                    |> List.fold (fun app (name, surface) -> addWithholdingSurface name surface app) app
                |> applyIf options.GroundingEnvelopeSeal applyGroundingEnvelopeSeal

    // ─── The setters the options record replaced ─────────────────────
    //
    // Each composes the one field it names through `withOptions` (the
    // withholding surface, which only renamed, through `addWithholdingSurface`),
    // and keeps compiling for the deprecation window
    // (docs/platform/deprecation-policy.md).

    /// Composes the fact store alone: `withOptions FactsComposeOptions.defaults`.
    [<Obsolete("Use FactsCompose.withOptions FactsComposeOptions.defaults. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withFactStore (app: ServerApp) : ServerApp =
        withOptions FactsComposeOptions.defaults app

    /// Adds a withholding surface: `addWithholdingSurface`.
    [<Obsolete("Use FactsCompose.addWithholdingSurface from a builder, or FactsComposeOptions.WithholdingSurfaces in a root's withOptions. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withWithholdingSurface (name: string) (surface: FactEgressSurface) (app: ServerApp) : ServerApp =
        addWithholdingSurface name surface app

    /// Composes the one field it sets, `FactsComposeOptions.NarrativeCalculations`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.NarrativeCalculations. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withNarrativeCalculations (calculations: NarrativeCalculations) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    NarrativeCalculations = Some calculations
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.PipelineProfile`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.PipelineProfile. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withPipelineProfile (profile: RegisteredPipelineProfile) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    PipelineProfile = Some profile
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.SpecTools`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.SpecTools (FactsComposeOptions.specTool). See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withSpecTool (tool: SpecTool<'Spec, 'Refusal, 'Result>) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    SpecTools = [ FactsComposeOptions.specTool tool ]
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.Methods`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.Methods. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withMethods (profile: MethodProfile) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    Methods = Some profile
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.DatasetModels`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.DatasetModels (DatasetModelsDeclaration.create). See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withDatasetModels
        (resolver: DisclosurePolicyResolver)
        (registry: DatasetModelRegistry)
        (app: ServerApp)
        : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    DatasetModels = Some(DatasetModelsDeclaration.create resolver registry)
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.FactTables`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.FactTables. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withFactTablesOptions (options: FactTablesOptions) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    FactTables = Some options
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.DisclosurePurposes`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.DisclosurePurposes. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withDisclosurePurposes (config: DisclosurePurposeConfig) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    DisclosurePurposes = Some config
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.DeclassificationBudgets`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.DeclassificationBudgets. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withDeclassificationBudgetConfig (config: DeclassificationBudgetConfig) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    DeclassificationBudgets = Some config
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.DeclassificationBudgets`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.DeclassificationBudgets (DeclassificationBudgetConfig.create). See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withDeclassificationBudgets
        (ledger: IPrivacyBudgetLedger)
        (budgets: DeclassificationBudget list)
        (app: ServerApp)
        : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    DeclassificationBudgets = Some(DeclassificationBudgetConfig.create ledger budgets)
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.DisclosureFanOut`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.DisclosureFanOut. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withDisclosureFanOut (maxConcurrentReads: int) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    DisclosureFanOut = Some maxConcurrentReads
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.FactImport`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.FactImport. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withFactImport (anchors: PeerTrustAnchor list) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    FactImport = Some anchors
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.GroundingEnvelopeSeal`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.GroundingEnvelopeSeal (GroundingEnvelopeSealDeclaration.create). See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withGroundingEnvelopeSeal
        (profile: CompositionProfile)
        (observe: (unit -> GroundingEnvelope) option)
        (app: ServerApp)
        : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    GroundingEnvelopeSeal = Some(GroundingEnvelopeSealDeclaration.create profile observe)
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.CoverageNarratives`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.CoverageNarratives. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withCoverageNarratives (options: CoverageNarrative.CoverageNarrativeOptions) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    CoverageNarratives = Some options
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.CoherenceChecks`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.CoherenceChecks (CoherenceCheckSchedule.create). See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withCoherenceChecksConfig
        (config: CoherenceConfig)
        (cadence: Trigger)
        (principals: DeclaredPrincipal list)
        (app: ServerApp)
        : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    CoherenceChecks = Some(CoherenceCheckSchedule.create config cadence principals)
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.CoherenceChecks`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.CoherenceChecks (CoherenceCheckSchedule.forPrincipal). See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withCoherenceChecks (cadence: Trigger) (principal: DeclaredPrincipal) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    CoherenceChecks = Some(CoherenceCheckSchedule.forPrincipal cadence principal)
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.FactTableWriter`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.FactTableWriter. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withFactTableWriter (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    FactTableWriter = true
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.DeliveryTrigger`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.DeliveryTrigger. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withDeliveryTrigger (runsFor: string -> string list) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    DeliveryTrigger = Some runsFor
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.ToleranceTriggers`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.ToleranceTriggers. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withToleranceTriggers (triggers: ToleranceTrigger list) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    ToleranceTriggers = triggers
            }
            app

    /// Turns the fact store on and composes it with the default writer: `EnableStore` and `FactTableWriter`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.EnableStore and FactTableWriter. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withFactTier (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    EnableStore = true
                    FactTableWriter = true
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.QuestionCompiler`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.QuestionCompiler. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withQuestionCompiler (compiler: QuestionCompiler) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    QuestionCompiler = Some compiler
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.StoreImplementation`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.StoreImplementation (FactStoreReplacement.create). See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withFactStoreImplementation
        (backend: string)
        (factory: IServiceProvider -> IFactStore)
        (app: ServerApp)
        : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    StoreImplementation = Some(FactStoreReplacement.create backend factory)
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.DelegateFacts`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.DelegateFacts. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withDelegateFacts (tableIds: string list) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    DelegateFacts = tableIds
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.TeamOutputVisibility`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.TeamOutputVisibility (TeamOutputVisibilityDeclaration.create). See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withTeamOutputVisibility
        (defaultLevel: TeamVisibilityLevel)
        (allowed: TeamVisibilityLevel list)
        (app: ServerApp)
        : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    TeamOutputVisibility = Some(TeamOutputVisibilityDeclaration.create defaultLevel allowed)
            }
            app

    /// Composes the one field it sets, `FactsComposeOptions.FactPublication`.
    [<Obsolete("Use FactsCompose.withOptions with FactsComposeOptions.FactPublication. See docs/migrations/1159-factscompose-options.md. Removed in a future major.")>]
    let withFactPublication (config: FactPublicationConfig) (app: ServerApp) : ServerApp =
        withOptions
            {
                FactsComposeOptions.defaults with
                    FactPublication = Some config
            }
            app