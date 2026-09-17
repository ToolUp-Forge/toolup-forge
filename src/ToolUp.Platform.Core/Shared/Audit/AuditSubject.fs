// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: the reserved audit source-module
// name and the `AuditSubject` attribution DU + module every payload and
// envelope below builds on. Same namespace, same names — only the file
// boundary moved.

// ─── Audit event types ───────────────────────────────────────────
//
// SDK-emitted audit events for compliance and incident response.
// Every state-changing operation — login, team CRUD, file ops,
// permission changes — is recorded as an `AuditEvent` via
// `IAuditLog`. Persistence reuses the existing `IEventStore`
// surface: each `AuditEvent` is serialised to a `ModuleEvent` with
// `SourceModule = AuditSourceModule` so audit events flow through the
// same blob layout, retention policy, and webhook hooks as every other
// platform event. The DU is the typed F# surface; the wire format is
// `ModuleEvent`.

/// Reserved `SourceModule` for audit events. Filtering `IEventStore`
/// reads on this constant returns the audit trail only — `ReadBySource`
/// is the canonical query path.
module AuditSourceModule =
    /// The reserved source-module name every `IAuditLog.Record` write
    /// carries. Read it back with `IEventStore.ReadBySource` to get the
    /// audit trail and nothing else; write it only through the seam —
    /// a `ModuleEvent` hand-built on this name bypasses the codec, the
    /// failure policy and (Phase 759) the replay scope.
    [<Literal>]
    let value = "_platform.audit"

/// Phase 66 Stream B.7 (design §3.6 + D15) — who/what an audit event
/// is attributed to. Maps 1:1 to the four `Subject` constructors but
/// shaped for the audit-side serialised form: `ClaimAudit` flattens the
/// `ShareTokenClaim` to the fields downstream sinks actually query on
/// (`tokenId`, `attributedHandle`, `resourceKind`, `resourceId`) so
/// Splunk / Datadog / S3-Archive read structured fields rather than
/// parsing a nested claim record. The four cases are exhaustive across
/// every `Subject` shape; new subject kinds force a new `AuditSubject`
/// case via the [[d15]] contract.
type AuditSubject =
    /// Unauthenticated session-scoped subject. `sessionId` mirrors the
    /// `Subject.AnonymousSession sid` value (the `X-User-Id` cookie or
    /// a freshly-minted GUID when the request did not carry one).
    | AnonymousAudit of sessionId: string
    /// Authenticated user without an active team scope. Covers both the
    /// `Subject.AuthenticatedUser` shape and the dispatcher-derived
    /// "scope is `_platform` so attribute to a system-pseudo-user" path
    /// — the `userId` field carries the sentinel in the latter case.
    | UserAudit of userId: string
    /// Authenticated user acting within a team scope. Both fields
    /// populated when the originating `Subject` was `TeamMember`. When
    /// the dispatcher derives the envelope from `ScopeId` alone (e.g.
    /// because the originating `AccessContext.Subject` was not persisted
    /// alongside the `ModuleEvent`), `userId` is the literal
    /// `"_dispatcher"` sentinel — sinks read this as "team event with
    /// unknown actor" rather than "team event by user named '_dispatcher'".
    | TeamAudit of userId: string * teamId: string
    /// Anonymous reach into a persistent scope gated by a validated
    /// `ShareTokenClaim`. The flattened shape (vs the nested
    /// `ShareTokenClaim` record carried on `Subject.ClaimBearer`) is
    /// deliberate — sinks query on these four fields directly.
    | ClaimAudit of tokenId: string * attributedHandle: string option * resourceKind: string * resourceId: string

// `AuditSubjectKind` is defined in `Shared/Types/AuditSampling.fs`
// (compiled before SDK.Shared.fs so `ServerConfig` can carry an
// `AuditSamplingPolicy`). The `kind` / `kindString` projections below
// reference it from that earlier file.

