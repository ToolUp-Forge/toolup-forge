// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text

// ─── Phase 453 — IModelRegistry core types ──────────────────────────────
//
// A fitted model as a provenance-complete, lifecycle-governed platform
// artifact (statistical-modelling substrate plan, Stage 4). Identity is the
// Phase 449 composite key `(specHash, datasetVersion, seed, providerId,
// providerVersion)` (plan D5) — two fits differing in any component are
// different artifacts, which is what makes branch × vintage evidence bases
// queryable. The registry manages the artifact's *lifecycle* + *provenance*;
// it is deliberately **distinct** from `IResultStore` (module-output
// caching — plan D11) and stores no module outputs.
//
// **Why server-only, not Core/Shared.** A `ModelArtifact` embeds the fit
// envelope's `FitCompositeKey` + `GateVerdict` (Phase 449, `Server/`) — those
// are SHA-256-addressed server-side compute types (`System.Security.
// Cryptography` is not Fable-compilable), so the artifact model is forced
// server-side too. The audit payloads that cross into persisted
// `ModuleEvent`s stay in `Core/Shared/AuditTypes.fs`; they carry only the
// composite-key strings the registry computes here. (Registry *query
// results* becoming a client surface is plan risk #8 — a later Fable-facing
// projection, not this phase.)
//
// **Immutability (GP 5, plan D5).** An artifact's *identity* (the composite
// key), its fit-derived core (diagnostics, gate verdicts, the opaque
// parameter-blob reference), and its registration provenance are fixed at
// registration and copied verbatim into every subsequent version. The only
// fields a lifecycle transition changes are `Status` (and the transition is
// recorded as a new immutable version via `StrictlyVersioned` storage) — so
// "lifecycle is a status, not a mutation" is enforced structurally, not by
// convention. The parameter blob itself lives in `IDataObjectStore` (plan
// D5), inheriting content dedup + version immutability rather than
// re-implementing them.

/// The governed lifecycle state of a `ModelArtifact` (plan Stage 4). A
/// forward lifecycle: an artifact is born `Fitted` (registration carries a
/// completed `FitOutcome`), may be `Approved` (a gated governance decision,
/// Owner/Admin only — GP 4), and may be `Retired` from any active state.
/// `Draft` is the pre-fit reservation state the DU carries for completeness;
/// registration-from-a-fit never mints it. `Retired` is terminal.
[<RequireQualifiedAccess>]
type ModelArtifactStatus =
    /// Reserved but not yet fit — a pre-fit placeholder. Registration from a
    /// `FitOutcome` never produces this; it exists so the lifecycle DU
    /// matches the plan's four states.
    | Draft
    /// Registered from a completed fit: diagnostics + gate verdicts attached.
    /// The birth status of every registered artifact.
    | Fitted
    /// Promoted to an approved evidence base by an Owner/Admin (GP 4). The
    /// only transition that requires an elevated role.
    | Approved
    /// Retired from active use. Terminal — no transition leaves `Retired`.
    | Retired

