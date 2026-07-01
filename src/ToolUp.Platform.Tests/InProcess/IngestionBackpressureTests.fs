module ToolUp.Platform.Tests.InProcess.IngestionBackpressureTests

// ─── Phase 303 — ingestion-queue backpressure observability ──────────
//
// Covers the Phase 303 additions:
//   * `IngestionQueue` drop counters — `Dropped` (cumulative) + rolling
//     60s window, bumped by `RecordDrop`; `Policy` default (`DropWrite`);
//     `Block` policy + `EnqueueBlocking`.
//   * `RAGCompose.emitIngestionDrop` — the drop-observability triple: a
//     `KnowledgeIngestionDropped` audit under `_platform.knowledge`, a
//     Warning `SystemMessage` to the uploading user (when attributed), and
//     the queue counter bump. This is the mechanism behind the acceptance
//     criteria (a) one audit per dropped document + (b) one Warning
//     notification; the endpoint side (c) `/health/rag` reads the same
//     queue counters this test exercises.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes

let private run = Async.RunSynchronously

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private mkJob (docId: string) : DocumentIngestionJob = {
    DocumentId = docId
    DocumentName = docId
    Chunks = [ docId + ":chunk:0", { Content = "x"; Metadata = Map.empty } ]
    Scope = Deployment
    ScopeId = "scope-1"
    Container = "deployment"
    OriginatingUserId = None
}

/// Thread-safe capturing IAuditLog double.
type private CapturingAuditLog() =
    let events = ResizeArray<string * AuditEvent>()
    let gate = obj ()

    member _.Events = lock gate (fun () -> events |> List.ofSeq)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock gate (fun () -> events.Add(scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Thread-safe capturing INotificationChannel double.
type private CapturingNotifications() =
    let published = ResizeArray<string * Notification>()
    let gate = obj ()

    member _.Published = lock gate (fun () -> published |> List.ofSeq)

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { lock gate (fun () -> published.Add(scopeId, notification)) }

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()

let private queueCounters =
    testList "IngestionQueue drop counters + policy" [
        test "Enqueue returns false past capacity; RecordDrop bumps cumulative + rolling 60s" {
            let q = IngestionQueue(2)
            Expect.isTrue (q.Enqueue(mkJob "a")) "first accepted"
            Expect.isTrue (q.Enqueue(mkJob "b")) "second accepted"
            Expect.isFalse (q.Enqueue(mkJob "c")) "third rejected — at capacity"
            Expect.equal q.Dropped 0L "no drops recorded until RecordDrop is called"
            q.RecordDrop()
            Expect.equal q.Dropped 1L "cumulative incremented"
            Expect.equal q.DroppedLast60s 1 "rolling window sees the drop"
        }

        test "default overflow policy is DropWrite (back-compat)" {
            Expect.equal (IngestionQueue(2).Policy) DropWrite "default"
        }

        test "Block policy: EnqueueBlocking completes when space is available" {
            let q = IngestionQueue(2, Block)
            Expect.equal q.Policy Block "policy stored"
            run (q.EnqueueBlocking(mkJob "a"))
            Expect.equal q.Count 1 "depth incremented after blocking enqueue"
        }
    ]

let private dropEmission =
    testList "emitIngestionDrop observability triple" [
        test "writes one KnowledgeIngestionDropped audit under _platform.knowledge + bumps queue counter" {
            let q = IngestionQueue(1)
            let audit = CapturingAuditLog()

            run (
                ToolUp.RAG.RAGCompose.emitIngestionDrop
                    (Some(audit :> IAuditLog))
                    None
                    q
                    noopLogger
                    "team:t1"
                    "doc.pdf"
                    3
                    "ingestion queue full after bounded retry"
                    None
            )

            Expect.equal q.Dropped 1L "queue cumulative bumped"

            match audit.Events with
            | [ (scopeId, KnowledgeIngestionDropped p) ] ->
                Expect.equal scopeId KnowledgeSourceModule.value "recorded under _platform.knowledge"
                Expect.equal p.ScopeKey "team:t1" "scope key"
                Expect.equal p.DocId "doc.pdf" "doc id"
                Expect.equal p.ChunkCount 3 "chunk count"
                Expect.equal p.QueueCapacity 1 "capacity read from the queue"
            | other -> failtestf "expected one KnowledgeIngestionDropped, got %A" other
        }

        test "publishes a Warning SystemMessage to the uploading user when attributed" {
            let q = IngestionQueue(1)
            let notifs = CapturingNotifications()

            run (
                ToolUp.RAG.RAGCompose.emitIngestionDrop
                    None
                    (Some(notifs :> INotificationChannel))
                    q
                    noopLogger
                    "team:t1"
                    "doc.pdf"
                    2
                    "queue full"
                    (Some "user-9")
            )

            match notifs.Published with
            | [ (scopeId, Notification.SystemMessage(level, _)) ] ->
                Expect.equal scopeId "user-9" "published to the uploader's scope"
                Expect.equal level SystemMessageLevel.Warning "Warning level"
            | other -> failtestf "expected one Warning SystemMessage, got %A" other
        }

        test "no user attribution ⇒ no notification, but the drop is still counted" {
            let q = IngestionQueue(1)
            let notifs = CapturingNotifications()

            run (
                ToolUp.RAG.RAGCompose.emitIngestionDrop
                    None
                    (Some(notifs :> INotificationChannel))
                    q
                    noopLogger
                    "deployment"
                    "doc.pdf"
                    1
                    "queue full"
                    None
            )

            Expect.isEmpty notifs.Published "no publish without an originating user"
            Expect.equal q.Dropped 1L "drop still counted"
        }
    ]

let tests =
    testList "Phase 303 — ingestion-queue backpressure observability" [ queueCounters; dropEmission ]