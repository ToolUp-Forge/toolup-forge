// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Compose

open ToolUp.Platform
open ToolUp.Platform.Jobs

open System
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

// ─── Phase 54e — durable last-`LifecycleSummary` store ───────────────
//
// Phase 54 backed `GetLifecycleSummary` with a process-local
// `TenantLifecycleSnapshot` — lost on restart and per-replica in a
// cluster, so an operator couldn't reliably answer "what did the last
// offboard of team-X do?" after a deploy. This interface persists the
// last summary per scope so the answer survives a restart and is the
// same on every replica; the process-local snapshot demotes to a
// read-through cache in front of it (`PlatformTenantApiHandler`).
//
// The durable record of the *fact* an offboard happened remains the
// audit trail (`TenantProvisioned` / `TenantDeprovisioned`); this store
// exists only so the admin UI can render the last run's per-hook
// disposition without replaying audit events.
//
// **Six-rule portability audit (Phase 9c, GP 12):**
//   1. Identity by value      — `scopeId` is a string; the summary is an
//                               immutable record of value types. No live
//                               handle crosses the boundary.
//   2. Async at every boundary — both methods return `Async<_>`.
//   3. Retry/supervision as data — failure surfaces as a raised error the
//                                  caller may treat as best-effort; no
//                                  `OnFailure` callback parameter.
//   4. Stateless between calls — the blob-backed default holds no
//                                in-memory state; every read/write
//                                round-trips to `IBlobStorage`, so any
//                                replica reads what any other wrote.
//   5. No cross-shard ordering — keyed per `scopeId`; no ordering is
//                                promised across scopes.
//   6. Precision at lower bound — no timing primitive; n/a.

/// Durable backing for the most-recent `LifecycleSummary` per tenant
/// scope. Registered as a DI singleton when
/// `ServerConfig.TenantLifecycle = FeatureMode.Enabled`; written after
/// each provision/deprovision run and read by `GetLifecycleSummary` on a
/// process-local cache miss. A deployment that swaps in a distributed
/// implementation (DB-backed, Redis-backed) satisfies the same six-rule
/// contract — the blob default is just the zero-dependency floor.
type ILifecycleSummaryStore =
    /// The last persisted summary for `scopeId`, or `None` when no run
    /// has been persisted for it. Stateless — reads the durable backing
    /// on every call, so a fresh replica (or a post-restart process)
    /// returns the last summary any replica wrote.
    abstract GetLast: scopeId: string -> Async<LifecycleSummary option>

    /// Persist `summary` as the last run for `scopeId`, overwriting any
    /// prior value. Best-effort from the caller's perspective — a
    /// persistence failure must not fail an offboard (the run already
    /// completed and the audit trail already recorded it), so the
    /// handler wraps this call.
    abstract SetLast: scopeId: string * summary: LifecycleSummary -> Async<unit>

// ─── blob-backed default ─────────────────────────────────────────────

[<AutoOpen>]
module private BlobBackedLifecycleSummaryStoreInternal =
    /// Blob-name prefix under the container. Mirrors the
    /// `_platform/trusted-publishers/` convention.
    let lifecyclePrefix = "_tenant-lifecycle/"

    /// `scopeId` forms part of a blob name. Sanitise to alphanumerics +
    /// `-` / `_` / `.`; reject path separators outright (they would
    /// escape the container prefix). Scope ids the resolver mints
    /// (`team-{guid}`, `user-{id}`, `session-{id}`) pass through
    /// unchanged. Mirrors `BlobBackedPublisherKeyStore.sanitiseKeyId`.
    let sanitiseScopeId (scopeId: string) : string =
        if String.IsNullOrWhiteSpace scopeId then
            raise (ArgumentException("scopeId must be non-empty", nameof scopeId))

        if scopeId.Contains('/') || scopeId.Contains('\\') then
            raise (
                ArgumentException(sprintf "scopeId must not contain path separators; got %s" scopeId, nameof scopeId)
            )

        scopeId
        |> Seq.map (fun c ->
            if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then
                c
            else
                '_')
        |> Seq.toArray
        |> String

    /// Tenant-lifecycle summaries are platform metadata, not tenant data,
    /// so they live in the reserved `_platform` container, not a tenant
    /// scope.
    let pathFor (scopeId: string) : DocPath =
        DocPath.ofString (lifecyclePrefix + (sanitiseScopeId scopeId) + ".json")

/// Phase 54e — `ILifecycleSummaryStore` backed by `IBlobStorage`. Each
/// scope's last summary lives at
/// `_platform/_tenant-lifecycle/{scopeId}.json`. Cluster-portable: any
/// blob backend (filesystem / Azure / S3 / GCS) works, and every node
/// reads the same last summary on each call.
///
/// A summary that cannot be read or decoded reads as "no last summary" —
/// the audit trail is the durable record of truth, and this is the admin
/// read path's convenience — and is logged (an undecodable one set aside
/// by the store). `SetLast` replaces the summary whole, so the next run's
/// summary takes the place of one that did not decode.
type BlobBackedLifecycleSummaryStore(blobs: IBlobStorage) =
    let logger = JsonConsoleLogger.JsonConsoleLogger() :> ILogger

    let docs =
        BlobDocumentStore<LifecycleSummary>(blobs, BlobCodec.json<LifecycleSummary>, logger)

    interface ILifecycleSummaryStore with
        member _.GetLast(scopeId: string) : Async<LifecycleSummary option> = async {
            match! docs.Read(ScopeKey.platform, pathFor scopeId) with
            | Ok summary -> return summary
            | Error e ->
                logger.Warn($"[LifecycleSummaryStore] {BlobMapStoreError.describe e}; reporting no last summary")
                return None
        }

        member _.SetLast(scopeId: string, summary: LifecycleSummary) : Async<unit> = async {
            match! docs.Replace(ScopeKey.platform, pathFor scopeId, summary) with
            | Ok() -> return ()
            | Error e ->
                return
                    raise (
                        InvalidOperationException(
                            sprintf "Failed to persist tenant-lifecycle summary: %s" (BlobMapStoreError.describe e)
                        )
                    )
        }

module BlobBackedLifecycleSummaryStore =
    /// Construct an `ILifecycleSummaryStore` backed by the given `IBlobStorage`.
    let create (blobs: IBlobStorage) : ILifecycleSummaryStore =
        BlobBackedLifecycleSummaryStore(blobs) :> ILifecycleSummaryStore