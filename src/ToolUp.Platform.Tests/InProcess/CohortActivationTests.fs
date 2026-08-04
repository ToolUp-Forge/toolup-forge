module ToolUp.Platform.Tests.InProcess.CohortActivationTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 490 — the governed cohort-activation seam ─────────────────
//
// Phase 311 made the clean-room floor structural and Phase 480 made the
// template approval bind to the exact bytes being enforced. This pack is
// about the step neither of them covers: the moment a cohort stops being
// an analytical artefact and starts being USED.
//
// Six kinds of case, in the order they carry weight:
//
//   1. **The mutation probe.** An authorisation issued for cohort C must
//      not authorise C′. Measured three ways — version inequality across
//      every field, the derived template's version moving with it, and
//      an end-to-end activation of an edited cohort being refused.
//   2. **Purpose limitation.** An approval for purpose A must not
//      authorise purpose B, with the cohort and destination byte-
//      identical. This is the control that stops an approval obtained
//      for measurement being reused for targeting.
//   3. **Revocation takes effect on the next activation**, from either
//      party.
//   4. **The probes have teeth.** Every refusal case is paired with a
//      control that RELEASES — usually the identical scenario with the
//      one governance control removed. Without that half, "the
//      unapproved activation was refused" would pass equally against a
//      pipeline that had broken and started refusing everything.
//   5. **Ordering is observed, not asserted.** The counterparty is not
//      contacted before the activation is authorised, and a sub-floor
//      cohort never causes a membership-revealing exchange — both
//      measured with a call-counting matcher rather than argued from
//      the source.
//   6. **The raw id list never crosses.** `ActivationRelease` has no
//      case that could carry one, and what the destination actually
//      received is searched for the cohort's own ids.

// ─── Fixtures ────────────────────────────────────────────────────────

[<Literal>]
let private seller = "seller-ssp"

[<Literal>]
let private buyer = "buyer-acme"

[<Literal>]
let private tokenKey = "an-activation-token-key-of-at-least-32-bytes"

let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Histogram ]
}

/// 40 opaque ids; the first 24 are also on the counterparty's side.
let private cohortIds = [ for i in 0..39 -> sprintf "member-%02i" i ]
let private overlapIds = cohortIds |> List.take 24

let private destinationD: ActivationDestination = {
    DestinationId = "audience-endpoint"
    CounterpartyPeerId = buyer
    PermittedShapes = Set.ofList [ ReleaseCount; ReleaseTokens ]
    Floor = floor
}

let private purposeA: ActivationPurpose = {
    PurposeId = "reach-measurement"
    Description = "Estimate the reach of a campaign against the partner's audience."
}

/// Same cohort, same destination — a DIFFERENT agreed use.
let private purposeB: ActivationPurpose = {
    PurposeId = "audience-targeting"
    Description = "Activate the matched audience for paid distribution."
}

let private cohortC: CohortSpec = {
    CohortId = "high-value"
    Definition = CohortMembers cohortIds
    Constraints = {
        MinCohortSize = 10
        Predicates = [ { Name = "recency"; Value = "P30D" }; { Name = "value"; Value = "500" } ]
    }
}

/// The edit: one more member. Nothing about the id, the purpose or the
/// destination moved — only the definition of who is in the cohort.
let private cohortCPrime: CohortSpec = {
    cohortC with
        Definition = CohortMembers("member-99" :: cohortIds)
}

let private authFor (cohort: CohortSpec) (purpose: ActivationPurpose) (destination: ActivationDestination) = {
    Cohort = cohort
    Purpose = purpose
    Destination = destination
}

let private authorisationA = authFor cohortC purposeA destinationD

// ─── Stubs ───────────────────────────────────────────────────────────

/// Records what actually crossed the boundary, and can refuse.
type private RecordingDestination(descriptor: ActivationDestination) =
    let delivered = ResizeArray<string * ActivationRelease>()

    member _.Delivered = List.ofSeq delivered
    member val Refuse: string option = None with get, set

    interface IActivationDestination with
        member _.Descriptor = descriptor

        member this.Deliver(authorisationId, release) = async {
            match this.Refuse with
            | Some reason -> return Error reason
            | None ->
                delivered.Add((authorisationId, release))
                return Ok()
        }

