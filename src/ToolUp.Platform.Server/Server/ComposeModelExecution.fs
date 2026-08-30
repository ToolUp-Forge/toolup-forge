// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ComposeModelExecution

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform

// ─── Phase 728 — the model-execution compose leg ─────────────────────────
//
// `ModelExecutionApi` resolves `IModelRegistry` from DI per request, and
// until this file existed no forge compose path ever registered one: the
// only registrations in the tree were test fixtures. A deployment that
// mounted the API therefore had to hand-register the registry, and
// discovered that on its first request as a `SubstrateDisabled "model
// registry"` refusal rather than at composition.
//
// This leg registers it. It is **opt-in and absent by default** (GP 13) —
// a deployment that never calls `ServerApp.withModelExecution` appends no
// registration at all, so its composed graph is byte-for-byte what it was.
//
// **What the leg covers, and what it deliberately does not.** The
// model-execution face resolves ten services; this leg fills the ONE forge
// can honestly build a default for, and `ModelExecutionDepsValidator`
// names it at startup when the API is mounted without it.
//
//   | Resolution                  | Who composes it                       |
//   |-----------------------------|---------------------------------------|
//   | `AccessContext`             | forge (scope-resolver, per request)   |
//   | `ITeamStore`                | forge (`ComposeTeamRuntime`)          |
//   | `IJobScheduler`             | forge (`ComposeJobs`)                 |
//   | `IAuditLog`                 | forge (`ComposeRuntimeServices`)      |
//   | `IDatasetStore`             | forge (`ComposeStores`, opt-in mode)  |
//   | `ModelFitProviderRegistry`  | forge (`ComposeJobs`, opt-in mode)    |
//   | `ComputeBudgetGuard`        | forge (`ComposeStores`, opt-in mode)  |
//   | `ModelExecutionPolicy`      | **this leg** (optional; permissive)   |
//   | `IModelRegistry`            | **this leg** (the finding)            |
//   | `IModelScorer`              | consumer — see below                  |
//
// **`IModelScorer` stays consumer-supplied, and that is a decision rather
// than an omission.** Forge can build a default registry because
// `BlobModelRegistry` needs only substrate forge already composes
// (`IDataObjectStore` + `IAuditLog`, and `ILineageStore` when present). It
// cannot build a default scorer: `ModelScorer` needs a
// `ModelScoreProviderRegistry`, and a provider registry with no providers
// scores nothing — it would answer every request with a refusal that reads
// as a forge defect rather than as an unconfigured deployment. So the leg
// accepts a scorer if the composition root holds one, and otherwise leaves
// the honest `SubstrateDisabled "model scorer"` refusal in place.
//
// **`TryAddSingleton` throughout**, so a consumer that pre-registered its
// own `IModelRegistry` (a companion store, a decorated registry) is never
// overridden by composing this leg.

/// Phase 728 — what a deployment declares when it composes the
/// model-execution leg.
///
/// **A new options record rather than new `ServerConfig` fields.** Every
/// field here is a live substrate instance or an observer list, none of
/// which belongs in the Fable-compiled config record; and widening
/// `ServerConfig` would retype its constructor for every consumer that
/// builds one positionally. Existing compositions are untouched (GP 11).
///
/// Every field is optional / empty in `ModelExecutionComposeOptions.defaults`:
/// composing the leg with the defaults registers the blob-backed registry
/// over the substrate forge already composed, and nothing else.
type ModelExecutionComposeOptions = {
    /// The registry to register. `None` (the default) builds the
    /// blob-backed `BlobModelRegistry` over the composed
    /// `IDataObjectStore` + `IAuditLog`, emitting the artifact →
    /// dataset-version lineage edge when an `ILineageStore` is composed.
    /// `Some` registers the supplied instance instead — a companion store,
    /// or one already wrapped by the caller.
    Registry: IModelRegistry option
    /// Phase 651 registration observers to wrap the registry with. Merged
    /// with any `IModelRegistrationObserver` registered in DI (a companion
    /// contributes that way); duplicates by `Name` are dropped, first
    /// occurrence winning. Empty (the default) leaves the registry
    /// undecorated — `ModelRegistrationObservers.decorate` returns the same
    /// object, so an unobserved deployment holds exactly what it built.
    Observers: IModelRegistrationObserver list
    /// The scorer `RequestScore` resolves. `None` (the default) registers
    /// nothing and leaves the typed `SubstrateDisabled "model scorer"`
    /// refusal in place — forge cannot build a default scorer without
    /// score providers (see the file header).
    Scorer: IModelScorer option
    /// Phase 640's executor policy. `None` (the default) registers nothing,
    /// and the handler falls back to `ModelExecutionPolicy.permissive` —
    /// byte-for-byte the behaviour a deployment had before it composed
    /// anything (GP 11).
    Policy: ModelExecutionPolicy option
    /// The provenance attachment cap the default registry declares.
    /// Ignored when `Registry` is `Some` (the supplied instance declares
    /// its own). Defaults to `ProvenanceAttachmentLimits.default'`.
    AttachmentLimits: ProvenanceAttachmentLimits
}

