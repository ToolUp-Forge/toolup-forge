// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets

// ─── Phase 480 — bilateral clean-room template approval ──────────────
//
// Phase 311 made the clean-room floor structural: a contract composed
// with `withCleanRoomTemplate` has `ICleanRoomBroker.Enforce` plus the
// substrate's own invariants applied to every answer, and the handler
// gets no say. What it did not make structural is **who agreed to the
// template**. A `CleanRoomTemplate` is a deployment-configured value:
// the receiver declares "these methods, this k-floor" unilaterally, and
// the counterparty whose data is being queried has no say and no record.
// For a commercial clean room that is the wrong shape twice over — the
// party bearing the disclosure risk did not consent, and neither side
// can answer "what analyses may our partner run against us, and who
// agreed to them" from anything but a code review.
//
// This file makes approval a signed, versioned, revocable artefact and
// wires it into the Phase 311 gate, so an unapproved template is not
// merely undocumented — it is **unusable through the same structural
// path** the floor already travels.
//
// ── What "approval" is, cryptographically ──
//
// A template's VERSION is the SHA-256 of its canonical encoding
// (`TemplateCanonical.version`), so the version *is* the content. An
// approval is a record naming `(templateId, templateVersion, acting
// peer, counterparty peer, action, validity window)`, signed by the
// acting deployment's own peer key over the canonical encoding of
// exactly those fields. Three consequences follow directly and none of
// them are policy:
//
//   * **Approval binds to the exact bytes.** The signed message contains
//     the content hash, so an approval issued for template T carries no
//     weight for template T′ — there is no field to re-point and no
//     signature that would still verify.
//   * **An edit revokes by construction.** Editing a template changes
//     its canonical encoding, hence its hash, hence its version. The
//     dispatch check asks for approvals of the version it is *about to
//     enforce*, and finds none. Nobody has to remember to invalidate
//     anything; there is nothing carried over to invalidate. This is the
//     whole attack, and the reason the version is a hash rather than a
//     number an author bumps.
//   * **Attribution is one-key.** Signing uses the Phase 343 asymmetric
//     peer keys (`peers/{peerId}/signing-private-key` PEM PKCS#8 to
//     sign, `peers/{peerId}/signing-public-key` PEM SPKI to verify), so
//     a receiver holding only public keys can verify every counterparty
//     approval and forge none. Nothing here hand-rolls a primitive: the
//     sign / verify pair is `PeerAsymmetricKeys`, the same ES256 / RS256
//     code path the asymmetric auth provider uses.
//
// **Canonicalisation is length-prefixed, and that is load-bearing.**
// Every field is emitted as `{utf8ByteLength}:{value}`, so no value can
// impersonate a field boundary: a template id containing the delimiter
// cannot be crafted to hash-collide with a different `(id, methods,
// floor)` triple the way a naive `String.concat "|"` would allow. Sets
// are emitted count-first in ordinal sort order, so a template's hash
// does not depend on the order an author wrote its methods in. Instants
// are truncated to whole seconds before signing (GP 12 rule 6 —
// precision at the lower bound), so a record that round-trips through
// JSON re-canonicalises to the same bytes it was signed over.
//
// ── Where it binds ──
//
// `TemplateApprovalGate.check` is a `CleanRoomApprovalCheck` — the seam
// Phase 311's `CleanRoomGate.wrapApproved` runs as invariant 0, before
// the surface check and before the handler computes anything over
// sensitive data. It resolves the live template's version, reads the
// registry, and evaluates the bilateral status as of now. Unapproved,
// pending, revoked and expired all reach the caller as the same
// `PeerCleanRoomWithheld templateId` Phase 311 already defined; the
// discriminating reason is recorded receiver-side on the
// `PeerCleanRoomDecision` row. **Reusing the existing refusal is
// deliberate**: the template id is the only thing a caller needs in
// order to look up its own half of the approval state (which it signed,
// so it already holds), a new `PeerError` case would degrade to
// `PeerTransport` on every pre-480 peer for no caller-actionable gain,
// and the wire staying quiet keeps the Phase 311 argument about counting
// oracles intact.
//
// ── Cost when unused (GP 11 / GP 13) ──
//
// A composition that never calls `PeerServerApp.withTemplateApprovals`
// passes `None` to `wrapApproved`, which is exactly the pre-480
// `CleanRoomGate.wrap` path: no registry read, no blob access, no
// contract registered, no allocation. A gated contract behaves
// byte-for-byte as it did before this phase.