/// The private match, stubbed — the protocol itself is Phase 18f / 479's
/// to prove. What this pack needs from it is a call COUNT, because the
/// ordering claims ("the counterparty is not contacted until the
/// activation is authorised", "a sub-floor cohort causes no membership
/// run") are only checkable by observing that the exchange did not
/// happen.
type private CountingMatcher(overlap: string list) =
    let mutable sizeCalls = 0
    let mutable memberCalls = 0

    member _.SizeCalls = sizeCalls
    member _.MemberCalls = memberCalls

    interface IActivationMatcher with
        member _.MatchSize(cohort, _) = async {
            sizeCalls <- sizeCalls + 1
            return Ok(overlap |> List.filter (fun m -> List.contains m cohort) |> List.length)
        }

        member _.MatchMembers(cohort, _) = async {
            memberCalls <- memberCalls + 1
            return Ok(overlap |> List.filter (fun m -> List.contains m cohort))
        }

/// A deterministic "noise" mechanism that always shifts by `+units`.
/// Not a distributional claim — Phase 481's pack owns those. What this
/// one needs is a release whose count demonstrably MOVED, so the token
/// projection's refusal can be measured rather than argued.
type private FixedNoise(units: int64) =
    interface INoiseMechanism with
        member _.SampleUnits(_) = units
        member _.Sample(spec) = float units * spec.Granularity

/// Counts registry reads, so "a composition that declares no approval
/// reads no registry" is a measurement rather than a claim.
type private CountingRegistry(inner: ITemplateApprovalRegistry) =
    let mutable reads = 0
    member _.Reads = reads

    interface ITemplateApprovalRegistry with
        member _.Issue(request) = inner.Issue request
        member _.Accept(record) = inner.Accept record

        member _.Records(templateId) =
            reads <- reads + 1
            inner.Records templateId

// ─── Real key material ───────────────────────────────────────────────
//
// Genuine P-256 keys through the shipped signer, on exactly Phase 480's
// argument: the claim under test is that an authorisation is a signature
// over the authorisation bytes, and a stub signer returning "ok" would
// let every case here pass against a pipeline that checked nothing
// cryptographic at all.

type private InMemorySecretStore() =
    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private secretsFor (peerIds: string list) : ISecretStore =
    let secrets = InMemorySecretStore() :> ISecretStore

    for peerId in peerIds do
        use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)

        secrets.SetSecret("_platform", $"peers/{peerId}/signing-private-key", ec.ExportPkcs8PrivateKeyPem())
        |> Async.RunSynchronously
        |> ignore

        secrets.SetSecret("_platform", $"peers/{peerId}/signing-public-key", ec.ExportSubjectPublicKeyInfoPem())
        |> Async.RunSynchronously
        |> ignore

    secrets.SetSecret("_platform", "activation/token-key", tokenKey)
    |> Async.RunSynchronously
    |> ignore

    secrets

let private freshRegistry (secrets: ISecretStore) : ITemplateApprovalRegistry =
    let signer = PeerKeyTemplateApprovalSigner(secrets) :> ITemplateApprovalSigner

    BlobTemplateApprovalRegistry(InMemoryBlobStorage() :> IBlobStorage, signer) :> ITemplateApprovalRegistry

let private issue (registry: ITemplateApprovalRegistry) acting counterparty action template =
    let request: TemplateApprovalRequest = {
        Template = template
        ActingPeerId = acting
        CounterpartyPeerId = counterparty
        Action = action
        NotBefore = None
        ExpiresAt = None
    }

    match registry.Issue request |> Async.RunSynchronously with
    | Ok record -> record
    | Error e -> failtestf "Issuing an approval record must succeed in a fixture, got %A" e

/// Both parties approve the template derived from `auth`: this
/// deployment issues its own record, and the counterparty's is issued
/// then ACCEPTED, exactly as it would arrive over the peer channel.
let private approve (registry: ITemplateApprovalRegistry) (auth: ActivationAuthorisation) =
    let template = ActivationCanonical.template auth
    issue registry seller buyer TemplateApproved template |> ignore
    let counterpartyRecord = issue registry buyer seller TemplateApproved template

    match registry.Accept counterpartyRecord |> Async.RunSynchronously with
    | Ok() -> ()
    | Error e -> failtestf "Accepting a well-signed counterparty record must succeed, got %A" e

let private revoke (registry: ITemplateApprovalRegistry) (auth: ActivationAuthorisation) (by: string) =
    let template = ActivationCanonical.template auth
    let other = if by = seller then buyer else seller
    let record = issue registry by other TemplateRevoked template

    if by <> seller then
        match registry.Accept record |> Async.RunSynchronously with
        | Ok() -> ()
        | Error e -> failtestf "Accepting a counterparty revocation must succeed, got %A" e

let private approvalCheck (registry: ITemplateApprovalRegistry) =
    TemplateApprovalGate.check (TemplateApprovalPolicy.forRegistry registry) seller

