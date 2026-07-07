// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent

// ─── Layer 5 — federation orchestration: fan-out ─────────────────────
//
// `IPeerFanout` fans one logical call out to N target peers concurrently
// and collects their typed results as a `Map<PeerIdentity, Result<'T,
// PeerError>>` so per-peer success / failure is preserved — a federation
// must tolerate partial failure, never collapse it. The substrate
// deliberately ships NO aggregation: "correct" combination of N answers
// (overlap, dedup, weighting) is a deployment-domain concern and stays
// the caller's responsibility (GP 1 — the SDK isolates mechanism, not
// domain judgement).
//
// Six portability rules (GP 12):
//   1. Identity by value — `TargetPeer` / `PeerIdentity` records keyed by
//      string id; no live handle crosses the seam.
//   2. Async at every boundary — `Fanout` returns `Async<_>`.
//   3. Policy as data — timeout / quorum / cancel-on-first ride a
//      `FanoutPolicy` record, not callbacks.
//   4. Stateless between calls — every `Fanout` is self-contained; the
//      default impl holds no cross-call state.
//   5. No cross-peer ordering — concurrent calls carry no ordering
//      promise; the result map is unordered by construction.
//   6. Precision at the lower bound — the `Timeout` is a best-effort
//      deadline, not a hard real-time guarantee.

/// How a fan-out completes. The default `FanoutPolicy.all` waits for
/// every peer; the early-return modes trade completeness for latency.
type FanoutPolicy = {
    /// Overall best-effort deadline. A peer that has not answered when
    /// the deadline elapses is recorded as `Error (PeerTransport "…")`
    /// and its in-flight call is cancelled. `None` = no deadline.
    Timeout: TimeSpan option
    /// Return once this many peers have answered (with EITHER `Ok` or
    /// `Error` — an errored peer has still answered). Remaining peers are
    /// cancelled and recorded as not-awaited. `None` = wait for all.
    Quorum: int option
    /// Return as soon as ANY peer answers `Ok`; cancel the rest. Layered
    /// over `Quorum` — whichever condition fires first wins.
    CancelOnFirstSuccess: bool
}

[<RequireQualifiedAccess>]
module FanoutPolicy =
    /// Wait for every peer, no deadline. The safe default — total result
    /// map, no early cancellation.
    let all: FanoutPolicy = {
        Timeout = None
        Quorum = None
        CancelOnFirstSuccess = false
    }

    /// Wait for every peer but bound the wall-clock by `timeout`.
    let withTimeout (timeout: TimeSpan) : FanoutPolicy = { all with Timeout = Some timeout }

    /// Return once `k` peers have answered (any outcome), cancelling the
    /// rest. `k` is clamped to `[1, targetCount]` at fan-out time.
    let quorum (k: int) : FanoutPolicy = { all with Quorum = Some k }

    /// Return the first `Ok` and cancel the rest. Useful for redundant
    /// read replicas where any one authoritative answer suffices.
    let firstSuccess: FanoutPolicy = { all with CancelOnFirstSuccess = true }

/// Fan one logical call out to N peers, collecting a per-peer result map.
/// The default implementation is `DefaultPeerFanout`; the interface is a
/// seam so a deployment can substitute its own scheduling / back-pressure
/// policy (GP 13 — nothing is registered unless a consumer composes it).
type IPeerFanout =
    /// Run `call` against every target concurrently and collect the
    /// outcomes. The returned map is total over `targets`: a peer that is
    /// cancelled by an early-return policy is present with a descriptive
    /// `Error`, so the caller can always distinguish answered-ok,
    /// answered-error, and not-awaited. Exceptions raised by `call` are
    /// captured as `Error (PeerTransport …)` — a fan-out never throws for
    /// a single peer's failure (partial-failure tolerance).
    abstract Fanout:
        targets: TargetPeer list * policy: FanoutPolicy * call: (TargetPeer -> Async<Result<'T, PeerError>>) ->
            Async<Map<PeerIdentity, Result<'T, PeerError>>>

/// Default in-process fan-out. Launches every call concurrently, honours
/// the policy's early-return / deadline rules via a single cancellation
/// token, and fills any un-awaited peer with a descriptive error so the
/// result map is total. Stateless between calls (GP 12 rule 4).
type DefaultPeerFanout() =

    /// Wrap a single peer call so an exception becomes a structured
    /// `PeerError` rather than tearing down the whole fan-out.
    static let callGuarded (call: TargetPeer -> Async<Result<'T, PeerError>>) (target: TargetPeer) = async {
        try
            return! call target
        with ex ->
            return Error(PeerTransport ex.Message)
    }

    interface IPeerFanout with
        member _.Fanout(targets, policy, call) = async {
            match targets with
            | [] -> return Map.empty
            | _ ->
                let total = List.length targets

                // Per-peer result slots, written exactly once by the
                // owning child computation.
                let results = ConcurrentDictionary<string, Result<'T, PeerError>>()

                // Cancels in-flight calls when an early-return policy
                // is satisfied or the deadline elapses.
                use cts = new CancellationTokenSource()

                // Completes when the stop condition is first met.
                let stop =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                let quorumTarget = policy.Quorum |> Option.map (fun k -> max 1 (min k total))

                // Evaluate the stop condition after each answer.
                let checkDone () =
                    let answered = results.Count

                    let firstSuccessMet =
                        policy.CancelOnFirstSuccess && results.Values |> Seq.exists Result.isOk

                    let quorumMet =
                        match quorumTarget with
                        | Some k -> answered >= k
                        | None -> false

                    if answered >= total || firstSuccessMet || quorumMet then
                        stop.TrySetResult(()) |> ignore

                // Launch one guarded child per target. Each records its
                // own slot, then re-checks the stop condition.
                for target in targets do
                    let work = async {
                        let! r = callGuarded call target
                        results[target.Peer.PeerId] <- r
                        checkDone ()
                    }

                    Async.Start(work, cts.Token)

                // Wait for the stop condition or the optional deadline.
                let stopTask = stop.Task

                match policy.Timeout with
                | Some t when t > TimeSpan.Zero ->
                    let delay = Task.Delay(t)
                    let! _ = Async.AwaitTask(Task.WhenAny(stopTask, delay))
                    ()
                | _ -> do! Async.AwaitTask stopTask

                // Stop in-flight work for un-awaited peers.
                cts.Cancel()

                // Build a total map: every target gets a slot. Peers
                // that never answered (early return / timeout) carry a
                // descriptive transport error rather than vanishing.
                return
                    targets
                    |> List.map (fun target ->
                        let outcome =
                            match results.TryGetValue target.Peer.PeerId with
                            | true, r -> r
                            | false, _ ->
                                Error(
                                    PeerTransport
                                        "peer not awaited (fan-out policy satisfied before this peer answered)"
                                )

                        target.Peer, outcome)
                    |> Map.ofList
        }

[<RequireQualifiedAccess>]
module PeerFanout =
    /// Construct the default fan-out. Equivalent to `DefaultPeerFanout()`
    /// behind the `IPeerFanout` seam — handy at call sites that want the
    /// interface type without naming the concrete class.
    let create () : IPeerFanout = DefaultPeerFanout() :> IPeerFanout