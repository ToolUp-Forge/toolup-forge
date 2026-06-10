// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.InterPlatform.PeerCapabilityNegotiation

open Microsoft.FSharp.Reflection

// ─── Phase 18d — sophisticated capability negotiation (logic) ────────
//
// Pure, total functions over two peers' capability profiles, plus the
// reflection helper authors use to build a `ContractProfile` from a
// contract record type. No transport, no DI, no IO — the negotiation
// outcome is a function of the two profiles alone, so it is exhaustively
// unit-testable. The handshake (`InMemoryPeerHandshake.NegotiateMethod`)
// fetches the remote profile and calls `negotiate`; the host endpoint
// (`/peer/v1/capabilities/profile`) and the provider supply the profiles.

/// The method's lifecycle status at a specific version of a contract
/// profile, or `None` when the contract has no profile for that version,
/// or the version has no method of that name.
let methodStatusAt (profile: ContractProfile) (version: ContractVersion) (methodName: string) : MethodStatus option =
    profile.VersionProfiles
    |> List.tryFind (fun vp -> vp.Version = version)
    |> Option.bind (fun vp -> vp.Methods |> List.tryFind (fun m -> m.Method = methodName))
    |> Option.map _.Status

/// Versions a profile advertises for a contract id (empty when the
/// contract is not in the profile).
let private versionsFor (contractId: string) (profile: PeerProfile) : ContractVersion list =
    profile
    |> List.tryFind (fun c -> c.ContractId = contractId)
    |> Option.map (fun c -> c.VersionProfiles |> List.map _.Version)
    |> Option.defaultValue []

/// Negotiate one method against a remote peer's profile: intersect the
/// two sides' advertised versions for the contract, take the highest
/// mutual version (the structural `Major`-dominates-`Minor` order, GP 12
/// rule 6), and resolve the method's status at that version *on the
/// remote* — the receiver is authoritative about its own method
/// lifecycle. A `Removed` status resolves successfully (the caller learns
/// the method is gone + why), distinct from `MethodNotAdvertised` (the
/// receiver never had it).
let negotiate
    (local: PeerProfile)
    (remote: PeerProfile)
    (contractId: string)
    (methodName: string)
    : Result<MethodResolution, MethodNegotiationError> =
    match remote |> List.tryFind (fun c -> c.ContractId = contractId) with
    | None -> Error(ContractNotAdvertised contractId)
    | Some remoteContract ->
        let localVersions = versionsFor contractId local
        let remoteVersions = remoteContract.VersionProfiles |> List.map _.Version
        let mutual = localVersions |> List.filter (fun v -> List.contains v remoteVersions)

        match mutual with
        | [] -> Error(NoMutualContractVersion(contractId, localVersions, remoteVersions))
        | _ ->
            let resolved = List.max mutual

            match methodStatusAt remoteContract resolved methodName with
            | None -> Error(MethodNotAdvertised(contractId, methodName))
            | Some status ->
                Ok {
                    ContractId = contractId
                    Method = methodName
                    Version = resolved
                    Status = status
                }

/// Degrade a bare `CapabilityList` (a foundation-only peer that does not
/// serve `/peer/v1/capabilities/profile`) into a `PeerProfile` with every
/// method `Active` — so a profile-aware caller can still negotiate against
/// a peer that predates 18d. Method names are unknown from a bare
/// capability list, so each advertised version carries an empty method
/// list; a per-method negotiate against it yields `MethodNotAdvertised`,
/// which a caller treats as "fall back to contract-version negotiation".
let fromCapabilityList (capabilities: CapabilityList) : PeerProfile =
    capabilities
    |> List.map (fun c -> {
        ContractId = c.ContractId
        VersionProfiles = c.Versions |> List.map (fun v -> { Version = v; Methods = [] })
    })

/// Build a `ContractProfile` from a contract record type by reflection:
/// every record field (a contract method) is `Active` at every supplied
/// version, then `overlay` overrides specific `(method, version)` pairs to
/// `Deprecated` / `Removed`. This is the author-facing convenience that
/// keeps the method list in sync with the contract type while letting the
/// author declare lifecycle intent explicitly (deprecation can never be
/// *inferred* from reflection).
let profileFor<'TApi>
    (contractId: string)
    (versions: ContractVersion list)
    (overlay: (string * ContractVersion * MethodStatus) list)
    : ContractProfile =
    let apiType = typeof<'TApi>

    if not (FSharpType.IsRecord apiType) then
        failwithf
            "PeerCapabilityNegotiation.profileFor requires a record contract type; %s is not a record"
            apiType.Name

    let methodNames =
        FSharpType.GetRecordFields apiType |> Array.map _.Name |> Array.toList

    let statusFor (methodName: string) (version: ContractVersion) : MethodStatus =
        overlay
        |> List.tryPick (fun (m, v, status) -> if m = methodName && v = version then Some status else None)
        |> Option.defaultValue Active

    {
        ContractId = contractId
        VersionProfiles =
            versions
            |> List.map (fun version -> {
                Version = version
                Methods =
                    methodNames
                    |> List.map (fun m -> {
                        Method = m
                        Status = statusFor m version
                    })
            })
    }