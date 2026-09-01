// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage

// ─── Phase 676 — the N-party countersignature registry ───────────────
//
// Phase 480 built a signed propose / review / approve / revoke lifecycle
// and bound it to CONTENT: a subject's version is the digest of its
// canonical encoding, so an approval carries no weight for an edit of the
// thing approved. The mechanism was right and its scope was one type and
// two parties. This file is the same mechanism with both of those
// generalised, and nothing else changed:
//
//   * **Any content-hashed subject**, named by a `(kind, id, hash)`
//     triple rather than by being a clean-room template.
//   * **Any number of parties**, evaluated over a roster rather than a
//     pair.
//
// The bilateral registry is re-expressed over this core as its first
// specialisation (GP 11 — its wire shape, its canonical bytes and its
// behaviour are unchanged, which is what its Phase 480 pack asserts).
//
// ── What "countersigned" means, and why it is not a stored flag ──
//
// A subject is countersigned at an instant iff **every party on the
// roster** has a live signed action approving **that exact subject hash**,
// and no party's latest action revokes it. Four consequences follow from
// the construction rather than from policy:
//
//   * **An edit is structurally unapproved.** The signed message contains
//     the content hash, so an approval of subject S carries nothing for
//     S′. There is no field to re-point and no signature that would still
//     verify. Nobody has to remember to invalidate anything, because
//     nothing is carried over to invalidate.
//   * **A roster change re-opens approval.** The roster is signed INTO
//     each record and is part of the evaluation key, so adding a fifth
//     party does not silently inherit four existing approvals — they were
//     made under a different agreement, and saying otherwise would let a
//     party be added to a completed approval it never saw. This is the
//     N-party generalisation of Phase 480's "records must name both
//     parties" filter, which is the same rule with N pinned at 2.
//   * **Revocation is a RECORD, not an erasure.** The store never
//     deletes. A trail that forgot a withdrawn approval could not answer
//     "who agreed, and when did they stop", which is the question the
//     artefact exists for.
//   * **The decision is not a store method.** `Countersignature.status`
//     is a pure total function over records, so two store
//     implementations cannot disagree about what approved means, and the
//     security-relevant half is exhaustively testable with no backend.
//
// **Canonicalisation is length-prefixed, and that is load-bearing.**
// Every field is emitted as `{utf8ByteLength}:{value}`, so no value can
// impersonate a field boundary: a subject id containing the delimiter
// cannot be crafted to collide with a different triple the way a naive
// `String.concat "|"` would allow. Rosters are emitted count-first in
// ordinal order, so an agreement's encoding does not depend on the order
// the parties were listed in. Instants are truncated to whole seconds
// before signing, so a record that round-trips through JSON
// re-canonicalises to the bytes it was signed over.
//
// ── Signing ──
//
// Signing is a SEAM (`ICountersignatureSigner`), never a primitive. This
// file computes SHA-256 digests of its own canonical encodings and does
// nothing else cryptographic: producing and checking signatures is the
// implementation's business, so a deployment whose custody is an HSM, a
// KMS, or the application signing seam substitutes one without touching
// the registry or its evaluation. The seam is declared HERE rather than
// taken from the signing companion because `ToolUp.Platform.*` never
// references a companion's types (GP 1) — a companion-backed signer is a
// four-line adapter at the composition boundary, which is where that
// dependency belongs.
//
// ── Cost when unused (GP 11 / GP 13) ──
//
// Types, one pure evaluator, and a store nothing composes by default. A
// deployment that never constructs a registry allocates nothing, reads no
// blob, and registers no service.

/// A content-hashed subject: what a set of parties are countersigning.
///
/// The **hash is the identity** for approval purposes. `Kind` and
/// `SubjectId` are what an operator reads and what a store partitions by;
/// they are not the key an approval binds to, because a key an author can
/// edit while keeping the signature is not a binding at all.
type CountersignatureSubject = {
    /// A stable tag naming what sort of artefact this is — e.g.
    /// `"cleanroom-template"`, `"composition-head"`,
    /// `"capability-manifest"`. Written out by the owning domain, never
    /// derived from an F# identifier: renaming a type must not be able to
    /// invalidate signatures already minted.
    Kind: string
    /// The subject's identity within its kind, stable across content
    /// edits. Reaches a blob name, so it is sanitised on the way in.
    SubjectId: string
    /// `sha256:{lowercase hex}` over the subject's canonical encoding.
    ///
    /// **Contract:** the hashed bytes MUST cover the kind and the id.
    /// `CountersignatureSubject.ofCanonicalBytes` guarantees it; a domain
    /// supplying its own digest through `create` (because it already has
    /// a canonical encoding and its own domain separator, as the
    /// clean-room template does) is asserting the same thing about its
    /// own encoder.
    ContentHash: string
}

