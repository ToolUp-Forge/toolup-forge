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

// ─── Phase 54a — background / async offboard handle ──────────────────
//
// The async offboard path (`IPlatformTenantApi.DeprovisionTenantAsync`)
// enqueues an `IJobScheduler`-backed lifecycle job and returns this
// handle promptly, instead of awaiting a (potentially 25-minute)
// multi-store erasure inline under the aggregator's per-hook timeout.
// The operator polls `GetLifecycleSummary scopeId` for streaming
// progress (running → N-of-M hooks done); the durable record remains the
// audit trail (`TenantDeprovisioned`).

/// Handle to a background offboard job. Identity-by-value (GP 12 rule 1)
/// — `JobId` is the scheduler's value-typed `JobId` (a `Guid`), never a
/// live handle, so the same poll works against the in-process scheduler
/// and any distributed companion. Lives on the shared tier (GP 10) so the
/// admin client renders the same shape `DeprovisionTenantAsync` returns.
type LifecycleJobHandle = {
    /// Scheduler `JobId` of the enqueued lifecycle job. Poll its
    /// disposition via the snapshot surface (`GetLifecycleSummary`) or the
    /// job substrate (`IJobScheduler.Get`).
    JobId: JobId
    /// Tenant scope the offboard targets.
    ScopeId: string
    /// Which phase the job runs. The v1 async path always enqueues
    /// `Deprovisioning`; carried for symmetry + forward use.
    Phase: TenantLifecyclePhase
}

// ─── Phase 54b — per-hook retry policy ───────────────────────────────
//
// Retry-as-data (GP 12 rule 3): the aggregator re-invokes a hook that
// returns `Failed` per this policy's backoff before recording the
// terminal outcome — never an `OnFailure` callback. An aggregator-level
// default applies to every hook (a per-hook override is a future
// extension that would not change this shape). Lives on the shared tier
// so a deployment can configure it from `ServerConfig`.

/// Per-hook retry policy for the offboard sweep. Exponential backoff
/// `min(InitialBackoff * 2^(attempt-1), MaxBackoff)`; `MaxAttempts` is
/// inclusive — `1` means no retry (one attempt, the prior Phase 54
/// behaviour). Mirrors `JobRetryPolicy`'s shape (minus the dead-letter
/// destination, which the lifecycle sweep records as a `Failed` outcome +
/// audit row rather than routing onward).
type LifecycleRetryPolicy = {
    /// Inclusive max attempts. `1` = no retry.
    MaxAttempts: int
    /// Delay before the first retry (attempt 2).
    InitialBackoff: TimeSpan
    /// Cap on the exponential backoff.
    MaxBackoff: TimeSpan
}

module LifecycleRetryPolicy =
    /// No retry — one attempt, then record the terminal outcome. The
    /// prior Phase 54 / 54a behaviour (the inline parallel `run` path
    /// keeps this; only the ledger-aware sweep opts into retry).
    let noRetry: LifecycleRetryPolicy = {
        MaxAttempts = 1
        InitialBackoff = TimeSpan.Zero
        MaxBackoff = TimeSpan.Zero
    }

    /// SDK default for the background sweep: 3 attempts, 2s initial
    /// backoff, 30s cap. Tuned for offboard hooks that fail on transient
    /// resource pressure (a store briefly unreachable mid-erasure) and
    /// recover within the window.
    let defaults: LifecycleRetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromSeconds 2.0
        MaxBackoff = TimeSpan.FromSeconds 30.0
    }

    /// Delay before `attempt` (1-indexed). Attempt 1 runs immediately
    /// (`delayFor 1 = Zero`); attempt 2+ uses exponential backoff capped
    /// at `MaxBackoff`. Mirrors `JobRetryPolicy.delayFor`.
    let delayFor (policy: LifecycleRetryPolicy) (attempt: int) : TimeSpan =
        if attempt <= 1 then
            TimeSpan.Zero
        else
            let raw = policy.InitialBackoff.TotalMilliseconds * (2.0 ** float (attempt - 1))
            TimeSpan.FromMilliseconds(min raw policy.MaxBackoff.TotalMilliseconds)

// ─── Phase 54c — offboard preview / dry-run ──────────────────────────
//
// A mutation-free projection of what a `DeprovisionTenant` *would* do —
// "destroy key X, cancel 12 jobs, erase 340 records" — so an operator can
// inspect the blast radius before committing to the irreversible call.
// Each hook MAY contribute a count-only would-affect item (via the
// server-tier `ITenantLifecyclePreview` optional capability); a hook that
// opts out surfaces a clear "no preview available" item rather than a
// silent gap. Lives on the shared tier (GP 10) so the admin client
// renders the same shape `PreviewDeprovision` returns.

/// One hook's would-affect projection for an offboard preview.
/// Count-only, mutation-free. `HasPreview = false` marks a hook that
/// opted out of preview (no `ITenantLifecyclePreview`), so the operator
/// sees the gap explicitly.
type LifecyclePreviewItem = {
    /// `ITenantLifecycle.Name` of the hook this item projects.
    HookName: string
    /// `true` when the hook produced a real preview; `false` when it
    /// opted out (then `WouldAffect = 0` and `Detail` says so).
    HasPreview: bool
    /// Count of records / keys / jobs the offboard would affect for this
    /// hook. `0` when the hook would affect nothing, or when it opted out.
    WouldAffect: int
    /// Human-readable detail — `"the scope's encryption key would be
    /// destroyed"`, `"12 scheduled jobs would be cancelled"`,
    /// `"no preview available"`, …
    Detail: string
}

module LifecyclePreviewItem =
    /// The hook opted out of preview (no `ITenantLifecyclePreview`).
    let noPreview (hookName: string) : LifecyclePreviewItem = {
        HookName = hookName
        HasPreview = false
        WouldAffect = 0
        Detail = "no preview available"
    }

    /// A real would-affect projection.
    let affecting (hookName: string) (count: int) (detail: string) : LifecyclePreviewItem = {
        HookName = hookName
        HasPreview = true
        WouldAffect = count
        Detail = detail
    }

/// Aggregated offboard preview — every registered hook's would-affect
/// item plus the total across hooks that produced a real preview.
type LifecyclePreview = {
    /// Tenant scope the preview targets.
    ScopeId: string
    /// Per-hook would-affect items, in hook order.
    Items: LifecyclePreviewItem list
    /// Sum of `WouldAffect` across items where `HasPreview = true`.
    TotalWouldAffect: int
}