// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text

// ─── Phase 646 — promotion transfer, the author-agnostic seam ────────
//
// In the two-instance topology this profile exists to serve, the builder
// must be **dispensable after promotion**: once a model is published from
// the data host, nothing that host answers may dereference the deployment
// that fitted it. Phase 644 moved the lifecycle JUDGMENT across the seam;
// what it did not move is the evidence. A peer could approve an artifact
// the data host already held, and could not hand it one.
//
// This is the act that does: a final artifact, its opaque spec payload and
// its provenance attachments landing in the data host's registry as ONE
// recorded transfer, at a declared lifecycle status, signed on acceptance.
//
// ── One judge, again ─────────────────────────────────────────────────
//
// The lifecycle half of a transfer is not a second state machine. It is
// `ModelTransition`'s graph, `ModelTransition`'s order, and
// `ModelTransition`'s refusal vocabulary — reached through the same pure
// `judge`, so a promotion and a bare transition cannot come to disagree
// about whether a peer may approve something. What this seam adds in front
// of that is the three questions a transfer raises and a transition does
// not: did the bytes survive, do they fit, and is this the same artifact
// the key already names.
//
// ── The order of judgment, and why it is this order ──────────────────
//
//   1. **Integrity.** Every attachment's declared digest recomputes over
//      the bytes that arrived. Checked first because a payload that did
//      not survive transport is not a question about size, authority or
//      identity — and answering it as one of those sends the sender to fix
//      the wrong thing.
//   2. **Cap.** The set, merged with whatever the artifact already holds,
//      is inside the receiver's DECLARED bound.
//   3. **Identity.** A composite key this scope already holds must be held
//      with the same content. Two different artifacts under one key is the
//      one condition that would make every downstream citation ambiguous,
//      so it is refused rather than reconciled.
//   4. **The edge**, then **5. the grant** — Phase 644's order, unchanged
//      and for its reasons: no grant makes an impossible edge possible.
//
// ── Signed BEFORE anything is written ────────────────────────────────
//
// The signature is minted over a canonical input derived entirely from
// values the transfer already carries, so it can be — and is — produced
// before the first store write. A signer having a bad day therefore
// refuses the transfer with nothing half-landed, rather than leaving the
// data host holding an artifact it cannot attest to. The alternative
// ordering (write, then sign, then swallow a signing failure) produces
// exactly the artifact this phase exists to prevent: one whose provenance
// resolves data-side and whose acceptance nobody can verify.
//
// ── Cost when unused (GP 13) ─────────────────────────────────────────
//
// Nothing here is composed by default. A deployment that supplies no
// `ModelPromotionDeps` admits no transfer, constructs none of these types
// and mints no signature.

/// The neutral acceptance-signing seam.
///
/// **`ToolUp.Platform.Server` carries no signer**, and cannot: the
/// `ToolUp.ArtefactSigning` companion already references this project, so
/// the dependency cannot run the other way (GP 1). This is the
/// type-neutral interface a promoted artifact is signed through — raw
/// bytes in, the detached-JWS triple out — filled by that companion's
/// `PromotedArtifactBundle.adapter` over an `IArtefactSigner`, and
/// registered by the deployment exactly as `IExportEnvelopeSigner` is.
///
/// Six portability rules (GP 12): identity by value (`byte[]` in, a record
/// out), async at the boundary, retry as data (`Result.Error string`),
/// stateless between calls.
type IPromotedArtifactSigner =
    /// Sign the canonical acceptance input. The returned signature carries
    /// the detached JWS, the key id, the public-key URL, and the digest of
    /// the exact bytes signed.
    abstract SignArtifact: signingInput: byte[] -> Async<Result<ModelArtifactSignature, string>>

