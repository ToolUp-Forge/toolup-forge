module ToolUp.Platform.Tests.InProcess.UsageLogTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Usage
open ToolUp.Platform.Tests.Contracts

// ─── In-process bindings — IUsageLog contract ────────────────────
//
// Two bindings: `InMemoryUsageLog` (fast, no flusher cost) and
// `BlobUsageLog + UsageBatchFlusher` over a temp-backed
// `LocalFileStorage` (exercises the rollup-file layout, the channel
// drain path, and per-(scope, day) write serialisation).

let private uniqueScope () =
    "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

// ─── In-memory binding ────────────────────────────────────────────

let inMemoryTests =
    let factory () =
        let log = UsageLog.InMemoryUsageLog() :> IUsageLog
        let scopeA = uniqueScope ()
        let scopeB = uniqueScope ()
        log, scopeA, scopeB

    IUsageLogContract.tests "InMemoryUsageLog" factory TimeSpan.Zero

// ─── Blob-backed binding ──────────────────────────────────────────
//
// Each factory call constructs a fresh `LocalFileStorage` + flusher +
// log triple. Rather than relying on the `BackgroundService` timer to
// flush within a test-window deadline (which is racy under CI load),
// we wrap the `IUsageLog` so every `Record` call immediately invokes
// the flusher's `DrainAndFlush()`. The contract pack still runs
// against `IUsageLog`; the flusher is what's under test for blob
// persistence + rollup-file layout + scope partitioning. The
// per-(scope, day) lock + RecordId dedup logic is exercised by the
// underlying `UsageBatchFlusher.flushBucket` invocations.

let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-usage-log-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private testLogger =
    { new ILogger with
        member _.Debug(_message: string) = ()
        member _.Info(_message: string) = ()
        member _.Warn(_message: string) = ()
        member _.Error(_message: string, _ex: exn option) = ()
    }

let private flushPolicy: BatchFlushPolicy = {
    FlushAtCount = 1 // Flush eagerly so tests don't wait
    FlushInterval = TimeSpan.FromMilliseconds 50.0
    ChannelCapacity = 4096
    ProducerWaitTimeout = TimeSpan.FromSeconds 1.0
}

let private blobFlushDelay = TimeSpan.FromMilliseconds 500.0

/// Wraps an `IUsageLog` so every `Record` call triggers the
/// underlying flusher to drain immediately. Eliminates the timing
/// race the BackgroundService introduces under test load.
type private SyncFlushUsageLog(inner: IUsageLog, flusher: UsageLog.UsageBatchFlusher) =
    interface IUsageLog with
        member _.Record(record) = async {
            do! inner.Record record
            do! flusher.DrainAndFlush()
        }

        member _.Query(scopeId, kind, range) = inner.Query(scopeId, kind, range)
        member _.Aggregate(scopeId, grouping) = inner.Aggregate(scopeId, grouping)

let blobTests =
    let factory () =
        let dir = tempDir ()
        let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

        let flusher =
            new UsageLog.UsageBatchFlusher(blobStorage, flushPolicy, testLogger, None)

        let inner = UsageLog.BlobUsageLog(blobStorage, flusher) :> IUsageLog
        let log = SyncFlushUsageLog(inner, flusher) :> IUsageLog
        let scopeA = uniqueScope ()
        let scopeB = uniqueScope ()
        log, scopeA, scopeB

    IUsageLogContract.tests "BlobUsageLog" factory TimeSpan.Zero

// ─── ITeamQuotaPolicy binding — BlobBackedTeamQuotaPolicy ────────
//
// Backs the policy with `InMemoryUsageLog` (so the budget check
// reads consistent state without flushing delays) and a stubbed
// `IConfigStore` that returns the test-supplied caps for `scopeA`
// and an empty map for any other scope.

type private FakeConfigStore(scopeA: string, raw: Map<string, string>) =
    interface IConfigStore with
        member _.GetRaw(scope, _moduleKey) = async {
            if scope.ScopeId = scopeA then
                return raw
            else
                return Map.empty
        }

        member _.Get<'T>(_scope, _moduleKey) : Async<'T option> = async { return None }

        member _.GetEffective<'T>(_scope, _moduleKey, _schema) : Async<'T> =
            failwith "not used by quota policy contract"

        member _.Set<'T>(_scope, _moduleKey, _value: 'T, _schema) = async { return Ok() }

        member _.SetRaw(_scope, _moduleKey, _values, _schema) = async { return Ok() }

        member _.Clear(_scope, _moduleKey) = async { return () }

        member _.Erase(_, _, _, _) = async {
            return
                Result.Ok {
                    HandlerName = "config"
                    RecordsAffected = 0
                    Note = None
                }
        }

let quotaPolicyTests =
    let factory (cfg: ITeamQuotaPolicyContract.QuotaFactoryConfig) =
        let scopeA = uniqueScope ()
        let scopeB = uniqueScope ()

        let raw =
            seq {
                match cfg.MaxConcurrentJobs with
                | Some v -> yield UsageConfigKey.maxConcurrentJobs, string v
                | None -> ()

                match cfg.MaxAITokensPerDay with
                | Some v ->
                    yield UsageConfigKey.maxAITokensPerDay, v.ToString System.Globalization.CultureInfo.InvariantCulture
                | None -> ()

                match cfg.MaxAITokensPerMonth with
                | Some v ->
                    yield
                        UsageConfigKey.maxAITokensPerMonth, v.ToString System.Globalization.CultureInfo.InvariantCulture
                | None -> ()
            }
            |> Map.ofSeq

        let configStore = FakeConfigStore(scopeA, raw) :> IConfigStore
        let usageLog = UsageLog.InMemoryUsageLog() :> IUsageLog

        let policy =
            TeamQuotaPolicy.BlobBackedTeamQuotaPolicy(configStore, usageLog) :> ITeamQuotaPolicy

        policy, scopeA, scopeB

    ITeamQuotaPolicyContract.tests "BlobBackedTeamQuotaPolicy" factory

// ─── Aggregate ────────────────────────────────────────────────────

let tests = testList "UsageLogTests" [ inMemoryTests; blobTests; quotaPolicyTests ]