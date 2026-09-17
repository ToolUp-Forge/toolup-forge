// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: tenant-lifecycle, offboard
// confirmation / scheduling, remoting-method and passkey payload records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

// ─── Phase 54 — tenant-lifecycle substrate audit payloads ──────────────
//
// Emitted by `TenantLifecycleAggregator` for the
// provision / deprovision choreography. Reserved
// `SourceModule = "_platform.tenant"`. Metadata-only by design: scope
// id, actor, per-phase hook counts + elapsed — never tenant data, never
// the bytes any hook erased. `TenantDeprovisioned` is the single
// end-of-offboard marker the phase exists to provide; per-hook failures
// surface as `TenantLifecycleHookFailed` rows without aborting the run.

module TenantLifecycleSourceModule =
    /// Reserved `SourceModule` for `ITenantLifecycle` audit events.
    /// Filter `IEventStore.ReadBySource` on this constant for the
    /// tenant-lifecycle audit trail.
    [<Literal>]
    let value = "_platform.tenant"

/// A tenant scope finished provisioning: every registered
/// `ITenantLifecycle.OnProvisioned` hook ran. Counts only — the per-hook
/// disposition lives on the returned `LifecycleSummary`; this row is the
/// durable "provisioning completed" marker.
type TenantProvisionedPayload = {
    /// Tenant scope provisioned.
    ScopeId: string
    /// Operator (Owner / Platform-Admin) who triggered provisioning.
    Actor: string
    /// Total hooks dispatched.
    HooksRun: int
    /// Hooks that returned `Completed`.
    HooksCompleted: int
    /// Hooks that returned `Skipped` (substrate inactive).
    HooksSkipped: int
    /// Hooks that returned `Failed` (run continued regardless).
    HooksFailed: int
    /// Wall-clock for the aggregated run, in milliseconds.
    ElapsedMs: int64
}

/// A tenant scope finished deprovisioning (offboard): every registered
/// `ITenantLifecycle.OnDeprovisioned` hook ran. The single end-of-
/// offboard marker — an operator querying the audit trail for this case
/// gets exactly one row per completed offboard, with the hook counts
/// proving how much cleanup ran.
type TenantDeprovisionedPayload = {
    ScopeId: string
    Actor: string
    HooksRun: int
    HooksCompleted: int
    HooksSkipped: int
    HooksFailed: int
    ElapsedMs: int64
}

/// One lifecycle hook failed during a provision / deprovision run.
/// Per-hook failure does NOT abort the run (the offboard continues so a
/// single misbehaving companion hook can't block crypto-shred / erasure
/// of the rest); the summary records the partial state and one of these
/// rows fires per failed hook for operator triage.
type TenantLifecycleHookFailedPayload = {
    ScopeId: string
    Actor: string
    /// `"Provisioning"` / `"Deprovisioning"` — which phase was running.
    Phase: string
    /// `ITenantLifecycle.Name` of the hook that failed.
    HookName: string
    /// Hook-supplied error text (or the timeout message when the hook
    /// exceeded its per-hook budget). Verbatim — operators read the
    /// hook's own diagnostic.
    Error: string
}

/// Phase 54j — the tenant's data-export archive was durably written as
/// the pre-step of an export-then-erase offboard, BEFORE any erasure
/// hook ran (fail-closed ordering: a failed export aborts the offboard,
/// so this row's presence proves the export committed first). Metadata
/// only — the archive reference, not its contents. Reserved
/// `SourceModule = "_platform.tenant"`.
type TenantDataExportedPayload = {
    ScopeId: string
    Actor: string
    /// Blob container the archive was written to.
    ArchiveContainer: string
    /// Blob path of the durable export archive (content-addressable).
    ArchivePath: string
    /// SHA-256 hex of the archive bytes — lets the departing customer
    /// verify the archive they received.
    ContentHash: string
    /// Number of export segments the archive bundles.
    SegmentCount: int
}

// ─── Phase 54i — offboard confirmation-gate audit payloads ─────────────
//
// Emitted by `PlatformTenantApiHandler` when `TenantOffboardConfirmation`
// is `TokenConfirmation` / `TwoPersonRule`. Reserved
// `SourceModule = "_platform.tenant"` (the same trail as the offboard
// itself). Metadata-only: scope, the requesting/redeeming admin ids, and
// the refusal reason — never tenant data, never the token secret.

/// A confirmation token was minted for a pending offboard
/// (`RequestDeprovisionToken`). The durable record that an admin asked to
/// arm a destructive offboard — the request itself touches no tenant data.
type TenantOffboardConfirmationRequestedPayload = {
    ScopeId: string
    /// Platform-Admin who requested the token.
    RequestedBy: string
    /// Operator-supplied reason for the offboard.
    Reason: string
    /// Token expiry — the window within which the redemption must happen.
    ExpiresAt: System.DateTimeOffset
}

