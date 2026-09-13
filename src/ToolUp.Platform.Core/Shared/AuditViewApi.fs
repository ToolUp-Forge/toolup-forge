// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 529 — audit-trail viewer surface ──────────────────────────
//
// The audit substrate is deep (the `AuditEvent` DU, external
// `IAuditSink` companions, evidence packs) and, until this phase, had
// no in-app read surface: every peer subsystem got one
// (`HealthMonitorUI`, `WebhookAdminUI`, `UsageDashboard`,
// `DataIngestionUI`) and the trail itself did not. `IAuditViewApi` is
// that surface — read-only, Owner/Admin-gated, caller-scope only.
//
// **Why the row type is a projection and not `AuditEvent` itself.**
// The obvious wire shape is the DU: it is declared in
// `Platform.Core/Shared`, so the Fable client can already see it. Two
// reasons it is not used here.
//
//   1. `IAuditLog.GetAuditTrail` returns `AuditEvent list` and DROPS
//      the persisted envelope — `ModuleEvent.Id` and
//      `ModuleEvent.OccurredAt` are not on the DU. A table with a time
//      column and a stable pagination cursor needs both, and only a
//      minority of payload cases carry a timestamp of their own. The
//      viewer therefore reads the envelope, and the envelope's fields
//      are what this record makes first-class.
//   2. The DU is append-only and long (~190 cases). Serialising it
//      whole would make every client build a total decoder over it, so
//      a client one release behind its server would fail to decode a
//      page containing a single new case — the audit viewer being the
//      one surface that must keep working during an incident. The
//      persisted payload rides across as its stored JSON instead
//      (`Payload`), which an older client renders generically rather
//      than failing on.
//
// **Scope.** Every method reads the caller's own resolved scope from
// `AccessContext`; no method takes a scope from the wire (GP 4). See
// `AuditViewApiHandler` for the resolution and the role gate.

/// One row of the audit trail as the viewer renders it: the persisted
/// `ModuleEvent` envelope plus two best-effort projections over its
/// payload.
type AuditEventView = {
    /// The persisted event id. Stable, and the tiebreak half of the
    /// pagination cursor.
    Id: Guid
    /// Envelope timestamp (UTC), authoritative for the time column and
    /// the ordering. Not the payload's own `OccurredAt` — most cases
    /// have none, and where both exist they are the same instant.
    OccurredAt: DateTime
    /// `AuditEvent.eventTypeName` of the recorded case, as persisted.
    /// Note the three Phase 625 `ModuleArtefact*` cases persist their
    /// historical `Artifact*` discriminator; the viewer shows what is
    /// stored rather than re-deriving a display name.
    EventType: string
    /// Best-effort actor attribution, probed out of the payload by
    /// field name (`AuditViewProjection.actorFieldNames`). `None` when
    /// the payload names no actor — which is honest: the persisted
    /// audit payload does NOT carry the resolved `AuditSubject` (that
    /// rides the sink-side `AuditEnvelope` only), so there is no field
    /// this could read for every case.
    Actor: string option
    /// One-line rendering of the payload's scalar fields, bounded in
    /// length. A convenience for the table row — the detail expansion
    /// renders `Payload`.
    Summary: string
    /// The persisted payload JSON exactly as stored. Rendered by the
    /// detail expansion; carried verbatim into the CSV export so an
    /// export is a faithful copy of the trail rather than a lossy
    /// summary of it.
    Payload: string
}

/// The two payload projections `AuditEventView` carries, declared here
/// rather than inside the handler because they are part of what a
/// client may rely on: the field names an `Actor` can come from, and
/// the bound on `Summary`'s length. Pure data — no JSON parsing, so it
/// ships in the Fable-packed source alongside the rest of this file.
module AuditViewProjection =
    /// Payload field names probed, in order, for the row's actor. The
    /// first present, non-blank string wins.
    ///
    /// A LIST rather than a per-case match because the persisted
    /// payload does not carry the resolved subject (see
    /// `AuditEventView.Actor`), and a ~190-case projection over payload
    /// records that mostly name their actor with one of these handful
    /// of field names would be a second copy of the DU that goes stale
    /// silently — a case added without a line here would show a blank
    /// actor either way, and the exhaustive version would additionally
    /// have to be edited to keep compiling.
    let actorFieldNames = [
        "Actor"
        "ActorId"
        "ActorUserId"
        "ChangedBy"
        "PerformedBy"
        "RequestedBy"
        "Grantor"
        "IssuedBy"
        "SubjectId"
        "UserId"
    ]

    /// Maximum rendered length of `AuditEventView.Summary`, including
    /// the ellipsis. The detail expansion renders the full payload, so
    /// the row summary is deliberately a glance rather than a record.
    [<Literal>]
    let SummaryMaxLength = 160

    /// How many of the payload's scalar fields the summary renders
    /// before it stops. Bounds the cost on the wide payload cases.
    [<Literal>]
    let SummaryMaxFields = 6

