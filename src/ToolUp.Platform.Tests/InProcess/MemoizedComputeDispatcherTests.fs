module ToolUp.Platform.Tests.InProcess.MemoizedComputeDispatcherTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 485 — compute-result memoization decorator ────────────────
//
// Six claims, each asserted on WHAT THE BACKEND WAS HANDED rather than on
// what the decorator returned — a returned outcome is equally consistent
// with a memo that dispatched anyway and then answered from the cache, and
// that is the failure being excluded throughout:
//
//   1. **A hit does not dispatch.** Counted on the inner dispatcher's
//      submissions across two identical submits, paired with a control
//      that differs in exactly one key field (payload / idempotency key /
//      profile / scope) and DOES dispatch twice. A "no second dispatch"
//      assertion whose control also never dispatches proves only that the
//      fixture is inert.
//
//   2. **Concurrent duplicates dispatch exactly once.** The hard case,
//      because it cannot be served from the cache: only `Succeeded`
//      caches, and a job still running has succeeded at nothing. It is the
//      coalescing window that must hold, so the fixture puts eight submits
//      in flight against a backend that is deliberately slow, and the
//      control is the SAME eight submits with distinct idempotency keys —
//      same fixture, same concurrency, one input changed, eight dispatches.
//
//   3. **A hit never crosses a scope (GP 4).** Behaviourally (scope B
//      re-dispatches what scope A cached) and structurally (the two
//      entries are separate blobs under separate scope prefixes). Plus the
//      mutation check that makes the envelope's key cross-check
//      load-bearing: scope A's envelope planted at scope B's own path is
//      REFUSED, while scope B's own envelope at that same path is served.
//
//   4. **TTL expiry re-dispatches**, on an injected clock rather than a
//      sleep, with the just-inside-TTL control on the same fixture.
//
//   5. **Opt-in only (GP 13).** A spec with no idempotency key is never
//      cached, writes no blob, and the handle it returns is field-for-field
//      the one the inner dispatcher minted. Non-`Succeeded` terminal
//      outcomes likewise leave the cache empty.
//
//   6. **A hit costs zero budget.** Phase 451 is unshipped, so the budget
//      decorator is stood in for by a counting pass-through composed
//      exactly where 451's would sit — memo OUTSIDE, budget INSIDE — and
//      the assertion is that it observes ONE submission for two identical
//      ones. Its own control (a non-idempotent pair, two observations)
//      proves the probe can count.

// ─── Fixtures ────────────────────────────────────────────────────────

/// A backend that remembers exactly what it was handed and can be made
/// slow. The delay is what creates the in-flight window the coalescing
/// cases need; it is zero for every sequential case.
type private RecordingBackend(label: string, ?delayMs: int, ?outcome: ExternalOutcome) =
    let delay = defaultArg delayMs 0
    let sync = obj ()
    let submitted = ResizeArray<string * ExternalWorkSpec>()
    let polled = ResizeArray<ExternalHandle>()
    let cancelled = ResizeArray<ExternalHandle>()
    let mutable minted = 0
    let mutable lastMinted: ExternalHandle option = None

    member _.SubmitCount = lock sync (fun () -> submitted.Count)
    member _.Submitted = lock sync (fun () -> List.ofSeq submitted)
    member _.PollCount = lock sync (fun () -> polled.Count)
    member _.CancelCount = lock sync (fun () -> cancelled.Count)
    /// The last handle this backend minted — so a pass-through can be
    /// asserted field-for-field rather than "an Ok came back".
    member _.LastMinted = lock sync (fun () -> lastMinted)

    interface IExternalComputeDispatcher with
        member _.Backend = label

        member _.Submit(scopeId, spec) = async {
            let handle =
                lock sync (fun () ->
                    submitted.Add((scopeId, spec))
                    minted <- minted + 1

                    let handle = {
                        HandleId = Guid.NewGuid()
                        Backend = label
                        ScopeId = scopeId
                        NativeRef = sprintf "opaque://%s/%d" label minted
                        SubmittedAt = DateTime.UtcNow
                    }

                    lastMinted <- Some handle
                    handle)

            if delay > 0 then
                do! Async.Sleep delay

            return Ok handle
        }

        member _.Poll(handle) = async {
            lock sync (fun () -> polled.Add handle)
            return outcome |> Option.defaultValue (ExternalOutcome.Succeeded "blob://out/1")
        }

        member _.Cancel(handle) = async { lock sync (fun () -> cancelled.Add handle) }

