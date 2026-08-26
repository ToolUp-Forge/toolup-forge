// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Consented-grant registry (Phase 552) ────────────────────────────
//
// Phase 551 let a module declare `RequiresCounterpartyApproval party` and
// then refused every such grant, at the write path AND at dispatch,
// because nothing in the estate could produce the artifact that arm asks
// for. This file is that artifact: consent to a module grant as a
// first-class, SIGNED record with its own lifecycle — proposed, approved,
// revoked — held in an `IGrantConsentStore` and re-verified ON USE.
//
// **What "on use" buys, and why it is the whole point.** A consent
// checked only when the grant is written is a claim about the past. The
// dispatch check makes it a claim about now: revoking a consent takes
// effect at the very next call, without a grant-table sweep, a cache
// invalidation, or an administrator remembering to remove the permission
// entry. That is the Phase 311 lesson (a gate the write path must
// remember to call is a defect class, not a control) applied to the one
// arm 551 could not close.
//
// **Revocation is a RECORD, never a row delete** (552.A). A revoked
// consent is a new record superseding the approved one, so the store
// answers "was this ever consented to, by whom, and when was it
// withdrawn" — a question a delete destroys the evidence for. `Put` is
// the only write on the interface below and there is deliberately no
// `Remove`: the store cannot be asked to forget.
//
// **Fable-safe by construction.** Everything here is records, DUs and
// string building over BCL primitives — no `System.Security.Cryptography`,
// no JSON. The canonical payload a signature covers is built HERE, so the
// bytes a counterparty signs are computed identically on the client tier,
// on the server, and by any third party holding this file's rules. The
// crypto that consumes those bytes lives server-side behind
// `IGrantConsentVerifier` (`ToolUp.Platform.Server`), on the
// `IModuleBindingVerifier` precedent: contract in Core, key handling
// above it.

/// The thing consent is *about*: one (principal × module) grant inside one
/// team. Identity by value throughout (GP 12 rule 1) — three strings, no
/// handle, so a record is meaningful in a log, on a wire, and in a store
/// that has never seen this process.
type ConsentSubject = {
    /// The team whose permission document carries the grant.
    TeamId: string
    /// The principal being granted access — the `TeamPermissions.Members`
    /// key, so there is no second naming axis to drift (the Phase 551
    /// argument, restated because it is the property that keeps the two
    /// halves joinable).
    SubjectId: string
    /// The module the grant targets — the `ServerModule.Name` /
    /// `AccessContext.ModulePermissions` key.
    ModuleName: string
}

module ConsentSubject =
    /// Build a subject, trimming surrounding whitespace on every part so a
    /// form field's stray space cannot mint a second, unmatchable subject.
    /// Does NOT case-fold: team, user and module ids are opaque deployment
    /// identifiers and the SDK is not entitled to decide their equality
    /// relation (the `PartyRef.create` rule, for the same reason).
    let create (teamId: string) (subjectId: string) (moduleName: string) = {
        TeamId = (if isNull (box teamId) then "" else teamId.Trim())
        SubjectId = (if isNull (box subjectId) then "" else subjectId.Trim())
        ModuleName = (if isNull (box moduleName) then "" else moduleName.Trim())
    }

    /// True when any part names nobody. An unnameable subject is a
    /// fail-closed state — never a wildcard.
    let isIncomplete (s: ConsentSubject) =
        s.TeamId = "" || s.SubjectId = "" || s.ModuleName = ""

/// The lifecycle state of one consent record. Deliberately a *record*
/// state rather than a mutable field: a transition writes a NEW record
/// superseding the old one, so the store keeps the whole history.
[<RequireQualifiedAccess>]
type ConsentStatus =
    /// Signed and lodged by the requesting side, awaiting the
    /// counterparty. Confers nothing.
    | Proposed
    /// The counterparty signed. This is the only state that can make a
    /// grant live, and only while it is also unexpired and unsuperseded.
    | Approved
    /// Withdrawn. Confers nothing, and — unlike an absent record — says
    /// so explicitly, which is what lets an operator tell "never
    /// consented" from "consent withdrawn".
    | Revoked

