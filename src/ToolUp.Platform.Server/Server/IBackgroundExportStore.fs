// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 9h.A — IBackgroundExportStore ────────────────────────────────
//
// Lifts Phase 9h's synchronous-in-process DSR export off the HTTP
// request thread and onto `IJobScheduler`, so large-tenant Article 15
// exports (gigabytes across KB + RAG + EventStore + …) don't hit HTTP
// timeouts. `BeginExport` mints a ticket + records it `Preparing`; the
// `DSRExportJobHandler` assembles the envelope on a background job and
// `Complete`s the ticket; the client polls `GetStatus` / `Download`.
//
// Tickets are blob-backed (a status sidecar + the envelope blob) with a
// TTL so envelopes don't accumulate. The ticket carries all state
// (identity-by-value), so the store holds nothing in memory and survives
// process restarts (GP 12 rule 4).

/// Opaque, value-typed handle for a background export. Encodes the scope
/// it belongs to so every store method needs only the ticket (no
/// out-of-band scope threading). Treat as opaque — construct only via
/// `IBackgroundExportStore.BeginExport`.
type ExportTicket = string

/// Lifecycle status of a background export ticket.
type ExportStatus =
    /// The envelope is being assembled by the background job.
    | Preparing
    /// The envelope is assembled and downloadable; `sizeBytes` is its
    /// length.
    | Ready of sizeBytes: int64
    /// The export job failed terminally; `reason` is operator-readable.
    | Failed of reason: string
    /// The ticket was cancelled mid-run (`IJobScheduler.Cancel`).
    | Cancelled
    /// The ticket outlived its TTL; the envelope has been (or will be)
    /// garbage-collected and is no longer downloadable.
    | Expired
    /// No ticket with this id exists (never issued, or GC'd past TTL).
    | Unknown

module ExportStatus =
    /// Stable case-name string for status sidecar persistence + audit.
    let name =
        function
        | Preparing -> "Preparing"
        | Ready _ -> "Ready"
        | Failed _ -> "Failed"
        | Cancelled -> "Cancelled"
        | Expired -> "Expired"
        | Unknown -> "Unknown"

/// Blob-backed store for background DSR export envelopes. The default
/// implementation is `BlobBackedBackgroundExportStore`; a distributed
/// deployment swaps the backing `IBlobStorage` without touching this
/// contract.
///
/// **Six portability rules (GP 12).**
/// 1. *Identity by value* — `ExportTicket` is a string; no live handles.
/// 2. *Async at every boundary* — every method returns `Async<_>`.
/// 3. *Retry as data* — terminal failure surfaces as `ExportStatus.Failed`,
///    not exceptions across the boundary.
/// 4. *Stateless between calls* — the ticket + its blobs carry all state;
///    the store caches nothing.
/// 5. *No cross-shard ordering* — tickets are independent; status
///    transitions are per-ticket only.
/// 6. *Precision N/A* — TTL is coarse (the status records an `expiresAt`);
///    no sub-second timing promise.
type IBackgroundExportStore =
    /// Mint a ticket for `request` under `scopeId` and record it
    /// `Preparing`. The envelope is filled later by the export job.
    abstract BeginExport: scopeId: string * request: DataSubjectRequest -> Async<ExportTicket>

    /// Current status of `ticket`. Returns `Unknown` for an unissued id,
    /// `Expired` once the TTL has lapsed.
    abstract GetStatus: ticket: ExportTicket -> Async<ExportStatus>

    /// Store the assembled envelope bytes for `ticket` and flip it to
    /// `Ready`. Called by `DSRExportJobHandler` after `executeExport`.
    abstract Complete: ticket: ExportTicket * envelope: byte[] -> Async<unit>

    /// Mark `ticket` terminally failed with an operator-readable reason.
    abstract Fail: ticket: ExportTicket * reason: string -> Async<unit>

    /// Mark `ticket` cancelled (mid-run abort).
    abstract Cancel: ticket: ExportTicket -> Async<unit>

    /// Download the assembled envelope. `Error status` when the ticket is
    /// not `Ready` (still `Preparing`, `Failed`, `Cancelled`, `Expired`,
    /// or `Unknown`).
    abstract Download: ticket: ExportTicket -> Async<Result<byte[], ExportStatus>>