/// Stands in for [Phase 451](451-compute-budget-governance.md)'s
/// `BudgetedComputeDispatcher`, which is unshipped. It does exactly what
/// the budget decorator does that this phase cares about: it observes
/// every submission that reaches it. Composed INSIDE the memo, so an
/// observation here is a submission the memo failed to absorb — i.e. spend.
type private SpendProbe(inner: IExternalComputeDispatcher) =
    // A heap array, not a `let mutable` field: `Interlocked` needs a byref
    // and the increment below sits inside an `async` closure, which cannot
    // address-take a field.
    let spend: int64[] = Array.zeroCreate 1

    member _.Spend = Threading.Interlocked.Read(&spend[0])

    interface IExternalComputeDispatcher with
        member _.Backend = inner.Backend

        member _.Submit(scopeId, spec) = async {
            Threading.Interlocked.Increment(&spend[0]) |> ignore
            return! inner.Submit(scopeId, spec)
        }

        member _.Poll(handle) = inner.Poll handle
        member _.Cancel(handle) = inner.Cancel handle

/// Injected clock, so TTL expiry is asserted by moving time rather than by
/// sleeping through it.
type private TestClock(start: DateTime) =
    let mutable current = start
    member _.Now = current
    member _.Advance(delta: TimeSpan) = current <- current + delta
    member this.Reader: unit -> DateTime = fun () -> this.Now

let private ttl = TimeSpan.FromMinutes 30.0

let private idempotentSpec (idem: string) (payload: string) =
    ExternalWorkSpec.create "train-forecast" payload
    |> ExternalWorkSpec.withIdempotency idem

let private spec1 = idempotentSpec "idem-1" """{"n":1}"""

/// A spec with NO idempotency key — the never-memoizable shape.
let private bareSpec = ExternalWorkSpec.create "train-forecast" """{"n":1}"""

let private submit (dispatcher: IExternalComputeDispatcher) (scopeId: string) (spec: ExternalWorkSpec) =
    dispatcher.Submit(scopeId, spec) |> Async.RunSynchronously

let private okHandle (result: Result<ExternalHandle, ExternalComputeError>) =
    match result with
    | Ok handle -> handle
    | Error error ->
        failtestf "expected an accepted submission, got a refusal: %s" (ExternalComputeError.describe error)

/// Submit, then poll to terminal — the memo learns a result only through a
/// `Poll` that observes `Succeeded`, so this is the "one complete unit of
/// work" helper every hit case needs.
let private submitAndPoll (dispatcher: IExternalComputeDispatcher) (scopeId: string) (spec: ExternalWorkSpec) =
    let handle = submit dispatcher scopeId spec |> okHandle
    let outcome = dispatcher.Poll handle |> Async.RunSynchronously
    handle, outcome

let private blobNames (blobs: IBlobStorage) (prefix: string) =
    blobs.List(ComputeMemoLayout.DefaultContainer, prefix)
    |> Async.RunSynchronously
    |> List.sort

// ─── 1. A hit returns without dispatching ────────────────────────────