[<RequireQualifiedAccess>]
module ModelExecutionComposeOptions =
    /// The leg at its defaults: the blob-backed registry over composed
    /// substrate, no observers, no scorer, no policy override.
    let defaults: ModelExecutionComposeOptions = {
        Registry = None
        Observers = []
        Scorer = None
        Policy = None
        AttachmentLimits = ProvenanceAttachmentLimits.default'
    }

    /// Register a specific `IModelRegistry` instead of the blob-backed
    /// default.
    let withRegistry (registry: IModelRegistry) (options: ModelExecutionComposeOptions) = {
        options with
            Registry = Some registry
    }

    /// Append a Phase 651 registration observer.
    let withObserver (observer: IModelRegistrationObserver) (options: ModelExecutionComposeOptions) = {
        options with
            Observers = options.Observers @ [ observer ]
    }

    /// Supply the `IModelScorer` the `RequestScore` face resolves.
    let withScorer (scorer: IModelScorer) (options: ModelExecutionComposeOptions) = {
        options with
            Scorer = Some scorer
    }

    /// Declare the Phase 640 executor policy.
    let withPolicy (policy: ModelExecutionPolicy) (options: ModelExecutionComposeOptions) = {
        options with
            Policy = Some policy
    }

    /// Declare the provenance attachment cap the default registry
    /// publishes.
    let withAttachmentLimits (limits: ProvenanceAttachmentLimits) (options: ModelExecutionComposeOptions) = {
        options with
            AttachmentLimits = limits
    }

/// The stable name recorded on the observer-isolation audit rows this leg's
/// decoration produces, and the label the deps validator names in its
/// remedy. Kept beside the leg so a message and the thing it names cannot
/// drift.
[<Literal>]
let BuilderName = "ServerApp.withModelExecution"

/// Merge the DI-registered observers with the declared ones, dropping
/// duplicates by `Name` (first occurrence wins).
///
/// DI first, deliberately: a companion that registered an observer did so
/// as part of composing itself, and a composition root that then names the
/// same observer is restating rather than replacing it.
let private mergeObservers
    (fromDi: IModelRegistrationObserver list)
    (declared: IModelRegistrationObserver list)
    : IModelRegistrationObserver list =
    (fromDi @ declared)
    |> List.fold
        (fun acc observer ->
            if
                acc
                |> List.exists (fun (existing: IModelRegistrationObserver) -> existing.Name = observer.Name)
            then
                acc
            else
                acc @ [ observer ])
        []

/// Phase 728 — register the model-execution leg into the service
/// collection.
///
/// Lazy factory throughout: nothing is constructed until something
/// resolves `IModelRegistry`, so a deployment that composes the leg but
/// never reaches the model-execution face pays only the registration
/// (GP 13).
let register (options: ModelExecutionComposeOptions) (services: IServiceCollection) : unit =
    services.TryAddSingleton<IModelRegistry>(fun (sp: System.IServiceProvider) ->
        let logger =
            match sp.GetService(typeof<ILogger>) with
            | :? ILogger as l -> l
            | _ -> ConsoleLogger.ConsoleLogger() :> ILogger

        let audit =
            match sp.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as a -> a
            | _ ->
                // Unreachable in a forge composition (`ComposeRuntimeServices`
                // always registers one), but stated rather than cast: a null
                // reference here would surface as an NRE inside the registry's
                // first audited write, which names nothing.
                failwith
                    $"{BuilderName} needs an IAuditLog in DI to build the default model registry. Compose one, or supply a registry with ModelExecutionComposeOptions.withRegistry."

        let inner =
            match options.Registry with
            | Some registry -> registry
            | None ->
                let dataObjects =
                    match sp.GetService(typeof<IDataObjectStore>) with
                    | :? IDataObjectStore as store -> store
                    | _ ->
                        failwith
                            $"{BuilderName} needs an IDataObjectStore in DI to build the default model registry. Compose one, or supply a registry with ModelExecutionComposeOptions.withRegistry."

                let lineage =
                    match sp.GetService(typeof<ILineageStore>) with
                    | :? ILineageStore as l -> Some l
                    | _ -> None

                BlobModelRegistry.createWithLimits dataObjects audit lineage options.AttachmentLimits

        let observers =
            mergeObservers (sp.GetServices<IModelRegistrationObserver>() |> List.ofSeq) options.Observers

        ModelRegistrationObservers.decorate { Audit = audit; Logger = logger } observers inner)

    match options.Scorer with
    | None -> ()
    | Some scorer -> services.TryAddSingleton<IModelScorer>(scorer)

    match options.Policy with
    | None -> ()
    | Some policy -> services.TryAddSingleton<ModelExecutionPolicy>(policy)