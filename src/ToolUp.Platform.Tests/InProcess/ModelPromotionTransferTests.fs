// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelPromotionTransferTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

// ─── Phase 646 — promotion-time provenance transfer ─────────────────────
//
// Three halves, deliberately tested apart, because each is a claim about a
// different thing.
//
// The **pure judge** (`ModelPromotion.judge`) is exercised with no store,
// no signer and no clock, because every refusal claim this phase makes is a
// claim about that function: the ORDER of judgment, the fact that a corrupt
// payload is reported as corrupt rather than as oversized, and the fact
// that a grant is required even when the artifact is already at the target.
// Asserting those through the envelope would prove the envelope reached the
// right verdict on one path, not that the function cannot reach the wrong
// one.
//
// The **envelope** (`ModelPromotion.accept`) runs over the real
// blob-backed registry, so the writes it drives are real Phase 453
// versioned writes, the lifecycle move goes through the real Phase 644
// seam, and the attachments are read back through the registry's own
// hash-verifying decode rather than out of a variable the test still holds.
//
// The **provenance walk** is the acceptance criterion stated as a test: a
// chain rooted at the promoted artifact, with the builder deployment
// entirely absent from the process, must reach the exploration record and
// the dataset vintage. That is what "the builder is dispensable" means
// operationally, and it is the one claim the phase would be worthless
// without.

let private t0 = DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero)

let private scope = "consortium-north"