let memoizationTests =
    testList "Phase 485 — outcome memoization" [
        test "an identical idempotent spec within TTL returns the cached outcome with no dispatch" {
            let backend = RecordingBackend "pool"

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            let first, firstOutcome = submitAndPoll memo "team-1" spec1
            let second = submit memo "team-1" spec1 |> okHandle

            Expect.equal
                backend.SubmitCount
                1
                "the second submit must not reach the backend — counted on the backend, because a cached-looking return value is equally consistent with a re-dispatch"

            Expect.equal
                second
                first
                "the hit replays the ORIGINAL handle verbatim, so a caller cannot tell a memo hit from Phase 318's own idempotent-submit behaviour"

            Expect.equal
                firstOutcome
                (ExternalOutcome.Succeeded "blob://out/1")
                "the first poll saw the backend's outcome"

            Expect.equal
                (memo.Poll second |> Async.RunSynchronously)
                (ExternalOutcome.Succeeded "blob://out/1")
                "and polling the replayed handle answers from the cache"

            Expect.equal backend.PollCount 1 "the second poll did not reach the backend either"
        }

        test "control — a different payload under the same idempotency key dispatches twice" {
            // The same fixture and the same key EXCEPT the payload hash.
            // Without this, "the second submit did not dispatch" could be
            // passing because the fixture never dispatches twice at all.
            let backend = RecordingBackend "pool"

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            submitAndPoll memo "team-1" (idempotentSpec "idem-1" """{"n":2}""") |> ignore

            Expect.equal
                backend.SubmitCount
                2
                "a payload byte change is a different entry — the payload hash is genuinely in the key"
        }

        test "control — a different idempotency key over the same payload dispatches twice" {
            let backend = RecordingBackend "pool"

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            submitAndPoll memo "team-1" (idempotentSpec "idem-2" """{"n":1}""") |> ignore

            Expect.equal
                backend.SubmitCount
                2
                "the caller-minted idempotency key is part of the identity, not decoration"
        }

        test "the execution profile is part of the key — an Isolated spec is never served a Standard result" {
            // The deliberate extension of the phase's four-tuple. The
            // idempotency key is caller-minted, so nothing stops one being
            // reused across profiles; without the profile in the key an
            // Isolated submission could replay work a non-isolating
            // backend ran, which is the Phase 478 leak arriving through a
            // cache instead of through a route.
            let backend = RecordingBackend "pool"

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            submitAndPoll memo "team-1" (ExternalWorkSpec.isolated spec1) |> ignore

            Expect.equal backend.SubmitCount 2 "the Isolated submission is a separate entry and re-dispatches"

            // Control on the same fixture: a SECOND Isolated submission of
            // the same spec does hit, so the assertion above is about the
            // profile and not about Isolated specs being unmemoizable.
            submitAndPoll memo "team-1" (ExternalWorkSpec.isolated spec1) |> ignore

            Expect.equal backend.SubmitCount 2 "control — Isolated memoizes against itself"
        }

        test "only Succeeded caches — a Failed outcome re-dispatches" {
            let failure = ExternalComputeError.retriable "backend saturated"

            let backend = RecordingBackend("pool", outcome = ExternalOutcome.Failed failure)

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            submitAndPoll memo "team-1" spec1 |> ignore

            Expect.equal
                backend.SubmitCount
                2
                "caching a retriable failure would turn a transient blip into a TTL-long outage for that spec"

            Expect.equal (memo :?> MemoizedComputeDispatcher).Stats.Stored 0L "and nothing was stored"
        }

        test "only Succeeded caches — a Cancelled outcome re-dispatches" {
            let backend = RecordingBackend("pool", outcome = ExternalOutcome.Cancelled)

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            submitAndPoll memo "team-1" spec1 |> ignore

            Expect.equal
                backend.SubmitCount
                2
                "a cancellation is a decision about one submission, not a fact about the work"
        }

        test "a still-running submission is not a hit — the sequential re-submit reaches the backend" {
            // The documented boundary between the two windows: coalescing
            // covers a duplicate that arrives while the first SUBMIT is
            // outstanding; memoization covers one that arrives after the
            // work FINISHED. A sequential re-submit of still-running work
            // is neither, and Phase 318's backend-side idempotency
            // contract is what covers it. Pinned so the boundary cannot
            // move silently.
            let backend = RecordingBackend("pool", outcome = ExternalOutcome.Running(Some 0.5))

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            submitAndPoll memo "team-1" spec1 |> ignore

            Expect.equal backend.SubmitCount 2 "a non-terminal outcome caches nothing"
        }

        test "stats attribute the fast answer — one miss, one hit, one store" {
            let backend = RecordingBackend "pool"
            let memo = MemoizedComputeDispatcher(backend, ttl = ttl)

            submitAndPoll (memo :> IExternalComputeDispatcher) "team-1" spec1 |> ignore
            submit (memo :> IExternalComputeDispatcher) "team-1" spec1 |> okHandle |> ignore

            let stats = memo.Stats
            Expect.equal stats.Misses 1L "the first submit missed"
            Expect.equal stats.Hits 1L "the second was served from the cache"
            Expect.equal stats.Stored 1L "one terminal outcome was recorded"
            Expect.equal stats.Coalesced 0L "nothing was concurrent"
            Expect.equal stats.Entries 1 "one live entry"
        }
    ]