module ModelArtifactStatus =
    /// Stable case-name string (audit payloads, metadata sidecar, telemetry).
    /// Round-trips through `parse`; do not rename without a wire-format bump.
    let name =
        function
        | ModelArtifactStatus.Draft -> "Draft"
        | ModelArtifactStatus.Fitted -> "Fitted"
        | ModelArtifactStatus.Approved -> "Approved"
        | ModelArtifactStatus.Retired -> "Retired"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "Draft" -> Some ModelArtifactStatus.Draft
        | "Fitted" -> Some ModelArtifactStatus.Fitted
        | "Approved" -> Some ModelArtifactStatus.Approved
        | "Retired" -> Some ModelArtifactStatus.Retired
        | _ -> None

    /// The status a fit-outcome registration mints. Every artifact is born
    /// `Fitted` — it carries a completed fit's diagnostics + gate verdicts.
    let initial = ModelArtifactStatus.Fitted

    /// Whether transitioning **into** `target` requires an elevated
    /// (Owner/Admin) role. Only `Approved` does (plan Stage 4 / GP 4); every
    /// other legal transition is available to any team member the storage
    /// scope already admits. Kept as data so the gate is one predicate, not
    /// scattered role matches.
    let requiresElevatedRole (target: ModelArtifactStatus) : bool =
        match target with
        | ModelArtifactStatus.Approved -> true
        | ModelArtifactStatus.Draft
        | ModelArtifactStatus.Fitted
        | ModelArtifactStatus.Retired -> false

    /// Whether `from -> target` is a legal lifecycle edge. The forward
    /// lifecycle:
    ///   Draft    → Fitted | Retired
    ///   Fitted   → Approved | Retired
    ///   Approved → Retired
    ///   Retired  → (terminal)
    /// A self-transition (`from = target`) is never legal — a transition is a
    /// state *change*. Illegality is a typed `ModelRegistryError`, never an
    /// exception.
    let canTransition (from: ModelArtifactStatus) (target: ModelArtifactStatus) : bool =
        match from, target with
        | ModelArtifactStatus.Draft, ModelArtifactStatus.Fitted
        | ModelArtifactStatus.Draft, ModelArtifactStatus.Retired
        | ModelArtifactStatus.Fitted, ModelArtifactStatus.Approved
        | ModelArtifactStatus.Fitted, ModelArtifactStatus.Retired
        | ModelArtifactStatus.Approved, ModelArtifactStatus.Retired -> true
        | _ -> false

// ─── Phase 646 — opaque provenance attachments ──────────────────────────
//
// In a two-instance topology the builder must be **dispensable after
// promotion**: anything published from the data host must never dereference
// a retired builder. What the data host is missing today is the evidence
// that sat beside the fit — the exploration record a modelling tool kept,
// the assembly spec the vintage was built from — because none of it has a
// slot on the artifact.
//
// **The slot is opaque by construction, and that is the cross-pillar rule
// rather than a convenience.** Forge never types another pillar's tree. An
// exploration-path record crosses as `(mediaType, contentHash, bytes)`, is
// stored verbatim, is cited by hash, and is never parsed — exactly the
// posture Phase 603 fixed for the spec payload, applied to everything else
// a promotion carries. A slot that understood its contents would make every
// producing tool's schema a forge dependency, and the schema of a
// modelling tool is not something a governance substrate has any business
// tracking.
//
// Three properties the slot is required to hold, and each is enforced
// rather than documented:
//
//   * **Append-only.** An attachment set only grows. Provenance that could
//     be edited after the fact is not provenance, and the artifact's whole
//     immutability argument (GP 5) would have a hole in exactly the place
//     an investigation looks.
//   * **Hash-verified on read.** Every read recomputes the digest over the
//     bytes it materialised. A stored attachment whose bytes no longer hash
//     to their declared digest is refused, not returned — a citation is
//     only worth anything if the thing cited is the thing that was
//     attached.
//   * **Size-bounded with a DECLARED cap.** The registry publishes its cap
//     (`IModelRegistry.AttachmentLimits`), so a caller can refuse early and
//     a counterparty can size a transfer before sending it. An undeclared
//     bound is one a caller discovers by hitting it.

/// One opaque provenance attachment on a registry artifact: a declared
/// media type, the digest of its bytes, and the bytes.
///
/// **`MediaType` is a label, not an instruction.** Forge records it so a
/// reader knows what tool to open the bytes with; nothing in forge selects
/// behaviour on it, and an unrecognised value is ordinary rather than an
/// error. Same posture as `ModelSpecRef.SpecHashAlgorithm` — carried
/// verbatim, never acted on.
type ProvenanceAttachment = {
    /// IANA-shaped media type of `Bytes` (e.g. `application/json`,
    /// `application/vnd.toolup.model-spec`). Recorded, never interpreted.
    MediaType: string
    /// `sha256:<lowercase hex>` over `Bytes`. The attachment's identity
    /// within an artifact — two attachments with equal bytes are one
    /// attachment, whoever sent them.
    ContentHash: string
    /// The opaque payload. Forge stores and hashes it; nothing in forge
    /// reads it.
    Bytes: byte[]
}