/// One step of a subject's countersignature lifecycle. Ordered as a
/// handshake usually runs, but the registry never assumes an order — a
/// party may approve without a prior proposal, and evaluation reads only
/// each party's LATEST record.
type CountersignatureAction =
    /// A party put a subject version forward for the roster to review.
    /// Carries no permission on its own.
    | SubjectProposed
    /// A party acknowledged it has reviewed the version. Still no
    /// permission — a separate record so a compliance trail can
    /// distinguish "seen" from "agreed".
    | SubjectReviewed
    /// A party agreed this exact version may be acted on. The only action
    /// that contributes to a live countersignature.
    | SubjectApproved
    /// A party withdrew a previous approval. Fail-closed: a revocation
    /// from ANY party on the roster stops the next evaluation.
    | SubjectRevoked

/// A signed lifecycle record. Value-typed and immutable (GP 12 rule 1 /
/// GP 5) — it travels between deployments and is persisted verbatim on
/// every side, so it carries no live handle and no framework
/// serialisation attribute.
type CountersignatureRecord = {
    /// The exact subject content this record is about.
    Subject: CountersignatureSubject
    /// **The party roster this record was signed under**, canonicalised
    /// (ordinal-sorted, deduplicated) by `Countersignature.roster`.
    ///
    /// Signed into the record, and part of the evaluation key. A record
    /// made under one roster is not evidence about an agreement between a
    /// different set of parties — see the file header.
    Roster: string list
    /// The party that took the action and signed the record. Always a
    /// member of `Roster`; a record whose actor is off-roster is refused
    /// at issue and contributes nothing at evaluation.
    ActingPartyId: string
    Action: CountersignatureAction
    /// When the acting party produced the record.
    IssuedAt: DateTimeOffset
    /// The instant the record takes effect. Equal to `IssuedAt` for an
    /// immediate approval; later for one agreed ahead of a start date.
    NotBefore: DateTimeOffset
    /// The instant it stops taking effect, or `None` for no end date. An
    /// expired approval is not a live one.
    ExpiresAt: DateTimeOffset option
    /// The acting party's signature over `CountersignatureCanonical.recordBytes`
    /// of every field above. Encoding is the signer's business — the
    /// registry treats it as an opaque token it hands back for
    /// verification.
    Signature: string
}

/// What a party asks its own registry to sign and persist.
type CountersignatureRequest = {
    Subject: CountersignatureSubject
    /// The parties to the agreement. Canonicalised before signing, so a
    /// caller need not sort or deduplicate.
    Roster: string list
    /// This party's own id — the signing identity. Must be on `Roster`.
    ActingPartyId: string
    Action: CountersignatureAction
    /// When the record takes effect. `None` means "now".
    NotBefore: DateTimeOffset option
    /// When it stops taking effect. `None` means "no end date".
    ExpiresAt: DateTimeOffset option
}

/// The roster's decision over one exact subject hash at one instant.
/// Fail-closed by construction: only `Countersigned` permits an action,
/// and every other case names who is missing or who withdrew, so an
/// operator (or a queue projection) can act without re-deriving it.
type CountersignatureStatus =
    /// Every party on the roster holds a live, unexpired, unrevoked
    /// approval of this exact hash. `effectiveFrom` is the LATEST of
    /// their `NotBefore` instants — the moment the agreement actually
    /// became complete.
    | Countersigned of effectiveFrom: DateTimeOffset
    /// At least one party has not approved. `awaiting` names the party
    /// ids whose approval is missing, or whose latest record is a
    /// proposal / review rather than an approval, in roster order.
    | CountersignaturePending of awaiting: string list
    /// A party's latest record withdraws its approval.
    | CountersignatureRevoked of byPartyId: string * at: DateTimeOffset
    /// A party's approval carried an end date that has passed.
    | CountersignatureExpired of byPartyId: string * at: DateTimeOffset

/// Why a registry call did not succeed. Three cases rather than a string,
/// so a caller mapping these onto its own transport vocabulary (the
/// bilateral adapter maps them onto peer errors) does not have to parse
/// prose to tell an authorisation failure from a storage one.
type CountersignatureError =
    /// No usable signing material for the acting party — the record was
    /// never minted. An unsigned approval is not an approval.
    | CountersignatureUnsigned of reason: string
    /// A submitted record's signature did not verify as the acting
    /// party's. Deliberately one case for every cause (unknown party,
    /// unusable key, forgery): this is an inbound, attacker-influenced
    /// path and the differences are not the submitter's business.
    | CountersignatureUnverified of reason: string
    /// The record was well-signed but could not be admitted — a
    /// malformed identifier, an off-roster actor, or a store failure.
    | CountersignatureRejected of reason: string

