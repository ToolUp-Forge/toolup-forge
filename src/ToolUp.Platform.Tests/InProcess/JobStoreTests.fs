module ToolUp.Platform.Tests.InProcess.JobStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── BlobJobStore — IJobStore contract binding ───────────────────
//
// Binds the `IJobStore` contract pack to the blob-backed default
// implementation, running against `LocalFileStorage` rooted in a
// fresh temp directory per factory call. Cross-test isolation is
// thus structural: each factory call gets its own filesystem
// subtree, two scope ids are GUID-suffixed, no test sees another's
// blobs.

let private tempStorage () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-jobstore-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(root) |> ignore
    LocalFileStorage.LocalFileStorage(root) :> IBlobStorage

let private uniqueScope () =
    "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

let private mkDefinition (scopeId: string) (handler: string) (nextRunAt: DateTime option) : JobDefinition = {
    JobId = Guid.NewGuid()
    ScopeId = scopeId
    Handler = handler
    Payload = "{}"
    Trigger = CronTrigger "0 9 * * *"
    Idempotency = None
    RetryPolicy = JobRetryPolicy.defaults
    ShardKey = None
    Precision = Minute
    Status = Active
    CreatedAt = DateTime.UtcNow
    CreatedBy = "alice"
    NextRunAt = nextRunAt
    LastRunAt = None
    LastRunStatus = None
    LastRunError = None
    ConsecutiveFailures = 0
    Tags = Map.empty
}

let contractTests =
    let factory () =
        let storage = tempStorage ()
        let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
        let store = JobStore.create storage eventStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    IJobStoreContract.tests "BlobJobStore" factory

