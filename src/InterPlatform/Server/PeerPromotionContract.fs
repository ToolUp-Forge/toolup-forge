// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open ToolUp.Platform

// ─── Phase 646 — promotion transfer over the peer seam ───────────────
//
// Phase 644 moved the lifecycle JUDGMENT across the seam: a peer that
// holds the modelling authority can approve an artifact the data host
// already holds. What it could not do is hand the data host one.
//
// That gap is the whole two-instance topology. A builder deployment fits
// models, explores, and produces evidence; a data host holds the durable
// record and publishes numbers from it. The builder is meant to be
// **dispensable after promotion** — retire it, and everything the data
// host publishes must still resolve. It cannot, if the artifact's spec
// payload and the exploration record that justified it live only on the
// machine that is being switched off.
//
// This file is the wire half of closing that: the transfer a builder
// sends, the receipt it gets back, and the refusal classes for the three
// things a transfer can get wrong that a bare transition cannot.
//
// ── One grant, not a second one ──────────────────────────────────────
//
// A promotion transfer is gated by the peer's Phase 644 **transition
// grant**, unchanged and per-peer. A peer that may not drive an artifact
// into `Approved` may not promote-transfer one into `Approved` either —
// and the refusal it gets is the same class, from the same judge, because
// it is the same question. A second grant covering "may this peer send us
// artifacts" was considered and rejected: it would let a deployment grant
// `Approved` on one axis and withhold it on the other, which is not a
// finer control but a contradiction an operator would have to reconcile.
//
// ── The bytes are opaque, and that is structural ─────────────────────
//
// An attachment crosses as `(mediaType, contentHash, base64 content)`.
// The receiver hashes what arrived and refuses a disagreement; it never
// parses. Forge does not type another pillar's tree — an exploration-path
// record from a modelling tool is somebody else's schema, and a seam that
// understood it would make that schema a dependency of this one. The spec
// payload rides the same way, for the reason §5.7.3 already gives.

// ─── Wire shapes (FEDERATION_WIRE.md §5.7.12) ────────────────────────

/// One opaque provenance record, as it crosses the seam.
type PeerPromotionAttachment = {
    /// IANA-shaped media type. **A label, not an instruction** — the
    /// receiver records it so a later reader knows what to open the bytes
    /// with, and selects no behaviour on it. An unrecognised value is
    /// ordinary.
    MediaType: string
    /// `sha256:<lowercase hex>` over the DECODED bytes, declared by the
    /// sender. The receiver recomputes it and refuses a disagreement —
    /// which is the one thing a receiver can check about a payload it is
    /// forbidden to read.
    ContentHash: string
    /// Base64 of the opaque bytes. Base64 rather than an embedded document
    /// (§3.1 rule 12) because the content is not JSON and is not required
    /// to be: a provenance record can be a protobuf, a parquet fragment or
    /// a tarball, and a slot that assumed otherwise would exclude exactly
    /// the tools worth attaching.
    Content: string
}

/// One gate's verdict, carried with the promoted artifact.
///
/// Declared here rather than reused from the outcome contract because this
/// file compiles ahead of it — and the duplication is the posture each
/// contract already takes with its own wire shapes: a shape shared by
/// reference between two contracts is one that cannot version separately.
type PeerPromotionGateVerdict = {
    Name: string
    Threshold: float
    Direction: string
    Observed: float
    Passed: bool
}

/// A final artifact being transferred to a data host: its identity, the
/// opaque spec it was fit from, the evidence it carries, and the lifecycle
/// status it is asked to hold.
///
/// **No scope member**, for the reason nothing on this profile has one:
/// the peer binding decides the scope. **No role member** either, and no
/// grant member — the receiver's own per-peer declaration decides the
/// authority, so nothing a sender writes can widen it.
///
/// **Idempotent by `ArtifactKey` + content hashes.** Re-sending the
/// identical transfer succeeds and writes nothing; a DIFFERENT payload
/// under the same key is refused as a conflict rather than reconciled,
/// because a composite key names one artifact and two artifacts under one
/// key would leave every downstream citation ambiguous about which it
/// meant.
type PeerPromotionTransfer = {
    /// The artifact's composite-key hash — the id every registry read on
    /// this profile already keys on, and the transfer's idempotency key.
    ArtifactKey: string
    /// The submitter-minted spec hash. Stored and keyed verbatim; the
    /// receiver never re-derives it (§5.7.3, the Phase 603 opacity
    /// contract).
    SpecHash: string
    /// The opaque provider spec. Carried so the artifact's assembly is
    /// resolvable data-side once the builder is gone.
    SpecPayload: string
    /// The `{scopeId}/{datasetId}@v{version}` key the fit read.
    DatasetVersion: string
    Seed: int64
    ProviderId: string
    ProviderVersion: string
    ArtifactId: string
    ArtifactContentHash: string
    ArtifactByteLength: int64
    /// Provider-reported diagnostics. Keys sorted ordinally (§3.1 rule 14).
    Diagnostics: Map<string, float>
    /// The building deployment's gate verdicts, sorted ordinally by `Name`.
    GateVerdicts: PeerPromotionGateVerdict list
    /// The opaque provenance records, sorted ordinally by `ContentHash` —
    /// a set has no order, so the emitter fixes one rather than carrying
    /// whichever order the sender happened to build it in.
    Attachments: PeerPromotionAttachment list
    /// The status the artifact is asked to hold, as its stable
    /// `ModelArtifactStatus` label. A label naming no status this profile
    /// defines is refused as an unreadable request, not as an illegal
    /// edge — §5.7.11's distinction, unchanged.
    Target: string
    /// Who, at the CALLING deployment, is asking. A claim, recorded
    /// verbatim, cross-checked against nothing and deciding nothing — the
    /// `SubmitterClass` posture §5.7.11 already takes for `ActorId`.
    ActorId: string
    /// The author's stated reason. `null` asserts nothing and is ordinary.
    Rationale: string option
}