// ─── The composed pipeline under test ────────────────────────────────

type private DecisionSink() =
    let rows = ResizeArray<PeerCleanRoomDecisionPayload>()
    member _.Rows = List.ofSeq rows

    member _.Sink: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun payload -> async { rows.Add payload }

let private baseDeps (matcher: IActivationMatcher) =
    ActivationDeps.create (CleanRoomBroker.create ()) CohortResolver.membersOnly matcher

let private request (auth: ActivationAuthorisation) (destination: IActivationDestination) shape = {
    Authorisation = auth
    Destination = destination
    Shape = shape
    Bucket = None
    Context = None
}

let private run deps req =
    CohortActivation.activate deps req |> Async.RunSynchronously

// ─── 1. The mutation probe ───────────────────────────────────────────

let canonicalTests =
    testList "Phase 490 — an authorisation binds to the exact cohort, purpose and destination" [

        test "editing the cohort's member set changes the authorisation version" {
            let before = ActivationCanonical.version authorisationA
            let after = ActivationCanonical.version (authFor cohortCPrime purposeA destinationD)

            Expect.notEqual
                before
                after
                "the version IS the content hash of the whole authorisation, so adding one member must produce a different version — this is the mechanism the phase rests on"

            Expect.stringStarts before "sha256:" "the digest algorithm is named in the value"
        }

        test "every field of the cohort, the purpose and the destination is covered by the version" {
            let baseline = ActivationCanonical.version authorisationA

            let mutations = [
                "cohort id",
                authFor
                    {
                        cohortC with
                            CohortId = "high-value-2"
                    }
                    purposeA
                    destinationD
                "cohort members", authFor cohortCPrime purposeA destinationD
                "cohort definition kind",
                authFor
                    {
                        cohortC with
                            Definition = CohortQuery "value >= 500"
                    }
                    purposeA
                    destinationD
                "cohort min size",
                authFor
                    {
                        cohortC with
                            Constraints = {
                                cohortC.Constraints with
                                    MinCohortSize = 11
                            }
                    }
                    purposeA
                    destinationD
                "cohort predicate value",
                authFor
                    {
                        cohortC with
                            Constraints = {
                                cohortC.Constraints with
                                    Predicates = [
                                        { Name = "recency"; Value = "P60D" }
                                        { Name = "value"; Value = "500" }
                                    ]
                            }
                    }
                    purposeA
                    destinationD
                "purpose id", authFor cohortC purposeB destinationD
                "purpose prose",
                authFor
                    cohortC
                    {
                        purposeA with
                            Description = "Something else entirely."
                    }
                    destinationD
                "destination id",
                authFor cohortC purposeA {
                    destinationD with
                        DestinationId = "other-endpoint"
                }
                "destination counterparty",
                authFor cohortC purposeA {
                    destinationD with
                        CounterpartyPeerId = "buyer-rival"
                }
                "destination shapes",
                authFor cohortC purposeA {
                    destinationD with
                        PermittedShapes = Set.ofList [ ReleaseCount ]
                }
                "destination floor k",
                authFor cohortC purposeA {
                    destinationD with
                        Floor = { floor with MinCohortSize = 50 }
                }
                "destination suppression",
                authFor cohortC purposeA {
                    destinationD with
                        Floor = { floor with SuppressionThreshold = 6 }
                }
                "destination permitted shapes",
                authFor cohortC purposeA {
                    destinationD with
                        Floor = {
                            floor with
                                PermittedShapes = Set.ofList [ Count ]
                        }
                }
            ]

            for name, mutated in mutations do
                Expect.notEqual
                    (ActivationCanonical.version mutated)
                    baseline
                    $"a change to {name} must change the authorisation version, else an approval of one activation silently approves another"
        }

        test "member order and predicate order do not change the version" {
            let reordered = {
                cohortC with
                    Definition = CohortMembers(List.rev cohortIds)
                    Constraints = {
                        cohortC.Constraints with
                            Predicates = List.rev cohortC.Constraints.Predicates
                    }
            }

            Expect.equal
                (ActivationCanonical.version (authFor reordered purposeA destinationD))
                (ActivationCanonical.version authorisationA)
                "sets are encoded count-first in ordinal order, so an authorisation's hash cannot depend on the order an author wrote it in"
        }

        test "a delimiter in a field cannot forge a different authorisation" {
            let injected = {
                cohortC with
                    CohortId = "high\n5:value"
            }

            Expect.notEqual
                (ActivationCanonical.version (authFor injected purposeA destinationD))
                (ActivationCanonical.version authorisationA)
                "the encoding is length-prefixed, so a value carrying the delimiter cannot shift a field boundary"
        }

        test "the derived template's Phase 480 version moves with the authorisation" {
            let versionOf auth =
                TemplateCanonical.version (ActivationCanonical.template auth)

            Expect.notEqual
                (versionOf authorisationA)
                (versionOf (authFor cohortCPrime purposeA destinationD))
                "the derived template's id carries the authorisation digest, which is what makes Phase 480's existing content-hash approval bind to a cohort it knows nothing about"
        }

        test "the derived template carries the destination's shapes and the tighter of the two floors" {
            let template =
                ActivationCanonical.template (
                    authFor
                        {
                            cohortC with
                                Constraints = {
                                    cohortC.Constraints with
                                        MinCohortSize = 500
                                }
                        }
                        purposeA
                        destinationD
                )

            Expect.equal
                template.AllowedMethods
                (Set.ofList [ "ReleaseCount"; "ReleaseTokens" ])
                "the gate's surface invariant IS the destination's approved shape set"

            Expect.equal
                template.Floor.MinCohortSize
                500
                "PrivacyGate.compose can only tighten — a cohort declaring 500 into a destination whose floor is 10 activates at 500"

            Expect.stringStarts
                template.TemplateId
                ActivationCanonical.templateIdPrefix
                "the derived id is namespaced beneath _platform so an author cannot collide with it"
        }

        test "an approval issued for cohort C does not authorise cohort C-prime" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            // The control: the approved cohort activates.
            match run deps (request authorisationA destination ReleaseCount) with
            | Ok(ActivatedCount 24) -> ()
            | other -> failtestf "The approved cohort must activate, got %A" other

            // The probe: one extra member, nothing else changed.
            match run deps (request (authFor cohortCPrime purposeA destinationD) destination ReleaseCount) with
            | Error(ActivationUnauthorised reason) ->
                Expect.stringContains
                    reason
                    "not bilaterally approved"
                    "the edited cohort has a version nobody has approved — there is no field to re-point and no signature that would still verify"
            | other -> failtestf "An edited cohort must not activate under the original approval, got %A" other

            Expect.hasLength destination.Delivered 1 "…and nothing reached the destination for the edited cohort"
        }
    ]

