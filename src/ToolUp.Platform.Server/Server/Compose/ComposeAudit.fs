module ToolUp.Platform.ComposeAudit

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── compose phase: audit-replicator substrate + DI ──────────────────
//
// Phase 9g — Audit replicator subsystem. When `AuditSinks` is non-empty,
// the replicator decorates the inner event store and runs as a
// `BackgroundService`. Sub-second steady-state via live hook + 5-min
// catch-up sweep recovers any drops or missed-during-downtime events.
// The decorator is the innermost wrap — the webhook subsystem's
// `WebhookDispatcher` self-audit events go THROUGH the audit-replication
// decorator (so they are mirrored externally) but NOT through the
// webhook hook (so they don't fan out recursively).
//
// Single-instance limitation (Phase 9c half 2): the bounded channel and
// per-scope semaphores are in-process. Distributed companion replaces
// both without changing the interface.
//
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up). Takes the exact substrate values the inline definition
// captured and returns the same shape. Zero behaviour change.

/// Build the audit-replicator subsystem and derive the "effective inner
/// event store" that downstream decorators (webhook hook, JobNotify
/// wrap) chain on top of. Returns `None` for `auditReplicatorSubsystem`
/// and the raw inner event store when no `IAuditSink` was registered.
let buildAuditReplicatorSubsystem
    (auditSinks: IAuditSink list)
    (auditReplicatorOptions: AuditReplicatorOptions option)
    (resolvedBlobStorage: IBlobStorage)
    (innerEventStore: IEventStore)
    (resolvedLogger: ILogger)
    : (AuditReplicator.AuditReplicator * IEventStore) option =

    if List.isEmpty auditSinks then
        None
    else
        // Validate Name uniqueness. Duplicate registration is a fatal
        // misconfiguration: the per-(sinkName, scopeId) cursor would
        // silently corrupt if two sinks shared a Name. Mirrors Phase 6f
        // duplicate-kind validation on `INotificationSink`.
        let names = auditSinks |> List.map _.Name

        let duplicates =
            names
            |> List.groupBy id
            |> List.filter (fun (_, xs) -> List.length xs > 1)
            |> List.map fst

        if not (List.isEmpty duplicates) then
            failwithf "Audit sinks must have unique Names. Duplicates: %s" (String.concat ", " duplicates)

        let options =
            auditReplicatorOptions |> Option.defaultValue AuditReplicatorOptions.defaults

        let cursorStore =
            AuditReplicator.BlobAuditReplicatorCursorStore(resolvedBlobStorage, resolvedLogger)
            :> AuditReplicator.IAuditReplicatorCursorStore

        // Replicator-self audit emission goes through the raw inner store —
        // bypasses webhook fan-out and job triggering for AuditSink*
        // events. Operators relying on webhook-driven alerts for
        // `AuditSinkDeadLettered` should poll `IAuditLog.GetAuditTrail`
        // filtered to that event type instead, or watch the replicator's
        // own ILogger.
        let replicatorAuditLog: IAuditLog =
            AuditLog.EventStoreAuditLog(innerEventStore, resolvedLogger) :> _

        let replicator =
            new AuditReplicator.AuditReplicator(
                auditSinks,
                cursorStore,
                innerEventStore,
                replicatorAuditLog,
                options,
                resolvedLogger
            )

        let auditDecoratedStore: IEventStore =
            AuditReplicator.AuditReplicationHookedEventStore(innerEventStore, replicator.Enqueue) :> _

        Some(replicator, auditDecoratedStore)

/// Derive the "effective inner event store" used by the webhook subsystem
/// and downstream decorator chain. Returns the raw inner store when no
/// audit sinks are registered; otherwise the audit-replication-hooked
/// decorator.
let effectiveInnerEventStore
    (innerEventStore: IEventStore)
    (auditReplicatorSubsystem: (AuditReplicator.AuditReplicator * IEventStore) option)
    : IEventStore =
    match auditReplicatorSubsystem with
    | None -> innerEventStore
    | Some(_, decorated) -> decorated

/// Phase 9g — register the audit replicator as a hosted service when
/// any `IAuditSink` was supplied. Sinks themselves are registered as
/// `IAuditSink` singletons so they can be resolved by tests + diagnostics,
/// even though the live dispatch path holds them through the captured
/// constructor list.
///
/// Phase 16 — `ServerlessHost` deployments skip the replicator's
/// `IHostedService`. The decorator chain still wraps `IEventStore`
/// writes (so emitted events enqueue against the in-process bounded
/// channel), but draining requires a sibling `WorkerOnly` silo
/// resolving the same `AuditReplicator` singleton. Pair
/// `ServerlessHost = ServerlessHost` with `AuditSinks = []` for
/// deployments without a sibling worker.
let registerAuditReplicatorHosting
    (services: IServiceCollection)
    (config: ServerConfig)
    (auditReplicatorSubsystem: (AuditReplicator.AuditReplicator * IEventStore) option)
    (auditSinks: IAuditSink list)
    : unit =
    match auditReplicatorSubsystem with
    | None -> ()
    | Some(replicator, _) ->
        services.AddSingleton<AuditReplicator.AuditReplicator>(replicator) |> ignore

        // Phase 16 + 16a — gate replicator BackgroundService on the
        // centralised matrix. ServerlessHost / WebOnly / DispatcherOnly
        // skip; AllInOne / WorkerOnly register.
        if ProcessProfileGate.shouldRegisterBackgroundService config AuditReplicatorSubsystem then
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                replicator :> Microsoft.Extensions.Hosting.IHostedService
            )
            |> ignore

        for sink in auditSinks do
            services.AddSingleton<IAuditSink>(sink) |> ignore