/// Sign and verify records as a named party.
///
/// A seam rather than a hard-wired call, for the same reason the
/// clean-room broker is one: key custody varies (a local secret store, a
/// KMS, an HSM, the application signing seam) and none of that should
/// reach the evaluation. Async at every boundary and stateless between
/// calls (GP 12 rules 2 + 4) — an implementation reading key material per
/// call makes a rotation take effect immediately.
///
/// A party holding only public material implements `Verify` and fails
/// `Sign`, which is the correct posture for a deployment that enforces
/// agreements it is not itself a party to.
type ICountersignatureSigner =
    /// Sign `message` as `partyId`. `Error` carries an operator-facing
    /// reason (no signing material, unusable key); it is never echoed to
    /// a remote submitter.
    abstract Sign: partyId: string * message: byte[] -> Async<Result<string, string>>

    /// Verify `signature` over `message` as having been produced by
    /// `partyId`. Every failure is an `Error`.
    abstract Verify: partyId: string * message: byte[] * signature: string -> Async<Result<unit, string>>

/// Canonical, length-prefixed encodings of a countersignature subject and
/// record. **This is the whole of the binding between a signature and the
/// bytes it covers** — read the file header before changing anything
/// here, because a change to any encoding invalidates every record
/// already signed everywhere this substrate is deployed.
[<RequireQualifiedAccess>]
module CountersignatureCanonical =

    /// Domain separator for a generically-hashed subject. Taken from the
    /// Phase 654 registry, never written out here: a module that spells a
    /// separator as a local literal has re-created the defect that
    /// registry closed.
    let subjectDomain = SignedShape.separator SignedShape.CountersignatureSubject

    /// Domain separator for a lifecycle record. Distinct from
    /// `subjectDomain`, so a signature over one can never be replayed as
    /// the other.
    let recordDomain = SignedShape.separator SignedShape.CountersignatureRecord

    /// Emit one field as `{utf8ByteLength}:{value}` followed by a
    /// newline. The length prefix is what makes the encoding
    /// injection-proof: a value containing a newline or a colon cannot
    /// shift a field boundary, because the reader — and any adversary
    /// reasoning about collisions — is told how many bytes the value
    /// occupies before it starts.
    let field (sb: StringBuilder) (value: string) : unit =
        sb.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('\n')
        |> ignore

    /// Emit a collection of strings count-first in ordinal order, so an
    /// encoding does not depend on the order an author wrote its members
    /// in. `String.CompareOrdinal` rather than the ambient culture: a
    /// culture-sensitive sort would make a digest depend on the machine
    /// that computed it.
    let sortedSet (sb: StringBuilder) (values: string seq) : unit =
        let ordered =
            values |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> List.ofSeq

        field sb (string (List.length ordered))
        ordered |> List.iter (field sb)

    /// Lowercase hex SHA-256, the estate's existing digest presentation.
    let sha256Hex (bytes: byte[]) : string =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    /// The stable wire name of a lifecycle action. Written out rather
    /// than derived from the DU case name so renaming a case in F# source
    /// cannot silently invalidate every record already signed.
    let actionName (action: CountersignatureAction) : string =
        match action with
        | SubjectProposed -> "Proposed"
        | SubjectReviewed -> "Reviewed"
        | SubjectApproved -> "Approved"
        | SubjectRevoked -> "Revoked"

    /// Unix seconds, the signed representation of an instant. Truncating
    /// to whole seconds (GP 12 rule 6 — precision at the lower bound) is
    /// what lets a record survive a JSON round trip and still
    /// re-canonicalise to the bytes it was signed over.
    let instant (value: DateTimeOffset) : string = string (value.ToUnixTimeSeconds())

    /// Truncate an instant to the precision the canonical encoding
    /// carries, so a record's stored fields and its signed bytes agree.
    let truncate (value: DateTimeOffset) : DateTimeOffset =
        DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds())

    /// The canonical encoding of a subject whose hash the GENERIC encoder
    /// produces — kind and id first, then the payload, all under the
    /// subject domain separator. A domain that already has its own
    /// canonical encoding and separator (the clean-room template does)
    /// keeps using it and supplies the digest through
    /// `CountersignatureSubject.create`.
    let subjectBytes (kind: string) (subjectId: string) (payload: byte[]) : byte[] =
        let sb = StringBuilder()
        field sb subjectDomain
        field sb kind
        field sb subjectId
        field sb (string payload.Length)
        Array.append (Encoding.UTF8.GetBytes(sb.ToString())) payload

    /// The canonical encoding of a record — every field EXCEPT
    /// `Signature`, which is what signs it. `ExpiresAt = None` encodes as
    /// the literal `"none"`, which no unix-seconds rendering can collide
    /// with.
    ///
    /// The roster is emitted count-first in ordinal order, so the same
    /// agreement encodes identically however its parties were listed —
    /// and a DIFFERENT roster encodes differently, which is the
    /// re-opening rule stated in bytes rather than in policy.
    let recordBytes (record: CountersignatureRecord) : byte[] =
        let sb = StringBuilder()
        field sb recordDomain
        field sb record.Subject.Kind
        field sb record.Subject.SubjectId
        field sb record.Subject.ContentHash
        sortedSet sb record.Roster
        field sb record.ActingPartyId
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
    /// distinct. Delivery is at-least-once wherever these travel, and a
    /// retry must not read as a second approval.
    let recordId (record: CountersignatureRecord) : string =
        Array.append (recordBytes record) (Encoding.UTF8.GetBytes record.Signature)
        |> sha256Hex

