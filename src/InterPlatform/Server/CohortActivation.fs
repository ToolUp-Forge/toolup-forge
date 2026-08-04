// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 490 — the enforced activation pipeline ─────────────────────
//
// `IActivationDestination.fs` is the vocabulary and the canonical
// encoding; this file is the one road from a cohort to a destination.
// Read that file's header first — it explains WHERE activation sits in
// the clean-room gate's invariant chain and why the raw id list is
// unreachable rather than forbidden. This header explains how the
// pipeline is built so that there is only one road.
//
// ── The pipeline runs INSIDE the gate, not beside it ──
//
// The obvious implementation of "match, then apply the floor, then
// release" is a sequence of calls in this module. That implementation
// would be a second enforcement path: it would have its own idea of the
// order, its own idea of what a withhold is, and — the moment Phase 190
// or Phase 481 changed the gate — its own idea of the epsilon. The
// estate has watched precisely this shape fail (Phase 18b's broker sat
// registered and uncalled for months, and Phase 330's `VerifyDelegation`
// shipped with no call site at all), so the pipeline does not
// re-implement the chain. It **dispatches through it**.
//
// `activate` builds a synthetic `PeerContractRegistration` whose
// dispatch resolves the cohort and runs the private match, hands it to
// `CleanRoomGate.wrapNoised` with the DERIVED template, and calls the
// resulting dispatch. Every invariant the gate has — 0 bilateral
// approval, 0.5 the epsilon reservation, 1 the surface, 2 checkability,
// 3 the release post-condition, 4 calibrated noise — therefore applies
// to an activation with no code here to keep in step, and a future
// invariant 6 applies to activation the day it lands.
//
// Three consequences worth naming, because each is a property nobody
// wrote a check for:
//
//   * **The counterparty is not contacted until the activation is
//     authorised.** The match is inside the dispatch closure, and
//     invariant 0 runs before the closure does. An activation for an
//     unapproved cohort, an unapproved purpose, or a revoked
//     authorisation produces no PSI exchange at all — not an exchange
//     whose answer is discarded.
//   * **A sub-floor cohort never causes a membership-revealing
//     exchange.** The dispatch always runs the Phase 479
//     `CardinalityOnly` preflight first, and only reaches the `Members`
//     run when that size already clears the composed floor. Withholding
//     a membership answer after computing it would be a weaker
//     statement than never asking for it.
//   * **A failed dispatch refunds its epsilon.** That is the gate's
//     settlement rule, inherited: a resolver failure or an unreachable
//     counterparty produced nothing about the protected data, so nothing
//     is owed.
//
// ── Invariant 5 — the egress projection ──
//
// What this file adds to the chain is the last step: the cleared
// `CohortResult` becomes an `ActivationRelease` in the destination's
// approved shape, or it becomes a refusal. It runs after invariant 4 for
// the same reason invariant 4 runs after invariant 3 — each layer binds
// on what the layer below it cleared, and a projection performed earlier
// would be projecting an answer nobody had yet agreed to release.
//
// The refusal that matters most is the token one. A `ReleaseTokens`
// activation carries one token per matched member, so the LENGTH of the
// list is the true cohort size. If suppression or calibrated noise moved
// the released count away from the matched count, publishing the token
// list would republish the number the gate had just adjusted — so the
// projection refuses rather than reconciling. There is no un-noised
// fallback and no truncation, on exactly the argument
// `CleanRoomGate.wrapNoised` makes about an exhausted budget: quietly
// degrading to the raw answer is the one behaviour that would make the
// arrangement worse than not having it.
//
// ── Cost when unused (GP 13) ──
//
// Nothing here is composed by `PeerServerApp.run`. A deployment that
// never calls `activate` constructs none of these types and pays
// nothing; its peer substrate is byte-for-byte what it was before this
// phase (GP 11).

