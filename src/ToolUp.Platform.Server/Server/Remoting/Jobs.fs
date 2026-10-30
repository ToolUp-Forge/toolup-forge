namespace ToolUp.Remoting.Server

open System
open System.Collections.Concurrent
open System.Threading

// =============================================================================
// Long-running operation typed handles
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
// With the same instance composed via `Remoting.withJobDispatcher`,
// the dispatcher classifies every method returning `Async<JobHandle<'T>>` as
// long-running at startup and serves the poll / progress / cancel companions
// for it (`LongRunning.fs`), so the consumer no longer hand-wires a poll
// method. Without the composition the v0 shape above still works unchanged.
//
// `IJobScheduler` is a COMPOSITION choice behind this seam, not
// something the dispatcher routes into: the work the v0 shape enqueues is a
// closure, and `JobResult` carries no payload, so a scheduler-backed
// `IJobDispatcher` needs a result store beside the scheduler — the shape
// built for the aggregate peer surface (`IPeerJobResultStore`)
// — and that is a store-backed implementation of THIS interface, not a
// change to the dispatcher. See `docs/migrations/69i-long-running-handle.md`.

/// Default in-memory `IJobDispatcher`. Background work runs
/// via `Async.Start`; status is held in a `ConcurrentDictionary` keyed
/// on the job id. Stateless between calls per the six portability
/// rules (every status read re-evaluates against the dictionary);
/// distributed deployments wire a Redis / Postgres / etc. backing
/// store against the same `IJobDispatcher` contract.
///
/// **Note: in-process only.** Restarts wipe state — appropriate for
/// dev/test; production wires a distributed impl.
///
/// Every job is stamped at `Enqueue` with the submitting subject
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
    // Bounded by `maxJobs` (default 100_000) through `BoundedFifo`.
    // Long-running deployments don't accumulate completed job records
    // forever; consumers that need persistence wire a Redis / Postgres
    // backing IJobDispatcher impl.
    let cap = defaultArg maxJobs 100_000
    let statuses = ConcurrentDictionary<string, obj>()
    let owners = ConcurrentDictionary<string, string option>()
    let cancellations = ConcurrentDictionary<string, CancellationTokenSource>()

    // Each job's terminal test, captured at Enqueue where `'T` is known, so the
    // answer comes from a match over `JobStatus<'T>` and not from where a case
    // sits in the declaration.
    let terminalTests = ConcurrentDictionary<string, obj -> bool>()

    /// True if `status` is terminal for `jobId`. Queued and Running are live and
    /// must NOT be evicted: a client polling against a live job would see
    /// "job-not-found", indistinguishable from a genuine failure.
    let isTerminal (jobId: string) (status: obj) =
        match terminalTests.TryGetValue jobId with
        | true, test -> test status
        | false, _ -> false

    let forget (jobId: string) =
        statuses.TryRemove jobId |> ignore
        owners.TryRemove jobId |> ignore
        terminalTests.TryRemove jobId |> ignore

        match cancellations.TryRemove jobId with
        | true, cts -> cts.Dispose()
        | false, _ -> ()

    // Only TERMINAL entries go. Queued / Running stay until they complete (or
    // the host shuts down); if none is terminal at the cap, the new Enqueue is
    // refused with a saturation signal so the operator sees the pressure
    // instead of silent job loss.
    let fifo =
        BoundedFifo(
            cap,
            (fun () -> statuses.Count),
            statuses.ContainsKey,
            forget,
            {
                DrainBatch = 1
                Evictable =
                    fun jobId ->
                        match statuses.TryGetValue jobId with
                        | true, status -> isTerminal jobId status
                        | false, _ -> true
                OnPressure =
                    function
                    | FifoPressure.AllHeld ->
                        invalidOp (
                            "InMemoryJobDispatcher saturated: every tracked job is live "
                            + "(Queued or Running) and the maxJobs cap has been reached. "
                            + "Increase `maxJobs` at construction time, or wire a "
                            + "distributed `IJobDispatcher` impl for production loads."
                        )
                    | _ -> ()
            }
        )

    /// May the current caller see / act on `jobId`? The owner, a
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
        | true, current when isTerminal jobId current -> false
        | true, current -> statuses.TryUpdate(jobId, terminal, current)
        | false, _ -> false

    interface IJobDispatcher with
        member _.Enqueue<'T>(work: Async<'T>) : Async<JobHandle<'T>> = async {
            let jobId = Guid.NewGuid().ToString("N")
            let owner = CallContext.subjectId ()
            let correlation = CallContext.correlationId ()

            let isTerminalStatus (status: obj) =
                JobStatusTerminal.isTerminal (status :?> JobStatus<'T>)

            let cts =
                fifo.Admit(
                    jobId,
                    fun () ->
                        let cts = new CancellationTokenSource()
                        terminalTests[jobId] <- isTerminalStatus
                        owners[jobId] <- owner
                        cancellations[jobId] <- cts
                        statuses[jobId] <- box (JobStatus<'T>.Queued)
                        cts
                )

            // Background runner. Updates status to Running on
            // start, then Succeeded / Failed on completion. The
            // intermediate `Running` arm is a one-shot transition
            // — v0 doesn't track progress; consumers wanting
            // progress wire their own dispatcher.
            //
            // The submitting subject and the request's
            // correlation id are re-established inside the job so the
            // work sees its original caller whatever thread it lands on.
            // The work runs under the job's own token; `Cancel`
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