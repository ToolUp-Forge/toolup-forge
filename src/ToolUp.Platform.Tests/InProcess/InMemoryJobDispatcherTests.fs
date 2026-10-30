module ToolUp.Platform.Tests.InProcess.InMemoryJobDispatcherTests

open System
open System.Threading.Tasks
open Expecto
open ToolUp.Remoting.Server

/// `JobStatus<'T>` with one live case added before `Succeeded`: the edit that
/// silently invalidates any test of terminality by case position.
type ReorderedJobStatus<'T> =
    | Queued
    | Paused
    | Running of progress: float
    | Succeeded of result: 'T
    | Failed of message: string
    | Cancelled

/// The ordinal test the dispatcher used to carry: read the DU's `Tag` by
/// reflection and call everything from position 2 terminal.
let private ordinalIsTerminal (status: obj) =
    let tag = status.GetType().GetProperty("Tag").GetValue status :?> int
    tag >= 2

/// Polls until `accept` holds, so a test never races the background runner.
let private waitFor (dispatcher: IJobDispatcher) (handle: JobHandle<'T>) (accept: JobStatus<'T> -> bool) = async {
    let deadline = DateTimeOffset.UtcNow.AddSeconds 10.0
    let mutable status = JobStatus<'T>.Queued
    let mutable reached = false

    while not reached && DateTimeOffset.UtcNow < deadline do
        let! current = dispatcher.GetStatus handle
        status <- current

        if accept current then
            reached <- true
        else
            do! Async.Sleep 5

    Expect.isTrue reached (sprintf "the job never reached the awaited status; last was %A" status)
}

let private isRunning (status: JobStatus<'T>) =
    match status with
    | JobStatus.Running _ -> true
    | _ -> false

let private isSucceeded (status: JobStatus<'T>) =
    match status with
    | JobStatus.Succeeded _ -> true
    | _ -> false

/// A job that stays `Running` until the returned source completes.
let private enqueueBlocked (dispatcher: IJobDispatcher) = async {
    let release = TaskCompletionSource<int>()
    let! handle = dispatcher.Enqueue(Async.AwaitTask release.Task)
    do! waitFor dispatcher handle isRunning
    return handle, release
}

[<Tests>]
let tests =
    testList "InMemoryJobDispatcher" [
        test "a case added before Succeeded makes the Tag ordinal call a live job terminal" {
            Expect.isTrue
                (ordinalIsTerminal (box (ReorderedJobStatus<int>.Running 0.5)))
                "the ordinal reads a live status as terminal once the case order moves"
        }

        testCaseAsync "a live job survives eviction under cap pressure and a succeeded one does not"
        <| async {
            let dispatcher = InMemoryJobDispatcher(maxJobs = 2)
            let jobs = dispatcher :> IJobDispatcher

            let! live, release = enqueueBlocked jobs
            let! finished = jobs.Enqueue(async { return 1 })
            do! waitFor jobs finished isSucceeded

            let! newest = jobs.Enqueue(async { return 2 })
            do! waitFor jobs newest isSucceeded

            let! liveStatus = jobs.GetStatus live
            Expect.isTrue (isRunning liveStatus) "the older live job was skipped by the sweep"

            let! finishedStatus = jobs.GetStatus finished

            Expect.equal finishedStatus (JobStatus<int>.Failed "job-not-found") "the succeeded job was the one evicted"

            Expect.equal dispatcher.JobCount 2 "the dispatcher stays at its cap"
            release.SetResult 0
            do! waitFor jobs live isSucceeded
        }

        testCaseAsync "a dispatcher whose every job is live refuses the next enqueue by name"
        <| async {
            let dispatcher = InMemoryJobDispatcher(maxJobs = 1)
            let jobs = dispatcher :> IJobDispatcher
            let! live, release = enqueueBlocked jobs

            let! outcome = jobs.Enqueue(async { return 1 }) |> Async.Catch

            match outcome with
            | Choice2Of2 ex -> Expect.stringContains ex.Message "saturated" "the refusal names the saturation"
            | Choice1Of2 _ -> failtest "an enqueue past the cap of live jobs must refuse"

            Expect.equal dispatcher.JobCount 1 "the refused job left nothing behind"
            release.SetResult 0
            do! waitFor jobs live isSucceeded
        }

        testCaseAsync "a cancelled job is terminal, so it yields its slot"
        <| async {
            let dispatcher = InMemoryJobDispatcher(maxJobs = 1)
            let jobs = dispatcher :> IJobDispatcher
            let! live, release = enqueueBlocked jobs

            do! jobs.Cancel live
            let! next = jobs.Enqueue(async { return 3 })
            do! waitFor jobs next isSucceeded

            Expect.equal dispatcher.JobCount 1 "the cancelled job was evicted for the new one"
            release.SetResult 0
        }
    ]