/// One step of a template's bilateral lifecycle. Ordered as the
/// handshake runs, but the registry never assumes an order — a
/// counterparty is free to approve without a prior proposal, and the
/// evaluation reads only each party's LATEST record.
type TemplateApprovalAction =
    /// A deployment put a template version forward for the counterparty
    /// to review. Carries no permission on its own.
    | TemplateProposed
    /// A deployment acknowledged it has reviewed the version. Still no
    /// permission — an explicit, separate record so a compliance trail
    /// can distinguish "seen" from "agreed".
    | TemplateReviewed
    /// A deployment agreed this exact version may be enforced. The only
    /// action that contributes to a live bilateral approval.
    | TemplateApproved
    /// A deployment withdrew a previous approval. Fail-closed: a
    /// revocation from either party stops the next dispatch.
    | TemplateRevoked

/// A signed lifecycle record. Value-typed and immutable (GP 12 rule 1 /
/// GP 5) — it travels between deployments over the peer channel and is
/// persisted verbatim on both sides, so it carries no live handle and no
/// framework serialisation attribute.
type TemplateApprovalRecord = {
    /// The `CleanRoomTemplate.TemplateId` this record is about.
    TemplateId: string
    /// `TemplateCanonical.version` of the exact template content agreed
    /// to — `sha256:{lowercase hex}`. An edit yields a different value,
    /// which is what makes a stale approval unusable rather than merely
    /// discouraged.
    TemplateVersion: string
    /// The peer id of the deployment that took the action and signed the
    /// record.
    ActingPeerId: string
    /// The peer id of the other party to this bilateral agreement. Both
    /// halves of a live approval name the same pair, in opposite roles.
    CounterpartyPeerId: string
    Action: TemplateApprovalAction
    /// When the acting deployment produced the record.
    IssuedAt: DateTimeOffset
    /// The instant the record takes effect. Equal to `IssuedAt` for an
    /// immediate approval; later for one agreed ahead of a start date.
    NotBefore: DateTimeOffset
    /// The instant the record stops taking effect, or `None` for an
    /// approval with no end date. An expired approval is not a live one.
    ExpiresAt: DateTimeOffset option
    /// Base64url signature by `ActingPeerId`'s private peer key over
    /// `TemplateCanonical.recordBytes` of every field above.
    Signature: string
}

/// What a deployment asks its own registry to sign and persist. The
/// template travels as the whole value rather than a pre-computed hash
/// so the registry — not the caller — decides what version means.
type TemplateApprovalRequest = {
    /// The exact template being acted on. Its canonical hash becomes the
    /// record's `TemplateVersion`.
    Template: CleanRoomTemplate
    /// This deployment's own peer id — the signing identity.
    ActingPeerId: string
    /// The other party to the agreement.
    CounterpartyPeerId: string
    Action: TemplateApprovalAction
    /// When the record takes effect. `None` means "now".
    NotBefore: DateTimeOffset option
    /// When it stops taking effect. `None` means "no end date".
    ExpiresAt: DateTimeOffset option
}

/// The bilateral decision over one exact template version at one
/// instant. Fail-closed by construction: only `BilaterallyApproved`
/// permits a dispatch, and every other case names who is missing so an
/// operator (or an admin queue) can act without re-deriving it.
type BilateralApprovalStatus =
    /// Both parties hold a live, unexpired, unrevoked approval of this
    /// exact version. `effectiveFrom` is the later of the two
    /// `NotBefore` instants — the moment the agreement actually became
    /// bilateral.
    | BilaterallyApproved of effectiveFrom: DateTimeOffset
    /// At least one party has not approved this version. `awaiting`
    /// names the peer ids whose approval is missing (or whose latest
    /// record is a proposal / review rather than an approval).
    | ApprovalPending of awaiting: string list
    /// A party's latest record for this version withdraws its approval.
    | ApprovalRevoked of byPeerId: string * at: DateTimeOffset
    /// A party's approval carried an end date that has passed.
    | ApprovalExpired of byPeerId: string * at: DateTimeOffset

/// Sign and verify approval records as a named peer. A seam rather than
/// a hard-wired call so a deployment whose key custody is an HSM, a KMS,
/// or an external signing service can substitute one without touching
/// the registry or the gate — the same reason `ICleanRoomBroker` is a
/// seam. Async at every boundary and stateless between calls (GP 12
/// rules 2 + 4): the default implementation reads key material per call
/// so a rotation takes effect immediately.
type ITemplateApprovalSigner =
    /// Sign `message` as `peerId`. `Error` carries an operator-facing
    /// reason (no signing material, unusable key); it is never echoed to
    /// a peer.
    abstract Sign: peerId: string * message: byte[] -> Async<Result<string, string>>

    /// Verify a base64url `signature` over `message` as having been
    /// produced by `peerId`. Every failure — unknown peer, unusable key,
    /// genuine mismatch — is an `Error`.
    abstract Verify: peerId: string * message: byte[] * signature: string -> Async<Result<unit, string>>

