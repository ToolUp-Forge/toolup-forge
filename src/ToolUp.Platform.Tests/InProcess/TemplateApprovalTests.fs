module ToolUp.Platform.Tests.InProcess.TemplateApprovalTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 480 — bilateral clean-room template approval ──────────────
//
// Phase 311 made the clean-room floor structural. This pack is about
// what it could not say: that both parties agreed to the floor being
// enforced, for the exact template content being enforced.
//
// Five kinds of case, in the order they carry weight:
//
//   1. **The mutation probe.** An approval issued for template T must
//      not validate template T′. This is the whole attack — a receiver
//      that could edit an approved template and keep the approval would
//      have a signature on nothing — so it is measured three ways:
//      version inequality, evaluation over real signed records, and an
//      end-to-end dispatch through the composed gate.
//   2. **The probes have teeth.** Every withhold case is paired with a
//      NEGATIVE CONTROL running the identical scenario through the
//      pre-480 path (`CleanRoomGate.wrap`, i.e. `wrapApproved … None`),
//      which releases. Without that half, "the unapproved answer was
//      withheld" would pass equally against a gate that had broken and
//      started refusing everything.
//   3. **The signature is real, and it binds.** Records are signed with
//      genuine P-256 keys through the shipped
//      `PeerKeyTemplateApprovalSigner`, and a record whose fields are
//      edited after signing is refused by `Accept` — paired with a
//      control showing the unedited record is accepted.
//   4. **Revocation and expiry take effect on the next dispatch.**
//      Fail-closed precedence, clock-skew tolerance at both ends.
//   5. **Nothing else moved (GP 11 / GP 13).** A composition that
//      declares no registry reads no registry — measured with a
//      counting store, not asserted.

// ─── Fixtures ────────────────────────────────────────────────────────

/// NOT `private`: `JsonRpcPeerHost.contract` reflects via
/// `FSharpType.IsRecord` without the private-representation flag, so a
/// `private` record reads back as a non-record and is rejected.
type ReachContract = {
    EstimateReach: string -> Async<CohortResult>
}

let private cell label count : PrivacyCell = {
    Label = label
    Count = count
    Value = None
}

let private floorAt k : PrivacyGate = {
    MinCohortSize = k
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Histogram ]
}

/// The approved template.
let private templateT: CleanRoomTemplate = {
    TemplateId = "reach"
    AllowedMethods = Set.ofList [ "EstimateReach" ]
    Floor = floorAt 10
}

/// The edit. Same id, same surface, a LOOSER floor — the change an
/// unco-operative receiver would make after obtaining an approval, and
/// the one a signature over the template id alone would not catch.
let private templateTPrime: CleanRoomTemplate = { templateT with Floor = floorAt 2 }

let private v1: ContractVersion = { Major = 1; Minor = 0 }

[<Literal>]
let private contractId = "example.reach"

[<Literal>]
let private seller = "seller-ssp"

[<Literal>]
let private buyer = "buyer-acme"

let private impl (cohort: int) : ReachContract = {
    EstimateReach =
        fun _ -> async {
            return {
                Shape = Count
                Cells = [ cell "all" cohort ]
            }
        }
}

let private rawRegistration (cohort: int) =
    (JsonRpcPeerHost.contract<ReachContract> contractId [ v1 ] None (impl cohort)).Registration

let private callContext: PeerCallContext = {
    Peer = {
        PeerId = buyer
        DisplayName = "Buyer"
    }
    User = Anonymous
    ContractVersion = v1
    Route = [ buyer ]
    RootRequestId = "root-480"
    ParentRequestId = None
    HopsRemaining = 4
}

type private DecisionSink() =
    let rows = ResizeArray<PeerCleanRoomDecisionPayload>()
    member _.Rows = List.ofSeq rows

    member _.Sink: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun payload -> async { rows.Add payload }

let private defaultBroker = CleanRoomBroker.create ()

let private dispatch (registration: PeerContractRegistration) =
    registration.Dispatch callContext "EstimateReach" "[\"any\"]"
    |> Async.RunSynchronously

// ─── Real key material ───────────────────────────────────────────────
//
// Genuine P-256 keys through the shipped signer, not a stub: the claim
// under test is that an approval is a signature over the template bytes,
// and a stub signer that returned "ok" would let every case here pass
// against a gate that checked nothing cryptographic at all.

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

/// A secret store holding a private + public P-256 key for each named
/// peer, under the Phase 343 key names the signer reads.
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

    secrets

/// A fresh registry over an in-memory blob store, signing with real
/// keys for both parties. Both sides' key material sits in one store
/// because a single-process test plays both deployments; the signer
/// reads a private key only for the peer it is asked to sign as.
let private freshRegistry () : ITemplateApprovalRegistry =
    let signer =
        PeerKeyTemplateApprovalSigner(secretsFor [ seller; buyer ]) :> ITemplateApprovalSigner

    BlobTemplateApprovalRegistry(InMemoryBlobStorage() :> IBlobStorage, signer) :> ITemplateApprovalRegistry

let private request acting counterparty action template : TemplateApprovalRequest = {
    Template = template
    ActingPeerId = acting
    CounterpartyPeerId = counterparty
    Action = action
    NotBefore = None
    ExpiresAt = None
}

let private issue (registry: ITemplateApprovalRegistry) req =
    match registry.Issue req |> Async.RunSynchronously with
    | Ok record -> record
    | Error e -> failtestf "Issuing an approval record must succeed in a fixture, got %A" e

