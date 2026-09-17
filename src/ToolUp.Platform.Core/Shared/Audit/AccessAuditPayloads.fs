// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: asset-store, team-invitation,
// anonymous-session migration, scope / surface / authorization denial and
// host-action payload records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

// ─── Phase 39 — ToolUp.AssetStore companion audit payloads ──────
//
// Image-asset lifecycle events. Source-module label
// `_platform.assets` (reserved). The IAuditLog default impl writes
// every audit event under `_platform.audit`; this constant exists
// for query-time filtering when an external sink (Splunk / Datadog)
// shards by source-module label.
//
// **Alt-text is deliberately excluded from these payloads** —
// treated as user content. The audit trail captures the *fact* of
// the upload (filename, mime, size, hash, profile), not the
// accessibility text the user typed.

module AssetSourceModule =
    /// Reserved `SourceModule` for asset-store lifecycle events.
    /// Forward-looking — current emission rides
    /// `AuditSourceModule.value` via `IAuditLog.Record`. External
    /// sinks filtering on this constant will see asset events when
    /// the IAuditLog impl gains per-event source-module routing.
    [<Literal>]
    let value = "_platform.assets"

/// Emitted by `IAssetStore.Upload` on success. PII-free —
/// `OriginalFilename` is the only user-supplied string and is the
/// uploader's own filename (the data already exists in their
/// scope; the audit echoes it for forensic linkage). Excludes
/// `AltText` and `Caption` deliberately.
type AssetUploadedPayload = {
    /// Actor who performed the upload. Resolved server-side from
    /// `AccessContext.UserId`.
    UserId: string
    /// Asset id minted by the store.
    AssetId: string
    /// SHA-256 of the original bytes (hex, lowercase). Two uploads
    /// of identical bytes share this — operators reconciling
    /// storage footprint with record count grep on this value.
    ContentHash: string
    /// User's original filename. Echoed verbatim from the upload.
    OriginalFilename: string
    /// MIME type sniffed at upload.
    MimeType: string
    /// Size of the original bytes.
    SizeBytes: int64
    /// Derivative profile pinned to the record.
    Profile: string
}

/// Emitted by `IAssetStore.Delete` on a record that existed.
/// Idempotent deletes of unknown ids do NOT emit (the delete
/// returned `Ok` after a no-op — there's no state change to record).
type AssetDeletedPayload = {
    UserId: string
    AssetId: string
    ContentHash: string
}

// ─── Phase 3d — team-invitation audit payloads ───────────────────────
//
// One payload per audit lifecycle event for the team-invitation
// substrate. All five emit under `team-{TeamId}` scope with reserved
// `SourceModule = "_platform.team_invites"` so admin queries filtering
// on the source module surface the invitation trail in isolation.

/// `ITeamInviteApi.IssueInvite` succeeded. Recorded under
/// `team-{TeamId}` scope with `SourceModule = "_platform.team_invites"`.
type TeamInviteIssuedPayload = {
    TeamId: string
    TokenId: string
    InviterUserId: string
    Role: TeamRole
    EmailHint: string option
    ExpiresAt: System.DateTime
    MaxUses: int
}

/// `ITeamInviteApi.AcceptInvite` succeeded — an authenticated visitor
/// was added to the team via a link-redemption flow.
type TeamInviteAcceptedPayload = {
    TeamId: string
    TokenId: string
    InviteeUserId: string
    InviterUserId: string
    Role: TeamRole
}

/// `ScopeResolutionMiddleware` consumed a `PendingInviteByEmail` row
/// on first sign-in matching the email claim — no link redemption was
/// involved. The pending entry is removed atomically with the
/// `AddMember` call.
type TeamInviteAcceptedFromPendingPayload = {
    TeamId: string
    InviteeUserId: string
    InviteeEmail: string
    InviterUserId: string
    Role: TeamRole
}

/// A pending-by-email invitation matched the signed-in user's email
/// claim but the subsequent `ITeamStore.AddMember` call failed (team
/// no longer exists, store glitch, etc.). Distinguishes "silent drop"
/// from "accepted" so an operator inspecting the audit trail can see
/// that the pending entry was consumed but the membership was not
/// applied — and follow up. The pending entry is consumed regardless
/// (single-shot semantics) so this event is the only signal something
/// went wrong.
type TeamInviteAcceptedFromPendingFailedPayload = {
    TeamId: string
    InviteeUserId: string
    InviteeEmail: string
    InviterUserId: string
    Role: TeamRole
    Reason: string
}

/// `ITeamInviteApi.RevokeInvite` succeeded. Subsequent acceptance
/// attempts against the same token return `RevokedToken`.
type TeamInviteRevokedPayload = {
    TeamId: string
    TokenId: string
    ActorUserId: string
}

/// `IShareTokenStore.MarkUsed` succeeded on a team-invite token —
/// emitted alongside `TeamInviteAccepted` for symmetry with the
/// existing `ShareTokenUsed` substrate event. The pair lets a
/// monitoring sink distinguish "invitee successfully joined the team"
/// (`TeamInviteAccepted`) from "the token's use-count was bumped"
/// (`TeamInviteRedeemed`).
type TeamInviteRedeemedPayload = {
    TeamId: string
    TokenId: string
    RemainingUses: int
}