module ProvenanceAttachment =
    /// The digest prefix every attachment hash carries. Named so no call
    /// site spells it, and so a later algorithm is a new prefix rather
    /// than an ambiguous hex string.
    [<Literal>]
    let HashPrefix = "sha256:"

    /// The reserved media type a promoted artifact's **assembly / model
    /// spec payload** is attached under (Phase 646).
    ///
    /// A reserved label rather than a second storage mechanism: the spec
    /// payload is opaque bytes with a digest, which is precisely what this
    /// slot holds, and giving it its own field would mean two append-only
    /// stores to keep honest instead of one. Forge still never reads it —
    /// the label says which reader to hand it to, and forge is not one.
    [<Literal>]
    let SpecPayloadMediaType = "application/vnd.toolup.model-spec"

    /// The bytes an attachment carries, with a null blob read as empty. A
    /// list-or-array field absent from an older persisted record
    /// deserialises to `null` rather than to the empty array, so every
    /// read of `Bytes` goes through here.
    let bytes (attachment: ProvenanceAttachment) : byte[] =
        if isNull (box attachment.Bytes) then
            Array.empty
        else
            attachment.Bytes

    /// The canonical digest of a byte payload.
    let hashOf (payload: byte[]) : string =
        let payload = if isNull (box payload) then Array.empty else payload

        HashPrefix + (SHA256.HashData payload |> Convert.ToHexStringLower)

    /// An attachment over `payload`, with its digest computed rather than
    /// asserted. The construction route a LOCAL caller takes; a transfer
    /// across a seam carries a declared digest that the receiver checks.
    let create (mediaType: string) (payload: byte[]) : ProvenanceAttachment = {
        MediaType = mediaType
        ContentHash = hashOf payload
        Bytes = payload
    }

    /// `create` over UTF-8 text — the shape a spec payload arrives in.
    let ofText (mediaType: string) (text: string) : ProvenanceAttachment =
        create mediaType (Encoding.UTF8.GetBytes(if isNull (box text) then "" else text))

    /// The size an attachment counts against the cap.
    let byteLength (attachment: ProvenanceAttachment) : int = (bytes attachment).Length

    /// Recompute the digest over the bytes actually held.
    let computedHash (attachment: ProvenanceAttachment) : string = hashOf (bytes attachment)

    /// Does the declared digest match a recomputation? The check every
    /// read runs and every transfer is judged by.
    let hashVerified (attachment: ProvenanceAttachment) : bool =
        not (isNull (box attachment.ContentHash))
        && String.Equals(attachment.ContentHash, computedHash attachment, StringComparison.Ordinal)

/// The size bound a registry declares over an artifact's attachment set.
///
/// Three dimensions rather than one, because they fail differently: a
/// thousand tiny records is a different operational problem from one
/// enormous one, and a total is what actually bounds a blob.
type ProvenanceAttachmentLimits = {
    /// Most attachments one artifact may hold, across every append.
    MaxAttachments: int
    /// Largest single attachment, in bytes.
    MaxAttachmentBytes: int
    /// Largest total, in bytes, across an artifact's whole set.
    MaxTotalBytes: int
}

module ProvenanceAttachmentLimits =
    /// The default cap: 32 attachments, 4 MiB each, 16 MiB in total.
    ///
    /// Chosen to be generous for the exploration records this exists to
    /// carry and firm enough that an artifact record stays a record. A
    /// deployment with different needs declares its own — the point of the
    /// cap being declared is that it is a deployment's decision.
    let default': ProvenanceAttachmentLimits = {
        MaxAttachments = 32
        MaxAttachmentBytes = 4 * 1024 * 1024
        MaxTotalBytes = 16 * 1024 * 1024
    }