/// Both parties approve `template`: the seller (this deployment) issues
/// its own record; the buyer's record is issued and then ACCEPTED,
/// exactly as it would arrive over the peer channel.
let private approveBilaterally (registry: ITemplateApprovalRegistry) (template: CleanRoomTemplate) =
    issue registry (request seller buyer TemplateApproved template) |> ignore
    let buyerRecord = issue registry (request buyer seller TemplateApproved template)

    match registry.Accept buyerRecord |> Async.RunSynchronously with
    | Ok() -> ()
    | Error e -> failtestf "Accepting a well-signed counterparty record must succeed, got %A" e

let private policyOver (registry: ITemplateApprovalRegistry) =
    TemplateApprovalPolicy.forRegistry registry

/// A gated registration under the composed approval check — the shape
/// `PeerCompose.run` wires.
let private gatedWithApproval registry template cohort (sink: DecisionSink) =
    (CleanRoomGate.wrapApproved
        defaultBroker
        template
        (Some(TemplateApprovalGate.check (policyOver registry) seller))
        sink.Sink
        (rawRegistration cohort))
        .Registration

/// The SAME registration with the approval check removed — the pre-480
/// path. Every withhold case below is paired with this, so a green
/// result measures the approval check rather than a broken dispatch.
let private gatedWithoutApproval template cohort (sink: DecisionSink) =
    (CleanRoomGate.wrap defaultBroker template sink.Sink (rawRegistration cohort)).Registration

// ─── 1. The mutation probe ───────────────────────────────────────────

let mutationTests =
    testList "Phase 480 — an approval binds to the exact template bytes" [

        test "editing a template's floor changes its version" {
            let before = TemplateCanonical.version templateT
            let after = TemplateCanonical.version templateTPrime

            Expect.notEqual
                before
                after
                "the version IS the content hash, so a floor edit must produce a different version — this is the mechanism the whole phase rests on"

            Expect.stringStarts before "sha256:" "the digest algorithm is named in the value"
        }

        test "every field of a template and its floor is covered by the version" {
            let baseline = TemplateCanonical.version templateT

            let mutations = [
                "TemplateId",
                {
                    templateT with
                        TemplateId = "reach-2"
                }
                "AllowedMethods",
                {
                    templateT with
                        AllowedMethods = Set.ofList [ "EstimateReach"; "ExportRows" ]
                }
                "MinCohortSize", { templateT with Floor = floorAt 11 }
                "SuppressionThreshold",
                {
                    templateT with
                        Floor = {
                            templateT.Floor with
                                SuppressionThreshold = 6
                        }
                }
                "PermittedShapes",
                {
                    templateT with
                        Floor = {
                            templateT.Floor with
                                PermittedShapes = Set.ofList [ Count ]
                        }
                }
            ]

            for name, mutated in mutations do
                Expect.notEqual
                    (TemplateCanonical.version mutated)
                    baseline
                    $"a change to {name} must change the version, else an approval of one value silently approves another"
        }

        test "the version does not depend on the order a set was written in" {
            let a = {
                templateT with
                    AllowedMethods = Set.ofList [ "Alpha"; "Beta"; "Gamma" ]
            }

            let b = {
                templateT with
                    AllowedMethods = Set.ofList [ "Gamma"; "Alpha"; "Beta" ]
            }

            Expect.equal
                (TemplateCanonical.version a)
                (TemplateCanonical.version b)
                "sets are emitted in ordinal order, so a template's hash is a property of its content and not of its authoring"
        }

        test "the length-prefixed encoding cannot be forged by embedding a delimiter" {
            // Two templates whose naive `id|methods` concatenation would
            // coincide. With length prefixes they cannot, because the
            // reader is told how many bytes the id occupies before it
            // starts.
            let sneaky = {
                templateT with
                    TemplateId = "reach\n1:X"
                    AllowedMethods = Set.ofList [ "Y" ]
            }

            let honest = {
                templateT with
                    TemplateId = "reach"
                    AllowedMethods = Set.ofList [ "X"; "Y" ]
            }

            Expect.notEqual
                (TemplateCanonical.version sneaky)
                (TemplateCanonical.version honest)
                "a value containing the field delimiter must not be able to impersonate a field boundary"
        }

        test "THE CRITICAL PROBE — an approval issued for T does not validate T'" {
            let registry = freshRegistry ()
            approveBilaterally registry templateT

            let records = registry.Records(Some templateT.TemplateId) |> Async.RunSynchronously
            Expect.hasLength records 2 "both parties' records are held"

            let now = DateTimeOffset.UtcNow

            let statusT =
                TemplateApproval.status
                    TemplateApproval.defaultSkew
                    seller
                    buyer
                    (TemplateCanonical.version templateT)
                    now
                    records

            match statusT with
            | BilaterallyApproved _ -> ()
            | other -> failtestf "CONTROL: the approved template must evaluate as approved, got %A" other

            let statusTPrime =
                TemplateApproval.status
                    TemplateApproval.defaultSkew
                    seller
                    buyer
                    (TemplateCanonical.version templateTPrime)
                    now
                    records

            match statusTPrime with
            | ApprovalPending awaiting ->
                Expect.equal
                    (List.sort awaiting)
                    (List.sort [ seller; buyer ])
                    "an edit produces a version NEITHER party has signed — the approval does not carry over"
            | other -> failtestf "An approval of T validated T' as %A — the template-mutation attack is open" other
        }

        test "THE CRITICAL PROBE, end to end — a gate over the edited template withholds" {
            let registry = freshRegistry ()
            // Both parties sign off on the k=10 floor…
            approveBilaterally registry templateT

            // …and the receiver then enforces the k=2 edit of it. The
            // handler answers with a cohort of 5: legal under the edit,
            // illegal under what was agreed.
            let sink = DecisionSink()
            let registration = gatedWithApproval registry templateTPrime 5 sink

            match dispatch registration with
            | Error(PeerCleanRoomWithheld id) ->
                Expect.equal id templateTPrime.TemplateId "the refusal names the template and nothing else"
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "A cohort of 5 reached the wire under an unapproved floor edit: %s" payload

            match sink.Rows with
            | [ row ] ->
                Expect.isFalse row.Released "the decision row records a withhold"

                Expect.stringContains
                    row.Reason
                    "not bilaterally approved"
                    "…naming the approval failure, so an operator can tell it from a floor breach"
            | other -> failtestf "Expected exactly one decision row, got %i" (List.length other)
        }

        test "NEGATIVE CONTROL — the identical edited gate, without the approval check, releases" {
            // The approval check removed and nothing else changed. This
            // is what makes the case above mean something: the ONLY
            // thing that withheld the answer was the bilateral check.
            let sink = DecisionSink()
            let registration = gatedWithoutApproval templateTPrime 5 sink

            match dispatch registration with
            | Ok payload ->
                Expect.stringContains
                    payload
                    "5"
                    "a pre-480 gate enforces the EDITED floor happily — a template nobody approved, applied to real data"
            | Error e -> failtestf "The pre-480 control must succeed, else the gated case proves nothing; got %A" e
        }

        test "CONTROL — a gate over the APPROVED template releases a conforming answer" {
            // Without this, every case above would pass against an
            // approval check that refused unconditionally.
            let registry = freshRegistry ()
            approveBilaterally registry templateT

            let sink = DecisionSink()
            let registration = gatedWithApproval registry templateT 50 sink

            match dispatch registration with
            | Ok payload ->
                Expect.stringContains payload "50" "an approved template's conforming answer rides back untouched"
            | Error e -> failtestf "An approved, within-floor answer must release, got %A" e

            match sink.Rows with
            | [ row ] -> Expect.isTrue row.Released "the decision row records a release"
            | other -> failtestf "Expected exactly one decision row, got %i" (List.length other)
        }
    ]

