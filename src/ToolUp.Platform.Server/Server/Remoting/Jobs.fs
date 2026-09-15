namespace ToolUp.Remoting.Server

open System
open System.Collections.Concurrent
open System.Threading

// =============================================================================
// Phase 69i — long-running operation typed handles
// =============================================================================
//
// `IJobDispatcher` substrate + a v0 in-memory implementation. Handlers use
// the dispatcher directly:
//
//     StartReport = fun spec -> async {
//         let work = async {
//             do! computeReport spec
//             return reportFile
//         }
//         return! jobs.Enqueue work
//     }
//
// 69i.B — with the same instance composed via `Remoting.withJobDispatcher`,
// the dispatcher classifies every method returning `Async<JobHandle<'T>>` as
// long-running at startup and serves the poll / progress / cancel companions
// for it (`LongRunning.fs`), so the consumer no longer hand-wires a poll
// method. Without the composition the v0 shape above still works unchanged.
//
// `IJobScheduler` (Phase 9b) is a COMPOSITION choice behind this seam, not
// something the dispatcher routes into: the work the v0 shape enqueues is a
// closure, and `JobResult` carries no payload, so a scheduler-backed
// `IJobDispatcher` needs a result store beside the scheduler — the shape
// Phases 630/631 built for the aggregate peer surface (`IPeerJobResultStore`)
// — and that is a store-backed implementation of THIS interface, not a
// change to the dispatcher. See `docs/migrations/69i-long-running-handle.md`.

/// Phase 69i — default in-memory `IJobDispatcher`. Background work runs
/// via `Async.Start`; status is held in a `ConcurrentDictionary` keyed
/// on the job id. Stateless between calls per the six portability
/// rules (every status read re-evaluates against the dictionary);
/// distributed deployments wire a Redis / Postgres / etc. backing
/// store against the same `IJobDispatcher` contract.
///
/// **Note: in-process only.** Restarts wipe state — appropriate for
/// dev/test; production wires a distributed impl.
///
/// 69i.E — every job is stamped at `Enqueue` with the submitting subject
/// (`CallContext.subjectId ()`) and the request's correlation id, and both
/// are re-established inside the job body so sub-operations see the
/// original caller. `GetStatus` / `Cancel` from a different subject answer
/// as if the job did not exist, unless `CallContext.isJobAdmin ()` holds.
/// A job enqueued with no subject resolved has no owner (GP 11).
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
    let owners = ConcurrentDictionary<string, string option>()
    let cancellations = ConcurrentDictionary<string, CancellationTokenSource>()
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

    let forget (jobId: string) =
        statuses.TryRemove jobId |> ignore
        owners.TryRemove jobId |> ignore

        match cancellations.TryRemove jobId with
        | true, cts -> cts.Dispose()
        | false, _ -> ()

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
                    forget candidate
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

    /// 69i.E — may the current caller see / act on `jobId`? The owner, a
    /// job-admin caller, and any caller of an ownerless job. Unknown ids
    /// are `false` so callers fall through to the not-found arm.
    let callerMayAccess (jobId: string) =
        match owners.TryGetValue jobId with
        | true, None -> true
        | true, Some owner -> CallContext.isJobAdmin () || CallContext.subjectId () = Some owner
        | false, _ -> false

    /// Transition to a terminal status unless one is already recorded —
    /// the runner and `Cancel` race for the terminal slot and the first
    /// writer wins, so a job cancelled just as it completed reports the
    /// outcome that landed first, never a later overwrite.
    let trySetTerminal (jobId: string) (terminal: obj) =
        match statuses.TryGetValue jobId with
        | true, current when isTerminal current -> false
        | true, current -> statuses.TryUpdate(jobId, terminal, current)
        | false, _ -> false

    interface IJobDispatcher with
        member _.Enqueue<'T>(work: Async<'T>) : Async<JobHandle<'T>> = async {
            evictOldestIfFull ()
            let jobId = Guid.NewGuid().ToString("N")
            let owner = CallContext.subjectId ()
            let correlation = CallContext.correlationId ()
            let cts = new CancellationTokenSource()
            owners[jobId] <- owner
            cancellations[jobId] <- cts
            statuses[jobId] <- box (JobStatus<'T>.Queued)
            order.Enqueue jobId

            // Background runner. Updates status to Running on
            // start, then Succeeded / Failed on completion. The
            // intermediate `Running` arm is a one-shot transition
            // — v0 doesn't track progress; consumers wanting
            // progress wire their own dispatcher.
            //
            // 69i.E — the submitting subject and the request's
            // correlation id are re-established inside the job so the
            // work sees its original caller whatever thread it lands on.
            // 69i.G — the work runs under the job's own token; `Cancel`
            // trips it, and the runner records `Cancelled` only if the
            // work had not already reached a terminal status.
            Async.Start(
                async {
                    use _subject = CallContext.beginSubject owner false

                    use _correlation =
                        match correlation with
                        | Some cid -> CallContext.beginRequest cid
                        | None ->
                            { new IDisposable with
                                member _.Dispose() = ()
                            }

                    statuses.TryUpdate(jobId, box (JobStatus<'T>.Running 0.0), box (JobStatus<'T>.Queued))
                    |> ignore

                    try
                        let! result = work
                        trySetTerminal jobId (box (JobStatus<'T>.Succeeded result)) |> ignore
                    with
                    | :? OperationCanceledException -> trySetTerminal jobId (box (JobStatus<'T>.Cancelled)) |> ignore
                    | ex -> trySetTerminal jobId (box (JobStatus<'T>.Failed ex.Message)) |> ignore
                },
                cts.Token
            )

            return JobHandle jobId
        }

        member _.GetStatus<'T>(handle: JobHandle<'T>) : Async<JobStatus<'T>> = async {
            let (JobHandle jobId) = handle

            match statuses.TryGetValue jobId with
            | true, status when callerMayAccess jobId ->
                // Type-erased unbox: relies on the caller using
                // the matching JobHandle<'T>. Safe because the
                // handle was created from the same Enqueue<'T>
                // call.
                return (status :?> JobStatus<'T>)
            | _ -> return JobStatus<'T>.Failed "job-not-found"
        }

        member _.Cancel<'T>(handle: JobHandle<'T>) : Async<unit> = async {
            let (JobHandle jobId) = handle

            if callerMayAccess jobId then
                // Mark first, then trip the token: a poll racing the
                // cancel sees `Cancelled` as soon as the call returns,
                // and the runner's own `OperationCanceledException` arm
                // finds the terminal slot already taken.
                trySetTerminal jobId (box (JobStatus<'T>.Cancelled)) |> ignore

                match cancellations.TryGetValue jobId with
                | true, cts ->
                    try
                        cts.Cancel()
                    with :? ObjectDisposedException ->
                        ()
                | false, _ -> ()
        }

    /// Diagnostics: current tracked-job count for telemetry / health.
    member _.JobCount = statuses.Count
    /// Diagnostics: configured cap.
    member _.MaxJobs = cap