/// A promoted artifact as it reaches the seam: the fit's own outcome, the
/// opaque spec payload it was fit from, the provenance records that came
/// with it, and the lifecycle status it is asked to hold.
///
/// **The spec payload is a member here and an ATTACHMENT in the store**,
/// under `ProvenanceAttachment.SpecPayloadMediaType`. It arrives named
/// because a sender should not have to know a receiver's reserved media
/// type to send the one payload every transfer carries; it is stored as an
/// attachment because it is opaque bytes with a digest, which is what that
/// slot holds, and two append-only stores to keep honest is one more than
/// the problem needs.
type PromotedArtifact = {
    /// The fit's outcome — composite key, artifact reference, diagnostics
    /// and gate verdicts, exactly as the building deployment recorded them.
    Outcome: FitOutcome
    /// The opaque provider spec. Carried verbatim and never re-hashed
    /// against `CompositeKey.SpecHash`: the spec hash is the SUBMITTER's,
    /// stored and keyed verbatim by the Phase 603 opacity contract, and
    /// re-deriving it here would be this deployment asserting a
    /// canonicalisation rule the two sides never agreed on.
    SpecPayload: string
    /// The opaque provenance records, each with its own declared digest.
    Attachments: ProvenanceAttachment list
    /// The lifecycle status the artifact is asked to hold once landed.
    Target: ModelArtifactStatus
    /// Who authored the transfer — a peer actor across the seam, a local
    /// user, or a policy. The same DU the transition seam judges by.
    Author: ModelTransitionAuthor
    /// The author's stated reason. `None` is ordinary.
    Rationale: string option
}

/// Why a promotion transfer was refused.
///
/// Five cases, and each names a different thing the sender would have to
/// change: the bytes, the size, the key, the lifecycle request, or this
/// deployment's own signing arrangements. The lifecycle case nests
/// `ModelTransitionRefusal` unchanged rather than restating it — the
/// judgment IS that one, and a second vocabulary for it would be a second
/// thing to keep in step with the graph.
[<RequireQualifiedAccess>]
type ModelPromotionRefusal =
    /// An attachment's declared digest disagrees with a recomputation, or
    /// the merged set exceeds the receiver's declared cap.
    | AttachmentRefused of artifactKey: string * refusal: ProvenanceAttachmentRefusal
    /// The composite key is already held here with different content.
    /// `field` names what differs, so the sender can tell a re-send of a
    /// changed artifact from a key collision.
    | PayloadConflict of artifactKey: string * field: string
    /// The lifecycle judgment refused: unknown edge or insufficient grant,
    /// in the transition seam's own vocabulary.
    | TransitionRefused of refusal: ModelTransitionRefusal
    /// A signer is composed and could not sign. The transfer is refused
    /// with nothing written — see this file's header on why the signature
    /// is minted before the first store write.
    | SigningFailed of artifactKey: string * reason: string

[<RequireQualifiedAccess>]
module ModelPromotionRefusal =

    /// Human-readable one-line description (logs, operator display, the
    /// audited row). The CASE is the contract; this wording is not.
    let describe (refusal: ModelPromotionRefusal) : string =
        match refusal with
        | ModelPromotionRefusal.AttachmentRefused(artifactKey, inner) ->
            $"the promotion of '{artifactKey}' was refused: {ProvenanceAttachmentRefusal.describe inner}"
        | ModelPromotionRefusal.PayloadConflict(artifactKey, field) ->
            $"'{artifactKey}' is already held here with a different '{field}'; a composite key names one artifact"
        | ModelPromotionRefusal.TransitionRefused inner -> ModelTransitionRefusal.describe inner
        | ModelPromotionRefusal.SigningFailed(artifactKey, reason) ->
            $"the promotion of '{artifactKey}' could not be signed on acceptance: {reason}"

/// What an admitted transfer will DO — the plan `judge` produces and
/// `accept` executes.
///
/// A value rather than a sequence of decisions taken inside the effectful
/// path, so the whole judgment is testable, loggable and certifiable
/// without a store: everything that varies between a first acceptance, an
/// idempotent replay and a partial re-send is visible here.
type ModelPromotionPlan = {
    /// The scope does not hold this key yet; the transfer registers it.
    Registers: bool
    /// The attachments this transfer adds — those the artifact does not
    /// already hold, in arrival order.
    NewAttachments: ProvenanceAttachment list
    /// The lifecycle move, when the artifact does not already hold the
    /// target. `None` when it does, which is not a refusal: a re-sent
    /// promotion of an already-approved artifact is a replay, and reporting
    /// it as an illegal self-transition would make idempotence impossible
    /// to express.
    Transition: ModelArtifactStatus option
    /// This deployment already holds exactly what the transfer carries, at
    /// the target status. Nothing will be written.
    Replay: bool
}

