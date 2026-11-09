module ToolUp.Platform.Infra.UsageLog

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Usage
open ToolUp.Platform.Infra.Usage

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: always `_platform`.
// Rollup blob: `usage/{scopeId}/{yyyy-MM-dd}.json`
//
// One blob per (scope, UTC date). The blob is a JSON array of
// `UsageRecord`s. Cross-team isolation is structural — `ScopeId` is
// part of the blob path, never enumerated outside the requested
// scope. `Aggregate` and `Query` `List` only the requested scope's
// prefix.
//
// On flush, the daemon groups the drained batch by `(ScopeId,
// Timestamp.Date)`, dispatches per-bucket flushes in parallel via
// `Async.Parallel`, and merges each into its rollup with one guarded
// update through `BlobDocumentStore` (its write stripes queue same-bucket
// flushes in this process; a conditional backend's CAS orders them across
// processes). Cross-scope flushes run concurrently — no head-of-line
// blocking.
//
// **A rollup that cannot be read is never merged into.** A transient read
// failure used to read as an empty day and write the batch over every
// record the day held — billing data erased by a blip. Now the flush
// writes nothing and logs, and the records are dropped as a failed write's
// are. A rollup whose bytes do not decode is set aside (copied beside
// itself, for an operator to repair) and the day starts again, so new
// records keep landing. A query over a rollup that cannot be read or
// decoded REFUSES (`DocumentUnavailableException`) rather than answering a
// usage total that silently omits a day.
//
// **Idempotency under retry.** Flush merges incoming records into
// the existing array de-duped by `RecordId` so a crash-mid-write
// retry does not double-count.
//
// **Wait, not DropWrite.** The bounded channel uses
// `BoundedChannelFullMode.Wait` — billing data dropped silently is
// worse than a 50ms producer stall. The producer waits up to
// `BatchFlushPolicy.ProducerWaitTimeout` and only on timeout drops
// the record (with a logged + counted event under
// `_platform.usage`).

let private platformContainer = ScopeKey.PlatformContainer

let private scopePrefix (scopeId: string) = $"usage/{scopeId}/"

let private rollupBlobName (scopeId: string) (date: DateTime) =
    let dateStr = date.ToUniversalTime().ToString("yyyy-MM-dd")
    $"{scopePrefix scopeId}{dateStr}.json"

let private dateKey (timestamp: DateTime) = timestamp.ToUniversalTime().Date

let private extractDateFromBlobName (blobName: string) (scopeId: string) : DateTime option =
    let prefix = scopePrefix scopeId

    if blobName.StartsWith prefix && blobName.EndsWith ".json" then
        let middle =
            blobName.Substring(prefix.Length, blobName.Length - prefix.Length - ".json".Length)

        match
            DateTime.TryParseExact(
                middle,
                "yyyy-MM-dd",
                Globalization.CultureInfo.InvariantCulture,
                Globalization.DateTimeStyles.AssumeUniversal
                ||| Globalization.DateTimeStyles.AdjustToUniversal
            )
        with
        | true, d -> Some d
        | _ -> None
    else
        None

// ─── Serialisation ───────────────────────────────────────────────
//
// `BlobCodec.json` over `PlatformJson`, the bytes the rollups have always
// carried. An empty blob, and a `null` document, are an empty day.

let private rollupCodec: BlobCodec<UsageRecord list> = {
    Encode = BlobCodec.json<UsageRecord list>.Encode
    Decode =
        fun bytes ->
            if bytes.Length = 0 then
                Ok []
            else
                // `Option.ofObj` requires `'T : null`, which F# lists do
                // not satisfy: box, then null-check.
                BlobCodec.json<UsageRecord list>.Decode bytes
                |> Result.map (fun records ->
                    match box records with
                    | null -> []
                    | _ -> records)
}

let private rollupStore (blobStorage: IBlobStorage) (logger: ILogger) =
    BlobDocumentStore<UsageRecord list>(blobStorage, rollupCodec, logger)