// ─── 2. Unapproved activation is refused ─────────────────────────────

let authorisationTests =
    testList "Phase 490 — an activation nobody approved does not happen" [

        test "an unapproved activation is refused, and an approved one is not" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            match run deps (request authorisationA destination ReleaseCount) with
            | Error(ActivationUnauthorised _) -> ()
            | other -> failtestf "An activation with no approval on file must be refused, got %A" other

            Expect.isEmpty destination.Delivered "nothing crossed the boundary"

            // The control — the identical scenario, once both parties
            // have signed. Without this half the case above would pass
            // against a pipeline that had broken and started refusing
            // everything.
            approve registry authorisationA

            match run deps (request authorisationA destination ReleaseCount) with
            | Ok(ActivatedCount 24) -> ()
            | other -> failtestf "The bilaterally-approved activation must release, got %A" other

            Expect.hasLength destination.Delivered 1 "…and it reached the destination"
        }

        test "one party's approval is not enough" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets

            issue registry seller buyer TemplateApproved (ActivationCanonical.template authorisationA)
            |> ignore

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            match run deps (request authorisationA destination ReleaseCount) with
            | Error(ActivationUnauthorised reason) ->
                Expect.stringContains reason buyer "the refusal names the party whose approval is missing"
            | other -> failtestf "A unilateral approval must not authorise an activation, got %A" other
        }

        test "the counterparty is not contacted until the activation is authorised" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            run deps (request authorisationA destination ReleaseCount) |> ignore

            Expect.equal
                matcher.SizeCalls
                0
                "invariant 0 runs before the dispatch closure, so an unauthorised activation produces no private-set exchange at all — not an exchange whose answer is discarded"

            approve registry authorisationA
            run deps (request authorisationA destination ReleaseCount) |> ignore

            Expect.equal matcher.SizeCalls 1 "…and an authorised one does exactly one preflight"
        }

        test "a destination whose live descriptor has drifted from the approved one is refused" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(overlapIds)

            // The seam presents a WIDER shape set than the one signed.
            let drifted =
                RecordingDestination {
                    destinationD with
                        PermittedShapes = Set.ofList [ ReleaseCount; ReleaseTokens; ReleaseHistogram ]
                }

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            match run deps (request authorisationA drifted ReleaseCount) with
            | Error(ActivationUnauthorised reason) ->
                Expect.stringContains reason "differs from the authorised one" "the drift is named"
            | other -> failtestf "A drifted destination descriptor must be refused, got %A" other

            Expect.equal matcher.SizeCalls 0 "…before the counterparty is contacted or the cohort is resolved"
        }
    ]

