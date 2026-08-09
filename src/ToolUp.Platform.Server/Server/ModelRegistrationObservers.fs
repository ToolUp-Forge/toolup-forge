// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 651 — the registration observer seam ─────────────────────────
//
// Phase 453 gave a model artifact a governed lifecycle and a registry to
// hold it in. Phase 645 gave a deployment declared promotion policies that
// judge a newly registered artifact and drive the verdict through Phase
// 644's author-agnostic seam. What was missing between them is the trigger:
// nothing in the platform observed an artifact ARRIVING, so a policy only
// ran where a consumer remembered to call it, and "the refit's output must
// meet its promotion policy" reduced to "somebody must remember".
//
// This file makes that moment a seam. `ModelRegistrationObservers.decorate`
// wraps any `IModelRegistry` and invokes registered observers after a
// successful registration, handing each the artifact record as stored.
//
// **Three properties, each of which is the phase rather than a detail.**
//
//   1. *A replay is not an arrival.* `Register` is idempotent — registering
//      a composite key the scope already holds returns the existing
//      artifact and appends nothing — so firing observers on it would make
//      a retried job look like a new artifact every time it retried. The
//      novelty is read from `IModelRegistrationNovelty`, i.e. decided by
//      the registry at the point it decides its own idempotency, never
//      re-derived here from a second read.
//   2. *Observe, don't gate.* The artifact is durably written before any
//      observer runs. An observer that raised could therefore only make the
//      registrar believe a completed write had failed, so failures are
//      caught, audited (`ModelRegistrationObserverFailed`) and dropped —
//      and one observer's failure never stops the next.
//   3. *Un-composed is unchanged.* `decorate` with no observers returns the
//      registry it was given, the same object, so a deployment that
//      composes none holds exactly what it held before (GP 11) and
//      allocates nothing (GP 13).
//
// **Generic vocabulary (GP 1).** An "observer of a registration" knows
// nothing about what was modelled or why; the promotion-policy binding that
// uses it is declared beside the policy, not here.

/// Phase 651 — the substrate the observer seam runs over.
///
/// `Audit` is not optional, for the reason `ModelTransitionDeps.Audit` is
/// not: an isolated failure that is not recorded is indistinguishable from
/// an observer that had nothing to do, and that indistinguishability is the
/// exact hazard "observe, don't gate" introduces. A deployment with no
/// interest in audit composes the SDK's own log — a decision it makes
/// explicitly rather than by omitting a field.
type ModelRegistrationObserverDeps = {
    Audit: IAuditLog
    /// Operator-visible warning beside the audit row. The trail is the
    /// durable record; this is the one a developer sees in the console
    /// while wiring an observer up.
    Logger: ILogger
}

