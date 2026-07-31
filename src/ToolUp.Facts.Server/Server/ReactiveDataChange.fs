// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform

// ─── ReactiveDataChange (Phase 623.C — react to data arrival) ─────────
//
// Phase 561 built `RecomputeJobHandler.reactToDataChange` and left
// it with no caller: nothing in a composed deployment told the fact tier
// that a data-object version had landed, so "reactive" recomputation was
// only ever schedule-driven. This file is the seam that closes it.
//
// **The seam is `IDataObjectStore`, and specifically its version-producing
// methods (`Save` / `Recover`).** That is where a data-object version
// *arrives* — the one event the invalidation walk is defined against. The
// two nearby candidates were considered and rejected:
//
//   * `ILineageStore.Record` fires when a *link* is written, and the link
//     names the object that was **produced** (`ToObjectId`) plus the
//     inputs it consumed. A brand-new object is cited by no fact yet, so
//     seeding the walk from a link invalidates nothing; seeding from its
//     `FromObjectId` would invalidate every fact citing an input that did
//     not change. Lineage is still walked — `invalidationSet` unions the
//     changed object's descendants — but it is the *derivation* of the
//     invalidation set, not its trigger.
//   * An `IEventStore` decorator (the `HookedEventStore` idiom) sits one
//     level below and would have to reconstruct "which object version is
//     this" from event payloads, re-deriving what `Save` already returns
//     typed.
//
// **The seed — and why it includes the object's PRIOR versions.** A fact
// cites its inputs by identity, and `Fact.compute` folds those identities
// (not the value) into the content address. So a `Computed` fact citing
// version v1 is invalidated by v2's arrival *because it cites v1*, and the
// recompute that follows produces a new head only by citing v2. The seed
// is therefore the whole identity set the arriving version supersedes or
// introduces: the stable `ObjectId` (the identity `ILineageStore` nodes
// carry), plus the `ContentHash` of **every** version of that object —
// the new one and the earlier ones the facts in the base actually name.
// Seeding only the new version's identities would invalidate nothing,
// because no fact can yet cite a version that has only just landed.
// `invalidationSet` then unions each seed's lineage descendants, so a fact
// computed from an object *derived from* the changed one is invalidated
// too.
//
// **Zero cost when unused (GP 13).** The decorator is registered only by
// a deployment that composes the fact tier, and it self-gates *before* the
// version read: unless a composed `Grounding.IMetricRegistry` declares at
// least one non-`Manual` `RecomputePolicy` *and* an `IJobScheduler` is
// present, a save costs one boolean test over an immutable singleton and
// touches no store. A deployment that declares no recompute policy
// therefore behaves — and pays — exactly as it did before Phase 623; the
// declaration IS the opt-in.
//
// **Never breaks a write.** The reaction runs after the inner store has
// committed and its outcome never changes the `Save` result: a throw is
// caught and logged at `Warn`. A fact base that failed to react is stale,
// which is recoverable; a data write that failed because the fact tier
// threw is not.
//
// **Awaited, not fire-and-forget.** The reaction is part of the same
// `Async` as the save. Firing it detached would make "the data landed" and
// "the facts reacted" unordered, which is untestable through a compose
// root and unobservable in production — the exact shape that let Phase
// 561's gap sit unnoticed. The cost is bounded by the gate above.

/// The reaction a data-object version arrival triggers: given the scope
/// and the changed input identities, drive whatever the fact tier does
/// about it. Kept as a function seam (rather than a hard dependency on
/// `RecomputeJobHandler`) so the decorator is directly testable and the
/// DI resolution lives in `FactsCompose`.
type FactDataChangeReaction = string -> string list -> Async<unit>

