namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Threading
open ToolUp.Platform.Metrics

// ─── Phase 9v — in-process sliding-window outbound rate limiter ──────
//
// Default `IRateLimiter` registered when
// `ServerConfig.RateLimiter = InProcessRateLimiter`. Per-bucket
// sliding-window counters plus a `SemaphoreSlim` per bucket serialise
// access to the window state; the bucket key is derived from the
// caller's `RateLimitKey` plus the matching `RateLimitDescriptor`'s
// `FairnessMode` (PerScope = each scope its own bucket; PerProvider =
// one bucket across the deployment).
//
// **Soft ceiling.** The limiter admits up to 95% of the declared
// `ShortWindow` count before holding callers; the 5% headroom leaves
// room for retries inside one window without tripping the upstream
// provider's hard limit. Sliding-window trim runs on every `Wait` so
// expired entries roll out of both the short and long-window logs as
// time passes.
//
// **Long-window terminal refusal.** When a descriptor declares a
// `LongWindow` (typical for daily quotas) the limiter returns
// `Refused` rather than blocking past the window reset — daily
// recovery is too long to hold a request thread/async for.
//
// **Distributed limitation.** This implementation tracks the window
// per-process; a load-balanced multi-instance deployment will burst
// past the declared limit by N× until the Phase 9c half-2 Redis-
// backed `IRateLimiter` companion ships. The contract is designed so
// the swap is contract-free — emission sites call `Wait` and observe
// `RateLimitDecision` regardless of which implementation is wired.
//
// **Observability.** Every Wait call emits to `IMetricsSink`
// (`toolup.ratelimit.waited_total`, `toolup.ratelimit.wait_ms`
// histogram, `toolup.ratelimit.refused_total`, tagged by `provider`).
// Waits exceeding `ServerConfig.SlowRateLimitThreshold` qualify the
// call for a `RateLimitWaited` audit row; `Refused` outcomes always
// emit a `RateLimitRefused` row. Sub-threshold waits are silent in
// the audit trail to avoid flooding it with the steady-state pacing
// emissions.

/// Per-bucket window state. Each bucket has its own `SemaphoreSlim`
/// (serialises window-state mutation) and two sorted timestamp logs —
/// short window plus optional long window. The bucket key is computed
/// from `(provider, subKey, scopeId-or-shared)` per the matching
/// descriptor's `FairnessMode`; equivalent input keys hit the same
/// state value through `ConcurrentDictionary.GetOrAdd`.
type private WindowState = {
    Semaphore: SemaphoreSlim
    ShortTimestamps: ResizeArray<DateTime>
    LongTimestamps: ResizeArray<DateTime>
}

/// Internal bucket-discriminating key. `ScopeId` is `None` when the
/// matching descriptor's `FairnessMode = PerProvider` so all callers
/// share one bucket across the deployment; `Some scope` when
/// `PerScope`. `SubKey` partitions further within either mode.
[<Struct>]
type private BucketKey = {
    Provider: string
    SubKey: string option
    ScopeId: string option
}

