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

// ─── Phase 863 — the DEFAULT store reports a failed write ────────────
//
// Every case above drives the policy with an `IEventStore` that THROWS.
// The default store never did: `PersistentEventStore.Write` discarded
// the `Result` of `IBlobStorage.Upload`, and every shipped blob store
// reports failure as `Error`, so under the default composition a lost
// audit row was counted as a success — no counter, no refusal, no spill.
// These cases drive the policy through the real store over a blob
// double whose `Upload` returns `Error`.

/// Returns `Error` from `Upload` for blob names matching `shouldFail`,
/// passing every other operation through to `inner`.
type private WriteFailingBlobStorage(inner: BlobStorage.IBlobStorage, shouldFail: string -> bool) =
    interface BlobStorage.IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            if shouldFail blobName then
                async { return Error "simulated storage write failure" }
            else
                inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// Records every counter increment, so a case can assert the failure
/// was COUNTED as well as acted on.
type private CountingMetricsSink() =
    let increments = System.Collections.Concurrent.ConcurrentBag<string>()

    member _.Count(name: string) =
        increments |> Seq.filter ((=) name) |> Seq.length

    interface Metrics.IMetricsSink with
        member _.Record(_name, _value, _tags) = ()
        member _.Increment(name, _tags) = increments.Add name
        member _.SetGauge(_name, _value, _tags) = ()

/// The default event store over a blob whose writes fail for `shouldFail`.
let private defaultStoreFailing (shouldFail: string -> bool) =
    let blob =
        WriteFailingBlobStorage(ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage(), shouldFail)

    PersistentEventStore.PersistentEventStore(blob, EventRetentionPolicy.unlimited) :> IEventStore

/// True for the secondary-index refs, false for the canonical event blob.
let private isIndexRef (blobName: string) =
    blobName.Contains "/_by-type/" || blobName.Contains "/_by-source/"

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

        test "Phase 863 — RefuseAction over the DEFAULT store refuses a failed blob write and counts it" {
            let sink = CountingMetricsSink()

            let auditLog =
                AuditLog.EventStoreAuditLog(
                    defaultStoreFailing (fun _ -> true),
                    silentLogger,
                    (fun () -> sink :> Metrics.IMetricsSink),
                    failurePolicy = RefuseAction
                )
                :> IAuditLog

            Expect.throwsT<AuditLog.AuditWriteRefusedException>
                (fun () -> auditLog.Record("team-acme", sampleAudit "alice") |> Async.RunSynchronously)
                "an Upload that returned Error must refuse the action, exactly as a throwing store does"

            Expect.equal
                (sink.Count AuditLog.AuditMetrics.WriteFailuresTotal)
                1
                "the lost write is counted, not recorded as a success"
        }

        test "Phase 863 — LogAndContinue over the DEFAULT store counts a failed blob write without failing" {
            let sink = CountingMetricsSink()

            let auditLog =
                AuditLog.EventStoreAuditLog(
                    defaultStoreFailing (fun _ -> true),
                    silentLogger,
                    (fun () -> sink :> Metrics.IMetricsSink),
                    failurePolicy = LogAndContinue
                )
                :> IAuditLog

            auditLog.Record("team-acme", sampleAudit "alice") |> Async.RunSynchronously

            Expect.equal
                (sink.Count AuditLog.AuditMetrics.WriteFailuresTotal)
                1
                "the lost write is counted under the default policy too"
        }

        test "Phase 863 — the default store's Write raises a typed EventStoreWriteException on a failed upload" {
            let store = defaultStoreFailing (fun name -> not (isIndexRef name))

            let raised =
                match
                    Async.Catch(store.Write(Events.create "team-acme" "_platform.audit" "UserLoggedIn" "{}"))
                    |> Async.RunSynchronously
                with
                | Choice1Of2() -> None
                | Choice2Of2 ex -> Some ex

            match raised with
            | Some(:? PersistentEventStore.EventStoreWriteException as ex) ->
                Expect.stringContains ex.Message "simulated storage write failure" "the storage error is carried"
            | Some other -> failtestf "expected EventStoreWriteException, got %s" (other.GetType().FullName)
            | None -> failtest "a failed canonical upload must not complete the write"
        }

        test "Phase 863 — a failed INDEX write stays best-effort: the canonical event is written and readable" {
            let store = defaultStoreFailing isIndexRef
            let evt = Events.create "team-acme" "_platform.audit" "UserLoggedIn" "{}"

            // Must not throw — the canonical blob is authoritative.
            store.Write evt |> Async.RunSynchronously

            let all = store.ReadAll "team-acme" |> Async.RunSynchronously
            Expect.equal (all |> List.map _.Id) [ evt.Id ] "the canonical event persisted despite the index failure"
        }
    ]