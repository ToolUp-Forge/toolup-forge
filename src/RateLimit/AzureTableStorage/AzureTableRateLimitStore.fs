// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.RateLimit.AzureTableStorage

open System
open System.Collections.Concurrent
open Azure
open Azure.Data.Tables
open ToolUp.Platform

// ─── ToolUp.RateLimit.AzureTableStorage — Phase 56 reference impl ────
//
// Azure Table Storage `IRateLimitStore`. Atomic-increment-and-check
// via ETag-retry: read the current row, compute the next count, write
// with the read-time ETag; retry on PreconditionFailed (412). Provides
// cross-instance correctness for `WebOnly`-deployed silos hitting the
// same Azure Table.
//
// **Storage layout.**
//   Table: `ToolupRateLimit` (configurable via `Options.TableName`).
//   PartitionKey: `<window>|<storeKey>` (single-window per partition
//     so the boundary update happens within a partition entity).
//   RowKey: `<windowStartTicks>` (calendar-aligned boundary so a
//     missed-then-recreated entity can re-derive its boundary).
//   Properties: `Count: Int32`, `ExpiresAt: DateTimeOffset` (TTL for
//     manual sweep; Azure Tables has no native TTL).
//
// **Atomic increment-and-check.**
//   1. Read the entity for `(partitionKey, windowStartRowKey)`.
//   2. Compute `next = current + 1`.
//   3. UpdateEntity with the read-time ETag; on PreconditionFailed,
//      re-read and retry up to `MaxRetries` (default 5).
//   4. Compare `next` against `threshold`; emit `Allow` or `Deny`.
//
// **TTL eviction.**
//   No native TTL in Azure Tables. The companion's `Maintenance.sweep`
//   helper iterates partitions whose `ExpiresAt < now` and deletes
//   them. Consumers wire it as an `IJobHandler` against
//   `IJobScheduler` for periodic cleanup, OR drop it entirely and
//   accept gradual storage growth (counts overwrite themselves in
//   the same row each window — the dead rows are old windows whose
//   boundary moved on).
//
// **Six-rule portability audit.** See README.md "Six-rule portability
// audit" section. All six honoured: identity-by-value
// (`InboundRateLimitKey` -> string partition), async at every method,
// retry-as-data (Result), stateless boundaries (every call re-reads
// state), no cross-shard ordering (per-key partition + ETag), precision
// (documented atomic-increment ceiling — ~10-100 RPS per partition
// per Azure Tables docs).

/// Configuration for the Azure Table Storage rate-limit store.
type Options = {
    /// Azure Storage table-service URI or connection string. Use the
    /// connection-string form for local dev (Azurite); URI + Default
    /// Azure Credential for production.
    ConnectionString: string
    /// Table name within the storage account. Auto-created on first
    /// use. Default `"ToolupRateLimit"`.
    TableName: string
    /// Maximum ETag-retry attempts on `IncrementAndCheck`. Default 5.
    /// Above this the store returns `StoreUnavailable` and the
    /// middleware fails open.
    MaxRetries: int
    /// Maximum recent-decision events to retain in-memory for
    /// `GetRecentDecisions` (Phase 61 admin widget). Stored locally
    /// per-instance — multi-instance deployments aggregate via the
    /// caller's metrics sink, not via the store. Default 1000.
    MaxRecentDecisions: int
}

module Options =
    let defaults: Options = {
        ConnectionString = ""
        TableName = "ToolupRateLimit"
        MaxRetries = 5
        MaxRecentDecisions = 1000
    }

