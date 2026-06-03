// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System

// ─── Layer 2 — identity, versioning, cascade context ─────────────────
//
// The peer substrate's authoring + identity types. Every type here is
// shared between two ToolUp deployments acting as peers (server-to-
// server) — there is no Fable client surface. The types satisfy the six
// portability rules (GP 12): identity is carried by value (strings /
// records, never live connection handles), every type is a plain
// immutable record / DU with no framework serialisation attributes, and
// nothing here opens Akka / Orleans.

/// Stable identity of a ToolUp deployment acting as a peer. Identity by
/// value (GP 12 rule 1) — a string id that both deployments agree on out
/// of band, never a live connection handle or transport object.
type PeerIdentity = {
    /// Stable, deployment-assigned id (e.g. "buyer-acme", "seller-ssp").
    /// Used for routing, registry lookup, and audit partitioning.
    PeerId: string
    /// Human-readable label for diagnostics / audit surfaces. Not
    /// load-bearing for routing or authorisation.
    DisplayName: string
}

/// A peer contract's wire version. Records derive structural comparison
/// field-by-field in declaration order, so `Major` dominates `Minor`
/// automatically — `{ Major = 2; Minor = 0 } > { Major = 1; Minor = 9 }`.
/// Used by the capability handshake to negotiate the highest mutual
/// version.
type ContractVersion = { Major: int; Minor: int }

/// A directly-authenticated end user whose identity the calling
/// deployment vouches for. The `Direct` case of `UserContext`.
type UserAssertion = {
    /// Subject id of the user on the calling deployment.
    Subject: string
    /// Peer id of the deployment issuing (vouching for) this assertion.
    Issuer: string
    /// Optional display name for audit / diagnostic surfaces.
    DisplayName: string option
}

/// A delegated user assertion produced when a call passes through an
/// intermediary peer on behalf of an end user (buyer → broker → seller).
/// Reserved from day one even though 1:1 deployments don't use it —
/// adding the `Delegated` case later would break wire compatibility and
/// force a v2 protocol.
type DelegatedAssertion = {
    /// The end user on whose behalf the call is ultimately made.
    Subject: string
    /// Ordered chain of peer ids that delegated, originator first. The
    /// receiving side checks this against its trust anchors.
    DelegationChain: string list
    /// Signature produced by the immediate delegating peer over the
    /// assertion payload, validated on the receiving side via
    /// `IPeerAuthProvider.VerifyDelegation`.
    Signature: string
}

/// The identity propagated with a peer call. `Anonymous` is the default
/// for deployment-to-deployment calls that carry no end-user identity;
/// `Direct` carries a single-hop authenticated user; `Delegated` carries
/// a multi-hop user assertion through an intermediary.
type UserContext =
    | Anonymous
    | Direct of UserAssertion
    | Delegated of DelegatedAssertion

/// A peer the local deployment can call: its identity plus the origin
/// of its JSON-RPC host. `BaseUrl` is the scheme + host (+ optional
/// port) with no trailing slash; the substrate appends
/// `/peer/v1/{contractId}`.
type TargetPeer = { Peer: PeerIdentity; BaseUrl: string }

/// Declares whether a contract method resolves synchronously within the
/// HTTP request (`Immediate`, returning `Async<'T>`) or asynchronously
/// through the job substrate (`LongRunning`, returning
/// `Async<PeerJobHandle<'T>>`). Precision at the lower bound (GP 12 rule
/// 6): the substrate makes no sub-request-latency promise for
/// `LongRunning` resolution — it is poll- or callback-driven.
type Lifetime =
    | Immediate
    | LongRunning

/// Context carried on every peer call. Cascade fields (`Route`,
/// `RootRequestId`, `ParentRequestId`, `HopsRemaining`) are present from
/// day one so multi-hop federation (Phase 18c) can layer on without a
/// wire-format break. A 1:1 call sets `Route = [ caller ]`,
/// `ParentRequestId = None`, and a sensible default `HopsRemaining`.
type PeerCallContext = {
    /// Identity of the *calling* peer as asserted to the receiver.
    Peer: PeerIdentity
    /// End-user identity propagated with the call.
    User: UserContext
    /// Negotiated contract version this call is made under.
    ContractVersion: ContractVersion
    /// Ordered list of peer ids the call has traversed, originator
    /// first. The receiver appends itself before forwarding. A repeat
    /// entry signals a loop (`PeerLoopDetected`).
    Route: string list
    /// Stable id shared across the whole cascade — every hop logs audit
    /// events under the same `RootRequestId` so a federated call is
    /// reconstructable end to end.
    RootRequestId: string
    /// The id of the immediate parent request in the cascade, or `None`
    /// at the originating hop.
    ParentRequestId: string option
    /// Remaining hop budget. Decremented at each hop; a call arriving
    /// with `HopsRemaining <= 0` is rejected with `PeerHopLimitExceeded`
    /// before the handler runs.
    HopsRemaining: int
}

/// Failure modes of a peer call. Translated to / from JSON-RPC error
/// codes on the wire (see `JsonRpcEnvelope.fs`). Expressed as data
/// (GP 12 rule 3) — no callback or exception leaks framework semantics
/// across the boundary.
type PeerError =
    /// The caller's peer token failed validation, or the asserted
    /// identity is not authorised for the requested contract.
    | PeerUnauthorized of reason: string
    /// No contract is hosted under the requested `contractId`.
    | PeerContractNotFound of contractId: string
    /// The contract is hosted but exposes no method of this name.
    | PeerMethodNotFound of methodName: string
    /// The requested contract version is not in the receiver's supported
    /// set (a per-call guard complementing the handshake).
    | PeerVersionMismatch of requested: ContractVersion * supported: ContractVersion list
    /// The call's `Route` already contains a peer it is being forwarded
    /// to — a cascade loop.
    | PeerLoopDetected of route: string list
    /// The call arrived with no remaining hop budget.
    | PeerHopLimitExceeded
    /// Transport-level failure (connection, timeout, non-JSON body).
    | PeerTransport of message: string
    /// The receiving handler raised an error executing the call.
    | PeerHandler of message: string
    /// Request or response body could not be (de)serialised.
    | PeerDeserialization of message: string

/// Failure modes of the capability handshake. Surfaced at handshake
/// time rather than mid-call so a version incompatibility is a
/// connect-time error, not a runtime surprise.
type PeerHandshakeError =
    /// No version of `contractId` is supported by both sides.
    | NoMutualVersion of contractId: string * localVersions: ContractVersion list * remoteVersions: ContractVersion list
    /// The peer's handshake endpoint was unreachable.
    | HandshakeUnreachable of message: string
    /// The peer refused the handshake (auth, policy, unknown peer).
    | HandshakeRejected of reason: string

/// A single contract's supported versions, as exchanged during the
/// capability handshake.
type ContractCapability = {
    ContractId: string
    Versions: ContractVersion list
}

/// The set of contracts (and their supported versions) a deployment
/// exposes. Exchanged on first contact; the resolved version per
/// contract is the highest mutual.
type CapabilityList = ContractCapability list