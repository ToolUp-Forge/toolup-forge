// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── MaintenanceApi (Fable.Remoting wire surface) ────────────────
//
// Owner-only admin API for rebuilding secondary indexes from
// canonical state. The endpoints are no-ops in the happy path —
// drift between an index and its canonical record surfaces in
// `/dev/inspect`, and `Rebuild` is the recovery operation when
// drift is observed.
//
// **Scope discipline.** Every method takes the caller's scope
// implicitly via `AccessContext` resolution — there is no `scopeId`
// parameter. The handler stamps the resolved scope server-side so
// admins from team A cannot rebuild team B's indexes.
//
// **Write gating.** Owner / Admin only in `Team` / `MultiTeam`
// mode (mirrors `JobApi.Schedule` / `Cancel`). Other modes are
// ungated within the caller's own scope.
//
// **Idempotency.** Both rebuild operations are idempotent — re-
// running them is safe. Each emits one `_platform.maintenance`
// `IndexRebuilt` event for the audit trail.

type MaintenanceApi = {
    /// Rebuild the by-type and by-source indexes for
    /// `PersistentEventStore` in the caller's scope. Returns the
    /// count of canonical events processed, or `Error` when the
    /// event store is in-memory (no indexes to rebuild) or when
    /// the caller lacks Owner / Admin role.
    RebuildEventIndexes: unit -> Async<Result<int, string>>

    /// Rebuild the idempotency-key and next-run indexes for
    /// `BlobJobStore` in the caller's scope. Returns the count of
    /// canonical definitions processed, or `Error` when no job
    /// scheduler is enabled or when the caller lacks Owner / Admin
    /// role.
    RebuildJobIndexes: unit -> Async<Result<int, string>>
}