[<RequireQualifiedAccess>]
module CountersignatureSubject =

    /// A subject whose digest its owning domain computed under its own
    /// canonical encoding and domain separator. `contentHash` is expected
    /// in the estate's `sha256:{lowercase hex}` form and to cover the
    /// kind and the id — see the field's contract note.
    let create (kind: string) (subjectId: string) (contentHash: string) : CountersignatureSubject = {
        Kind = kind
        SubjectId = subjectId
        ContentHash = contentHash
    }

    /// A subject hashed by the GENERIC encoder over a domain's canonical
    /// payload bytes. The kind and id are inside the hashed bytes, so the
    /// same payload registered under two kinds yields two hashes and
    /// neither approval can be replayed as the other.
    let ofCanonicalBytes (kind: string) (subjectId: string) (payload: byte[]) : CountersignatureSubject =
        let digest =
            CountersignatureCanonical.sha256Hex (CountersignatureCanonical.subjectBytes kind subjectId payload)

        create kind subjectId $"sha256:{digest}"

/// Evaluating a set of records into a decision. Pure and total,
/// deliberately: the decision is the security-relevant part, and keeping
/// it out of the store means it can be tested exhaustively without a
/// backend and cannot differ between store implementations.
[<RequireQualifiedAccess>]
module Countersignature =

    /// The canonical form of a party roster: ordinal-sorted and
    /// deduplicated. Applied on the way into every record and every
    /// evaluation, so `["b"; "a"]`, `["a"; "b"]` and `["a"; "b"; "a"]`
    /// are one agreement rather than three.
    let roster (parties: string seq) : string list =
        parties
        |> Seq.distinct
        |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.ofSeq

    /// Records for one party, newest first. Ties on `IssuedAt` resolve
    /// towards `SubjectRevoked` — two records stamped in the same second
    /// is exactly the case where failing closed matters.
    let private latestFor (partyId: string) (records: CountersignatureRecord list) =
        records
        |> List.filter (fun r -> r.ActingPartyId = partyId)
        |> List.sortByDescending (fun r ->
            r.IssuedAt,
            (match r.Action with
             | SubjectRevoked -> 1
             | _ -> 0))
        |> List.tryHead

    /// One party's contribution to the decision.
    type private PartyVerdict =
        | PartyLive of effectiveFrom: DateTimeOffset
        | PartyPending
        | PartyRevoked of at: DateTimeOffset
        | PartyExpired of at: DateTimeOffset

    let private verdict (skew: TimeSpan) (asOf: DateTimeOffset) (record: CountersignatureRecord option) =
        match record with
        | None -> PartyPending
        | Some r ->
            match r.Action with
            | SubjectRevoked -> PartyRevoked r.IssuedAt
            | SubjectProposed
            | SubjectReviewed -> PartyPending
            | SubjectApproved ->
                if asOf + skew < r.NotBefore then
                    // Agreed, but not yet in force. Pending rather than
                    // expired: nothing is wrong, the start date has not
                    // arrived.
                    PartyPending
                else
                    match r.ExpiresAt with
                    | Some expiry when asOf - skew > expiry -> PartyExpired expiry
                    | _ -> PartyLive r.NotBefore

    /// The roster's decision over `subject`'s exact content hash.
    ///
    /// Only records naming the SAME subject and the SAME canonical roster
    /// count. A record for a different hash is not evidence about this
    /// one — that is the mutation defence, and it is a filter rather than
    /// a rule because the hash IS the content. A record made under a
    /// different roster is not evidence about this agreement — that is
    /// the re-opening rule, and it is the same filter.
    ///
    /// Precedence is fail-closed: a revocation from any party beats
    /// everything, then an expiry, then a pending party, and only a fully
    /// live roster is countersigned. An EMPTY roster is pending, never
    /// approved — "everyone has agreed" must not be satisfiable by there
    /// being no one.
    let status
        (skew: TimeSpan)
        (parties: string list)
        (subject: CountersignatureSubject)
        (asOf: DateTimeOffset)
        (records: CountersignatureRecord list)
        : CountersignatureStatus =

        let enrolled = roster parties

        let relevant =
            records |> List.filter (fun r -> r.Subject = subject && r.Roster = enrolled)

        let verdicts =
            enrolled
            |> List.map (fun partyId -> partyId, verdict skew asOf (latestFor partyId relevant))

        let revoked =
            verdicts
            |> List.tryPick (fun (partyId, v) ->
                match v with
                | PartyRevoked at -> Some(CountersignatureRevoked(partyId, at))
                | _ -> None)

        let expired =
            verdicts
            |> List.tryPick (fun (partyId, v) ->
                match v with
                | PartyExpired at -> Some(CountersignatureExpired(partyId, at))
                | _ -> None)

        let pending =
            verdicts
            |> List.choose (fun (partyId, v) ->
                match v with
                | PartyPending -> Some partyId
                | _ -> None)

        match revoked, expired, pending with
        | Some r, _, _ -> r
        | None, Some e, _ -> e
        | None, None, [] ->
            let live =
                verdicts
                |> List.choose (fun (_, v) ->
                    match v with
                    | PartyLive from -> Some from
                    | _ -> None)

            match live with
            // An empty roster reaches here with no pending party and no
            // live one. Failing closed rather than asserting keeps the
            // total match honest without a partial function on a
            // security path.
            | [] -> CountersignaturePending enrolled
            | instants -> Countersigned(List.max instants)
        | None, None, awaiting -> CountersignaturePending awaiting

    /// The audit-facing explanation of a status. Written for an operator
    /// reading a decision row; a caller deciding what a REMOTE party is
    /// told composes its own wording.
    let explain (subject: CountersignatureSubject) (status: CountersignatureStatus) : string =
        let named = $"{subject.Kind} '{subject.SubjectId}' version {subject.ContentHash}"

        match status with
        | Countersigned from -> $"{named} is countersigned by every enrolled party with effect from {from:O}"
        | CountersignaturePending awaiting ->
            let parties = String.concat ", " awaiting

            $"{named} is not countersigned — awaiting an approval record from [{parties}]. An edit produces a new version and a roster change re-opens approval, so a prior approval of different content, or by a different set of parties, does not carry over"
        | CountersignatureRevoked(partyId, at) -> $"{named} was revoked by party '{partyId}' at {at:O}"
        | CountersignatureExpired(partyId, at) -> $"party '{partyId}' approval of {named} expired at {at:O}"

    /// Clock-skew tolerance applied to both ends of a validity window.
    /// Sixty seconds — the same tolerance the platform's token
    /// validators allow, so a deployment does not have to hold two
    /// different opinions about how far apart two clocks may be.
    let defaultSkew = TimeSpan.FromSeconds 60.0

