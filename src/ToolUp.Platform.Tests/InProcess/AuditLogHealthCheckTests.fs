module ToolUp.Platform.Tests.InProcess.AuditLogHealthCheckTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.HealthChecks

// ─── Helpers ──────────────────────────────────────────────────────────

/// Silent test logger — `EventStoreAuditLog` requires one but the
/// audit log writes are not under inspection here.
let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Build an InMemoryEventStore + EventStoreAuditLog pair (the
/// production audit path).
let private workingChain () =
    let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let auditLog = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog
    auditLog, store

/// Build a NoOpAuditLog + a separate IEventStore. The NoOp deliberately
/// doesn't participate in the chain — it's there to confirm the probe
/// reports `Healthy` for an explicitly-off configuration.
let private noOpChain () =
    let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let auditLog = AuditLog.NoOpAuditLog() :> IAuditLog
    auditLog, store

/// Build a working IAuditLog but a non-durable IEventStore — Write
/// returns immediately but ReadBySource never finds the marker. The
/// probe must report Unhealthy.
let private brokenStore () =
    { new IEventStore with
        member _.Write _ = async { return () }
        member _.ReadAll _ = async { return [] }
        member _.ReadByType(_, _) = async { return [] }
        member _.ReadBySource(_, _) = async { return [] }
        member _.ListScopes() = async { return [] }

        member _.Erase(_, _, _, _) = async {
            return
                Result.Ok {
                    HandlerName = "events"
                    RecordsAffected = 0
                    Note = None
                }
        }
    }

let private check (auditLog: IAuditLog) (store: IEventStore) : HealthResult =
    let probe = AuditLogHealthCheck.AuditLogHealthCheck(auditLog, store) :> IHealthCheck

    probe.Check() |> Async.RunSynchronously

// ─── Tests ────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 6l.B — Audit log health check" [

        test "Working EventStoreAuditLog + InMemoryEventStore → Healthy" {
            let auditLog, store = workingChain ()
            let result = check auditLog store
            Expect.equal result Healthy "round-trip succeeds"
        }

        test "NoOpAuditLog → Healthy (configured off, not broken)" {
            let auditLog, store = noOpChain ()
            let result = check auditLog store
            Expect.equal result Healthy "no-op chain reports Healthy"
        }

        test "EventStoreAuditLog over broken store → Unhealthy with diagnostic" {
            let store = brokenStore ()
            let auditLog = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog
            let result = check auditLog store

            match result with
            | Unhealthy msg -> Expect.stringContains msg "ReadBySource" "names the failing operation"
            | other -> failtestf "expected Unhealthy, got %A" other
        }

        test "Probe is Readiness (impacts /ready, not /health)" {
            let auditLog, store = workingChain ()

            let probe = AuditLogHealthCheck.AuditLogHealthCheck(auditLog, store) :> IHealthCheck

            Expect.equal probe.Kind Readiness "audit chain affects readiness"
            Expect.equal probe.Name "audit-log" "stable identifier"
        }

        test "Probe writes under _platform.audit_health (does not pollute the audit trail)" {
            let auditLog, store = workingChain ()
            let _ = check auditLog store

            // The audit trail filters to SourceModule = "_platform.audit"
            let trail =
                auditLog.GetAuditTrail("_platform", None, None) |> Async.RunSynchronously

            Expect.isEmpty trail "probe events do not surface in the audit trail"

            // But the probe events ARE in IEventStore under their own source
            let probeEvents =
                store.ReadBySource("_platform", "_platform.audit_health")
                |> Async.RunSynchronously

            Expect.isNonEmpty probeEvents "probe events live under _platform.audit_health"
        }
    ]