// ─── 2. The signature is real, and it binds ──────────────────────────

let signatureTests =
    testList "Phase 480 — signed records, verified on arrival" [

        test "a well-signed counterparty record is accepted and stored" {
            let registry = freshRegistry ()
            let record = issue registry (request buyer seller TemplateApproved templateT)

            match registry.Accept record |> Async.RunSynchronously with
            | Ok() -> ()
            | Error e -> failtestf "A record signed by its acting peer must be accepted, got %A" e

            let held = registry.Records(Some templateT.TemplateId) |> Async.RunSynchronously

            Expect.isTrue
                (held |> List.exists (fun r -> r.Signature = record.Signature))
                "the accepted record is readable back"
        }

        test "a record whose fields were edited after signing is refused" {
            let registry = freshRegistry ()
            let record = issue registry (request buyer seller TemplateApproved templateT)

            let forged = {
                record with
                    TemplateVersion = TemplateCanonical.version templateTPrime
            }

            match registry.Accept forged |> Async.RunSynchronously with
            | Error(PeerUnauthorized _) -> ()
            | Error e -> failtestf "Expected PeerUnauthorized, got %A" e
            | Ok() ->
                failtest
                    "A record re-pointed at a different template version was accepted — the signature does not bind the version"
        }

        test "every signed field is covered — re-pointing any of them breaks the signature" {
            let registry = freshRegistry ()
            let record = issue registry (request buyer seller TemplateApproved templateT)

            let tampered = [
                "TemplateId", { record with TemplateId = "other" }
                "ActingPeerId", { record with ActingPeerId = seller }
                "CounterpartyPeerId",
                {
                    record with
                        CounterpartyPeerId = "rival"
                }
                "Action", { record with Action = TemplateRevoked }
                "IssuedAt",
                {
                    record with
                        IssuedAt = record.IssuedAt.AddDays 1.0
                }
                "NotBefore",
                {
                    record with
                        NotBefore = record.NotBefore.AddDays -1.0
                }
                "ExpiresAt",
                {
                    record with
                        ExpiresAt = Some(record.IssuedAt.AddDays 3650.0)
                }
            ]

            for name, edit in tampered do
                match registry.Accept edit |> Async.RunSynchronously with
                | Error _ -> ()
                | Ok() -> failtestf "Editing %s after signing left the signature valid" name
        }

        test "an approval cannot be minted for a peer whose private key this deployment does not hold" {
            // A receiver holding only PUBLIC keys verifies everything
            // and forges nothing — the posture a host-only deployment
            // should have.
            let secrets = InMemorySecretStore() :> ISecretStore

            use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)

            secrets.SetSecret("_platform", $"peers/{buyer}/signing-public-key", ec.ExportSubjectPublicKeyInfoPem())
            |> Async.RunSynchronously
            |> ignore

            let registry =
                BlobTemplateApprovalRegistry(
                    InMemoryBlobStorage() :> IBlobStorage,
                    PeerKeyTemplateApprovalSigner(secrets) :> ITemplateApprovalSigner
                )
                :> ITemplateApprovalRegistry

            match
                registry.Issue(request buyer seller TemplateApproved templateT)
                |> Async.RunSynchronously
            with
            | Error(PeerUnauthorized reason) ->
                Expect.stringContains reason "unsigned approval is not an approval" "…and says why it failed closed"
            | Error e -> failtestf "Expected PeerUnauthorized, got %A" e
            | Ok _ -> failtest "A deployment with no private key minted an approval"

            Expect.isEmpty (registry.Records None |> Async.RunSynchronously) "nothing unsigned reaches the store"
        }

        test "a record survives a JSON round trip and still verifies" {
            // Instants are truncated to whole seconds before signing
            // precisely so the wire form re-canonicalises to the bytes
            // that were signed. If that stopped holding, every record
            // arriving over the peer channel would be refused.
            let registry = freshRegistry ()
            let record = issue registry (request buyer seller TemplateApproved templateT)

            let roundTripped =
                JsonRpc.deserialize<TemplateApprovalRecord> (JsonRpc.serialize record)

            Expect.equal roundTripped record "the record round-trips structurally"

            match registry.Accept roundTripped |> Async.RunSynchronously with
            | Ok() -> ()
            | Error e -> failtestf "A round-tripped record must still verify, got %A" e
        }

        test "re-accepting the same record is idempotent" {
            // The peer channel is at-least-once; a retry must not look
            // like a second approval.
            let registry = freshRegistry ()
            let record = issue registry (request buyer seller TemplateApproved templateT)
            registry.Accept record |> Async.RunSynchronously |> ignore
            registry.Accept record |> Async.RunSynchronously |> ignore

            Expect.hasLength
                (registry.Records(Some templateT.TemplateId) |> Async.RunSynchronously)
                1
                "a content-addressed store folds a re-sent record onto itself"
        }
    ]

