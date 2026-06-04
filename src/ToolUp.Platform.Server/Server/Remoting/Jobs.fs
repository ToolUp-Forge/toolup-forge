namespace ToolUp.Remoting.Server

open System
open System.Collections.Concurrent

// =============================================================================
// Phase 69i — long-running operation typed handles
// =============================================================================
//
// `IJobDispatcher` substrate + a v0 in-memory implementation. The dispatcher
// itself doesn't yet special-case `Async<JobHandle<'T>>` return shapes on the
// wire side (a future enhancement; today the JobHandle is serialised as any
// other record). Handlers use the dispatcher directly:
//
//     StartReport = fun spec -> async {
//         let work = async {
//             do! computeReport spec
//             return reportFile
//         }
//         return! jobs.Enqueue work
//     }
//
// And clients poll a companion `GetReportStatus` method that delegates to
// the same dispatcher. Phase 69k's source-generator would auto-generate the
// polling companion; v0 wires it manually.

/// Phase 69i — default in-memory `IJobDispatcher`. Background work runs
/// via `Async.Start`; status is held in a `ConcurrentDictionary` keyed
/// on the job id. Stateless between calls per the six portability
/// rules (every status read re-evaluates against the dictionary);
/// distributed deployments wire a Redis / Postgres / etc. backing
/// store against the same `IJobDispatcher` contract.
///
/// **Note: in-process only.** Restarts wipe state — appropriate for
/// dev/test; production wires a distributed impl.
type InMemoryJobDispatcher(?maxJobs: int) =
    // Status stored as `obj` because the dispatcher interface methods
    // are polymorphic over `'T` but the store is single-typed. Boxing
    // happens at Enqueue; the typed unbox at GetStatus relies on the
    // caller using the matching `JobHandle<'T>` (compile-time
    // type-safety on the handle preserves correctness in practice).
    //
    // Bounded by `maxJobs` (default 100_000) — once the cap is reached
    // the oldest entry (by enqueue order) is evicted on next Enqueue.
    // Long-running deployments don't accumulate completed job records
    // forever; consumers that need persistence wire a Redis / Postgres
    // backing IJobDispatcher impl.
    let cap = defaultArg maxJobs 100_000
    let statuses = ConcurrentDictionary<string, obj>()
    let order = ConcurrentQueue<string>()

    /// True if `status` is a terminal state — Succeeded / Failed /
    /// Cancelled. Queued and Running are live and must NOT be evicted
    /// (a client polling against a live job would see "job-not-found",
    /// indistinguishable from a genuine failure; the background runner
    /// also continues writing to the evicted slot, silently re-adding
    /// it as a phantom entry).
    let isTerminal (status: obj) =
        let t = status.GetType()

        if not t.IsGenericType then
            false
        else
            // JobStatus<'T> is a DU; the case tag is exposed as `Tag` int.
            // 0 = Queued, 1 = Running, 2 = Succeeded, 3 = Failed, 4 = Cancelled.
            // (Order matches the DU declaration in Types.fs.)
            let tag =
                t.GetProperty "Tag"
                |> Option.ofObj
                |> Option.bind (fun pi -> pi.GetValue status |> Option.ofObj)
                |> Option.map unbox<int>
                |> Option.defaultValue -1

            tag >= 2

    /// Opportunistically drain stale heads from `order` — keys that were
    /// already removed from `statuses` (e.g. by a prior eviction).
    /// Keeps `order.Count` close to `statuses.Count` so subsequent
    /// eviction work doesn't scan through ghost entries.
    let compactStaleHeads () =
        let mutable peeked = Unchecked.defaultof<string>
        let mutable keepCompacting = true

        while keepCompacting && order.TryPeek(&peeked) do
            if statuses.ContainsKey peeked then
                keepCompacting <- false
            else
                let mutable _ignored = Unchecked.defaultof<string>
                order.TryDequeue(&_ignored) |> ignore

    let evictOldestIfFull () =
        // Drain stale heads first so the cap-check below isn't perturbed
        // by ghost entries still occupying `order`.
        compactStaleHeads ()

        // Only evict TERMINAL entries — Queued / Running stay until they
        // complete (or the host shuts down). Walk forward through `order`
        // skipping live ids; if none are terminal but the cap is
        // exceeded, refuse the new Enqueue with a saturation signal so
        // the operator sees the pressure instead of silent job loss.
        let mutable evicted = false
        let mutable scanned = 0

        while not evicted && statuses.Count >= cap && scanned < statuses.Count do
            let mutable candidate = Unchecked.defaultof<string>

            if order.TryDequeue(&candidate) then
                match statuses.TryGetValue candidate with
                | true, status when isTerminal status ->
                    statuses.TryRemove candidate |> ignore
                    evicted <- true
                | true, _liveStatus ->
                    // Live job — push back to the tail of `order` so
                    // it stays in the FIFO sweep but doesn't block.
                    order.Enqueue candidate
                    scanned <- scanned + 1
                | false, _ ->
                    // Already gone (race with lazy expiry / Get). Treat
                    // as a successful eviction.
                    evicted <- true
            else
                scanned <- statuses.Count // exit the loop

        if not evicted && statuses.Count >= cap then
            invalidOp (
                "InMemoryJobDispatcher saturated: every tracked job is live "
                + "(Queued or Running) and the maxJobs cap has been reached. "
                + "Increase `maxJobs` at construction time, or wire a "
                + "distributed `IJobDispatcher` impl for production loads."
            )

    interface IJobDispatcher with
        member _.Enqueue<'T>(work: Async<'T>) : Async<JobHandle<'T>> = async {
            evictOldestIfFull ()
            let jobId = Guid.NewGuid().ToString("N")
            statuses[jobId] <- box (JobStatus<'T>.Queued)
            order.Enqueue jobId

            // Background runner. Updates status to Running on
            // start, then Succeeded / Failed on completion. The
            // intermediate `Running` arm is a one-shot transition
            // — v0 doesn't track progress; consumers wanting
            // progress wire their own dispatcher.
            Async.Start(
                async {
                    statuses[jobId] <- box (JobStatus<'T>.Running 0.0)

                    try
                        let! result = work
                        statuses[jobId] <- box (JobStatus<'T>.Succeeded result)
                    with ex ->
                        statuses[jobId] <- box (JobStatus<'T>.Failed ex.Message)
                }
            )

            return JobHandle jobId
        }

        member _.GetStatus<'T>(handle: JobHandle<'T>) : Async<JobStatus<'T>> = async {
            let (JobHandle jobId) = handle

            match statuses.TryGetValue jobId with
            | true, status ->
                // Type-erased unbox: relies on the caller using
                // the matching JobHandle<'T>. Safe because the
                // handle was created from the same Enqueue<'T>
                // call.
                return (status :?> JobStatus<'T>)
            | false, _ -> return JobStatus<'T>.Failed "job-not-found"
        }

    /// Diagnostics: current tracked-job count for telemetry / health.
    member _.JobCount = statuses.Count
    /// Diagnostics: configured cap.
    member _.MaxJobs = cap