/// The substrate an activation runs over. A record rather than
/// constructor parameters so a composition can carry its posture as a
/// value it can log and audit, the shape `PsiRunOptions` already takes.
///
/// The four optional members mirror the gate's optional invariants
/// exactly: `None` in each is the pre-phase behaviour of that invariant,
/// so an activation composed with none of them is Phase 311's three
/// invariants and nothing else (GP 11).
type ActivationDeps = {
    /// The privacy mechanism. Substitutable (GP 1); the substrate's own
    /// release post-condition still binds over whatever it returns.
    Broker: ICleanRoomBroker
    /// Invariant 0 — the bilateral-approval check, normally
    /// `TemplateApprovalGate.check policy localPeerId`. `None` composes
    /// an UNGOVERNED activation, which is a legitimate posture only for
    /// a deployment activating to itself; the whole of this phase is the
    /// `Some` case.
    Approval: CleanRoomApprovalCheck option
    /// Invariant 0.5 — the cumulative epsilon meter.
    Budget: PrivacyBudgetMeter option
    /// Invariant 4 — the calibrated-noise posture.
    Noise: (INoiseMechanism * NoisedReleasePolicy) option
    /// Turns the cohort's definition into an id set.
    Resolver: ICohortResolver
    /// The private match (Phases 18f / 479).
    Matcher: IActivationMatcher
    /// Required for a `ReleaseTokens` activation; unused otherwise.
    Tokens: IActivationTokenMinter option
    /// One row per activation-pipeline decision, alongside the rows the
    /// gate itself records. `CohortActivation.noAudit` records nothing.
    Sink: PeerCleanRoomDecisionPayload -> Async<unit>
}

/// One activation.
type ActivationRequest = {
    /// This cohort, for this purpose, to this destination — the unit
    /// both parties signed.
    Authorisation: ActivationAuthorisation
    /// The live delivery seam. Its `Descriptor` must equal the
    /// authorised one; a destination whose live description has drifted
    /// from the approved one is refused before anything is computed.
    Destination: IActivationDestination
    /// Which of the destination's approved shapes to release.
    Shape: ActivationShape
    /// Labels a matched member into a histogram bucket. Required for
    /// `ReleaseHistogram`, unused otherwise. A caller-supplied function
    /// because bucketing is domain vocabulary and forge has none (GP 1).
    Bucket: (string -> string) option
    /// The call context the approval check reads its counterparty from.
    /// `None` derives one from the destination — the outbound case,
    /// where this deployment initiates. An inbound activation pulled by
    /// the counterparty passes its own validated context instead.
    Context: PeerCallContext option
}

/// The `Members`-only resolver: the shipped default.
[<RequireQualifiedAccess>]
module CohortResolver =

    /// Resolves a materialised member set and refuses a query
    /// expression by name.
    ///
    /// Refusing rather than returning an empty set is the fail-closed
    /// reading: an empty cohort is a legitimate answer that the k-floor
    /// then withholds, and a resolver that answered "empty" for
    /// "I cannot evaluate this" would make a configuration error
    /// indistinguishable from a genuine zero overlap.
    let membersOnly: ICohortResolver =
        { new ICohortResolver with
            member _.Resolve(definition) = async {
                match definition with
                | CohortMembers ids -> return Ok ids
                | CohortQuery _ ->
                    return
                        Error
                            "this deployment composed CohortResolver.membersOnly, which resolves a materialised member set only — compose an ICohortResolver that understands the deployment's query language before authorising a CohortQuery definition"
            }
        }