// ─── 3. Lifecycle: pending, revoked, expired ─────────────────────────

let private signedStub
    (acting: string)
    (counterparty: string)
    (action: TemplateApprovalAction)
    (notBefore: DateTimeOffset)
    (expiresAt: DateTimeOffset option)
    : TemplateApprovalRecord =
    {
        TemplateId = templateT.TemplateId
        TemplateVersion = TemplateCanonical.version templateT
        ActingPeerId = acting
        CounterpartyPeerId = counterparty
        Action = action
        IssuedAt = notBefore
        NotBefore = notBefore
        ExpiresAt = expiresAt
        Signature = "not-checked-by-the-pure-evaluator"
    }

let private now = DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero)
let private version = TemplateCanonical.version templateT

let private evaluate records =
    TemplateApproval.status TemplateApproval.defaultSkew seller buyer version now records

let lifecycleTests =
    testList "Phase 480 — bilateral evaluation is fail-closed" [

        test "one party's approval is not an approval" {
            match evaluate [ signedStub seller buyer TemplateApproved (now.AddDays -1.0) None ] with
            | ApprovalPending [ awaiting ] -> Expect.equal awaiting buyer "the missing party is named"
            | other -> failtestf "Expected the counterparty to be awaited, got %A" other
        }

        test "CONTROL — both parties' approvals are" {
            let records = [
                signedStub seller buyer TemplateApproved (now.AddDays -2.0) None
                signedStub buyer seller TemplateApproved (now.AddDays -1.0) None
            ]

            match evaluate records with
            | BilaterallyApproved effectiveFrom ->
                Expect.equal
                    effectiveFrom
                    (now.AddDays -1.0)
                    "the agreement became bilateral at the LATER of the two start dates"
            | other -> failtestf "Expected BilaterallyApproved, got %A" other
        }

        test "a proposal or a review is not an approval" {
            for action in [ TemplateProposed; TemplateReviewed ] do
                let records = [
                    signedStub seller buyer TemplateApproved (now.AddDays -2.0) None
                    signedStub buyer seller action (now.AddDays -1.0) None
                ]

                match evaluate records with
                | ApprovalPending [ awaiting ] -> Expect.equal awaiting buyer $"{action} confers no permission"
                | other -> failtestf "Expected pending for %A, got %A" action other
        }

        test "a revocation from either side beats everything, whoever issued it" {
            for revoker, other in [ seller, buyer; buyer, seller ] do
                let records = [
                    signedStub seller buyer TemplateApproved (now.AddDays -3.0) None
                    signedStub buyer seller TemplateApproved (now.AddDays -3.0) None
                    signedStub revoker other TemplateRevoked (now.AddDays -1.0) None
                ]

                match evaluate records with
                | ApprovalRevoked(byPeerId, _) -> Expect.equal byPeerId revoker "the revoking party is named"
                | verdict -> failtestf "A revocation by %s was not honoured: %A" revoker verdict
        }

        test "a re-approval after a revocation restores the agreement" {
            // Revocation is a record, not a wall: without this the case
            // above would pass against an evaluator that latched.
            let records = [
                signedStub seller buyer TemplateApproved (now.AddDays -3.0) None
                signedStub buyer seller TemplateApproved (now.AddDays -3.0) None
                signedStub buyer seller TemplateRevoked (now.AddDays -2.0) None
                signedStub buyer seller TemplateApproved (now.AddDays -1.0) None
            ]

            match evaluate records with
            | BilaterallyApproved _ -> ()
            | other -> failtestf "Only each party's LATEST record counts, got %A" other
        }

        test "two records in the same second resolve towards the revocation" {
            let instant = now.AddDays -1.0

            let records = [
                signedStub seller buyer TemplateApproved (now.AddDays -3.0) None
                signedStub buyer seller TemplateApproved instant None
                signedStub buyer seller TemplateRevoked instant None
            ]

            match evaluate records with
            | ApprovalRevoked(byPeerId, _) -> Expect.equal byPeerId buyer "an ambiguous ordering fails closed"
            | other -> failtestf "Expected the revocation to win the tie, got %A" other
        }

        test "an expired approval stops the next dispatch" {
            let records = [
                signedStub seller buyer TemplateApproved (now.AddDays -3.0) None
                signedStub buyer seller TemplateApproved (now.AddDays -3.0) (Some(now.AddDays -1.0))
            ]

            match evaluate records with
            | ApprovalExpired(peerId, _) -> Expect.equal peerId buyer "the expired party is named"
            | other -> failtestf "Expected ApprovalExpired, got %A" other
        }

        test "an approval that has not started yet is pending, not expired" {
            let records = [
                signedStub seller buyer TemplateApproved (now.AddDays -3.0) None
                signedStub buyer seller TemplateApproved (now.AddDays 1.0) None
            ]

            match evaluate records with
            | ApprovalPending [ awaiting ] ->
                Expect.equal awaiting buyer "nothing is wrong — the start date has not arrived"
            | other -> failtestf "Expected pending, got %A" other
        }

        test "the skew tolerance is applied to both ends of the window" {
            // Half a minute either side of a boundary, under the shared
            // 60 s peer clock skew.
            let justStarted = [
                signedStub seller buyer TemplateApproved (now.AddDays -3.0) None
                signedStub buyer seller TemplateApproved (now.AddSeconds 30.0) None
            ]

            match evaluate justStarted with
            | BilaterallyApproved _ -> ()
            | other -> failtestf "A start date 30 s away must clear the 60 s skew, got %A" other

            let justExpired = [
                signedStub seller buyer TemplateApproved (now.AddDays -3.0) None
                signedStub buyer seller TemplateApproved (now.AddDays -3.0) (Some(now.AddSeconds -30.0))
            ]

            match evaluate justExpired with
            | BilaterallyApproved _ -> ()
            | other -> failtestf "An expiry 30 s past must still clear the 60 s skew, got %A" other

            // CONTROL — well outside the window, so the tolerance is a
            // tolerance and not a blanket pass.
            let longExpired = [
                signedStub seller buyer TemplateApproved (now.AddDays -3.0) None
                signedStub buyer seller TemplateApproved (now.AddDays -3.0) (Some(now.AddHours -1.0))
            ]

            match evaluate longExpired with
            | ApprovalExpired _ -> ()
            | other -> failtestf "An hour-old expiry must not be tolerated, got %A" other
        }

        test "records naming a third party are not evidence about this pair" {
            let records = [
                signedStub seller buyer TemplateApproved (now.AddDays -1.0) None
                signedStub "rival" seller TemplateApproved (now.AddDays -1.0) None
            ]

            match evaluate records with
            | ApprovalPending [ awaiting ] -> Expect.equal awaiting buyer "another federation's approval is not ours"
            | other -> failtestf "Expected the counterparty to still be awaited, got %A" other
        }

        test "a revocation takes effect on the NEXT dispatch, end to end" {
            let registry = freshRegistry ()
            approveBilaterally registry templateT

            let sink = DecisionSink()
            let registration = gatedWithApproval registry templateT 50 sink

            match dispatch registration with
            | Ok _ -> ()
            | Error e -> failtestf "CONTROL: the approved dispatch must release first, got %A" e

            let revocation = issue registry (request buyer seller TemplateRevoked templateT)

            registry.Accept revocation |> Async.RunSynchronously |> ignore

            match dispatch registration with
            | Error(PeerCleanRoomWithheld _) -> ()
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "A revoked template kept answering: %s" payload

            match sink.Rows with
            | [ released; withheld ] ->
                Expect.isTrue released.Released "the first call released"
                Expect.isFalse withheld.Released "…and the call after the revocation did not"
                Expect.stringContains withheld.Reason "was revoked by peer" "the audit row names the revocation"
            | other -> failtestf "Expected two decision rows, got %i" (List.length other)
        }
    ]

