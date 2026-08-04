// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.IngestionQueues.Redis.RedisIngestionQueueStore

open System
open System.Text.Json
open StackExchange.Redis
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.RAG.IngestionTypes

// ─── Phase 509 — Redis durable ingestion-queue companion ─────────────
//
// The shipped ingestion queue is a `System.Threading.Channels` channel:
// process-local, with no leasing and no redelivery. Two consequences the
// SDK has carried as known gaps:
//
//   * A restart mid-ingestion loses every queued document. The KB index
//     entry survives in a non-terminal status, but the JOB does not, so
//     the document is never indexed and nothing retries it.
//   * `RagIngestionInstanceValidator` REFUSES `ReplicaCount > 1`,
//     because only the replica that handled an upload can drain it.
//     Its own text calls the distributed path "a roadmap item".
//
// This companion is that path. It implements `IIngestionQueueStore` over
// Redis, so the queue outlives the process and N replicas drain ONE
// queue.
//
// **Why this is not double-processing.** The claim is a single `LMOVE`
// from the pending list to the processing list. `LMOVE` is atomic, so
// exactly one caller receives a given job id however many replicas race
// — the property the whole multi-replica lift rests on. Everything else
// (attempt counting, lease expiry, redelivery) is bookkeeping layered on
// top of that one guarantee.
//
// **At-least-once, never at-most-once.** A drainer that dies mid-document
// leaves its lease key to expire; `ReclaimExpired` then returns the job
// to the pending list. Ingestion is batch-idempotent — re-indexing a
// chunk overwrites the same vector-store id — so a redelivery costs
// embedding spend, never corpus corruption. A job that has consumed its
// delivery-attempt budget is dropped rather than redelivered forever;
// the drop is counted so a poison document is visible instead of
// spinning a replica.
//
// **GP 1** — `StackExchange.Redis` is isolated here and never reaches
// `ToolUp.Platform.*`. **GP 2** — Redis is OSS. **GP 13** — a deployment
// that composes nothing keeps the in-memory channel byte for byte.

/// Raised for a misconfiguration detected at `create` time — an invalid
/// option or an unusable connection string. Never raised from the
/// `IIngestionQueueStore` members, which degrade rather than throw.
exception RedisIngestionQueueException of message: string

/// Compose-time configuration. Every field has a default that is safe
/// for a deployment that thinks about none of them.
type RedisIngestionQueueOptions = {
    /// Namespace for every key this companion writes. Kept distinct from
    /// `toolup:embeddings:` / `toolup:notifications:` so one Redis
    /// instance can back several substrates. Glob metacharacters are
    /// refused — the same reasoning as the embedding cache: an operator
    /// inspecting `redis-cli` must be able to trust the prefix.
    KeyPrefix: string
    /// How many times one document may be delivered before it is
    /// dropped. `3` means: first delivery plus two redeliveries. A
    /// document that crashes its drainer every time is a poison message;
    /// redelivering it forever would keep one replica permanently busy
    /// failing.
    MaxDeliveryAttempts: int
    /// Redis logical database. `-1` is StackExchange.Redis' "whatever the
    /// connection string selected", which is the right default.
    Database: int
}

module RedisIngestionQueueOptions =
    /// Longest accepted `KeyPrefix` — a sanity bound, not a protocol limit.
    [<Literal>]
    let MaxKeyPrefixLength = 128

    /// `toolup:ingestion`, 3 delivery attempts, default database.
    let defaults: RedisIngestionQueueOptions = {
        KeyPrefix = "toolup:ingestion"
        MaxDeliveryAttempts = 3
        Database = -1
    }

    /// A prefix safe to interpolate into a key pattern. Glob
    /// metacharacters are the load-bearing exclusion.
    let isSafeKeyPrefix (prefix: string) =
        not (String.IsNullOrWhiteSpace prefix)
        && prefix.Length <= MaxKeyPrefixLength
        && prefix
           |> Seq.forall (fun c ->
               not (Char.IsWhiteSpace c)
               && not (Char.IsControl c)
               && c <> '*'
               && c <> '?'
               && c <> '['
               && c <> ']'
               && c <> '\\')

    /// Validate every option before any I/O. Returns the first problem
    /// as a message an operator can act on.
    let validate (options: RedisIngestionQueueOptions) : Result<unit, string> =
        if not (isSafeKeyPrefix options.KeyPrefix) then
            Error(
                sprintf
                    "KeyPrefix '%s' is not usable: it must be 1-%d non-whitespace, non-control characters and must not contain the glob metacharacters * ? [ ] \\."
                    options.KeyPrefix
                    MaxKeyPrefixLength
            )
        elif options.MaxDeliveryAttempts < 1 then
            Error(
                sprintf
                    "MaxDeliveryAttempts must be at least 1; got %d. Zero would drop every document on its first delivery."
                    options.MaxDeliveryAttempts
            )
        elif options.Database < -1 then
            Error(sprintf "Database must be -1 (connection default) or a non-negative index; got %d." options.Database)
        else
            Ok()

