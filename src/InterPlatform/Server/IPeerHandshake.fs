// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Layer 4 — capability handshake ──────────────────────────────────
//
// `IPeerHandshake` negotiates the highest contract version both
// deployments support, *before* the first contract call. Surfacing a
// version incompatibility at connect time makes it a handshake error
// (`PeerHandshakeError`), not a runtime surprise mid-call. `Negotiate`
// is the initiating side asking a target peer "what is the highest
// version of `contractId` we both speak?"; `LocalCapabilities` is the
// answer this deployment gives to an inbound handshake.
//
// Six portability rules (GP 12):
//   1. Identity by value — `TargetPeer`, `ContractVersion`,
//      `CapabilityList` are records; no live handle.
//   2. Async at every boundary — both methods return `Async<_>`.
//   3. Retry / supervision as data — failure is the
//      `PeerHandshakeError` DU (`NoMutualVersion` carries both version
//      lists so the caller can diagnose), never a callback.
//   4. Stateless between calls — each negotiation is self-contained;
//      the result is not cached inside the handshake (a caller that
//      wants to pin a negotiated version holds it itself).
//   5. No cross-shard ordering — handshakes are independent.
//   6. Precision at the lower bound — version comparison is the
//      structural `Major`-dominates-`Minor` order of `ContractVersion`;
//      no finer-grained promise is made.

type IPeerHandshake =
    /// Negotiate the highest mutually-supported version of `contractId`
    /// with `target`. Returns the resolved `ContractVersion` on success;
    /// `NoMutualVersion` (carrying both sides' version lists) when the
    /// intersection is empty; `HandshakeUnreachable` / `HandshakeRejected`
    /// for transport / policy failures.
    abstract Negotiate: target: TargetPeer * contractId: string -> Async<Result<ContractVersion, PeerHandshakeError>>

    /// The set of contracts (and supported versions) this deployment
    /// advertises to an inbound handshake — the receiving side of a
    /// peer's `Negotiate`.
    abstract LocalCapabilities: unit -> Async<CapabilityList>