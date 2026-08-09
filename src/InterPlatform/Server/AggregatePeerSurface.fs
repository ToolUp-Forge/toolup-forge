// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform
open ToolUp.InterPlatform.PeerCompose

// ─── Phase 595 — aggregate peer surface + gateway composition ────────
//
// A group of deployments often needs to face the outside world **as one
// peer**: a multi-site organisation joining an external exchange, a
// consortium exposing a single front. Two additive pieces over the Phase
// 590 `PeerSurface`:
//
// (a) **`AggregatePeerSurface.derive`** — compose one collective
//     `PeerSurface` from a set of member surfaces plus an explicit
//     **exposure selection**. The selection is an allow-list of served
//     contract ids: what is not listed stays internal (default-deny), so
//     a group never leaks a member's contract by accident. Trust-posture
//     facets take the **floor** — the collective posture is never
//     stronger than the weakest exposing member's, because a call routed
//     through the group lands on that member. Pinned data-vocabulary
//     packs (Phase 594) are carried only where every exposing member
//     agrees exactly; a divergent pack is **omitted, never averaged** —
//     a group cannot honestly assert a shared meaning its members do not
//     share.
//
// (b) **`PeerGateway`** — a deployment shape that serves the aggregate's
//     exposed contracts by delegating to the owning member over the
//     existing typed peer client, and contributes the aggregate as its
//     own `PeerSurface`. An external counterparty pins the *group's*
//     face exactly like any single instance's; the Phase 591 preflight
//     applies unchanged.
//
// **The export is the same shape as a single instance's.** `derive`
// returns a real `PeerSurface`, so `PeerSurface.export` / `exportJson`
// stamp it through the very same code path — an external consumer cannot
// (and need not) distinguish a gateway-fronted group from one
// deployment.
//
// **Generic SDK vocabulary — no group governance.** "Aggregate surface",
// "member", "gateway". Who may speak for a group, how membership is
// admitted, and what a member owes the group are consumer concerns,
// expressed by what they sign; the substrate only composes labels and
// routes calls (GP 1).
//
// **Zero cost when unused (GP 11 / GP 13).** Everything here is pure and
// on demand: nothing is registered, allocated, or hosted unless a
// deployment calls `PeerGateway.withAggregate`. A non-grouped deployment
// is byte-for-byte unchanged.
//
// **Long-running methods (Phase 630).** Phase 595 fronted the *invoke*
// leg only: a member's long-running result parks in that member's own
// job-result store, which the group's `/peer/v1/{contractId}/jobs/{jobId}`
// route could not read, so the aggregate advertised
// `LongRunningEnabled = false` however capable its members were. The
// gateway now **translates the handle**: it mints a group job id of its
// own, records what it stands for in an `IPeerGroupJobMap`, and the
// host's poll route resolves a group handle by forwarding to the owning
// member (`PeerGroupJobFronting`). Three consequences visible here:
//
//   * `LongRunningEnabled` is now a **floor** across the exposing
//     members, like every other posture facet — the group is
//     long-running-capable only if every member behind its face is,
//     because a call routed through the group lands on one of them.
//   * A gateway fronts the poll leg only when a group job map was
//     composed (`PeerServerApp.withGroupJobMap`). Without one it is
//     byte-for-byte the Phase 595 shape, and `PeerGateway.surface`
//     reports `LongRunningEnabled = false` regardless of the members'
//     floor — a gateway must not advertise a leg it cannot serve
//     (GP 11 / GP 13).
//   * The group still advertises **no routines**. A routine name on a
//     `PeerSurface` is a handler fused onto *that instance's* job
//     substrate, and the gateway fuses none — it brokers the member's.
//     The routine set is carried on `AggregateRoute` instead, where the
//     dispatch closure needs it to tell a long-running method from an
//     immediate one, and never reaches the wire face.

/// One member of an aggregate group: how the gateway reaches it, plus
/// its own cross-instance face. The surface is whatever the group has of
/// that member — `PeerSurface.describe` of a co-owned composition, or a
/// pinned export received from a counterparty. Derivation reads the
/// surface only, so a group can be composed from labels alone.
type AggregateMember = {
    /// Identity + base URL the gateway delegates to.
    Target: TargetPeer
    /// The member's own `PeerSurface`.
    Surface: PeerSurface
}