/// A pending offboard's confirmation token was accepted and the
/// destructive offboard proceeded (`DeprovisionTenantConfirmed`). Under
/// `TwoPersonRule`, `ApprovedBy` differs from the original `RequestedBy`.
type TenantOffboardConfirmationApprovedPayload = {
    ScopeId: string
    /// Platform-Admin who requested the token (`ShareTokenClaim.IssuedBy`).
    RequestedBy: string
    /// Platform-Admin who redeemed the token and executed the offboard.
    ApprovedBy: string
}

/// A confirmation-gated offboard was refused at the gate (before any
/// destruction): a token-less destructive call under a confirmation mode,
/// a missing/expired/wrong-scope token, or a same-admin redemption under
/// `TwoPersonRule`. One row per refusal so a blocked teardown is never
/// silent (GP 6).
type TenantOffboardConfirmationRefusedPayload = {
    ScopeId: string
    /// Platform-Admin whose offboard attempt was refused.
    Actor: string
    /// Human-readable refusal cause (`"confirmation required"`,
    /// `"token expired"`, `"token scope mismatch"`,
    /// `"two-person rule: requester cannot self-approve"`, …).
    Reason: string
}

// ─── Phase 54f — scheduled / grace-period offboard audit payloads ──────
//
// Emitted by `PlatformTenantApiHandler` when an offboard is scheduled
// behind a grace window or that pending schedule is cancelled. Reserved
// `SourceModule = "_platform.tenant"`. Metadata-only. The eventual fire
// is recorded by the offboard's own `TenantDeprovisioned` marker, so
// there is no separate "fired" event.

/// A grace-period offboard was scheduled (`ScheduleDeprovision`): the
/// tenant will be deprovisioned at `DueAt` unless cancelled first.
type TenantDeprovisionScheduledPayload = {
    ScopeId: string
    /// Platform-Admin who scheduled the offboard.
    RequestedBy: string
    /// Operator-supplied reason.
    Reason: string
    /// When the offboard fires unless cancelled (UTC).
    DueAt: System.DateTimeOffset
    /// Backing scheduler job id (string-rendered).
    JobId: string
}

/// A pending grace-period offboard was cancelled
/// (`CancelScheduledDeprovision`) before it fired — the tenant survives.
type TenantDeprovisionCancelledPayload = {
    ScopeId: string
    /// Platform-Admin who cancelled the pending offboard.
    CancelledBy: string
    /// The reason the cancelled schedule carried (for the trail).
    Reason: string
}

/// Phase 69h.tail — uniform-shape audit row emitted by the ToolUp.Remoting
/// dispatcher for `[<Audit>]`-annotated API record methods. One payload
/// shape for every annotated method: the dispatcher knows the method
/// name, the resolved subject, the request correlation id, the declared
/// audit kind, and a PII-redacted snapshot of the input record (fields
/// without `[<PiiSafe>]` are `<redacted:TypeName>`). Bespoke per-domain
/// audit cases continue to exist where richer payloads are load-bearing;
/// this case is the structural floor every annotated method gets for free.
type RemotingMethodAuditedPayload = {
    /// Declared audit kind from the `[<Audit "...">]` attribute —
    /// `"MoneyMoved"`, `"PolicyChanged"`, `"PermissionGranted"`, … or
    /// `"Custom:<name>"` for open-vocabulary kinds.
    Kind: string
    /// The invoked API record method's bare name (e.g. `SetOverride`).
    MethodName: string
    /// Resolved subject id from the request's auth context
    /// (`user:{id}` / `team:{tid}:user:{uid}` / `anonymous:{sid}`), or
    /// `"anonymous"` when no auth context resolved.
    SubjectId: string
    /// Request correlation id (Phase 69b.D) for joining against logs +
    /// telemetry of the same request.
    CorrelationId: string option
    /// PII-redacted input-record snapshot. Field name → string value
    /// for `[<PiiSafe>]` fields; `<redacted:TypeName>` otherwise.
    Payload: Map<string, string>
}

/// Phase 443 — a WebAuthn passkey credential was enrolled for a user via
/// the passkey auth companion's registration ceremony. PII-free apart
/// from the platform `UserId` (already the audit actor). The credential
/// id is truncated so the trail correlates without persisting the full
/// authenticator handle. Source-module label `_platform.auth.passkey`.
type PasskeyCredentialRegisteredPayload = {
    UserId: string
    /// First 12 chars of the base64url credential id — correlatable,
    /// non-reversible to the raw authenticator handle.
    CredentialIdPrefix: string
    /// How the registration was authorised: `ExistingSession` /
    /// `Bootstrap` / `PendingInvite` / `OpenRegistration`.
    Grant: string
}

/// Phase 443 — a passkey credential was removed for a user (self-service
/// deregistration or admin revocation). Source-module label
/// `_platform.auth.passkey`.
type PasskeyCredentialRemovedPayload = {
    UserId: string
    /// First 12 chars of the base64url credential id.
    CredentialIdPrefix: string
}