/// The receipt an accepted transfer produced.
///
/// Every member is metadata about writes the data host has already
/// committed. There is no member an artifact's parameters or a dataset row
/// could ride in — the §5.7.4 argument again — which is why a transfer
/// needs no visibility level above the floor.
type PeerPromotionRecord = {
    ArtifactKey: string
    /// The lifecycle status the artifact holds afterwards.
    Status: string
    /// Every attachment the artifact now holds, by digest, ordinally
    /// sorted. **The citation set**: a grounding certificate naming this
    /// artifact resolves against exactly these, entirely data-side.
    AttachmentHashes: string list
    /// The acceptance signature's detached JWS over the canonical
    /// promotion input. `""` when the data host composed no signer — an
    /// absence stated rather than inferred.
    DetachedJws: string
    SigningKeyId: string
    /// Origin-relative path the public verification key is served from, so
    /// a third party that trusts neither deployment can verify offline.
    SigningKeyUrl: string
    /// `sha256:<lowercase hex>` over the exact bytes signed.
    SignedInputHash: string
    /// `"peer"` on this seam, carried for the reason §5.7.11 gives: the
    /// record the builder holds and the record the data host's own trail
    /// holds are then the same document.
    Channel: string
    /// `"peer"` on this seam.
    AuthorKind: string
    /// `{peerId}/{actorId}` — both identities.
    AuthorId: string
    /// The data host already held exactly this; nothing was written. A
    /// replay is an ACCEPTANCE, not a refusal — which is what "idempotent"
    /// has to mean for a sender that cannot tell whether its last attempt
    /// arrived.
    Replayed: bool
    RecordedAt: DateTimeOffset
    /// The artifact version the transfer left behind.
    Version: int
}