/// Canonical, length-prefixed encodings of a clean-room template and of
/// an approval record. **This is the whole of the binding between an
/// approval and the bytes it approves** — read the file header before
/// changing anything here, because a change to any encoding invalidates
/// every approval already signed across the federation.
[<RequireQualifiedAccess>]
module TemplateCanonical =

    /// Domain separator for a template encoding. Present so a template
    /// encoding and a record encoding can never be confused for one
    /// another even if their field sequences happened to coincide.
    [<Literal>]
    let templateDomain = "toolup.cleanroom.template/1"

    /// Domain separator for an approval-record encoding.
    [<Literal>]
    let recordDomain = "fuaran.federation.cleanroom.approval/1"

    /// Emit one field as `{utf8ByteLength}:{value}` followed by a
    /// newline. The length prefix is what makes the encoding injection-
    /// proof: a value containing a newline or a colon cannot shift a
    /// field boundary, because the reader (and any adversary reasoning
    /// about collisions) is told how many bytes the value occupies
    /// before it starts.
    let private field (sb: StringBuilder) (value: string) : unit =
        sb.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('\n')
        |> ignore

    /// The stable wire name of an output shape. Written out rather than
    /// derived from the DU case name so renaming a case in F# source
    /// cannot silently invalidate every signed approval in a federation.
    let shapeName (shape: OutputShape) : string =
        match shape with
        | Count -> "Count"
        | Aggregate -> "Aggregate"
        | Histogram -> "Histogram"

    /// The stable wire name of a lifecycle action, on the same terms.
    let actionName (action: TemplateApprovalAction) : string =
        match action with
        | TemplateProposed -> "Proposed"
        | TemplateReviewed -> "Reviewed"
        | TemplateApproved -> "Approved"
        | TemplateRevoked -> "Revoked"

    /// Emit a set of strings count-first in ordinal order, so a
    /// template's encoding does not depend on the order an author wrote
    /// its members in. `String.CompareOrdinal` rather than the ambient
    /// culture: a culture-sensitive sort would make the hash depend on
    /// the machine that computed it.
    let private sortedSet (sb: StringBuilder) (values: string seq) : unit =
        let ordered =
            values |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofSeq

        field sb (string (List.length ordered))
        ordered |> List.iter (field sb)

    /// The canonical UTF-8 encoding of a template. Every field of
    /// `CleanRoomTemplate` and `PrivacyGate` is covered — a template
    /// differing in any one of them encodes differently, so approving a
    /// k=50 floor does not approve the k=5 edit of it.
    let templateBytes (template: CleanRoomTemplate) : byte[] =
        let sb = StringBuilder()
        field sb templateDomain
        field sb template.TemplateId
        sortedSet sb template.AllowedMethods
        field sb (string template.Floor.MinCohortSize)
        field sb (string template.Floor.SuppressionThreshold)
        sortedSet sb (template.Floor.PermittedShapes |> Seq.map shapeName)
        Encoding.UTF8.GetBytes(sb.ToString())

    /// Lowercase hex SHA-256, the estate's existing digest presentation
    /// (`PeerSurface.export`'s `SurfaceHash` uses the same shape).
    let private sha256Hex (bytes: byte[]) : string =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    /// A template's version: `sha256:{lowercase hex}` over its canonical
    /// encoding. The algorithm is named in the value so a future digest
    /// change is a visible discontinuity rather than a silent one.
    let version (template: CleanRoomTemplate) : string =
        $"sha256:{sha256Hex (templateBytes template)}"

    /// Unix seconds, the signed representation of an instant. Truncating
    /// to whole seconds is what lets a record survive a JSON round trip
    /// and still re-canonicalise to the bytes it was signed over.
    let private instant (value: DateTimeOffset) = string (value.ToUnixTimeSeconds())

    /// Truncate an instant to the precision the canonical encoding
    /// carries, so a record's stored fields and its signed bytes agree.
    let truncate (value: DateTimeOffset) : DateTimeOffset =
        DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds())

    /// The canonical UTF-8 encoding of an approval record — every field
    /// EXCEPT `Signature`, which is what signs it. `ExpiresAt = None`
    /// encodes as the literal `"none"`, which no unix-seconds rendering
    /// can collide with.
    let recordBytes (record: TemplateApprovalRecord) : byte[] =
        let sb = StringBuilder()
        field sb recordDomain
        field sb record.TemplateId
        field sb record.TemplateVersion
        field sb record.ActingPeerId
        field sb record.CounterpartyPeerId
        field sb (actionName record.Action)
        field sb (instant record.IssuedAt)
        field sb (instant record.NotBefore)

        field
            sb
            (match record.ExpiresAt with
             | Some expiry -> instant expiry
             | None -> "none")

        Encoding.UTF8.GetBytes(sb.ToString())

    /// A record's content address — the digest of its canonical bytes
    /// PLUS its signature, so re-persisting an identical record is
    /// idempotent while two records differing only in signature stay
    /// distinct.
    let recordId (record: TemplateApprovalRecord) : string =
        let signed = recordBytes record
        let full = Array.append signed (Encoding.UTF8.GetBytes record.Signature)
        sha256Hex full