/// SDK-default in-process `IRateLimiter`. Construction takes the
/// compose-time list of `RateLimitDescriptor`s; descriptors whose
/// `Provider` is not referenced by any `Wait` call carry no runtime
/// cost (buckets are lazily allocated). Implementations resolve
/// `IMetricsSink` / `IAuditLog` / `ILogger` from compose-time
/// construction rather than per-call DI lookups so the hot path is
/// allocation-free in the admit-immediately branch.
type InProcessRateLimiter
    (
        descriptors: RateLimitDescriptor list,
        metricsSink: IMetricsSink,
        auditLog: IAuditLog,
        slowRateLimitThreshold: TimeSpan,
        logger: ILogger
    ) =

    // 5% headroom below the declared short-window count so retries
    // inside one window have room without tripping the upstream's
    // hard ceiling. See the file-header comment for the rationale.
    let softCeilingFactor = 0.95

    let descriptorByProvider =
        descriptors |> List.map (fun d -> d.Provider, d) |> Map.ofList

    let buckets = ConcurrentDictionary<BucketKey, WindowState>()

    let bucketKeyOf (descriptor: RateLimitDescriptor) (key: RateLimitKey) : BucketKey =
        match descriptor.FairnessMode with
        | PerScope -> {
            Provider = key.Provider
            SubKey = key.SubKey
            ScopeId = Some key.ScopeId
          }
        | PerProvider -> {
            Provider = key.Provider
            SubKey = key.SubKey
            ScopeId = None
          }

    let getOrCreate (bucketKey: BucketKey) : WindowState =
        buckets.GetOrAdd(
            bucketKey,
            fun _ -> {
                Semaphore = new SemaphoreSlim(1, 1)
                ShortTimestamps = ResizeArray()
                LongTimestamps = ResizeArray()
            }
        )

    // Drop expired entries from the head of the timestamp log. The log
    // is append-only and sorted by construction, so a linear forward
    // scan to find the first non-expired index is correct; a single
    // `RemoveRange` then truncates the prefix.
    let trim (timestamps: ResizeArray<DateTime>) (windowDuration: TimeSpan) (now: DateTime) =
        let cutoff = now - windowDuration
        let mutable removeCount = 0

        while removeCount < timestamps.Count && timestamps[removeCount] < cutoff do
            removeCount <- removeCount + 1

        if removeCount > 0 then
            timestamps.RemoveRange(0, removeCount)

    let metricTags (provider: string) : Map<string, string> = Map.ofList [ "provider", provider ]

    interface IRateLimiter with
        member _.Wait(key) = async {
            match Map.tryFind key.Provider descriptorByProvider with
            | None ->
                // No descriptor declared for this provider — admit
                // immediately. Misconfigurations fail open (no gate)
                // rather than failing closed (deny everything) so a
                // forgotten registration doesn't manifest as an outage.
                return Proceed
            | Some descriptor ->
                let bucketKey = bucketKeyOf descriptor key
                let state = getOrCreate bucketKey
                let stopwatch = System.Diagnostics.Stopwatch.StartNew()

                // Acquire the per-bucket semaphore + compute the
                // decision inside it; release before emitting metrics
                // / audit so observability writes don't serialise
                // through the bucket lock.
                let! resolved = async {
                    let! _ = state.Semaphore.WaitAsync() |> Async.AwaitTask

                    try
                        let shortCount, shortWindow = descriptor.ShortWindow
                        let softCap = max 1 (int (float shortCount * softCeilingFactor))

                        let mutable decision: RateLimitDecision option = None
                        // Flips to `true` only when the loop hits the
                        // soft-ceiling sleep branch — distinguishes
                        // "admitted immediately" (Proceed) from
                        // "admitted after waiting at the cap" (DelayedBy).
                        // Semaphore-acquisition and trim-walk latency is
                        // bookkeeping, not throttling, so it does not
                        // qualify a call for `DelayedBy`.
                        let mutable wasDelayed = false

                        while decision.IsNone do
                            let now = DateTime.UtcNow
                            trim state.ShortTimestamps shortWindow now

                            // Long-window check is terminal — daily
                            // quotas recover too slowly to block on.
                            match descriptor.LongWindow with
                            | Some(longCount, longWindow) ->
                                trim state.LongTimestamps longWindow now

                                if state.LongTimestamps.Count >= longCount then
                                    let reason = sprintf "long-window quota exhausted (%d in %A)" longCount longWindow

                                    decision <- Some(Refused reason)
                            | None -> ()

                            if decision.IsNone then
                                if state.ShortTimestamps.Count < softCap then
                                    state.ShortTimestamps.Add now

                                    match descriptor.LongWindow with
                                    | Some _ -> state.LongTimestamps.Add now
                                    | None -> ()

                                    stopwatch.Stop()

                                    decision <-
                                        if wasDelayed then
                                            Some(DelayedBy stopwatch.Elapsed)
                                        else
                                            Some Proceed
                                else
                                    // Soft-ceiling hit — sleep until
                                    // the oldest entry rolls out.
                                    // 50ms slack so the next loop
                                    // pass observes the trim window
                                    // having shifted.
                                    wasDelayed <- true
                                    let oldest = state.ShortTimestamps[0]
                                    let expiresIn = (oldest + shortWindow) - now

                                    let sleepFor =
                                        if expiresIn <= TimeSpan.Zero then
                                            TimeSpan.FromMilliseconds 50.0
                                        else
                                            expiresIn + TimeSpan.FromMilliseconds 50.0

                                    do! Async.Sleep sleepFor

                        return decision.Value
                    finally
                        state.Semaphore.Release() |> ignore
                }

                // Observability — outside the bucket lock so audit /
                // metric writes don't block the next caller in line.
                match resolved with
                | Proceed -> ()
                | DelayedBy waited ->
                    metricsSink.Increment("toolup.ratelimit.waited_total", metricTags key.Provider)

                    metricsSink.Record("toolup.ratelimit.wait_ms", waited.TotalMilliseconds, metricTags key.Provider)

                    if waited >= slowRateLimitThreshold then
                        do!
                            auditLog.Record(
                                key.ScopeId,
                                RateLimitWaited {
                                    ScopeId = key.ScopeId
                                    Provider = key.Provider
                                    SubKey = key.SubKey
                                    WaitedMs = int64 waited.TotalMilliseconds
                                }
                            )
                | Refused reason ->
                    metricsSink.Increment("toolup.ratelimit.refused_total", metricTags key.Provider)

                    do!
                        auditLog.Record(
                            key.ScopeId,
                            RateLimitRefused {
                                ScopeId = key.ScopeId
                                Provider = key.Provider
                                SubKey = key.SubKey
                                Reason = reason
                            }
                        )

                return resolved
        }