/// Phase 547 — an email-keyed pending invite expired unconsumed and was
/// swept from `IPendingInviteStore`. Emitted once per dropped entry by
/// every sweep site in the store impl (`SweepExpired`, the opportunistic
/// compaction inside `Upsert`, and the expired-branch of
/// `TryConsumeForEmail`), recorded under the `team-{TeamId}` scope.
/// Without it the sweep is silent — the invitee ends up in neither the
/// Members panel nor Pending Invites and nobody is told, so the
/// operator's mental model diverges permanently from system state
/// (GP 6, "audit the silent path"). PII envelope matches the sibling
/// `TeamInviteAcceptedFromPending*` events, which already carry the
/// invitee email verbatim for this team-scoped, admin-only trail.
type TeamInviteExpiredPayload = {
    TeamId: string
    /// Lower-cased email the invite was keyed on (the store's map key).
    InviteeEmail: string
    /// Inviter captured at issue time — the natural recipient of the
    /// optional expiry notification (Phase 547.C).
    InviterUserId: string
    /// Role the invitee would have been granted had they signed in in
    /// time.
    Role: TeamRole
    /// When the invite was issued. `DateTime.MinValue` for entries
    /// persisted before Phase 547 added `PendingInviteByEmail.IssuedAt`
    /// (the record decodes leniently — a missing field defaults rather
    /// than quarantining the blob).
    IssuedAt: System.DateTime
    /// The entry's `ExpiresAt` — the instant it became unconsumable.
    ExpiredAt: System.DateTime
}

/// Phase 66 Stream C.1 (continuation) — an `IAnonymousSessionMigrator`
/// ran on the first authenticated request following an anonymous
/// session in the same browser. Emitted by
/// `AnonymousSessionMigrationMiddleware` on `Ok`, `PartialFailure`, and
/// `InfrastructureFailed` (the benign `NotEligible` outcome is not
/// audited — it fires on every unwired / no-data deployment and would
/// drown the trail). `Outcome` is the machine-readable discriminator
/// (`"ok"` / `"partial_failure"` / `"infrastructure_failed"`); the
/// migrated-volume counts come from the `MigrationSummary` (zero for
/// the infrastructure-failure case where nothing landed). `FailedItems`
/// + `Error` carry the partial / infrastructure diagnostic so a
/// runbook can correlate a stuck migration without re-deriving from
/// logs. PII-free: `AnonymousSessionId` is an opaque session token and
/// `TargetUserId` matches the convention of the existing
/// `UserLoggedInPayload`. Reserved `SourceModule = "_platform.subject"`.
type AnonymousSessionMigratedPayload = {
    AnonymousSessionId: string
    TargetUserId: string
    Outcome: string
    ItemsMigrated: int
    BytesMigrated: int64
    Modules: string list
    FailedItems: int
    Error: string option
    OccurredAt: DateTimeOffset
}

/// Auth-observability phase A — `ScopeResolutionMiddleware` infrastructure
/// failure. Fires when the catch-all in `ScopeResolutionMiddleware` traps
/// an exception (DI resolution failure, cache failure, store throw) and
/// the request falls through to anonymous-subject behaviour. Pre-A1 the
/// catch was silent; operators had no signal to distinguish "no
/// credentials" from "resolver crashed."
///
/// Reserved `SourceModule = "_platform.auth"`. `ScopeId` for the
/// emission is the literal `"_platform"` (the scope is unresolvable —
/// that's precisely the failure we're recording).
type ScopeResolutionFailedPayload = {
    /// Request method (`GET` / `POST` / …) at the moment of failure.
    Method: string
    /// Request path (unparameterised — operators read this when triaging,
    /// PII risk is low for `/api/*` shapes).
    Path: string
    /// Concrete .NET exception type name (`NullReferenceException`,
    /// `SocketException`, etc.). Operators correlate spikes to infra.
    ExceptionKind: string
    /// Human-readable message from the exception. Bounded — middleware
    /// truncates to 512 chars to keep audit-row size predictable.
    Message: string
    /// Correlation id (read from `CallContext.correlationId()` if set,
    /// otherwise `None`). Stitches this event to the request log + the
    /// client-side trace.
    CorrelationId: string option
    OccurredAt: DateTimeOffset
}

