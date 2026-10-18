module ToolUp.Platform.Tests.Support.FireAndForgetHarness

// The scaffolding the fire-and-forget guard tests share (Phases 1024, 1025
// and 1026): a sink that fails in a chosen way, a hook that collects every
// unobserved task exception, and the waits that let a background write
// land before an arm asserts on it.
//
// Each arm fails its sink with a fresh marker and looks only for that
// marker, so one process-wide hook serves every suite: a sibling test's
// unobserved exception can neither fail nor pass another's arm.
//
// The `Plain` mode is the crash class. Started with a bare `Async.Start`,
// the exception is rethrown on a thread-pool thread and terminates the host,
// so an arm proving it goes red must run in a child process of its own.

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// How a fire-and-forget write fails.
type FailureMode =
    /// The write fails inside plain async code.
    | Plain
    /// The write fails behind a task boundary (`Async.AwaitTask`).
    | TaskShaped
    /// The write throws before an `Async` exists.
    | Synchronous

/// A write that fails per `mode`, raising `fault marker`. Each suite passes
/// its own exception type, which its log assertions then name.
let failWith<'a> (fault: string -> exn) (mode: FailureMode) (marker: string) : Async<'a> =
    match mode with
    | Plain -> async {
        do! Async.Sleep 5
        return raise (fault marker)
      }
    | TaskShaped -> async {
        do!
            Task.Run(
                Action(fun () ->
                    Thread.Sleep 5
                    raise (fault marker))
            )
            |> Async.AwaitTask

        return Unchecked.defaultof<'a>
      }
    | Synchronous -> raise (fault marker)

/// Every unobserved task exception the process raises, by message.
let private unobserved = ConcurrentQueue<string>()

let private hook =
    lazy
        TaskScheduler.UnobservedTaskException.Add(fun e ->
            for inner in e.Exception.Flatten().InnerExceptions do
                unobserved.Enqueue inner.Message)

/// Install the unobserved-exception hook. An arm calls it before its sink
/// can fail; installing it again is a no-op.
let watchUnobserved () = hook.Force()

/// Poll `probe` every 50 ms until it finds something, for at most
/// `attempts` polls, and return what it found last.
let settleOn (attempts: int) (probe: unit -> 'a list) = async {
    let rec go attempt = async {
        match probe () with
        | [] when attempt < attempts ->
            do! Async.Sleep 50
            return! go (attempt + 1)
        | found -> return found
    }

    return! go 0
}

/// The unobserved task exceptions carrying `marker`. A faulted, unobserved
/// task is only reported when it is finalised, so this collects first.
let leakedFor (marker: string) = async {
    for _ in 1..3 do
        GC.Collect()
        GC.WaitForPendingFinalizers()

    do! Async.Sleep 100
    GC.Collect()
    GC.WaitForPendingFinalizers()
    return unobserved.ToArray() |> Array.filter (fun m -> m.Contains marker)
}