/// Phase 9f index-consistency + Rebuild round-trip tests. The
/// store maintains an idempotency-key index and a next-run bucket
/// index; both must survive drift, recover via Rebuild, and shed
/// stale bucket entries on status / NextRunAt transitions.
let indexTests =
    testList "BlobJobStore — secondary indexes (Phase 9f)" [

        testCaseAsync "Update from Active to Cancelled removes the bucket entry"
        <| async {
            let storage = tempStorage ()
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = JobStore.BlobJobStore(storage, eventStore)
            let storeI = store :> IJobStore
            let scope = uniqueScope ()

            let nextRun = DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)
            let job = mkDefinition scope "h1" (Some nextRun)
            do! storeI.Save job

            // Bucket entry exists.
            let! preNames = storage.List("_platform", $"jobs/{scope}/_next-run/{nextRun:yyyyMMddHHmm}/")

            Expect.hasLength preNames 1 "bucket entry written"

            // Cancel via Update.
            let cancelled = { job with Status = Cancelled }
            do! storeI.Update cancelled

            let! postNames = storage.List("_platform", $"jobs/{scope}/_next-run/{nextRun:yyyyMMddHHmm}/")

            Expect.isEmpty postNames "bucket entry removed on Cancel"
        }

        testCaseAsync "Update with new NextRunAt removes prior bucket"
        <| async {
            let storage = tempStorage ()
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = JobStore.BlobJobStore(storage, eventStore)
            let storeI = store :> IJobStore
            let scope = uniqueScope ()

            let originalRun = DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)
            let newRun = DateTime(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc)

            let job = mkDefinition scope "h1" (Some originalRun)
            do! storeI.Save job

            // Reschedule.
            let updated = { job with NextRunAt = Some newRun }
            do! storeI.Update updated

            let! oldBucket = storage.List("_platform", $"jobs/{scope}/_next-run/{originalRun:yyyyMMddHHmm}/")

            let! newBucket = storage.List("_platform", $"jobs/{scope}/_next-run/{newRun:yyyyMMddHHmm}/")

            Expect.isEmpty oldBucket "prior bucket removed"
            Expect.hasLength newBucket 1 "new bucket entry written"
        }

        testCaseAsync "DueJobs uses the next-run index — drift hides due jobs"
        <| async {
            let storage = tempStorage ()
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = JobStore.BlobJobStore(storage, eventStore)
            let storeI = store :> IJobStore
            let scope = uniqueScope ()

            let now = DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)

            let due = mkDefinition scope "due" (Some(now.AddMinutes(-1.0)))
            do! storeI.Save due

            let! preDue = storeI.DueJobs(scope, now)
            Expect.hasLength preDue 1 "due job found via index"

            // Wipe the next-run index ref.
            let! refs = storage.List("_platform", $"jobs/{scope}/_next-run/")

            for r in refs do
                let! _ = storage.Delete("_platform", r)
                ()

            let! postDue = storeI.DueJobs(scope, now)
            Expect.isEmpty postDue "due job hidden by drift"

            // Rebuild restores it.
            let! _ = store.Rebuild scope
            let! recoveredDue = storeI.DueJobs(scope, now)
            Expect.hasLength recoveredDue 1 "due job recovered after Rebuild"
        }

        testCaseAsync "Rebuild restores idempotency index"
        <| async {
            let storage = tempStorage ()
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = JobStore.BlobJobStore(storage, eventStore)
            let storeI = store :> IJobStore
            let scope = uniqueScope ()

            let job = {
                mkDefinition scope "h" None with
                    Idempotency = Some { Key = "daily"; TtlSeconds = 3600 }
            }

            do! storeI.Save job

            // Wipe the idempotency index.
            let! refs = storage.List("_platform", $"jobs/{scope}/_idempotency/")

            for r in refs do
                let! _ = storage.Delete("_platform", r)
                ()

            let! preFind = storeI.FindByIdempotencyKey(scope, "daily", 3600, DateTime.UtcNow)
            Expect.equal preFind None "idempotency lookup misses after wipe"

            let! _ = store.Rebuild scope
            let! postFind = storeI.FindByIdempotencyKey(scope, "daily", 3600, DateTime.UtcNow)
            Expect.equal postFind (Some job.JobId) "idempotency lookup recovers after Rebuild"
        }

        testCaseAsync "Rebuild is idempotent"
        <| async {
            let storage = tempStorage ()
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = JobStore.BlobJobStore(storage, eventStore)
            let storeI = store :> IJobStore
            let scope = uniqueScope ()

            let job = {
                mkDefinition scope "h" (Some(DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc))) with
                    Idempotency = Some { Key = "k"; TtlSeconds = 3600 }
            }

            do! storeI.Save job

            let! first = store.Rebuild scope
            let! second = store.Rebuild scope

            Expect.equal first second "same count both runs"

            // No duplicates — bucket should still hold one ref, not two.
            let! buckets = storage.List("_platform", $"jobs/{scope}/_next-run/")
            Expect.hasLength buckets 1 "bucket has single ref after double rebuild"
        }

        testCaseAsync "IndexConsistencyCheck reports zero drift on a freshly-written store"
        <| async {
            let storage = tempStorage ()
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = JobStore.BlobJobStore(storage, eventStore)
            let storeI = store :> IJobStore
            let scope = uniqueScope ()

            let job1 = {
                mkDefinition scope "h1" (Some(DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc))) with
                    Idempotency = Some { Key = "key1"; TtlSeconds = 3600 }
            }

            let job2 = {
                mkDefinition scope "h2" (Some(DateTime(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc))) with
                    Idempotency = Some { Key = "key2"; TtlSeconds = 3600 }
            }

            do! storeI.Save job1
            do! storeI.Save job2

            let! entries = store.IndexConsistencyCheck(scope, 20)

            Expect.hasLength entries 2 "one entry per index"

            for e in entries do
                Expect.equal e.OrphanedIndexEntries 0 $"no orphans in {e.IndexName}"
                Expect.equal e.UnindexedCanonicals 0 $"no unindexed canonicals in {e.IndexName}"
        }
    ]

let tests = testList "BlobJobStore — all" [ contractTests; indexTests ]