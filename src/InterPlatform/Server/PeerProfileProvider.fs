// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Phase 18d — local capability profile provider ───────────────────
//
// Supplies this deployment's aggregated `PeerProfile` — the answer to a
// `GET /peer/v1/capabilities/profile` request and the local side of an
// outbound `NegotiateMethod`. Aggregation rule: walk the *live* contract
// table (`IPlatformPeer.Capabilities()`, so the profile can never claim a
// contract that is not actually registered), and for each registered
// contract use the author-declared `ContractProfile` when one exists,
// otherwise synthesise a versions-only profile with empty method lists
// (the contract is reachable but declares no per-method lifecycle, so
// per-method negotiation against it degrades to `MethodNotAdvertised`).

/// Provides the local deployment's capability profile. A thin interface so
/// the host endpoint and the handshake read the same aggregation without
/// re-implementing it, and so an alternate provider can compose the same
/// `routes` / handshake.
type IPeerProfileProvider =
    /// The aggregated profile for every contract this deployment hosts.
    abstract LocalProfile: unit -> Async<PeerProfile>

/// Default `IPeerProfileProvider`: aggregates the author-declared
/// `ContractProfile`s over the live capability table. Stateless between
/// calls (GP 12 rule 4) — every call re-reads `peer.Capabilities()`, so a
/// contract registered after construction is reflected immediately.
type DefaultPeerProfileProvider(peer: IPlatformPeer, declared: ContractProfile list) =
    interface IPeerProfileProvider with
        member _.LocalProfile() = async {
            let! caps = peer.Capabilities()

            return
                caps
                |> List.map (fun cap ->
                    match declared |> List.tryFind (fun p -> p.ContractId = cap.ContractId) with
                    | Some p -> p
                    | None -> {
                        ContractId = cap.ContractId
                        VersionProfiles = cap.Versions |> List.map (fun v -> { Version = v; Methods = [] })
                      })
        }