/// One contract the group fronts externally, and the member it is
/// fronted from. Anything not named here stays internal — the exposure
/// selection is an allow-list, never a deny-list.
type ExposedContract = {
    /// The served contract id, as it appears on the member's surface (and
    /// as external callers will address it on the gateway).
    ContractId: string
    /// The peer id of the member the gateway routes this contract to.
    /// `None` resolves to the sole member serving it; ambiguous when
    /// more than one does — the group must then say which one owns the
    /// external face.
    Owner: string option
}

/// The group's external face declaration: the identity it presents as
/// one peer, plus the contracts it fronts.
type AggregateExposure = {
    /// The group's own peer identity — what an external counterparty
    /// addresses, authenticates, and pins. Distinct from every member's
    /// identity: the group *is* a peer.
    Group: PeerIdentity
    /// The exposure allow-list. Empty ⇒ the group serves nothing
    /// externally (a legitimate, if inert, shape).
    Contracts: ExposedContract list
}

/// Why an exposure selection cannot be resolved against the member set.
/// Expressed as data (GP 12 rule 3): every case names the contract (and
/// where relevant the member) so a composition failure is diagnosable
/// without reading the derivation.
type AggregateDerivationError =
    /// The exposure names a contract no member serves — the group would
    /// advertise a face nothing stands behind.
    | ExposureUnserved of contractId: string
    /// More than one member serves the contract and the exposure does not
    /// say which owns the external face.
    | ExposureOwnerAmbiguous of contractId: string * candidates: string list
    /// The declared owner is not a member of the group.
    | ExposureOwnerUnknown of contractId: string * ownerPeerId: string
    /// The declared owner is a member but does not serve the contract.
    | ExposureOwnerDoesNotServe of contractId: string * ownerPeerId: string
    /// The same contract id appears twice in the exposure selection.
    | ExposureDuplicated of contractId: string
    /// The same peer id appears twice in the member set.
    | MemberDuplicated of peerId: string

/// One resolved routing entry: the contract the group fronts, the wire
/// versions it fronts it at (the owner's own advertised set, so an
/// external caller's handshake resolves to a version the member accepts),
/// and the member the gateway delegates to. Routing declared as data —
/// the gateway holds no lookup logic of its own.
type AggregateRoute = {
    ContractId: string
    Versions: ContractVersion list
    Owner: TargetPeer
    /// Phase 630 — the long-running routine handler names the owning
    /// member advertises for this contract, taken verbatim from its own
    /// `ServedContract.Routines`
    /// (`_platform.peer.{contractId}.{methodName}`).
    ///
    /// This is what lets the gateway's method-agnostic dispatch tell a
    /// long-running call from an immediate one **without** a gateway-side
    /// list of method names to keep in step: a member that adds a
    /// long-running method republishes its surface and the group fronts it
    /// with no gateway edit. Routing data only — it is deliberately not
    /// projected onto the group's `PeerSurface`, where a routine means a
    /// handler fused onto *this* instance's job substrate.
    Routines: string list
}

