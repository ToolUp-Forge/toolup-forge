// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: the `AuditEnvelope` wire shape and
// its schema-version contract, the `IAuditLog` seam, the Phase 120
// `IAuthAuditHook` denial trail and the Phase 2c `BlobStorageAuthAudit`
// helper. Same namespace, same names — only the file boundary moved.

/// Phase 66 Stream B.7 (design §3.6 + D15 + D16) — sink-side envelope
/// that wraps an `AuditEvent` with the resolved `AuditSubject` and the
/// recording-side bookkeeping (`OccurredAt`, `ScopeId`). External sinks
/// (`SplunkHec` / `DatadogLogs` / `S3Archive`, plus the in-memory test
/// double) receive `AuditEnvelope list` batches — the audit-event DU
/// stays the load-bearing case shape, but the envelope is the layer
/// downstream Splunk dashboards and Datadog alerts read `subject_kind`
/// off without re-deriving from per-payload introspection.
///
/// **Why a wrapper, not a flat field on every case.** Today's
/// `AuditEvent` is a DU of ~70 case constructors, each carrying its own
/// payload record. Adding a `Subject` field to every payload would
/// touch every payload type and every emission call site for negligible
/// runtime benefit — sinks already need the wrapper for `OccurredAt` +
/// `ScopeId` correlation, so the envelope is the natural home for
/// `Subject` too. The DU stays append-only at the case level; envelope
/// shape evolves under the `LatestAuditSchemaVersion` contract.
///
/// **Construction.** Three call sites construct envelopes:
///   * `IAuditLog.Record` emission sites with an `AccessContext` in
///     scope — use `AuditSubject.fromSubject ctx.Subject`.
///   * `AuditReplicator` dispatch path — decodes `ModuleEvent` into
///     `AuditEvent` and derives `AuditSubject` from `ModuleEvent.ScopeId`
///     via `AuditSubject.fromScopeId` (best-effort; the persisted
///     `ModuleEvent` does not carry the originating subject today).
///   * Tests and contract packs — synthesise envelopes directly.
type AuditEnvelope = {
    /// Resolved per-request subject of the audited operation. Sinks
    /// read this to populate `subject_kind` / `subject.user_id` /
    /// `subject.team_id` tags without inspecting the payload.
    Subject: AuditSubject
    /// The audit event body itself. Existing wire format preserved at
    /// the case level; the envelope is the new layer above it.
    Event: AuditEvent
    /// Wall-clock timestamp the event was recorded. Mirrors the
    /// underlying `ModuleEvent.OccurredAt` so sinks can preserve
    /// per-scope ordering at the destination.
    OccurredAt: System.DateTime
    /// `IEventStore` scope under which the event was persisted.
    /// Preserved so sinks that tag-route by tenant can read it
    /// directly rather than re-derive via per-payload introspection
    /// (the legacy DatadogLogsAuditSink.extractScopeId pattern).
    ScopeId: string
}

module AuditEnvelope =
    /// Construct an envelope from a resolved `Subject` (request side)
    /// and the matching `AuditEvent`. Canonical helper for emission
    /// call sites that have an `AccessContext` in scope.
    let fromSubject
        (subject: Subject)
        (scopeId: string)
        (occurredAt: System.DateTime)
        (event: AuditEvent)
        : AuditEnvelope =
        {
            Subject = AuditSubject.fromSubject subject
            Event = event
            OccurredAt = occurredAt
            ScopeId = scopeId
        }

    /// Construct an envelope without a resolved `Subject`. Used by the
    /// audit-replicator dispatch path, which only has `ScopeId`
    /// available post-`ModuleEvent` decode. Defers to
    /// `AuditSubject.fromScopeId`.
    let fromScopeId (scopeId: string) (occurredAt: System.DateTime) (event: AuditEvent) : AuditEnvelope = {
        Subject = AuditSubject.fromScopeId scopeId
        Event = event
        OccurredAt = occurredAt
        ScopeId = scopeId
    }

    /// Project the envelope to its underlying event. Convenience for
    /// sinks that batch-serialise events and need only the inner
    /// payload (callers tag the envelope separately).
    let event (envelope: AuditEnvelope) : AuditEvent = envelope.Event

    /// Project the envelope to its subject-kind string. Convenience for
    /// sinks emitting `subject_kind:user` style tags.
    let subjectKindString (envelope: AuditEnvelope) : string =
        envelope.Subject |> AuditSubject.kind |> AuditSubject.kindString

