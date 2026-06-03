// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System

// ─── Layer 1 — long-running-call resolution ──────────────────────────
//
// `PeerJobHandle<'T>` is the typed wrapper a `LongRunning` contract
// method returns. It carries the substrate `JobId` by value (GP 12 rule
// 1) plus the poll function the client proxy supplies, so the handle is
// resolvable without holding a live transport object.

/// Substrate-assigned id of a long-running peer call's backing job.
/// Identity by value (GP 12 rule 1).
type PeerJobId = Guid

/// Current resolution state of a long-running peer call.
type PeerJobStatus<'T> =
    /// The peer's job has not yet produced a terminal result.
    | Pending
    /// The job completed with a typed result.
    | Completed of 'T
    /// The job failed; carries the structured peer error.
    | Failed of PeerError

/// Typed handle to a long-running peer call. Returned by `LongRunning`
/// contract methods as `Async<PeerJobHandle<'T>>`. Polling is the
/// default resolution mechanism — it works through one-way NAT and does
/// not require the caller to expose an inbound endpoint. Callback
/// resolution is opt-in via `PeerJobHandle.withCallback`.
type PeerJobHandle<'T> = {
    /// Id of the backing job on the receiving peer.
    JobId: PeerJobId
    /// Polls the peer for the current status. Supplied by the client
    /// proxy (`JsonRpcPeerClient`); a hand-rolled client may supply its
    /// own.
    Poll: unit -> Async<PeerJobStatus<'T>>
}

/// Resolution helpers for `PeerJobHandle<'T>`.
module PeerJobHandle =

    /// Default poll interval between status checks.
    let defaultPollInterval = TimeSpan.FromSeconds 2.0

    let rec private pollUntil (interval: TimeSpan) (handle: PeerJobHandle<'T>) = async {
        let! status = handle.Poll()

        match status with
        | Pending ->
            do! Async.Sleep(int interval.TotalMilliseconds)
            return! pollUntil interval handle
        | Completed value -> return Ok value
        | Failed err -> return Error err
    }

    /// Resolve the handle by polling at the default interval until the
    /// backing job reaches a terminal state.
    let resolve (handle: PeerJobHandle<'T>) : Async<Result<'T, PeerError>> = pollUntil defaultPollInterval handle

    /// Resolve the handle by polling at a caller-chosen interval.
    let resolveEvery (interval: TimeSpan) (handle: PeerJobHandle<'T>) : Async<Result<'T, PeerError>> =
        pollUntil interval handle

    /// Opt-in callback resolution: resolves the handle on a background
    /// async and invokes `onResolved` with the terminal result. Returns
    /// immediately. Use when the caller prefers a continuation to
    /// awaiting `resolve` inline; the underlying mechanism is still
    /// polling (no inbound endpoint required).
    let withCallback (onResolved: Result<'T, PeerError> -> unit) (handle: PeerJobHandle<'T>) : unit =
        async {
            let! result = resolve handle
            onResolved result
        }
        |> Async.Start