module ConsentStatus =
    /// Stable wire token for persistence, audit, and the signed canonical
    /// payload. Changing one of these strings changes every signature.
    let toToken =
        function
        | ConsentStatus.Proposed -> "proposed"
        | ConsentStatus.Approved -> "approved"
        | ConsentStatus.Revoked -> "revoked"

    /// Parse a persisted token. An unrecognised token reads as `Proposed`
    /// — the state that confers nothing without asserting a withdrawal
    /// that never happened. `Approved` is unreachable by fall-through, by
    /// construction: a state this node cannot interpret must never be the
    /// one that confers authority (the `GrantState.ofToken` rule).
    let ofToken (token: string) =
        match
            (if isNull (box token) then
                 ""
             else
                 token.Trim().ToLowerInvariant())
        with
        | "approved" -> ConsentStatus.Approved
        | "revoked" -> ConsentStatus.Revoked
        | _ -> ConsentStatus.Proposed

/// The signature a party puts on a consent record, over the canonical
/// payload built by `GrantConsentRecord.canonicalPayload`.
///
/// **`DeclaredAlgorithm` is diagnostic, never authority** (552.C). The
/// algorithm a signature is verified under comes from the party's
/// REGISTERED key material; this field records what the signer said it
/// used, so a disagreement is a refusal naming both rather than a silent
/// downgrade to whatever the record asked for.
type ConsentSignature = {
    /// The registered key the signature was produced under. Looked up
    /// against the party's keyring; an id with no registered key is a
    /// refusal, never a skip.
    KeyId: string
    /// What the signer claims it used (`"EcdsaP256"` / `"HmacSha256"`).
    /// Recorded for diagnosis and compared against the registered key's
    /// actual algorithm; NOT the input to any verification decision.
    DeclaredAlgorithm: string
    /// Base64 signature (ECDSA P-256 IEEE-P1363 r||s) or MAC tag
    /// (HMAC-SHA256) over the canonical payload's UTF-8 bytes.
    Value: string
    /// Wall-clock at signing time, as asserted by the signer. Covered by
    /// the signature, so it cannot be edited after the fact — but it is
    /// the signer's clock, so expiry is enforced from `ExpiresAtUtc`
    /// against the verifying node's clock, not from this.
    SignedAtUtc: DateTimeOffset
}

/// One consent artifact: a party's signed statement about one grant, at
/// one point in its lifecycle.
///
/// Records are **append-only**. `Supersedes` is the edge that orders
/// them: an approval supersedes the proposal it answers, a revocation
/// supersedes the approval it withdraws. Superseded records stay in the
/// store forever — they are the evidence.
type GrantConsentRecord = {
    /// Opaque, store-minted identifier for THIS record (not for the
    /// consent's whole history). Safe as a blob-name segment by
    /// construction — `GrantConsentRecord.isSafeId` is what the store
    /// enforces before persisting one.
    ConsentId: string
    /// The grant this record is about.
    Subject: ConsentSubject
    /// The counterparty whose approval the module's declared
    /// `GrantPolicy` requires. Must equal the `PartyRef` carried by the
    /// module's `RequiresCounterpartyApproval` arm; a record naming a
    /// different party is refused rather than accepted as "some consent".
    Party: PartyRef
    Status: ConsentStatus
    /// When this record was issued, by the issuing party's clock. The
    /// total order over records is `(IssuedAtUtc, ConsentId)` — see
    /// `GrantConsent.current` for why the tiebreak is not decoration.
    IssuedAtUtc: DateTimeOffset
    /// When an `Approved` record stops conferring authority. `None` means
    /// no expiry — legitimate for an open-ended data-sharing agreement,
    /// and visible as such rather than hidden behind a sentinel date.
    ExpiresAtUtc: DateTimeOffset option
    /// The party's signature over the canonical payload.
    Signature: ConsentSignature
    /// The `ConsentId` this record replaces, if any.
    Supersedes: string option
    /// Who lodged the record with the deployment (an administrator, a
    /// service account). Attribution for the audit row; NOT the signer —
    /// the signer is identified by `Signature.KeyId` under `Party`, and
    /// only the signature proves anything.
    RecordedBy: string
}

