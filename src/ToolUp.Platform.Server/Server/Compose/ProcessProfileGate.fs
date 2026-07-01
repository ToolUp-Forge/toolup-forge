// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── ProcessProfileGate — Phase 16 + 16a centralised gating ──────────
//
// Centralises the cross-config gating that decides whether each
// background subsystem's `IHostedService` registration happens in
// `compose`. The matrix sits on top of two orthogonal `ServerConfig`
// switches:
//
//   - `ServerlessHost` (Phase 16) — `ServerlessHost` short-circuits
//     EVERY background subsystem (the host adapter platform doesn't
//     keep the process alive long enough for any of them to run).
//
//   - `ProcessProfile` (Phase 16a) — `AllInOne` (default) runs every
//     subsystem in one process. `WebOnly` runs none (HTTP tier only;
//     a sibling worker drains background work). `WorkerOnly` runs
//     every subsystem (background tier only; no HTTP). `DispatcherOnly`
//     runs only the outbound dispatchers (transactional notifications
//     + webhook delivery) — outbound-delivery isolation.
//
// Helpers below decode the matrix into per-subsystem booleans. Each
// gate-site in `compose` calls one helper rather than re-deriving the
// rule. Adding a new subsystem only edits the matrix here.
//
// Lives in `Server/Compose/` so every Compose helper (ComposeJobs,
// ComposeNotifications, ComposeAudit, ComposeAuth, etc.) sees it; the
// IServerHost interface itself lives later in the compile chain
// because it references `WebApplication` and is consumed only by
// `compose`'s tail.

/// Distinct subsystems whose `IHostedService` registration is gated
/// by `ServerlessHost` and `ProcessProfile`. Case names use a
/// `Subsystem` suffix to disambiguate from the actual SDK class names
/// (`WebhookDispatcherService`, `OAuthStateCleanupService`,
/// `TransactionalDispatcher`, etc.) that appear in the same `open`
/// scope inside the Compose helpers.
type BackgroundSubsystem =
    /// Job scheduler tick + handler dispatch.
    | JobSchedulerSubsystem
    /// Webhook dispatcher retry / dead-letter loop.
    | WebhookDispatcherSubsystem
    /// Transactional notification dispatcher (email / SMS / push fan-out).
    | TransactionalDispatcherSubsystem
    /// Audit replicator — fans event-store writes out to `IAuditSink`s.
    | AuditReplicatorSubsystem
    /// Usage-metering batch flusher.
    | UsageBatchFlusherSubsystem
    /// Periodic IHealthCheck poll + state-change audit emission.
    | HealthStateTrackerSubsystem
    /// Phase 178 — periodic alert-rule / threshold evaluation +
    /// notification delivery. Runs on `AllInOne` / `WorkerOnly`; skipped
    /// on `WebOnly` / `DispatcherOnly` (not an outbound dispatcher) /
    /// `ServerlessHost` — all handled by the existing wildcard arms.
    | AlertRuleEngineSubsystem
    /// OAuth state-store TTL sweeper.
    | OAuthStateCleanupSubsystem
    /// OAuth refresher startup-time `Recover` (one-shot IHostedService).
    | OAuthRefresherRecoverSubsystem

module ProcessProfileGate =

    /// Returns `true` when `compose` should register the given
    /// subsystem's `IHostedService`. Encodes the cross-profile +
    /// serverless gating matrix as a single function.
    let shouldRegisterBackgroundService (config: ServerConfig) (subsystem: BackgroundSubsystem) : bool =
        // `ServerlessHost` trumps everything — the host adapter
        // platform can't keep a long-running tick alive.
        match config.ServerlessHost with
        | ServerlessHost -> false
        | KestrelHost ->
            match config.ProcessProfile, subsystem with
            // `WebOnly` silos serve no background work; a sibling
            // `WorkerOnly` drains the persistent stores.
            | WebOnly, _ -> false

            // `WorkerOnly` runs every background subsystem.
            | WorkerOnly, _ -> true

            // `DispatcherOnly` runs only the outbound dispatchers
            // (transactional notifications + webhook delivery). No
            // scheduler, no RAG ingestion, no audit replicator, no
            // usage flusher, no health tracker, no OAuth substrate.
            | DispatcherOnly, TransactionalDispatcherSubsystem -> true
            | DispatcherOnly, WebhookDispatcherSubsystem -> true
            | DispatcherOnly, _ -> false

            // `AllInOne` runs everything (today's default).
            | AllInOne, _ -> true

    /// Returns `true` when `compose` should register HTTP middleware
    /// + the Giraffe router. `WorkerOnly` silos serve no HTTP, so
    /// `configurePipeline` is bypassed (the silo runs only its
    /// registered `IHostedService` set). Every other profile mounts
    /// the HTTP pipeline.
    let shouldRegisterHttpPipeline (config: ServerConfig) : bool =
        match config.ProcessProfile with
        | WorkerOnly -> false
        | AllInOne
        | WebOnly
        | DispatcherOnly -> true