/// Evaluating a set of records into a bilateral decision. Pure and
/// total, deliberately: the decision is the security-relevant part, and
/// keeping it out of the store means it can be tested exhaustively
/// without a blob backend and cannot differ between store
/// implementations.
[<RequireQualifiedAccess>]
module TemplateApproval =

    /// Clock-skew tolerance applied to both ends of a validity window.
    /// The same 60 s the symmetric peer-token validators allow
    /// (`JwtCrypto.clockSkewSeconds`), so a federation does not have to
    /// hold two different opinions about how far apart two deployments'
    /// clocks may be.
    let defaultSkew = TimeSpan.FromSeconds(float JwtCrypto.clockSkewSeconds)

    /// Records for one party, newest first. Ties on `IssuedAt` resolve
    /// towards `TemplateRevoked` — two records stamped in the same
    /// second is exactly the case where failing closed matters.
    let private latestFor (peerId: string) (records: TemplateApprovalRecord list) =
        records
        |> List.filter (fun r -> r.ActingPeerId = peerId)
        |> List.sortByDescending (fun r ->
            r.IssuedAt,
            (match r.Action with
             | TemplateRevoked -> 1
             | _ -> 0))
        |> List.tryHead

    /// One party's contribution to the decision.
    type private PartyVerdict =
        | PartyLive of effectiveFrom: DateTimeOffset
        | PartyPending
        | PartyRevoked of at: DateTimeOffset
        | PartyExpired of at: DateTimeOffset

    let private verdict (skew: TimeSpan) (asOf: DateTimeOffset) (record: TemplateApprovalRecord option) =
        match record with
        | None -> PartyPending
        | Some r ->
            match r.Action with
            | TemplateRevoked -> PartyRevoked r.IssuedAt
            | TemplateProposed
            | TemplateReviewed -> PartyPending
            | TemplateApproved ->
                if asOf + skew < r.NotBefore then
                    // Agreed, but not yet in force. Pending rather than
                    // expired: nothing is wrong, the start date has not
                    // arrived.
                    PartyPending
                else
                    match r.ExpiresAt with
                    | Some expiry when asOf - skew > expiry -> PartyExpired expiry
                    | _ -> PartyLive r.NotBefore

    /// The bilateral decision over `query`'s exact template version.
    ///
    /// Only records naming BOTH parties (in either role) and that exact
    /// version count. A record for a different version is not evidence
    /// about this one — that is the mutation defence, and it is a filter
    /// rather than a rule because the version is the content.
    ///
    /// Precedence is fail-closed: a revocation from either side beats
    /// everything, then an expiry, then a pending party, and only a
    /// fully-live pair approves.
    let status
        (skew: TimeSpan)
        (localPeerId: string)
        (counterpartyPeerId: string)
        (templateVersion: string)
        (asOf: DateTimeOffset)
        (records: TemplateApprovalRecord list)
        : BilateralApprovalStatus =

        let parties = Set.ofList [ localPeerId; counterpartyPeerId ]

        let relevant =
            records
            |> List.filter (fun r ->
                r.TemplateVersion = templateVersion
                && Set.ofList [ r.ActingPeerId; r.CounterpartyPeerId ] = parties)

        let local = verdict skew asOf (latestFor localPeerId relevant)
        let remote = verdict skew asOf (latestFor counterpartyPeerId relevant)

        let revoked =
            [ localPeerId, local; counterpartyPeerId, remote ]
            |> List.tryPick (fun (peerId, v) ->
                match v with
                | PartyRevoked at -> Some(ApprovalRevoked(peerId, at))
                | _ -> None)

        let expired =
            [ localPeerId, local; counterpartyPeerId, remote ]
            |> List.tryPick (fun (peerId, v) ->
                match v with
                | PartyExpired at -> Some(ApprovalExpired(peerId, at))
                | _ -> None)

        let pending =
            [ localPeerId, local; counterpartyPeerId, remote ]
            |> List.choose (fun (peerId, v) ->
                match v with
                | PartyPending -> Some peerId
                | _ -> None)

        match revoked, expired, pending with
        | Some r, _, _ -> r
        | None, Some e, _ -> e
        | None, None, [] ->
            match local, remote with
            | PartyLive a, PartyLive b -> BilaterallyApproved(max a b)
            // Unreachable: every non-`PartyLive` verdict is captured
            // above. Failing closed rather than asserting keeps the
            // total match honest without a partial function on a
            // security path.
            | _ -> ApprovalPending [ localPeerId; counterpartyPeerId ]
        | None, None, awaiting -> ApprovalPending awaiting

    /// The audit-facing explanation of a status. Recorded on the
    /// `PeerCleanRoomDecision` row and NEVER sent on the wire — the
    /// caller receives only `PeerCleanRoomWithheld templateId`.
    let explain (templateId: string) (templateVersion: string) (status: BilateralApprovalStatus) : string =
        match status with
        | BilaterallyApproved from ->
            $"template '{templateId}' version {templateVersion} is bilaterally approved with effect from {from:O}"
        | ApprovalPending awaiting ->
            let named = String.concat ", " awaiting

            $"template '{templateId}' version {templateVersion} is not bilaterally approved — awaiting an approval record from [{named}]. A template edit produces a new version, so a prior approval of a different version does not carry over"
        | ApprovalRevoked(peerId, at) ->
            $"template '{templateId}' version {templateVersion} was revoked by peer '{peerId}' at {at:O}"
        | ApprovalExpired(peerId, at) ->
            $"peer '{peerId}' approval of template '{templateId}' version {templateVersion} expired at {at:O}"