// ─── 3. Purpose limitation ───────────────────────────────────────────

let purposeTests =
    testList "Phase 490 — an approval for one purpose does not authorise another" [

        test "purpose B is refused under an approval issued for purpose A" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            // Control: the approved purpose activates.
            match run deps (request authorisationA destination ReleaseCount) with
            | Ok(ActivatedCount 24) -> ()
            | other -> failtestf "The approved purpose must activate, got %A" other

            // Probe: byte-identical cohort and destination, different
            // agreed use.
            match run deps (request (authFor cohortC purposeB destinationD) destination ReleaseCount) with
            | Error(ActivationUnauthorised _) -> ()
            | other -> failtestf "A second purpose must not ride on the first purpose's approval, got %A" other

            Expect.hasLength destination.Delivered 1 "only the approved purpose reached the destination"
        }

        test "approving purpose B in turn does not retroactively widen purpose A" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            let authB = authFor cohortC purposeB destinationD
            approve registry authB

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            match run deps (request authB destination ReleaseCount) with
            | Ok(ActivatedCount 24) -> ()
            | other -> failtestf "The approved purpose must activate, got %A" other

            match run deps (request authorisationA destination ReleaseCount) with
            | Error(ActivationUnauthorised _) -> ()
            | other -> failtestf "Purpose limitation is symmetric — A must not ride on B either, got %A" other
        }

        test "two purposes over the same member produce unlinkable tokens" {
            let minter =
                HmacActivationTokenMinter(secretsFor [ seller; buyer ]) :> IActivationTokenMinter

            let tokenUnder auth =
                match minter.Mint(ActivationCanonical.id auth, "member-00") |> Async.RunSynchronously with
                | Ok token -> token
                | Error e -> failtestf "Minting must succeed in a fixture, got %s" e

            let a = tokenUnder authorisationA
            let b = tokenUnder (authFor cohortC purposeB destinationD)

            Expect.notEqual
                a
                b
                "purpose limitation reaches into the token space — the same person activated for two purposes is two unrelated pseudonyms"

            Expect.isFalse (a.Contains "member-00") "a token is not the id it stands for"
        }
    ]

// ─── 4. Revocation ───────────────────────────────────────────────────

let revocationTests =
    testList "Phase 490 — a withdrawn authorisation stops the next activation" [

        for revoker in [ seller; buyer ] do
            test $"an activation approved then revoked by {revoker} is refused" {
                let secrets = secretsFor [ seller; buyer ]
                let registry = freshRegistry secrets
                approve registry authorisationA

                let matcher = CountingMatcher(overlapIds)
                let destination = RecordingDestination(destinationD)

                let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

                match run deps (request authorisationA destination ReleaseCount) with
                | Ok(ActivatedCount 24) -> ()
                | other -> failtestf "The approved activation must release before the revocation, got %A" other

                revoke registry authorisationA revoker

                match run deps (request authorisationA destination ReleaseCount) with
                | Error(ActivationUnauthorised reason) ->
                    Expect.stringContains reason "revoked" "the audit-facing reason names the withdrawal"
                    Expect.stringContains reason revoker "…and who withdrew it"
                | other -> failtestf "A revoked authorisation must not activate, got %A" other

                Expect.hasLength destination.Delivered 1 "only the pre-revocation activation reached the destination"
            }
    ]

// ─── 5. The enforced pipeline ────────────────────────────────────────