// ─── 2. Concurrent duplicates dispatch exactly once ──────────────────

let coalescingTests =
    testList "Phase 485 — in-flight coalescing" [
        test "eight concurrent identical submissions dispatch exactly once and share one handle" {
            // A cache cannot serve these: nothing has succeeded yet. The
            // coalescing window is what must hold, so the backend is made
            // slow enough that all eight are inside the memo before the
            // leader's submit returns. The delay is a full second rather
            // than "enough" — a straggler that arrived after the window
            // closed would dispatch a second time and read as a genuine
            // coalescing failure, so the margin is deliberately wide.
            let backend = RecordingBackend("pool", delayMs = 1000)

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            let results =
                Array.init 8 (fun _ -> memo.Submit("team-1", spec1))
                |> Async.Parallel
                |> Async.RunSynchronously

            Expect.equal backend.SubmitCount 1 "exactly one dispatch for eight concurrent identical submissions"

            let handles = results |> Array.map okHandle |> Array.distinct

            Expect.equal
                handles.Length
                1
                "all eight callers hold the SAME handle, so all eight polls resolve to the one execution's one outcome"
        }

        test "control — eight concurrent submissions with distinct idempotency keys dispatch eight times" {
            // Same fixture, same concurrency, same delay; only the key
            // differs. If this also collapsed to one dispatch, the case
            // above would be measuring the fixture rather than the
            // coalescer.
            let backend = RecordingBackend("pool", delayMs = 1000)

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            let results =
                Array.init 8 (fun i -> memo.Submit("team-1", idempotentSpec (sprintf "idem-%d" i) """{"n":1}"""))
                |> Async.Parallel
                |> Async.RunSynchronously

            Expect.equal backend.SubmitCount 8 "distinct work coalesces onto nothing"

            let handles = results |> Array.map okHandle |> Array.distinct
            Expect.equal handles.Length 8 "and every caller holds its own handle"
        }

        test "coalescing is counted, and the joiners are counted as joiners" {
            let backend = RecordingBackend("pool", delayMs = 1000)
            let memo = MemoizedComputeDispatcher(backend, ttl = ttl)

            Array.init 8 (fun _ -> (memo :> IExternalComputeDispatcher).Submit("team-1", spec1))
            |> Async.Parallel
            |> Async.RunSynchronously
            |> ignore

            let stats = memo.Stats

            Expect.equal
                stats.Coalesced
                7L
                "seven of the eight were joined to the leader's submit — the counter is what tells an operator a burst was absorbed rather than served"

            Expect.equal
                stats.Misses
                8L
                "all eight missed the cache; coalescing is a subset of misses, not an alternative to them"

            Expect.equal stats.Hits 0L "and none of them was a cache hit — that is the whole point of this case"
        }

        test "the window closes when the submit returns — a later duplicate is a cache decision, not a coalescing one" {
            let backend = RecordingBackend "pool"
            let memo = MemoizedComputeDispatcher(backend, ttl = ttl)

            submitAndPoll (memo :> IExternalComputeDispatcher) "team-1" spec1 |> ignore
            submit (memo :> IExternalComputeDispatcher) "team-1" spec1 |> okHandle |> ignore

            Expect.equal
                (memo.Stats).Coalesced
                0L
                "a sequential duplicate must be served by the cache, never by a stale in-flight entry"

            Expect.equal (memo.Stats).Hits 1L "and it was"
        }
    ]

// ─── 3. Scope isolation (GP 4) ───────────────────────────────────────