/// The countersignature store. Deliberately four members: signing +
/// persisting this party's own action, verifying + persisting another
/// party's, reading back, and the derived status.
///
/// **`Status` is derived, never stored.** It is on the interface for the
/// caller's convenience and the default implementation computes it as
/// `Countersignature.status` over `Records` — an implementation that
/// answered it from a stored flag would have re-introduced exactly the
/// state this design removes.
///
/// Satisfies the six portability rules (GP 12): identity by value, async
/// at every boundary, no callbacks, stateless between calls, no
/// cross-shard ordering promise, and instants at second precision.
type ICountersignatureRegistry =
    /// Sign `request` as its `ActingPartyId` and persist the resulting
    /// record. Returns the record so the caller can ship it to the other
    /// parties over whatever channel it uses.
    abstract Issue: request: CountersignatureRequest -> Async<Result<CountersignatureRecord, CountersignatureError>>

    /// Verify another party's signed record and persist it. A record
    /// whose signature does not verify against its `ActingPartyId` is
    /// refused and NOT stored — an unverified record in the store would
    /// be indistinguishable from an agreement.
    abstract Accept: record: CountersignatureRecord -> Async<Result<unit, CountersignatureError>>

    /// Every record held, or those for one `(kind, subjectId)` pair.
    /// `None` is the queue read; `Some` is the decision-time read.
    abstract Records: subject: (string * string) option -> Async<CountersignatureRecord list>

    /// The roster's decision over one exact subject at one instant.
    abstract Status:
        parties: string list * subject: CountersignatureSubject * asOf: DateTimeOffset -> Async<CountersignatureStatus>