/// Why an attachment set was refused.
///
/// Two facts, and they are different things for a caller to fix: the bytes
/// that arrived are not the bytes that were declared, or there are too many
/// of them. `Dimension` names which bound a cap refusal hit, because
/// "shrink the payload" and "send fewer" are different remedies.
[<RequireQualifiedAccess>]
type ProvenanceAttachmentRefusal =
    /// A declared digest disagrees with a recomputation over the bytes.
    | HashMismatch of declared: string * computed: string
    /// The set exceeds a declared bound. `dimension` is `"count"`,
    /// `"attachment-bytes"` or `"total-bytes"`.
    | CapExceeded of dimension: string * measured: int * cap: int

module ProvenanceAttachmentRefusal =
    /// The dimension labels, held here so the judge and any operator
    /// surface cannot spell one of them differently.
    [<Literal>]
    let CountDimension = "count"

    [<Literal>]
    let AttachmentBytesDimension = "attachment-bytes"

    [<Literal>]
    let TotalBytesDimension = "total-bytes"

    /// Human-readable one-line description. The CASE is the contract; this
    /// wording is not.
    let describe (refusal: ProvenanceAttachmentRefusal) : string =
        match refusal with
        | ProvenanceAttachmentRefusal.HashMismatch(declared, computed) ->
            $"a provenance attachment declares content hash '{declared}' and its bytes hash to '{computed}'"
        | ProvenanceAttachmentRefusal.CapExceeded(dimension, measured, cap) ->
            $"the provenance attachment set exceeds this registry's declared '{dimension}' cap ({measured} > {cap})"

module ProvenanceAttachments =
    /// The set an artifact holds after appending `arriving` to `held`, in
    /// the order attachments were first seen.
    ///
    /// **Append-only, and de-duplicated by content hash.** An attachment
    /// already held is not appended again — re-sending the identical
    /// evidence is a replay, not a second attachment — and nothing held is
    /// ever dropped or replaced. That is what makes a re-sent promotion
    /// idempotent without a separate idempotency store.
    let append (held: ProvenanceAttachment list) (arriving: ProvenanceAttachment list) : ProvenanceAttachment list =
        let seen = held |> List.map _.ContentHash |> Set.ofList

        let novel =
            arriving
            |> List.fold
                (fun (acc, seen) attachment ->
                    if Set.contains attachment.ContentHash seen then
                        acc, seen
                    else
                        attachment :: acc, Set.add attachment.ContentHash seen)
                ([], seen)
            |> fst
            |> List.rev

        held @ novel

    /// The attachments in `arriving` that `held` does not already carry.
    let novel (held: ProvenanceAttachment list) (arriving: ProvenanceAttachment list) : ProvenanceAttachment list =
        let seen = held |> List.map _.ContentHash |> Set.ofList

        arriving
        |> List.fold
            (fun (acc, seen) attachment ->
                if Set.contains attachment.ContentHash seen then
                    acc, seen
                else
                    attachment :: acc, Set.add attachment.ContentHash seen)
            ([], seen)
        |> fst
        |> List.rev

    /// Every attachment's declared digest recomputes, and the resulting
    /// set is within the declared cap.
    ///
    /// **Pure and total over its inputs** — no store, no clock, nothing
    /// ambient — for the reason `ModelTransition.judge` is: a conformance
    /// corpus certifies a refusal vector against the SHIPPED function
    /// rather than against a harness's reconstruction of it, and a local
    /// append and a federated transfer are then judged identically.
    ///
    /// Integrity is checked before the cap, deliberately: a payload whose
    /// bytes did not survive transport is not a question about size, and
    /// telling a sender to send less when what it sent was corrupt would
    /// send it to fix the wrong thing.
    let validate
        (limits: ProvenanceAttachmentLimits)
        (held: ProvenanceAttachment list)
        (arriving: ProvenanceAttachment list)
        : Result<unit, ProvenanceAttachmentRefusal> =
        let mismatched = arriving |> List.tryFind (ProvenanceAttachment.hashVerified >> not)

        match mismatched with
        | Some attachment ->
            Error(
                ProvenanceAttachmentRefusal.HashMismatch(
                    (if isNull (box attachment.ContentHash) then
                         ""
                     else
                         attachment.ContentHash),
                    ProvenanceAttachment.computedHash attachment
                )
            )
        | None ->
            let oversized =
                arriving
                |> List.tryFind (fun a -> ProvenanceAttachment.byteLength a > limits.MaxAttachmentBytes)

            match oversized with
            | Some attachment ->
                Error(
                    ProvenanceAttachmentRefusal.CapExceeded(
                        ProvenanceAttachmentRefusal.AttachmentBytesDimension,
                        ProvenanceAttachment.byteLength attachment,
                        limits.MaxAttachmentBytes
                    )
                )
            | None ->
                let merged = append held arriving
                let count = List.length merged
                let total = merged |> List.sumBy ProvenanceAttachment.byteLength

                if count > limits.MaxAttachments then
                    Error(
                        ProvenanceAttachmentRefusal.CapExceeded(
                            ProvenanceAttachmentRefusal.CountDimension,
                            count,
                            limits.MaxAttachments
                        )
                    )
                elif total > limits.MaxTotalBytes then
                    Error(
                        ProvenanceAttachmentRefusal.CapExceeded(
                            ProvenanceAttachmentRefusal.TotalBytesDimension,
                            total,
                            limits.MaxTotalBytes
                        )
                    )
                else
                    Ok()