let scopeIsolationTests =
    testList "Phase 485 — scope isolation" [
        test "a cache hit never crosses a scope" {
            let backend = RecordingBackend "pool"
            let blobs = InMemoryBlobStorage() :> IBlobStorage

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl, blobs = blobs) :> IExternalComputeDispatcher

            let teamOne, _ = submitAndPoll memo "team-1" spec1
            let teamTwo, teamTwoOutcome = submitAndPoll memo "team-2" spec1

            Expect.equal
                backend.SubmitCount
                2
                "the identical spec under a second scope is a second entry and dispatches"

            Expect.notEqual teamTwo.HandleId teamOne.HandleId "and team-2 holds its own handle, never team-1's"
            Expect.equal teamTwo.ScopeId "team-2" "the handle carries its own scope"
            Expect.equal teamTwoOutcome (ExternalOutcome.Succeeded "blob://out/1") "team-2 got its own outcome"

            // Control: a second submit under team-2 DOES hit, so the
            // assertion above is about the scope boundary and not about
            // this fixture being unable to hit at all.
            submit memo "team-2" spec1 |> okHandle |> ignore
            Expect.equal backend.SubmitCount 2 "control — team-2 memoizes against itself"
        }

        test "the entries are structurally partitioned — one blob per scope, under that scope's prefix" {
            let backend = RecordingBackend "pool"
            let blobs = InMemoryBlobStorage() :> IBlobStorage

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl, blobs = blobs) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            submitAndPoll memo "team-2" spec1 |> ignore

            let one = blobNames blobs (ComputeMemoLayout.scopePrefix "team-1")
            let two = blobNames blobs (ComputeMemoLayout.scopePrefix "team-2")

            Expect.equal one.Length 1 "team-1 has exactly one entry blob"
            Expect.equal two.Length 1 "team-2 has exactly one entry blob"

            Expect.isTrue
                (one
                 |> List.forall (fun name -> name.StartsWith("compute-memo/team-1/", StringComparison.Ordinal)))
                "and it sits under team-1's prefix — the isolation is in the path, not in a filter a caller could forget"

            Expect.notEqual one two "the two scopes' entry names differ, so neither enumeration can reach the other"
        }

        test "an envelope from another scope planted at this scope's own path is refused" {
            // The mutation check on the key cross-check. Without it, a
            // mis-derived path or a digest collision would be served, and
            // the failure would be one tenant reading another's result.
            let backend = RecordingBackend "pool"
            let blobs = InMemoryBlobStorage() :> IBlobStorage

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl, blobs = blobs) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore

            let keyOne = (ComputeMemoKey.forSpec "team-1" spec1).Value
            let keyTwo = (ComputeMemoKey.forSpec "team-2" spec1).Value

            let foreign =
                blobs.Download(
                    ComputeMemoLayout.DefaultContainer,
                    ComputeMemoLayout.entryBlob "team-1" (ComputeMemoKey.digest keyOne)
                )
                |> Async.RunSynchronously
                |> function
                    | Ok bytes -> bytes
                    | Error e -> failtestf "team-1's entry should exist: %s" e

            // Plant team-1's envelope at exactly the name a team-2 lookup
            // will construct. A memo that trusted the path would serve it.
            blobs.Upload(
                ComputeMemoLayout.DefaultContainer,
                ComputeMemoLayout.entryBlob "team-2" (ComputeMemoKey.digest keyTwo),
                foreign
            )
            |> Async.RunSynchronously
            |> ignore

            let before = backend.SubmitCount
            submit memo "team-2" spec1 |> okHandle |> ignore

            Expect.equal
                backend.SubmitCount
                (before + 1)
                "the foreign envelope is read as a miss and the work is dispatched — a re-dispatch is the only acceptable degradation"
        }

        test "control — this scope's own envelope at that same path IS served" {
            // Same planting mechanism, same path, correct envelope. Proves
            // the refusal above is the cross-check firing and not the blob
            // tier being unreadable.
            let backendOne = RecordingBackend "pool-1"
            let blobs = InMemoryBlobStorage() :> IBlobStorage

            let writer =
                MemoizedComputeDispatcher(backendOne, ttl = ttl, blobs = blobs) :> IExternalComputeDispatcher

            submitAndPoll writer "team-2" spec1 |> ignore

            // A fresh memo over a fresh backend: the only way it can
            // answer without dispatching is the blob the first one wrote.
            let backendTwo = RecordingBackend "pool-2"

            let reader =
                MemoizedComputeDispatcher(backendTwo, ttl = ttl, blobs = blobs) :> IExternalComputeDispatcher

            submit reader "team-2" spec1 |> okHandle |> ignore
            Expect.equal backendTwo.SubmitCount 0 "the correctly-scoped envelope is served"
        }

        test "a corrupt envelope is a miss, never a crash" {
            let backend = RecordingBackend "pool"
            let blobs = InMemoryBlobStorage() :> IBlobStorage

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl, blobs = blobs) :> IExternalComputeDispatcher

            let key = (ComputeMemoKey.forSpec "team-1" spec1).Value

            blobs.Upload(
                ComputeMemoLayout.DefaultContainer,
                ComputeMemoLayout.entryBlob "team-1" (ComputeMemoKey.digest key),
                Text.Encoding.UTF8.GetBytes "not json at all {{{"
            )
            |> Async.RunSynchronously
            |> ignore

            let handle = submit memo "team-1" spec1 |> okHandle

            Expect.equal backend.SubmitCount 1 "the unreadable entry degraded to a dispatch"
            Expect.equal handle.Backend "pool" "and the caller got a real handle from the backend"
        }
    ]

