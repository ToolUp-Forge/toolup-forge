// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IEdgeCacheContract

open System
open System.Collections.Concurrent
open System.Diagnostics
open Expecto
open ToolUp.Platform

// ─── Phase 472 — the IEdgeCache conformance bar ───────────────────────
//
// Bound over every implementation: the in-tree `NoopEdgeCache`, the
// recording fake below, and the `ToolUp.Hosts.EdgeCache` HTTP adapter
// (which proves the seam from the outside — GP 12).
//
// What the pack can honestly assert across all three is narrower than it
// looks, and the narrowness is the point. A purge is a call to someone
// else's network: this pack cannot assert that an object is gone,
// because no implementation can. What it CAN assert is that the seam is
// total and typed — every verb answers, on every input, with `Ok` or a
// named `EdgePurgeError`, and never by throwing. An implementation that
// throws on an empty list, or that reports "not supported" as a silent
// success, breaks a caller that has no way to see it.

/// A recording `IEdgeCache`. The fake every wiring test asserts against
/// — the seam's whole observable contribution is "the right purge was
/// issued", so the test double records purges and nothing else.
///
/// Thread-safe, because the in-tree callers purge on the thread pool
/// (fire-and-forget, GP 7): the recording happens on a different thread
/// from the assertion, always.
type RecordingEdgeCache(name: string, outcome: Result<unit, EdgePurgeError>) =
    let paths = ConcurrentQueue<string list>()
    let prefixes = ConcurrentQueue<string>()
    let tags = ConcurrentQueue<string list>()

    new() = RecordingEdgeCache("recording", Ok())

    member _.Paths = paths |> Seq.toList
    member _.Prefixes = prefixes |> Seq.toList
    member _.Tags = tags |> Seq.toList

    /// Every path named across every `PurgePaths` call, flattened.
    member this.AllPaths = this.Paths |> List.collect id

    member _.CallCount = paths.Count + prefixes.Count + tags.Count

    /// Block until at least `n` purges have been recorded, or `timeout`
    /// elapses. Returns whether the count was reached.
    ///
    /// The in-tree purge is detached by design, so a test that asserted
    /// immediately would be asserting on a race — and would pass or fail
    /// by scheduler luck rather than by behaviour. Polling with a
    /// deadline is the honest shape: a failure to reach the count within
    /// a generous window is a real failure, not a slow machine.
    member this.WaitFor(n: int, timeout: TimeSpan) : bool =
        let sw = Stopwatch.StartNew()

        while this.CallCount < n && sw.Elapsed < timeout do
            Threading.Thread.Sleep 10

        this.CallCount >= n

    /// `WaitFor` with the pack's standard five-second window.
    member this.WaitFor(n: int) : bool =
        this.WaitFor(n, TimeSpan.FromSeconds 5.0)

    /// Assert that NO purge arrives within a short window. Necessarily a
    /// wait rather than an instant read: "nothing was scheduled" and
    /// "something was scheduled and has not run yet" are the same
    /// observation at t=0, and only one of them is the claim.
    member this.StaysSilentFor(window: TimeSpan) : bool =
        Threading.Thread.Sleep window
        this.CallCount = 0

    interface IEdgeCache with
        member _.Name = name
        member _.Propagation = PurgeImmediate

        member _.PurgePaths(p) = async {
            paths.Enqueue p
            return outcome
        }

        member _.PurgePrefix(p) = async {
            prefixes.Enqueue p
            return outcome
        }

        member _.PurgeTags(t) = async {
            tags.Enqueue t
            return outcome
        }

/// The conformance bar every `IEdgeCache` implementation must clear.
/// `make` returns a fresh instance per case.
let tests (name: string) (make: unit -> IEdgeCache) =
    /// Every verb must ANSWER — `Ok` or a named error. An implementation
    /// legitimately returns `PurgeNotSupported` for a verb its edge does
    /// not offer; what it must not do is throw, or return `Ok` for work
    /// it did not do.
    let mustAnswer (label: string) (run: IEdgeCache -> Async<Result<unit, EdgePurgeError>>) =
        testCaseAsync (sprintf "%s: %s answers with Ok or a named error" name label)
        <| async {
            let edge = make ()

            let! result = async {
                try
                    let! r = run edge
                    return Ok r
                with ex ->
                    return Error ex
            }

            match result with
            | Error ex -> failtestf "the verb threw (%s) — failure must be data, not an exception" ex.Message
            | Ok(Ok()) -> ()
            | Ok(Error(PurgeTransportFailure d))
            | Ok(Error(PurgeRejected d)) -> Expect.isNotEmpty d "a failure must carry a describable detail"
            | Ok(Error(PurgeNotSupported verb)) -> Expect.isNotEmpty verb "an unsupported verb must name itself"
        }

    testList (sprintf "IEdgeCache contract (%s)" name) [
        testCase (sprintf "%s: declares a non-empty name" name)
        <| fun () ->
            let edge = make ()
            Expect.isNotEmpty edge.Name "an audited purge failure names the edge that failed"

        testCase (sprintf "%s: declares a propagation contract" name)
        <| fun () ->
            // Rule 6 — precision at the lower bound. There is no
            // "undeclared" case in the DU, so this asserts the shape is
            // reachable and, more usefully, that reading it has no side
            // effect and cannot throw.
            let edge = make ()

            match edge.Propagation with
            | PurgeImmediate
            | PurgeEventualWithin _
            | PurgeEventualUnbounded -> ()

        testCaseAsync (sprintf "%s: an EMPTY path purge is a success, not an error" name)
        <| async {
            // An empty purge set is what a caller with nothing to purge
            // has. Reporting it as a failure would put a permanent
            // warning in an audit trail for a non-event.
            let edge = make ()
            let! result = edge.PurgePaths []
            Expect.equal result (Ok()) "purging nothing succeeds"
        }

        mustAnswer "PurgePaths" (fun e -> e.PurgePaths [ "/a"; "/b/c" ])
        mustAnswer "PurgePrefix" (fun e -> e.PurgePrefix "/a/")
        mustAnswer "PurgeTags" (fun e -> e.PurgeTags [ "tag-1" ])

        testCaseAsync (sprintf "%s: purging the same paths twice is idempotent" name)
        <| async {
            // A purge is retried by `EdgeCache.purgeWithRetry`, and a
            // publish can legitimately fire twice. Neither may turn the
            // second call into an error.
            let edge = make ()
            let! first = edge.PurgePaths [ "/idempotent" ]
            let! second = edge.PurgePaths [ "/idempotent" ]
            Expect.equal second first "the second purge answers as the first did"
        }
    ]