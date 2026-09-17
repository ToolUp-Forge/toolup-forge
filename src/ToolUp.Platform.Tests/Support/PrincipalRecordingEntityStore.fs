// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Support.PrincipalRecordingEntityStore

open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

// ─── Phase 814 — what a module seam hands the entity store ───────────
//
// The four module seams (`IFormStore`, `IReportTemplateStore`,
// `IBookingScheduler`, `INarrativePagePublisher`) carry a principal so
// the lifecycle row the entity store records names the caller rather
// than the host. Their contract packs bind over a test project's
// in-memory `IEntityStore` fake, which records no rows — so the pack
// observes the seam one layer down instead: this decorator sits between
// the module store and whatever `IEntityStore` the pack was handed, and
// records the principal on every MUTATING call. That the store then
// stamps the row with exactly that principal is `IEntityStoreContract
// .auditTests`' claim (Phase 806), proved once against the real stores;
// this pack's claim is the half above it — the seam passed the caller
// through, and did not substitute `EntityPrincipal.system`.
//
// Linked (not copied) into the module test projects, as
// `TestRegistrationGuard.fs` is.

/// One mutating call as the decorated store saw it.
type PrincipalCall = {
    /// `Save` / `SaveIfVersion` / `Delete` / `DeleteIfVersion`.
    Member: string
    /// The scope the call named.
    ScopeId: string
    /// The principal the seam handed the store — the whole point.
    Principal: EntityPrincipal
}

/// Records the principal on every mutating `IEntityStore` call and
/// delegates everything to `inner` unchanged.
type PrincipalRecordingEntityStore(inner: IEntityStore) =
    let calls = System.Collections.Concurrent.ConcurrentQueue<PrincipalCall>()

    let record name scopeId principal =
        calls.Enqueue {
            Member = name
            ScopeId = scopeId
            Principal = principal
        }

    /// Every mutating call so far, in call order.
    member _.Calls = calls |> Seq.toList

    /// The principals handed to the store, in call order — the shape a
    /// pack compares against the callers it passed.
    member _.Principals = calls |> Seq.map _.Principal |> Seq.toList

    interface IEntityStore with
        member _.Save<'T>(scopeId, actor, entity: 'T) =
            record "Save" scopeId actor
            inner.Save<'T>(scopeId, actor, entity)

        member _.SaveIfVersion<'T>(scopeId, actor, entity: 'T, expectedVersion) =
            record "SaveIfVersion" scopeId actor
            inner.SaveIfVersion<'T>(scopeId, actor, entity, expectedVersion)

        member _.Get<'T>(scopeId, entityType, entityId) =
            inner.Get<'T>(scopeId, entityType, entityId)

        member _.GetVersion<'T>(scopeId, entityType, entityId, version) =
            inner.GetVersion<'T>(scopeId, entityType, entityId, version)

        member _.ListVersions<'T>(scopeId, entityType, entityId) =
            inner.ListVersions<'T>(scopeId, entityType, entityId)

        member _.Delete(scopeId, actor, entityType, entityId) =
            record "Delete" scopeId actor
            inner.Delete(scopeId, actor, entityType, entityId)

        member _.DeleteIfVersion(scopeId, actor, entityType, entityId, expectedVersion) =
            record "DeleteIfVersion" scopeId actor
            inner.DeleteIfVersion(scopeId, actor, entityType, entityId, expectedVersion)

        member _.FindByIndex<'T>(scopeId, entityType, indexName, value) =
            inner.FindByIndex<'T>(scopeId, entityType, indexName, value)

        member _.Count(scopeId, entityType) = inner.Count(scopeId, entityType)

        member _.ListAll<'T>(scopeId, entityType, skip, take) =
            inner.ListAll<'T>(scopeId, entityType, skip, take)

        member _.Query<'T>(scopeId, query) = inner.Query<'T>(scopeId, query)