/// `IBlobStorage`-backed default registry: one JSON document per record
/// under the reserved `_platform` container at
/// `countersignatures/{kind}/{subjectId}/{recordId}.json`.
///
/// **Content-addressed, so persistence is idempotent.** The blob name is
/// the digest of the record's signed bytes plus its signature, so
/// re-accepting a record another party re-sends writes the same blob
/// rather than accumulating duplicates.
///
/// **Records are never deleted.** Revocation is a record, not an erasure
/// — see the file header. There is deliberately no delete method to call.
///
/// Stateless between calls (GP 12 rule 4): every method reads through to
/// the blob store, so two instances of a deployment see the same records
/// without coordination.
type BlobCountersignatureRegistry(blobs: IBlobStorage, signer: ICountersignatureSigner, skew: TimeSpan) =
    let container = "_platform"
    let root = "countersignatures/"

    /// A kind and a subject id both reach a BLOB NAME, so both go through
    /// the platform's own `IdentitySanitiser` policy rather than a second
    /// dialect invented here. `None` is "this pair may never become a
    /// blob name"; each caller turns that into its own miss.
    let folderFor (kind: string) (subjectId: string) =
        match IdentitySanitiser.sanitiseScopeId kind, IdentitySanitiser.sanitiseScopeId subjectId with
        | Result.Ok cleanKind, Result.Ok cleanId -> Some $"{root}{cleanKind}/{cleanId}/"
        | _ -> None

    let jsonOptions = FableConverters.create ()

    let tryDecode (bytes: byte[]) =
        try
            let decoded =
                JsonSerializer.Deserialize<CountersignatureRecord>(Encoding.UTF8.GetString bytes, jsonOptions)

            // A document persisted before `Roster` existed — or one
            // truncated in transit — deserialises with a null list, which
            // NREs on the first list operation rather than failing here.
            // Coerce, then let the evaluation ignore it: a record with an
            // empty roster matches no agreement.
            if isNull (box decoded.Roster) then
                Some { decoded with Roster = [] }
            else
                Some decoded
        with _ ->
            None

    /// The one read path. Named separately from the interface member so
    /// `Status` can reach it without casting itself back to the
    /// interface — an implementation detail, not a second contract.
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

    let recordsFor (subject: (string * string) option) =
        match subject with
        | None -> readPrefix root
        | Some(kind, subjectId) ->
            match folderFor kind subjectId with
            | None -> async { return [] }
            | Some folder -> readPrefix folder

    let persist (record: CountersignatureRecord) = async {
        match folderFor record.Subject.Kind record.Subject.SubjectId with
        | None ->
            return
                Error(
                    CountersignatureRejected
                        "Refusing to persist a countersignature whose subject kind or id is not a well-formed identifier — it would become a blob name outside the countersignature directory"
                )
        | Some folder ->
            let payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, jsonOptions))

            let! result = blobs.Upload(container, $"{folder}{CountersignatureCanonical.recordId record}.json", payload)

            return
                match result with
                | Ok _ -> Ok()
                | Error message -> Error(CountersignatureRejected message)
    }

    /// The 60 s default tolerance. An explicit secondary constructor
    /// rather than an optional argument: `?skew` would fold into one
    /// widened constructor and the arity-2 token would vanish from the
    /// public surface as a break.
    new(blobs: IBlobStorage, signer: ICountersignatureSigner) =
        BlobCountersignatureRegistry(blobs, signer, Countersignature.defaultSkew)

    interface ICountersignatureRegistry with
        member _.Issue(request: CountersignatureRequest) = async {
            let enrolled = Countersignature.roster request.Roster

            if not (List.contains request.ActingPartyId enrolled) then
                // Refused before signing, not filtered at evaluation: an
                // off-roster record would be a well-signed statement
                // about an agreement its author is not party to, and
                // storing one would put noise in the trail that reads
                // like an approval to anything counting rows.
                let named = String.concat ", " enrolled

                return
                    Error(
                        CountersignatureRejected
                            $"Refusing to issue a countersignature for party '{request.ActingPartyId}', which is not on the roster [{named}]"
                    )
            else
                let issuedAt = CountersignatureCanonical.truncate DateTimeOffset.UtcNow

                let unsigned = {
                    Subject = request.Subject
                    Roster = enrolled
                    ActingPartyId = request.ActingPartyId
                    Action = request.Action
                    IssuedAt = issuedAt
                    NotBefore =
                        request.NotBefore
                        |> Option.map CountersignatureCanonical.truncate
                        |> Option.defaultValue issuedAt
                    ExpiresAt = request.ExpiresAt |> Option.map CountersignatureCanonical.truncate
                    Signature = ""
                }

                let! signed = signer.Sign(request.ActingPartyId, CountersignatureCanonical.recordBytes unsigned)

                match signed with
                | Error e ->
                    return
                        Error(
                            CountersignatureUnsigned
                                $"Refusing to issue a countersignature record for party '{request.ActingPartyId}' — {e}. An unsigned approval is not an approval"
                        )
                | Ok signature ->
                    let record = { unsigned with Signature = signature }
                    let! stored = persist record

                    match stored with
                    | Error e -> return Error e
                    | Ok() -> return Ok record
        }

        member _.Accept(record: CountersignatureRecord) = async {
            if not (List.contains record.ActingPartyId record.Roster) then
                return
                    Error(
                        CountersignatureRejected
                            $"The countersignature record for {record.Subject.Kind} '{record.Subject.SubjectId}' names an acting party that is not on its own roster"
                    )
            else
                let! verified =
                    signer.Verify(record.ActingPartyId, CountersignatureCanonical.recordBytes record, record.Signature)

                match verified with
                | Error _ ->
                    return
                        Error(
                            CountersignatureUnverified
                                $"The countersignature record for {record.Subject.Kind} '{record.Subject.SubjectId}' does not carry a valid signature from party '{record.ActingPartyId}'"
                        )
                | Ok() -> return! persist record
        }

        member _.Records(subject: (string * string) option) = recordsFor subject

        member _.Status(parties: string list, subject: CountersignatureSubject, asOf: DateTimeOffset) = async {
            // A store failure is not distinguished from a missing
            // approval: both leave the deployment unable to demonstrate
            // consent, and a gate that opens when its registry is
            // unreachable is not a gate. `Records` already collapses a
            // read failure to an empty list, so this falls through to
            // pending.
            let! records = recordsFor (Some(subject.Kind, subject.SubjectId))
            return Countersignature.status skew parties subject asOf records
        }

