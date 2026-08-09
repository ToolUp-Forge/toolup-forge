module ToolUp.Platform.Tests.InProcess.AggregatePeerSurfaceTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

// ─── Phase 595 — aggregate peer surface + gateway composition ────────
//
// Two halves, and the load-bearing assertion joins them: the derived
// aggregate is a real `PeerSurface`, and the *gateway composed from the
// same exposure* reports exactly that surface — the served half arriving
// through `PeerSurface.describe` of the gateway's real registrations, not
// re-asserted from the exposure list. So the equality is a genuine check
// that what the gateway mounts is what the group advertises.
//
// The derivation half is covered against the honest failure modes:
// exposure is an allow-list (unlisted stays internal), posture takes the
// floor of the exposing members (never better), vocabulary pins survive
// only on unanimity, and every unresolvable exposure is a named error.
// Each property carries a negative control — the same fixture with the
// mechanism's input removed, asserted to produce the *other* answer — so
// a check that stopped having teeth fails here rather than passing
// everywhere.

// ─── Member deployments ──────────────────────────────────────────────

/// Served by `alpha`, fronted by the group.
type OrdersContract = {
    PlaceOrder: string -> Async<string>
    ReconcileLedger: string -> Async<PeerJobHandle<int>>
}

/// Served by `alpha`, deliberately NOT exposed — the internal-by-default
/// control.
type LedgerContract = { PostEntry: string -> Async<string> }

/// Served by `beta`, fronted by the group.
type CatalogueContract = {
    ListItems: unit -> Async<string list>
}

/// Consumed by `beta` from outside the group.
type ExternalDirectoryContract = {
    Lookup: string -> Async<string option>
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }
let private v11: ContractVersion = { Major = 1; Minor = 1 }

let private ordersId = "example.orders"
let private ledgerId = "example.ledger"
let private catalogueId = "example.catalogue"
let private reportsId = "example.reports"
let private directoryId = "external.directory"

let private peer id name : PeerIdentity = { PeerId = id; DisplayName = name }

let private alphaPeer = peer "alpha" "Alpha site"
let private betaPeer = peer "beta" "Beta site"
let private gammaPeer = peer "gamma" "Gamma (non-platform participant)"
let private groupPeer = peer "consortium" "The consortium"

let private target (identity: PeerIdentity) : TargetPeer = {
    Peer = identity
    BaseUrl = $"https://{identity.PeerId}.example"
}

/// The shared vocabulary pack both platform members pin.
let private sharedPack: DataVocabularyPack = {
    Id = "consortium-core"
    Namespace = "consortium"
    Version = { Major = 1; Minor = 0 }
    Entries = [
        {
            TypeName = "consortium.Order"
            Description = "an order"
            Fields = []
        }
    ]
}

let private enabledConfig = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
        PinnedVocabularyPacks = [ sharedPack ]
}

let private ordersImpl (site: string) : OrdersContract = {
    PlaceOrder = fun order -> async { return $"{site}:placed:{order}" }
    ReconcileLedger =
        fun _ -> async {
            return {
                JobId = Guid.NewGuid()
                Poll = fun () -> async { return Completed 0 }
            }
        }
}

let private ledgerImpl: LedgerContract = {
    PostEntry = fun entry -> async { return $"alpha:posted:{entry}" }
}

let private catalogueImpl: CatalogueContract = {
    ListItems = fun () -> async { return [ "widget"; "sprocket" ] }
}

/// `alpha` — serves the fronted orders contract plus an internal ledger
/// contract the group deliberately does not expose.
let private alphaApp () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withLocalPeer alphaPeer
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<OrdersContract> ordersId [ v1; v11 ] fusion (ordersImpl "alpha"))
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<LedgerContract> ledgerId [ v1 ] fusion ledgerImpl)

/// `beta` — serves the fronted catalogue contract, consumes one contract
/// satisfied inside the group (orders) and one satisfied outside it.
let private betaApp () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withLocalPeer betaPeer
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<CatalogueContract> catalogueId [ v1 ] fusion catalogueImpl)
    |> PeerServerApp.withConsumedContract (
        PeerSurface.consumes<ExternalDirectoryContract> directoryId [ v1 ] "registry"
    )
    |> PeerServerApp.withConsumedContract (PeerSurface.consumes<OrdersContract> ordersId [ v1 ] "any-member")