// ─── Default in-memory implementation (tests / dev) ──────────────
//
// `InMemoryUsageLog` keeps records in a `ConcurrentDictionary` keyed
// by `ScopeId`. Used by the contract pack and any test harness that
// wants the API surface without the durability cost. NOT registered
// by `compose` — `FeatureMode.Enabled` always picks the blob-backed
// default; the `IUsageLog` interface accepts any implementation.

type InMemoryUsageLog() =
    let store = ConcurrentDictionary<string, ResizeArray<UsageRecord>>()

    let scopeBucket (scopeId: string) =
        store.GetOrAdd(scopeId, fun _ -> ResizeArray<UsageRecord>())

    let snapshot (scopeId: string) =
        match store.TryGetValue scopeId with
        | true, list -> lock list (fun () -> list.ToArray() |> Array.toList)
        | _ -> []

    interface IUsageLog with
        member _.Record(record) = async {
            let bucket = scopeBucket record.ScopeId

            lock bucket (fun () ->
                if not (bucket |> Seq.exists (fun r -> r.RecordId = record.RecordId)) then
                    bucket.Add record)
        }

        member _.Query(scopeId, resourceKind, range) = async {
            let all = snapshot scopeId

            return
                all
                |> List.filter (fun r ->
                    match resourceKind with
                    | Some k -> r.ResourceKind = k
                    | None -> true)
                |> List.filter (fun r ->
                    match range with
                    | Some(fromTs, toTs) -> r.Timestamp >= fromTs && r.Timestamp <= toTs
                    | None -> true)
                |> List.sortBy _.Timestamp
        }

        member this.Aggregate(scopeId, grouping) = async {
            let! records = (this :> IUsageLog).Query(scopeId, None, None)

            let bucketKey (r: UsageRecord) =
                match grouping with
                | ByDay -> r.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd")
                | ByMonth -> r.Timestamp.ToUniversalTime().ToString("yyyy-MM")
                | ByResourceKind -> r.ResourceKind
                | ByUser -> r.Metadata |> Map.tryFind "userId" |> Option.defaultValue ""

            return
                records
                |> List.groupBy bucketKey
                |> List.map (fun (k, rs) -> k, rs |> List.sumBy _.Quantity)
                |> Map.ofList
        }

// ─── Blob-backed implementation + flusher ────────────────────────

[<Literal>]
let private UsageSourceModule = "_platform.usage"

/// Backpressure / drop telemetry — surfaced through the constructor's
/// `ILogger` (warn) and counted via `IEventStore` when one is wired
/// in (the dispatcher takes an optional event store; mainline
/// production wires one, tests can omit it).
type private DropCounter() =
    let mutable drops: int64 = 0L
    member _.Increment() = Interlocked.Increment(&drops) |> ignore
    member _.Snapshot() = Interlocked.Read(&drops)