/// One row of the countersignature queue an operator surface renders:
/// what each party has said about one subject version, and where that
/// leaves the roster.
type CountersignatureQueueEntry = {
    Subject: CountersignatureSubject
    /// The agreement's canonical roster.
    Roster: string list
    /// Each party's latest action, in roster order. `None` for a party
    /// that has said nothing — the pending case, named rather than
    /// omitted so a reader can see who is missing.
    Actions: (string * CountersignatureAction option) list
    /// The decision, evaluated at the instant the queue was projected.
    Status: CountersignatureStatus
}

/// The operator-side projection over a registry's records. A pure
/// function over DATA rather than a UI: the question actually asked —
/// "what has been agreed, by whom, and what is still open" — is this
/// list, and rendering it is the consuming deployment's business. This is
/// the Phase 480.D posture, unchanged: no view ships here.
[<RequireQualifiedAccess>]
module CountersignatureQueue =

    /// Group every record by `(subject, roster)` and evaluate each group.
    /// Sorted by kind, then subject id, then content hash, then roster,
    /// so the projection is storage-order-independent and two machines
    /// render the same queue from the same records.
    let project
        (skew: TimeSpan)
        (asOf: DateTimeOffset)
        (records: CountersignatureRecord list)
        : CountersignatureQueueEntry list =

        records
        |> List.groupBy (fun r -> r.Subject, r.Roster)
        |> List.map (fun ((subject, enrolled), group) ->
            let latest (partyId: string) =
                group
                |> List.filter (fun r -> r.ActingPartyId = partyId)
                |> List.sortByDescending _.IssuedAt
                |> List.tryHead
                |> Option.map _.Action

            {
                Subject = subject
                Roster = enrolled
                Actions = enrolled |> List.map (fun partyId -> partyId, latest partyId)
                Status = Countersignature.status skew enrolled subject asOf group
            })
        |> List.sortBy (fun e ->
            e.Subject.Kind, e.Subject.SubjectId, e.Subject.ContentHash, String.concat "\u0000" e.Roster)

// ─── Phase 679 — the budget-amendment subject kind ───────────────────
//
// A declared budget ceiling is a term of an agreement, and the term a
// party is most often asked to move mid-engagement: the analysis needed
// one more crossing than anyone estimated. Renegotiating the whole
// agreement to move one number is expensive enough that the practical
// alternative is raising it out of band — which is exactly the act the
// countersignature exists to make impossible.
//
// So the raise itself becomes a countersigned subject. This is the first
// subject kind declared over the Phase 676 core beyond the bilateral
// specialisation it generalised, and it is declared HERE, in the
// registry, for the same reason the domain separators are: a subject
// kind is a wire tag, and a wire tag written out at its use site is one
// nobody can find when they need to know what has been agreed.
//
// **The amendment carries a DELTA against the ceiling it was agreed
// against, not a replacement.** Both halves matter:
//
//   * The delta is what the parties actually discussed ("five hundred
//     more crossings"), and putting it in the signed bytes means the
//     signature covers the change rather than a number whose meaning
//     depends on a baseline nobody recorded.
//   * `PriorCeiling` pins that baseline INTO the hash, so an amendment
//     agreed against 500 cannot be silently re-applied to a budget that
//     has since moved. Applying it is therefore a compare-and-set
//     against the ceiling in force, and a chain of amendments composes
//     in exactly one order — see `DeclassificationAmendments` at the
//     grounding tier, which is where the chain is folded.
//
// **`SubjectId` is a digest, deliberately, and this is not indifference
// to readability.** Both coordinates are free-form: a template id is a
// namespaced key (`declassify:aggregate-over-k` at the grounding tier)
// and a party id is whatever the agreement calls a party. Neither is
// constrained to the blob-name alphabet `IdentitySanitiser` enforces,
// and folding two free-form strings into one sanitised path segment
// would let two different pairs land in one folder — an amendment to
// party A's ceiling reading as evidence about party B's. A digest over
// the length-prefixed pair is injective, always well-formed, and stable;
// the readable pair rides the amendment record and the audit event,
// which is where an operator reads it.
//
// This tier declares the subject and nothing else. It does not know what
// a ceiling means, cannot read a ledger, and takes no view on whether an
// amendment may be applied — that is the accounting tier's business and
// lives with the accounting.