let private alphaMember () : AggregateMember = {
    Target = target alphaPeer
    Surface = PeerSurface.describe (alphaApp ())
}

let private betaMember () : AggregateMember = {
    Target = target betaPeer
    Surface = PeerSurface.describe (betaApp ())
}

/// A hand-authored member surface — a participant the group federates
/// with that does not run this platform at all. Phase 590's shape is a
/// label, and a label can be asserted rather than derived; the group is
/// obliged to carry its *weaker* posture regardless.
let private gammaMember () : AggregateMember = {
    Target = target gammaPeer
    Surface = {
        Enabled = true
        LocalPeerId = Some gammaPeer.PeerId
        Serves = {
            Contracts = [
                {
                    ContractId = reportsId
                    Versions = [ v1 ]
                    Routines = []
                }
            ]
            Endpoints = PeerSurface.endpoints
        }
        Consumes = []
        TrustPosture =
            Some {
                AuthProfile = "mtls-client-cert"
                AudienceBound = false
                DelegationVerification = "none"
                ReplayStance = "freshness-window"
                TransportSecurity = "deployment-managed"
            }
        Budgets =
            Some {
                CascadeGuard = "hop-budget-decrement-with-route-loop-detection"
                LongRunningEnabled = false
            }
        PinnedVocabulary = []
        // Phase 642 — a hand-authored label that names a level rather
        // than omitting it. `Full` is the interesting choice here: the
        // group's floor must still come out at the narrowest level any
        // participant grants, so a member claiming the MOST cannot raise
        // the group's claim.
        DataVisibility = PeerDataVisibilityLevel.label PeerDataVisibilityLevel.Full
    }
}

let private expose (contractId: string) : ExposedContract = {
    ContractId = contractId
    Owner = None
}

let private exposeFrom (contractId: string) (ownerPeerId: string) : ExposedContract = {
    ContractId = contractId
    Owner = Some ownerPeerId
}

/// The reference group: two platform members, orders + catalogue fronted,
/// alpha's ledger contract left internal.
let private referenceMembers () = [ alphaMember (); betaMember () ]

let private referenceExposure: AggregateExposure = {
    Group = groupPeer
    Contracts = [ expose ordersId; expose catalogueId ]
}

let private deriveOk (members: AggregateMember list) (exposure: AggregateExposure) : PeerSurface =
    match AggregatePeerSurface.derive (members, exposure) with
    | Ok surface -> surface
    | Error errors -> failtestf "derivation was expected to succeed but reported %A" errors

let private servedIds (surface: PeerSurface) =
    surface.Serves.Contracts |> List.map _.ContractId

// ─── In-process transport for the gateway leg ────────────────────────

/// Replaces the wire with a direct call into each member's own
/// `IPlatformPeer`, recording every delegation so a routing assertion can
/// name the member that was actually reached. The gateway's routing is
/// what is under test; the HTTP transport is Phase 18's and already
/// covered.
let private inProcessClient (members: (string * IPlatformPeer) list) (log: ResizeArray<string * string * string>) =
    let table = Map.ofList members

    { new IPeerClient with
        member _.Invoke(target, contractId, methodName, payload, ?_cancellationToken) = async {
            lock log (fun () -> log.Add(target.Peer.PeerId, contractId, methodName))

            match Map.tryFind target.Peer.PeerId table with
            | Some peer -> return! peer.Handle(contractId, payload.Context, methodName, payload.Arguments)
            | None -> return Error(PeerTransport $"no member registered for {target.Peer.PeerId}")
        }

        member _.PollJob(_, _, _, ?_cancellationToken) = async {
            return Error(PeerTransport "the gateway shape does not front the long-running poll leg")
        }
    }

/// Register a composed app's contracts on a fresh in-process peer — the
/// same thing `PeerCompose.run` does at first `IPlatformPeer` resolution,
/// without standing up a host.
let private peerOf (app: PeerServerApp) : IPlatformPeer =
    let platformPeer = DefaultPlatformPeer() :> IPlatformPeer

    for builder in app.Contracts do
        platformPeer.RegisterContract (builder None).Registration

    platformPeer