/// `BackgroundService` that drains the bounded channel into per-
/// (scope, day) rollup blobs.
///
/// **Single-instance limitation (Phase 9c follow-up).** The buffer is
/// in-process; running multiple silos with the same `IUsageLog`
/// registration would each accept their own writes and flush
/// independently. Per-(scope, day) `RecordId` dedup means duplicate
/// flushes from a second instance won't double-count, but throughput
/// scaling under multi-silo deployments is the migration path. The
/// distributed companion lives alongside the Phase 9c half 2 work.
type UsageBatchFlusher
    (blobStorage: IBlobStorage, policy: BatchFlushPolicy, logger: ILogger, eventStore: IEventStore option) =
    inherit BackgroundService()

    let queue =
        let opts =
            BoundedChannelOptions(
                policy.ChannelCapacity,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            )

        Channel.CreateBounded<UsageRecord> opts

    let dropCounter = DropCounter()

    let rollups = rollupStore blobStorage logger

    /// Merge `incoming` into the day's rollup by `RecordId`, in one guarded
    /// update.
    let flushBucket (scopeId: string) (date: DateTime) (incoming: UsageRecord list) = async {
        let path = DocPath.ofString (rollupBlobName scopeId date)

        let! result =
            rollups.UpdateOrRestart(
                ScopeKey.platform,
                path,
                fun stored ->
                    let existingList = Option.defaultValue [] stored
                    let existingIds = existingList |> List.map _.RecordId |> Set.ofList

                    let merged =
                        existingList
                        @ (incoming |> List.filter (fun r -> not (Set.contains r.RecordId existingIds)))

                    BlobUpdate.Write(merged, ())
            )

        match result with
        | Ok() -> ()
        | Error e ->
            logger.Warn(
                sprintf
                    "[UsageLog] flush failed scope=%s date=%s records=%d err=%s"
                    scopeId
                    (date.ToString "yyyy-MM-dd")
                    incoming.Length
                    (BlobMapStoreError.describe e)
            )
    }

    /// Group the drained batch by (ScopeId, Date) and dispatch in
    /// parallel. A single noisy bucket cannot starve quieter ones —
    /// per-bucket locks serialise within bucket only.
    let flushBatch (records: UsageRecord list) = async {
        if records.IsEmpty then
            return ()
        else
            let buckets = records |> List.groupBy (fun r -> r.ScopeId, dateKey r.Timestamp)

            let! _ = buckets |> List.map (fun ((s, d), rs) -> flushBucket s d rs) |> Async.Parallel

            return ()
    }

    /// Drain up to `maxCount` records from the channel without
    /// blocking. Returns whatever is immediately available.
    let drainImmediate (maxCount: int) =
        let acc = ResizeArray<UsageRecord>(min maxCount 256)

        let mutable continueDraining = true

        while continueDraining && acc.Count < maxCount do
            match queue.Reader.TryRead() with
            | true, r -> acc.Add r
            | _ -> continueDraining <- false

        acc |> Seq.toList

    /// Producer side — public API for the `IUsageLog` interface.
    /// Waits up to `policy.ProducerWaitTimeout`; on timeout drops
    /// the record and increments the drop counter. Producer never
    /// throws.
    member _.Enqueue(record: UsageRecord) = async {
        try
            use cts = new CancellationTokenSource(policy.ProducerWaitTimeout)

            let! ok =
                queue.Writer.WriteAsync(record, cts.Token).AsTask()
                |> Async.AwaitTask
                |> Async.Catch

            match ok with
            | Choice1Of2 _ -> ()
            | Choice2Of2 _ ->
                dropCounter.Increment()

                logger.Warn(
                    sprintf
                        "[UsageLog] event=record_dropped scope=%s kind=%s reason=channel_full"
                        record.ScopeId
                        record.ResourceKind
                )

                match eventStore with
                | Some store ->
                    let payload =
                        sprintf "{\"scopeId\":\"%s\",\"kind\":\"%s\"}" record.ScopeId record.ResourceKind

                    let evt =
                        Events.create record.ScopeId UsageSourceModule "UsageRecordDropped" payload

                    do! store.Write evt
                | None -> ()
        with ex ->
            dropCounter.Increment()
            logger.Warn(sprintf "[UsageLog] event=enqueue_threw scope=%s: %s" record.ScopeId ex.Message)
    }

    member _.DropCount = dropCounter.Snapshot()

    /// Drain everything currently buffered and flush to blob storage.
    /// Used by `StopAsync` for graceful shutdown.
    member this.DrainAndFlush() = async {
        let drained = drainImmediate Int32.MaxValue

        if not drained.IsEmpty then
            do! flushBatch drained
    }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // Tick on either the timer OR a count-trigger. Channel
            // reads block until a record is available; the timer is
            // a separate trigger to bound flush latency.
            while not stoppingToken.IsCancellationRequested do
                try
                    let timerTask = Task.Delay(policy.FlushInterval, stoppingToken)
                    let waitTask = queue.Reader.WaitToReadAsync(stoppingToken).AsTask()
                    let! _ = Task.WhenAny(timerTask, waitTask)

                    if not stoppingToken.IsCancellationRequested then
                        let drained = drainImmediate policy.FlushAtCount

                        if not drained.IsEmpty then
                            do! Async.StartAsTask(flushBatch drained, cancellationToken = stoppingToken)
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error("[UsageLog] event=flush_loop_error", Some ex)

            // Final drain on shutdown — graceful redeploy must not
            // lose buffered records. Mirrors the contract pack's
            // `flush-on-shutdown` test.
            do! Async.StartAsTask(this.DrainAndFlush())
        }
        :> Task

    override this.StopAsync(cancellationToken: CancellationToken) =
        // Complete the writer so producers can no longer enqueue;
        // ExecuteAsync's tail drains everything still buffered before
        // its task completes. F#'s `task { }` builder can't carry a
        // `base.X` call across the closure; doing the side-effect
        // directly and chaining the base task keeps the `base`
        // reference in the override's direct scope (mirrors
        // `TransactionalDispatcher.StopAsync`).
        queue.Writer.TryComplete() |> ignore
        base.StopAsync(cancellationToken)