// ─── 4. TTL ──────────────────────────────────────────────────────────

let ttlTests =
    testList "Phase 485 — TTL" [
        test "an entry past its TTL re-dispatches" {
            let backend = RecordingBackend "pool"
            let clock = TestClock(DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc))
            let blobs = InMemoryBlobStorage() :> IBlobStorage

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl, blobs = blobs, clock = clock.Reader)
                :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            clock.Advance(ttl + TimeSpan.FromMinutes 1.0)
            submit memo "team-1" spec1 |> okHandle |> ignore

            Expect.equal backend.SubmitCount 2 "the expired entry is not served"

            Expect.equal
                (blobNames blobs (ComputeMemoLayout.scopePrefix "team-1"))
                []
                "and the expired blob was reclaimed on the read that found it — the blob tier's bound is the TTL"
        }

        test "control — the same fixture just inside the TTL does not re-dispatch" {
            let backend = RecordingBackend "pool"
            let clock = TestClock(DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc))

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl, clock = clock.Reader) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            clock.Advance(ttl - TimeSpan.FromMinutes 1.0)
            submit memo "team-1" spec1 |> okHandle |> ignore

            Expect.equal
                backend.SubmitCount
                1
                "one minute short of the TTL is still a hit — the boundary is the TTL, not the clock moving at all"
        }

        test "expiry is counted" {
            let backend = RecordingBackend "pool"
            let clock = TestClock(DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc))
            let memo = MemoizedComputeDispatcher(backend, ttl = ttl, clock = clock.Reader)

            submitAndPoll (memo :> IExternalComputeDispatcher) "team-1" spec1 |> ignore
            clock.Advance(ttl + TimeSpan.FromMinutes 1.0)
            submit (memo :> IExternalComputeDispatcher) "team-1" spec1 |> okHandle |> ignore

            Expect.equal
                memo.Stats.Expired
                1L
                "an expiry is distinguishable from a plain miss — otherwise a mis-set TTL looks like cold traffic"
        }
    ]

// ─── 5. Opt-in / GP 13 ───────────────────────────────────────────────