/// An inbound call context as an external counterparty would present it
/// to the group.
let private inboundContext (hops: int) : PeerCallContext = {
    Peer = peer "external-caller" "External counterparty"
    User = Anonymous
    ContractVersion = v1
    Route = [ "external-caller" ]
    RootRequestId = "root-595"
    ParentRequestId = None
    HopsRemaining = hops
}

/// Compose the gateway over the reference group and hand back everything
/// a routing assertion needs.
let private gatewayFixture (members: AggregateMember list) (exposure: AggregateExposure) =
    let log = ResizeArray<string * string * string>()

    let client =
        inProcessClient [ alphaPeer.PeerId, peerOf (alphaApp ()); betaPeer.PeerId, peerOf (betaApp ()) ] log

    let composed =
        PeerServerApp.create ()
        |> PeerServerApp.withConfig {
            ServerConfig.defaults with
                PeerSubstrate = EnabledPeerSubstrate
        }
        |> PeerGateway.withAggregate client members exposure

    match composed with
    | Error errors -> failtestf "gateway composition was expected to succeed but reported %A" errors
    | Ok app -> app, peerOf app, log

let tests =
    testList "InProcess.AggregatePeerSurface (Phase 595)" [

        // ─── (a) derivation ──────────────────────────────────────────

        test "exposure is an allow-list — an unlisted served contract stays internal" {
            let surface = deriveOk (referenceMembers ()) referenceExposure

            Expect.equal
                (servedIds surface)
                [ catalogueId; ordersId ]
                "the group fronts exactly the exposed contracts, sorted"

            Expect.isFalse
                (servedIds surface |> List.contains ledgerId)
                "a served-but-unlisted contract must not reach the group's face (default-deny)"

            // Negative control: the same member set with the ledger LISTED
            // does front it — so the exclusion above is the allow-list
            // working, not the contract being invisible.
            let widened =
                deriveOk (referenceMembers ()) {
                    referenceExposure with
                        Contracts = expose ledgerId :: referenceExposure.Contracts
                }

            Expect.isTrue
                (servedIds widened |> List.contains ledgerId)
                "listing the contract must front it — otherwise the allow-list assertion proves nothing"
        }

        test "the group fronts the owning member's advertised versions" {
            let surface = deriveOk (referenceMembers ()) referenceExposure

            let orders =
                surface.Serves.Contracts |> List.find (fun c -> c.ContractId = ordersId)

            Expect.equal
                orders.Versions
                [ v1; v11 ]
                "an external caller's handshake must resolve to a version the owning member accepts"

            Expect.equal surface.Serves.Endpoints PeerSurface.endpoints "the group mounts the same v1 wire face"

            Expect.equal
                surface.LocalPeerId
                (Some groupPeer.PeerId)
                "the group presents its own identity, not any member's"
        }

        test "the group advertises no routines and no long-running dispatch" {
            // alpha's orders contract carries a long-running method; the
            // gateway fuses nothing onto its own job substrate, so the
            // group honestly advertises neither the routine nor the
            // capability.
            let surface = deriveOk (referenceMembers ()) referenceExposure

            Expect.isTrue
                (surface.Serves.Contracts |> List.forall (fun c -> List.isEmpty c.Routines))
                "a gateway-fronted contract advertises no routine"

            Expect.equal
                (surface.Budgets |> Option.map _.LongRunningEnabled)
                (Some false)
                "the group dispatches no long-running work of its own"
        }

        test "trust posture takes the floor of the exposing members — never better" {
            let unanimous = deriveOk (referenceMembers ()) referenceExposure

            match unanimous.TrustPosture with
            | None -> failtest "an aggregate must carry a trust posture"
            | Some posture ->
                Expect.isTrue posture.AudienceBound "both platform members bind audiences, so the group does"

                Expect.equal
                    posture.AuthProfile
                    "jwt-hs256-bearer"
                    "a facet every exposing member agrees on surfaces as that value"

            // Add a member whose posture is weaker on two facets: it binds
            // no audience and speaks a different auth profile.
            let mixed =
                deriveOk (referenceMembers () @ [ gammaMember () ]) {
                    referenceExposure with
                        Contracts = expose reportsId :: referenceExposure.Contracts
                }

            match mixed.TrustPosture with
            | None -> failtest "an aggregate must carry a trust posture"
            | Some posture ->
                Expect.isFalse posture.AudienceBound "one exposing member that binds no audience floors the whole group"

                Expect.stringStarts
                    posture.AuthProfile
                    AggregatePeerSurface.mixedPrefix
                    "a divergent facet must be reported as mixed, never as one member's claim"

                Expect.stringContains posture.AuthProfile "mtls-client-cert" "the divergence names both stances"

                Expect.equal
                    posture.ReplayStance
                    "freshness-window"
                    "a facet the divergent member still agrees on stays unanimous"
        }

        test "a member exposing nothing contributes no posture" {
            // gamma is in the member set but its contract is NOT exposed —
            // nothing of gamma's is reachable through the group face, so
            // its weaker posture must not floor the group. The negative
            // control for the test above.
            let surface = deriveOk (referenceMembers () @ [ gammaMember () ]) referenceExposure

            Expect.equal
                (surface.TrustPosture |> Option.map _.AudienceBound)
                (Some true)
                "an unexposed member's posture is internal and floors nothing"
        }

        test "vocabulary pins are carried only on unanimous agreement" {
            let unanimous = deriveOk (referenceMembers ()) referenceExposure

            Expect.equal
                (unanimous.PinnedVocabulary |> List.map _.PackId)
                [ sharedPack.Id ]
                "a pack both exposing members pin identically is carried"

            Expect.equal
                (unanimous.PinnedVocabulary |> List.exactlyOne).Hash
                (DataVocabulary.hash sharedPack)
                "the carried pin keeps the pack's canonical hash"

            // Same fixture, beta repinned to a later version: the group can
            // no longer claim a shared meaning, so the pack is omitted
            // entirely — not averaged, not majority-voted.
            let repinnedBeta = {
                betaMember () with
                    Surface =
                        PeerSurface.describe (
                            betaApp ()
                            |> PeerServerApp.withConfig {
                                enabledConfig with
                                    PinnedVocabularyPacks = [
                                        {
                                            sharedPack with
                                                Version = { Major = 2; Minor = 0 }
                                        }
                                    ]
                            }
                        )
            }

            let divergent = deriveOk [ alphaMember (); repinnedBeta ] referenceExposure

            Expect.isEmpty
                divergent.PinnedVocabulary
                "a pack the exposing members pin at different versions must be omitted"

            // And a member that pins nothing at all is equally a
            // disagreement — silence is not assent.
            let unpinnedBeta = {
                betaMember () with
                    Surface =
                        PeerSurface.describe (
                            betaApp ()
                            |> PeerServerApp.withConfig {
                                enabledConfig with
                                    PinnedVocabularyPacks = []
                            }
                        )
            }

            Expect.isEmpty
                (deriveOk [ alphaMember (); unpinnedBeta ] referenceExposure).PinnedVocabulary
                "a member that pins nothing breaks unanimity"
        }

        test "consumption satisfied inside the group is internal; the rest surfaces" {
            let surface = deriveOk (referenceMembers ()) referenceExposure

            Expect.equal
                (surface.Consumes |> List.map _.ContractId)
                [ directoryId ]
                "beta's orders consumption is served by alpha, so it is internal; the directory is not"
        }

        test "an exposed contract no member serves is a derivation error" {
            let result =
                AggregatePeerSurface.derive (
                    referenceMembers (),
                    {
                        referenceExposure with
                            Contracts = expose "example.absent" :: referenceExposure.Contracts
                    }
                )

            match result with
            | Ok _ -> failtest "the group must not advertise a face nothing stands behind"
            | Error errors ->
                Expect.equal errors [ ExposureUnserved "example.absent" ] "the error names the unserved contract"
        }

        test "an ambiguous owner must be declared, and declaring it resolves" {
            // Both members serve the orders contract: the group must say
            // which one owns the external face.
            let betaAlsoServesOrders = {
                betaMember () with
                    Surface =
                        PeerSurface.describe (
                            betaApp ()
                            |> PeerServerApp.withContract (fun fusion ->
                                JsonRpcPeerHost.contract<OrdersContract> ordersId [ v1 ] fusion (ordersImpl "beta"))
                        )
            }

            let members = [ alphaMember (); betaAlsoServesOrders ]

            match AggregatePeerSurface.derive (members, referenceExposure) with
            | Ok _ -> failtest "two serving members with no declared owner is ambiguous"
            | Error errors ->
                Expect.equal
                    errors
                    [ ExposureOwnerAmbiguous(ordersId, [ alphaPeer.PeerId; betaPeer.PeerId ]) ]
                    "the error names both candidates"

            // Negative control: naming the owner resolves it, and the
            // group fronts THAT member's versions.
            let declared =
                deriveOk members {
                    referenceExposure with
                        Contracts = [ exposeFrom ordersId betaPeer.PeerId; expose catalogueId ]
                }

            Expect.equal
                (declared.Serves.Contracts |> List.find (fun c -> c.ContractId = ordersId)).Versions
                [ v1 ]
                "the declared owner's version set is what the group fronts"
        }

        test "a declared owner that is unknown or does not serve is named" {
            let unknown =
                AggregatePeerSurface.derive (
                    referenceMembers (),
                    {
                        referenceExposure with
                            Contracts = [ exposeFrom ordersId "delta"; expose catalogueId ]
                    }
                )

            Expect.equal
                unknown
                (Error [ ExposureOwnerUnknown(ordersId, "delta") ])
                "an owner outside the member set is named as unknown"

            let doesNotServe =
                AggregatePeerSurface.derive (
                    referenceMembers (),
                    {
                        referenceExposure with
                            Contracts = [ exposeFrom ordersId betaPeer.PeerId; expose catalogueId ]
                    }
                )

            Expect.equal
                doesNotServe
                (Error [ ExposureOwnerDoesNotServe(ordersId, betaPeer.PeerId) ])
                "a member that does not serve the contract cannot own its external face"
        }

        test "duplicate members and duplicate exposures are named" {
            let duplicated =
                AggregatePeerSurface.derive (
                    [ alphaMember (); alphaMember (); betaMember () ],
                    {
                        referenceExposure with
                            Contracts = expose ordersId :: referenceExposure.Contracts
                    }
                )

            match duplicated with
            | Ok _ -> failtest "a duplicated member / exposure must not resolve"
            | Error errors ->
                Expect.containsAll
                    errors
                    [ MemberDuplicated alphaPeer.PeerId; ExposureDuplicated ordersId ]
                    "every problem is reported, not just the first"
        }

        // ─── (b) export shape ────────────────────────────────────────

        test "the export is a single instance's export — indistinguishable on the wire" {
            let aggregate = deriveOk (referenceMembers ()) referenceExposure

            // A ONE-deployment composition whose face happens to match the
            // group's: same identity, same fronted contracts + versions,
            // same external consumption, same pin, no job substrate. Its
            // export must be byte-identical to the group's — which is the
            // whole acceptance criterion: an external consumer cannot tell
            // a gateway-fronted group from one deployment.
            let singleInstance =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withLocalPeer groupPeer
                |> PeerServerApp.withContract (fun fusion ->
                    JsonRpcPeerHost.contract<OrdersContract> ordersId [ v1; v11 ] fusion (ordersImpl "single"))
                |> PeerServerApp.withContract (fun fusion ->
                    JsonRpcPeerHost.contract<CatalogueContract> catalogueId [ v1 ] fusion catalogueImpl)
                |> PeerServerApp.withConsumedContract (
                    PeerSurface.consumes<ExternalDirectoryContract> directoryId [ v1 ] "registry"
                )
                |> PeerSurface.describe

            Expect.equal
                (PeerSurface.exportJson aggregate)
                (PeerSurface.exportJson singleInstance)
                "the aggregate export must be indistinguishable from an equivalent single instance's"

            let roundTrip =
                JsonRpc.deserialize<PeerSurfaceExport> (PeerSurface.exportJson aggregate)

            Expect.equal roundTrip.Surface aggregate "the aggregate rides the ordinary export envelope"

            Expect.equal
                roundTrip.FormatVersion
                PeerSurface.formatVersion
                "the aggregate is stamped at the same format version"

            // Negative control: a group that fronts a different selection
            // is not that single instance.
            let narrower =
                deriveOk (referenceMembers ()) {
                    referenceExposure with
                        Contracts = [ expose catalogueId ]
                }

            Expect.notEqual
                (PeerSurface.export narrower).SurfaceHash
                (PeerSurface.export aggregate).SurfaceHash
                "a different exposure selection must be a different pinned face"
        }

        // ─── (c) gateway composition ─────────────────────────────────

        test "the gateway's live surface equals the derived aggregate" {
            let app, _, _ = gatewayFixture (referenceMembers ()) referenceExposure
            let aggregate = deriveOk (referenceMembers ()) referenceExposure

            match PeerGateway.surface (referenceMembers ()) referenceExposure app with
            | Error errors -> failtestf "the gateway surface was expected to resolve but reported %A" errors
            | Ok live ->
                Expect.equal live aggregate "the gateway must advertise exactly the aggregate it was composed from"

                Expect.equal
                    (PeerSurface.export live).SurfaceHash
                    (PeerSurface.export aggregate).SurfaceHash
                    "the gateway's live surface hash equals the derived aggregate's"

            // Negative control: a registration the exposure does not
            // account for diverges the two — which is what the equality
            // above exists to catch.
            let drifted =
                app
                |> PeerServerApp.withContract (fun fusion ->
                    JsonRpcPeerHost.contract<LedgerContract> ledgerId [ v1 ] fusion ledgerImpl)

            match PeerGateway.surface (referenceMembers ()) referenceExposure drifted with
            | Error errors -> failtestf "the gateway surface was expected to resolve but reported %A" errors
            | Ok live ->
                Expect.notEqual
                    live
                    aggregate
                    "a gateway serving more than the group declares must not report the group's face"
        }

        testCaseAsync "an external call through the gateway reaches the owning member"
        <| async {
            let _, gateway, log = gatewayFixture (referenceMembers ()) referenceExposure

            let! placed = gateway.Handle(ordersId, inboundContext 4, "PlaceOrder", JsonRpc.serialize [ "widget" ])

            Expect.equal
                placed
                (Ok(JsonRpc.serialize "alpha:placed:widget"))
                "the call must be answered by alpha, the owning member"

            // A zero-argument contract method still carries one positional
            // slot on the wire (the `unit`), marshalled as `null`.
            let! listed = gateway.Handle(catalogueId, inboundContext 4, "ListItems", "[null]")

            Expect.equal
                listed
                (Ok(JsonRpc.serialize [ "widget"; "sprocket" ]))
                "a second contract must route to ITS owner, not the first's"

            Expect.equal
                (List.ofSeq log)
                [
                    alphaPeer.PeerId, ordersId, "PlaceOrder"
                    betaPeer.PeerId, catalogueId, "ListItems"
                ]
                "each contract was delegated to exactly its declared owner"
        }

        testCaseAsync "an unexposed contract is not reachable through the gateway"
        <| async {
            let _, gateway, log = gatewayFixture (referenceMembers ()) referenceExposure

            let! posted = gateway.Handle(ledgerId, inboundContext 4, "PostEntry", JsonRpc.serialize [ "e1" ])

            Expect.equal
                posted
                (Error(PeerContractNotFound ledgerId))
                "alpha serves the ledger contract, but the group does not front it"

            Expect.isEmpty log "an unexposed contract must not reach any member"
        }

        testCaseAsync "the gateway hop is a first-class cascade hop and costs a hop"
        <| async {
            let place hops =
                let _, gateway, log = gatewayFixture (referenceMembers ()) referenceExposure

                async {
                    let! result =
                        gateway.Handle(ordersId, inboundContext hops, "PlaceOrder", JsonRpc.serialize [ "widget" ])

                    return result, List.ofSeq log
                }

            // Two hops: one to reach the group, one to reach the member.
            let! spare, spareLog = place 2
            Expect.isOk spare "a call with budget for both hops reaches the member"
            Expect.isNonEmpty spareLog "and is actually delegated"

            // One hop: the group spends it on itself, so the member refuses
            // the onward call. The delegation still happened — the group is
            // a real hop, not a transparent proxy.
            let! spent, spentLog = place 1
            Expect.equal spent (Error PeerHopLimitExceeded) "the gateway hop costs a hop from the caller's budget"
            Expect.isNonEmpty spentLog "the refusal came from the member, which was reached"

            // No budget at all: the gateway's own receiver guard refuses
            // before any delegation.
            let! none, noneLog = place 0
            Expect.equal none (Error PeerHopLimitExceeded) "a call with no budget cannot enter the group"
            Expect.isEmpty noneLog "and is refused before the wire, not after"
        }

        testCaseAsync "the gateway refuses a loop back to a peer already on the route"
        <| async {
            let _, gateway, log = gatewayFixture (referenceMembers ()) referenceExposure

            let looping = {
                inboundContext 4 with
                    Route = [ "external-caller"; alphaPeer.PeerId ]
            }

            let! result = gateway.Handle(ordersId, looping, "PlaceOrder", JsonRpc.serialize [ "widget" ])

            match result with
            | Error(PeerLoopDetected route) ->
                Expect.contains route alphaPeer.PeerId "the reported route names the peer it would double back to"
            | other -> failtestf "a delegation back onto the route must be refused, got %A" other

            Expect.isEmpty log "a loop must be refused before the wire"
        }

        test "a gateway composition that cannot resolve its exposure does not compose" {
            let client = inProcessClient [] (ResizeArray())

            let result =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig {
                    ServerConfig.defaults with
                        PeerSubstrate = EnabledPeerSubstrate
                }
                |> PeerGateway.withAggregate client (referenceMembers ()) {
                    referenceExposure with
                        Contracts = [ expose "example.absent" ]
                }

            // `PeerServerApp` holds closures, so the Result is matched
            // rather than compared.
            match result with
            | Ok _ -> failtest "a gateway that cannot say what it fronts must not compose"
            | Error errors ->
                Expect.equal
                    errors
                    [ ExposureUnserved "example.absent" ]
                    "the composition failure carries the derivation error verbatim"
        }

        test "the data-visibility authority floors to the narrowest exposing member's, with no mixed marker" {
            // Phase 642. The contrast with the posture facets above is the
            // point: those report a divergence as `mixed:` because their
            // values are unordered and no floor is computable. Authority
            // levels ARE ordered, so a divergence has a minimum, and
            // publishing that minimum gives a counterparty something it can
            // act on instead of a marker that satisfies nothing.
            //
            // gamma's hand-authored label claims `Full` — the MOST any
            // member claims — and the group must still come out at the
            // narrowest, because a call routed through the group lands on
            // some member and the caller cannot choose which.
            let mixed =
                deriveOk (referenceMembers () @ [ gammaMember () ]) {
                    referenceExposure with
                        Contracts = expose reportsId :: referenceExposure.Contracts
                }

            Expect.equal
                mixed.DataVisibility
                (PeerDataVisibilityLevel.label PeerDataVisibilityLevel.AggregatesOnly)
                "a member claiming the most cannot raise the group's claim"

            Expect.isFalse
                (mixed.DataVisibility.StartsWith AggregatePeerSurface.mixedPrefix)
                "an ordered facet has a floor, so it is never reported as a disagreement"

            // The control: gamma unexposed contributes nothing here
            // either, so the group is unchanged rather than accidentally
            // floored by a member nothing reaches.
            let unexposed =
                deriveOk (referenceMembers () @ [ gammaMember () ]) referenceExposure

            Expect.equal
                unexposed.DataVisibility
                (PeerDataVisibilityLevel.label PeerDataVisibilityLevel.AggregatesOnly)
                "an unexposed member's grant is internal and floors nothing"
        }

        test "a non-grouped deployment is unchanged (GP 11 / GP 13)" {
            // Nothing in this file runs unless a deployment composes a
            // gateway: an ordinary peer composition describes exactly as it
            // did before Phase 595.
            let ordinary = alphaApp ()

            Expect.equal
                (PeerSurface.describe ordinary)
                (PeerSurface.describe (alphaApp ()))
                "an ungrouped composition's face is untouched by the aggregate machinery"

            Expect.equal
                (servedIds (PeerSurface.describe ordinary))
                [ ledgerId; ordersId ]
                "and still reports its own registrations, group or no group"
        }
    ]