module GrantConsentRecord =
    /// Escape a value for the canonical payload so no field can smuggle a
    /// separator and re-frame a neighbouring one. Backslash first, else
    /// the escape of a newline would itself be re-escapable.
    let private escape (raw: string) =
        if isNull (box raw) then
            ""
        else
            raw.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r")

    /// True when `id` is safe as a single blob-name segment. The store
    /// mints ids itself, so this is a guard against a hand-built or
    /// wire-supplied record steering a write outside its own keyspace —
    /// not a formatting preference.
    let isSafeId (id: string) =
        not (String.IsNullOrWhiteSpace id)
        && id.Length <= 128
        && id |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_')

    /// **The bytes a party signs**, as a string (UTF-8 encoded by the
    /// verifier). Hand-built and versioned — deliberately NOT the
    /// record's JSON.
    ///
    /// Two reasons, and the second is the one that bites. A signature is
    /// a security-relevant identity, and JSON property ordering is a
    /// serialiser implementation detail: a converter upgrade or an
    /// options change would silently re-canonicalise every consent in the
    /// estate and invalidate signatures nobody touched (the Phase 555
    /// fingerprint argument, which is the same argument). And this file
    /// is Fable-packed, so a counterparty tool built on the client tier
    /// computes byte-identical input without owning a matching serialiser.
    ///
    /// **Every field that changes what the record MEANS is covered**,
    /// `Status` included: an approval's signature must not be replayable
    /// as a revocation, nor a revocation's as an approval. `Signature`
    /// itself is excluded (it is the output), and `RecordedBy` is
    /// excluded because the lodging administrator is the deployment's
    /// attribution, not the party's claim — a party cannot be asked to
    /// sign over who filed its statement.
    let canonicalPayload (record: GrantConsentRecord) =
        String.Join(
            "\n",
            [|
                "toolup.grant-consent.v1"
                "consent=" + escape record.ConsentId
                "team=" + escape record.Subject.TeamId
                "subject=" + escape record.Subject.SubjectId
                "module=" + escape record.Subject.ModuleName
                "party=" + escape (PartyRef.value record.Party)
                "status=" + ConsentStatus.toToken record.Status
                "issued=" + record.IssuedAtUtc.ToUniversalTime().ToString("o")
                "expires="
                + (match record.ExpiresAtUtc with
                   | Some e -> e.ToUniversalTime().ToString("o")
                   | None -> "-")
                "supersedes="
                + (match record.Supersedes with
                   | Some s -> escape s
                   | None -> "-")
                "keyid=" + escape record.Signature.KeyId
                "alg=" + escape record.Signature.DeclaredAlgorithm
            |]
        )

    /// Has an `Approved` record passed its expiry at `now`? A record with
    /// no expiry never has.
    let isExpiredAt (now: DateTimeOffset) (record: GrantConsentRecord) =
        match record.ExpiresAtUtc with
        | None -> false
        | Some e -> now >= e

/// Why a consent did not confer authority. Every arm is a *denial* — the
/// type has no success case, because the success case is the record
/// itself.
///
/// Split deliberately into a LIFECYCLE group (nothing is wrong; the
/// consent simply is not live) and a TRUST group (something claims to be
/// consent and is not). Only the trust group raises the tamper audit
/// event: an operator alerting on a forged signature and one watching a
/// revocation take effect are asking different questions, and folding
/// them together would make the first a filter over the second's volume.
[<RequireQualifiedAccess>]
type ConsentDenial =
    // ── Lifecycle ──
    /// The deployment composes no `IGrantConsentStore`, so no consent can
    /// exist. This is the pre-552 state and the reason
    /// `RequiresCounterpartyApproval` refuses everything without one.
    | NoConsentStore
    /// The store holds no record for this subject.
    | NoConsentRecord
    /// The current record is not `Approved`.
    | NotApproved of status: string
    /// The current record is `Approved` but past its expiry.
    | Expired of expiredAt: DateTimeOffset
    /// The current record is an explicit withdrawal.
    | Revoked
    /// The store could not be read. Reported rather than treated as
    /// absence: a storage blip must not read as "no consent was ever
    /// given", because that is indistinguishable from a revocation and an
    /// operator would chase the wrong thing.
    | StoreUnavailable of reason: string
    // ── Trust ──
    /// The record's subject is not the subject asked about — a misfiled
    /// or replayed record.
    | SubjectMismatch
    /// The record names a different counterparty than the module's
    /// declared policy does.
    | PartyMismatch of expected: string * found: string
    /// No key is registered for this party under the signature's key id.
    | UnknownKey of keyId: string
    /// The registered key's algorithm is not one this verifier admits, or
    /// the record's declared algorithm disagrees with the registered
    /// key's. Both are refusals; neither falls back.
    | AlgorithmNotAllowed of declared: string
    /// The signature does not validate over the canonical payload.
    | SignatureInvalid
    /// No verifier is composed, so no signature can be checked. Denies
    /// rather than admitting an unverified record.
    | NoVerifier

