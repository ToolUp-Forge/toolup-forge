// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataSubjectRequestApi

open System
open ToolUp.Platform

// ─── Phase 9h — IDataSubjectRequestApi (Fable.Remoting contract) ────
//
// Admin-facing API. Owner / Admin gated at the handler. Two-phase
// erasure (preview → confirm) so the admin can review what will be
// affected before mutation. Export streams every registered
// exporter's segments concatenated (deterministic ordering) as the
// response body's archive.
//
// Contract lives in Core so the client-tier `DataSubjectRequestAdminUI`
// module references the same record shape the server registers via
// `Fable.Remoting`. The server-side handler factory (which depends on
// `IDataExporter` / `IErasureHandler` extension points) stays in
// `ToolUp.Platform.Server\Server\DataSubjectRequestApiHandler.fs`.

/// Input shape for `RequestExport`. Carries the subject + the admin
/// reason; the scope is resolved from the caller's session by the
/// handler.
type ExportRequestInput = {
    [<PiiSafe>]
    SubjectUserId: string
    [<PiiSafe>]
    TeamId: string option
    Reason: string
}

/// Input shape for `RequestErasure`. `OverridePolicy` lets the admin
/// pick a different policy than the deployment default for this
/// specific request (typically when the deployment runs `Tombstone`
/// by default but a verified GDPR Article 17 demand requires
/// `HardDelete`).
type ErasureRequestInput = {
    [<PiiSafe>]
    SubjectUserId: string
    [<PiiSafe>]
    TeamId: string option
    Reason: string
    [<PiiSafe>]
    OverridePolicy: ErasurePolicy option
}

/// Aggregate outcome of one Erase pipeline run. Indexed per-handler so
/// the admin UI can render which stores succeeded, which refused (e.g.
/// audit-retention regimes), and which failed transiently. Shape is
/// Fable-compatible (the orchestrator function-side definition in
/// `ErasurePipeline.fs` returns this same record).
type ErasureRunSummary = {
    Request: DataSubjectRequest
    StartedAt: DateTimeOffset
    CompletedAt: DateTimeOffset
    /// Per-handler outcome — either an ErasureSummary or an
    /// ErasureError. Indexed by handler name for stable rendering.
    PerHandler: Map<string, Result<ErasureSummary, ErasureError>>
    /// True iff every handler succeeded. Partial failures and
    /// refusals both flip this to false.
    OverallSuccess: bool
}

/// Outcome returned by the API when an erasure preview completes.
/// Carries the request id so the admin can confirm against the
/// preview's snapshot.
type ErasurePreview = {
    Request: DataSubjectRequest
    PerHandlerCounts: Map<string, ErasureSummary>
}

/// Outcome returned by the API when an erasure execution completes
/// (or fails partially).
type ErasureRunResult =
    | Completed of ErasureRunSummary
    | NotImplemented of detail: string
    | Refused of detail: string

/// Phase 9h.A — notification contract for background-export progress.
/// The `DSRExportJobHandler` publishes a `CustomNotification` under this
/// key on every ticket state transition (Preparing → Ready / Failed /
/// Cancelled) via `INotificationChannel`; an SSE consumer or the admin
/// UI can observe ticket progress without polling. The payload is a JSON
/// object `{ "ticket": "<ticket>", "status": "<ExportStatus.name>" }`.
///
/// The key is a Core literal (not a `Notification` builder) because the
/// `Notification` DU is compiled after this file; the publish sites
/// (server-only) construct `CustomNotification(ExportProgressKey, json)`
/// directly.
module DsrNotifications =
    [<Literal>]
    let ExportProgressKey = "_sdk.DataSubjectRequests.ExportProgress"

/// Fable.Remoting contract surface — every method returns
/// `Async<Result<_, string>>` per the SDK convention. **Platform-Admin
/// only**: every method carries `[<RequiresRole "PlatformAdmin">]` (the
/// classifier rejects non-admins at dispatch) AND
/// `DataSubjectRequestApiHandler.create` re-checks
/// `AccessContext.canModifyPlatformConfig` in-handler (defence in depth —
/// the gate does not depend solely on the deployment's auth middleware).
/// Phase 229 closed the prior `[<AllowAnonymous>]` gap, where export and
/// erasure of any subject's data by id were reachable by any caller the
/// deployment surface admitted.
type IDataSubjectRequestApi = {
    /// Stream every record across every registered exporter that
    /// names the subject. Returns a single byte payload (the
    /// orchestrator's archive shape; downstream handler decides
    /// zip / tar / raw concatenation based on byte budget). For
    /// the MVP the payload is a JSON envelope `{ segments: [...] }`
    /// where each segment carries name / mimeType / base64 bytes.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "DataExported">]
    RequestExport: ExportRequestInput -> Async<Result<byte[], string>>

    /// Phase 1 of an erasure — preview only. Returns the affected-
    /// record counts per handler. No mutation.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "PiiAccessed">]
    PreviewErasure: ErasureRequestInput -> Async<Result<ErasurePreview, string>>

    /// Phase 2 of an erasure — confirm a previously-previewed
    /// request and execute. Caller passes the preview's
    /// `Request.Id` so the handler can correlate.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "Custom:DataErased">]
    ConfirmErasure: DataSubjectRequestId -> Async<Result<ErasureRunResult, string>>

    /// Phase 9h.A — background-job export. Returns an `ExportTicket`
    /// immediately (no envelope assembly on the request thread); the
    /// export runs on `IJobScheduler` and the client polls
    /// `GetExportStatus` then `DownloadExport`. Returns `Error` when the
    /// deployment did not enable async DSR
    /// (`DataSubjectRequestConfig.Async = false`) or no
    /// `IJobScheduler` / `IBackgroundExportStore` is composed.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "DataExported">]
    RequestExportAsync: ExportRequestInput -> Async<Result<ExportTicket, string>>

    /// Phase 9h.A — poll a background export ticket's status.
    /// `Preparing` while the job runs; `Ready sizeBytes` when the
    /// envelope is downloadable; `Failed` / `Cancelled` / `Expired` /
    /// `Unknown` terminally.
    [<RequiresRole "PlatformAdmin">]
    GetExportStatus: ExportTicket -> Async<Result<ExportStatus, string>>

    /// Phase 9h.A — download a `Ready` background-export envelope. The
    /// payload is byte-identical to the synchronous `RequestExport`
    /// shape. `Error` carries the non-`Ready` status name when the
    /// ticket isn't downloadable yet.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "DataExported">]
    DownloadExport: ExportTicket -> Async<Result<byte[], string>>

    /// Phase 9h.A — cancel an in-flight background export. Flips the
    /// ticket to `Cancelled`; the background job observes the cancelled
    /// ticket before `Complete` and skips writing the envelope, so the
    /// system stays consistent (the partial envelope TTL-expires).
    [<RequiresRole "PlatformAdmin">]
    CancelExport: ExportTicket -> Async<Result<unit, string>>
}