/// Derives one collective `PeerSurface` from a set of member surfaces
/// plus an exposure selection. Pure; nothing here touches the wire.
[<RequireQualifiedAccess>]
module AggregatePeerSurface =

    /// Prefix marking a trust-posture facet the exposing members disagree
    /// on. A counterparty reading `mixed:a|b` learns it may rely on
    /// neither stance — strictly weaker than any single member's claim,
    /// which is the whole point of a floor.
    [<Literal>]
    let mixedPrefix = "mixed:"

    /// The floor of one string-valued posture facet: the unanimous value
    /// where the exposing members agree, else an explicit `mixed:` marker
    /// enumerating the divergence (sorted, so the aggregate's hash is
    /// independent of member order).
    let private floorFacet (values: string list) : string =
        match values |> List.distinct |> List.sort with
        | [] -> ""
        | [ single ] -> single
        | many -> mixedPrefix + String.concat "|" many

    /// The gateway's own structural surface: what `PeerSurface.describe`
    /// reports for a bare enabled composition carrying the group identity
    /// and nothing else. **Derived, never re-asserted** — the posture
    /// constants and the cascade-guard description live in one place
    /// (`PeerSurface.describe`), so a change there flows here with no
    /// edit. Runs no contract builder: the composition has none.
    let private edgeSurface (group: PeerIdentity) : PeerSurface =
        PeerServerApp.create ()
        |> PeerServerApp.withConfig {
            ServerConfig.defaults with
                PeerSubstrate = EnabledPeerSubstrate
        }
        |> PeerServerApp.withLocalPeer group
        |> PeerSurface.describe

    /// The posture floor over the gateway edge and every exposing member:
    /// boolean facets are conjunctions (a group binds audiences only if
    /// every member does), string facets fall back to `mixed:` on
    /// divergence. The edge participates because the group's face is the
    /// weaker of what the gateway enforces and what the members behind it
    /// do.
    let private floorPosture (postures: PeerTrustPosture list) : PeerTrustPosture = {
        AuthProfile = floorFacet (postures |> List.map _.AuthProfile)
        AudienceBound = postures |> List.forall _.AudienceBound
        DelegationVerification = floorFacet (postures |> List.map _.DelegationVerification)
        ReplayStance = floorFacet (postures |> List.map _.ReplayStance)
        TransportSecurity = floorFacet (postures |> List.map _.TransportSecurity)
    }

    /// Phase 630 — the long-running floor across the exposing members: the
    /// group dispatches long-running work only if **every** member behind
    /// its face can, because a call routed through the group lands on one
    /// of them and the caller cannot choose which.
    ///
    /// A conjunction, exactly like `AudienceBound` — the same floor
    /// discipline every other posture facet takes, and for the same
    /// reason: an aggregate must never claim a capability stronger than
    /// its weakest exposing member's.
    ///
    /// The **gateway edge itself is deliberately not a term.** A gateway
    /// needs no job substrate of its own to front this leg: it schedules
    /// nothing, executes nothing, and holds only a handle translation. If
    /// the edge participated, a group would report `false` whenever the
    /// gateway had no scheduler — which is every gateway — and the floor
    /// would be a constant wearing a floor's clothes. What the *gateway*
    /// can actually serve is folded in by `PeerGateway.surface`, which is
    /// the only thing that knows whether a group job map was composed.
    ///
    /// A group exposing nothing floors to `false`: it fronts no contract,
    /// so it dispatches no long-running work either.
    let private longRunningFloor (exposing: PeerSurface list) : bool =
        match exposing with
        | [] -> false
        | _ ->
            exposing
            |> List.forall (fun surface ->
                surface.Budgets |> Option.map _.LongRunningEnabled |> Option.defaultValue false)

    /// The vocabulary pins the group can honestly carry: a pack surfaces
    /// only when **every** exposing member pins it at the identical
    /// version AND hash. Any divergence — a differing version, an
    /// in-place mutation of the same version, or a member that pins it at
    /// all where another does not — omits the pack entirely. Never
    /// averaged, never majority-voted: a federated data type either means
    /// the same thing across every exposing member or the group makes no
    /// claim about it.
    let private unanimousPins (exposing: PeerSurface list) : VocabularyPackPin list =
        match exposing with
        | [] -> []
        | _ ->
            let pinsOf (surface: PeerSurface) =
                surface.PinnedVocabulary |> List.map (fun p -> p.PackId, p) |> Map.ofList

            let perMember = exposing |> List.map pinsOf

            exposing
            |> List.collect (fun s -> s.PinnedVocabulary |> List.map _.PackId)
            |> List.distinct
            |> List.choose (fun packId ->
                match perMember |> List.map (Map.tryFind packId) |> List.distinct with
                | [ Some pin ] -> Some pin
                | _ -> None)
            |> List.sortBy (fun p -> p.PackId, p.Version.Major, p.Version.Minor)

    /// What the group consumes from *outside* itself: the exposing
    /// members' consumed declarations, minus every contract some member
    /// of the group serves. A consumption satisfied inside the group is
    /// internal traffic and no part of the group's external face — the
    /// same default-deny reading that governs the serving half.
    let private externalConsumes
        (members: AggregateMember list)
        (exposing: AggregateMember list)
        : ConsumedContract list =
        let servedInGroup =
            members
            |> List.collect (fun m -> m.Surface.Serves.Contracts |> List.map _.ContractId)
            |> Set.ofList

        exposing
        |> List.collect (fun m -> m.Surface.Consumes)
        |> List.filter (fun c -> not (Set.contains c.ContractId servedInGroup))
        |> List.distinct
        |> List.sortBy _.ContractId

    /// Resolve the exposure selection against the member set into the
    /// gateway's routing table. Every problem is reported, not just the
    /// first: a group author fixing one exposure typo should not have to
    /// re-run to find the next.
    let routes
        (members: AggregateMember list, exposure: AggregateExposure)
        : Result<AggregateRoute list, AggregateDerivationError list> =

        let duplicateMembers =
            members
            |> List.countBy (fun m -> m.Target.Peer.PeerId)
            |> List.filter (snd >> (<) 1)
            |> List.map (fst >> MemberDuplicated)

        let duplicateExposures =
            exposure.Contracts
            |> List.countBy _.ContractId
            |> List.filter (snd >> (<) 1)
            |> List.map (fst >> ExposureDuplicated)

        /// The members whose surface serves `contractId`, and the served
        /// entry itself (the versions the group will front it at).
        let servingMembers (contractId: string) =
            members
            |> List.choose (fun m ->
                m.Surface.Serves.Contracts
                |> List.tryFind (fun c -> c.ContractId = contractId)
                |> Option.map (fun c -> m, c))

        let resolved =
            exposure.Contracts
            |> List.distinctBy _.ContractId
            |> List.map (fun exposed ->
                match servingMembers exposed.ContractId, exposed.Owner with
                | [], _ -> Error [ ExposureUnserved exposed.ContractId ]
                | [ (owner, served) ], None ->
                    Ok {
                        ContractId = exposed.ContractId
                        Versions = served.Versions |> List.sort
                        Owner = owner.Target
                        Routines = served.Routines |> List.sort
                    }
                | candidates, None ->
                    Error [
                        ExposureOwnerAmbiguous(
                            exposed.ContractId,
                            candidates |> List.map (fun (m, _) -> m.Target.Peer.PeerId) |> List.sort
                        )
                    ]
                | candidates, Some ownerId ->
                    match candidates |> List.tryFind (fun (m, _) -> m.Target.Peer.PeerId = ownerId) with
                    | Some(owner, served) ->
                        Ok {
                            ContractId = exposed.ContractId
                            Versions = served.Versions |> List.sort
                            Owner = owner.Target
                            Routines = served.Routines |> List.sort
                        }
                    | None when members |> List.exists (fun m -> m.Target.Peer.PeerId = ownerId) ->
                        Error [ ExposureOwnerDoesNotServe(exposed.ContractId, ownerId) ]
                    | None -> Error [ ExposureOwnerUnknown(exposed.ContractId, ownerId) ])

        let resolutionErrors =
            resolved
            |> List.collect (function
                | Error errs -> errs
                | Ok _ -> [])

        match duplicateMembers @ duplicateExposures @ resolutionErrors with
        | [] ->
            resolved
            |> List.choose (function
                | Ok route -> Some route
                | Error _ -> None)
            |> List.sortBy _.ContractId
            |> Ok
        | errors -> Error errors

    /// Compose one collective `PeerSurface` from the member surfaces and
    /// the exposure selection. The result is an ordinary `PeerSurface` —
    /// `PeerSurface.export` / `exportJson` stamp it through the identical
    /// code path a single instance uses, which is what makes a
    /// gateway-fronted group indistinguishable from one deployment on the
    /// wire.
    let derive
        (members: AggregateMember list, exposure: AggregateExposure)
        : Result<PeerSurface, AggregateDerivationError list> =
        routes (members, exposure)
        |> Result.map (fun resolvedRoutes ->
            let edge = edgeSurface exposure.Group

            // Exposing members = the owners actually reachable through the
            // group's face. A member serving only unlisted contracts is
            // internal and contributes no posture, no pin, and no
            // consumption — it is not part of what the group asserts.
            let exposing =
                resolvedRoutes
                |> List.map (fun r -> r.Owner.Peer.PeerId)
                |> List.distinct
                |> List.sort
                |> List.choose (fun peerId -> members |> List.tryFind (fun m -> m.Target.Peer.PeerId = peerId))

            let posturesInPlay =
                (edge.TrustPosture |> Option.toList)
                @ (exposing |> List.choose (fun m -> m.Surface.TrustPosture))

            {
                Enabled = true
                LocalPeerId = Some exposure.Group.PeerId
                Serves = {
                    Contracts =
                        resolvedRoutes
                        |> List.map (fun route -> {
                            ContractId = route.ContractId
                            Versions = route.Versions |> List.sort
                            // The gateway fuses no routine onto its own job
                            // substrate — it brokers the member's. See the
                            // long-running note at the head of this file;
                            // the routine set the dispatch closure needs
                            // rides `AggregateRoute.Routines` instead.
                            Routines = []
                        })
                    Endpoints = PeerSurface.endpoints
                }
                Consumes = externalConsumes members exposing
                TrustPosture = Some(floorPosture posturesInPlay)
                Budgets =
                    Some {
                        CascadeGuard =
                            floorFacet (
                                (edge.Budgets |> Option.toList |> List.map _.CascadeGuard)
                                @ (exposing |> List.choose (fun m -> m.Surface.Budgets) |> List.map _.CascadeGuard)
                            )
                        // Phase 630 — a floor across the exposing members,
                        // no longer a constant `false`. `PeerGateway.surface`
                        // narrows it further when the composed gateway
                        // cannot resolve a group handle.
                        LongRunningEnabled = longRunningFloor (exposing |> List.map _.Surface)
                    }
                PinnedVocabulary = unanimousPins (exposing |> List.map _.Surface)
                // Phase 642 — the authority floor across the gateway edge
                // and every exposing member. A call routed through the
                // group lands on one of them and the caller cannot choose
                // which, so the group may honestly grant only what its
                // narrowest participant grants.
                //
                // **No `mixed:` marker here, and that is not an
                // inconsistency.** The opaque posture facets take one
                // because their values are unordered — the only honest
                // report of a divergence is that there was one. Authority
                // levels are TOTALLY ORDERED, so a divergence has a
                // computable floor, and reporting it as `mixed:` would
                // discard the one property this facet has that those do
                // not. A group whose members grant `Full` and
                // `AggregatesOnly` grants `AggregatesOnly` — a claim a
                // counterparty can act on, rather than one it must treat
                // as satisfying nothing.
                DataVisibility =
                    PeerDataVisibilityLevel.label (
                        PeerDataVisibilityLevel.floor (
                            PeerSurface.dataVisibility edge
                            :: (exposing |> List.map (fun m -> PeerSurface.dataVisibility m.Surface))
                        )
                    )
                // Phase 644 — the transition grant floors by INTERSECTION
                // across the gateway edge and every exposing member. Same
                // argument as the level above and the same conclusion by a
                // different operator: a call routed through the group
                // lands on one member and the caller cannot choose which,
                // so the group may honestly admit only a transition every
                // participant admits. A set has no order, so the floor is
                // an intersection rather than a minimum — and where the
                // ordered level can report a computable floor, an
                // unordered set's honest floor is what they all share.
                TransitionAuthority =
                    (PeerSurface.transitionAuthority edge
                     :: (exposing |> List.map (fun m -> PeerSurface.transitionAuthority m.Surface)))
                    |> List.map Set.ofList
                    |> List.reduce Set.intersect
                    |> Set.toList
                    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
            })