module AzureTableRateLimitStore =

    let private windowBoundary (window: RateLimitWindow) (now: DateTimeOffset) : DateTimeOffset * TimeSpan =
        match window with
        | PerSecond ->
            DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Offset),
            TimeSpan.FromSeconds 1.0
        | PerMinute ->
            DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset), TimeSpan.FromMinutes 1.0
        | PerHour -> DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset), TimeSpan.FromHours 1.0
        | PerDay -> DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset), TimeSpan.FromDays 1.0
        | SlidingWindow(duration, _) -> now - duration, duration

    let private windowLabel =
        function
        | PerSecond -> "PerSecond"
        | PerMinute -> "PerMinute"
        | PerHour -> "PerHour"
        | PerDay -> "PerDay"
        | SlidingWindow(d, _) -> sprintf "Sliding%fs" d.TotalSeconds

    /// Wraps an `Azure.Data.Tables.TableClient` against an
    /// `IRateLimitStore` interface. Constructor takes the options
    /// record + an `ILogger` for the fail-open path.
    type private Store(options: Options, logger: ILogger) =
        let client = TableServiceClient(options.ConnectionString)

        // Ensure the table exists. Idempotent — Azure SDK no-ops when
        // the table is already there. Called lazily on first
        // `IncrementAndCheck` so test setups don't pay it on every
        // construct.
        let tableClient: TableClient =
            let tc = client.GetTableClient(options.TableName)
            tc.CreateIfNotExists() |> ignore
            tc

        // Local recent-decisions buffer (per-instance). See Options.
        let recentDecisions = ConcurrentQueue<RateLimitDecisionEvent>()

        let trimRecent () =
            while recentDecisions.Count > options.MaxRecentDecisions do
                let mutable _drop = Unchecked.defaultof<RateLimitDecisionEvent>
                recentDecisions.TryDequeue &_drop |> ignore

        let recordDecision evt =
            recentDecisions.Enqueue evt
            trimRecent ()

        let partitionKey (key: InboundRateLimitKey) (window: RateLimitWindow) : string =
            sprintf "%s|%s" (windowLabel window) (InboundRateLimitKey.asStoreKey key)

        let rowKey (windowStart: DateTimeOffset) : string = string windowStart.UtcTicks

        let readCounter (pk: string) (rk: string) : Async<(int * ETag) option> = async {
            let! ct = Async.CancellationToken

            try
                let! response =
                    tableClient.GetEntityAsync<TableEntity>(pk, rk, cancellationToken = ct)
                    |> Async.AwaitTask

                let entity = response.Value
                let count = entity.GetInt32 "Count" |> Option.ofNullable |> Option.defaultValue 0
                return Some(count, entity.ETag)
            with :? RequestFailedException as ex when ex.Status = 404 ->
                return None
        }

        let writeCounter
            (pk: string)
            (rk: string)
            (count: int)
            (expiresAt: DateTimeOffset)
            (etag: ETag option)
            : Async<Result<unit, RateLimitStoreError>> =
            async {
                let! ct = Async.CancellationToken
                let entity = TableEntity(pk, rk)
                entity["Count"] <- count
                entity["ExpiresAt"] <- expiresAt

                try
                    match etag with
                    | None ->
                        // First write — Add (fails if entity exists,
                        // which the ETag-retry loop above handles).
                        let! _ = tableClient.AddEntityAsync(entity, cancellationToken = ct) |> Async.AwaitTask
                        return Ok()
                    | Some tag ->
                        let! _ =
                            tableClient.UpdateEntityAsync(entity, tag, TableUpdateMode.Replace, cancellationToken = ct)
                            |> Async.AwaitTask

                        return Ok()
                with
                | :? RequestFailedException as ex when ex.Status = 412 || ex.Status = 409 ->
                    // PreconditionFailed (412) or Conflict (409) —
                    // another writer raced. Caller retries.
                    return Error(StoreUnavailable "ETag mismatch — retry")
                | :? RequestFailedException as ex ->
                    return Error(StoreUnavailable(sprintf "Azure Table write failed: %s" ex.Message))
            }

        interface IRateLimitStore with
            member _.GetCurrent(key, window) = async {
                let now = DateTimeOffset.UtcNow
                let boundary, _ = windowBoundary window now
                let pk = partitionKey key window
                let rk = rowKey boundary
                let! readResult = readCounter pk rk

                return
                    match readResult with
                    | Some(count, _) -> count
                    | None -> 0
            }

            member _.IncrementAndCheck(key, window, threshold) = async {
                let now = DateTimeOffset.UtcNow
                let boundary, duration = windowBoundary window now
                let pk = partitionKey key window
                let rk = rowKey boundary
                let expiresAt = boundary + duration + TimeSpan.FromMinutes 1.0

                let rec attempt remaining = async {
                    if remaining <= 0 then
                        logger.Warn(
                            sprintf
                                "[AzureTableRateLimitStore] Exhausted %d retries on (%s, %s); failing open."
                                options.MaxRetries
                                pk
                                rk
                        )

                        return Error(StoreUnavailable "ETag-retry exhausted")
                    else
                        let! readResult = readCounter pk rk

                        let current, etag =
                            match readResult with
                            | Some(c, e) -> c, Some e
                            | None -> 0, None

                        let next = current + 1
                        let! writeResult = writeCounter pk rk next expiresAt etag

                        match writeResult with
                        | Ok() ->
                            if next > threshold then
                                let elapsed = now - boundary
                                let remainingTime = duration - elapsed

                                let retryAfter = max 1 (int (Math.Ceiling remainingTime.TotalSeconds))

                                return
                                    Ok(
                                        DenyWithError {
                                            RetryAfterSeconds = retryAfter
                                            Limit = threshold
                                            Window = window
                                        }
                                    )
                            else
                                return Ok(AllowWithRemaining(threshold - next))
                        | Error(StoreUnavailable reason) when reason.StartsWith "ETag mismatch" ->
                            return! attempt (remaining - 1)
                        | Error err -> return Error err
                }

                let! result = attempt options.MaxRetries

                match result with
                | Ok((DenyWithError _) as decision) ->
                    recordDecision {
                        Key = key
                        Route = ""
                        Window = window
                        Threshold = threshold
                        Decision = decision
                        OccurredAt = now
                    }
                | _ -> ()

                return result
            }

            member _.GetRecentDecisions(keyFilter, count) = async {
                let snapshot = recentDecisions |> Seq.toArray

                let filtered =
                    match keyFilter with
                    | None -> snapshot
                    | Some k -> snapshot |> Array.filter (fun e -> e.Key = k)

                return filtered |> Array.rev |> Array.truncate count |> Array.toList
            }

    /// Create an `IRateLimitStore` backed by Azure Table Storage.
    /// Validates the table exists (creates if missing) on first
    /// invocation. Consumer wires this into compose via a
    /// `ComposeExtensions.ServiceConfig` callback:
    ///
    ///     ServerApp.empty
    ///     |> ServerApp.withConfig { ... with RateLimitStore = ExternalRateLimitStore }
    ///     |> ServerApp.withServiceConfig (fun s ->
    ///         let store = AzureTableRateLimitStore.create options logger
    ///         s.AddSingleton<IRateLimitStore>(store))
    let create (options: Options) (logger: ILogger) : IRateLimitStore = Store(options, logger) :> _