/// Phase 66 Stream B.7 (design D16) — schema-version contract bumped
/// when the `AuditEnvelope` wire shape changes incompatibly. Sinks
/// declare `IAuditSink.SchemaVersion` against `AuditSchemaVersion.current`
/// so the audit-replicator can warn (or refuse delivery) on mismatch
/// rather than silently mangling downstream dashboards.
///
/// - `pre66 = 1` — bare `AuditEvent` batches; no `Subject` /
///   `OccurredAt` / `ScopeId` envelope fields exposed to sinks; sinks
///   would have to re-derive scope via per-payload introspection.
///   Retained as the documented historical floor. No production sink
///   ships at this version.
/// - `current = 2` — Phase 66 Stream B.7 envelope (this file's
///   `AuditEnvelope`). All three reference companions ship at this
///   version, as does the in-process `InMemoryAuditSink` test double.
module AuditSchemaVersion =
    /// Pre-Phase-66 wire shape. Retained as documented floor for the
    /// `IAuditSink.SchemaVersion` negotiation contract. No production
    /// sink ships at this version.
    [<Literal>]
    let pre66 = 1

    /// Phase 66 Stream B.7 envelope shape. Sinks implementing
    /// `IAuditSink.SchemaVersion` typically return this so they always
    /// ship the latest envelope shape rather than pinning a specific
    /// version.
    [<Literal>]
    let current = 2

/// Query interface for the audit log. SDK-wide DI service registered in
/// `SDK.Server.compose`. The default implementation wraps `IEventStore`
/// (writes through the configured store; reads filter to `SourceModule =
/// AuditSourceModule.value`).
///
/// **Scoping.** `Record` writes the event under the supplied `scopeId`;
/// `GetAuditTrail` reads from that scope only. Cross-scope reads are
/// structurally impossible — the underlying `IEventStore` enforces
/// team isolation via the blob path prefix.
///
/// **Best-effort, never blocking.** `Record` is fire-and-forget from the
/// caller's perspective: implementation failures are logged via
/// `ILogger` and swallowed. Audit emission must never roll back a
/// primary operation — the underlying state is durable in its own store.
type IAuditLog =
    /// Record an audit event under the given scope. Best-effort
    /// durability via the configured `IEventStore`. Failures are logged
    /// at `Warn` and swallowed.
    abstract Record: scopeId: string * audit: AuditEvent -> Async<unit>

    /// Query audit events for `scopeId`. Optional filters:
    /// - `dateRange`: inclusive `(from, to)` filter on `OccurredAt`.
    ///   `None` = no date constraint.
    /// - `eventType`: case-name filter (e.g. `"UserLoggedIn"`).
    ///   `None` = all kinds.
    /// Returned list is reverse-chronological by `OccurredAt`,
    /// matching `IEventStore.ReadAll`.
    abstract GetAuditTrail:
        scopeId: string * dateRange: (DateTime * DateTime) option * eventType: string option -> Async<AuditEvent list>

// ─── Phase 120 — IAuthAuditHook (structured authz-denial trail) ──────
//
// A single write-side seam every authorization-denial emission point
// calls, so the whole HTTP auth surface produces one uniform
// `AuthorizationDenied` audit row instead of scattered per-subsystem
// metrics + log lines. Generalises the AI tool-allowlist denial stream
// (Phase 45) to surface-enforcement / RBAC / share-token / SSE-identity
// / module-permission / KB-destructive denials.
//
// The default implementation (`AuthAuditHook`, Server tier) writes an
// `AuthorizationDenied` event through the registered `IAuditLog` and
// coalesces probing bursts via a per-`(route, subject)` dedup window, so
// a scripted enumeration produces bounded audit volume with an accurate
// count (GP 6 + GP 13 — no new infrastructure; the hook's backing store
// is the existing audit log).