let optInTests =
    testList "Phase 485 — opt-in only (GP 13)" [
        test "a spec with no idempotency key is never cached and passes through field-for-field" {
            let backend = RecordingBackend "pool"
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let memo = MemoizedComputeDispatcher(backend, ttl = ttl, blobs = blobs)
            let dispatcher = memo :> IExternalComputeDispatcher

            let first = submit dispatcher "team-1" bareSpec |> okHandle

            Expect.equal
                (Some first)
                backend.LastMinted
                "the handle is the backend's own, unchanged — the memo relabels nothing, mints nothing, and stamps nothing"

            let outcome = dispatcher.Poll first |> Async.RunSynchronously
            let second = submit dispatcher "team-1" bareSpec |> okHandle

            Expect.equal outcome (ExternalOutcome.Succeeded "blob://out/1") "the outcome is the backend's"
            Expect.equal backend.SubmitCount 2 "side-effecting work is never silently deduplicated"
            Expect.notEqual second.HandleId first.HandleId "the second submission is genuinely its own"

            Expect.equal
                (blobNames blobs ComputeMemoLayout.BlobPrefix)
                []
                "and not one blob was written — a non-memoizable deployment pays no I/O"

            let stats = memo.Stats
            Expect.equal stats.Hits 0L "no hits"
            Expect.equal stats.Misses 0L "and no misses either: the key was never derived, so there was nothing to miss"
            Expect.equal stats.Entries 0 "the cache is empty"
        }

        test "a whitespace idempotency key is not an opt-in" {
            let backend = RecordingBackend "pool"
            let memo = MemoizedComputeDispatcher(backend, ttl = ttl)

            let blank =
                ExternalWorkSpec.create "train-forecast" """{"n":1}"""
                |> ExternalWorkSpec.withIdempotency "   "

            Expect.isNone (ComputeMemoKey.forSpec "team-1" blank) "a blank key is a caller defect, not an assertion"

            submitAndPoll (memo :> IExternalComputeDispatcher) "team-1" blank |> ignore
            submitAndPoll (memo :> IExternalComputeDispatcher) "team-1" blank |> ignore

            Expect.equal backend.SubmitCount 2 "so it memoizes nothing"
        }

        test "Cancel passes straight through, and a cached handle stays cancellable" {
            let backend = RecordingBackend "pool"

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            let handle, _ = submitAndPoll memo "team-1" spec1
            memo.Cancel handle |> Async.RunSynchronously
            let replayed = submit memo "team-1" spec1 |> okHandle
            memo.Cancel replayed |> Async.RunSynchronously

            Expect.equal
                backend.CancelCount
                2
                "the memo intercepts no cancellation — Phase 318 makes cancelling a terminal handle a non-throwing no-op, so there is nothing to decide"
        }

        test "the memo declares no isolation posture" {
            // Re-declaring the inner posture would be wrong in both
            // directions: over a plain backend it claims a guarantee about
            // work the memo may answer with no backend running, and over
            // Phase 484's router (which deliberately claims nothing) it
            // would read as standardOnly and refuse every Isolated
            // submission the router would have placed correctly.
            let backend = RecordingBackend "pool"

            let memo =
                MemoizedComputeDispatcher(backend, ttl = ttl) :> IExternalComputeDispatcher

            Expect.isFalse
                (match box memo with
                 | :? IIsolatedComputeBackend -> true
                 | _ -> false)
                "the decorator is not a backend and must not present as one"

            Expect.equal
                (ExecutionProfileGate.postureOf memo)
                IsolationPosture.standardOnly
                "so it reads as Phase 478's claiming-nothing identity"

            Expect.equal
                memo.Backend
                "pool"
                "and it reports the inner backend's own label, so a handle still polls against a name someone answers to"
        }
    ]

// ─── 6. Zero budget spend on a hit ───────────────────────────────────

