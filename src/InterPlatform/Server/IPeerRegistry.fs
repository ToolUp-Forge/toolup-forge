// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Layer 4 — peer directory ────────────────────────────────────────
//
// `IPeerRegistry` resolves a peer id (the value both deployments agree
// on out of band) into the `TargetPeer` the substrate can actually call
// — its identity plus its JSON-RPC host origin. The default
// implementation (`BlobPeerRegistry`) persists one document per peer
// under `_platform/peers/{peerId}.json` via `IConfigStore` / blob
// storage, so the directory survives a restart and is editable by an
// admin surface. Identity is always carried by value (GP 12 rule 1) —
// the registry returns a `TargetPeer` record, never a live connection.
//
// Six portability rules (GP 12):
//   1. Identity by value — keyed by `peerId` string; returns
//      `TargetPeer` records.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry / supervision as data — `Register` failure is `PeerError`.
//   4. Stateless between calls — the registry reads through to its
//      backing store on every call; no in-memory authority is assumed.
//   5. No cross-shard ordering — directory reads / writes are
//      independent.
//   6. Precision at the lower bound — no timing promise; the registry
//      is a key/value directory.

type IPeerRegistry =
    /// Resolve a peer id to its callable `TargetPeer`. Returns `None`
    /// for an unknown peer — does not throw — so a caller can map the
    /// miss to `PeerContractNotFound` / a routing decision of its own.
    abstract Resolve: peerId: string -> Async<TargetPeer option>

    /// Enumerate every registered peer. Powers an admin directory view
    /// and the cascade router's loop check.
    abstract List: unit -> Async<TargetPeer list>

    /// Register (or overwrite) a peer's directory entry. Idempotent —
    /// re-registering the same `peerId` replaces the entry. `Error`
    /// forwards an underlying persistence failure.
    abstract Register: target: TargetPeer -> Async<Result<unit, PeerError>>

    /// Remove a peer's directory entry. Idempotent — removing an
    /// unknown peer is a no-op.
    abstract Remove: peerId: string -> Async<unit>