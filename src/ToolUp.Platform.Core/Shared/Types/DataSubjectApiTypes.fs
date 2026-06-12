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

/// Fable.Remoting contract surface — every method returns
/// `Async<Result<_, string>>` per the SDK convention. Owner / Admin
/// gated upstream.
/// Annotation note (69d.tail): neither `DataSubjectRequestApiHandler.create`
/// nor the compose mount applies an in-handler role gate today — the only
/// guard is the deployment's auth middleware, so the honest classification
/// is `AllowAnonymous` (handler behaviour unchanged; see gap flag).
type IDataSubjectRequestApi = {
    /// Stream every record across every registered exporter that
    /// names the subject. Returns a single byte payload (the
    /// orchestrator's archive shape; downstream handler decides
    /// zip / tar / raw concatenation based on byte budget). For
    /// the MVP the payload is a JSON envelope `{ segments: [...] }`
    /// where each segment carries name / mimeType / base64 bytes.
    [<AllowAnonymous>]
    [<Audit "DataExported">]
    RequestExport: ExportRequestInput -> Async<Result<byte[], string>>

    /// Phase 1 of an erasure — preview only. Returns the affected-
    /// record counts per handler. No mutation.
    [<AllowAnonymous>]
    [<Audit "PiiAccessed">]
    PreviewErasure: ErasureRequestInput -> Async<Result<ErasurePreview, string>>

    /// Phase 2 of an erasure — confirm a previously-previewed
    /// request and execute. Caller passes the preview's
    /// `Request.Id` so the handler can correlate.
    [<AllowAnonymous>]
    [<Audit "Custom:DataErased">]
    ConfirmErasure: DataSubjectRequestId -> Async<Result<ErasureRunResult, string>>
}