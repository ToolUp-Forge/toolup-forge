// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Layer 4 — receiver surface ──────────────────────────────────────
//
// `IPlatformPeer` is the *receiving* deployment's surface: contracts a
// deployment exposes to peers, plus the dispatch entry point the
// JSON-RPC host calls when a request arrives on `/peer/v1/{contractId}`.
// A deployment that does not enable the peer substrate never constructs
// one (GP 13 — zero cost when unused).
//
// Six portability rules (GP 12):
//   1. Identity by value — every parameter / return is a string, a
//      record, or a DU (`PeerCallContext`, `CapabilityList`); no live
//      connection handle crosses the surface.
//   2. Async at every boundary — `Handle` / `Capabilities` return
//      `Async<_>`. `RegisterContract` is a compose-time mutator (like
//      `IJobScheduler.RegisterHandler`), not a per-request call, so it
//      is `unit` by the same documented exception.
//   3. Retry / supervision as data — failure is the `PeerError` DU in
//      the `Result`, never an `OnFailure` callback.
//   4. Stateless handlers between invocations — `PeerDispatch` receives
//      all per-call state through `PeerCallContext` + the argument
//      string; it holds no state between dispatches.
//   5. No cross-shard ordering — each dispatch is independent; the
//      receiver makes no ordering promise across concurrent calls.
//   6. Precision at the lower bound — long-running results resolve via
//      the job substrate (`PeerJobHandle`), not a sub-request-latency
//      promise.

/// A registered contract's method-dispatch closure. Given the
/// propagated call context, the method name, and the positional
/// arguments (a JSON array string), it runs the handler and returns the
/// serialised result or a structured `PeerError`. Server-side only — it
/// holds a live closure, mirroring how `ScheduledJobDeclaration` holds
/// an `IJobHandler` instance; it never crosses the Fable boundary.
type PeerDispatch = PeerCallContext -> string -> string -> Async<Result<string, PeerError>>

/// A contract a deployment exposes to peers: its id, the wire versions
/// it supports (highest mutual wins at handshake), and the dispatch
/// closure that routes a method call to the contract implementation.
type PeerContractRegistration = {
    ContractId: string
    Versions: ContractVersion list
    Dispatch: PeerDispatch
}

/// The receiving deployment's registered-contract host. Modules / the
/// worked example register their contracts at compose time; the
/// JSON-RPC host calls `Handle` per inbound request after it has
/// authenticated the caller and rebuilt the `PeerCallContext` from the
/// wire payload.
type IPlatformPeer =
    /// Register a contract under its id. Idempotent — re-registering the
    /// same id overwrites the previous binding. Called at compose time,
    /// never per request.
    abstract RegisterContract: registration: PeerContractRegistration -> unit

    /// Dispatch an inbound call to the registered contract. Returns the
    /// serialised method result, or a `PeerError` for an unknown
    /// contract / method, a version the contract does not support, a
    /// loop / hop-limit violation, or a handler failure. The host has
    /// already validated the peer token before this is called; cascade
    /// guards (`Route`, `HopsRemaining`) are checked here so they run
    /// regardless of which host invokes the dispatch.
    abstract Handle:
        contractId: string * context: PeerCallContext * methodName: string * arguments: string ->
            Async<Result<string, PeerError>>

    /// The full set of contracts (and their supported versions) this
    /// deployment exposes — the answer to a capability handshake.
    abstract Capabilities: unit -> Async<CapabilityList>