/// Decorator over the composed `IDataObjectStore` that drives fact
/// invalidation when a version lands. Every non-version-producing member
/// delegates verbatim.
type ReactiveDataObjectStore
    (inner: IDataObjectStore, armed: unit -> bool, react: FactDataChangeReaction, logger: ILogger) =

    /// Seed the invalidation walk from a landed version. Failures are
    /// contained — the write has already committed.
    let onVersion (scopeId: string) (dataObject: DataObject) : Async<unit> = async {
        if armed () then
            try
                // Every identity this object has ever had: the facts in
                // the base cite the SUPERSEDED versions, so seeding only
                // the new one would invalidate nothing.
                let! versions = inner.ListVersions(scopeId, dataObject.ObjectId)

                let changedIds =
                    dataObject.ObjectId
                    :: dataObject.ContentHash
                    :: (versions |> List.map _.ContentHash)
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                    |> List.distinct

                do! react scopeId changedIds
            with ex ->
                logger.Warn(
                    sprintf
                        "[Phase 623] Fact invalidation failed for data object %s in scope %s — the write stands, the fact base may be stale: %s"
                        dataObject.ObjectId
                        scopeId
                        ex.Message
                )
    }

    interface IDataObjectStore with

        member _.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy) = async {
            let! result = inner.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy)

            match result with
            | Ok dataObject -> do! onVersion scopeId dataObject
            | Error _ -> ()

            return result
        }

        member _.Recover(scopeId, objectId, version, createdBy) = async {
            let! result = inner.Recover(scopeId, objectId, version, createdBy)

            match result with
            | Ok dataObject -> do! onVersion scopeId dataObject
            | Error _ -> ()

            return result
        }

        member _.Get(scopeId, objectId) = inner.Get(scopeId, objectId)

        member _.GetVersion(scopeId, objectId, version) =
            inner.GetVersion(scopeId, objectId, version)

        member _.GetContent(scopeId, contentHash) = inner.GetContent(scopeId, contentHash)
        member _.ListVersions(scopeId, objectId) = inner.ListVersions(scopeId, objectId)
        member _.ListObjects scopeId = inner.ListObjects scopeId
        member _.Delete(scopeId, objectId) = inner.Delete(scopeId, objectId)
        member _.Evict(scopeId, objectId) = inner.Evict(scopeId, objectId)
        member _.Purge scopeId = inner.Purge scopeId

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

/// Construction + the DI-resolved reaction the fact tier composes.
module ReactiveDataChange =

    /// Does this deployment's grounding vocabulary ask for reactive
    /// recomputation at all? True when some registered metric declares a
    /// `RecomputePolicy` other than `Manual` — the declaration is the
    /// opt-in, so a deployment that declares none pays nothing (GP 13).
    let declaresReactivePolicy (registry: Grounding.IMetricRegistry option) : bool =
        match registry with
        | None -> false
        | Some reg ->
            reg.Metrics
            |> List.exists (fun metric ->
                match metric.RecomputePolicy with
                | Some policy -> policy <> Grounding.Manual
                | None -> false)

    /// The decorator's short-circuit, evaluated before any store read: is
    /// this deployment asking for reactive recomputation at all, and is
    /// there a scheduler for the `Eager` arm to run on? The registry is an
    /// immutable composed singleton, so the policy scan happens once.
    let gate
        (registry: unit -> Grounding.IMetricRegistry option)
        (scheduler: unit -> IJobScheduler option)
        : unit -> bool =
        let declared = lazy (declaresReactivePolicy (registry ()))
        fun () -> declared.Value && (scheduler ()).IsSome

    /// The reaction wired to `RecomputeJobHandler.reactToDataChange` over
    /// the composed substrate, resolved lazily from the built provider
    /// (nothing here is resolvable at compose time). `inner` is the
    /// *undecorated* lineage/fact substrate the decorator wraps, so the
    /// resolution can never re-enter this decorator.
    ///
    /// Re-checks the same `gate` conditions the decorator applied, so the
    /// reaction is safe to call on its own.
    let reaction
        (factStore: unit -> IFactStore)
        (lineage: unit -> ILineageStore)
        (scheduler: unit -> IJobScheduler option)
        (registry: unit -> Grounding.IMetricRegistry option)
        : FactDataChangeReaction =
        let armed = gate registry scheduler

        fun scopeId changedIds -> async {
            if not (armed ()) || List.isEmpty changedIds then
                return ()
            else
                match scheduler () with
                | None -> return ()
                | Some jobs ->
                    let! _outcomes =
                        RecomputeJobHandler.reactToDataChange
                            (lineage ())
                            (factStore ())
                            jobs
                            (registry ())
                            scopeId
                            changedIds

                    return ()
        }

    /// Wrap `inner` so a landed version drives `react`, guarded by
    /// `armed` — which is consulted before the decorator reads anything.
    let decorate
        (inner: IDataObjectStore)
        (armed: unit -> bool)
        (react: FactDataChangeReaction)
        (logger: ILogger)
        : IDataObjectStore =
        ReactiveDataObjectStore(inner, armed, react, logger) :> IDataObjectStore