module AuditSubject =
    /// Project an `AuditSubject` to its lightweight kind tag. Used by
    /// sinks to emit a `subject_kind` tag (Datadog / OpenTelemetry) or a
    /// `_meta.kind` envelope field (Splunk HEC) without inspecting the
    /// payload.
    let kind =
        function
        | AnonymousAudit _ -> AnonymousAuditKind
        | UserAudit _ -> UserAuditKind
        | TeamAudit _ -> TeamAuditKind
        | ClaimAudit _ -> ClaimAuditKind

    /// String form of `kind` — what sinks actually emit as a tag value.
    /// Stable across the audit-schema-version envelope bump; do not
    /// rename without bumping `LatestAuditSchemaVersion`.
    let kindString =
        function
        | AnonymousAuditKind -> "anonymous"
        | UserAuditKind -> "user"
        | TeamAuditKind -> "team"
        | ClaimAuditKind -> "claim"

    /// Construct an `AuditSubject` from the request-side `Subject`. The
    /// canonical bridge — every audit emission site that has a resolved
    /// `AccessContext.Subject` should use this rather than re-derive.
    let fromSubject (subject: Subject) : AuditSubject =
        match subject with
        | AnonymousSession sid -> AnonymousAudit sid
        | AuthenticatedUser uid -> UserAudit uid
        | TeamMember(uid, tid) -> TeamAudit(uid, tid)
        | ClaimBearer claim -> ClaimAudit(claim.TokenId, claim.AttributedHandle, claim.ResourceKind, claim.ResourceId)

    /// Project a `Subject` to the `(kind, id)` pair an audit ROW carries —
    /// the only subject information that reaches a flat payload, and no
    /// PII beyond the id. `None` for an anonymous session: the session id
    /// is a cookie value, not an identity, and a row asserting it as one
    /// would be worse than a row that says "anonymous".
    ///
    /// Phase 739 promoted this out of `AuthAuditHook`, where it was
    /// `internal` and therefore unreachable from the companions. It is
    /// here rather than duplicated because both halves of an
    /// authorization trail must name a subject the SAME way or they do
    /// not join: `AuthorizationDenied` and `MediaKeyDelivered` are the
    /// refusal and the grant for one endpoint, and a reviewer asking "who
    /// reached this media" has to be able to union them on one key. A
    /// second spelling at the second site is the re-declared-literal
    /// defect Phase 730 recorded — it drifts with no compile error.
    let sanitise (subject: Subject) : string * string option =
        match subject with
        | AnonymousSession _ -> "anonymous", None
        | AuthenticatedUser uid -> "user", Some uid
        | TeamMember(uid, _) -> "team", Some uid
        | ClaimBearer claim -> "claim", Some claim.TokenId

    /// Sentinel `userId` for `TeamAudit` cases derived from `ScopeId`
    /// alone — see `fromScopeId`. Sinks treat this value as "team event
    /// with unknown actor".
    [<Literal>]
    let DispatcherSentinelUserId = "_dispatcher"

    /// Sentinel `userId` for `UserAudit` cases derived from the
    /// reserved `_platform` scope id — system-level events with no
    /// per-user attribution at the dispatcher layer.
    [<Literal>]
    let PlatformSentinelUserId = "_platform"

    /// Phase 66 Stream B.7 — derive a best-effort `AuditSubject` from
    /// the `IEventStore` `ScopeId` alone. Used by the audit-replicator
    /// dispatch path, which decodes the persisted `ModuleEvent` back
    /// into an `AuditEvent` without access to the originating
    /// `AccessContext.Subject` (today's `ModuleEvent` envelope does not
    /// persist the subject — Phase 66 follow-on substrate could extend
    /// the wire format; until then, the dispatcher carries this
    /// approximation).
    ///
    /// Mapping:
    /// - `_platform` → `UserAudit "_platform"` (system-level event).
    /// - `session-{sid}` → `AnonymousAudit sid`.
    /// - `user-{uid}` → `UserAudit uid`.
    /// - `team-{tid}` → `TeamAudit ("_dispatcher", tid)`.
    /// - Anything else → `UserAudit scopeId` as a last-resort
    ///   fallback so the envelope always has a subject (downstream
    ///   sinks can tag-route on the unprefixed scope id).
    let fromScopeId (scopeId: string) : AuditSubject =
        if System.String.IsNullOrWhiteSpace scopeId then
            UserAudit PlatformSentinelUserId
        elif scopeId = "_platform" then
            UserAudit PlatformSentinelUserId
        elif scopeId.StartsWith("session-", System.StringComparison.Ordinal) then
            AnonymousAudit(scopeId.Substring 8)
        elif scopeId.StartsWith("user-", System.StringComparison.Ordinal) then
            UserAudit(scopeId.Substring 5)
        elif scopeId.StartsWith("team-", System.StringComparison.Ordinal) then
            TeamAudit(DispatcherSentinelUserId, scopeId.Substring 5)
        else
            UserAudit scopeId

// `AuditSamplingPolicy` (Phase 66 Stream C.2) is defined in
// `Shared/Types/AuditSampling.fs`, compiled before SDK.Shared.fs so
// `ServerConfig` can carry it as a field.