[<RequireQualifiedAccess>]
module ActivationDeps =

    /// The minimum composition: a mechanism, a resolver, a matcher, and
    /// no audit. Every governance control is added explicitly, so a
    /// deployment cannot believe it has one it never composed.
    let create (broker: ICleanRoomBroker) (resolver: ICohortResolver) (matcher: IActivationMatcher) : ActivationDeps = {
        Broker = broker
        Approval = None
        Budget = None
        Noise = None
        Resolver = resolver
        Matcher = matcher
        Tokens = None
        Sink = fun _ -> async { return () }
    }

    /// Invariant 0 — require a live bilateral approval of the exact
    /// authorisation. This is what makes an activation *governed*.
    let withApproval (check: CleanRoomApprovalCheck) (deps: ActivationDeps) : ActivationDeps = {
        deps with
            Approval = Some check
    }

    /// Invariant 0.5 — meter the activation against a cumulative
    /// epsilon budget.
    let withBudget (meter: PrivacyBudgetMeter) (deps: ActivationDeps) : ActivationDeps = {
        deps with
            Budget = Some meter
    }

    /// Invariant 4 — apply calibrated noise to the released cohort.
    /// Incompatible with `ReleaseTokens` by construction (see the file
    /// header); the projection refuses rather than reconciling.
    let withNoise (mechanism: INoiseMechanism) (policy: NoisedReleasePolicy) (deps: ActivationDeps) : ActivationDeps = {
        deps with
            Noise = Some(mechanism, policy)
    }

    /// Required before a `ReleaseTokens` activation can project.
    let withTokenMinter (minter: IActivationTokenMinter) (deps: ActivationDeps) : ActivationDeps = {
        deps with
            Tokens = Some minter
    }

    /// Record one row per pipeline decision.
    let withAudit (sink: PeerCleanRoomDecisionPayload -> Async<unit>) (deps: ActivationDeps) : ActivationDeps = {
        deps with
            Sink = sink
    }

/// The Phase 18f / 479 matcher: `CardinalityOnly` for the preflight,
/// `Members` for a token or histogram release.
///
/// Thin by design — it owns no protocol, only the mapping from an
/// activation's two questions onto the two PSI modes that answer them.
/// `exchange` is a per-destination wire closure, typically a
/// `JsonRpcPeerClient` proxy over `PsiModeApi`, which is what keeps this
/// type free of any transport knowledge (the discipline
/// `IRoundOrchestrator.RunRounds` follows for the same reason).
///
/// Stateless between calls (GP 12 rule 4): the key and the exchange
/// factory are composition-time values, and nothing is carried from one
/// activation to the next.
type PsiActivationMatcher
    (
        psi: IPrivateSetIntersectionModes,
        key: byte[],
        exchange: ActivationDestination -> (PsiModeRequest -> Async<Result<PsiModeResponse, PeerError>>)
    ) =

    /// A cohort id is opaque to the substrate; UTF-8 is the encoding the
    /// peer wire already uses for every other opaque identifier, and the
    /// round trip back through `MatchedElements` is what makes the
    /// matched set usable without forge knowing what an id means.
    static let toElements (ids: string list) = ids |> List.map Encoding.UTF8.GetBytes

    static let options mode : PsiRunOptions = {
        Mode = mode
        Aggregator = None
        AllowRevealingAggregator = false
        // The release gate is deliberately NOT set here: the floor is
        // applied once, by the clean-room gate the activation dispatches
        // through. Two floors on one answer is two places to disagree.
        Release = None
    }

    interface IActivationMatcher with
        member _.MatchSize(cohort, destination) = async {
            let! outcome = psi.IntersectAs(options CardinalityOnly, key, toElements cohort, exchange destination)

            match outcome with
            | Error e -> return Error e
            | Ok(CardinalityReleased matched) -> return Ok matched
            | Ok(ReleaseWithheld reason) -> return Error(PsiProtocol reason)
            | Ok other ->
                return
                    Error(
                        PsiProtocol
                            $"the cardinality preflight for destination '{destination.DestinationId}' answered with {other.GetType().Name}, which is not an intersection size"
                    )
        }

        member _.MatchMembers(cohort, destination) = async {
            let! outcome = psi.IntersectAs(options Members, key, toElements cohort, exchange destination)

            match outcome with
            | Error e -> return Error e
            | Ok(MembersReleased result) -> return Ok(result.MatchedElements |> List.map Encoding.UTF8.GetString)
            | Ok(ReleaseWithheld reason) -> return Error(PsiProtocol reason)
            | Ok other ->
                return
                    Error(
                        PsiProtocol
                            $"the membership run for destination '{destination.DestinationId}' answered with {other.GetType().Name}, which carries no intersection set"
                    )
        }