/// Blob-backed `IUsageLog`. `Record` enqueues into the flusher; query
/// paths read directly from the rollup blobs (the flusher is a
/// write-side concern only).
type BlobUsageLog(blobStorage: IBlobStorage, flusher: UsageBatchFlusher) =

    let rollups =
        rollupStore blobStorage (JsonConsoleLogger.JsonConsoleLogger() :> ILogger)

    let listScopeBlobs (scopeId: string) = async {
        let! names = blobStorage.List(platformContainer, scopePrefix scopeId)
        // Filter to JSON rollup files; defensive against future
        // sibling files under the same prefix (e.g. an `_index/`).
        return names |> List.filter (fun n -> n.EndsWith ".json")
    }

    let blobsInRange (scopeId: string) (range: (DateTime * DateTime) option) = async {
        let! all = listScopeBlobs scopeId

        match range with
        | None -> return all
        | Some(fromTs, toTs) ->
            let fromDate = fromTs.ToUniversalTime().Date
            let toDate = toTs.ToUniversalTime().Date

            return
                all
                |> List.filter (fun name ->
                    match extractDateFromBlobName name scopeId with
                    | Some d -> d >= fromDate && d <= toDate
                    | None -> false)
    }

    /// Every record in the scope's rollups for `range`. A rollup that
    /// cannot be read or decoded refuses the query: a usage total is a
    /// bill, and one that silently omits a day is wrong.
    let readAllRecords (scopeId: string) (range: (DateTime * DateTime) option) = async {
        let! blobs = blobsInRange scopeId range

        let! rows =
            rollups.ReadMany(
                ScopeKey.platform,
                blobs |> List.map DocPath.ofString,
                BlobDocumentStoreLimits.DefaultListDegree
            )

        return rows |> List.collect (snd >> DocumentUnavailable.orRaise)
    }

    interface IUsageLog with
        member _.Record(record) = flusher.Enqueue record

        member _.Query(scopeId, resourceKind, range) = async {
            let! records = readAllRecords scopeId range

            return
                records
                |> List.filter (fun r ->
                    match resourceKind with
                    | Some k -> r.ResourceKind = k
                    | None -> true)
                |> List.filter (fun r ->
                    match range with
                    | Some(fromTs, toTs) -> r.Timestamp >= fromTs && r.Timestamp <= toTs
                    | None -> true)
                |> List.sortBy _.Timestamp
        }

        member _.Aggregate(scopeId, grouping) = async {
            let! records = readAllRecords scopeId None

            let bucketKey (r: UsageRecord) =
                match grouping with
                | ByDay -> r.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd")
                | ByMonth -> r.Timestamp.ToUniversalTime().ToString("yyyy-MM")
                | ByResourceKind -> r.ResourceKind
                | ByUser -> r.Metadata |> Map.tryFind "userId" |> Option.defaultValue ""

            return
                records
                |> List.groupBy bucketKey
                |> List.map (fun (k, rs) -> k, rs |> List.sumBy _.Quantity)
                |> Map.ofList
        }