let pipelineTests =
    testList "Phase 490 — the release pipeline is the clean-room gate's, not a second one" [

        test "a sub-floor cohort is withheld as a typed refusal, never partially released" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(cohortIds |> List.take 3)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            match run deps (request authorisationA destination ReleaseCount) with
            | Error(ActivationWithheld templateId) ->
                Expect.equal
                    templateId
                    (ActivationCanonical.id authorisationA)
                    "the refusal carries the authorisation id and nothing quantitative — a caller that could read the sub-floor size back has the counting oracle the floor exists to refuse"
            | other -> failtestf "A cohort of 3 under a floor of 10 must be withheld, got %A" other

            Expect.isEmpty destination.Delivered "nothing partial crossed"
        }

        test "a sub-floor cohort causes no membership-revealing exchange" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(cohortIds |> List.take 3)
            let destination = RecordingDestination(destinationD)

            let deps =
                baseDeps matcher
                |> ActivationDeps.withApproval (approvalCheck registry)
                |> ActivationDeps.withTokenMinter (HmacActivationTokenMinter(secrets))

            match run deps (request authorisationA destination ReleaseTokens) with
            | Error(ActivationWithheld _) -> ()
            | other -> failtestf "A sub-floor token activation must be withheld, got %A" other

            Expect.equal matcher.SizeCalls 1 "the CardinalityOnly preflight ran"

            Expect.equal
                matcher.MemberCalls
                0
                "…and the Members run did NOT — withholding a membership answer after computing it would be the weaker statement"
        }

        test "an above-floor token activation does perform the membership run" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps =
                baseDeps matcher
                |> ActivationDeps.withApproval (approvalCheck registry)
                |> ActivationDeps.withTokenMinter (HmacActivationTokenMinter(secrets))

            match run deps (request authorisationA destination ReleaseTokens) with
            | Ok(ActivatedTokens tokens) -> Expect.hasLength tokens 24 "one token per matched member"
            | other -> failtestf "The above-floor token activation must release, got %A" other

            Expect.equal matcher.SizeCalls 1 "the preflight still runs first"
            Expect.equal matcher.MemberCalls 1 "…and the membership run happens only after it cleared"
        }

        test "a shape off the destination's approved surface is withheld by the gate" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets

            let countOnly = {
                destinationD with
                    PermittedShapes = Set.ofList [ ReleaseCount ]
            }

            let auth = authFor cohortC purposeA countOnly
            approve registry auth

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(countOnly)

            let deps =
                baseDeps matcher
                |> ActivationDeps.withApproval (approvalCheck registry)
                |> ActivationDeps.withTokenMinter (HmacActivationTokenMinter(secrets))

            // Control: the approved shape releases.
            match run deps (request auth destination ReleaseCount) with
            | Ok(ActivatedCount 24) -> ()
            | other -> failtestf "The approved shape must release, got %A" other

            match run deps (request auth destination ReleaseTokens) with
            | Error(ActivationWithheld _) -> ()
            | other -> failtestf "A shape the destination was never approved for must be withheld, got %A" other

            Expect.hasLength destination.Delivered 1 "only the approved shape crossed"
        }

        test "a cohort the resolver cannot evaluate refuses without contacting the counterparty" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets

            let auth =
                authFor
                    {
                        cohortC with
                            Definition = CohortQuery "value >= 500"
                    }
                    purposeA
                    destinationD

            approve registry auth

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            match run deps (request auth destination ReleaseCount) with
            | Error(ActivationCohortUnresolved reason) ->
                Expect.stringContains reason "query language" "the refusal names what the deployment must compose"
            | other -> failtestf "An unresolvable cohort definition must refuse, got %A" other

            Expect.equal matcher.SizeCalls 0 "no exchange happened"
        }

        test "the pipeline records an audit row naming who activated what, when, and under which authorisation" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let sink = DecisionSink()
            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps =
                baseDeps matcher
                |> ActivationDeps.withApproval (approvalCheck registry)
                |> ActivationDeps.withAudit sink.Sink

            run deps (request authorisationA destination ReleaseCount) |> ignore

            // Two released rows: the gate's own (which carries the
            // suppression trail and an empty reason on a clean release)
            // and the pipeline's, which names the activation. They join
            // on the authorisation id and the root request id, which is
            // how an auditor reconstructs one activation from both.
            let released = sink.Rows |> List.filter _.Released
            Expect.hasLength released 2 "the gate records its decision and the pipeline records the act"

            Expect.equal
                (released |> List.map _.RootRequestId |> List.distinct |> List.length)
                1
                "…and the two rows join on one correlation id"

            match released |> List.filter (fun r -> r.Reason <> "") with
            | [ row ] ->
                Expect.equal row.ContractId destinationD.DestinationId "what it was activated to"
                Expect.equal row.CallerPeerId buyer "…and the counterparty it went to"
                Expect.equal row.TemplateId (ActivationCanonical.id authorisationA) "…under which authorisation"
                Expect.equal row.MethodName "ReleaseCount" "…in which shape"
                Expect.stringContains row.Reason purposeA.PurposeId "…for which purpose"
                Expect.stringContains row.Reason cohortC.CohortId "…over which cohort"
            | other -> failtestf "Expected exactly one released row, got %i" (List.length other)
        }

        test "a destination that refuses the delivery is a typed refusal, not a silent success" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)
            destination.Refuse <- Some "the endpoint rejected the audience"

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            match run deps (request authorisationA destination ReleaseCount) with
            | Error(ActivationDeliveryFailed reason) ->
                Expect.stringContains reason "rejected" "the downstream reason is preserved"
            | other -> failtestf "A refused delivery must surface, got %A" other
        }
    ]

