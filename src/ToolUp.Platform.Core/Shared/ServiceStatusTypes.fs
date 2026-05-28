// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 9p.A — ServiceStatusBoard composite snapshot ──────────────
//
// Read-only aggregation of every operator-facing observability surface
// (Phase 9k HealthCheck, 9m Preflight, 9q ConfigDrift, 9p HealthMonitor,
// 9v RateLimiter, 9b JobScheduler, 9o SmokeTest) into a single wire
// shape — single pane of glass replacing today's per-concern admin tabs.
//
// **No new substrate interface.** The handler composes existing audited
// interfaces; this file declares only the wire-shape DTOs.
//
// **Per-section auto-skip.** When the underlying substrate is disabled
// (`ServerConfig.JobScheduler = NoJobScheduler`, `RateLimiter =
// NoRateLimiter`, etc.) the section is built with `Disabled = true` and
// no underlying read is performed — the UI renders a "disabled" badge
// and the section does not contribute to `OverallStatus`.

/// Three-level severity for a section's roll-up. `Ok` contributes
/// nothing to `OverallStatus`; `Warn` contributes to `DegradedBy`;
/// `Error` contributes to `UnhealthyBy`. `[<RequireQualifiedAccess>]`
/// because the bare `Ok` / `Error` case names would shadow the
/// standard `Result<_,_>` constructors for every consumer that opens
/// `ToolUp.Platform` (i.e. essentially every file in the SDK).
[<RequireQualifiedAccess>]
type StatusSeverity =
    | Ok
    | Warn
    | Error

/// Uniform per-section shape. Every concrete section type below is a
/// type abbreviation over this record — the snapshot's wire shape stays
/// the same across sections so the client renderer is one template, not
/// six. `Disabled = true` shapes describe "substrate not enabled in
/// this deployment"; `Severity` is irrelevant in that case but kept
/// `Ok` so a section-disabled deployment can still produce `AllOk`.
type SectionSummary = {
    /// Substrate-level enablement. `true` means the matching mode field
    /// on `ServerConfig` is the `No*` case — no underlying read was
    /// performed. The UI renders a neutral "disabled" badge and the
    /// section is excluded from `OverallStatus` aggregation.
    Disabled: bool
    /// Human-readable explanation when `Disabled = true` (e.g.
    /// "Job scheduler disabled (ServerConfig.JobScheduler = NoJobScheduler)").
    /// Empty when `Disabled = false`.
    DisabledReason: string
    /// Section's roll-up severity. Aggregated by `OverallStatus`.
    /// Ignored when `Disabled = true`.
    Severity: StatusSeverity
    /// One-line headline ("5 of 5 probes Healthy", "2 of 12 validators Warn").
    Headline: string
    /// Optional supplementary lines — failing-probe names, drift paths,
    /// scope-with-most-jobs, etc. Bounded length per section (≤ 10) so
    /// the snapshot does not bloat into a paginatable surface.
    Details: string list
}

module SectionSummary =
    /// Disabled-substrate shape with the given reason. Used by every
    /// section builder when its matching `ServerConfig` mode is `No*`.
    let disabled (reason: string) : SectionSummary = {
        Disabled = true
        DisabledReason = reason
        Severity = StatusSeverity.Ok
        Headline = "Disabled."
        Details = []
    }

    /// Failure shape used when the underlying handler threw or the
    /// per-section timeout expired. `Severity = Error` so it
    /// propagates into `OverallStatus.UnhealthyBy`.
    let failure (headline: string) (reason: string) : SectionSummary = {
        Disabled = false
        DisabledReason = ""
        Severity = StatusSeverity.Error
        Headline = headline
        Details = [ reason ]
    }

/// Per-section type abbreviations. The wire shape is identical across
/// sections; the type aliases exist so the snapshot record's field
/// names self-document which section each summary belongs to and so a
/// per-section refresh method's signature is unambiguous.
type HealthSummary = SectionSummary
type PreflightSummary = SectionSummary
type DriftSummary = SectionSummary
type RateLimitSummary = SectionSummary
type JobQueueSummary = SectionSummary
type SmokeTestSummary = SectionSummary

/// Top-line roll-up across every non-disabled section. `AllOk` when
/// every enabled section reports `Ok`; `DegradedBy` lists sections
/// that reported `Warn` (no `Error`); `UnhealthyBy` lists sections
/// that reported `Error` (may also include `Warn` sections — the UI
/// pill flips red as soon as one section is unhealthy).
type OverallStatus =
    | AllOk
    | DegradedBy of sections: string list
    | UnhealthyBy of sections: string list

/// Composite snapshot returned by `IServiceStatusBoardApi.GetSnapshot`.
type ServiceStatusSnapshot = {
    /// Server wall-clock at the moment the snapshot was generated —
    /// operators use this to confirm a refresh actually re-ran the
    /// underlying probes rather than returning a cached read.
    GeneratedAt: DateTime
    Overall: OverallStatus
    Health: HealthSummary
    Preflight: PreflightSummary
    Drift: DriftSummary
    RateLimit: RateLimitSummary
    JobQueue: JobQueueSummary
    SmokeTest: SmokeTestSummary
}

module ServiceStatusSnapshot =
    /// Stable section names used by `OverallStatus.DegradedBy` /
    /// `UnhealthyBy` lists and by the per-section refresh helpers.
    /// Strings (not a DU) at the wire boundary so the section set can
    /// evolve without breaking the API contract — same rationale as
    /// `HealthProbeView.Kind`.
    [<Literal>]
    let HealthSection = "Health"

    [<Literal>]
    let PreflightSection = "Preflight"

    [<Literal>]
    let DriftSection = "Drift"

    [<Literal>]
    let RateLimitSection = "RateLimit"

    [<Literal>]
    let JobQueueSection = "JobQueue"

    [<Literal>]
    let SmokeTestSection = "SmokeTest"

    /// Roll up per-section severities into the snapshot's `Overall`
    /// field. Disabled sections are excluded; everything else feeds
    /// the aggregate. Pure — no I/O. Defined here (not in the handler)
    /// so the client renderer can recompute `Overall` after a single-
    /// section refresh without a round-trip to the server.
    let computeOverall
        (health: HealthSummary)
        (preflight: PreflightSummary)
        (drift: DriftSummary)
        (rateLimit: RateLimitSummary)
        (jobQueue: JobQueueSummary)
        (smokeTest: SmokeTestSummary)
        : OverallStatus =
        let sections = [
            HealthSection, health
            PreflightSection, preflight
            DriftSection, drift
            RateLimitSection, rateLimit
            JobQueueSection, jobQueue
            SmokeTestSection, smokeTest
        ]

        let active = sections |> List.filter (fun (_, s) -> not s.Disabled)

        let errors =
            active
            |> List.choose (fun (name, s) ->
                match s.Severity with
                | StatusSeverity.Error -> Some name
                | _ -> None)

        let warnings =
            active
            |> List.choose (fun (name, s) ->
                match s.Severity with
                | StatusSeverity.Warn -> Some name
                | _ -> None)

        if not errors.IsEmpty then UnhealthyBy errors
        elif not warnings.IsEmpty then DegradedBy warnings
        else AllOk