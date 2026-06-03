// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Layer 4 — initiator surface ─────────────────────────────────────
//
// `IPeerClient` is the *initiating* deployment's transport-level
// surface: it serialises a `PeerWirePayload` into a JSON-RPC request,
// posts it to the target peer's `/peer/v1/{contractId}` route, and
// deserialises the response into a result string or a `PeerError`. The
// typed proxy (`JsonRpcPeerClient.create<'TApi>`) is built on top of
// this — it reflects over the contract type, marshals positional
// arguments, and resolves `LongRunning` methods into `PeerJobHandle<'T>`
// whose `Poll` closure calls `PollJob` below.
//
// Six portability rules (GP 12):
//   1. Identity by value — `TargetPeer`, `PeerWirePayload`, `PeerJobId`
//      are records / primitives; no live socket handle is exposed.
//   2. Async at every boundary — both methods return `Async<_>`.
//   3. Retry / supervision as data — failure is `PeerError`; the caller
//      decides retry policy (the substrate does not auto-retry a
//      mutating call, which could double-execute).
//   4. Stateless between calls — the client carries no per-call state;
//      every `Invoke` is self-contained.
//   5. No cross-shard ordering — concurrent invokes to the same peer
//      carry no ordering promise.
//   6. Precision at the lower bound — `PollJob` is poll-driven; the
//      substrate makes no sub-request-latency promise for long-running
//      resolution.

type IPeerClient =
    /// Post a single peer call and await its immediate result. Used for
    /// `Immediate`-lifetime methods and for the first leg of a
    /// `LongRunning` call (which returns the backing `PeerJobId`,
    /// serialised). Returns the serialised method result, or a
    /// `PeerError` for transport, auth, version, or handler failures.
    abstract Invoke:
        target: TargetPeer * contractId: string * methodName: string * payload: PeerWirePayload ->
            Async<Result<string, PeerError>>

    /// Poll the target peer for the current status of a long-running
    /// call's backing job. `Completed` carries the serialised result
    /// string (the typed proxy deserialises it into `'T`); `Failed`
    /// carries the structured peer error; `Pending` means no terminal
    /// result yet. A transport-level failure of the poll itself is the
    /// outer `Error`.
    abstract PollJob:
        target: TargetPeer * contractId: string * jobId: PeerJobId -> Async<Result<PeerJobStatus<string>, PeerError>>