/// The default token minter: HMAC-SHA256 over the authorisation digest
/// and the member id, under one deployment-wide key read from
/// `ISecretStore` per call.
///
/// **One key, not one per destination, and the unlinkability still
/// holds.** The message includes the authorisation id, which carries the
/// SHA-256 of the destination, the purpose and the cohort definition —
/// so the same person activated for two purposes, or to two
/// destinations, yields two unrelated tokens without a second key to
/// rotate. A per-destination key would add custody surface and buy
/// nothing the message already provides.
///
/// Nothing here is hand-rolled: the MAC is `JwtCrypto.computeHmac`, the
/// comparison is `JwtCrypto.fixedTimeEquals`, and the encoding is the
/// platform `Base64Url` — the same three primitives the peer token layer
/// uses.
type HmacActivationTokenMinter(secrets: ISecretStore) =

    /// The reserved key holding the deployment's activation-token
    /// secret. Namespaced beside the peer signing keys rather than in a
    /// scope of its own, so one custody policy covers both.
    static let secretKey = "activation/token-key"

    /// Length-prefixed, for exactly the reason
    /// `ActivationCanonical.field` is: without it, the pair
    /// `("abc", "de")` and `("ab", "cde")` would MAC identically, and a
    /// token minted under one authorisation would verify under another.
    static let message (authorisationId: string) (memberId: string) =
        let sb = StringBuilder()

        sb
            .Append(Encoding.UTF8.GetByteCount authorisationId)
            .Append(':')
            .Append(authorisationId)
            .Append(Encoding.UTF8.GetByteCount memberId)
            .Append(':')
            .Append(memberId)
        |> ignore

        Encoding.UTF8.GetBytes(sb.ToString())

    let compute (authorisationId: string) (memberId: string) = async {
        let! secret = secrets.GetSecret(PeerJwt.platformScope, secretKey)

        match secret with
        | None ->
            return
                Error
                    $"No activation-token key registered (expected a secret at '{secretKey}' in the reserved '{PeerJwt.platformScope}' scope). A token release cannot mint an unlinkable pseudonym without one"
        | Some value when not (PeerJwt.signingKeyIsStrong value) ->
            // The same floor the peer signing keys are held to: an HMAC
            // key below the SHA-256 block size offers less than the full
            // keyspace, and a blank one is publicly computable — which
            // would make every token forgeable by anyone who knows the
            // member id.
            return
                Error
                    $"The registered activation-token key is blank or shorter than {PeerJwt.minSigningKeyBytes} bytes, so the tokens it mints would be forgeable"
        | Some value ->
            let mac =
                JwtCrypto.computeHmac (Encoding.UTF8.GetBytes value) (message authorisationId memberId)

            return Ok(Base64Url.encode mac)
    }

    interface IActivationTokenMinter with
        member _.Mint(authorisationId, memberId) = compute authorisationId memberId

        member _.Verify(authorisationId, memberId, token) = async {
            let! expected = compute authorisationId memberId

            match expected with
            | Error e -> return Error e
            | Ok value ->
                // Constant time over the encoded forms, the shape
                // `PeerJwt.constantTimeEquals` already uses: a
                // redemption endpoint that leaked a prefix match through
                // its timing would be a token oracle.
                return Ok(JwtCrypto.fixedTimeEquals (Encoding.UTF8.GetBytes value) (Encoding.UTF8.GetBytes token))
        }

