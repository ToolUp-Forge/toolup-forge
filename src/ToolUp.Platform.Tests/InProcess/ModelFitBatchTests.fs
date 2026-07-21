// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelFitBatchTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tracing
open ToolUp.ModelProviders.Reference

// ─── Phase 599 — batch fit submission + bulk retrieval ──────────────────
//
// Asserts the batch shape end-to-end against the reference provider + the
// blob-backed registry:
//   * a batch of N runs to N registered outcomes with distinct composite
//     keys, each annotated with the batch id + index, retrievable in bulk
//     by batch id (`IModelRegistry.QueryPage`);
//   * one failing item fails only its own job — siblings' outcomes are
//     intact and the failure is reported as data (`JobResult`);
//   * `QueryPage` pagination is deterministic (same cursor walk → same
//     sequence; disjoint pages tile the ordered match set) and multi-key
//     filters conjoin;
//   * `ModelFitBatch.submit` audits the batch as a unit
//     (`ModelFitBatchSubmitted`, one row) and enqueues one item job per
//     request against the real in-process scheduler (typed validation
//     refusals before any work);
//   * a batch-of-one item is semantically identical to the single-item
//     envelope path (same composite key, diagnostics, verdicts).
//
// Item execution drives `ModelFitBatchItemJobHandler.Execute` directly
// with synthesised `JobContext`s (the IModelFitProviderContract pattern) —
// the scheduler's dispatch loop is hosting-only and not started in tests.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }
    }

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

let private providers =
    ModelFitProviderRegistry [ ReferenceModelFitProvider.create () ]

/// A fresh blob-backed model registry (full default stack, temp-dir
/// isolated) plus its recording audit log.
let private freshRegistry () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-fitbatch-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let blob = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let audit = RecordingAuditLog()
    BlobModelRegistry.create dataObjects (audit :> IAuditLog), audit

let private buildScheduler () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-fitbatch-sched-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore
    let config = ServerConfig.defaults

    JobScheduler.create jobStore eventStore silentChannel config silentLogger (NoOpActivitySink() :> IActivitySink)
    :> IJobScheduler

let private fitRequest (scope: string) (seed: int64) : FitRequest = {
    ScopeId = scope
    DatasetVersion = {
        ScopeId = scope
        DatasetId = "sales-panel"
        Version = 3
    }
    SpecRef = ModelSpecRef.ofPayload """{"opaque":"provider-spec"}"""
    ProviderKind = ReferenceModelFitProvider.Kind
    Seed = seed
    Gates = []
}

let private batchOf (scope: string) (batchId: string) (seeds: int64 list) : FitRequestBatch = {
    BatchId = batchId
    ScopeId = scope
    Requests = seeds |> List.map (fitRequest scope)
}

let private itemCtx (payload: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = "team-1"
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
    Attempt = 1
    Trigger = Manual
    TriggerSource = ScheduledManually "system"
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = payload
    DeadLetterDestination = None
}

/// Execute every item of `batch` through the batch-item handler; returns
/// per-item `JobResult`s in batch order.
let private executeItems (handler: IJobHandler) (batch: FitRequestBatch) = async {
    let results = ResizeArray<JobResult>()

    for index, request in List.indexed batch.Requests do
        let payload =
            ModelFitBatch.serialiseItem {
                BatchId = batch.BatchId
                Index = index
                Request = request
            }

        let! result = handler.Execute(itemCtx payload)
        results.Add result

    return List.ofSeq results
}

let private queryAllByBatch (registry: IModelRegistry) (scope: string) (batchId: string) (limit: int) = async {
    let query = {
        ModelRegistryQuery.any with
            BatchId = Some batchId
    }

    let rec walk (cursor: string option) (acc: ModelArtifact list) = async {
        match! registry.QueryPage(scope, query, cursor, limit) with
        | Error e -> return failtestf "QueryPage failed: %s" (ModelRegistryError.describe e)
        | Ok page ->
            let acc = acc @ page.Artifacts

            match page.NextCursor with
            | Some c -> return! walk (Some c) acc
            | None -> return acc
    }

    return! walk None []
}

