// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Phase 18d — sophisticated capability negotiation (profile) ──────
//
// The Phase 18 handshake negotiates the single highest *contract* version
// both peers support and stops there. That is too coarse for a long-lived
// federation: a receiver evolving a contract needs to retire individual
// methods on a schedule, and a caller needs advance, machine-readable
// warning before a method disappears under it — not a runtime
// `PeerMethodNotFound` surprise.
//
// 18d adds a per-method *capability profile* layer beside the bare
// `CapabilityList` (which stays the wire contract for
// `GET /peer/v1/capabilities`, so the foundation and any non-F# caller
// are unaffected). The profile carries, per contract per version, each
// method's lifecycle status — `Active`, `Deprecated` (with a sunset
// window), or `Removed` (still advertised so callers learn *why*). The
// receiver is authoritative about its own contract's method lifecycle;
// negotiation resolves the highest mutual version, then reads the method's
// status at that version off the remote profile.
//
// Every type here satisfies the six portability rules (GP 12): identity by
// value, plain immutable records / DUs, no framework serialisation
// attributes. Server-to-server only — no Fable client surface.

/// A declared deprecation of a contract method: when it was deprecated,
/// the version it is (or will be) removed in, and a human note pointing
/// callers at the replacement. Carried inside `MethodStatus.Deprecated` /
/// `MethodStatus.Removed`.
type DeprecationNotice = {
    /// The contract version the method became deprecated in.
    DeprecatedSince: ContractVersion
    /// The version the method is scheduled to be (or was) removed in.
    /// `None` = deprecated with no announced removal version yet.
    RemovedIn: ContractVersion option
    /// Human-readable migration guidance (e.g. "use `GetReachV2` instead").
    Note: string
}

/// The lifecycle status of one contract method at one contract version.
/// `Removed` is still *advertised* (carrying its notice) so a caller that
/// pins the version learns the method is gone and why, rather than hitting
/// a bare `PeerMethodNotFound` at call time.
type MethodStatus =
    | Active
    | Deprecated of DeprecationNotice
    | Removed of DeprecationNotice

/// One method's status within a contract version profile.
type MethodProfile = { Method: string; Status: MethodStatus }

/// The method lifecycle a contract advertises *at a specific version*.
type ContractVersionProfile = {
    Version: ContractVersion
    Methods: MethodProfile list
}

/// A contract's full per-version method lifecycle profile — the richer
/// analogue of a single `ContractCapability` (`{ ContractId; Versions }`).
type ContractProfile = {
    ContractId: string
    VersionProfiles: ContractVersionProfile list
}

/// The set of contract profiles a deployment advertises — the richer
/// analogue of `CapabilityList`, served at
/// `GET /peer/v1/capabilities/profile`.
type PeerProfile = ContractProfile list

/// The resolved outcome of a successful per-method negotiation: the
/// highest mutual contract version, and the method's lifecycle status on
/// the receiver at that version.
type MethodResolution = {
    ContractId: string
    Method: string
    Version: ContractVersion
    Status: MethodStatus
}

/// Why a per-method negotiation did not resolve. Expressed as data
/// (GP 12 rule 3) — never an exception across the boundary. `NoMutual-
/// ContractVersion` carries both sides' version lists for diagnosis,
/// mirroring `PeerHandshakeError.NoMutualVersion`.
type MethodNegotiationError =
    /// The remote does not advertise the contract at all.
    | ContractNotAdvertised of contractId: string
    /// The contract is advertised, but exposes no method of this name at
    /// the resolved version.
    | MethodNotAdvertised of contractId: string * methodName: string
    /// The two sides share no contract version (the per-method analogue of
    /// `NoMutualVersion`).
    | NoMutualContractVersion of contractId: string * local: ContractVersion list * remote: ContractVersion list
    /// The remote profile could not be fetched (transport / auth / policy).
    /// Wraps the underlying handshake error so a caller can distinguish a
    /// reachability failure from a genuine capability mismatch.
    | RemoteProfileUnavailable of PeerHandshakeError