/// The bilateral-approval store. Deliberately three methods: signing +
/// persisting this deployment's own action, verifying + persisting a
/// counterparty's, and reading back. The DECISION is not a method — it
/// is `TemplateApproval.status` over the records — so two store
/// implementations cannot disagree about what "approved" means.
///
/// Satisfies the six portability rules (GP 12): identity by value,
/// async at every boundary, no callbacks, stateless between calls, no
/// cross-shard ordering promise, and instants at second precision.
type ITemplateApprovalRegistry =
    /// Sign `request` as its `ActingPeerId` and persist the resulting
    /// record. Returns the record so the caller can ship it to the
    /// counterparty over the peer channel.
    abstract Issue: request: TemplateApprovalRequest -> Async<Result<TemplateApprovalRecord, PeerError>>

    /// Verify a counterparty's signed record and persist it. A record
    /// whose signature does not verify against `ActingPeerId`'s public
    /// key is refused and NOT stored — an unverified record in the store
    /// would be indistinguishable from an agreement.
    abstract Accept: record: TemplateApprovalRecord -> Async<Result<unit, PeerError>>

    /// Every record held, or those for one template id. `None` is the
    /// admin-queue read; `Some` is the dispatch-time read.
    abstract Records: templateId: string option -> Async<TemplateApprovalRecord list>

/// The default signer: the Phase 343 asymmetric peer keys, read from
/// `ISecretStore` per call.
///
/// **The same key custody as peer tokens, on purpose.** An approval and
/// a peer token are both statements a deployment makes in its own name,
/// and giving approvals a second key hierarchy would double the rotation
/// surface for no gain — a federation that has already established
/// `peers/{peerId}/signing-public-key` for its counterparties can start
/// approving templates without exchanging anything new.
///
/// A deployment holding only public keys can `Verify` every
/// counterparty's record and `Sign` none, which is the correct posture
/// for a receiver that enforces templates it did not author.
type PeerKeyTemplateApprovalSigner(secrets: ISecretStore, algorithm: PeerSignatureAlgorithm) =

    /// ES256 on the deployment's existing peer keys — the default choice
    /// for the same reason `PeerSignatureAlgorithm.PeerEs256` is the
    /// provider's.
    new(secrets: ISecretStore) = PeerKeyTemplateApprovalSigner(secrets, PeerEs256)

    interface ITemplateApprovalSigner with
        member _.Sign(peerId: string, message: byte[]) = async {
            let! pem = secrets.GetSecret(PeerJwt.platformScope, PeerAsymmetricKeys.privateKeyFor peerId)

            match pem with
            | None ->
                return
                    Error
                        $"No private signing key registered for peer '{peerId}' (expected a PEM PKCS#8 key at {PeerAsymmetricKeys.privateKeyFor peerId})"
            | Some pem ->
                match PeerAsymmetricKeys.sign algorithm pem message with
                | Ok signature -> return Ok(Base64Url.encode signature)
                | Error e -> return Error e
        }

        member _.Verify(peerId: string, message: byte[], signature: string) = async {
            let! pem = secrets.GetSecret(PeerJwt.platformScope, PeerAsymmetricKeys.publicKeyFor peerId)

            match pem with
            | None -> return Error $"No public key registered for peer '{peerId}'"
            | Some pem -> return PeerAsymmetricKeys.verify algorithm pem message signature
        }