/// The key layout. Four keys plus one short-lived key per in-flight job.
module Key =
    /// Bumped only when the layout changes, so an old-layout queue is
    /// left alone rather than half-read by a new build.
    [<Literal>]
    let SchemaVersion = "1"

    let private at (options: RedisIngestionQueueOptions) (suffix: string) =
        String.Join(":", [| options.KeyPrefix; SchemaVersion; suffix |])

    /// LIST of job ids awaiting a drainer (head = next out).
    let pending options = at options "pending"

    /// LIST of job ids claimed by some drainer.
    let processing options = at options "processing"

    /// HASH jobId → framed payload.
    let jobs options = at options "jobs"

    /// HASH jobId → delivery-attempt count.
    let attempts options = at options "attempts"

    /// STRING with a TTL — its EXISTENCE is the live lease. Absence with
    /// the id still in `processing` is exactly "the drainer died".
    let lease options (jobId: string) = at options ("lease:" + jobId)

    /// Counter of jobs dropped after exhausting their attempt budget.
    let dropped options = at options "dropped"

/// Payload framing. The whole point is that a DIFFERENT process reads
/// what this one wrote, so the frame is explicit rather than "whatever
/// `JsonSerializer` happened to emit": a magic marker and a version
/// precede the JSON, and a payload that does not carry them is reported
/// as foreign rather than parsed hopefully.
module Codec =
    /// `TUIQ` — ToolUp Ingestion Queue.
    [<Literal>]
    let Magic = "TUIQ"

    [<Literal>]
    let FormatVersion = 1

    let private jsonOptions = FableConverters.create ()

    let encode (job: DocumentIngestionJob) : string =
        let json = JsonSerializer.Serialize(job, jsonOptions)
        sprintf "%s%d:%s" Magic FormatVersion json

    /// Decode a stored payload. Every rejection names what was wrong, so
    /// a corrupt or foreign value is dropped loudly rather than silently
    /// becoming a document that never indexes.
    let decode (payload: string) : Result<DocumentIngestionJob, string> =
        if String.IsNullOrEmpty payload then
            Error "payload is empty"
        else
            let prefix = sprintf "%s%d:" Magic FormatVersion

            if not (payload.StartsWith(prefix, StringComparison.Ordinal)) then
                Error(
                    sprintf
                        "payload does not start with the ToolUp ingestion-queue frame '%s' — the key namespace is shared with another writer, or was written by an incompatible build"
                        prefix
                )
            else
                try
                    let json = payload.Substring prefix.Length

                    let job = JsonSerializer.Deserialize<DocumentIngestionJob>(json, jsonOptions)

                    // STJ can hand back a null for a record when the JSON
                    // body is literally `null`; `box` is the only way to
                    // test it, since the F# type has no null as a value.
                    if isNull (box job) then
                        Error "payload deserialised to null"
                    else
                        Ok job
                with ex ->
                    Error(sprintf "payload is not a readable DocumentIngestionJob: %s" ex.Message)