/// The one road from a cohort to a destination.
[<RequireQualifiedAccess>]
module CohortActivation =

    /// The audit sink a composition without an `IAuditLog` uses: the
    /// pipeline still decides, it simply records nothing. Explicit
    /// rather than an `option` so there is one code path, the same
    /// reason `CleanRoomGate.noAudit` exists.
    let noAudit: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun _ -> async { return () }

    /// The version the peer contract this pipeline synthesises is
    /// dispatched under. It never reaches the wire — the registration is
    /// built and called in-process — but the gate reads a context, and a
    /// context needs one.
    let private v1: ContractVersion = { Major = 1; Minor = 0 }

    /// The outbound call context: this deployment initiating an
    /// activation towards the destination's owning peer. The
    /// counterparty id is taken from the AUTHORISED descriptor, never
    /// from anything the destination asserts at call time — it is the
    /// id the approval was signed against.
    let contextFor (request: ActivationRequest) : PeerCallContext = {
        Peer = {
            PeerId = request.Authorisation.Destination.CounterpartyPeerId
            DisplayName = request.Authorisation.Destination.DestinationId
        }
        User = Anonymous
        ContractVersion = v1
        Route = [ request.Authorisation.Destination.CounterpartyPeerId ]
        RootRequestId = ActivationCanonical.id request.Authorisation
        ParentRequestId = None
        HopsRemaining = 1
    }

    /// Record without ever letting the recording fail the activation.
    /// Best-effort on the same terms as `CleanRoomGate.record`: an audit
    /// sink that is down must not turn a cleared release into a refusal,
    /// nor a refusal into a release. Cancellation propagates rather than
    /// being swallowed — a disconnected caller is not a failed write.
    let private record (sink: PeerCleanRoomDecisionPayload -> Async<unit>) payload = async {
        try
            do! sink payload
        with ex when not (ex :? OperationCanceledException) ->
            ()
    }

    /// Invariant 5 — the egress projection.
    ///
    /// Separate and pure-ish so it can be read (and tested) as the
    /// single answer to "what may leave, given what the gate cleared".
    /// `matched` is the preflight size; `cleared` is what invariants 3
    /// and 4 released; `members` is populated only when the dispatch
    /// performed a membership run.
    let private project
        (deps: ActivationDeps)
        (request: ActivationRequest)
        (matched: int)
        (members: string list option)
        (cleared: CohortResult)
        : Async<Result<ActivationRelease, ActivationRefusal>> =
        async {
            let destination = request.Authorisation.Destination
            let released = cleared.Cells |> List.sumBy _.Count

            if not (Set.contains request.Shape destination.PermittedShapes) then
                // Unreachable on the composed path — the derived
                // template's `AllowedMethods` IS this set, so the gate's
                // invariant 1 refused it long before now. Kept total
                // rather than assumed: an egress classifier's default
                // branch must be silence.
                return
                    Error(
                        ActivationEgressRefused
                            $"shape '{ActivationShape.name request.Shape}' is not among the shapes destination '{destination.DestinationId}' is authorised to receive"
                    )
            else
                match request.Shape with
                | ReleaseCount -> return Ok(ActivatedCount released)
                | ReleaseHistogram ->
                    return
                        Ok(
                            ActivatedHistogram(
                                cleared.Cells
                                |> List.map (fun cell -> {
                                    Label = cell.Label
                                    Count = cell.Count
                                })
                            )
                        )
                | ReleaseTokens ->
                    match deps.Tokens, members with
                    | None, _ ->
                        return
                            Error(
                                ActivationEgressRefused
                                    "a token release needs a composed IActivationTokenMinter — releasing raw matched ids in its place is not a fallback this substrate has"
                            )
                    | _, None ->
                        return
                            Error(
                                ActivationEgressRefused
                                    "the membership run did not take place, so there is nothing to mint tokens over"
                            )
                    | Some minter, Some matchedMembers ->
                        if released <> matched || List.length matchedMembers <> matched then
                            // The token list's LENGTH is the true cohort
                            // size. Publishing it after suppression or
                            // calibrated noise moved the released count
                            // would republish the number the gate had
                            // just adjusted, so the projection refuses
                            // rather than truncating or padding — there
                            // is no un-noised fallback here for the same
                            // reason there is none in the gate.
                            return
                                Error(
                                    ActivationEgressRefused
                                        $"the released cohort ({released}) differs from the matched cohort ({matched}), so a per-member token list would disclose the true size the gate adjusted; a token release cannot be composed with suppression or calibrated noise that moves the count"
                                )
                        else
                            let ordered =
                                matchedMembers |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

                            let authorisationId = ActivationCanonical.id request.Authorisation

                            let! minted =
                                ordered
                                |> List.map (fun memberId -> minter.Mint(authorisationId, memberId))
                                |> Async.Sequential

                            let tokens = minted |> Array.choose Result.toOption

                            if Array.length tokens <> Array.length minted then
                                // All or nothing. A partial token list
                                // is an audience that silently omits
                                // people, which is worse than a refusal
                                // because nothing downstream can tell.
                                return
                                    Error(
                                        ActivationEgressRefused
                                            "one or more activation tokens could not be minted; no partial token list is released"
                                    )
                            else
                                return Ok(ActivatedTokens(List.ofArray tokens))
        }

    /// Activate `request`'s cohort to its destination.
    ///
    /// The whole governance chain in one call. See the file header for
    /// why the pipeline dispatches THROUGH `CleanRoomGate` rather than
    /// re-implementing its ordering, and `IActivationDestination.fs`'s
    /// header for how an authorisation binds to the exact cohort
    /// definition, purpose and destination.
    let activate
        (deps: ActivationDeps)
        (request: ActivationRequest)
        : Async<Result<ActivationRelease, ActivationRefusal>> =
        async {
            let auth = request.Authorisation
            let destination = auth.Destination
            let template = ActivationCanonical.template auth
            let authorisationId = template.TemplateId
            let shapeName = ActivationShape.name request.Shape
            let context = request.Context |> Option.defaultValue (contextFor request)

            let row released reason : PeerCleanRoomDecisionPayload = {
                ContractId = destination.DestinationId
                MethodName = shapeName
                TemplateId = authorisationId
                CallerPeerId = destination.CounterpartyPeerId
                RootRequestId = context.RootRequestId
                Released = released
                SuppressedCells = []
                Reason = reason
                OccurredAt = DateTimeOffset.UtcNow
            }

            // The live destination must be the one that was approved. A
            // seam whose descriptor has drifted from the signed one is
            // not a governance edge case — it is the whole attack in
            // miniature, and it is refused before the counterparty is
            // contacted or the cohort is even resolved.
            if request.Destination.Descriptor <> destination then
                let reason =
                    $"destination '{destination.DestinationId}' presents a descriptor that differs from the authorised one, so the approval does not cover the destination this activation would reach"

                do! record deps.Sink (row false reason)
                return Error(ActivationUnauthorised reason)
            else
                // Out-of-band carriers for the values the dispatch
                // computes. `PeerDispatch` answers with a serialised
                // string and a `PeerError`, which is the right contract
                // for a peer call and the wrong one for a typed refusal
                // — so the typed refusal, the preflight size, and the
                // matched membership ride out through per-call refs.
                // Locals, not module state: the pipeline holds nothing
                // between activations (GP 12 rule 4).
                let refusal = ref None
                let preflight = ref 0
                let members = ref None

                let dispatch: PeerDispatch =
                    fun _ _ _ -> async {
                        let! resolved = deps.Resolver.Resolve auth.Cohort.Definition

                        match resolved with
                        | Error reason ->
                            refusal.Value <- Some(ActivationCohortUnresolved reason)
                            return Error(PeerHandler reason)
                        | Ok ids ->
                            // Phase 479 `CardinalityOnly`: the size
                            // preflight, always, before anything that
                            // could reveal membership.
                            let! sized = deps.Matcher.MatchSize(ids, destination)

                            match sized with
                            | Error e ->
                                refusal.Value <- Some(ActivationMatchFailed e)
                                return Error(PeerHandler(sprintf "the cardinality preflight failed: %A" e))
                            | Ok matched ->
                                preflight.Value <- matched

                                // A membership run happens only for a
                                // shape that needs one AND only on a
                                // cohort whose size already clears the
                                // composed floor. Withholding a
                                // membership answer after computing it
                                // would be the weaker statement.
                                let needsMembers =
                                    match request.Shape with
                                    | ReleaseCount -> false
                                    | ReleaseTokens
                                    | ReleaseHistogram -> true

                                if needsMembers && matched >= template.Floor.MinCohortSize then
                                    let! matchedMembers = deps.Matcher.MatchMembers(ids, destination)

                                    match matchedMembers with
                                    | Error e ->
                                        refusal.Value <- Some(ActivationMatchFailed e)
                                        return Error(PeerHandler(sprintf "the membership run failed: %A" e))
                                    | Ok found ->
                                        members.Value <- Some found

                                        match request.Shape, request.Bucket with
                                        | ReleaseHistogram, None ->
                                            let reason =
                                                "a histogram release needs a caller-supplied bucketing function; forge has no bucket vocabulary of its own"

                                            refusal.Value <- Some(ActivationEgressRefused reason)
                                            return Error(PeerHandler reason)
                                        | ReleaseHistogram, Some bucket ->
                                            let cells =
                                                found
                                                |> List.countBy bucket
                                                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                                                |> List.map (fun (label, count) -> {
                                                    Label = label
                                                    Count = count
                                                    Value = None
                                                })

                                            return Ok(JsonRpc.serialize { Shape = Histogram; Cells = cells })
                                        | _ ->
                                            return
                                                Ok(
                                                    JsonRpc.serialize {
                                                        Shape = ActivationShape.outputShape request.Shape
                                                        Cells = [
                                                            {
                                                                Label = auth.Purpose.PurposeId
                                                                Count = matched
                                                                Value = None
                                                            }
                                                        ]
                                                    }
                                                )
                                else
                                    return
                                        Ok(
                                            JsonRpc.serialize {
                                                Shape = ActivationShape.outputShape request.Shape
                                                Cells = [
                                                    {
                                                        Label = auth.Purpose.PurposeId
                                                        Count = matched
                                                        Value = None
                                                    }
                                                ]
                                            }
                                        )
                    }

                let registration: PeerContractRegistration = {
                    ContractId = destination.DestinationId
                    Versions = [ v1 ]
                    Dispatch = dispatch
                }

                // The single road. Invariants 0 → 4, applied by the same
                // wrapper every gated peer contract goes through.
                let gated =
                    (CleanRoomGate.wrapNoised
                        deps.Broker
                        template
                        deps.Approval
                        deps.Budget
                        deps.Noise
                        deps.Sink
                        registration)
                        .Registration

                let! answer = gated.Dispatch context shapeName "[]"

                match answer with
                | Error(PeerCleanRoomWithheld templateId) ->
                    // The gate collapses every withhold — unapproved,
                    // sub-floor, off-surface, budget-exhausted — into
                    // one opaque refusal, and it is right to: a REMOTE
                    // caller that can vary its request and read the
                    // reason back has a counting oracle over the
                    // protected data.
                    //
                    // The deployment running its own activation is not
                    // that caller. Its operator needs to know whether to
                    // chase an approval or a bigger cohort, so the
                    // refusal is CLASSIFIED here by re-asking the same
                    // check the gate ran as invariant 0.
                    //
                    // Re-asking rather than pre-asking is deliberate on
                    // both counts. The gate stays the sole ENFORCER —
                    // deleting this block changes nothing about whether
                    // an unapproved activation releases — and the extra
                    // registry read happens only on a refusal, never on
                    // the path that released (GP 13).
                    match deps.Approval with
                    | None -> return Error(ActivationWithheld templateId)
                    | Some check ->
                        let! verdict = check context template

                        match verdict with
                        | Error reason -> return Error(ActivationUnauthorised reason)
                        | Ok() -> return Error(ActivationWithheld templateId)
                | Error e ->
                    match refusal.Value with
                    | Some typed ->
                        do! record deps.Sink (row false (sprintf "%A" typed))
                        return Error typed
                    | None ->
                        let reason = sprintf "the activation dispatch failed: %A" e
                        do! record deps.Sink (row false reason)
                        return Error(ActivationDeliveryFailed reason)
                | Ok json ->
                    let cleared = JsonRpc.deserialize<CohortResult> json
                    let! projected = project deps request preflight.Value members.Value cleared

                    match projected with
                    | Error typed ->
                        do! record deps.Sink (row false (sprintf "%A" typed))
                        return Error typed
                    | Ok release ->
                        let! delivered = request.Destination.Deliver(authorisationId, release)

                        match delivered with
                        | Error reason ->
                            do! record deps.Sink (row false $"the destination refused the delivery — {reason}")
                            return Error(ActivationDeliveryFailed reason)
                        | Ok() ->
                            do!
                                record
                                    deps.Sink
                                    (row
                                        true
                                        $"activated cohort '{auth.Cohort.CohortId}' to destination '{destination.DestinationId}' for purpose '{auth.Purpose.PurposeId}' as {shapeName} under authorisation {authorisationId}")

                            return Ok release
        }