// ─── 6. The egress projection (invariant 5) ──────────────────────────

let egressTests =
    testList "Phase 490 — invariant 5: only the approved shape leaves, and never a list of ids" [

        test "ActivationRelease has no case that could carry the cohort's ids" {
            let cases =
                Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<ActivationRelease>)
                |> Array.map _.Name
                |> Set.ofArray

            Expect.equal
                cases
                (Set.ofList [ "ActivatedCount"; "ActivatedTokens"; "ActivatedHistogram" ])
                "the structural half of the phase is that a raw-list release is unrepresentable — there is no value to construct, so there is no branch to forget"

            Expect.equal
                (Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<ActivationShape>)
                 |> Array.length)
                3
                "…and the shape vocabulary a destination may be approved for matches it exactly"
        }

        test "none of the cohort's own ids appears in what the destination received" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps =
                baseDeps matcher
                |> ActivationDeps.withApproval (approvalCheck registry)
                |> ActivationDeps.withTokenMinter (HmacActivationTokenMinter(secrets))

            run deps (request authorisationA destination ReleaseCount) |> ignore
            run deps (request authorisationA destination ReleaseTokens) |> ignore

            Expect.hasLength destination.Delivered 2 "both approved shapes released"

            for _, release in destination.Delivered do
                let wire = JsonRpc.serialize release

                for memberId in cohortIds do
                    Expect.isFalse
                        (wire.Contains memberId)
                        $"'{memberId}' must not appear in a release — the underlying id list never crosses the boundary"
        }

        test "a token release under calibrated noise is refused rather than republishing the true size" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)
            let policy = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m)

            let noised =
                baseDeps matcher
                |> ActivationDeps.withApproval (approvalCheck registry)
                |> ActivationDeps.withTokenMinter (HmacActivationTokenMinter(secrets))
                |> ActivationDeps.withNoise (FixedNoise 3L) policy

            match run noised (request authorisationA destination ReleaseTokens) with
            | Error(ActivationEgressRefused reason) ->
                Expect.stringContains
                    reason
                    "disclose the true size"
                    "a per-member token list's LENGTH is the true cohort size, so publishing it would republish the number the gate had just adjusted"
            | other -> failtestf "A noised token release must be refused, got %A" other

            // Two controls. The count shape under the SAME noise
            // releases (so the refusal is about the projection, not a
            // broken noise path), and the token shape WITHOUT noise
            // releases (so it is about the noise, not a broken token
            // path).
            match run noised (request authorisationA destination ReleaseCount) with
            | Ok(ActivatedCount 27) -> ()
            | other -> failtestf "A noised count release must still release, got %A" other

            let unnoised =
                baseDeps matcher
                |> ActivationDeps.withApproval (approvalCheck registry)
                |> ActivationDeps.withTokenMinter (HmacActivationTokenMinter(secrets))

            match run unnoised (request authorisationA destination ReleaseTokens) with
            | Ok(ActivatedTokens tokens) -> Expect.hasLength tokens 24 "…and the un-noised token release does"
            | other -> failtestf "The un-noised token release must release, got %A" other
        }

        test "a token release with no composed minter refuses rather than falling back to ids" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            match run deps (request authorisationA destination ReleaseTokens) with
            | Error(ActivationEgressRefused reason) ->
                Expect.stringContains reason "IActivationTokenMinter" "the missing composition is named"
            | other -> failtestf "A token release with no minter must refuse, got %A" other

            Expect.isEmpty destination.Delivered "and nothing crossed in its place"
        }

        test "a histogram release buckets by the caller's own labels and suppresses under the floor" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets

            let histogramDestination = {
                destinationD with
                    PermittedShapes = Set.ofList [ ReleaseHistogram ]
            }

            let auth = authFor cohortC purposeA histogramDestination
            approve registry auth

            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(histogramDestination)

            let deps = baseDeps matcher |> ActivationDeps.withApproval (approvalCheck registry)

            // 24 matched members: 20 land in "bulk", 4 in "rare" — and
            // "rare" is below the suppression threshold of 5.
            let bucket (memberId: string) =
                if memberId >= "member-20" then "rare" else "bulk"

            let req = {
                request auth destination ReleaseHistogram with
                    Bucket = Some bucket
            }

            match run deps req with
            | Ok(ActivatedHistogram buckets) ->
                Expect.equal
                    buckets
                    [ { Label = "bulk"; Count = 20 } ]
                    "the sub-threshold bucket was suppressed by the gate, not by this file"
            | other -> failtestf "The histogram release must release, got %A" other
        }
    ]