/// Phase 651 — an `IModelRegistry` that invokes observers on each
/// successful, non-replay registration.
///
/// Every other method delegates verbatim: this is a decorator over the
/// registration path only, and a read or a transition through it behaves
/// exactly as it does through the registry beneath.
type ObservedModelRegistry
    (inner: IModelRegistry, observers: IModelRegistrationObserver list, deps: ModelRegistrationObserverDeps) =

    /// The inner registry's novelty capability, resolved ONCE rather than
    /// per call — the answer cannot change over an instance's life.
    let novelty =
        match box inner with
        | :? IModelRegistrationNovelty as source -> Some source
        | _ -> None

    /// Run every observer, isolating each.
    ///
    /// Sequential rather than parallel, deliberately: observers commonly
    /// write (the promotion binding transitions the artifact), and running
    /// two writers concurrently over one artifact would manufacture the
    /// interleaving a deployment would then have to reason about. The cost
    /// is the sum of a small, deployment-declared list.
    let notify (scopeId: string) (artifact: ModelArtifact) : Async<unit> = async {
        for observer in observers do
            try
                do! observer.OnRegistered(scopeId, artifact)
            with ex ->
                // The registration already succeeded and is already
                // durable. There is nothing to roll back and nothing to
                // report to the caller — only something to record.
                deps.Logger.Warn
                    $"registration observer '{observer.Name}' failed for artifact {artifact.CompositeKey.Hash}: {ex.Message}"

                try
                    do!
                        deps.Audit.Record(
                            scopeId,
                            ModelRegistrationObserverFailed {
                                CompositeKeyHash = artifact.CompositeKey.Hash
                                Observer = observer.Name
                                Reason = ex.Message
                                ScopeId = scopeId
                            }
                        )
                with _ ->
                    // An audit store having a bad day must not turn one
                    // isolated observer failure into a failed registration
                    // — which is the very thing this whole path exists to
                    // prevent.
                    ()
    }

    /// Register through the inner registry and observe the arrival.
    ///
    /// **The novelty is the registry's, or it is approximate and says so.**
    /// A registry declaring `IModelRegistrationNovelty` decides create-vs-
    /// replay at the same point it decides its own idempotency, so at most
    /// one concurrent registration of a key can ever be the creating one.
    /// A registry that does not declare it leaves this seam with only a
    /// pre-read probe, which is a genuine race: two concurrent
    /// registrations would both find the key absent and both observe. That
    /// is a weaker guarantee than the default registry gives and is
    /// documented rather than hidden — it is still strictly better than the
    /// alternatives of firing on every replay or of silently never firing
    /// at all.
    let registerReporting
        (scopeId: string)
        (outcome: FitOutcome)
        (registeredBy: string)
        (annotations: Map<string, string>)
        (notes: string)
        : Async<Result<ModelRegistration, ModelRegistryError>> =
        async {
            match novelty with
            | Some source ->
                match! source.RegisterReporting(scopeId, outcome, registeredBy, annotations, notes) with
                | Error e -> return Error e
                | Ok registration ->
                    if registration.Novelty = ModelRegistrationNovelty.Created then
                        do! notify scopeId registration.Artifact

                    return Ok registration
            | None ->
                let! before = inner.Get(scopeId, outcome.CompositeKey.Hash)
                let alreadyHeld = Result.isOk before

                match! inner.Register(scopeId, outcome, registeredBy, annotations, notes) with
                | Error e -> return Error e
                | Ok artifact ->
                    if not alreadyHeld then
                        do! notify scopeId artifact

                    return
                        Ok {
                            Artifact = artifact
                            Novelty =
                                if alreadyHeld then
                                    ModelRegistrationNovelty.Replayed
                                else
                                    ModelRegistrationNovelty.Created
                        }
        }

    /// Declared so the capability survives decoration. A decorator that hid
    /// its inner registry's novelty reporting would silently degrade
    /// anything wrapped outside it to the probe path above.
    interface IModelRegistrationNovelty with
        member _.RegisterReporting(scopeId, outcome, registeredBy, annotations, notes) =
            registerReporting scopeId outcome registeredBy annotations notes

    interface IModelRegistry with
        member _.Register(scopeId, outcome, registeredBy, annotations, notes) = async {
            match! registerReporting scopeId outcome registeredBy annotations notes with
            | Ok registration -> return Ok registration.Artifact
            | Error e -> return Error e
        }

        member _.Get(scopeId, keyHash) = inner.Get(scopeId, keyHash)

        member _.QueryBySpecHash(scopeId, specHash) =
            inner.QueryBySpecHash(scopeId, specHash)

        member _.QueryByDatasetVersion(scopeId, datasetVersion) =
            inner.QueryByDatasetVersion(scopeId, datasetVersion)

        member _.QueryByStatus(scopeId, status) = inner.QueryByStatus(scopeId, status)

        member _.QueryPage(scopeId, query, cursor, limit) =
            inner.QueryPage(scopeId, query, cursor, limit)

        member _.TransitionStatus(scopeId, keyHash, target, callerRole, actorUserId) =
            inner.TransitionStatus(scopeId, keyHash, target, callerRole, actorUserId)

        member _.AttachProvenance(scopeId, keyHash, attachments, signature) =
            inner.AttachProvenance(scopeId, keyHash, attachments, signature)

        member _.AttachmentLimits = inner.AttachmentLimits

[<RequireQualifiedAccess>]
module ModelRegistrationObservers =

    /// Decorate a registry so each successful, non-replay registration is
    /// observed.
    ///
    /// **An empty observer list returns `inner` itself** — the same object,
    /// not an equivalent wrapper. A deployment that composes no observers
    /// therefore holds exactly the registry it built, with no extra
    /// indirection on any call (GP 11 / GP 13), and a test can assert that
    /// by reference.
    let decorate
        (deps: ModelRegistrationObserverDeps)
        (observers: IModelRegistrationObserver list)
        (inner: IModelRegistry)
        : IModelRegistry =
        if List.isEmpty observers then
            inner
        else
            ObservedModelRegistry(inner, observers, deps) :> IModelRegistry