/// Filter + paging request. Every field narrows; a query with every
/// field at its default reads the newest page of the whole trail.
type AuditTrailQuery = {
    /// Inclusive lower bound on `OccurredAt` (UTC).
    [<PiiSafe>]
    From: DateTime option
    /// Inclusive upper bound on `OccurredAt` (UTC).
    [<PiiSafe>]
    To: DateTime option
    /// Exact `EventType` match. `None` = every kind.
    [<PiiSafe>]
    EventType: string option
    /// Case-insensitive substring match on the projected `Actor`. Rows
    /// with no projected actor never match a non-`None` filter.
    /// Deliberately NOT `PiiSafe`: it is a user identifier the caller
    /// typed, and the export's own audit row does not need it.
    Actor: string option
    /// Opaque continuation token from a previous page's `NextCursor`.
    /// Server-minted; a token the server cannot read is refused rather
    /// than silently treated as "start from the top", because a
    /// silently-restarted page reads as a complete result.
    Cursor: string option
    /// Requested page size. Clamped server-side to
    /// `AuditViewApi.MaxPageSize`; a non-positive value takes
    /// `AuditViewApi.DefaultPageSize`.
    [<PiiSafe>]
    PageSize: int
}

/// One page of the filtered trail.
type AuditEventPage = {
    /// The page, newest first.
    Events: AuditEventView list
    /// Token for the next (older) page, or `None` at the end of the
    /// trail. Opaque — clients pass it back unchanged.
    NextCursor: string option
    /// How many rows the filter matched in total, across every page.
    /// Lets the table render "1–50 of 312" without walking the trail
    /// client-side.
    MatchedCount: int
}

/// Read-only admin surface over the deployment's audit trail.
///
/// **Role gate.** Owner/Admin (`TeamRoles.canWriteTeamConfig`) in team
/// mode; any authenticated user in the single-scope modes, where the
/// caller owns the scope they are reading. Anonymous callers are
/// refused outright — an audit trail is a map of who did what, and a
/// deployment with no role concept has nobody to whom it may safely be
/// shown.
///
/// **Substrate.** Reads the rows `IAuditLog` writes. A deployment on
/// the default `ServerConfig.AuditLog = NoAuditLog` records nothing, so
/// every method here returns an empty result and the module renders its
/// empty state (the same pairing `IUsageQueryApi` has with
/// `ServerConfig.UsageMetering`).
type IAuditViewApi = {
    /// One page of the filtered trail for the caller's scope.
    [<RequiresClaim "scope">]
    Query: AuditTrailQuery -> Async<Result<AuditEventPage, string>>

    /// The distinct `EventType` values present in the caller's scope,
    /// ascending. The filter bar's options — derived from the trail
    /// rather than from the DU, so it offers only kinds this deployment
    /// has actually recorded instead of ~190 mostly-empty choices.
    [<RequiresClaim "scope">]
    ListEventTypes: unit -> Async<Result<string list, string>>

    /// CSV export of the whole filtered window (not just the current
    /// page); `Cursor` and `PageSize` are ignored. Returns raw bytes so
    /// the client can trigger the download without a second round trip,
    /// exactly as `IUsageQueryApi.ExportCsv` does.
    ///
    /// `[<Audit "DataExported">]` is what makes the trail record its own
    /// reads-for-export: the remoting dispatcher's audit interceptor
    /// (`Api.make` composes it whenever a record carries `[<Audit>]`)
    /// writes a `RemotingMethodAudited` row through the same `IAuditLog`
    /// the export just read. No bespoke emission here — an export that
    /// audited itself by hand could be forgotten; an attribute the
    /// dispatcher enforces cannot.
    [<RequiresClaim "scope">]
    [<Audit "DataExported">]
    ExportCsv: AuditTrailQuery -> Async<Result<byte[], string>>
}

module AuditViewApi =
    /// Server-side ceiling on a page. The trail is read whole from the
    /// event store before it is filtered, so this bounds what crosses
    /// the wire and what the browser renders, not what the store reads.
    [<Literal>]
    let MaxPageSize = 200

    /// Page size applied when the caller asks for a non-positive one.
    [<Literal>]
    let DefaultPageSize = 50

    /// Clamp a requested page size into `1 .. MaxPageSize`.
    let clampPageSize (requested: int) : int =
        if requested <= 0 then DefaultPageSize
        elif requested > MaxPageSize then MaxPageSize
        else requested

    /// The unfiltered newest page — the query the module issues on
    /// first render, and the base every filter change modifies.
    let defaultQuery: AuditTrailQuery = {
        From = None
        To = None
        EventType = None
        Actor = None
        Cursor = None
        PageSize = DefaultPageSize
    }

    /// ToolUp.Remoting route builder. Mirrors `UsageQueryApi`'s
    /// `/api/_platform/<area>/<method>` shape so the platform's admin
    /// surfaces are discoverable by path alone.
    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "/api/_platform/audit/%s" methodName