/// The attributed record an admitted transfer produced.
type ModelPromotionRecord = {
    ArtifactKey: string
    /// The lifecycle status the artifact holds afterwards.
    Status: string
    /// Every attachment the artifact now holds, by digest, ordinally
    /// sorted — the citation set a grounding certificate resolves against.
    AttachmentHashes: string list
    /// The acceptance signature, when a signer was composed.
    Signature: ModelArtifactSignature option
    /// `"local"` or `"peer"` — `ModelTransitionChannel.label`.
    Channel: string
    /// `"user"` / `"peer"` / `"policy"`.
    AuthorKind: string
    AuthorId: string
    /// Nothing was written: the identical transfer was already held.
    Replayed: bool
    RecordedAt: DateTimeOffset
    /// The artifact version the transfer left behind.
    Version: int
}

/// The substrate a transfer runs over.
type ModelPromotionDeps = {
    /// The registry, audit log and clock — reused rather than restated, so
    /// a deployment cannot compose a transfer seam against one registry and
    /// a transition seam against another.
    Transition: ModelTransitionDeps
    /// `None` on a deployment that composed no signer: the transfer still
    /// lands and the artifact carries no acceptance signature. An honest
    /// absence rather than a silent one — the record says so, and so does
    /// the audited row.
    Signer: IPromotedArtifactSigner option
}

/// The canonical bytes a promoted artifact is signed over.
[<RequireQualifiedAccess>]
module ModelPromotionSigningInput =

    /// The canonical, order-fixed string form.
    ///
    /// **Stable — the signature is over exactly this shape, so do not
    /// reorder.** Same construction as `FitCompositeKey.canonical`, for the
    /// same reason: a verifier in another language must be able to rebuild
    /// the bytes from published values without reading this code.
    ///
    /// It states what the deployment is actually attesting to: that THIS
    /// artifact, fit from THIS spec against THIS vintage, was accepted here
    /// at THIS status carrying THESE attachments. The attachment digests
    /// are ordinally sorted because a set has no order and a signature over
    /// an arrival-ordered list would depend on which sender sent it.
    let canonical (key: FitCompositeKey) (status: ModelArtifactStatus) (attachmentHashes: string seq) : string =
        let attachments =
            attachmentHashes
            |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> Seq.map (sprintf "|attachment=%s")
            |> String.concat ""

        sprintf
            "fuaran.federation.promoted-artifact/1|key=%s|spec=%s|dataset=%s|seed=%d|provider=%s|pver=%s|status=%s%s"
            key.Hash
            key.SpecHash
            key.DatasetVersion
            key.Seed
            key.ProviderId
            key.ProviderVersion
            (ModelArtifactStatus.name status)
            attachments

    /// The UTF-8 bytes of the canonical form — what the signer signs.
    let bytes (key: FitCompositeKey) (status: ModelArtifactStatus) (attachmentHashes: string seq) : byte[] =
        Encoding.UTF8.GetBytes(canonical key status attachmentHashes)

