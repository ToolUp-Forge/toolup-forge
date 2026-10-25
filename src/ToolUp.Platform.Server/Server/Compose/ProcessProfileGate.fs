// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

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
// Phase 1051 — the matrix governs the subsystems that RUN background
// work a sibling silo could run instead. Every other hosted service is
// registered on every profile and says why, beside the registration, as
// a `HostedServiceExemption` (`ProcessProfileGate.addExempt`). A hosted
// service is therefore in exactly one of two states — admitted by the
// matrix, or exempt with its reason — and `HostedServiceGateTests` holds
// the registrations to that: under `WebOnly`, where the matrix admits
// nothing, every `IHostedService` descriptor has its exemption.
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
    /// Phase 9t — periodic drain of the `DegradeToFile` audit spill
    /// back into the live `IEventStore`. Runs on `AllInOne` /
    /// `WorkerOnly` via the wildcard arms; skipped on `WebOnly` /
    /// `DispatcherOnly` (not an outbound dispatcher) / `ServerlessHost`.
    | AuditFallbackReplaySubsystem
    /// Phase 599 — entity-outbox relay: publishes the staged events of
    /// version-witnessed entity saves and discards never-committed
    /// intents. Same wildcard-arm profile as the audit fallback replay.
    /// Phase 1027 — it also gates the lineage-outbox relay (durable
    /// lineage): a profile that skips this subsystem retries no staged
    /// lineage link either.
    | EntityOutboxRelaySubsystem
    /// Phase 10a — one-shot startup sweep upgrading stored objects
    /// whose stamped schema version lags what their module declares.
    /// Same wildcard-arm profile as the audit fallback replay: runs on
    /// `AllInOne` / `WorkerOnly`; skipped on `WebOnly` (a sibling
    /// worker silo does the upgrading), `DispatcherOnly` (not an
    /// outbound dispatcher) and `ServerlessHost`.
    | DataMigrationSweepSubsystem
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

/// Phase 1051 — a hosted service registered without consulting the
/// `BackgroundSubsystem` matrix, with the reason written beside it.
///
/// Registered as a DI singleton next to the `IHostedService` it explains
/// (`ProcessProfileGate.addExempt` registers both), so the exemption is
/// data a test or an inspector reads off the service collection rather
/// than a comment only a reader of the source can find.
type HostedServiceExemption = {
    /// The service, as an operator would name it in a log line.
    Service: string
    /// Why the matrix is not consulted for it.
    Reason: string
}

module ProcessProfileGate =

    /// Phase 1051 — the reason every startup registration with
    /// `IJobScheduler` shares. Such a service runs no loop of its own: it
    /// hands handlers (and, for a deferred declaration, triggers) to the
    /// scheduler, whose own `JobSchedulerSubsystem` gate decides where the
    /// work runs. A registration has to reach every process that can
    /// enqueue the job, which is why compose-time module declarations
    /// (`ComposeJobs.registerScheduledJobDeclarations`) register on every
    /// profile too.
    [<Literal>]
    let SchedulerRegistrationReason =
        "hands its handlers to IJobScheduler at start and runs no work of its own: the scheduler's JobSchedulerSubsystem gate decides where the work runs, and every process that can enqueue the job needs the handler"

    /// Phase 1051 — the exemption alone, for a hosted service registered
    /// as an INSTANCE beside it (a container never disposes an instance it
    /// was handed, so moving such a registration onto `addExempt`'s
    /// factory would change who disposes it).
    let exempt (service: string) (reason: string) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<HostedServiceExemption>({ Service = service; Reason = reason })

    /// Phase 1051 — register `factory`'s hosted service on every profile,
    /// with the exemption that says why. The entry point for a hosted
    /// service the matrix does not govern; a governed service consults
    /// `shouldRegisterBackgroundService` instead.
    let addExempt
        (service: string)
        (reason: string)
        (factory: IServiceProvider -> IHostedService)
        (services: IServiceCollection)
        : IServiceCollection =
        (exempt service reason services).AddSingleton<IHostedService>(Func<IServiceProvider, IHostedService>(factory))

    /// Phase 1051 — the startup-registration shape: a hosted service whose
    /// `StartAsync` runs `start` once (typically
    /// `IJobScheduler.RegisterHandler`) and whose stop does nothing. The
    /// one object expression the startup registrations share. Construct it
    /// inside the registration factory (`AddSingleton(fun sp -> atStart …)`),
    /// beside `exempt … SchedulerRegistrationReason`, so whatever `start`
    /// resolves from `sp` is resolved in the sanctioned place.
    let atStart (start: unit -> unit) : IHostedService =
        { new IHostedService with
            member _.StartAsync(_ct) =
                start ()
                Threading.Tasks.Task.CompletedTask

            member _.StopAsync(_ct) = Threading.Tasks.Task.CompletedTask
        }

    /// Phase 1051 — register `declarations` through
    /// `DeferredScheduledJobDeclaration.hostedService`, exempt with
    /// `SchedulerRegistrationReason`. `label` names the composing feature
    /// in the exemption and in the no-scheduler diagnostic alike.
    let addDeferredJobs
        (label: string)
        (declarations: DeferredScheduledJobDeclaration list)
        (services: IServiceCollection)
        : IServiceCollection =
        services
        |> addExempt
            label
            SchedulerRegistrationReason
            (DeferredScheduledJobDeclaration.hostedService label declarations)

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
            // scheduler, no audit replicator, no usage flusher, no
            // health tracker, no OAuth substrate. RAG ingestion is not a
            // case of this matrix: its in-process queue is drained only
            // by the process that accepted the upload, so it registers
            // on every profile with that reason (Phase 1051).
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