// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 54 — tenant-lifecycle substrate shared types ──────────────
//
// Tenant provisioning + offboarding choreography consolidated behind a
// single substrate (`ITenantLifecycle`, server-tier). Companions
// self-register `OnProvisioned` / `OnDeprovisioned` hooks the same way
// they self-register `IHealthCheck` / `IConfigValidator`; one operator
// call runs every hook in deterministic order with audit + retry.
//
// These types are the Fable-compatible, async-free data surface that
// crosses the client/server boundary (the `IPlatformTenantApi`
// Fable.Remoting contract returns a `LifecycleSummary`, GP 10). The
// `ITenantLifecycle` interface itself + the aggregator that drives it
// are server-tier (`ToolUp.Platform.Server`).
//
// **Portability (GP 12).** Identity-by-value throughout (`scopeId` /
// `actorUserId` are strings); the per-hook outcome is data
// (`LifecycleHookResult`), never a framework callback; the summary
// carries no live handles. The interface's async/timeout/ordering
// contract lives on `ITenantLifecycle` (server tier).

/// Which phase of a tenant's lifecycle a hook (or an aggregated run) is
/// reacting to. `Provisioning` fans `OnProvisioned` across every
/// registered hook; `Deprovisioning` fans `OnDeprovisioned`.
type TenantLifecyclePhase =
    /// A new tenant scope is being stood up — `OnProvisioned` hooks run
    /// (key-material creation, default-config seeding, etc.).
    | Provisioning
    /// A tenant scope is being offboarded — `OnDeprovisioned` hooks run
    /// (crypto-shred, membership-cache eviction, scheduled-job
    /// cancellation, subject-data erasure, etc.).
    | Deprovisioning

module TenantLifecyclePhase =
    /// Stable wire/audit string for the phase. Used in audit payloads
    /// and log lines; do not rename without considering downstream
    /// dashboards keyed on the value.
    let name =
        function
        | Provisioning -> "Provisioning"
        | Deprovisioning -> "Deprovisioning"

/// Outcome of a single lifecycle hook invocation. Retry/supervision as
/// data (GP 12 rule 3) — a hook reports its own terminal disposition
/// rather than throwing a framework-specific failure callback. A hook
/// that decides its substrate is inactive returns `Skipped` with a
/// human-readable reason (e.g. an `EncryptionKeyLifecycle` under a
/// non-`PerScopeKeyResolver` resolver); a hook that genuinely failed
/// returns `Failed` with the error text. The aggregator never aborts
/// the run on a `Failed` — the summary records the partial state.
/// `[<RequireQualifiedAccess>]` because `Completed` / `Failed` collide
/// with the existing `JobRunStatus` cases — callers write
/// `LifecycleHookResult.Completed`.
[<RequireQualifiedAccess>]
type LifecycleHookResult =
    /// The hook ran its work to completion (or had no work to do but
    /// its substrate WAS active — distinct from `Skipped`).
    | Completed
    /// The hook's substrate is not active in this deployment, so the
    /// hook deliberately did nothing. Not a failure — the reason is
    /// surfaced for operator visibility.
    | Skipped of reason: string
    /// The hook attempted its work and failed. The run continues; the
    /// summary + a `TenantLifecycleHookFailed` audit row record it.
    | Failed of error: string

module LifecycleHookResult =
    /// Stable string discriminator — `"Completed"` / `"Skipped"` /
    /// `"Failed"`. Used by the aggregator for counting + log lines.
    let status =
        function
        | LifecycleHookResult.Completed -> "Completed"
        | LifecycleHookResult.Skipped _ -> "Skipped"
        | LifecycleHookResult.Failed _ -> "Failed"

/// One hook's recorded outcome within an aggregated run — the hook's
/// stable `Name`, its terminal `Result`, and how long it took. Ordered
/// (in the `LifecycleSummary.Outcomes` list) by hook completion; the
/// aggregator runs hooks in parallel, so completion order is not the
/// registration order.
type LifecycleHookOutcome = {
    /// `ITenantLifecycle.Name` of the hook that produced this outcome.
    HookName: string
    /// The hook's terminal disposition.
    Result: LifecycleHookResult
    /// Wall-clock duration of this hook's invocation, in milliseconds.
    ElapsedMs: int64
}

/// Aggregated result of running every registered hook for one
/// provision / deprovision call. Returned by the aggregator and by the
/// `IPlatformTenantApi` admin surface (`GetLifecycleSummary`); persisted
/// best-effort process-locally so an operator can read the last run's
/// disposition. The durable record is the audit trail
/// (`TenantProvisioned` / `TenantDeprovisioned` / `TenantLifecycleHookFailed`).
type LifecycleSummary = {
    /// Tenant scope the run targeted.
    ScopeId: string
    /// Which phase ran.
    Phase: TenantLifecyclePhase
    /// Per-hook outcomes, in completion order. Empty when no hook was
    /// registered (a valid, if no-op, run).
    Outcomes: LifecycleHookOutcome list
    /// Sum of wall-clock from the first hook dispatched to the last
    /// hook resolved, in milliseconds. With parallel dispatch this is
    /// the slowest hook, not the sum of per-hook times.
    TotalElapsedMs: int64
}

module LifecycleSummary =
    /// Count of outcomes whose result is `Completed`.
    let completedCount (s: LifecycleSummary) =
        s.Outcomes
        |> List.filter (fun o ->
            match o.Result with
            | LifecycleHookResult.Completed -> true
            | _ -> false)
        |> List.length

    /// Count of outcomes whose result is `Skipped _`.
    let skippedCount (s: LifecycleSummary) =
        s.Outcomes
        |> List.filter (fun o ->
            match o.Result with
            | LifecycleHookResult.Skipped _ -> true
            | _ -> false)
        |> List.length

    /// Count of outcomes whose result is `Failed _`.
    let failedCount (s: LifecycleSummary) =
        s.Outcomes
        |> List.filter (fun o ->
            match o.Result with
            | LifecycleHookResult.Failed _ -> true
            | _ -> false)
        |> List.length

    /// `true` when at least one hook failed. The run still completed —
    /// callers use this to decide whether to surface a partial-success
    /// banner, not to gate the offboard.
    let hasFailures (s: LifecycleSummary) = failedCount s > 0

/// A tenant-lifecycle domain event. Carried by emission sites + tests;
/// the `Deprovisioned` case fuses the `LifecycleSummary` so a single
/// event reconstructs the full offboard disposition. Kept on the shared
/// tier so client-side admin UIs can render the same shape.
///
/// `[<RequireQualifiedAccess>]` because `Deprovisioning` collides with
/// the `TenantLifecyclePhase` case of the same name — callers write
/// `TenantLifecycleEvent.Deprovisioning`.
[<RequireQualifiedAccess>]
type TenantLifecycleEvent =
    /// Provisioning completed for `scopeId`, triggered by `actorUserId`.
    | Provisioned of scopeId: string * actorUserId: string
    /// Deprovisioning began for `scopeId`, triggered by `actorUserId`,
    /// with an operator-supplied `reason`.
    | Deprovisioning of scopeId: string * actorUserId: string * reason: string
    /// Deprovisioning completed for `scopeId`; `summary` carries the
    /// per-hook disposition.
    | Deprovisioned of scopeId: string * summary: LifecycleSummary