/// Phase 646 — a detached signature over a promoted artifact's canonical
/// identity, minted at the data host on acceptance.
///
/// **Detached, and over an identity rather than over the parameter blob.**
/// The blob lives in `IDataObjectStore` and may be large; what a citing
/// party needs to verify is that THIS deployment accepted THIS artifact
/// with THIS attachment set at THIS lifecycle status, which is exactly what
/// the canonical signing input states. `SignedInputHash` is carried so a
/// verifier can confirm which bytes were signed without re-deriving the
/// canonical form under a different implementation's idea of it.
type ModelArtifactSignature = {
    /// Detached JWS (`base64url(header)..base64url(signature)`) over the
    /// canonical signing input — empty payload segment.
    DetachedJws: string
    /// Signing-key id stamped into the JWS header `kid`.
    SigningKeyId: string
    /// Origin-relative URL the public verification key is served from, so
    /// a citing party can confirm the signature offline.
    SigningKeyUrl: string
    /// `sha256:<lowercase hex>` over the exact signing-input bytes.
    SignedInputHash: string
}

/// A fitted model as a governed platform artifact (plan D5 / Stage 4).
/// Identity is the Phase 449 `FitCompositeKey`; the addressable id within a
/// scope is its `Hash`. The immutable fit-derived core (`ArtifactRef`,
/// `Diagnostics`, `GateVerdicts`) is carried verbatim from the fit; only
/// `Status` advances through the lifecycle, each transition recorded as a new
/// immutable version (GP 5). Annotations are the registrar's structured +
/// free-text notes.
type ModelArtifact = {
    /// The composite identity (plan D5). `CompositeKey.Hash` is the artifact's
    /// addressable id within `ScopeId`.
    CompositeKey: FitCompositeKey
    /// Team scope the artifact lives under (GP 4 — structural isolation
    /// inherited from `IDataObjectStore`).
    ScopeId: string
    /// Reference to the opaque fitted-parameter blob (Phase 449). The bytes
    /// live in `IDataObjectStore` (plan D5) — the registry stores the
    /// reference, never the parameters inline (GP 1).
    ArtifactRef: ArtifactRef
    /// Provider-reported diagnostics carried from the fit. Forge stores +
    /// compares; it never interprets them (plan D10).
    Diagnostics: Map<string, float>
    /// Forge's gate verdicts carried from the fit.
    GateVerdicts: GateVerdict list
    /// Current lifecycle state. The only field a transition changes.
    Status: ModelArtifactStatus
    /// Structured, queryable annotations the registrar attached (e.g.
    /// `branch`, `campaign`). Free-form keys — forge assigns no meaning.
    Annotations: Map<string, string>
    /// Free-text note the registrar attached. Empty string when none.
    Notes: string
    /// Phase 646 — the opaque provenance attachments this artifact carries.
    /// **Append-only** (`ProvenanceAttachments.append`) and hash-verified
    /// whenever the record is materialised, so a citation resolves to the
    /// bytes that were attached or to nothing at all.
    ///
    /// Empty on every artifact registered before a promotion transfer
    /// attached anything, which is the pre-646 behaviour exactly (GP 11).
    Attachments: ProvenanceAttachment list
    /// Phase 646 — the data host's own signature over this artifact's
    /// canonical identity, minted when a promotion transfer was accepted.
    ///
    /// `None` for an artifact this deployment fitted itself, and for a
    /// promotion accepted by a deployment that composed no signer. A
    /// signature is evidence that THIS deployment accepted the artifact,
    /// which is a claim only a promotion has occasion to make.
    Signature: ModelArtifactSignature option
    /// Actor who registered the artifact.
    RegisteredBy: string
    /// When the artifact was first registered (v1). Preserved verbatim across
    /// lifecycle versions.
    RegisteredAt: DateTimeOffset
    /// The `IDataObjectStore` version of this artifact record. `1` at
    /// registration; each lifecycle transition appends a version (GP 5).
    Version: int
}