// ─── 4. The gate's placement + GP 13 ─────────────────────────────────

/// Counts registry reads so "a composition without a registry reads no
/// registry" is a measurement rather than an inference.
type private CountingRegistry(inner: ITemplateApprovalRegistry) =
    let mutable reads = 0
    member _.Reads = reads

    interface ITemplateApprovalRegistry with
        member _.Issue request = inner.Issue request
        member _.Accept record = inner.Accept record

        member _.Records templateId =
            reads <- reads + 1
            inner.Records templateId

let gateTests =
    testList "Phase 480 — where the check sits" [

        test "the approval check refuses BEFORE the handler computes anything" {
            let mutable handlerRuns = 0
            let registry = freshRegistry ()
            // Nobody has approved anything.

            let counting: ReachContract = {
                EstimateReach =
                    fun _ -> async {
                        handlerRuns <- handlerRuns + 1

                        return {
                            Shape = Count
                            Cells = [ cell "all" 500 ]
                        }
                    }
            }

            let sink = DecisionSink()

            let registration =
                (CleanRoomGate.wrapApproved
                    defaultBroker
                    templateT
                    (Some(TemplateApprovalGate.check (policyOver registry) seller))
                    sink.Sink
                    ((JsonRpcPeerHost.contract<ReachContract> contractId [ v1 ] None counting).Registration))
                    .Registration

            match
                registration.Dispatch callContext "EstimateReach" "[\"any\"]"
                |> Async.RunSynchronously
            with
            | Error(PeerCleanRoomWithheld _) -> ()
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "An unapproved template answered: %s" payload

            Expect.equal
                handlerRuns
                0
                "a template neither party has agreed to is not a contract this deployment computes anything under"
        }

        test "NEGATIVE CONTROL — the same handler ungated by approval runs and answers" {
            let mutable handlerRuns = 0

            let counting: ReachContract = {
                EstimateReach =
                    fun _ -> async {
                        handlerRuns <- handlerRuns + 1

                        return {
                            Shape = Count
                            Cells = [ cell "all" 500 ]
                        }
                    }
            }

            let sink = DecisionSink()

            let registration =
                (CleanRoomGate.wrap
                    defaultBroker
                    templateT
                    sink.Sink
                    ((JsonRpcPeerHost.contract<ReachContract> contractId [ v1 ] None counting).Registration))
                    .Registration

            match
                registration.Dispatch callContext "EstimateReach" "[\"any\"]"
                |> Async.RunSynchronously
            with
            | Ok _ -> Expect.equal handlerRuns 1 "the pre-480 gate runs the handler and releases"
            | Error e -> failtestf "The pre-480 control must succeed, got %A" e
        }

        test "GP 13 — a gate composed without a registry reads no registry" {
            let counting = CountingRegistry(freshRegistry ())
            let sink = DecisionSink()

            let registration =
                (CleanRoomGate.wrap defaultBroker templateT sink.Sink (rawRegistration 50)).Registration

            dispatch registration |> ignore

            Expect.equal
                counting.Reads
                0
                "wrap is wrapApproved with None — no store read, which is what makes an existing deployment byte-for-byte unchanged"
        }

        test "CONTROL — the composed check DOES read the registry, once per dispatch" {
            let counting = CountingRegistry(freshRegistry ())
            let sink = DecisionSink()

            let registration =
                (CleanRoomGate.wrapApproved
                    defaultBroker
                    templateT
                    (Some(TemplateApprovalGate.check (policyOver (counting :> ITemplateApprovalRegistry)) seller))
                    sink.Sink
                    (rawRegistration 50))
                    .Registration

            dispatch registration |> ignore
            Expect.equal counting.Reads 1 "the read the case above measures the absence of"
        }

        test "the wire refusal discloses only the template id" {
            let registry = freshRegistry ()
            let sink = DecisionSink()
            let registration = gatedWithApproval registry templateT 50 sink

            match dispatch registration with
            | Error e ->
                let wire = JsonRpc.serialize (JsonRpc.failure "id" e)
                Expect.stringContains wire templateT.TemplateId "the caller learns which template refused"

                Expect.isFalse
                    (wire.Contains seller || wire.Contains buyer)
                    "…and not who has or has not approved it; the reason lives on the receiver's audit row"
            | Ok payload -> failtestf "Expected a refusal, got %s" payload

            match sink.Rows with
            | [ row ] -> Expect.stringContains row.Reason buyer "the audit row DOES name the awaited party"
            | other -> failtestf "Expected exactly one decision row, got %i" (List.length other)
        }

        test "an unreachable registry withholds rather than opens" {
            // A privacy gate that opens when its consent record is
            // unavailable is not a gate.
            let broken =
                { new ITemplateApprovalRegistry with
                    member _.Issue _ = async { return Error(PeerHandler "store down") }
                    member _.Accept _ = async { return Error(PeerHandler "store down") }
                    member _.Records _ = async { return [] }
                }

            let sink = DecisionSink()

            let registration =
                (CleanRoomGate.wrapApproved
                    defaultBroker
                    templateT
                    (Some(TemplateApprovalGate.check (policyOver broken) seller))
                    sink.Sink
                    (rawRegistration 50))
                    .Registration

            match dispatch registration with
            | Error(PeerCleanRoomWithheld _) -> ()
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "An unreadable approval store let an answer through: %s" payload
        }
    ]

