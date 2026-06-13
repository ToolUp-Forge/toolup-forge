module ToolUp.Platform.Tests.InProcess.AuditWriteFailureMetricTests

open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── Phase 114 — audit-write failure metric ──────────────────────────
//
// `EventStoreAuditLog.Record` swallows write failures (audit emission
// must never fail the primary operation), but pre-114 the only signal a
// row was lost was a `logger.Warn` — invisible to dashboards. Phase 114
// adds the `toolup.audit.write_failures_total` counter, tagged by
// `event_type`, so silent audit loss is alertable. This pack verifies
// the counter increments when the event-store write throws.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// `IEventStore` whose `Write` always throws — the faulting double the
/// acceptance criterion calls for. Reads return empty.
type private FaultingEventStore() =
    interface IEventStore with
        member _.Write(_evt) = async { return failwith "simulated event-store write failure" }
        member _.ReadAll(_scopeId) = async { return [] }
        member _.ReadByType(_scopeId, _eventType) = async { return [] }
        member _.ReadBySource(_scopeId, _sourceModule) = async { return [] }
        member _.ListScopes() = async { return [] }

        member _.Erase(_scopeId, _subjectUserId, _policy, _dryRun) = async {
            return Ok(Unchecked.defaultof<ErasureSummary>)
        }

/// `IMetricsSink` that records every `Increment(name, tags)` call.
type private CapturingMetricsSink() =
    let increments = ConcurrentBag<string * Map<string, string>>()
    member _.Increments = increments |> List.ofSeq

    interface IMetricsSink with
        member _.Record(_name, _value, _tags) = ()
        member _.Increment(name, tags) = increments.Add(name, tags)
        member _.SetGauge(_name, _value, _tags) = ()

[<Tests>]
let tests =
    testList "Phase 114 — audit-write failure metric" [

        test "Record increments audit_write_failures_total when the store write throws" {
            let sink = CapturingMetricsSink()
            let store = FaultingEventStore() :> IEventStore

            let auditLog =
                AuditLog.EventStoreAuditLog(store, silentLogger, (fun () -> sink :> IMetricsSink)) :> IAuditLog

            // Record must not throw even though the underlying write does.
            auditLog.Record(
                "team-acme",
                UserLoggedIn {
                    UserId = "alice"
                    AuthProvider = "Header"
                }
            )
            |> Async.RunSynchronously

            let failureIncrements =
                sink.Increments
                |> List.filter (fun (name, _) -> name = AuditLog.AuditMetrics.WriteFailuresTotal)

            Expect.equal failureIncrements.Length 1 "exactly one write-failure increment"

            let _, tags = failureIncrements.Head
            Expect.equal (Map.tryFind "event_type" tags) (Some "UserLoggedIn") "tagged with the failing event type"
        }

        test "Record does not increment the counter when the store write succeeds" {
            let sink = CapturingMetricsSink()
            let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let auditLog =
                AuditLog.EventStoreAuditLog(store, silentLogger, (fun () -> sink :> IMetricsSink)) :> IAuditLog

            auditLog.Record(
                "team-acme",
                UserLoggedIn {
                    UserId = "bob"
                    AuthProvider = "Header"
                }
            )
            |> Async.RunSynchronously

            let failureIncrements =
                sink.Increments
                |> List.filter (fun (name, _) -> name = AuditLog.AuditMetrics.WriteFailuresTotal)

            Expect.isEmpty failureIncrements "no write-failure increment on the success path"
        }
    ]