/// Redis-backed `IIngestionQueueStore`. Takes an `IConnectionMultiplexer`
/// the caller owns — StackExchange.Redis wants one multiplexer per
/// process, so a deployment already running `RedisNotificationChannel` /
/// `RedisEmbeddingCache` should pass the SAME instance.
///
/// `ownsMultiplexer` is set only by the connection-string factory, which
/// created the multiplexer and therefore must dispose it.
type RedisIngestionQueueStore
    (
        multiplexer: IConnectionMultiplexer,
        options: RedisIngestionQueueOptions,
        ownsMultiplexer: bool,
        logger: ILogger option
    ) =

    let db = multiplexer.GetDatabase options.Database

    let pendingKey = RedisKey.op_Implicit (Key.pending options)
    let processingKey = RedisKey.op_Implicit (Key.processing options)
    let jobsKey = RedisKey.op_Implicit (Key.jobs options)
    let attemptsKey = RedisKey.op_Implicit (Key.attempts options)
    let droppedKey = RedisKey.op_Implicit (Key.dropped options)

    let warn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    let leaseKey (jobId: string) =
        RedisKey.op_Implicit (Key.lease options jobId)

    /// Remove every trace of a job. Used by both `Complete` (success) and
    /// the drop path (attempt budget exhausted).
    let purge (jobId: string) = async {
        let field = RedisValue.op_Implicit jobId
        do! db.ListRemoveAsync(processingKey, field, 0L) |> Async.AwaitTask |> Async.Ignore
        do! db.HashDeleteAsync(jobsKey, field) |> Async.AwaitTask |> Async.Ignore
        do! db.HashDeleteAsync(attemptsKey, field) |> Async.AwaitTask |> Async.Ignore
        do! db.KeyDeleteAsync(leaseKey jobId) |> Async.AwaitTask |> Async.Ignore
    }

    /// Return a claimed job to the pending list, or drop it when it has
    /// used its delivery-attempt budget.
    let requeueOrDrop (jobId: string) (attempts: int) = async {
        if attempts >= options.MaxDeliveryAttempts then
            warn
                $"RedisIngestionQueueStore: document job {jobId} exhausted its {options.MaxDeliveryAttempts} delivery attempts and was dropped. The document is saved but unindexed — re-upload it, and check the ingestion logs for why every attempt failed."

            do! purge jobId
            do! db.StringIncrementAsync droppedKey |> Async.AwaitTask |> Async.Ignore
        else
            let field = RedisValue.op_Implicit jobId
            do! db.ListRemoveAsync(processingKey, field, 0L) |> Async.AwaitTask |> Async.Ignore
            do! db.KeyDeleteAsync(leaseKey jobId) |> Async.AwaitTask |> Async.Ignore
            do! db.ListRightPushAsync(pendingKey, field) |> Async.AwaitTask |> Async.Ignore
    }

    member _.Options = options

    /// Jobs dropped after exhausting their delivery-attempt budget, over
    /// the queue's lifetime (fleet-wide — the counter lives in Redis).
    member _.Dropped: Async<int64> = async {
        try
            let! value = db.StringGetAsync droppedKey |> Async.AwaitTask

            match Int64.TryParse(value.ToString()) with
            | true, parsed -> return parsed
            | _ -> return 0L
        with ex ->
            warn $"RedisIngestionQueueStore: could not read the drop counter ({ex.Message})"
            return 0L
    }

    interface IIngestionQueueStore with
        member _.Name = "redis"

        member _.Enqueue(job, capacity) = async {
            try
                let! pendingDepth = db.ListLengthAsync pendingKey |> Async.AwaitTask
                let! inFlight = db.ListLengthAsync processingKey |> Async.AwaitTask

                if pendingDepth + inFlight >= int64 capacity then
                    return false
                else
                    // A fresh id per enqueue, not `job.DocumentId`: the
                    // same document can legitimately be re-uploaded while
                    // an earlier job for it is still queued, and collapsing
                    // the two would silently drop one of them.
                    let jobId = Guid.NewGuid().ToString("N")
                    let field = RedisValue.op_Implicit jobId

                    do!
                        db.HashSetAsync(jobsKey, field, RedisValue.op_Implicit (Codec.encode job))
                        |> Async.AwaitTask
                        |> Async.Ignore

                    do! db.ListRightPushAsync(pendingKey, field) |> Async.AwaitTask |> Async.Ignore
                    return true
            with ex ->
                // A failed enqueue must read as backpressure, not as a
                // silent success: the caller's `false` path marks the
                // document `Failed` and emits the drop observability
                // triple, which is exactly the right outcome here.
                warn
                    $"RedisIngestionQueueStore: enqueue failed ({ex.Message}); reporting the queue as full so the caller surfaces the rejection"

                return false
        }

        member _.Claim(leaseDuration) = async {
            try
                // ── The atomic step. `LMOVE pending processing LEFT RIGHT`
                //    is one command, so exactly one caller gets a given
                //    job id no matter how many replicas race here.
                let! moved =
                    db.ListMoveAsync(pendingKey, processingKey, ListSide.Left, ListSide.Right)
                    |> Async.AwaitTask

                if moved.IsNullOrEmpty then
                    return None
                else
                    let jobId = moved.ToString()
                    let field = RedisValue.op_Implicit jobId

                    let! attempt = db.HashIncrementAsync(attemptsKey, field, 1L) |> Async.AwaitTask

                    do!
                        db.StringSetAsync(
                            leaseKey jobId,
                            RedisValue.op_Implicit "1",
                            Nullable leaseDuration,
                            false,
                            When.Always,
                            CommandFlags.None
                        )
                        |> Async.AwaitTask
                        |> Async.Ignore

                    let! payload = db.HashGetAsync(jobsKey, field) |> Async.AwaitTask

                    match Codec.decode (payload.ToString()) with
                    | Ok job ->
                        return
                            Some {
                                LeaseId = jobId
                                Job = job
                                Attempt = int attempt
                            }
                    | Error reason ->
                        // An unreadable payload is not a document — it can
                        // never be indexed, and redelivering it would spin.
                        warn $"RedisIngestionQueueStore: discarding unreadable job {jobId} ({reason})"
                        do! purge jobId
                        do! db.StringIncrementAsync droppedKey |> Async.AwaitTask |> Async.Ignore
                        return None
            with ex ->
                warn $"RedisIngestionQueueStore: claim failed ({ex.Message}); the drainer will retry"
                return None
        }

        member _.Complete(leaseId) = async {
            try
                do! purge leaseId
            with ex ->
                // The job stays in `processing` with an expiring lease, so
                // `ReclaimExpired` will redeliver it. At-least-once is the
                // contract; a duplicate index is idempotent.
                warn
                    $"RedisIngestionQueueStore: could not complete job {leaseId} ({ex.Message}); it will be redelivered after its lease expires"
        }

        member _.Release(leaseId) = async {
            try
                let! recorded = db.HashGetAsync(jobsKey, RedisValue.op_Implicit leaseId) |> Async.AwaitTask

                if recorded.IsNullOrEmpty then
                    // Already completed or purged — nothing to return.
                    ()
                else
                    let! attempts = db.HashGetAsync(attemptsKey, RedisValue.op_Implicit leaseId) |> Async.AwaitTask

                    let attemptCount =
                        match Int32.TryParse(attempts.ToString()) with
                        | true, parsed -> parsed
                        | _ -> 1

                    do! requeueOrDrop leaseId attemptCount
            with ex ->
                warn
                    $"RedisIngestionQueueStore: could not release job {leaseId} ({ex.Message}); it will be redelivered after its lease expires"
        }

        member _.ReclaimExpired() = async {
            try
                let! inFlight = db.ListRangeAsync(processingKey, 0L, -1L) |> Async.AwaitTask
                let mutable reclaimed = 0

                for entry in inFlight do
                    let jobId = entry.ToString()
                    let! live = db.KeyExistsAsync(leaseKey jobId) |> Async.AwaitTask

                    if not live then
                        // No lease, still in `processing` ⇒ the drainer
                        // holding it died. This is the restart-recovery
                        // path: the document is returned to the queue
                        // rather than stranded.
                        let! attempts = db.HashGetAsync(attemptsKey, RedisValue.op_Implicit jobId) |> Async.AwaitTask

                        let attemptCount =
                            match Int32.TryParse(attempts.ToString()) with
                            | true, parsed -> parsed
                            | _ -> 1

                        do! requeueOrDrop jobId attemptCount
                        reclaimed <- reclaimed + 1

                return reclaimed
            with ex ->
                warn
                    $"RedisIngestionQueueStore: reclaim sweep failed ({ex.Message}); it runs again on the next interval"

                return 0
        }

        member _.Depth() = async {
            try
                let! pendingDepth = db.ListLengthAsync pendingKey |> Async.AwaitTask
                let! inFlight = db.ListLengthAsync processingKey |> Async.AwaitTask
                return int (pendingDepth + inFlight)
            with ex ->
                warn $"RedisIngestionQueueStore: depth read failed ({ex.Message}); reporting 0"
                return 0
        }

    interface IDisposable with
        member _.Dispose() =
            if ownsMultiplexer then
                multiplexer.Dispose()