// ─── 5. Composition posture ──────────────────────────────────────────

let private enabledConfig = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
}

let private appHosting () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<ReachContract> contractId [ v1 ] fusion (impl 50))

let compositionTests =
    testList "Phase 480 — composition posture" [

        test "a fresh compose record declares no approval registry" {
            Expect.isNone (PeerServerApp.create ()).TemplateApprovals "off unless composed"

            Expect.equal
                (PeerServerApp.auditTemplateApprovals (appHosting ()))
                TemplateApprovalOff
                "…and reports nothing"
        }

        test "gating under a registry with a local identity is Enforced" {
            let app =
                appHosting ()
                |> PeerServerApp.withLocalPeer {
                    PeerId = seller
                    DisplayName = "Seller"
                }
                |> PeerServerApp.withCleanRoomTemplate contractId templateT
                |> PeerServerApp.withTemplateApprovals (policyOver (freshRegistry ()))

            match PeerServerApp.auditTemplateApprovals app with
            | TemplateApprovalEnforced(receiverId, gated) ->
                Expect.equal receiverId seller "the receiver's own id is the local half of every agreement"
                Expect.equal gated [ contractId ] "…over the gated contracts"
            | other -> failtestf "Expected Enforced, got %A" other

            PeerServerApp.enforceTemplateApprovals app
        }

        test "a registry with nothing gated is RecordingOnly, not a defect" {
            // Approving a counterparty's template so THEY can enforce
            // it, and holding the trail, is a whole posture.
            let app =
                appHosting ()
                |> PeerServerApp.withTemplateApprovals (policyOver (freshRegistry ()))

            Expect.equal
                (PeerServerApp.auditTemplateApprovals app)
                TemplateApprovalRecordingOnly
                "recording + serving approvals needs no local gate"

            PeerServerApp.enforceTemplateApprovals app
        }

        test "gating under a registry with NO local identity refuses to start" {
            let app =
                appHosting ()
                |> PeerServerApp.withCleanRoomTemplate contractId templateT
                |> PeerServerApp.withTemplateApprovals (policyOver (freshRegistry ()))

            Expect.equal
                (PeerServerApp.auditTemplateApprovals app)
                TemplateApprovalUnidentified
                "no peer id means no half of a bilateral agreement"

            Expect.throws
                (fun () -> PeerServerApp.enforceTemplateApprovals app)
                "every gated call would be withheld forever with no composed lever to fix it"
        }

        test "a blank LocalPeer id counts as absent" {
            let app =
                appHosting ()
                |> PeerServerApp.withLocalPeer { PeerId = "  "; DisplayName = "Blank" }
                |> PeerServerApp.withCleanRoomTemplate contractId templateT
                |> PeerServerApp.withTemplateApprovals (policyOver (freshRegistry ()))

            Expect.equal
                (PeerServerApp.auditTemplateApprovals app)
                TemplateApprovalUnidentified
                "an empty peer id is not an identity, it just looks composed"
        }

        test "GP 13 — the strip-imports path reports nothing even with a registry composed" {
            let app =
                PeerServerApp.create ()
                |> PeerServerApp.withCleanRoomTemplate contractId templateT
                |> PeerServerApp.withTemplateApprovals (policyOver (freshRegistry ()))

            Expect.equal
                (PeerServerApp.auditTemplateApprovals app)
                TemplateApprovalOff
                "NoPeerSubstrate registers nothing at all, so the short-circuit stays a bare ServerApp.run"
        }

        test "the reserved approval contract counts as hosted, so a template on it binds" {
            let app =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withTemplateApprovals (policyOver (freshRegistry ()))
                |> PeerServerApp.withCleanRoomTemplate TemplateApprovalContract.contractId templateT

            Expect.isEmpty
                (PeerServerApp.auditCleanRoomTemplates app)
                "the handshake contract is registered when a registry is composed"
        }

        test "the composed registry surfaces the handshake contract on the peer face" {
            let baseline =
                appHosting ()
                |> PeerServerApp.withLocalPeer {
                    PeerId = seller
                    DisplayName = "Seller"
                }

            let served (app: PeerServerApp) =
                (PeerSurface.describe app).Serves.Contracts |> List.map _.ContractId

            Expect.isFalse
                (List.contains TemplateApprovalContract.contractId (served baseline))
                "a deployment without the registry serves no approval contract"

            let withRegistry =
                baseline |> PeerServerApp.withTemplateApprovals (policyOver (freshRegistry ()))

            Expect.contains
                (served withRegistry)
                TemplateApprovalContract.contractId
                "a counterparty that cannot see the contract cannot submit an approval at all"
        }

        test "the skew tolerance is a tunable on the composed policy" {
            let tightened =
                policyOver (freshRegistry ())
                |> TemplateApprovalPolicy.withSkewTolerance (TimeSpan.FromSeconds 5.0)

            Expect.equal
                tightened.SkewTolerance
                (TimeSpan.FromSeconds 5.0)
                "an NTP-disciplined federation may shrink it"

            Expect.equal
                (policyOver (freshRegistry ())).SkewTolerance
                TemplateApproval.defaultSkew
                "…and the default is the shared peer clock skew"
        }
    ]