/// Records every `Record` call so a test can assert the audited fact rather
/// than infer it from an answer.
type private RecordingAuditLog() =
    let recorded = ResizeArray<AuditEvent>()
    member _.Events = List.ofSeq recorded

    member this.Promotions =
        this.Events
        |> List.choose (function
            | ModelArtifactPromoted p -> Some p
            | _ -> None)

    member this.Attachments =
        this.Events
        |> List.choose (function
            | ModelArtifactProvenanceAttached p -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(_, audit) = async { recorded.Add audit }
        member _.GetAuditTrail(_, _, _) = async { return List.ofSeq recorded }

/// A signer that mints a deterministic, verifiable-by-construction
/// "signature": the input's own digest, reversed. Deterministic so a test
/// can assert an exact value; obviously not cryptography, which is the
/// point — what is under test here is that the RIGHT BYTES reach a signer
/// and that its answer is stored, not that ECDSA works.
type private RecordingSigner(keyId: string) =
    let seen = ResizeArray<string>()

    /// The canonical inputs this signer was handed, in order.
    member _.Inputs = List.ofSeq seen

    interface IPromotedArtifactSigner with
        member _.SignArtifact(input) = async {
            seen.Add(Encoding.UTF8.GetString input)

            return
                Ok {
                    DetachedJws = "jws." + (ProvenanceAttachment.hashOf input)
                    SigningKeyId = keyId
                    SigningKeyUrl = "/_platform/signing-key/" + keyId
                    SignedInputHash = ProvenanceAttachment.hashOf input
                }
        }

/// A signer that cannot sign. The transfer must refuse with NOTHING
/// written — see `ModelPromotionTransfer.fs`'s header on why the signature
/// is minted before the first store write.
type private FailingSigner() =
    interface IPromotedArtifactSigner with
        member _.SignArtifact _ = async { return Error "the signing key is unavailable" }

type private Stack = {
    Registry: IModelRegistry
    DataObjects: IDataObjectStore
    Lineage: ILineageStore
    Audit: RecordingAuditLog
}

let private freshStack (limits: ProvenanceAttachmentLimits) : Stack =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-transfer-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let audit = RecordingAuditLog()
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let lineage = LineageStore.EventStoreLineageStore(eventStore) :> ILineageStore

    {
        Registry = BlobModelRegistry.createWithLimits dataObjects audit (Some lineage) limits
        DataObjects = dataObjects
        Lineage = lineage
        Audit = audit
    }

let private depsOf (stack: Stack) (signer: IPromotedArtifactSigner option) : ModelPromotionDeps = {
    Transition = {
        Registry = stack.Registry
        Audit = stack.Audit
        Now = fun () -> t0
    }
    Signer = signer
}

// ─── The reference transfer ─────────────────────────────────────────────

/// The opaque exploration record a modelling tool kept beside the fit —
/// the canonical example of what an attachment IS. Nothing here reads it.
let private explorationRecord =
    """{"candidates":["price","promo","seasonality"],"kept":["price","promo"]}"""

let private runLog = "fit 1/3 converged\nfit 2/3 converged\n"

let private specPayload = """{"link":"log","terms":["price","promo"]}"""

let private datasetVersion = $"{scope}/weekly-panel@v7"

let private promotedKey =
    FitCompositeKey.compute "sha256:submitter-minted" datasetVersion 20260716L "reference-regression" "1.4.0"

let private grantApprove = ModelTransitionAuthority.ofTargets [ "Approved" ]

let private author = PeerActor("builder-north", "r.okafor")

let private promotedArtifact: PromotedArtifact = {
    Outcome = {
        CompositeKey = promotedKey
        ArtifactRef = {
            ArtifactId = "artifact-8821"
            ContentHash = "sha256:parameters"
            ByteLength = 4096L
        }
        Diagnostics = Map [ "holdout-r2", 0.71 ]
        GateVerdicts = [
            {
                Name = "holdout-r2"
                Threshold = 0.6
                Direction = GateDirection.AtLeast
                Observed = 0.71
                Passed = true
            }
        ]
        DurationMs = 0L
        CostUnits = 0.0
    }
    SpecPayload = specPayload
    Attachments = [
        ProvenanceAttachment.ofText "application/json" explorationRecord
        ProvenanceAttachment.ofText "text/plain" runLog
    ]
    Target = ModelArtifactStatus.Approved
    Author = author
    Rationale = Some "holdout MAPE within tolerance on three vintages"
}

/// The three digests the artifact holds once a transfer lands: the two
/// records plus the spec payload, which the receiver folds into the same
/// append-only slot.
let private expectedHashes =
    ModelPromotion.arriving promotedArtifact
    |> List.map _.ContentHash
    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

/// The artifact the scope already holds, for the conflict + replay cases.
let private heldArtifact (contentHash: string) (status: ModelArtifactStatus) : ModelArtifact = {
    CompositeKey = promotedKey
    ScopeId = scope
    ArtifactRef = {
        ArtifactId = "artifact-8821"
        ContentHash = contentHash
        ByteLength = 4096L
    }
    Diagnostics = Map.empty
    GateVerdicts = []
    Status = status
    Annotations = Map.empty
    Notes = ""
    Attachments = []
    Signature = None
    RegisteredBy = "local"
    RegisteredAt = t0
    Version = 1
}

let private classOf (refusal: ModelPromotionRefusal) =
    match refusal with
    | ModelPromotionRefusal.AttachmentRefused(_, ProvenanceAttachmentRefusal.HashMismatch _) -> "hash-mismatch"
    | ModelPromotionRefusal.AttachmentRefused(_, ProvenanceAttachmentRefusal.CapExceeded(dimension, _, _)) ->
        "cap:" + dimension
    | ModelPromotionRefusal.PayloadConflict(_, field) -> "conflict:" + field
    | ModelPromotionRefusal.TransitionRefused(ModelTransitionRefusal.InsufficientAuthority _) -> "authority"
    | ModelPromotionRefusal.TransitionRefused(ModelTransitionRefusal.InvalidTransition _) -> "invalid-edge"
    | ModelPromotionRefusal.TransitionRefused(ModelTransitionRefusal.UnknownArtifact _) -> "unknown"
    | ModelPromotionRefusal.SigningFailed _ -> "signing"

let private judgeDefault existing authority promoted =
    ModelPromotion.judge existing ProvenanceAttachmentLimits.default' authority promoted

[<Tests>]
let tests =
    testList "Phase 646 — promotion-time provenance transfer" [

        // ── The pure judge ───────────────────────────────────────────

        test "a clean transfer of a new artifact plans a registration, its attachments, and the lifecycle move" {
            match judgeDefault None grantApprove promotedArtifact with
            | Error refusal -> failtestf "expected a plan; got %s" (ModelPromotionRefusal.describe refusal)
            | Ok plan ->
                Expect.isTrue plan.Registers "the scope does not hold the key, so the transfer registers it"

                Expect.equal
                    (plan.NewAttachments |> List.map _.ContentHash |> List.sort)
                    (expectedHashes |> List.sort)
                    "the spec payload is folded into the attachment set beside the records that arrived"

                Expect.equal
                    plan.Transition
                    (Some ModelArtifactStatus.Approved)
                    "a new artifact is born Fitted, so the transfer takes the Fitted → Approved edge"

                Expect.isFalse plan.Replay "nothing was held, so nothing is a replay"
        }

        test "a corrupt attachment is refused as corrupt, and BEFORE the cap it also breaches" {
            // The vector is deliberately wrong in TWO ways at once: the
            // declared digest disagrees with the bytes AND the set is over
            // a cap of one. If integrity were judged second, this would
            // report a size problem — and a sender told to send less when
            // what it sent was corrupt fixes the wrong thing.
            let corrupted = {
                promotedArtifact with
                    Attachments =
                        promotedArtifact.Attachments
                        |> List.map (fun a -> {
                            a with
                                ContentHash = "sha256:not-the-digest"
                        })
            }

            let tightLimits = {
                ProvenanceAttachmentLimits.default' with
                    MaxAttachments = 1
            }

            match ModelPromotion.judge None tightLimits grantApprove corrupted with
            | Ok _ -> failtest "a corrupt attachment set must be refused"
            | Error refusal -> Expect.equal (classOf refusal) "hash-mismatch" "integrity is judged before size"
        }

        test "each declared cap dimension refuses on its own terms" {
            let overCount = {
                ProvenanceAttachmentLimits.default' with
                    MaxAttachments = 1
            }

            let overPerAttachment = {
                ProvenanceAttachmentLimits.default' with
                    MaxAttachmentBytes = 4
            }

            let overTotal = {
                ProvenanceAttachmentLimits.default' with
                    MaxTotalBytes = 8
            }

            for limits, expected in
                [
                    overCount, "cap:count"
                    overPerAttachment, "cap:attachment-bytes"
                    overTotal, "cap:total-bytes"
                ] do
                match ModelPromotion.judge None limits grantApprove promotedArtifact with
                | Ok _ -> failtestf "expected %s" expected
                | Error refusal -> Expect.equal (classOf refusal) expected "the refusal names the bound it hit"
        }

        test "a composite key already held with different content is a conflict, not a merge" {
            let existing = heldArtifact "sha256:different-parameters" ModelArtifactStatus.Fitted

            match judgeDefault (Some existing) grantApprove promotedArtifact with
            | Ok _ -> failtest "two artifacts under one composite key must not be reconciled quietly"
            | Error refusal ->
                Expect.equal
                    (classOf refusal)
                    "conflict:ArtifactRef.ContentHash"
                    "the refusal names what differs, so a re-send of a changed artifact is distinguishable"
        }

        test "the peer's Phase 644 grant gates the transfer — a peer that may not approve may not promote to Approved" {
            let grantRetireOnly = ModelTransitionAuthority.ofTargets [ "Retired" ]

            match judgeDefault None grantRetireOnly promotedArtifact with
            | Ok _ -> failtest "an ungranted target must be refused"
            | Error refusal ->
                Expect.equal
                    (classOf refusal)
                    "authority"
                    "the transfer reuses the transition vocabulary, because it is the same question"
        }

        test
            "an artifact ALREADY at the target still needs the grant — 'promote to where it is' is not a way around the gate" {
            let existing = heldArtifact "sha256:parameters" ModelArtifactStatus.Approved

            // Granted: a replay, with no lifecycle move to make.
            match judgeDefault (Some existing) grantApprove promotedArtifact with
            | Error refusal -> failtestf "a granted re-send must be accepted; got %s" (classOf refusal)
            | Ok plan ->
                Expect.isNone plan.Transition "the artifact already holds the target, so nothing moves"
                Expect.isFalse plan.Registers "the key is already held"

            // Ungranted: refused, even though nothing would move. A write
            // path that needed no authority because it changed no status
            // would be a hole in the gate exactly where an attacker would
            // look for one.
            match judgeDefault (Some existing) ModelTransitionAuthority.none promotedArtifact with
            | Ok _ -> failtest "an ungranted author must not land attachments on an already-approved artifact"
            | Error refusal -> Expect.equal (classOf refusal) "authority" "the grant is required regardless of movement"
        }

        test "an impossible edge is refused regardless of grant" {
            let retired = heldArtifact "sha256:parameters" ModelArtifactStatus.Retired

            match
                ModelPromotion.judge
                    (Some retired)
                    ProvenanceAttachmentLimits.default'
                    ModelTransitionAuthority.full
                    promotedArtifact
            with
            | Ok _ -> failtest "Retired is terminal; no grant makes it leavable"
            | Error refusal ->
                Expect.equal (classOf refusal) "invalid-edge" "Phase 644's order, unchanged: the edge before the grant"
        }

        // ── The envelope, over the real registry ─────────────────────

        testCaseAsync "an accepted transfer lands the artifact, its spec payload, its attachments and its signature"
        <| async {
            let stack = freshStack ProvenanceAttachmentLimits.default'
            let signer = RecordingSigner "data-host-2026-07"
            let deps = depsOf stack (Some(signer :> IPromotedArtifactSigner))

            match! ModelPromotion.accept deps scope grantApprove promotedArtifact with
            | Error refusal -> failtestf "expected acceptance; got %s" (ModelPromotionRefusal.describe refusal)
            | Ok record ->
                Expect.equal record.Status "Approved" "the artifact holds the transferred target"
                Expect.equal record.AttachmentHashes expectedHashes "the receipt cites every attachment, ordinally"
                Expect.isFalse record.Replayed "a first acceptance is not a replay"
                Expect.equal record.AuthorKind "peer" "the transfer is attributed to the peer that sent it"
                Expect.equal record.AuthorId "builder-north/r.okafor" "both identities, per Phase 644"

                let signature =
                    Expect.wantSome record.Signature "an accepted transfer carries the acceptance signature"

                Expect.equal signature.SigningKeyId "data-host-2026-07" "the data host's own key signed it"

                // The signer was handed the canonical form the
                // specification states, not some other rendering of it.
                let expectedInput =
                    ModelPromotionSigningInput.canonical promotedKey ModelArtifactStatus.Approved expectedHashes

                Expect.equal signer.Inputs [ expectedInput ] "the signer signs the canonical acceptance input, once"

                // Read the artifact BACK through the registry, so what is
                // asserted is what was stored and re-decoded (which is also
                // where the hash verification runs), not what the caller
                // still holds in a variable.
                match! stack.Registry.Get(scope, promotedKey.Hash) with
                | Error e -> failtestf "the promoted artifact must be readable: %s" (ModelRegistryError.describe e)
                | Ok stored ->
                    Expect.equal stored.Status ModelArtifactStatus.Approved "stored at the transferred status"

                    Expect.equal
                        (stored.Attachments |> List.map _.ContentHash |> List.sort)
                        (expectedHashes |> List.sort)
                        "every attachment survived the round trip through storage"

                    Expect.isSome stored.Signature "the signature is stored WITH the artifact record"

                    // The dispensable-builder claim, at its narrowest: the
                    // opaque spec payload is recoverable byte-for-byte from
                    // this deployment alone.
                    let spec =
                        stored.Attachments
                        |> List.find (fun a -> a.MediaType = ProvenanceAttachment.SpecPayloadMediaType)

                    Expect.equal
                        (Encoding.UTF8.GetString(ProvenanceAttachment.bytes spec))
                        specPayload
                        "the spec payload is recoverable verbatim, data-side"

                let promotion =
                    Expect.wantSome (List.tryHead stack.Audit.Promotions) "the transfer is audited"

                Expect.isTrue promotion.Accepted "the audited row records the acceptance"
                Expect.equal promotion.Channel "peer" "and the channel it arrived on"
        }

        testCaseAsync "re-sending the identical transfer succeeds, writes nothing, and does not duplicate an attachment"
        <| async {
            let stack = freshStack ProvenanceAttachmentLimits.default'
            let signer = RecordingSigner "data-host-2026-07" :> IPromotedArtifactSigner
            let deps = depsOf stack (Some signer)

            let! first = ModelPromotion.accept deps scope grantApprove promotedArtifact
            let firstRecord = Expect.wantOk first "the first transfer is accepted"

            let! second = ModelPromotion.accept deps scope grantApprove promotedArtifact

            let secondRecord =
                Expect.wantOk second "an identical re-send is ACCEPTED, not refused"

            Expect.isTrue secondRecord.Replayed "the second transfer reports itself as a replay"

            Expect.equal
                secondRecord.Version
                firstRecord.Version
                "no version was appended: a replay writes nothing, which is what makes it idempotent"

            match! stack.Registry.Get(scope, promotedKey.Hash) with
            | Error e -> failtestf "unreadable: %s" (ModelRegistryError.describe e)
            | Ok stored ->
                Expect.equal
                    (List.length stored.Attachments)
                    (List.length expectedHashes)
                    "attachments de-duplicate by content hash, so a re-send appends nothing"
        }

        testCaseAsync "a signer that cannot sign refuses the transfer with nothing written"
        <| async {
            let stack = freshStack ProvenanceAttachmentLimits.default'
            let deps = depsOf stack (Some(FailingSigner() :> IPromotedArtifactSigner))

            match! ModelPromotion.accept deps scope grantApprove promotedArtifact with
            | Ok _ -> failtest "a data host that cannot attest to an artifact must not silently hold one"
            | Error refusal -> Expect.equal (classOf refusal) "signing" "the failure is typed, not swallowed"

            match! stack.Registry.Get(scope, promotedKey.Hash) with
            | Ok _ -> failtest "the artifact must NOT have landed: the signature is minted before the first write"
            | Error ModelRegistryError.NotFound -> ()
            | Error e -> failtestf "unexpected: %s" (ModelRegistryError.describe e)

            let promotion =
                Expect.wantSome (List.tryHead stack.Audit.Promotions) "a refused transfer is audited too"

            Expect.isFalse promotion.Accepted "the row records the refusal"

            Expect.stringContains
                promotion.Refusal
                "could not be signed"
                "and names it, so 'which peer tried to promote what' is answerable without a registry row"
        }

        testCaseAsync "an ungranted peer's transfer is refused and lands nothing"
        <| async {
            let stack = freshStack ProvenanceAttachmentLimits.default'
            let deps = depsOf stack None

            match! ModelPromotion.accept deps scope ModelTransitionAuthority.none promotedArtifact with
            | Ok _ -> failtest "an undeclared grant admits nothing"
            | Error refusal -> Expect.equal (classOf refusal) "authority" "refused on the Phase 644 grant"

            match! stack.Registry.Get(scope, promotedKey.Hash) with
            | Error ModelRegistryError.NotFound -> ()
            | _ -> failtest "a refused transfer must not have registered the artifact"
        }

        // ── The registry's own append-only + read guarantees ─────────

        testCaseAsync "an attachment whose stored bytes no longer match its digest fails the READ"
        <| async {
            let stack = freshStack ProvenanceAttachmentLimits.default'
            let deps = depsOf stack None

            let! accepted = ModelPromotion.accept deps scope grantApprove promotedArtifact
            Expect.isOk accepted "the transfer lands"

            // Rewrite the stored record with one attachment's declared
            // digest corrupted. This is the only way to reach the read
            // path's verification, and reaching it matters: a citation is
            // worth nothing if the thing cited can drift.
            match! stack.DataObjects.Get(scope, promotedKey.Hash) with
            | Error e -> failtestf "unreadable: %A" e
            | Ok(_, bytes) ->
                let json = Encoding.UTF8.GetString bytes

                let tampered =
                    json.Replace("\"" + ProvenanceAttachment.HashPrefix, "\"" + ProvenanceAttachment.HashPrefix + "ff")

                Expect.notEqual tampered json "the fixture must actually have been tampered with"

                let! saved =
                    stack.DataObjects.Save(
                        scope,
                        promotedKey.Hash,
                        Encoding.UTF8.GetBytes tampered,
                        "toolup.modelartifact",
                        "tamper",
                        Map.empty,
                        VersioningPolicy.StrictlyVersioned
                    )

                Expect.isOk saved "the tampered version is stored"

                match! stack.Registry.Get(scope, promotedKey.Hash) with
                | Error(ModelRegistryError.AttachmentRefused(ProvenanceAttachmentRefusal.HashMismatch _)) -> ()
                | Ok _ -> failtest "a record whose attachment bytes no longer hash to their digest must not be returned"
                | Error e -> failtestf "expected a hash mismatch; got %s" (ModelRegistryError.describe e)
        }

        testCaseAsync "AttachProvenance is append-only: nothing held is dropped, and a repeat is a no-op"
        <| async {
            let stack = freshStack ProvenanceAttachmentLimits.default'
            // Signed, because one of the claims below is that a later
            // append does not erase an acceptance signature — which is
            // unfalsifiable against a transfer that never minted one.
            let deps = depsOf stack (Some(RecordingSigner "k" :> IPromotedArtifactSigner))

            let! accepted = ModelPromotion.accept deps scope grantApprove promotedArtifact
            Expect.isOk accepted "the transfer lands"

            let extra = ProvenanceAttachment.ofText "text/csv" "week,spend\n1,100\n"

            match! stack.Registry.AttachProvenance(scope, promotedKey.Hash, [ extra ], None) with
            | Error e -> failtestf "the append must succeed: %s" (ModelRegistryError.describe e)
            | Ok updated ->
                Expect.equal
                    (List.length updated.Attachments)
                    (List.length expectedHashes + 1)
                    "the new record is appended and every earlier one is still held"

                Expect.isSome updated.Signature "an append does not erase the acceptance signature"

            match! stack.Registry.AttachProvenance(scope, promotedKey.Hash, [ extra ], None) with
            | Error e -> failtestf "a repeat must be a no-op, not a failure: %s" (ModelRegistryError.describe e)
            | Ok repeated ->
                Expect.equal
                    (List.length repeated.Attachments)
                    (List.length expectedHashes + 1)
                    "an attachment already held is not appended twice"
        }

        testCaseAsync "an attachment over the DECLARED cap is refused by the registry, not silently truncated"
        <| async {
            let stack =
                freshStack {
                    ProvenanceAttachmentLimits.default' with
                        MaxAttachments = 1
                }

            Expect.equal
                stack.Registry.AttachmentLimits.MaxAttachments
                1
                "the cap is DECLARED, so a caller can size a transfer before sending it"

            let deps = depsOf stack None

            match! ModelPromotion.accept deps scope grantApprove promotedArtifact with
            | Ok _ -> failtest "three attachments against a cap of one must be refused"
            | Error refusal -> Expect.equal (classOf refusal) "cap:count" "and refused as a cap breach"
        }

        // ── The Phase 524 walk, with the builder gone ────────────────

        testCaseAsync "the provenance walk reaches the attachments and the vintage with no reference to the builder"
        <| async {
            let stack = freshStack ProvenanceAttachmentLimits.default'
            let deps = depsOf stack (Some(RecordingSigner "k" :> IPromotedArtifactSigner))

            let! accepted = ModelPromotion.accept deps scope grantApprove promotedArtifact
            Expect.isOk accepted "the transfer lands"

            // Everything below runs against this deployment's own stores.
            // There is no peer client, no binding and no builder in this
            // test by construction — which is the whole claim.
            let graph =
                ProvenanceGraph.createWithArtifacts stack.Lineage (ModelArtifactProvenance.source stack.Registry)

            let! chain = graph.GetChain(scope, ModelArtifactRef promotedKey.Hash, Upstream, 4)

            Expect.equal chain.Root promotedKey.Hash "the chain is rooted at the promoted artifact"

            let attachmentNodes =
                chain.Nodes |> List.filter (fun n -> n.Kind = ProvenanceAttachmentNode)

            Expect.equal
                (attachmentNodes |> List.map _.Id |> List.sort)
                (expectedHashes |> List.sort)
                "every attachment is a chain node, cited by its digest"

            Expect.isTrue
                (attachmentNodes |> List.exists (fun n -> n.Label = "application/json"))
                "and labelled by its declared media type — cited, never interpreted"

            Expect.equal
                (chain.Edges |> List.filter (fun e -> e.Kind = HasAttachment) |> List.length)
                (List.length expectedHashes)
                "one HasAttachment edge per attachment, from the artifact"

            Expect.isTrue
                (chain.Edges
                 |> List.exists (fun e -> e.From = promotedKey.Hash && e.To = datasetVersion && e.Kind = DerivedFrom))
                "and the artifact reaches the dataset vintage, which is where the assembly chain continues"

            let artifactNode = chain.Nodes |> List.find (fun n -> n.Kind = ModelArtifactNode)

            Expect.equal
                artifactNode.Disclosure
                (Some "Approved")
                "the artifact node carries its lifecycle status, so a renderer can tell an approved base from a retired one"
        }
    ]