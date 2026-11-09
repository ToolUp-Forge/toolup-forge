module ToolUp.Platform.Tests.Contracts.IRemotingRateLimitStoreContract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Remoting.Server

/// Phase 1068 — contract assertions every remoting rate-limit store
/// (`TryAcquire`) must satisfy, the twin of `IRateLimitStoreContract`
/// (the Phase 56 `IncrementAndCheck` interface). Bound by `InProcess`
/// test files that supply a factory returning a fresh store. Properties:
///
/// 1. **Atomic admission.** N callers released together against one
///    remaining slot admit exactly one (Phase 870).
/// 2. **The window slides.** A slot frees when ITS admission leaves the
///    trailing window, not at a calendar boundary.
/// 3. **A denial says when to come back.** `retryAfter` is positive and
///    never longer than the window.
/// 4. **Key isolation.** One key's admissions never spend another's.
/// 5. **A non-positive budget admits** — the documented edge of the
///    in-memory store, held so a second implementation cannot quietly
///    turn a misconfigured budget into a refusal of every caller.
///
/// Every case runs on the real clock, because a distributed store has
/// no clock this pack can inject; the sliding case keeps ~800 ms of
/// headroom either side of each expiry.
let tests (name: string) (factory: unit -> IRemotingRateLimitStore) =
    let key () =
        sprintf "pack-%s" (Guid.NewGuid().ToString "N")

    let minute = TimeSpan.FromMinutes 1.0

    let admitted =
        function
        | Admission.Admit _ -> true
        | Admission.Deny _ -> false

    testList $"{name} — IRemotingRateLimitStore contract" [
        testCaseAsync "admits up to the budget and denies past it"
        <| async {
            let store = factory ()
            let k = key ()
            let! first = store.TryAcquire(k, 2, minute)
            let! second = store.TryAcquire(k, 2, minute)
            let! third = store.TryAcquire(k, 2, minute)

            Expect.isTrue (admitted first && admitted second) "the first two calls fit a budget of 2"
            Expect.isFalse (admitted third) "the third call is over the budget"
        }

        testCaseAsync "a denial carries a positive retry-after no longer than the window"
        <| async {
            let store = factory ()
            let k = key ()
            let! _ = store.TryAcquire(k, 1, minute)
            let! denied = store.TryAcquire(k, 1, minute)

            match denied with
            | Admission.Deny retryAfter ->
                Expect.isGreaterThan retryAfter TimeSpan.Zero "retry-after is positive"
                Expect.isLessThanOrEqual retryAfter minute "retry-after never exceeds the window"
            | Admission.Admit _ -> failtest "a budget of 1 admits once"
        }

        testCaseAsync "key isolation — one key's admissions never spend another's"
        <| async {
            let store = factory ()
            let kA, kB = key (), key ()
            let! _ = store.TryAcquire(kA, 1, minute)
            let! b = store.TryAcquire(kB, 1, minute)
            Expect.isTrue (admitted b) "B's budget is untouched by A's admission"
        }

        testCaseAsync "a non-positive budget admits"
        <| async {
            let store = factory ()
            let k = key ()
            let! zero = store.TryAcquire(k, 0, minute)
            let! negative = store.TryAcquire(k, -1, minute)

            Expect.isTrue
                (admitted zero && admitted negative)
                "a non-positive budget admits rather than refusing everyone"
        }

        testCaseAsync "the window slides — a slot frees when its own admission expires"
        <| async {
            // Budget 2 over 2 s: A at t0, B at t0+1 s, C refused. At
            // t0+2.2 s A has left the trailing window and B has not, so
            // exactly one slot is free: D admitted, E refused. A fixed
            // window would free both slots at its boundary or neither.
            let store = factory ()
            let k = key ()
            let window = TimeSpan.FromSeconds 2.0
            let started = DateTimeOffset.UtcNow
            let! a = store.TryAcquire(k, 2, window)
            do! Async.Sleep 1000
            let! b = store.TryAcquire(k, 2, window)
            let! c = store.TryAcquire(k, 2, window)
            let wait = started.AddMilliseconds 2200.0 - DateTimeOffset.UtcNow
            do! Async.Sleep(max 0 (int wait.TotalMilliseconds))
            let! d = store.TryAcquire(k, 2, window)
            let! e = store.TryAcquire(k, 2, window)

            Expect.isTrue (admitted a && admitted b) "A and B fit the budget"
            Expect.isFalse (admitted c) "C is over the budget"
            Expect.isTrue (admitted d) "D takes the slot A's expiry freed"
            Expect.isFalse (admitted e) "E finds B still inside the window"
        }

        testCaseAsync "concurrent callers against one remaining slot — exactly one is admitted"
        <| async {
            let budget = 5
            let callers = 32

            for round in 1..5 do
                let store = factory ()
                let k = key ()

                for _ in 1 .. budget - 1 do
                    let! _ = store.TryAcquire(k, budget, minute)
                    ()

                use gate = new Barrier(callers)

                let caller () =
                    Task.Factory.StartNew(
                        (fun () ->
                            gate.SignalAndWait()
                            store.TryAcquire(k, budget, minute) |> Async.RunSynchronously),
                        TaskCreationOptions.LongRunning
                    )

                let! results = [| for _ in 1..callers -> caller () |] |> Task.WhenAll |> Async.AwaitTask
                let wins = results |> Array.filter admitted |> Array.length

                Expect.equal wins 1 (sprintf "Round %d: %d callers against one slot admit exactly one" round callers)
        }
    ]