/// A countersigned change to one budget ceiling: which budget, whose
/// allowance, the ceiling it was agreed against, and by how much it
/// moves.
///
/// Value-typed and immutable (GP 5 / GP 12 rule 1) — it is hashed,
/// signed over, and persisted on every side of the agreement.
type BudgetAmendment = {
    /// The budget's `BudgetScope.TemplateId` — the key the ledger
    /// accounts under. The grounding tier's declassification budgets
    /// spell it `declassify:{operationId}`.
    TemplateId: string
    /// The party whose ceiling this amends. Budgets are accounted per
    /// party, so an amendment is too: raising one party's allowance must
    /// never raise another's.
    PartyId: string
    /// The effective ceiling this amendment was agreed AGAINST. Signed
    /// into the subject hash, and checked against the ceiling in force
    /// when the amendment is applied — an amendment is a
    /// compare-and-set, never a blind overwrite.
    PriorCeiling: decimal
    /// The signed change to it. Positive raises. A negative delta is
    /// admissible and is bounded at APPLICATION time by spend already
    /// recorded, because a ceiling retroactively below what has already
    /// been spent would declare a breach that never happened.
    CeilingDelta: decimal
}

[<RequireQualifiedAccess>]
module BudgetAmendment =

    /// The stable wire tag for this subject kind. Written out, never
    /// derived from the F# type name: renaming the record must not be
    /// able to invalidate signatures already minted.
    [<Literal>]
    let Kind = "budget-amendment"

    /// The ceiling this amendment puts in force, if applied.
    let amendedCeiling (amendment: BudgetAmendment) : decimal =
        amendment.PriorCeiling + amendment.CeilingDelta

    /// The invariant, trailing-zero-free rendering of a decimal used in
    /// the canonical encoding.
    ///
    /// Both properties are load-bearing. Culture-sensitive formatting
    /// would make a subject hash depend on the machine that computed it,
    /// so two parties on differently-configured hosts would sign
    /// different bytes for one agreement. And .NET preserves a decimal's
    /// scale through `ToString`, so `500m` and `500.00m` — equal values,
    /// and equal under every comparison this substrate makes — would
    /// otherwise hash differently and read as two different amendments.
    let canonicalDecimal (value: decimal) : string =
        if value = 0m then
            "0"
        else
            value.ToString("0.############################", CultureInfo.InvariantCulture)

    /// The blob-safe identity of the (budget, party) pair an amendment
    /// chain belongs to. A digest over the length-prefixed triple — see
    /// the section header for why this is not the readable join it looks
    /// like it should be.
    let subjectId (amendment: BudgetAmendment) : string =
        let sb = StringBuilder()
        CountersignatureCanonical.field sb Kind
        CountersignatureCanonical.field sb amendment.TemplateId
        CountersignatureCanonical.field sb amendment.PartyId

        CountersignatureCanonical.sha256Hex (Encoding.UTF8.GetBytes(sb.ToString()))

    /// The canonical payload the subject hash is taken over: the pair
    /// the amendment belongs to, the baseline, and the delta. Emitted
    /// through the registry's own length-prefixed encoder, so no value
    /// can impersonate a field boundary.
    let canonicalBytes (amendment: BudgetAmendment) : byte[] =
        let sb = StringBuilder()
        CountersignatureCanonical.field sb amendment.TemplateId
        CountersignatureCanonical.field sb amendment.PartyId
        CountersignatureCanonical.field sb (canonicalDecimal amendment.PriorCeiling)
        CountersignatureCanonical.field sb (canonicalDecimal amendment.CeilingDelta)
        Encoding.UTF8.GetBytes(sb.ToString())

    /// The countersignature subject for this amendment. Two amendments
    /// differing in ANY field — including the delta alone — are different
    /// subjects, so an approval of one carries nothing for the other.
    let subject (amendment: BudgetAmendment) : CountersignatureSubject =
        CountersignatureSubject.ofCanonicalBytes Kind (subjectId amendment) (canonicalBytes amendment)

    /// Every way an amendment declaration is unenforceable, as data
    /// rather than an exception (GP 12 rule 3). Empty on a healthy
    /// declaration.
    ///
    /// These are the refusals decidable from the declaration ALONE. The
    /// one that needs the ledger — a ceiling lowered below spend already
    /// recorded — is decidable only against live accounting, and is
    /// refused at application time where the reading is real.
    let validate (amendment: BudgetAmendment) : string list = [
        if String.IsNullOrWhiteSpace amendment.TemplateId then
            "a budget amendment must name the budget template it amends"

        if String.IsNullOrWhiteSpace amendment.PartyId then
            $"the budget amendment to '{amendment.TemplateId}' names no party; budgets are accounted per party, so an amendment naming none would move an allowance nobody holds"

        if amendment.PriorCeiling < 0m then
            $"the budget amendment to '{amendment.TemplateId}' was agreed against a prior ceiling of {canonicalDecimal amendment.PriorCeiling}; a negative ceiling is not a baseline any budget ever had"

        if amendment.CeilingDelta = 0m then
            $"the budget amendment to '{amendment.TemplateId}' moves the ceiling by zero; an amendment that changes nothing is a signature ceremony over a no-op, and applying it would put a row in the trail asserting a change that did not happen"

        if amendedCeiling amendment <= 0m then
            $"the budget amendment to '{amendment.TemplateId}' would put the ceiling at {canonicalDecimal (amendedCeiling amendment)}; a ceiling at or below zero admits nothing and seals the budget by accident — withdraw the routine deliberately instead"
    ]