/// The requirement class that was not satisfied at a denial. A DU (not a
/// bare string) so every emission call site is exhaustiveness-checked and
/// the read-side rollup can cut by typed requirement; serialised to its
/// `requirementString` form on the persisted `AuthorizationDeniedPayload`.
type AuthDenialRequirement =
    /// `SurfaceEnforcementMiddleware` route-surface matrix denial.
    | SurfaceDenialRequirement
    /// RBAC role / platform-admin gate denial.
    | RoleDenialRequirement
    /// Share-token validation failure (signature / revoked / expired /
    /// use-limit / rate-limit).
    | ShareTokenDenialRequirement
    /// SSE `?userId=` / principal-mismatch denial (the emission point lands
    /// when identity-aware SSE lifecycle ships; the case exists now so that
    /// wiring emits through the same uniform seam).
    | SseIdentityDenialRequirement
    /// Module-permission denial (e.g. a `SchemaOnly` caller reaching a
    /// real-row path).
    | ModulePermissionDenialRequirement
    /// Knowledge-Base destructive-op authorization denial.
    | KbDestructiveDenialRequirement

module AuthDenialRequirement =
    /// Stable wire string for the persisted payload's `Requirement` field.
    let toString =
        function
        | SurfaceDenialRequirement -> "surface"
        | RoleDenialRequirement -> "role"
        | ShareTokenDenialRequirement -> "share-token"
        | SseIdentityDenialRequirement -> "sse-identity"
        | ModulePermissionDenialRequirement -> "module-permission"
        | KbDestructiveDenialRequirement -> "kb-destructive"

/// Value-typed denial record handed to `IAuthAuditHook.RecordDenial`.
/// Carries the resolved `Subject` so emission sites stay terse; the hook
/// sanitises it to `(kind, id)` when it writes the audit row (no PII
/// beyond the subject id reaches the trail). Six-rule rule 1 (identity by
/// value): every field is a value — `Subject` is a value DU, no live
/// handles.
type AuthDenial = {
    /// Route the denial fired on (method + path, unparameterised).
    Route: string
    /// Resolved subject at denial time. Sanitised to kind + id by the hook.
    Subject: Subject
    /// Requirement class that was not satisfied.
    Requirement: AuthDenialRequirement
    /// Machine-readable verdict / denial code (mirrors the response body's
    /// `error` field where one exists).
    Verdict: string
    /// Human-readable reason. Callers keep this PII-free beyond the subject.
    Reason: string
    /// Scope the denial occurred in; `None` for scope-less (anonymous)
    /// denials. Determines the audit write scope (caller-scope when present,
    /// `_platform` otherwise) so the read-side rollup is caller-scoped (GP 4).
    ScopeId: string option
    /// Correlation id stitching to the request log.
    CorrelationId: string option
}

/// Write-side hook for authorization denials. SDK-wide DI singleton; the
/// default implementation writes an `AuthorizationDenied` audit event.
///
/// **Best-effort, never blocking** — same contract as `IAuditLog.Record`:
/// a hook failure on the denial path MUST NOT stall the response. Emission
/// sites wrap the call defensively.
///
/// Six-rule portability audit:
///   1. Identity by value      — `AuthDenial` is all values (`Subject` is a
///                                 value DU). No live handles.
///   2. Async                  — `RecordDenial : AuthDenial -> Async<unit>`.
///   3. Retry as data          — none; emission is best-effort fire-and-
///                                 forget, matching `IAuditLog`.
///   4. Stateless handlers     — the contract carries all state per call;
///                                 the default impl's dedup window is an
///                                 in-process optimisation, documented
///                                 single-instance (a distributed impl can
///                                 no-op the coalescing and emit per call).
///   5. No cross-shard ordering — denial rows are independent; no ordering
///                                 promise across routes/subjects.
///   6. Precision lower bound   — the dedup window is a coarse `TimeSpan`;
///                                 no sub-second promise.
type IAuthAuditHook =
    /// Record an authorization denial. Best-effort; failures are swallowed
    /// by the implementation. The default impl coalesces probing bursts on
    /// the same `(route, subject)` key within its dedup window.
    abstract RecordDenial: denial: AuthDenial -> Async<unit>
// ─── Phase 2c — cloud-storage credential-rejection trail ─────────────
//
// The one place a cloud-storage companion turns a rejected SDK call into
// a `BlobStorageAuthFailed` row. Three companions need identical
// behaviour on this path — scope, sanitisation, and the guarantee that
// emission can never change what the caller sees — so it lives once
// here rather than three times in `Storage/*`. What CANNOT live here is
// the classification itself: deciding that an exception is a 401/403
// means naming `AmazonS3Exception` / `RequestFailedException` /
// `GoogleApiException`, and a vendor SDK type never enters
// `ToolUp.Platform.*` (GP 1). So each companion classifies and this
// module records.
//
// Fable-safe: values, `Async`, and BCL string operations only — no
// regex (nothing else in `Platform.Core/Shared` uses one, and this file
// ships under `fable/`).