/// `IBlobStorage`-backed default registry: one JSON document per record
/// under the reserved `_platform` container at
/// `peer-template-approvals/{templateId}/{recordId}.json`.
///
/// **Content-addressed, so persistence is idempotent.** The blob name is
/// the digest of the record's signed bytes plus its signature, so
/// re-accepting a record a counterparty re-sends writes the same blob
/// rather than accumulating duplicates — which matters because the peer
/// channel is at-least-once and a retry must not look like a second
/// approval.
///
/// **Records are never deleted.** Revocation is a record, not an
/// erasure: the trail is the artefact a regulated buyer asks for, and a
/// store that forgot a withdrawn approval could not answer "who agreed,
/// and when did they stop".
///
/// Stateless between calls (GP 12 rule 4) — every method reads through
/// to the blob store, so two instances of a deployment see the same
/// approvals without coordination.
type BlobTemplateApprovalRegistry(blobs: IBlobStorage, signer: ITemplateApprovalSigner) =
    let container = "_platform"
    let root = "peer-template-approvals/"

    /// A template id reaches a BLOB NAME here, exactly as a peer id does
    /// in `BlobPeerRegistry` — so it goes through the same platform
    /// `IdentitySanitiser` policy rather than a second dialect. `None`
    /// is "this id may never become a blob name"; each caller turns that
    /// into its own miss.
    let folderFor (templateId: string) =
        match IdentitySanitiser.sanitiseScopeId templateId with
        | Result.Ok clean -> Some $"{root}{clean}/"
        | Result.Error _ -> None

    let tryDecode (bytes: byte[]) =
        try
            Some(JsonRpc.deserialize<TemplateApprovalRecord> (Encoding.UTF8.GetString bytes))
        with _ ->
            None

    let readPrefix (prefix: string) = async {
        let! names = blobs.List(container, prefix)

        let! documents =
            names
            |> List.map (fun name -> async {
                let! result = blobs.Download(container, name)

                return
                    match result with
                    | Ok bytes -> tryDecode bytes
                    | Error _ -> None
            })
            |> Async.Parallel

        return documents |> Array.choose id |> List.ofArray
    }

    let persist (record: TemplateApprovalRecord) = async {
        match folderFor record.TemplateId with
        | None ->
            return
                Error(
                    PeerHandler
                        "Refusing to persist an approval whose template id is not a well-formed identifier — it would become a blob name outside the approval directory"
                )
        | Some folder ->
            let payload = Encoding.UTF8.GetBytes(JsonRpc.serialize record)
            let! result = blobs.Upload(container, $"{folder}{TemplateCanonical.recordId record}.json", payload)

            return
                match result with
                | Ok _ -> Ok()
                | Error message -> Error(PeerHandler message)
    }

    interface ITemplateApprovalRegistry with
        member _.Issue(request: TemplateApprovalRequest) = async {
            let issuedAt = TemplateCanonical.truncate DateTimeOffset.UtcNow

            let unsigned = {
                TemplateId = request.Template.TemplateId
                TemplateVersion = TemplateCanonical.version request.Template
                ActingPeerId = request.ActingPeerId
                CounterpartyPeerId = request.CounterpartyPeerId
                Action = request.Action
                IssuedAt = issuedAt
                NotBefore =
                    request.NotBefore
                    |> Option.map TemplateCanonical.truncate
                    |> Option.defaultValue issuedAt
                ExpiresAt = request.ExpiresAt |> Option.map TemplateCanonical.truncate
                Signature = ""
            }

            let! signed = signer.Sign(request.ActingPeerId, TemplateCanonical.recordBytes unsigned)

            match signed with
            | Error e ->
                return
                    Error(
                        PeerUnauthorized
                            $"Refusing to issue a template-approval record for peer '{request.ActingPeerId}' — {e}. An unsigned approval is not an approval"
                    )
            | Ok signature ->
                let record = { unsigned with Signature = signature }
                let! stored = persist record

                match stored with
                | Error e -> return Error e
                | Ok() -> return Ok record
        }

        member _.Accept(record: TemplateApprovalRecord) = async {
            let! verified = signer.Verify(record.ActingPeerId, TemplateCanonical.recordBytes record, record.Signature)

            match verified with
            | Error _ ->
                // One wording for every failure — unknown peer, unusable
                // key, forged signature — on the same argument
                // `PeerAsymmetricKeys.verify` makes: this is an inbound,
                // attacker-influenced path and the differences are not
                // the submitter's business.
                return
                    Error(
                        PeerUnauthorized
                            $"The approval record for template '{record.TemplateId}' does not carry a valid signature from peer '{record.ActingPeerId}'"
                    )
            | Ok() -> return! persist record
        }

        member _.Records(templateId: string option) =
            match templateId with
            | None -> readPrefix root
            | Some id ->
                match folderFor id with
                | None -> async { return [] }
                | Some folder -> readPrefix folder

/// A composed approval posture: the registry plus the clock-skew
/// tolerance its validity windows are read under. A record rather than
/// two compose fields, so `PeerServerApp` gains exactly one registration
/// surface and the two values cannot be set apart from one another.
type TemplateApprovalPolicy = {
    Registry: ITemplateApprovalRegistry
    /// Tolerance applied to both ends of every validity window.
    SkewTolerance: TimeSpan
}