module ConsentDenial =
    /// Human-readable rendering, prefixed with a stable machine-greppable
    /// code — the `GrantRefusal.describe` shape, so the two refusal
    /// families read alike in a log.
    let describe =
        function
        | ConsentDenial.NoConsentStore ->
            "GRANT-CONSENT-NO-STORE: no consent store is composed, so a counterparty-approval policy can be satisfied by nothing."
        | ConsentDenial.NoConsentRecord -> "GRANT-CONSENT-NO-RECORD: no consent record exists for this grant."
        | ConsentDenial.NotApproved status ->
            $"GRANT-CONSENT-NOT-APPROVED: the current consent record is '{status}', not 'approved'."
        | ConsentDenial.Expired at ->
            let stamp = at.ToUniversalTime().ToString("o")
            $"GRANT-CONSENT-EXPIRED: the consent expired at {stamp}."
        | ConsentDenial.Revoked -> "GRANT-CONSENT-REVOKED: the consent was withdrawn."
        | ConsentDenial.StoreUnavailable reason -> $"GRANT-CONSENT-STORE-UNAVAILABLE: {reason}"
        | ConsentDenial.SubjectMismatch ->
            "GRANT-CONSENT-SUBJECT-MISMATCH: the consent record names a different grant subject."
        | ConsentDenial.PartyMismatch(expected, found) ->
            $"GRANT-CONSENT-PARTY-MISMATCH: the module requires approval from '{expected}'; the record is signed for '{found}'."
        | ConsentDenial.UnknownKey keyId ->
            $"GRANT-CONSENT-UNKNOWN-KEY: no verification key is registered for key id '{keyId}' under this party."
        | ConsentDenial.AlgorithmNotAllowed declared ->
            $"GRANT-CONSENT-ALGORITHM-NOT-ALLOWED: signature algorithm '{declared}' is not admitted for the registered key."
        | ConsentDenial.SignatureInvalid ->
            "GRANT-CONSENT-SIGNATURE-INVALID: the consent record does not match its signature."
        | ConsentDenial.NoVerifier ->
            "GRANT-CONSENT-NO-VERIFIER: no consent verifier is composed, so no signature can be checked."

    /// The stable discriminator an audit row and an operator dashboard
    /// group by. Also what the dispatch refusal carries as its
    /// `InertReason`, so the 551 audit row gains the 552 detail without a
    /// second event type.
    let code =
        function
        | ConsentDenial.NoConsentStore -> "consent-no-store"
        | ConsentDenial.NoConsentRecord -> "consent-no-record"
        | ConsentDenial.NotApproved _ -> "consent-not-approved"
        | ConsentDenial.Expired _ -> "consent-expired"
        | ConsentDenial.Revoked -> "consent-revoked"
        | ConsentDenial.StoreUnavailable _ -> "consent-store-unavailable"
        | ConsentDenial.SubjectMismatch -> "consent-subject-mismatch"
        | ConsentDenial.PartyMismatch _ -> "consent-party-mismatch"
        | ConsentDenial.UnknownKey _ -> "consent-unknown-key"
        | ConsentDenial.AlgorithmNotAllowed _ -> "consent-algorithm-not-allowed"
        | ConsentDenial.SignatureInvalid -> "consent-signature-invalid"
        | ConsentDenial.NoVerifier -> "consent-no-verifier"

    /// Is this denial a TRUST failure — something presenting itself as
    /// consent that is not — rather than an ordinary lifecycle state?
    ///
    /// Load-bearing: this predicate is what decides whether the tamper
    /// audit event fires. A revoked consent produces exactly one refusal
    /// row at dispatch; a forged signature produces that row AND a
    /// `GrantConsentVerificationDenied` alert, because the two need
    /// different responses.
    let isTrustFailure =
        function
        | ConsentDenial.SubjectMismatch
        | ConsentDenial.PartyMismatch _
        | ConsentDenial.UnknownKey _
        | ConsentDenial.AlgorithmNotAllowed _
        | ConsentDenial.SignatureInvalid -> true
        | _ -> false