module ModelArtifact =
    /// The artifact's addressable id within its scope — the composite-key
    /// hash (plan D5). Every query + `Get` + `TransitionStatus` keys on this.
    let id (artifact: ModelArtifact) : string = artifact.CompositeKey.Hash

/// Typed failures from `IModelRegistry`. A closed DU so callers pattern-match
/// on cases rather than parse messages (mirrors `DataObjectError` /
/// `DatasetError`).
[<RequireQualifiedAccess>]
type ModelRegistryError =
    /// No artifact with the given composite-key hash exists in the scope.
    | NotFound
    /// A `TransitionStatus` to `Approved` was attempted by a caller lacking
    /// the Owner/Admin role (GP 4). Carries the human-readable reason.
    | Forbidden of reason: string
    /// A `TransitionStatus` requested an edge the lifecycle graph forbids
    /// (including a self-transition). Carries the attempted edge.
    | IllegalTransition of from: ModelArtifactStatus * target: ModelArtifactStatus
    /// Underlying storage failure. Message is the raw error; not stable, only
    /// useful for diagnostics.
    | StorageFailure of string
    /// Phase 599 — a `QueryPage` call was malformed (non-positive limit).
    /// Carries the human-readable reason.
    | InvalidQuery of reason: string
    /// Phase 646 — an `AttachProvenance` call carried an attachment set the
    /// registry refused, or a materialised record carried one whose bytes
    /// no longer hash to their declared digest.
    ///
    /// A distinct case rather than a `StorageFailure` with a nicer message:
    /// the two remedies differ entirely. A storage failure is this
    /// deployment's disk having a bad day and is worth retrying; an
    /// attachment refusal is a statement about the document, and retrying
    /// the same bytes will produce the same answer forever.
    | AttachmentRefused of ProvenanceAttachmentRefusal

module ModelRegistryError =
    /// Human-readable one-line description for logs + error surfaces.
    let describe =
        function
        | ModelRegistryError.NotFound -> "model artifact not found"
        | ModelRegistryError.Forbidden reason -> $"model artifact transition forbidden: {reason}"
        | ModelRegistryError.IllegalTransition(from, target) ->
            $"illegal model artifact transition: {ModelArtifactStatus.name from} → {ModelArtifactStatus.name target}"
        | ModelRegistryError.StorageFailure r -> $"model registry storage failure: {r}"
        | ModelRegistryError.InvalidQuery r -> $"invalid model registry query: {r}"
        | ModelRegistryError.AttachmentRefused r -> ProvenanceAttachmentRefusal.describe r