[<RequireQualifiedAccess>]
module PeerPromotion =

    /// The stable refusal classes (§7.2) of the promotion family.
    ///
    /// Three, and each names something only a TRANSFER can get wrong. The
    /// lifecycle refusals a transfer can also earn — an impossible edge, an
    /// insufficient grant — keep §5.7.11's classes unchanged, because the
    /// judgment is §5.7.11's judgment: one graph, one order, one
    /// vocabulary, whether the artifact arrived with the request or was
    /// already here.
    [<Literal>]
    let HashMismatchClass = "model-execution-promotion-hash-mismatch"

    [<Literal>]
    let CapExceededClass = "model-execution-promotion-cap-exceeded"

    [<Literal>]
    let ConflictClass = "model-execution-promotion-conflict"

    /// A signer was composed data-side and could not sign. Not a document
    /// property, so no corpus reject vector can be built for it from a
    /// document alone — enumerated here because a caller still has to
    /// enumerate it, and it is the one class in the family whose remedy is
    /// entirely the receiver's.
    [<Literal>]
    let SigningFailedClass = "model-execution-promotion-signing-failed"

    /// The refusal class of one promotion judgment.
    let className (refusal: ModelPromotionRefusal) : string =
        match refusal with
        | ModelPromotionRefusal.AttachmentRefused(_, ProvenanceAttachmentRefusal.HashMismatch _) -> HashMismatchClass
        | ModelPromotionRefusal.AttachmentRefused(_, ProvenanceAttachmentRefusal.CapExceeded _) -> CapExceededClass
        | ModelPromotionRefusal.PayloadConflict _ -> ConflictClass
        | ModelPromotionRefusal.SigningFailed _ -> SigningFailedClass
        // The lifecycle half keeps §5.7.11's vocabulary. A caller that
        // already handles a transition refusal handles this one, and an
        // operator reading a log sees one authority class rather than two
        // spellings of one decision.
        | ModelPromotionRefusal.TransitionRefused inner -> PeerTransition.className inner

    /// Read the transfer's target label. `None` for a label naming no
    /// status this profile defines, including `null` and the empty string
    /// — refused as an unreadable request, never as an illegal edge.
    let target (transfer: PeerPromotionTransfer) : ModelArtifactStatus option =
        if isNull (box transfer.Target) then
            None
        else
            ModelArtifactStatus.parse transfer.Target

    /// The ordinal comparison every sort in this file uses. Named so no
    /// call site reaches for a culture-sensitive comparison, which would
    /// make a document's bytes depend on the machine that produced it
    /// (§3.1 rule 10).
    let private byOrdinal (key: 'a -> string) (a: 'a) (b: 'a) = String.CompareOrdinal(key a, key b)

    /// A transfer in canonical member order: gate verdicts by name,
    /// attachments by content hash. **The emitter owns the sorts, not the
    /// caller** — two builders promoting the same artifact must produce the
    /// same document, or the transfer is not canonical in the sense §3
    /// means.
    let canonicalise (transfer: PeerPromotionTransfer) : PeerPromotionTransfer = {
        transfer with
            GateVerdicts = transfer.GateVerdicts |> List.sortWith (byOrdinal _.Name)
            Attachments = transfer.Attachments |> List.sortWith (byOrdinal _.ContentHash)
    }

    /// An attachment, projected onto the wire. The digest is COPIED from
    /// the platform value rather than recomputed, so a corrupt attachment
    /// crosses the seam as the corrupt thing it is and is refused by the
    /// receiver — recomputing here would launder exactly the failure the
    /// declared hash exists to catch.
    let toWireAttachment (attachment: ProvenanceAttachment) : PeerPromotionAttachment = {
        MediaType = attachment.MediaType
        ContentHash = attachment.ContentHash
        Content = Convert.ToBase64String(ProvenanceAttachment.bytes attachment)
    }

    /// Build a transfer from a promoted artifact, canonicalised.
    ///
    /// The spec payload rides its own member rather than as an attachment:
    /// a sender should not have to know the receiver's reserved media type
    /// to send the one payload every transfer carries. The receiver folds
    /// it into the attachment set on arrival
    /// (`ModelPromotion.arriving`), which is why it does not appear twice.
    let ofPromoted (promoted: PromotedArtifact) (actorId: string) : PeerPromotionTransfer =
        let key = promoted.Outcome.CompositeKey

        canonicalise {
            ArtifactKey = key.Hash
            SpecHash = key.SpecHash
            SpecPayload = promoted.SpecPayload
            DatasetVersion = key.DatasetVersion
            Seed = key.Seed
            ProviderId = key.ProviderId
            ProviderVersion = key.ProviderVersion
            ArtifactId = promoted.Outcome.ArtifactRef.ArtifactId
            ArtifactContentHash = promoted.Outcome.ArtifactRef.ContentHash
            ArtifactByteLength = promoted.Outcome.ArtifactRef.ByteLength
            Diagnostics = promoted.Outcome.Diagnostics
            GateVerdicts =
                promoted.Outcome.GateVerdicts
                |> List.map (fun v -> {
                    Name = v.Name
                    Threshold = v.Threshold
                    Direction = GateDirection.name v.Direction
                    Observed = v.Observed
                    Passed = v.Passed
                })
            Attachments = promoted.Attachments |> List.map toWireAttachment
            Target = ModelArtifactStatus.name promoted.Target
            ActorId = actorId
            Rationale = promoted.Rationale
        }

    /// Project a validated transfer onto the platform seam's own shape,
    /// stamping the PEER as the author.
    ///
    /// The author is built from the binding's peer id and the wire's actor
    /// claim — never from the wire alone — so a caller cannot author a
    /// promotion as somebody else by editing a document.
    ///
    /// `Error` carries a reason for a document that is well-formed JSON and
    /// still not readable as a transfer: base64 that does not decode. That
    /// is an unreadable REQUEST rather than a hash mismatch, and the
    /// distinction is real — a sender whose base64 is malformed has a
    /// different bug from one whose bytes were altered in flight.
    let toPromoted
        (peerId: string)
        (target: ModelArtifactStatus)
        (transfer: PeerPromotionTransfer)
        : Result<PromotedArtifact, string> =
        let decode (attachment: PeerPromotionAttachment) =
            try
                Ok {
                    MediaType = attachment.MediaType
                    ContentHash = attachment.ContentHash
                    Bytes =
                        Convert.FromBase64String(
                            if isNull (box attachment.Content) then
                                ""
                            else
                                attachment.Content
                        )
                }
            with _ ->
                Error $"the attachment declaring content hash '{attachment.ContentHash}' does not carry valid base64"

        let attachments =
            if isNull (box transfer.Attachments) then
                []
            else
                transfer.Attachments

        let decoded =
            attachments
            |> List.fold
                (fun acc attachment ->
                    match acc with
                    | Error _ -> acc
                    | Ok items ->
                        match decode attachment with
                        | Error reason -> Error reason
                        | Ok item -> Ok(item :: items))
                (Ok [])
            |> Result.map List.rev

        match decoded with
        | Error reason -> Error reason
        | Ok attachments ->
            let key: FitCompositeKey = {
                SpecHash = transfer.SpecHash
                DatasetVersion = transfer.DatasetVersion
                Seed = transfer.Seed
                ProviderId = transfer.ProviderId
                ProviderVersion = transfer.ProviderVersion
                // The sender's own key hash, carried verbatim. **Not
                // recomputed**, and that is the §5.7.3 opacity posture one
                // level up: the hash is over a canonical form whose spec
                // component the receiver is forbidden to re-derive, so a
                // receiver that recomputed the key would be asserting a
                // canonicalisation the two sides never agreed on. What
                // makes a wrong key harmless is that it addresses nothing
                // — every read on this profile keys on it, so a sender
                // that mints one badly finds its own artifact and nobody
                // else's.
                Hash = transfer.ArtifactKey
            }

            Ok {
                Outcome = {
                    CompositeKey = key
                    ArtifactRef = {
                        ArtifactId = transfer.ArtifactId
                        ContentHash = transfer.ArtifactContentHash
                        ByteLength = transfer.ArtifactByteLength
                    }
                    Diagnostics =
                        if isNull (box transfer.Diagnostics) then
                            Map.empty
                        else
                            transfer.Diagnostics
                    GateVerdicts =
                        (if isNull (box transfer.GateVerdicts) then
                             []
                         else
                             transfer.GateVerdicts)
                        |> List.map (fun v -> {
                            Name = v.Name
                            Threshold = v.Threshold
                            // An unrecognised direction label reads as
                            // `AtMost` — the STRICTER of the two, since a
                            // gate is a bound and a bound this receiver
                            // cannot read is not one it should widen. The
                            // verdict's `Passed` is the builder's own
                            // finding and is carried verbatim either way;
                            // the direction is what a later reader
                            // interprets the threshold under.
                            Direction = GateDirection.parse v.Direction |> Option.defaultValue GateDirection.AtMost
                            Observed = v.Observed
                            Passed = v.Passed
                        })
                    // Timing and cost are the BUILDING deployment's
                    // deterministic self-report about its own compute, and
                    // they are not part of what a data host is being asked
                    // to hold: a promoted artifact's value is its identity,
                    // its gates and its provenance. Carrying them would put
                    // two numbers on the wire that the receiver stores,
                    // never reads, and cannot verify.
                    DurationMs = 0L
                    CostUnits = 0.0
                }
                SpecPayload =
                    if isNull (box transfer.SpecPayload) then
                        ""
                    else
                        transfer.SpecPayload
                Attachments = attachments
                Target = target
                Author = PeerActor(peerId, transfer.ActorId)
                Rationale =
                    match transfer.Rationale with
                    | Some rationale when not (String.IsNullOrWhiteSpace rationale) -> Some rationale
                    | _ -> None
            }

    /// The accepted transfer's receipt, projected onto the profile's shape.
    ///
    /// The signature's three members are carried FLAT and non-optional,
    /// empty when the data host composed no signer — the posture
    /// `toWireOutcome` takes for an outcome with no retained artifact. A
    /// nested optional would make "unsigned" and "signed with nothing" two
    /// shapes a reader has to tell apart for no gain.
    let toWireRecord (record: ModelPromotionRecord) : PeerPromotionRecord = {
        ArtifactKey = record.ArtifactKey
        Status = record.Status
        AttachmentHashes =
            record.AttachmentHashes
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        DetachedJws = record.Signature |> Option.map _.DetachedJws |> Option.defaultValue ""
        SigningKeyId = record.Signature |> Option.map _.SigningKeyId |> Option.defaultValue ""
        SigningKeyUrl = record.Signature |> Option.map _.SigningKeyUrl |> Option.defaultValue ""
        SignedInputHash = record.Signature |> Option.map _.SignedInputHash |> Option.defaultValue ""
        Channel = record.Channel
        AuthorKind = record.AuthorKind
        AuthorId = record.AuthorId
        Replayed = record.Replayed
        RecordedAt = record.RecordedAt
        Version = record.Version
    }