// ─── 7. Tokens ───────────────────────────────────────────────────────

let tokenTests =
    testList "Phase 490 — activation tokens" [

        test "a token verifies for its own member and for no other" {
            let minter =
                HmacActivationTokenMinter(secretsFor [ seller ]) :> IActivationTokenMinter

            let authorisationId = ActivationCanonical.id authorisationA

            let token =
                match minter.Mint(authorisationId, "member-00") |> Async.RunSynchronously with
                | Ok t -> t
                | Error e -> failtestf "Minting must succeed in a fixture, got %s" e

            match minter.Verify(authorisationId, "member-00", token) |> Async.RunSynchronously with
            | Ok true -> ()
            | other -> failtestf "The token must verify for its own member, got %A" other

            match minter.Verify(authorisationId, "member-01", token) |> Async.RunSynchronously with
            | Ok false -> ()
            | other -> failtestf "…and for no other, got %A" other
        }

        test "minting is deterministic, so a re-delivery is idempotent" {
            let secrets = secretsFor [ seller ]

            let mint () =
                (HmacActivationTokenMinter(secrets) :> IActivationTokenMinter)
                    .Mint(ActivationCanonical.id authorisationA, "member-00")
                |> Async.RunSynchronously

            Expect.equal
                (mint ())
                (mint ())
                "the same activation twice is the same audience, not a second differently-named one"
        }

        test "a blank or short token key is refused rather than minting a forgeable token" {
            let secrets = InMemorySecretStore() :> ISecretStore

            secrets.SetSecret("_platform", "activation/token-key", "short")
            |> Async.RunSynchronously
            |> ignore

            let minter = HmacActivationTokenMinter(secrets) :> IActivationTokenMinter

            match
                minter.Mint(ActivationCanonical.id authorisationA, "member-00")
                |> Async.RunSynchronously
            with
            | Error reason -> Expect.stringContains reason "forgeable" "the refusal says why"
            | Ok _ -> failtest "An HMAC key below the SHA-256 block size must not mint a token"
        }

        test "no key at all is a refusal that names the expected secret" {
            let minter =
                HmacActivationTokenMinter(InMemorySecretStore()) :> IActivationTokenMinter

            match
                minter.Mint(ActivationCanonical.id authorisationA, "member-00")
                |> Async.RunSynchronously
            with
            | Error reason ->
                Expect.stringContains reason "activation/token-key" "the operator is told what to register"
            | Ok _ -> failtest "Minting without key material must refuse"
        }
    ]

// ─── 8. Nothing else moved (GP 11 / GP 13) ───────────────────────────

let compositionTests =
    testList "Phase 490 — a composition that declares no governance reads none" [

        test "ActivationDeps.create composes no approval, no budget, no noise and no minter" {
            let deps = baseDeps (CountingMatcher(overlapIds))

            Expect.isNone deps.Approval "invariant 0 is opt-in"
            Expect.isNone deps.Budget "invariant 0.5 is opt-in"
            Expect.isNone deps.Noise "invariant 4 is opt-in"
            Expect.isNone deps.Tokens "the token minter is opt-in"
        }

        test "an activation with no composed approval reads no approval registry" {
            let secrets = secretsFor [ seller; buyer ]
            let counting = CountingRegistry(freshRegistry secrets)
            let matcher = CountingMatcher(overlapIds)
            let destination = RecordingDestination(destinationD)

            // Ungoverned: the pre-490 posture of a deployment activating
            // to itself. It releases, and it never touches the store.
            match run (baseDeps matcher) (request authorisationA destination ReleaseCount) with
            | Ok(ActivatedCount 24) -> ()
            | other -> failtestf "An ungoverned activation releases, got %A" other

            Expect.equal counting.Reads 0 "no registry read, no blob access, no allocation (GP 13)"

            // The same activation WITH the check composed reads it —
            // which is what proves the counter is wired at all.
            let governed =
                baseDeps matcher
                |> ActivationDeps.withApproval (approvalCheck (counting :> ITemplateApprovalRegistry))

            run governed (request authorisationA destination ReleaseCount) |> ignore
            Expect.isGreaterThan counting.Reads 0 "…and the governed one does"
        }
    ]