/// The one entry point a promotion transfer takes.
[<RequireQualifiedAccess>]
module ModelPromotion =

    /// The spec payload as the attachment it is stored as.
    let specAttachment (promoted: PromotedArtifact) : ProvenanceAttachment =
        ProvenanceAttachment.ofText ProvenanceAttachment.SpecPayloadMediaType promoted.SpecPayload

    /// Everything the transfer offers the attachment slot: its declared
    /// attachments plus the spec payload, de-duplicated by digest.
    ///
    /// The spec goes LAST so a sender that also sent it explicitly keeps
    /// its own copy's position — the two are the same attachment by hash,
    /// and de-duplication makes the second a no-op either way.
    let arriving (promoted: PromotedArtifact) : ProvenanceAttachment list =
        let declared =
            if isNull (box promoted.Attachments) then
                []
            else
                promoted.Attachments

        ProvenanceAttachments.append declared [ specAttachment promoted ]

    /// Judge one transfer against what this scope already holds, the
    /// receiver's declared cap and the author's declared grant.
    ///
    /// **Pure and total over its inputs** — no store read, no clock, no
    /// signer — for the reason `ModelTransition.judge` is: a conformance
    /// corpus certifies a refusal vector against the SHIPPED function
    /// rather than a harness's reconstruction, and a locally-authored
    /// transfer and a peer's are judged identically. `existing` is `None`
    /// for a key this scope does not hold.
    ///
    /// The order is stated in this file's header. In one line: bytes, then
    /// size, then identity, then the lifecycle — each refused before the
    /// next is asked, so the one thing a sender is told is the first thing
    /// it would have to change.
    let judge
        (existing: ModelArtifact option)
        (limits: ProvenanceAttachmentLimits)
        (authority: ModelTransitionAuthority)
        (promoted: PromotedArtifact)
        : Result<ModelPromotionPlan, ModelPromotionRefusal> =
        let artifactKey = promoted.Outcome.CompositeKey.Hash
        let offered = arriving promoted

        let held = existing |> Option.map _.Attachments |> Option.defaultValue []

        match ProvenanceAttachments.validate limits held offered with
        | Error refusal -> Error(ModelPromotionRefusal.AttachmentRefused(artifactKey, refusal))
        | Ok() ->
            // Identity. A composite key names ONE artifact: it is the hash
            // of the spec, vintage, seed and provider that produced it, so
            // two transfers under one key that disagree about the fitted
            // parameters disagree about something the key asserts they
            // share. Reconciling that quietly would leave every downstream
            // citation ambiguous about which one it meant.
            let conflict =
                match existing with
                | None -> None
                | Some current ->
                    if
                        not (
                            String.Equals(
                                current.ArtifactRef.ContentHash,
                                promoted.Outcome.ArtifactRef.ContentHash,
                                StringComparison.Ordinal
                            )
                        )
                    then
                        Some "ArtifactRef.ContentHash"
                    elif
                        not (
                            String.Equals(
                                current.CompositeKey.SpecHash,
                                promoted.Outcome.CompositeKey.SpecHash,
                                StringComparison.Ordinal
                            )
                        )
                    then
                        Some "SpecHash"
                    elif
                        not (
                            String.Equals(
                                current.CompositeKey.DatasetVersion,
                                promoted.Outcome.CompositeKey.DatasetVersion,
                                StringComparison.Ordinal
                            )
                        )
                    then
                        Some "DatasetVersion"
                    else
                        None

            match conflict with
            | Some field -> Error(ModelPromotionRefusal.PayloadConflict(artifactKey, field))
            | None ->
                // The status the artifact holds, or would hold the moment
                // it is registered. Registration mints `Fitted`, so a
                // transfer of a brand-new artifact to `Approved` is judged
                // as the `Fitted -> Approved` edge it will actually take.
                let current =
                    match existing with
                    | Some artifact -> artifact.Status
                    | None -> ModelArtifactStatus.initial

                let request: ModelTransitionRequest = {
                    ArtifactKey = artifactKey
                    Target = promoted.Target
                    Author = promoted.Author
                    Rationale = promoted.Rationale
                }

                // A transfer whose target is the status the artifact
                // already holds moves nothing, so the lifecycle GRAPH has
                // nothing to say about it — but the GRANT still does. An
                // author that may not approve may not land an artifact
                // already approved either, or "promote to a status it is
                // already in" would be an authority-free write path around
                // the whole gate.
                let lifecycle =
                    if current = promoted.Target then
                        if ModelTransitionAuthority.admits promoted.Target authority then
                            Ok None
                        else
                            Error(
                                ModelTransitionRefusal.InsufficientAuthority(
                                    artifactKey,
                                    ModelArtifactStatus.name promoted.Target,
                                    ModelTransitionAuthor.id promoted.Author
                                )
                            )
                    else
                        ModelTransition.judge (Some current) authority request
                        |> Result.map (fun _ -> Some promoted.Target)

                match lifecycle with
                | Error refusal -> Error(ModelPromotionRefusal.TransitionRefused refusal)
                | Ok transition ->
                    let novel = ProvenanceAttachments.novel held offered

                    Ok {
                        Registers = Option.isNone existing
                        NewAttachments = novel
                        Transition = transition
                        Replay =
                            Option.isSome existing
                            && List.isEmpty novel
                            && Option.isNone transition
                            && (existing |> Option.bind _.Signature |> Option.isSome)
                    }

    /// Record the judged transfer on the attributed trail. Written for an
    /// accepted AND a refused transfer, so "which peer tried to promote
    /// what" is answerable without joining to a registry row a refused
    /// transfer never produced.
    ///
    /// Best-effort in the sense `IAuditLog.Record` already is: the
    /// registry's own writes are the primary effect and an audit store
    /// having a bad day must not change the outcome a caller is told.
    let private record
        (deps: ModelPromotionDeps)
        (scopeId: string)
        (promoted: PromotedArtifact)
        (signature: ModelArtifactSignature option)
        (outcome: Result<ModelPromotionRecord, ModelPromotionRefusal>)
        : Async<unit> =
        async {
            try
                do!
                    deps.Transition.Audit.Record(
                        scopeId,
                        ModelArtifactPromoted {
                            CompositeKeyHash = promoted.Outcome.CompositeKey.Hash
                            TargetStatus = ModelArtifactStatus.name promoted.Target
                            Channel = ModelTransitionChannel.label (ModelTransitionAuthor.channel promoted.Author)
                            AuthorKind = ModelTransitionAuthor.kind promoted.Author
                            AuthorId = ModelTransitionAuthor.id promoted.Author
                            AttachmentHashes = arriving promoted |> List.map _.ContentHash
                            SigningKeyId = signature |> Option.map _.SigningKeyId |> Option.defaultValue ""
                            Accepted = Result.isOk outcome
                            Replayed =
                                match outcome with
                                | Ok record -> record.Replayed
                                | Error _ -> false
                            Refusal =
                                match outcome with
                                | Ok _ -> ""
                                | Error refusal -> ModelPromotionRefusal.describe refusal
                            ScopeId = scopeId
                        }
                    )
            with _ ->
                ()
        }

    /// Judge and (when admitted) execute one promotion transfer.
    ///
    /// **`authority` is supplied by the caller rather than read from the
    /// author**, exactly as `ModelTransition.invoke` requires it: an author
    /// that carried its own grant would be an author that decided its own
    /// authority. The receiving surface resolves it from ITS OWN
    /// declarations — a peer's from its binding — and hands it in.
    ///
    /// The effect order is judge → sign → register → attach → transition.
    /// Signing sits before the first write for the reason in this file's
    /// header; the transition is last because it is the only step whose
    /// meaning depends on the artifact already being here.
    let accept
        (deps: ModelPromotionDeps)
        (scopeId: string)
        (authority: ModelTransitionAuthority)
        (promoted: PromotedArtifact)
        : Async<Result<ModelPromotionRecord, ModelPromotionRefusal>> =
        async {
            let registry = deps.Transition.Registry
            let artifactKey = promoted.Outcome.CompositeKey.Hash

            // A registry error here reads as "this scope holds no such
            // artifact", the posture `ModelTransition.invoke` takes: the
            // one input every later check needs is the current record, and
            // reporting this deployment's disk to a counterparty as a
            // distinct refusal class would put a case on a closed wire
            // vocabulary to describe something the caller cannot act on.
            let! existing = async {
                match! registry.Get(scopeId, artifactKey) with
                | Error _ -> return None
                | Ok artifact -> return Some artifact
            }

            match judge existing registry.AttachmentLimits authority promoted with
            | Error refusal ->
                do! record deps scopeId promoted None (Error refusal)
                return Error refusal
            // **A replay writes NOTHING, and returning early is what makes
            // that true rather than merely intended.** The plan already
            // says there is no attachment to append and no status to
            // move; falling through would still hand the registry a
            // signature to record, and recording one appends a version
            // (GP 5). A sender that cannot tell whether its last attempt
            // arrived would then advance the artifact's version on every
            // retry — an idempotent operation whose only observable
            // effect is a growing version history is not idempotent, and
            // the version history is exactly where an investigation
            // counts acceptances.
            | Ok plan when plan.Replay ->
                let held = Option.get existing

                let recorded = {
                    ArtifactKey = artifactKey
                    Status = ModelArtifactStatus.name held.Status
                    AttachmentHashes =
                        held.Attachments
                        |> List.map _.ContentHash
                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                    Signature = held.Signature
                    Channel = ModelTransitionChannel.label (ModelTransitionAuthor.channel promoted.Author)
                    AuthorKind = ModelTransitionAuthor.kind promoted.Author
                    AuthorId = ModelTransitionAuthor.id promoted.Author
                    Replayed = true
                    RecordedAt = deps.Transition.Now()
                    Version = held.Version
                }

                do! record deps scopeId promoted held.Signature (Ok recorded)
                return Ok recorded
            | Ok plan ->
                let heldHashes =
                    existing
                    |> Option.map (fun a -> a.Attachments |> List.map _.ContentHash)
                    |> Option.defaultValue []

                let finalHashes = heldHashes @ (plan.NewAttachments |> List.map _.ContentHash)

                let finalStatus =
                    plan.Transition
                    |> Option.defaultValue (
                        existing
                        |> Option.map _.Status
                        |> Option.defaultValue ModelArtifactStatus.initial
                    )

                let! signed = async {
                    match deps.Signer with
                    | None -> return Ok None
                    | Some signer ->
                        let input =
                            ModelPromotionSigningInput.bytes promoted.Outcome.CompositeKey finalStatus finalHashes

                        match! signer.SignArtifact input with
                        | Error reason -> return Error reason
                        | Ok signature -> return Ok(Some signature)
                }

                match signed with
                | Error reason ->
                    let refusal = ModelPromotionRefusal.SigningFailed(artifactKey, reason)
                    do! record deps scopeId promoted None (Error refusal)
                    return Error refusal
                | Ok signature ->
                    // Registration is idempotent under the composite key
                    // (Phase 453): a key already held returns the held
                    // artifact unchanged, with no new version and no audit
                    // row. That is the shipped behaviour this seam's
                    // idempotence rests on rather than re-implements.
                    let outcomeToRegister = promoted.Outcome

                    match!
                        registry.Register(
                            scopeId,
                            outcomeToRegister,
                            ModelTransitionAuthor.id promoted.Author,
                            Map.empty,
                            ""
                        )
                    with
                    | Error error ->
                        let refusal =
                            match error with
                            | ModelRegistryError.AttachmentRefused inner ->
                                ModelPromotionRefusal.AttachmentRefused(artifactKey, inner)
                            | _ -> ModelPromotionRefusal.PayloadConflict(artifactKey, ModelRegistryError.describe error)

                        do! record deps scopeId promoted signature (Error refusal)
                        return Error refusal
                    | Ok _ ->
                        match! registry.AttachProvenance(scopeId, artifactKey, plan.NewAttachments, signature) with
                        | Error error ->
                            let refusal =
                                match error with
                                | ModelRegistryError.AttachmentRefused inner ->
                                    ModelPromotionRefusal.AttachmentRefused(artifactKey, inner)
                                | _ ->
                                    ModelPromotionRefusal.PayloadConflict(
                                        artifactKey,
                                        ModelRegistryError.describe error
                                    )

                            do! record deps scopeId promoted signature (Error refusal)
                            return Error refusal
                        | Ok attached ->
                            // The lifecycle move goes through the shared
                            // seam, which re-judges it against the same
                            // grant and writes its own attributed row. The
                            // duplication is deliberate: this seam's judge
                            // is pure and answers before any write, and
                            // that one is the authority of record — two
                            // readings of one graph, never two graphs.
                            let! moved = async {
                                match plan.Transition with
                                | None -> return Ok attached
                                | Some target ->
                                    let request: ModelTransitionRequest = {
                                        ArtifactKey = artifactKey
                                        Target = target
                                        Author = promoted.Author
                                        Rationale = promoted.Rationale
                                    }

                                    match! ModelTransition.invoke deps.Transition scopeId authority request with
                                    | Error refusal -> return Error refusal
                                    | Ok _ ->
                                        match! registry.Get(scopeId, artifactKey) with
                                        | Ok artifact -> return Ok artifact
                                        | Error _ -> return Ok attached
                            }

                            match moved with
                            | Error refusal ->
                                let refusal = ModelPromotionRefusal.TransitionRefused refusal
                                do! record deps scopeId promoted signature (Error refusal)
                                return Error refusal
                            | Ok final ->
                                let recorded = {
                                    ArtifactKey = artifactKey
                                    Status = ModelArtifactStatus.name final.Status
                                    AttachmentHashes =
                                        final.Attachments
                                        |> List.map _.ContentHash
                                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                                    Signature = final.Signature
                                    Channel =
                                        ModelTransitionChannel.label (ModelTransitionAuthor.channel promoted.Author)
                                    AuthorKind = ModelTransitionAuthor.kind promoted.Author
                                    AuthorId = ModelTransitionAuthor.id promoted.Author
                                    Replayed = plan.Replay
                                    RecordedAt = deps.Transition.Now()
                                    Version = final.Version
                                }

                                do! record deps scopeId promoted signature (Ok recorded)
                                return Ok recorded
        }