/// Composes a deployment that presents an aggregate group as one peer:
/// it serves the exposed contracts by delegating each to its owning
/// member over the typed peer client, and reports the aggregate as its
/// own cross-instance face.
[<RequireQualifiedAccess>]
module PeerGateway =

    /// Phase 630 — the forwarding contract host for one resolved route,
    /// with optional long-running fronting.
    ///
    /// Dispatch is method-agnostic — whatever method name arrives is
    /// forwarded — so a member adding a method needs no gateway edit; what
    /// the gateway pins is the *contract* and its versions.
    ///
    /// The outbound context is derived through `PeerCascade.deriveNext`,
    /// the same single-sourced bookkeeping every other forwarding site
    /// uses (Phase 314): the group is appended to `Route`, the hop budget
    /// decremented, `RootRequestId` preserved, and a doomed hop (loop or
    /// exhausted budget) refused **before** the wire call — so a gateway
    /// hop is a first-class cascade hop, not an invisible one.
    ///
    /// **What `jobMap` changes.** A long-running method's invoke leg
    /// answers with the *member's* own `PeerJobId`. Returned verbatim,
    /// that id is useless to the caller — it addresses a job on a
    /// deployment the caller cannot see and is not authorised to poll —
    /// and it is a topology disclosure, because a caller that also talks
    /// to that member directly can correlate the two. With a map the
    /// gateway instead mints a fresh handle of its own, records the
    /// binding, and hands back the handle; the host's poll route resolves
    /// it. Without a map (`None`) the member's id passes through exactly
    /// as it did in Phase 595, which is what keeps an existing gateway
    /// byte-for-byte unchanged (GP 11).
    ///
    /// Which methods are long-running is read from `route.Routines` —
    /// the owner's own advertised handler names — through
    /// `PeerJob.handlerName`, the single naming convention the dispatch
    /// and execution sides already share. No gateway-side method list to
    /// drift.
    let forwardingContractWith
        (client: IPeerClient)
        (jobMap: IPeerGroupJobMap option)
        (group: PeerIdentity)
        (route: AggregateRoute)
        : PeerContractHost =
        let routines = Set.ofList route.Routines

        let isLongRunning (methodName: string) =
            Set.contains (PeerJob.handlerName route.ContractId methodName) routines

        /// The member answered the invoke leg with its own job id,
        /// serialised. A shape that will not parse is a member breaking
        /// the wire contract for a long-running method — reported as a
        /// handler error rather than passed on, because passing on a
        /// handle the gateway cannot resolve would fail later, at the
        /// poll, with nothing left to attribute it to.
        let tryMemberJobId (resultJson: string) : PeerJobId option =
            try
                Some(JsonRpc.deserialize<PeerJobId> resultJson)
            with _ ->
                None

        let dispatch: PeerDispatch =
            fun context methodName argsJson -> async {
                match PeerCascade.deriveNext context group route.Owner with
                | Error e -> return Error e
                | Ok outbound ->
                    let payload: PeerWirePayload = {
                        Context = outbound
                        Arguments = argsJson
                    }

                    let! result = client.Invoke(route.Owner, route.ContractId, methodName, payload)

                    match jobMap, result with
                    | Some map, Ok resultJson when isLongRunning methodName ->
                        match tryMemberJobId resultJson with
                        | None ->
                            return
                                Error(
                                    PeerHandler
                                        $"member '{route.Owner.Peer.PeerId}' answered long-running method '{methodName}' with a value that is not a job id"
                                )
                        | Some memberJobId ->
                            // A fresh v4 Guid, unrelated to the member's
                            // own id: the handle must carry no information
                            // about which member owns it, or possession of
                            // it partitions the group.
                            let groupJobId = Guid.NewGuid()

                            let binding: PeerGroupJobBinding = {
                                // The peer that dispatched THROUGH the
                                // group, from the receiver-derived inbound
                                // context (Phase 331) — not from `outbound`,
                                // which has already been re-keyed to the
                                // group itself.
                                OwnerPeerId = context.Peer.PeerId
                                MemberPeer = route.Owner
                                ContractId = route.ContractId
                                MethodName = methodName
                                MemberJobId = memberJobId
                                RootRequestId = context.RootRequestId
                            }

                            do! map.Bind(PeerJob.Scope, groupJobId, binding)
                            return Ok(JsonRpc.serialize groupJobId)
                    | _ -> return result
            }

        {
            Registration = {
                ContractId = route.ContractId
                Versions = route.Versions
                Dispatch = dispatch
            }
            // No routine is fused onto the gateway's job substrate — the
            // member owns the execution leg; the gateway owns only the
            // handle translation in front of it.
            JobHandlers = []
        }

    /// The Phase 595 shape: forward the invoke leg, front no poll leg. A
    /// long-running method's member job id passes through unchanged.
    let forwardingContract (client: IPeerClient) (group: PeerIdentity) (route: AggregateRoute) : PeerContractHost =
        forwardingContractWith client None group route

    /// Register the `PeerGroupJobFronting` the host's poll route resolves,
    /// through the base `ServerApp`'s `ServiceConfig` seam — the same route
    /// `withCommutativeCipher` takes, so nothing in `PeerCompose.run` has
    /// to know a gateway exists. `TryAdd`, so a deployment that registered
    /// its own fronting earlier keeps it.
    let private registerFronting (map: IPeerGroupJobMap) (client: IPeerClient) (app: PeerServerApp) : PeerServerApp =
        let register (services: IServiceCollection) =
            services.TryAddSingleton<PeerGroupJobFronting>({ Map = map; Client = client })
            services

        let extensions = app.Base.Extensions

        {
            app with
                Base = {
                    app.Base with
                        Extensions = {
                            extensions with
                                ServiceConfig =
                                    match extensions.ServiceConfig with
                                    | None -> Some register
                                    | Some existing -> Some(existing >> register)
                        }
                }
        }

    /// Register the group's exposed contracts on a peer composition as
    /// forwarding hosts, adopt the group identity as the deployment's
    /// local peer, and declare the group's external consumption. The
    /// composition's own `withConfig` still decides everything else —
    /// this helper contributes registrations, never configuration.
    ///
    /// `client` is the transport the gateway delegates over. A deployment
    /// that wants the composed `IPeerClient` singleton (built inside
    /// `PeerServerApp.run` from the resolved `ISecretStore`) passes a thin
    /// forwarder that resolves it per call; a deployment that owns its own
    /// `HttpPeerClient` passes it directly.
    ///
    /// Fails with the full error list when the exposure cannot be
    /// resolved — a gateway that cannot say what it fronts must not
    /// compose.
    ///
    /// **Phase 630 — long-running fronting is opt-in through
    /// `PeerServerApp.withGroupJobMap`.** When the app carries a group job
    /// map, the forwarding dispatch mints group handles for the members'
    /// long-running methods and a `PeerGroupJobFronting` singleton is
    /// registered so the host's poll route can resolve them. When it does
    /// not, this composes exactly what Phase 595 composed — same
    /// registrations, same singletons, same derived surface (GP 11 /
    /// GP 13).
    let withAggregate
        (client: IPeerClient)
        (members: AggregateMember list)
        (exposure: AggregateExposure)
        (app: PeerServerApp)
        : Result<PeerServerApp, AggregateDerivationError list> =
        match AggregatePeerSurface.routes (members, exposure), AggregatePeerSurface.derive (members, exposure) with
        | Ok resolvedRoutes, Ok aggregate ->
            let withContracts =
                resolvedRoutes
                |> List.fold
                    (fun acc route ->
                        acc
                        |> PeerServerApp.withContract (fun _ ->
                            forwardingContractWith client app.GroupJobMap exposure.Group route))
                    app

            let withFronting =
                match app.GroupJobMap with
                | None -> withContracts
                | Some map -> registerFronting map client withContracts

            aggregate.Consumes
            |> List.fold (fun acc consumed -> PeerServerApp.withConsumedContract consumed acc) withFronting
            |> PeerServerApp.withLocalPeer exposure.Group
            |> Ok
        | Error errors, _
        | _, Error errors -> Error errors

    /// The gateway's live cross-instance face. The serving and consuming
    /// halves come from `PeerSurface.describe` of the *composed* app — so
    /// they are derived from the registrations the gateway actually
    /// mounts, never re-asserted from the exposure list — and the group's
    /// posture / budget / vocabulary floors are folded over the top,
    /// because those describe the members behind the edge and no amount of
    /// local introspection can see them.
    ///
    /// For a composition built by `withAggregate` (over an enabled peer
    /// substrate, with no registrations of its own) this equals
    /// `AggregatePeerSurface.derive` exactly — asserted by test. A gateway
    /// that registers extra contracts diverges, which is precisely what
    /// that assertion exists to catch.
    ///
    /// **Phase 630 — `LongRunningEnabled` is narrowed by what THIS gateway
    /// composed.** `derive` reports the members' floor: what the group
    /// *could* front. Whether it does depends on a group job map being
    /// composed, and only the app knows that. A gateway without one
    /// therefore reports `false` however capable its members are — a
    /// deployment that advertised a poll leg it cannot resolve would hand
    /// counterparties a pinned face that lies, which is worse than the
    /// narrower truth. The two agree (and the equality above holds)
    /// whenever the composition matches the capability, in both
    /// directions.
    let surface
        (members: AggregateMember list)
        (exposure: AggregateExposure)
        (app: PeerServerApp)
        : Result<PeerSurface, AggregateDerivationError list> =
        AggregatePeerSurface.derive (members, exposure)
        |> Result.map (fun aggregate ->
            let live = PeerSurface.describe app

            {
                live with
                    TrustPosture = aggregate.TrustPosture
                    Budgets =
                        aggregate.Budgets
                        |> Option.map (fun budgets -> {
                            budgets with
                                LongRunningEnabled = budgets.LongRunningEnabled && app.GroupJobMap.IsSome
                        })
                    PinnedVocabulary = aggregate.PinnedVocabulary
            })