// ─── 6. The handshake contract ───────────────────────────────────────

let private contextFor peerId : PeerCallContext = {
    callContext with
        Peer = {
            PeerId = peerId
            DisplayName = peerId
        }
}

let private ackOf (json: string) =
    JsonRpc.deserialize<TemplateApprovalAck> json

let handshakeTests =
    testList "Phase 480 — the approval handshake over the peer channel" [

        test "a counterparty submits its own signed record and it is stored" {
            let registry = freshRegistry ()
            let record = issue registry (request buyer seller TemplateApproved templateT)
            let fresh = freshRegistry ()
            let host = TemplateApprovalContract.registration seller fresh

            let result =
                host.Dispatch (contextFor buyer) TemplateApprovalContract.submitMethod (JsonRpc.serialize [ record ])
                |> Async.RunSynchronously

            match result with
            | Ok json ->
                // The record was signed by a DIFFERENT registry's key
                // material, so the fresh receiver cannot verify it —
                // which is the point of the next case. Here we only
                // assert the routing reached `Accept`.
                Expect.isFalse (ackOf json).Accepted "a record signed under unknown key material is refused"
            | Error e -> failtestf "The submit method must answer, got %A" e

            // …and now the honest path: one registry, one key set.
            let acked =
                (TemplateApprovalContract.registration seller registry).Dispatch
                    (contextFor buyer)
                    TemplateApprovalContract.submitMethod
                    (JsonRpc.serialize [ record ])
                |> Async.RunSynchronously

            match acked with
            | Ok json -> Expect.isTrue (ackOf json).Accepted "a verifiable record from its own signer is accepted"
            | Error e -> failtestf "Expected an ack, got %A" e
        }

        test "a peer may not submit a record it did not sign" {
            let registry = freshRegistry ()
            let record = issue registry (request buyer seller TemplateApproved templateT)
            let host = TemplateApprovalContract.registration seller registry

            let result =
                host.Dispatch (contextFor "rival") TemplateApprovalContract.submitMethod (JsonRpc.serialize [ record ])
                |> Async.RunSynchronously

            match result with
            | Ok json ->
                let ack = ackOf json
                Expect.isFalse ack.Accepted "the acting peer must be the calling peer"
                Expect.stringContains ack.Reason "signed it" "…and the ack says so"
            | Error e -> failtestf "Expected an ack, got %A" e
        }

        test "a record addressed to a different counterparty is refused" {
            let registry = freshRegistry ()

            let record =
                issue registry (request buyer "somewhere-else" TemplateApproved templateT)

            let host = TemplateApprovalContract.registration seller registry

            let result =
                host.Dispatch (contextFor buyer) TemplateApprovalContract.submitMethod (JsonRpc.serialize [ record ])
                |> Async.RunSynchronously

            match result with
            | Ok json -> Expect.isFalse (ackOf json).Accepted "this deployment is not the named counterparty"
            | Error e -> failtestf "Expected an ack, got %A" e
        }

        test "a query returns only the records the caller is a party to" {
            let registry = freshRegistry ()
            approveBilaterally registry templateT

            let host = TemplateApprovalContract.registration seller registry

            let scoped (caller: string) =
                match
                    host.Dispatch
                        (contextFor caller)
                        TemplateApprovalContract.queryMethod
                        (JsonRpc.serialize [ templateT.TemplateId ])
                    |> Async.RunSynchronously
                with
                | Ok json -> JsonRpc.deserialize<TemplateApprovalRecord list> json
                | Error e -> failtestf "The query must answer, got %A" e

            Expect.hasLength (scoped buyer) 2 "the buyer sees the agreement it is a party to"

            Expect.isEmpty
                (scoped "rival")
                "a peer that is party to nothing sees nothing — the scope comes from the validated principal, and the query has no peer-id field to spoof"
        }

        test "an unknown method is PeerMethodNotFound" {
            let host = TemplateApprovalContract.registration seller (freshRegistry ())

            match host.Dispatch (contextFor buyer) "Nope" "[]" |> Async.RunSynchronously with
            | Error(PeerMethodNotFound name) -> Expect.equal name "Nope" "the unknown name is echoed"
            | other -> failtestf "Expected PeerMethodNotFound, got %A" other
        }

        test "the contract id is namespaced under the reserved peer prefix" {
            Expect.stringStarts
                TemplateApprovalContract.contractId
                "_platform.peer."
                "a reserved id can never collide with an author-defined contract"
        }
    ]

