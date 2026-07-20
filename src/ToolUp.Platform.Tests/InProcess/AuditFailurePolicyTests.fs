module ToolUp.Platform.Tests.InProcess.AuditFailurePolicyTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform

// ─── Phase 9t — audit-write failure policy ───────────────────────────
//
// `AuditFailurePolicy` selects what `EventStoreAuditLog.Record` does
// when the store write fails, beyond the Phase 114 counter:
// `LogAndContinue` (prior behaviour), `RefuseAction` (raise —
// compliance-grade), `DegradeToFile` (spill to a bounded local
// directory + replay on recovery). This pack drives all three against
// a faulting store, plus the fallback store's capacity bound and
// poison-file quarantine.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

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

let private freshFallbackRoot () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-audit-fallback-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    root

let private sampleAudit userId =
    UserLoggedIn {
        UserId = userId
        AuthProvider = "Header"
    }

[<Tests>]
let tests =
    testList "Phase 9t — audit-write failure policy" [

        test "LogAndContinue (default) swallows the failure — prior behaviour" {
            let auditLog =
                AuditLog.EventStoreAuditLog(FaultingEventStore(), silentLogger, failurePolicy = LogAndContinue)
                :> IAuditLog

            // Must not throw.
            auditLog.Record("team-acme", sampleAudit "alice") |> Async.RunSynchronously
        }

        test "RefuseAction raises AuditWriteRefusedException so the action fails visibly" {
            let auditLog =
                AuditLog.EventStoreAuditLog(FaultingEventStore(), silentLogger, failurePolicy = RefuseAction)
                :> IAuditLog

            Expect.throwsT<AuditLog.AuditWriteRefusedException>
                (fun () -> auditLog.Record("team-acme", sampleAudit "alice") |> Async.RunSynchronously)
                "the failed audit write must refuse the action"
        }

        test "DegradeToFile spills the record and the replay drains it into a recovered store" {
            let root = freshFallbackRoot ()

            let fallback =
                AuditFallbackStore.AuditFallbackStore(root, AuditFallbackStore.DefaultMaxBytes, silentLogger)

            let auditLog =
                AuditLog.EventStoreAuditLog(
                    FaultingEventStore(),
                    silentLogger,
                    failurePolicy = DegradeToFile,
                    fallbackStore = fallback
                )
                :> IAuditLog

            // The action completes; the record spills.
            auditLog.Record("team-acme", sampleAudit "alice") |> Async.RunSynchronously
            Expect.equal (fallback.PendingCount()) 1 "one spilled record awaiting replay"

            // The store recovers — replay drains the spill into it.
            let recovered = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let replayed = fallback.ReplayOnce(recovered, 100) |> Async.RunSynchronously
            Expect.equal replayed 1 "one record replayed"
            Expect.equal (fallback.PendingCount()) 0 "spill drained"

            let events =
                recovered.ReadBySource("team-acme", "_platform.audit") |> Async.RunSynchronously

            Expect.equal events.Length 1 "the replayed record is the original audit event"
            Expect.equal events.Head.EventType "UserLoggedIn" "event type preserved through the spill"
        }

        test "DegradeToFile at capacity drops the record without failing the action" {
            let root = freshFallbackRoot ()

            // 8 bytes — nothing fits.
            let fallback = AuditFallbackStore.AuditFallbackStore(root, 8L, silentLogger)

            let auditLog =
                AuditLog.EventStoreAuditLog(
                    FaultingEventStore(),
                    silentLogger,
                    failurePolicy = DegradeToFile,
                    fallbackStore = fallback
                )
                :> IAuditLog

            // Must not throw; the record is lost (loudly, via the logger).
            auditLog.Record("team-acme", sampleAudit "alice") |> Async.RunSynchronously
            Expect.equal (fallback.PendingCount()) 0 "nothing spilled at capacity"
        }

        test "replay quarantines a poison file and still drains the valid spill" {
            let root = freshFallbackRoot ()

            let fallback =
                AuditFallbackStore.AuditFallbackStore(root, AuditFallbackStore.DefaultMaxBytes, silentLogger)

            // One valid spill via the audit log...
            let auditLog =
                AuditLog.EventStoreAuditLog(
                    FaultingEventStore(),
                    silentLogger,
                    failurePolicy = DegradeToFile,
                    fallbackStore = fallback
                )
                :> IAuditLog

            auditLog.Record("team-acme", sampleAudit "alice") |> Async.RunSynchronously

            // ...plus a hand-planted corrupt file that sorts FIRST
            // (all-zero ticks), so a non-quarantining drain would wedge
            // on it forever.
            let poisonDir = Path.Combine(root, "2020-01-01")
            Directory.CreateDirectory poisonDir |> ignore

            File.WriteAllBytes(
                Path.Combine(poisonDir, "0000000000000000000-poison.json"),
                Encoding.UTF8.GetBytes "not-json{"
            )

            let recovered = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let replayed = fallback.ReplayOnce(recovered, 100) |> Async.RunSynchronously

            Expect.equal replayed 1 "the valid record replayed despite the poison file"
            Expect.equal (fallback.PendingCount()) 0 "poison file no longer counted as pending"

            let quarantined =
                Directory.EnumerateFiles(root, "*.poison", SearchOption.AllDirectories)
                |> List.ofSeq

            Expect.equal quarantined.Length 1 "poison file quarantined, not deleted"
        }
    ]