[<RequireQualifiedAccess>]
module TemplateApprovalPolicy =

    /// The registry read under the shared 60 s peer clock-skew
    /// tolerance.
    let forRegistry (registry: ITemplateApprovalRegistry) : TemplateApprovalPolicy = {
        Registry = registry
        SkewTolerance = TemplateApproval.defaultSkew
    }

    /// Tighten or loosen the tolerance. A federation whose deployments
    /// are NTP-disciplined can shrink it; one spanning a flaky clock
    /// domain can widen it and say so.
    let withSkewTolerance (skew: TimeSpan) (policy: TemplateApprovalPolicy) : TemplateApprovalPolicy = {
        policy with
            SkewTolerance = skew
    }

/// The dispatch-time half: turns a composed policy into the
/// `CleanRoomApprovalCheck` Phase 311's gate runs before anything else.
[<RequireQualifiedAccess>]
module TemplateApprovalGate =

    /// The check a gated contract's dispatch runs, bound to this
    /// deployment's own peer id.
    ///
    /// The template's version is computed from the LIVE template the
    /// composition is about to enforce, never from a stored value — that
    /// is what makes an edit take effect immediately and a stale
    /// approval useless. The counterparty is the validated calling peer
    /// from the derived `PeerCallContext`, never a self-asserted wire
    /// field.
    ///
    /// A store failure is not distinguished from a missing approval:
    /// both leave the deployment unable to demonstrate consent, and a
    /// privacy gate that opens when its registry is unreachable is not a
    /// gate. `Records` already collapses a read failure to an empty
    /// list, so this falls through to `ApprovalPending` and withholds.
    let check (policy: TemplateApprovalPolicy) (localPeerId: string) : CleanRoomApprovalCheck =
        fun context template -> async {
            let templateVersion = TemplateCanonical.version template
            let! records = policy.Registry.Records(Some template.TemplateId)

            let status =
                TemplateApproval.status
                    policy.SkewTolerance
                    localPeerId
                    context.Peer.PeerId
                    templateVersion
                    DateTimeOffset.UtcNow
                    records

            match status with
            | BilaterallyApproved _ -> return Ok()
            | other -> return Error(TemplateApproval.explain template.TemplateId templateVersion other)
        }

/// One row of the approval queue an admin surface renders: what this
/// deployment and one counterparty have each said about one template
/// version, and where that leaves the pair.
type TemplateApprovalQueueEntry = {
    TemplateId: string
    TemplateVersion: string
    CounterpartyPeerId: string
    /// This deployment's latest action for the version, or `None` when
    /// it has said nothing — the "pending outbound" case.
    LocalAction: TemplateApprovalAction option
    /// The counterparty's latest action, or `None` — the "pending
    /// inbound" case.
    CounterpartyAction: TemplateApprovalAction option
    /// The bilateral decision, evaluated at the instant the queue was
    /// projected.
    Status: BilateralApprovalStatus
}

/// The admin-side projection over a registry's records. A pure function
/// over data rather than a UI: the query an operator actually asks —
/// "what analyses may our partner run against us, and who agreed" — is
/// this list, and rendering it is the consuming deployment's business
/// (the same posture `PeerSurface` takes for the wire face).
[<RequireQualifiedAccess>]
module TemplateApprovalQueue =

    /// Group every record by `(templateId, version, counterparty)` and
    /// evaluate each group. Sorted by template id then version then
    /// counterparty, so the projection is registration-order-independent
    /// and two machines render the same queue.
    let project
        (skew: TimeSpan)
        (localPeerId: string)
        (asOf: DateTimeOffset)
        (records: TemplateApprovalRecord list)
        : TemplateApprovalQueueEntry list =

        let counterpartyOf (record: TemplateApprovalRecord) =
            if record.ActingPeerId = localPeerId then
                record.CounterpartyPeerId
            else
                record.ActingPeerId

        records
        // A record naming neither role for this deployment is another
        // federation's business and is not this deployment's queue.
        |> List.filter (fun r -> r.ActingPeerId = localPeerId || r.CounterpartyPeerId = localPeerId)
        |> List.groupBy (fun r -> r.TemplateId, r.TemplateVersion, counterpartyOf r)
        |> List.map (fun ((templateId, templateVersion, counterparty), group) ->
            let latest (peerId: string) =
                group
                |> List.filter (fun r -> r.ActingPeerId = peerId)
                |> List.sortByDescending _.IssuedAt
                |> List.tryHead
                |> Option.map _.Action

            {
                TemplateId = templateId
                TemplateVersion = templateVersion
                CounterpartyPeerId = counterparty
                LocalAction = latest localPeerId
                CounterpartyAction = latest counterparty
                Status = TemplateApproval.status skew localPeerId counterparty templateVersion asOf group
            })
        |> List.sortBy (fun e -> e.TemplateId, e.TemplateVersion, e.CounterpartyPeerId)

