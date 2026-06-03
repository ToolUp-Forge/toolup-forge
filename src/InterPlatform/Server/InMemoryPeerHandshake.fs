// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Layer 4 — default capability handshake ──────────────────────────
//
// `InMemoryPeerHandshake` is the default `IPeerHandshake`. It answers an
// inbound handshake from the local `IPlatformPeer`'s capability table and
// negotiates an outbound handshake by intersecting the local versions
// with the remote peer's advertised versions for a contract.
//
// The remote-capability fetch is injected (`fetchRemote`) so the
// handshake stays transport-agnostic (GP 12 rule 2 — async boundary):
// in-process for tests (call the peer's `Capabilities` directly), HTTP
// for production (a `GET /peer/v1/capabilities` against the target's
// base URL). The handshake holds no negotiated-version cache (GP 12 rule
// 4) — a caller that wants to pin a version holds it itself.

/// Default `IPeerHandshake`. `LocalCapabilities` reads the local peer's
/// contract table; `Negotiate` intersects local and remote version sets
/// and returns the highest mutual `ContractVersion` (the structural
/// `Major`-dominates-`Minor` order, GP 12 rule 6).
type InMemoryPeerHandshake
    (local: IPlatformPeer, fetchRemote: TargetPeer -> Async<Result<CapabilityList, PeerHandshakeError>>) =

    let versionsFor (contractId: string) (capabilities: CapabilityList) =
        capabilities
        |> List.tryFind (fun c -> c.ContractId = contractId)
        |> Option.map _.Versions
        |> Option.defaultValue []

    interface IPeerHandshake with
        member _.LocalCapabilities() = local.Capabilities()

        member _.Negotiate(target: TargetPeer, contractId: string) = async {
            let! localCaps = local.Capabilities()
            let localVersions = versionsFor contractId localCaps

            let! remoteResult = fetchRemote target

            return
                match remoteResult with
                | Error e -> Error e
                | Ok remoteCaps ->
                    let remoteVersions = versionsFor contractId remoteCaps

                    let mutual = localVersions |> List.filter (fun v -> List.contains v remoteVersions)

                    match mutual with
                    | [] -> Error(NoMutualVersion(contractId, localVersions, remoteVersions))
                    | _ -> Ok(List.max mutual)
        }