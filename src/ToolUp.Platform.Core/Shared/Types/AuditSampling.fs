// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Audit sampling policy ───────────────────────────────────────
//
// Phase 66 Stream C.2 (design §3.6 + D17). Hoisted out of AuditTypes.fs
// into its own file compiled BEFORE SDK.Shared.fs so `ServerConfig` can
// carry an `AuditSamplingPolicy` field. The type depends only on
// `AuditSubjectKind` + `Guid` — neither needs `ModuleEvent` (which lives
// in SDK.Shared.fs), so both can compile ahead of the config record.
// `AuditSubjectKind` lives here (rather than in AuditTypes.fs) because
// `AuditSamplingPolicy.rateFor` matches on it; AuditTypes.fs's
// `AuditSubject.kind` / `kindString` reference it from this earlier file.

/// Lightweight kind tag for `AuditSubject`. Mirrors `SubjectKind` (the
/// four-case discriminator for the request-side `Subject`) so audit-side
/// downstream code can call the same `kind` projection without
/// disambiguating which DU it holds.
type AuditSubjectKind =
    | AnonymousAuditKind
    | UserAuditKind
    | TeamAuditKind
    | ClaimAuditKind

/// Phase 66 Stream C.2 (design §3.6 + D17) — per-subject-kind audit
/// sampling. A single central policy on `ServerConfig` (NOT per-sink):
/// the `AuditReplicator` consults it per event and deterministically
/// skips delivery for a fraction of events whose subject matches a kind
/// with a keep-rate < 1.0.
///
/// **Why per subject kind.** The volume asymmetry the feature exists to
/// tame is kind-shaped — an anonymous-heavy public surface can emit
/// orders of magnitude more audit events than its authenticated surface.
/// Operators keep 100% of the lower-volume, higher-value authenticated
/// trail while thinning the anonymous firehose (design preset: "100%
/// authenticated, 10% anonymous").
///
/// **Deterministic, not random.** The keep/skip decision hashes the
/// event's `Id` (a `Guid`) into `[0, 1)` and keeps the event when the
/// hash is strictly below the kind's rate. Determinism means (a) the
/// same event is sampled identically on the live-hook path and on any
/// catch-up re-read, so a sampled-out event never flickers into
/// delivery on a sweep; (b) tests are reproducible without seeding an
/// RNG.
///
/// **Default = no sampling.** `AuditSamplingPolicy.none` sets every kind
/// to `1.0` (keep everything) — byte-for-byte the pre-C.2 audit pipeline
/// (GP 11 backward-compatible default). Operators opt in to thinning.
type AuditSamplingPolicy = {
    /// Keep-rate for `AnonymousAudit` events, in `[0.0, 1.0]`. `1.0`
    /// keeps every event; `0.1` keeps ~10%; `0.0` drops all.
    Anonymous: float
    /// Keep-rate for `UserAudit` events, in `[0.0, 1.0]`.
    User: float
    /// Keep-rate for `TeamAudit` events, in `[0.0, 1.0]`.
    Team: float
    /// Keep-rate for `ClaimAudit` events, in `[0.0, 1.0]`.
    Claim: float
}

module AuditSamplingPolicy =
    /// No sampling — keep every event for every subject kind. The
    /// default on `ServerConfig`; byte-for-byte the pre-C.2 audit
    /// pipeline (GP 11).
    let none: AuditSamplingPolicy = {
        Anonymous = 1.0
        User = 1.0
        Team = 1.0
        Claim = 1.0
    }

    /// Keep-rate for a given subject kind under this policy.
    let rateFor (policy: AuditSamplingPolicy) (kind: AuditSubjectKind) : float =
        match kind with
        | AnonymousAuditKind -> policy.Anonymous
        | UserAuditKind -> policy.User
        | TeamAuditKind -> policy.Team
        | ClaimAuditKind -> policy.Claim

    /// Deterministic `Guid` → `[0, 1)` projection. Folds six bytes of
    /// the id into a 48-bit non-negative integer (which fits exactly in
    /// a `float`) and scales by `2^48`. Pure `float` arithmetic — no
    /// `uint64` so the projection compiles identically under Fable and
    /// .NET, and is stable across platforms / runs.
    let private hash01 (eventId: Guid) : float =
        let bytes = eventId.ToByteArray()
        let mutable acc = 0.0

        for i in 0..5 do
            acc <- acc * 256.0 + float bytes[i]

        acc / 281474976710656.0 // 256^6 = 2^48

    /// Deterministic keep/skip decision for a single event. Rate `>= 1.0`
    /// always keeps (the fast path for the default policy — no hashing);
    /// rate `<= 0.0` always drops; otherwise keep when `hash01 eventId`
    /// is strictly below the kind's rate.
    let shouldDeliver (policy: AuditSamplingPolicy) (kind: AuditSubjectKind) (eventId: Guid) : bool =
        let rate = rateFor policy kind

        if rate >= 1.0 then true
        elif rate <= 0.0 then false
        else hash01 eventId < rate