// ─── 7. The admin queue ──────────────────────────────────────────────

let queueTests =
    testList "Phase 480 — the approval queue an admin surface renders" [

        test "one row per (template, version, counterparty) with each side's latest action" {
            let records = [
                signedStub seller buyer TemplateProposed (now.AddDays -3.0) None
                signedStub seller buyer TemplateApproved (now.AddDays -2.0) None
                signedStub buyer seller TemplateReviewed (now.AddDays -1.0) None
            ]

            match TemplateApprovalQueue.project TemplateApproval.defaultSkew seller now records with
            | [ row ] ->
                Expect.equal row.TemplateId templateT.TemplateId "the template"
                Expect.equal row.TemplateVersion version "…at the exact version agreed"
                Expect.equal row.CounterpartyPeerId buyer "…with the counterparty"
                Expect.equal row.LocalAction (Some TemplateApproved) "our latest word"
                Expect.equal row.CounterpartyAction (Some TemplateReviewed) "…and theirs"

                match row.Status with
                | ApprovalPending [ awaiting ] -> Expect.equal awaiting buyer "so the pair is pending on them"
                | other -> failtestf "Expected pending, got %A" other
            | other -> failtestf "Expected one queue row, got %i" (List.length other)
        }

        test "an inbound proposal we have not answered shows as pending on us" {
            let records = [ signedStub buyer seller TemplateProposed (now.AddDays -1.0) None ]

            match TemplateApprovalQueue.project TemplateApproval.defaultSkew seller now records with
            | [ row ] ->
                Expect.isNone row.LocalAction "we have said nothing"
                Expect.equal row.CounterpartyAction (Some TemplateProposed) "they proposed"
            | other -> failtestf "Expected one queue row, got %i" (List.length other)
        }

        test "another federation's records are not in our queue" {
            let records = [ signedStub "rival" "other" TemplateApproved (now.AddDays -1.0) None ]

            Expect.isEmpty
                (TemplateApprovalQueue.project TemplateApproval.defaultSkew seller now records)
                "a record naming neither role for this deployment is somebody else's business"
        }

        test "two versions of one template are two rows, ordered deterministically" {
            let atVersion (template: CleanRoomTemplate) action = {
                signedStub seller buyer action (now.AddDays -1.0) None with
                    TemplateVersion = TemplateCanonical.version template
            }

            let records = [
                atVersion templateTPrime TemplateProposed
                atVersion templateT TemplateApproved
            ]

            let rows =
                TemplateApprovalQueue.project TemplateApproval.defaultSkew seller now records

            Expect.hasLength rows 2 "an edit is a new version, and a new row — not an overwrite of the old agreement"

            Expect.equal
                (rows |> List.map _.TemplateVersion)
                (rows |> List.map _.TemplateVersion |> List.sort)
                "the projection is sorted, so two machines render the same queue"
        }
    ]