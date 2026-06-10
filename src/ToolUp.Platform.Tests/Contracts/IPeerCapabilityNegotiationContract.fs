module ToolUp.Platform.Tests.Contracts.IPeerCapabilityNegotiationContract

open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform

// ─── Phase 18d — sophisticated capability negotiation contract pack ──
//
// Exercises the pure negotiation logic (`PeerCapabilityNegotiation`) and
// the handshake wrapper (`InMemoryPeerHandshake.NegotiateMethod`):
//   • methodStatusAt lookups,
//   • per-method negotiation resolving Active / Deprecated / Removed at the
//     highest mutual version,
//   • the ContractNotAdvertised / MethodNotAdvertised / NoMutual error
//     paths,
//   • profileFor reflection auto-population + lifecycle overlay,
//   • fromCapabilityList degradation (a foundation-only peer),
//   • the handshake's RemoteProfileUnavailable wrapping of a fetch error.

// A record contract that evolves across versions. NOT private — the
// reflection in `profileFor` rejects a private record (same constraint as
// the foundation host/proxy).
type EvolvingContract = {
    Echo: string -> Async<string>
    GetReach: string -> Async<int>
    OldQuery: string -> Async<string>
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }
let private v2: ContractVersion = { Major = 2; Minor = 0 }
let private v3: ContractVersion = { Major = 3; Minor = 0 }

let private getReachSunset: DeprecationNotice = {
    DeprecatedSince = v2
    RemovedIn = Some v3
    Note = "use GetReachV2 in v3"
}

let private oldQueryRemoval: DeprecationNotice = {
    DeprecatedSince = v1
    RemovedIn = Some v2
    Note = "removed in v2 — use GetReach"
}

/// The receiver's profile: contract advertised at v1 + v2. At v2, GetReach
/// is Deprecated (sunset v3) and OldQuery is Removed; everything else is
/// Active. Built via the author-facing reflection helper + overlay.
let private remoteProfile: PeerProfile = [
    PeerCapabilityNegotiation.profileFor<EvolvingContract> "evolving" [ v1; v2 ] [
        ("GetReach", v2, Deprecated getReachSunset)
        ("OldQuery", v2, Removed oldQueryRemoval)
    ]
]

let private remoteContract = List.head remoteProfile

/// A caller that supports both versions.
let private callerBothVersions: PeerProfile = [
    PeerCapabilityNegotiation.profileFor<EvolvingContract> "evolving" [ v1; v2 ] []
]

/// A caller pinned to v1 only.
let private callerV1Only: PeerProfile = [ PeerCapabilityNegotiation.profileFor<EvolvingContract> "evolving" [ v1 ] [] ]