let budgetCompositionTests =
    testList "Phase 485 — composition order (memo outside budget)" [
        test "a hit is invisible to the decorator below — zero spend" {
            // memo(budget(backend)) — the order Phase 451 must be composed
            // in. A hit returns before the inner dispatcher is consulted,
            // so the budget decorator observes nothing to charge for.
            let backend = RecordingBackend "pool"
            let budget = SpendProbe(backend)

            let memo =
                MemoizedComputeDispatcher(budget, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" spec1 |> ignore
            submit memo "team-1" spec1 |> okHandle |> ignore

            Expect.equal budget.Spend 1L "two identical submissions, one charge — the memoized hit costs zero budget"
            Expect.equal backend.SubmitCount 1 "and the backend saw one too"
        }

        test "control — the probe does count, so the zero above is the memo absorbing and not the probe failing" {
            let backend = RecordingBackend "pool"
            let budget = SpendProbe(backend)

            let memo =
                MemoizedComputeDispatcher(budget, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll memo "team-1" bareSpec |> ignore
            submitAndPoll memo "team-1" bareSpec |> ignore

            Expect.equal budget.Spend 2L "a non-memoizable pair is charged twice"
        }

        test "a concurrent burst is charged once" {
            let backend = RecordingBackend("pool", delayMs = 1000)
            let budget = SpendProbe(backend)

            let memo =
                MemoizedComputeDispatcher(budget, ttl = ttl) :> IExternalComputeDispatcher

            Array.init 8 (fun _ -> memo.Submit("team-1", spec1))
            |> Async.Parallel
            |> Async.RunSynchronously
            |> ignore

            Expect.equal
                budget.Spend
                1L
                "coalescing is a budget property too — eight duplicates must not reserve eight concurrency slots"
        }
    ]

// ─── Restart survival + bounded eviction ─────────────────────────────

let durabilityTests =
    testList "Phase 485 — restart survival" [
        test "a hit survives a restart — a fresh dispatcher over the same blob store does not dispatch" {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let before = RecordingBackend "pool-before"

            let firstRun =
                MemoizedComputeDispatcher(before, ttl = ttl, blobs = blobs) :> IExternalComputeDispatcher

            let original, _ = submitAndPoll firstRun "team-1" spec1

            // A new dispatcher instance over a NEW backend: any dispatch
            // would be visible on a counter that starts at zero, and the
            // only thing carried over is the blob.
            let after = RecordingBackend "pool-after"

            let secondRun =
                MemoizedComputeDispatcher(after, ttl = ttl, blobs = blobs) :> IExternalComputeDispatcher

            let replayed = submit secondRun "team-1" spec1 |> okHandle

            Expect.equal after.SubmitCount 0 "the restarted process serves the cached outcome without dispatching"

            Expect.equal
                replayed
                original
                "and the durable envelope round-trips the original handle field-for-field, including the backend's own opaque NativeRef"

            Expect.equal
                (secondRun.Poll replayed |> Async.RunSynchronously)
                (ExternalOutcome.Succeeded "blob://out/1")
                "the replayed handle then polls to the cached outcome"

            Expect.equal after.PollCount 0 "without reaching the new backend"
        }

        test "control — without a blob store the restart is a cold cache" {
            // Proves the case above is the blob tier doing the work, not
            // some incidental sharing between the two instances.
            let before = RecordingBackend "pool-before"

            let firstRun =
                MemoizedComputeDispatcher(before, ttl = ttl) :> IExternalComputeDispatcher

            submitAndPoll firstRun "team-1" spec1 |> ignore

            let after = RecordingBackend "pool-after"

            let secondRun =
                MemoizedComputeDispatcher(after, ttl = ttl) :> IExternalComputeDispatcher

            submit secondRun "team-1" spec1 |> okHandle |> ignore

            Expect.equal after.SubmitCount 1 "an in-process-only memo dispatches after a restart"
        }
    ]

let evictionTests =
    testList "Phase 485 — bounded eviction (Phase 328 discipline)" [
        test "the cap bounds the index, and eviction is a FIFO drain rather than a wipe" {
            // Phase 328's lesson: a mass wipe under cap pressure discards
            // live entries silently, and every one it discards is a
            // re-dispatch of work already paid for. So the OLDEST entry
            // goes and the newest survive.
            let backend = RecordingBackend "pool"
            let memo = MemoizedComputeDispatcher(backend, ttl = ttl, maxEntries = 2)
            let dispatcher = memo :> IExternalComputeDispatcher

            for i in 1..3 do
                submitAndPoll dispatcher "team-1" (idempotentSpec (sprintf "idem-%d" i) """{"n":1}""")
                |> ignore

            Expect.isLessThanOrEqual memo.Stats.Entries 2 "the index respects its cap"
            Expect.isGreaterThan memo.Stats.Evicted 0L "and it evicted rather than grew"

            let afterFill = backend.SubmitCount

            // The newest entry survived — this is the not-a-wipe assertion.
            submit dispatcher "team-1" (idempotentSpec "idem-3" """{"n":1}""")
            |> okHandle
            |> ignore

            Expect.equal
                backend.SubmitCount
                afterFill
                "the most recent entry is still served — an eviction that took everything would re-dispatch this too"

            // The oldest was dropped, so it genuinely re-dispatches.
            submit dispatcher "team-1" (idempotentSpec "idem-1" """{"n":1}""")
            |> okHandle
            |> ignore

            Expect.equal backend.SubmitCount (afterFill + 1) "and the evicted entry is a miss, not a stale hit"
        }

        test "over-cap recoveries start at zero and are reportable" {
            // The Phase 328 signal exists and is readable; it should not
            // fire under ordinary at-cap operation (the branch is the
            // count/queue race only), which is what makes a non-zero value
            // meaningful.
            let backend = RecordingBackend "pool"
            let memo = MemoizedComputeDispatcher(backend, ttl = ttl, maxEntries = 2)

            for i in 1..4 do
                submitAndPoll
                    (memo :> IExternalComputeDispatcher)
                    "team-1"
                    (idempotentSpec (sprintf "idem-%d" i) """{"n":1}""")
                |> ignore

            Expect.equal
                memo.Stats.OverCapRecoveries
                0L
                "sequential at-cap operation never hits the cap race, so a non-zero reading is a real signal rather than noise"

            Expect.equal memo.Stats.MaxEntries 2 "and the configured cap is reported for an operator to compare against"
        }
    ]