/// Records the `BlobStorageAuthFailed` row for a cloud-storage call that
/// was rejected 401/403. See `BlobStorageAuthFailedPayload` for why the
/// row exists and what each field answers.
module BlobStorageAuthAudit =

    /// The scope every credential-rejection row is recorded under.
    ///
    /// Deliberately NOT the caller's scope, which is the more usual
    /// choice for a storage event. A rejected credential is a property of
    /// the DEPLOYMENT, not of whichever tenant happened to make the call
    /// that discovered it: during a rotation window every scope's calls
    /// fail, so a per-scope trail would scatter one operational fact
    /// across every tenant and leave no single place to ask "when did
    /// this start". The container the call was made against is carried in
    /// the payload instead, so nothing is lost by centralising.
    [<Literal>]
    let scopeId = "_platform"

    /// Longest sanitised SDK message kept on a row. An SDK message is a
    /// diagnostic aid here, not a queryable axis — the four fields above
    /// it are — so a bound that keeps the row small beats a complete
    /// message.
    [<Literal>]
    let maxReasonLength = 512

    /// Name fragments that mark a `name=value` pair as secret-bearing.
    /// Deliberately broad: `AccessKeyId` matches on `key` and is redacted
    /// even though it is an identifier rather than a secret, because the
    /// cost of redacting an id is a slightly less specific diagnostic and
    /// the cost of the opposite mistake is a credential in an audit
    /// store that replicates to third-party sinks.
    let private secretMarkers = [ "key"; "secret"; "password"; "token"; "signature"; "sig"; "credential" ]

    /// Redact secret-bearing `name=value` pairs from an SDK message,
    /// flatten control characters and separators to single spaces, and
    /// truncate to `maxReasonLength`.
    ///
    /// The motivating case is an Azure connection string echoed back in a
    /// `RequestFailedException` message — `AccountKey=...` — but the rule
    /// is applied to every companion's message rather than to Azure's,
    /// because "which SDK might quote a credential" is not a question
    /// this code can keep answering correctly as SDKs change.
    ///
    /// Separators are normalised rather than preserved: rebuilding the
    /// original punctuation around redacted tokens would be more code for
    /// a string nobody parses.
    let sanitiseReason (message: string) : string =
        if String.IsNullOrWhiteSpace message then
            ""
        else
            let flattened = message |> String.map (fun c -> if Char.IsControl c then ' ' else c)

            let redact (token: string) =
                let separator = token.IndexOf '='

                if separator > 0 then
                    let name = token.Substring(0, separator)
                    let lowered = name.ToLowerInvariant()

                    if secretMarkers |> List.exists lowered.Contains then
                        name + "=***"
                    else
                        token
                else
                    token

            let normalised =
                flattened.Split([| ' '; ';'; '&'; ',' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map redact
                |> String.concat " "

            if normalised.Length <= maxReasonLength then
                normalised
            else
                normalised.Substring(0, maxReasonLength) + "..."

    /// Build the row for a rejected call. Split out from `record` so a
    /// caller can assert the payload without an `IAuditLog`, and so the
    /// timestamp is the only impurity.
    let payload
        (companion: string)
        (container: string)
        (operation: string)
        (statusCode: int)
        (message: string)
        : BlobStorageAuthFailedPayload =
        {
            Companion = companion
            Container = container
            Operation = operation
            StatusCode = statusCode
            Reason = sanitiseReason message
            At = DateTime.UtcNow
        }

    /// Record one credential-rejection row, if the companion was composed
    /// with an audit log.
    ///
    /// **Never throws, and never delays the caller's own error.** A
    /// deployment that composed no `IAuditLog` (`None`) does nothing at
    /// all; a sink that raises is swallowed here, matching
    /// `IAuditLog.Record`'s own best-effort contract. The one thing this
    /// function must not do is turn a storage failure into a different
    /// storage failure — the caller returns the error it always returned,
    /// immediately after this returns.
    let record
        (audit: IAuditLog option)
        (companion: string)
        (container: string)
        (operation: string)
        (statusCode: int)
        (message: string)
        : Async<unit> =
        async {
            match audit with
            | None -> ()
            | Some log ->
                try
                    let event =
                        BlobStorageAuthFailed(payload companion container operation statusCode message)

                    do! log.Record(scopeId, event)
                with _ ->
                    ()
        }