module ToolUp.Platform.Tests.InProcess.TransientFaultPolicyTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.TransientFault
open ToolUp.Platform.ResilientBlobStorage
open ToolUp.Platform.ResilientSecretStore
open ToolUp.Platform.Tests.Contracts

// ─── Phase 176 — transient-fault decorator substrate ─────────────────
//
// Proves: (1) the identity decorator is a pure pass-through — the
// existing IBlobStorage / ISecretStore contract packs re-run unchanged
// through it; (2) a classifier-positive thrown fault retries up to
// MaxRetries with the declared backoff then surfaces; (3) a non-transient
// fault is not retried; (4) the breaker opens after the threshold and
// short-circuits during cooldown (deterministic via an injected clock);
// (5) PerCallTimeout cancels a hung call.

// Distinct exception shapes so the classifier can tell transient from
// permanent without string-matching.
exception private TransientFailure
exception private PermanentFailure

let private retryTransient (ex: exn) : bool =
    match ex with
    | TransientFailure -> true
    | _ -> false

/// Run an Async to a Result, capturing any surfaced exception.
let private runResult (a: Async<'T>) : Result<'T, exn> =
    try
        Ok(Async.RunSynchronously a)
    with ex ->
        Error ex

/// An op that throws `ex` on its first `failFirst` invocations, then
/// returns `value`. The ref cell records the total call count.
let private countingOp (failFirst: int) (ex: exn) (value: 'T) =
    let calls = ref 0

    let op () = async {
        calls.Value <- calls.Value + 1

        if calls.Value <= failFirst then
            return raise ex
        else
            return value
    }

    calls, op

// ─── in-memory test doubles ──────────────────────────────────────────

/// Minimal in-memory ISecretStore (the writable contract subject).
type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// Wraps an inner IBlobStorage and throws `TransientFailure` on the first
/// `failFirst` Upload calls (then delegates). Records Upload call count so
/// a test can assert the decorator retried.
type private FlakyBlobStorage(inner: IBlobStorage, failFirst: int) =
    let mutable uploadCalls = 0
    member _.UploadCalls = uploadCalls

    interface IBlobStorage with
        member _.Upload(container, blobName, content) = async {
            uploadCalls <- uploadCalls + 1

            if uploadCalls <= failFirst then
                return raise TransientFailure
            else
                return! inner.Upload(container, blobName, content)
        }

        member _.Download(container, blobName) = inner.Download(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

// ─── factories binding the identity decorator to the contract packs ───

let private identityBlobFactory () : IBlobStorage =
    ResilientBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage(), TransientFaultPolicy.identity) :> IBlobStorage

let private identitySecretFactory () : ISecretStore =
    ResilientSecretStore(InMemorySecretStore(), TransientFaultPolicy.identity) :> ISecretStore

let tests =
    testList "TransientFaultPolicy (Phase 176)" [

        // ── BackoffSchedule truth table ──────────────────────────────
        testCase "BackoffSchedule.delayFor honours each schedule shape"
        <| fun () ->
            Expect.equal (BackoffSchedule.delayFor NoBackoff 0) TimeSpan.Zero "NoBackoff is always zero"
            Expect.equal (BackoffSchedule.delayFor NoBackoff 5) TimeSpan.Zero "NoBackoff ignores attempt"

            let fixedT = TimeSpan.FromMilliseconds 50.0
            Expect.equal (BackoffSchedule.delayFor (Fixed fixedT) 0) fixedT "Fixed is constant (attempt 0)"
            Expect.equal (BackoffSchedule.delayFor (Fixed fixedT) 9) fixedT "Fixed ignores attempt"

            let exp =
                Exponential(TimeSpan.FromMilliseconds 10.0, TimeSpan.FromMilliseconds 100.0)

            Expect.equal (BackoffSchedule.delayFor exp 0) (TimeSpan.FromMilliseconds 10.0) "exp attempt 0 = base"
            Expect.equal (BackoffSchedule.delayFor exp 1) (TimeSpan.FromMilliseconds 20.0) "exp attempt 1 = base*2"
            Expect.equal (BackoffSchedule.delayFor exp 2) (TimeSpan.FromMilliseconds 40.0) "exp attempt 2 = base*4"
            Expect.equal (BackoffSchedule.delayFor exp 3) (TimeSpan.FromMilliseconds 80.0) "exp attempt 3 = base*8"

            Expect.equal
                (BackoffSchedule.delayFor exp 4)
                (TimeSpan.FromMilliseconds 100.0)
                "exp attempt 4 clamps to max"

            Expect.equal
                (BackoffSchedule.delayFor exp 100)
                (TimeSpan.FromMilliseconds 100.0)
                "exp huge attempt clamps (no overflow)"

        // ── identity runner is a literal pass-through ────────────────
        testCaseAsync "identity runner returns the inner result with a single call"
        <| async {
            let calls, op = countingOp 0 PermanentFailure 99
            let runner = TransientFaultRunner TransientFaultPolicy.identity
            let! r = runner.Run op
            Expect.equal r 99 "identity returns the op's value"
            Expect.equal calls.Value 1 "identity calls the op exactly once"
        }

        // ── retry up to MaxRetries, then surface ─────────────────────
        testCase "transient fault retries up to MaxRetries then succeeds"
        <| fun () ->
            // 2 transient failures, then success — MaxRetries 3 allows it.
            let calls, op = countingOp 2 TransientFailure "ok"

            let policy = {
                TransientFaultPolicy.identity with
                    MaxRetries = 3
                    Backoff = Fixed(TimeSpan.FromMilliseconds 5.0)
                    RetryClassifier = retryTransient
            }

            let runner = TransientFaultRunner policy

            match runResult (runner.Run op) with
            | Ok v -> Expect.equal v "ok" "succeeds after retries"
            | Error e -> failtestf "expected success, got %O" e

            Expect.equal calls.Value 3 "1 initial attempt + 2 retries = 3 calls"

        testCase "transient fault that never clears surfaces after MaxRetries+1 attempts"
        <| fun () ->
            // Always fails; MaxRetries 2 ⇒ 3 total attempts then surfaces.
            let calls, op = countingOp 99 TransientFailure "unreached"

            let policy = {
                TransientFaultPolicy.identity with
                    MaxRetries = 2
                    Backoff = NoBackoff
                    RetryClassifier = retryTransient
            }

            let runner = TransientFaultRunner policy

            match runResult (runner.Run op) with
            | Ok _ -> failtest "expected the transient fault to surface"
            | Error TransientFailure -> ()
            | Error e -> failtestf "expected TransientFailure, got %O" e

            Expect.equal calls.Value 3 "exactly MaxRetries+1 = 3 attempts"

        // ── non-transient faults are not retried ─────────────────────
        testCase "non-transient fault is surfaced on the first attempt"
        <| fun () ->
            let calls, op = countingOp 99 PermanentFailure "unreached"

            let policy = {
                TransientFaultPolicy.identity with
                    MaxRetries = 5
                    Backoff = NoBackoff
                    RetryClassifier = retryTransient
            }

            let runner = TransientFaultRunner policy

            match runResult (runner.Run op) with
            | Ok _ -> failtest "expected the permanent fault to surface"
            | Error PermanentFailure -> ()
            | Error e -> failtestf "expected PermanentFailure, got %O" e

            Expect.equal calls.Value 1 "permanent fault is not retried"

        // ── backoff actually delays ──────────────────────────────────
        testCase "declared Fixed backoff inserts a wait between retries"
        <| fun () ->
            let _, op = countingOp 2 TransientFailure "ok"

            let policy = {
                TransientFaultPolicy.identity with
                    MaxRetries = 3
                    Backoff = Fixed(TimeSpan.FromMilliseconds 60.0)
                    RetryClassifier = retryTransient
            }

            let runner = TransientFaultRunner policy
            let sw = Stopwatch.StartNew()
            runResult (runner.Run op) |> ignore
            sw.Stop()
            // Two retries × 60ms ≈ 120ms; a loose lower bound avoids
            // scheduler-jitter flake while still proving a wait happened.
            Expect.isGreaterThan sw.Elapsed.TotalMilliseconds 90.0 "two 60ms backoffs elapsed"

        // ── breaker opens + short-circuits, then recovers ────────────
        testCase "breaker opens after threshold, short-circuits in cooldown, recovers after"
        <| fun () ->
            let mutable nowRef = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let clock () = nowRef
            let mutable shouldFail = true

            let op () = async {
                if shouldFail then
                    return raise PermanentFailure
                else
                    return 7
            }

            let policy = {
                TransientFaultPolicy.identity with
                    MaxRetries = 0
                    Breaker = Threshold(2, TimeSpan.FromSeconds 30.0)
                    RetryClassifier = fun _ -> false
            }

            let runner = TransientFaultRunner(policy, now = clock)

            // Two terminal failures trip the breaker.
            (match runResult (runner.Run op) with
             | Error PermanentFailure -> ()
             | other -> failtestf "call 1 expected PermanentFailure, got %A" other)

            (match runResult (runner.Run op) with
             | Error PermanentFailure -> ()
             | other -> failtestf "call 2 expected PermanentFailure, got %A" other)

            // Third call is short-circuited — the breaker is open.
            (match runResult (runner.Run op) with
             | Error(CircuitOpenException _) -> ()
             | other -> failtestf "call 3 expected CircuitOpenException, got %A" other)

            // Advance past the cooldown + heal the op: the breaker
            // half-opens, lets the trial through, and resets on success.
            nowRef <- nowRef.AddSeconds 31.0
            shouldFail <- false

            (match runResult (runner.Run op) with
             | Ok v -> Expect.equal v 7 "post-cooldown trial runs the op and succeeds"
             | other -> failtestf "call 4 expected Ok 7, got %A" other)

            // Breaker stays closed after the successful reset.
            (match runResult (runner.Run op) with
             | Ok v -> Expect.equal v 7 "breaker reset — subsequent calls flow"
             | other -> failtestf "call 5 expected Ok 7, got %A" other)

        // ── per-call timeout cancels a hung call ─────────────────────
        testCase "PerCallTimeout cancels a hung call"
        <| fun () ->
            let op () = async {
                do! Async.Sleep 5000
                return "never"
            }

            let policy = {
                TransientFaultPolicy.identity with
                    PerCallTimeout = Some(TimeSpan.FromMilliseconds 100.0)
                    RetryClassifier = fun _ -> false
            }

            let runner = TransientFaultRunner policy
            let sw = Stopwatch.StartNew()
            let result = runResult (runner.Run op)
            sw.Stop()

            match result with
            | Error(:? TimeoutException) -> ()
            | other -> failtestf "expected a TimeoutException, got %A" other

            Expect.isLessThan sw.Elapsed.TotalMilliseconds 2000.0 "timed out well before the 5s op would complete"

        // ── decorator wiring: a flaky inner store retries to success ──
        testCaseAsync "ResilientBlobStorage retries a transient Upload through to success"
        <| async {
            let inner = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let flaky = FlakyBlobStorage(inner, failFirst = 1)

            let policy = {
                TransientFaultPolicy.identity with
                    MaxRetries = 2
                    Backoff = Fixed(TimeSpan.FromMilliseconds 5.0)
                    RetryClassifier = retryTransient
            }

            let resilient = ResilientBlobStorage(flaky, policy) :> IBlobStorage

            match! resilient.Upload("user-test", "a.txt", [| 1uy; 2uy |]) with
            | Ok _ -> ()
            | Error e -> failtestf "expected the retried Upload to succeed, got Error %s" e

            Expect.equal flaky.UploadCalls 2 "one transient failure + one success = 2 inner Upload calls"

            match! inner.Download("user-test", "a.txt") with
            | Ok bytes -> Expect.sequenceEqual bytes [| 1uy; 2uy |] "the second attempt persisted the bytes"
            | Error e -> failtestf "Download after retried Upload failed: %s" e
        }

        // ── identity decorator preserves the FULL interface contract ──
        IBlobStorageContract.tests "ResilientBlobStorage (identity pass-through)" identityBlobFactory
        ISecretStoreContract.tests "ResilientSecretStore (identity pass-through)" identitySecretFactory
    ]