/// Auth-observability phase A — `SurfaceEnforcementMiddleware` denial.
/// Fires on every `writeRejection` so operator dashboards see denial
/// rates (rate-limit on a scripted enumeration, recent surface-config
/// changes that flipped legit calls to denied). Pre-A2 these were
/// completely silent — the middleware wrote a 401/403 JSON body but
/// emitted no audit.
///
/// Reserved `SourceModule = "_platform.auth"`. `ScopeId` is `_platform`
/// (denials are deployment-wide observability, not tenant-scoped).
type SurfaceDeniedPayload = {
    /// Request method.
    Method: string
    /// Request path. **Unparameterised path** today; future evolution
    /// will template (`/api/teams/{id}/members`) once the route registry
    /// exposes the template. For now operators query on path prefix.
    Path: string
    /// `Subject` kind at denial time (`anonymous` / `user` / `team` /
    /// `claim`). The full subject is available in `SubjectId` when not
    /// anonymous.
    SubjectKind: string
    /// Stable identifier of the denied subject. `None` for anonymous.
    SubjectId: string option
    /// Machine-readable denial code (`authentication_required` /
    /// `team_required` / `claim_bearer_not_admitted` / etc.) — matches
    /// the `error` field in the response body.
    DenialCode: string
    /// Optional caller-visible hint that accompanies the denial code
    /// (`select_team` etc.).
    Hint: string option
    /// Correlation id (set by `installRequestSeam` client-side or by the
    /// dispatcher's per-request generator).
    CorrelationId: string option
    OccurredAt: DateTimeOffset
}

/// Phase 120 — the uniform structured authorization-denial row written by
/// `IAuthAuditHook` across every denial class on the HTTP surface
/// (surface-enforcement / RBAC role / share-token / SSE-identity /
/// module-permission / KB-destructive). Generalises the write side of the
/// AI tool-allowlist denial stream (Phase 45) to the whole auth surface,
/// so operators get one queryable trail keyed by route/requirement/scope
/// instead of scattered per-subsystem metrics and log lines (GP 6).
///
/// Reserved `SourceModule = AuditSourceModule.value`; written under the
/// caller's scope when known (so the `/dev/auth-denials` rollup is
/// caller-scope-only, GP 4) and under `_platform` for scope-less
/// (anonymous / pre-scope) denials.
///
/// PII envelope: nothing beyond the sanitised `SubjectId` (the same id the
/// `SurfaceDenied` row already carries) — no request bodies, no headers.
type AuthorizationDeniedPayload = {
    /// Request route the denial fired on (method + path, unparameterised).
    Route: string
    /// Requirement class that was not satisfied — one of `"surface"` /
    /// `"role"` / `"share-token"` / `"sse-identity"` /
    /// `"module-permission"` / `"kb-destructive"`. String at the wire
    /// boundary so the `AuthDenialRequirement` DU can evolve without
    /// breaking persisted rows.
    Requirement: string
    /// Subject kind at denial time (`anonymous` / `user` / `team` /
    /// `claim`). Full subject id is in `SubjectId` when not anonymous.
    SubjectKind: string
    /// Stable identifier of the denied subject. `None` for anonymous.
    SubjectId: string option
    /// Machine-readable verdict / denial code (e.g. `team_required`,
    /// `revoked_token`, `use_limit_exceeded`, `claim_bearer_not_admitted`).
    Verdict: string
    /// Human-readable reason. Bounded + sanitised — carries no PII beyond
    /// `SubjectId`.
    Reason: string
    /// Scope the denial occurred in (`team-…` / `user-…` / claim scope).
    /// `None` when there was no caller scope (anonymous / pre-scope).
    ScopeId: string option
    /// Correlation id stitching this row to the request log + client trace.
    CorrelationId: string option
    /// Phase 120 flood guard — how many denials this single row represents.
    /// `1` for a leading-edge row; `> 1` for a window-rollover summary that
    /// coalesced a probing burst on the same `(route, subject)` key, so a
    /// scripted enumeration yields bounded audit volume with an accurate
    /// total rather than one row per probe.
    DedupCount: int
    OccurredAt: DateTimeOffset
}

/// Phase 272 — a hosted-tree action was authorized (or denied) through the
/// host-neutral action seam (Phase 113 `IActionAuthorizer`). GP 6 mandates
/// "audit everything that changes state" — an action a user drove through a
/// hosted UI must be traceable for the regulated / Sovereign buyers in the
/// Vision (provenance is the moat). Keyed on the neutral `ActionDescriptor`
/// (kind / target / scope) + the decision; NO tree-language type appears
/// (open-core boundary). PII-free beyond `SubjectId` — same envelope as the
/// Phase 120 `AuthorizationDenied` row. A DENIED action is audited too (the
/// security-relevant case).
type HostActionDispatchedPayload = {
    /// Subject kind at dispatch (`anonymous` / `user` / `team` / `claim`).
    SubjectKind: string
    /// Stable subject id; `None` for anonymous (no PII).
    SubjectId: string option
    /// `ActionDescriptor.Kind` — the action space (`dispatch` / `call` /
    /// `navigate` / `notify` / `invoke` / host-defined).
    ActionKind: string
    /// `ActionDescriptor.Target` — the specific action within the kind (a
    /// message case name, a Remoting method, a route, a capability id).
    ActionTarget: string
    /// Scope the action targeted (`ActionDescriptor.Scope`); `None` when the
    /// action carried no explicit scope.
    ScopeId: string option
    /// `true` when the authorizer granted the action; `false` for a denial.
    Allowed: bool
    /// Human-readable decision reason — the authorizer's `Deny` reason, or a
    /// fixed `"allowed"` marker on the grant path. Bounded + PII-free beyond
    /// `SubjectId`.
    Reason: string
    OccurredAt: DateTimeOffset
}