let tests =
    testList "IPeerCapabilityNegotiationContract" [

        // ─── profileFor reflection + overlay ──────────────────────

        testCase "profileFor auto-populates every method Active at every version"
        <| fun _ ->
            let v1Profile =
                remoteContract.VersionProfiles |> List.find (fun vp -> vp.Version = v1)

            Expect.equal v1Profile.Methods.Length 3 "all three contract methods appear in the v1 profile"
            Expect.isTrue (v1Profile.Methods |> List.forall (fun m -> m.Status = Active)) "every method is Active at v1"

        testCase "profileFor overlay overrides specific (method, version) pairs"
        <| fun _ ->
            Expect.equal
                (PeerCapabilityNegotiation.methodStatusAt remoteContract v2 "GetReach")
                (Some(Deprecated getReachSunset))
                "GetReach is Deprecated at v2 per the overlay"

            Expect.equal
                (PeerCapabilityNegotiation.methodStatusAt remoteContract v2 "OldQuery")
                (Some(Removed oldQueryRemoval))
                "OldQuery is Removed at v2 per the overlay"

            Expect.equal
                (PeerCapabilityNegotiation.methodStatusAt remoteContract v1 "GetReach")
                (Some Active)
                "GetReach is still Active at v1 — the overlay is version-specific"

        testCase "methodStatusAt returns None for an unknown version or method"
        <| fun _ ->
            Expect.isNone (PeerCapabilityNegotiation.methodStatusAt remoteContract v3 "Echo") "v3 is not advertised"
            Expect.isNone (PeerCapabilityNegotiation.methodStatusAt remoteContract v2 "Ghost") "Ghost is not a method"

        // ─── negotiate: status at the resolved version ────────────

        testCase "negotiate resolves an Active method at the highest mutual version"
        <| fun _ ->
            match PeerCapabilityNegotiation.negotiate callerBothVersions remoteProfile "evolving" "Echo" with
            | Ok res ->
                Expect.equal res.Version v2 "the highest mutual version is v2"
                Expect.equal res.Status Active "Echo is Active at v2"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "negotiate surfaces a Deprecated method with its sunset window"
        <| fun _ ->
            match PeerCapabilityNegotiation.negotiate callerBothVersions remoteProfile "evolving" "GetReach" with
            | Ok res -> Expect.equal res.Status (Deprecated getReachSunset) "GetReach is Deprecated at the resolved v2"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "negotiate surfaces a Removed method at connect time (not a runtime PeerMethodNotFound)"
        <| fun _ ->
            match PeerCapabilityNegotiation.negotiate callerBothVersions remoteProfile "evolving" "OldQuery" with
            | Ok res -> Expect.equal res.Status (Removed oldQueryRemoval) "OldQuery is Removed at the resolved v2"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "negotiate resolves per the negotiated version — a method Deprecated in v2 is Active when v1 is mutual"
        <| fun _ ->
            // Caller pins v1; mutual = [v1]; GetReach is Active at v1.
            match PeerCapabilityNegotiation.negotiate callerV1Only remoteProfile "evolving" "GetReach" with
            | Ok res ->
                Expect.equal res.Version v1 "the mutual version is v1"
                Expect.equal res.Status Active "GetReach is Active at v1 even though it is Deprecated at v2"
            | Error e -> failtestf "expected Ok, got %A" e

        // ─── negotiate: error paths ───────────────────────────────

        testCase "negotiate reports ContractNotAdvertised for an unknown contract"
        <| fun _ ->
            match PeerCapabilityNegotiation.negotiate callerBothVersions remoteProfile "ghost" "Echo" with
            | Error(ContractNotAdvertised c) -> Expect.equal c "ghost" "the unknown contract id is reported"
            | other -> failtestf "expected ContractNotAdvertised, got %A" other

        testCase "negotiate reports MethodNotAdvertised for an unknown method"
        <| fun _ ->
            match PeerCapabilityNegotiation.negotiate callerBothVersions remoteProfile "evolving" "Ghost" with
            | Error(MethodNotAdvertised(c, m)) ->
                Expect.equal c "evolving" "the contract id is reported"
                Expect.equal m "Ghost" "the unknown method name is reported"
            | other -> failtestf "expected MethodNotAdvertised, got %A" other

        testCase "negotiate reports NoMutualContractVersion when versions do not overlap"
        <| fun _ ->
            let callerV3Only: PeerProfile = [
                {
                    ContractId = "evolving"
                    VersionProfiles = [ { Version = v3; Methods = [] } ]
                }
            ]

            match PeerCapabilityNegotiation.negotiate callerV3Only remoteProfile "evolving" "Echo" with
            | Error(NoMutualContractVersion(c, local, remote)) ->
                Expect.equal c "evolving" "the contract id is reported"
                Expect.equal local [ v3 ] "the caller's versions are reported"
                Expect.equal remote [ v1; v2 ] "the remote's versions are reported"
            | other -> failtestf "expected NoMutualContractVersion, got %A" other

        // ─── fromCapabilityList degradation ───────────────────────

        testCase "fromCapabilityList degrades a foundation-only peer to all-Active, empty-method profiles"
        <| fun _ ->
            let caps: CapabilityList = [
                {
                    ContractId = "evolving"
                    Versions = [ v1; v2 ]
                }
            ]

            let degraded = PeerCapabilityNegotiation.fromCapabilityList caps

            // Versions are preserved so contract-version negotiation still
            // works; method lists are empty, so per-method negotiate yields
            // MethodNotAdvertised (the caller falls back to version-level).
            match PeerCapabilityNegotiation.negotiate callerBothVersions degraded "evolving" "Echo" with
            | Error(MethodNotAdvertised _) -> ()
            | other -> failtestf "expected MethodNotAdvertised against a degraded profile, got %A" other

        // ─── handshake wrapper ────────────────────────────────────

        testCaseAsync "NegotiateMethod resolves through the handshake when the remote profile is reachable"
        <| async {
            let peer = DefaultPlatformPeer() :> IPlatformPeer

            let handshake =
                InMemoryPeerHandshake(
                    peer,
                    (fun _ -> async { return Ok [] }),
                    (fun () -> async { return callerBothVersions }),
                    (fun _ -> async { return Ok remoteProfile })
                )
                :> IPeerHandshake

            let target = {
                Peer = {
                    PeerId = "seller"
                    DisplayName = "Seller"
                }
                BaseUrl = "loopback"
            }

            let! result = handshake.NegotiateMethod(target, "evolving", "GetReach")

            match result with
            | Ok res -> Expect.equal res.Status (Deprecated getReachSunset) "the handshake surfaces the deprecation"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        testCaseAsync "NegotiateMethod wraps a remote-profile fetch failure as RemoteProfileUnavailable"
        <| async {
            let peer = DefaultPlatformPeer() :> IPlatformPeer

            let handshake =
                InMemoryPeerHandshake(
                    peer,
                    (fun _ -> async { return Ok [] }),
                    (fun () -> async { return callerBothVersions }),
                    (fun _ -> async { return Error(HandshakeUnreachable "connection refused") })
                )
                :> IPeerHandshake

            let target = {
                Peer = {
                    PeerId = "seller"
                    DisplayName = "Seller"
                }
                BaseUrl = "loopback"
            }

            let! result = handshake.NegotiateMethod(target, "evolving", "Echo")

            match result with
            | Error(RemoteProfileUnavailable(HandshakeUnreachable msg)) ->
                Expect.stringContains msg "connection refused" "the underlying transport error is preserved"
            | other -> failtestf "expected RemoteProfileUnavailable, got %A" other
        }
    ]