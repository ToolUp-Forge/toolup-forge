// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.BlobStorage

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
/// `ServerConfig.TenantLifecycle = EnabledTenantLifecycle`; written after
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
    /// Reserved SDK-level container (see `IBlobStorage` scope-discipline
    /// docs). Tenant-lifecycle summaries are platform metadata, not
    /// tenant data, so they live under `_platform`, not a tenant scope.
    let lifecycleContainer = "_platform"

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

    let blobNameFor (scopeId: string) : string =
        lifecyclePrefix + (sanitiseScopeId scopeId) + ".json"

    /// FableConverters-backed STJ options — the canonical wire shape for
    /// F# records / DUs / options so the persisted `LifecycleSummary`
    /// round-trips with its `TenantLifecyclePhase` DU + nested outcome
    /// list intact. Constructed once at module level.
    let jsonOptions = FableConverters.create ()

/// Phase 54e — `ILifecycleSummaryStore` backed by `IBlobStorage`. Each
/// scope's last summary lives at
/// `_platform/_tenant-lifecycle/{scopeId}.json`. Cluster-portable: any
/// blob backend (filesystem / Azure / S3 / GCS) works, and every node
/// reads the same last summary on each call.
type BlobBackedLifecycleSummaryStore(blobs: IBlobStorage) =
    interface ILifecycleSummaryStore with
        member _.GetLast(scopeId: string) : Async<LifecycleSummary option> = async {
            let name = blobNameFor scopeId
            let! exists = blobs.Exists(lifecycleContainer, name)

            if not exists then
                return None
            else
                match! blobs.Download(lifecycleContainer, name) with
                | Result.Ok bytes ->
                    try
                        return
                            Some(
                                JsonSerializer.Deserialize<LifecycleSummary>(Encoding.UTF8.GetString bytes, jsonOptions)
                            )
                    with _ ->
                        // A corrupt / unreadable sidecar reads as "no last
                        // summary" rather than throwing into the admin read
                        // path — the audit trail is the durable record of truth.
                        return None
                | Result.Error _ -> return None
        }

        member _.SetLast(scopeId: string, summary: LifecycleSummary) : Async<unit> = async {
            let name = blobNameFor scopeId
            let bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(summary, jsonOptions))
            let! result = blobs.Upload(lifecycleContainer, name, bytes)

            match result with
            | Result.Ok _ -> return ()
            | Result.Error err ->
                return raise (InvalidOperationException(sprintf "Failed to persist tenant-lifecycle summary: %s" err))
        }

module BlobBackedLifecycleSummaryStore =
    /// Construct an `ILifecycleSummaryStore` backed by the given `IBlobStorage`.
    let create (blobs: IBlobStorage) : ILifecycleSummaryStore =
        BlobBackedLifecycleSummaryStore(blobs) :> ILifecycleSummaryStore