let tests =
    testList "ModelFitBatch" [
        testCaseAsync "a batch of N registers N outcomes with distinct composite keys, retrievable by batch id"
        <| async {
            let registry, _ = freshRegistry ()
            let audit = RecordingAuditLog()

            let handler =
                ModelFitBatchItemJobHandler.create providers registry (audit :> IAuditLog) silentLogger

            let batch = batchOf "team-1" "wave-7" [ 1L .. 20L ]
            let! results = executeItems handler batch

            Expect.allEqual results Success "all 20 items run to Success"

            let! artifacts = queryAllByBatch registry "team-1" "wave-7" 7

            Expect.equal (List.length artifacts) 20 "all 20 outcomes retrievable by batch id"

            let keys = artifacts |> List.map (fun a -> a.CompositeKey.Hash) |> List.distinct
            Expect.equal (List.length keys) 20 "per-item composite keys are distinct"

            for a in artifacts do
                Expect.equal
                    (Map.tryFind FitRequestBatch.BatchIdAnnotationKey a.Annotations)
                    (Some "wave-7")
                    "outcome carries the batch id annotation"

                Expect.isSome
                    (Map.tryFind FitRequestBatch.BatchIndexAnnotationKey a.Annotations)
                    "outcome carries the batch index annotation"
        }

        testCaseAsync "one failing item leaves sibling outcomes intact; the failure is data"
        <| async {
            let registry, _ = freshRegistry ()
            let audit = RecordingAuditLog()

            let handler =
                ModelFitBatchItemJobHandler.create providers registry (audit :> IAuditLog) silentLogger

            let good = batchOf "team-1" "wave-8" [ 1L .. 10L ]

            let withBadItem = {
                good with
                    Requests =
                        good.Requests
                        |> List.mapi (fun i r ->
                            if i = 4 then
                                {
                                    r with
                                        ProviderKind = "no-such-provider"
                                }
                            else
                                r)
            }

            let! results = executeItems handler withBadItem

            let failures =
                results
                |> List.indexed
                |> List.filter (fun (_, r) ->
                    match r with
                    | Success -> false
                    | _ -> true)

            Expect.equal (List.length failures) 1 "exactly one item failed"
            Expect.equal (fst failures.Head) 4 "the failing item is the one with the unknown provider"

            match snd failures.Head with
            | PermanentFailure _ -> ()
            | other -> failtestf "an unknown provider kind is a PermanentFailure; got %A" other

            let! artifacts = queryAllByBatch registry "team-1" "wave-8" 100
            Expect.equal (List.length artifacts) 9 "the nine sibling outcomes are intact"
        }

        testCaseAsync "QueryPage paginates deterministically and filters conjoin"
        <| async {
            let registry, _ = freshRegistry ()
            let audit = RecordingAuditLog()

            let handler =
                ModelFitBatchItemJobHandler.create providers registry (audit :> IAuditLog) silentLogger

            let batch = batchOf "team-1" "wave-9" [ 1L .. 25L ]
            let! _ = executeItems handler batch

            let query = {
                ModelRegistryQuery.any with
                    BatchId = Some "wave-9"
            }

            // Walk pages of 10: 10 / 10 / 5, strictly ordered, no overlap.
            let! page1 = registry.QueryPage("team-1", query, None, 10)

            let p1 =
                match page1 with
                | Ok p -> p
                | Error e -> failtestf "page 1 failed: %s" (ModelRegistryError.describe e)

            Expect.equal (List.length p1.Artifacts) 10 "first page holds 10"
            Expect.isSome p1.NextCursor "more pages remain"

            let! page2 = registry.QueryPage("team-1", query, p1.NextCursor, 10)

            let p2 =
                match page2 with
                | Ok p -> p
                | Error e -> failtestf "page 2 failed: %s" (ModelRegistryError.describe e)

            let! page3 = registry.QueryPage("team-1", query, p2.NextCursor, 10)

            let p3 =
                match page3 with
                | Ok p -> p
                | Error e -> failtestf "page 3 failed: %s" (ModelRegistryError.describe e)

            Expect.equal (List.length p2.Artifacts) 10 "second page holds 10"
            Expect.equal (List.length p3.Artifacts) 5 "third page holds the remaining 5"
            Expect.isNone p3.NextCursor "the walk terminates"

            let walked =
                (p1.Artifacts @ p2.Artifacts @ p3.Artifacts)
                |> List.map (fun a -> a.CompositeKey.Hash)

            Expect.equal (List.length (List.distinct walked)) 25 "pages tile without overlap"

            Expect.equal
                walked
                (List.sortWith (fun a b -> String.CompareOrdinal(a, b)) walked)
                "ordinal-ascending order"

            // Re-walking the same cursors yields the identical sequence.
            let! page1' = registry.QueryPage("team-1", query, None, 10)

            match page1' with
            | Ok p -> Expect.equal p.Artifacts p1.Artifacts "same cursor walk, same page"
            | Error e -> failtestf "re-walk failed: %s" (ModelRegistryError.describe e)

            // Conjunctive filters: batch id + a specific spec hash.
            let specHash = (fitRequest "team-1" 1L).SpecRef.SpecHash

            let! filtered = registry.QueryPage("team-1", { query with SpecHashes = [ specHash ] }, None, 100)

            match filtered with
            | Ok p -> Expect.equal (List.length p.Artifacts) 25 "spec-hash filter conjoins (all share one spec)"
            | Error e -> failtestf "filtered query failed: %s" (ModelRegistryError.describe e)

            let! mismatch = registry.QueryPage("team-1", { query with SpecHashes = [ "nope" ] }, None, 100)

            match mismatch with
            | Ok p -> Expect.isEmpty p.Artifacts "a non-matching spec-hash filter empties the page"
            | Error e -> failtestf "mismatch query failed: %s" (ModelRegistryError.describe e)

            // Scope isolation rides the structural partition.
            let! otherScope = registry.QueryPage("team-2", query, None, 100)

            match otherScope with
            | Ok p -> Expect.isEmpty p.Artifacts "another scope sees nothing (GP 4)"
            | Error e -> failtestf "other-scope query failed: %s" (ModelRegistryError.describe e)

            // Malformed limit is a typed refusal.
            match! registry.QueryPage("team-1", query, None, 0) with
            | Error(ModelRegistryError.InvalidQuery _) -> ()
            | other -> failtestf "expected InvalidQuery for limit 0; got %A" other
        }

        testCaseAsync "submit audits the batch as a unit and enqueues one item job per request"
        <| async {
            let scheduler = buildScheduler ()
            let registry, _ = freshRegistry ()
            let audit = RecordingAuditLog()

            // The item handler must be registered for Schedule to accept the
            // handler name (consumer-wired, like the Phase 454 scorer).
            scheduler.RegisterHandler(
                ModelFitBatch.ItemHandlerName,
                ModelFitBatchItemJobHandler.create providers registry (audit :> IAuditLog) silentLogger
            )

            let batch = batchOf "team-1" "wave-10" [ 1L .. 5L ]

            let! submission = ModelFitBatch.submit scheduler (audit :> IAuditLog) "operator-1" batch

            match submission with
            | Error e -> failtestf "submit refused: %s" (FitBatchError.describe e)
            | Ok s ->
                Expect.equal s.ItemCount 5 "submission names the item count"
                Expect.equal (List.length s.ScheduledJobs) 5 "all five items enqueued"
                Expect.isEmpty s.ScheduleFailures "no enqueue failures"

            let batchRows =
                audit.Events
                |> List.choose (fun (scope, e) ->
                    match e with
                    | ModelFitBatchSubmitted p -> Some(scope, p)
                    | _ -> None)

            Expect.equal (List.length batchRows) 1 "exactly one batch-level audit row"
            Expect.equal (snd batchRows.Head).BatchId "wave-10" "the row carries the batch id"
            Expect.equal (snd batchRows.Head).ItemCount 5 "the row carries the item count"
            Expect.equal (fst batchRows.Head) "team-1" "audited under the batch scope"

            let! jobs = scheduler.ListJobs "team-1"

            let itemJobs =
                jobs |> List.filter (fun j -> j.Handler = ModelFitBatch.ItemHandlerName)

            Expect.equal (List.length itemJobs) 5 "one persisted item job per request"

            // Re-submitting the same batch id dedupes via the per-item
            // idempotency key — no duplicate jobs.
            let! resubmission = ModelFitBatch.submit scheduler (audit :> IAuditLog) "operator-1" batch

            match resubmission with
            | Ok s -> Expect.equal (List.length s.ScheduledJobs) 5 "re-submit resolves the same items"
            | Error e -> failtestf "re-submit refused: %s" (FitBatchError.describe e)

            let! jobsAfter = scheduler.ListJobs "team-1"

            let itemJobsAfter =
                jobsAfter |> List.filter (fun j -> j.Handler = ModelFitBatch.ItemHandlerName)

            Expect.equal (List.length itemJobsAfter) 5 "idempotent re-submit adds no duplicate jobs"
        }

        testCaseAsync "batch validation refuses empty / uncorrelatable / cross-scope batches as typed data"
        <| async {
            let scheduler = buildScheduler ()
            let audit = RecordingAuditLog()

            let refused (batch: FitRequestBatch) = async {
                match! ModelFitBatch.submit scheduler (audit :> IAuditLog) "operator-1" batch with
                | Error e -> return e
                | Ok _ -> return failtest "expected a typed refusal"
            }

            let! empty = refused (batchOf "team-1" "b" [])

            Expect.equal empty FitBatchError.EmptyBatch "empty batch refused"

            let! unnamed = refused (batchOf "team-1" "" [ 1L ])
            Expect.equal unnamed FitBatchError.MissingBatchId "missing batch id refused"

            let crossScope = {
                batchOf "team-1" "b" [ 1L; 2L ] with
                    Requests = [ fitRequest "team-1" 1L; fitRequest "team-2" 2L ]
            }

            let! mismatch = refused crossScope

            match mismatch with
            | FitBatchError.ScopeMismatch(1, "team-2") -> ()
            | other -> failtestf "expected ScopeMismatch(1, team-2); got %A" other

            Expect.isEmpty audit.Events "a refused submission audits nothing (deny early, deny typed)"
        }

        testCaseAsync "a batch-of-one item is semantically the single-item envelope path"
        <| async {
            let registry, _ = freshRegistry ()
            let audit = RecordingAuditLog()

            let handler =
                ModelFitBatchItemJobHandler.create providers registry (audit :> IAuditLog) silentLogger

            let request = fitRequest "team-1" 77L

            // Single-item path.
            let! single = ModelFitEnvelope.runFit providers (RecordingAuditLog() :> IAuditLog) request

            let singleOutcome =
                match single with
                | Ok o -> o
                | Error e -> failtestf "single-item fit failed: %s" (ModelFitError.describe e)

            // Batch-of-one path.
            let batch = batchOf "team-1" "wave-single" [ 77L ]
            let! results = executeItems handler batch
            Expect.allEqual results Success "the batch-of-one item succeeds"

            let! artifacts = queryAllByBatch registry "team-1" "wave-single" 10

            match artifacts with
            | [ artifact ] ->
                Expect.equal artifact.CompositeKey singleOutcome.CompositeKey "identical composite key"
                Expect.equal artifact.Diagnostics singleOutcome.Diagnostics "identical diagnostics"
                Expect.equal artifact.GateVerdicts singleOutcome.GateVerdicts "identical gate verdicts"
                Expect.equal artifact.ArtifactRef singleOutcome.ArtifactRef "identical artifact reference"
            | other -> failtestf "expected exactly one registered artifact; got %d" (List.length other)
        }
    ]