/// The receiver's answer to a submitted approval record.
type TemplateApprovalAck = {
    /// `true` when the record verified and was persisted.
    Accepted: bool
    /// Why not, when it was refused. Deliberately coarse — the
    /// submitting peer learns that its record was not accepted, not
    /// which of several checks it failed.
    Reason: string
}

/// The typed peer contract carrying the handshake: a deployment ships
/// its signed lifecycle records to its counterparty and reads back what
/// the counterparty holds. A record of functions like every peer
/// contract, so `JsonRpcPeerClient.create<ITemplateApprovalApi>` proxies
/// it with the same machinery.
type ITemplateApprovalApi = {
    /// Submit one signed record. The receiver verifies the signature
    /// against the acting peer's public key before storing it.
    SubmitApproval: TemplateApprovalRecord -> Async<TemplateApprovalAck>
    /// Read the records the receiver holds for one template id, scoped
    /// to agreements the calling peer is a party to.
    QueryApprovals: string -> Async<TemplateApprovalRecord list>
}

/// Reserved identifiers and the receiver-side host for the approval
/// handshake.
[<RequireQualifiedAccess>]
module TemplateApprovalContract =

    /// The reserved contract id, namespaced beneath the peer substrate's
    /// `_platform.peer` prefix so it never collides with an author id —
    /// the same convention `PeerAudit.contractId` follows.
    [<Literal>]
    let contractId = "_platform.peer.template-approval"

    /// Wire method names — must match `ITemplateApprovalApi`'s fields,
    /// since the typed proxy uses the field name as the method name.
    [<Literal>]
    let submitMethod = "SubmitApproval"

    [<Literal>]
    let queryMethod = "QueryApprovals"

    /// The contract's only wire version.
    let v1: ContractVersion = { Major = 1; Minor = 0 }

    /// Build the handshake registration bound to `registry`.
    ///
    /// Like the Phase 18a audit contract this is a bespoke
    /// `PeerContractRegistration` rather than a
    /// `JsonRpcPeerHost.contract` one, because both methods need the
    /// **validated** caller identity that the generic dispatch closure
    /// discards:
    ///
    ///   * `SubmitApproval` refuses a record whose `ActingPeerId` is not
    ///     the calling peer, and whose `CounterpartyPeerId` is not this
    ///     deployment. The signature already binds the record to its
    ///     signer, so this adds nothing cryptographically — what it adds
    ///     is that one peer cannot fill this deployment's store with
    ///     records about agreements it is not part of, which is a
    ///     denial-of-service and an audit-noise surface rather than a
    ///     forgery one.
    ///   * `QueryApprovals` returns only records the caller is a party
    ///     to, on exactly the Phase 18a scoping argument: the scope
    ///     comes from the authenticated principal, and the query has no
    ///     field through which another peer's id could be named.
    let registration (localPeerId: string) (registry: ITemplateApprovalRegistry) : PeerContractRegistration =
        let dispatch: PeerDispatch =
            fun context methodName argsJson -> async {
                let caller = context.Peer.PeerId

                try
                    if methodName = submitMethod then
                        let record =
                            PeerReflection.unmarshalArgs argsJson [ typeof<TemplateApprovalRecord> ]
                            |> List.head
                            :?> TemplateApprovalRecord

                        if record.ActingPeerId <> caller then
                            return
                                Ok(
                                    JsonRpc.serialize {
                                        Accepted = false
                                        Reason = "an approval record may only be submitted by the peer that signed it"
                                    }
                                )
                        elif record.CounterpartyPeerId <> localPeerId then
                            return
                                Ok(
                                    JsonRpc.serialize {
                                        Accepted = false
                                        Reason = "this deployment is not the counterparty named by the record"
                                    }
                                )
                        else
                            let! accepted = registry.Accept record

                            return
                                Ok(
                                    JsonRpc.serialize (
                                        match accepted with
                                        | Ok() -> { Accepted = true; Reason = "" }
                                        | Error _ -> {
                                            Accepted = false
                                            Reason = "the record was not accepted"
                                          }
                                    )
                                )
                    elif methodName = queryMethod then
                        let templateId =
                            PeerReflection.unmarshalArgs argsJson [ typeof<string> ] |> List.head :?> string

                        let! records = registry.Records(Some templateId)

                        let scoped =
                            records
                            |> List.filter (fun r -> r.ActingPeerId = caller || r.CounterpartyPeerId = caller)

                        return Ok(JsonRpc.serialize scoped)
                    else
                        return Error(PeerMethodNotFound methodName)
                with ex ->
                    return Error(PeerHandler ex.Message)
            }

        {
            ContractId = contractId
            Versions = [ v1 ]
            Dispatch = dispatch
        }