module GrantConsent =
    /// The record that currently speaks for a subject, or the denial that
    /// explains why none does. **Pure** — no store, no clock beyond the
    /// `now` handed in, no crypto — so the lifecycle rules are testable
    /// on their own and compute identically on every node.
    ///
    /// Three rules, in order:
    ///
    /// 1. **Superseded records are dropped.** Any record named by another
    ///    record's `Supersedes` is history, whatever its status. This is
    ///    what makes a revocation effective without deleting the approval
    ///    it withdrew.
    /// 2. **The survivor is the latest by `(IssuedAtUtc, ConsentId)`.**
    ///    The `ConsentId` tiebreak is not decoration: two records issued
    ///    in the same clock tick must resolve identically on every node,
    ///    and a store's enumeration order is explicitly not a promise
    ///    (GP 12 rule 5). Without the tiebreak two app instances could
    ///    disagree about whether a grant is live.
    /// 3. **The survivor's own state decides**, conservatively — anything
    ///    that is not a live approval is a named denial rather than a
    ///    fall-through.
    ///
    /// A record that supersedes an id the store does not hold is still
    /// counted: it is evidence a party acted, and ignoring it would let a
    /// lost predecessor resurrect a withdrawn consent.
    let current (now: DateTimeOffset) (records: GrantConsentRecord list) : Result<GrantConsentRecord, ConsentDenial> =
        match records with
        | [] -> Error ConsentDenial.NoConsentRecord
        | _ ->
            let superseded = records |> List.choose _.Supersedes |> Set.ofList

            let live = records |> List.filter (fun r -> not (superseded.Contains r.ConsentId))

            match live with
            // Every record supersedes another — a cycle, or a store that
            // lost the head. Refused rather than resolved by picking one:
            // guessing here would be guessing about authority.
            | [] -> Error ConsentDenial.NoConsentRecord
            | _ ->
                let winner =
                    live
                    |> List.sortWith (fun a b ->
                        let byTime = compare a.IssuedAtUtc b.IssuedAtUtc

                        if byTime <> 0 then
                            byTime
                        else
                            String.CompareOrdinal(a.ConsentId, b.ConsentId))
                    |> List.last

                match winner.Status with
                | ConsentStatus.Revoked -> Error ConsentDenial.Revoked
                | ConsentStatus.Proposed -> Error(ConsentDenial.NotApproved(ConsentStatus.toToken winner.Status))
                | ConsentStatus.Approved ->
                    if GrantConsentRecord.isExpiredAt now winner then
                        Error(ConsentDenial.Expired(defaultArg winner.ExpiresAtUtc now))
                    else
                        Ok winner

    /// Does the record actually speak about the grant and the party being
    /// asked about? Checked BEFORE any signature work, so a misfiled or
    /// replayed record is refused as what it is rather than as a crypto
    /// failure — the two mean different things to whoever reads the alert.
    let addressesGrant
        (subject: ConsentSubject)
        (party: PartyRef)
        (record: GrantConsentRecord)
        : Result<unit, ConsentDenial> =
        if record.Subject <> subject then
            Error ConsentDenial.SubjectMismatch
        elif record.Party <> party then
            Error(ConsentDenial.PartyMismatch(PartyRef.value party, PartyRef.value record.Party))
        else
            Ok()