let private guardOptions (options: RedisIngestionQueueOptions) =
    match RedisIngestionQueueOptions.validate options with
    | Error problem -> raise (RedisIngestionQueueException problem)
    | Ok() -> ()

/// Wrap a multiplexer the deployment already owns — the same instance
/// backing `RedisNotificationChannel` / `RedisEmbeddingCache`, so all of
/// them share one connection pool. Disposing the returned store does NOT
/// dispose the caller's multiplexer.
let fromMultiplexer
    (multiplexer: IConnectionMultiplexer)
    (options: RedisIngestionQueueOptions)
    (logger: ILogger option)
    : IIngestionQueueStore =
    guardOptions options
    new RedisIngestionQueueStore(multiplexer, options, false, logger) :> IIngestionQueueStore

/// Connect and wrap. The returned store owns the multiplexer and
/// disposes it. Option validation runs BEFORE the connection attempt, so
/// a typo in the options is reported as a typo rather than surfacing as
/// a connection error.
let createWith
    (connectionString: string)
    (options: RedisIngestionQueueOptions)
    (logger: ILogger option)
    : IIngestionQueueStore =
    guardOptions options

    if String.IsNullOrWhiteSpace connectionString then
        raise (
            RedisIngestionQueueException
                "Redis connection string is empty. Pass the value of TOOLUP_REDIS_CONNECTION (e.g. 'localhost:6379'); the companion never reads environment variables itself."
        )

    let multiplexer =
        try
            ConnectionMultiplexer.Connect connectionString :> IConnectionMultiplexer
        with ex ->
            raise (
                RedisIngestionQueueException(
                    sprintf "could not connect to Redis with the supplied connection string: %s" ex.Message
                )
            )

    new RedisIngestionQueueStore(multiplexer, options, true, logger) :> IIngestionQueueStore

/// Shortest form: connect with the shipped defaults.
let create (connectionString: string) (logger: ILogger option) : IIngestionQueueStore =
    createWith connectionString RedisIngestionQueueOptions.defaults logger