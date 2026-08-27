// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.GrantConsentStore

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore

// ─── The consented-grant registry (Phase 552) ────────────────────────
//
// Phase 551 gave a module a voice — `RequiresCounterpartyApproval party`
// — and then refused every grant that named it, at the write path and at
// dispatch, because nothing in the estate could produce the artifact that
// arm asks for. Its own outcome recorded the gap precisely: an `Active`
// record forged for a counterparty module is refused while this phase is
// unshipped, and this store is what legitimises writing one. This file is
// the missing half.
//
// **The claim being made mechanical** is "no-one gains access to your
// data without your signature". Three properties carry it, and each is a
// separate piece of machinery below:
//
//   1. **Consent is an ARTIFACT, not a flag.** A signed record, verified
//      against key material the party registered, over a canonical
//      payload built in `Platform.Core` so every tier and every outside
//      tool computes the same bytes. A row in a table anyone with write
//      access can flip is not a signature.
//   2. **It is checked ON USE, not at the write.** `resolveLive` runs per
//      request via the stamped verdicts, so a revocation lands at the
//      very next call — no sweep, no cache invalidation, no administrator
//      remembering to remove the permission entry. A consent checked only
//      when the grant was written is a claim about the past.
//   3. **Revocation is a RECORD.** `Put` is the only write on the
//      interface; there is no `Remove`. Withdrawing consent appends a
//      `Revoked` record superseding the approval, so "was this consented
//      to, by whom, and when was it withdrawn" survives, which a delete
//      destroys (GP 5 in spirit — history preserved).
//
// **Both halves are required at dispatch**, and that is worth stating
// because it is the property an assurance reader actually samples: a
// counterparty grant is live only when the permission document carries an
// `Active` `ModuleGrantRecord` for the exact declared policy AND the
// registry holds a live, signature-valid, unexpired, unrevoked consent.
// Forging either one alone changes nothing.
//
// **Why the crypto is here and the types are in Core.** This is the
// `IModuleBindingVerifier` shape (Phase 165): the contract, the lifecycle
// rules and the canonical payload are BCL-pure and Fable-packed so a
// counterparty's own tooling can produce a record without owning a
// matching serialiser; the key handling and signature checks sit
// server-side where `System.Security.Cryptography` is available.
// `ToolUp.ArtefactSigning` — where `IArtefactSigner` lives — references
// `ToolUp.Platform.Server`, so it sits ABOVE this file and cannot be
// referenced from it (GP 1). A companion-supplied verifier over KMS or
// Ed25519 composes over `IGrantConsentVerifier` exactly as
// `DefaultModuleBindingVerifier` composes over its seam.

let private jsonOptions = FableConverters.create ()

// ─── The store seam (552.B) ──────────────────────────────────────────

/// The consented-grant registry. Satisfies the six portability rules
/// (GP 12): identity by value (three strings and a record — no handles),
/// async at every boundary, no retry semantics to leak, no state between
/// calls, and — rule 5 — **no ordering promise on `ListForSubject`**,
/// which is why `GrantConsent.current` imposes its own total order over
/// `(IssuedAtUtc, ConsentId)` rather than trusting enumeration order. Two
/// app instances reading the same records must reach the same verdict, and
/// a store that happened to enumerate differently must not be able to
/// change who has access.
///
/// **There is deliberately no `Remove`.** Revocation appends a record; the
/// store cannot be asked to forget. An implementation that offered
/// deletion would let the evidence of a consent be destroyed by the same
/// credential that could forge one.
type IGrantConsentStore =
    /// Persist a record. Idempotent on `ConsentId` — re-putting the same
    /// id overwrites, which makes a retried write safe and makes an
    /// attempt to REWRITE history detectable only by the signature, never
    /// by the store. That is intentional: the signature is the integrity
    /// control here, not the storage layer.
    abstract Put: record: GrantConsentRecord -> Async<Result<unit, string>>

    /// One record by id, scoped to the team it belongs to.
    abstract TryGet: teamId: string * consentId: string -> Async<Result<GrantConsentRecord option, string>>

    /// Every record the registry holds for one grant subject, in no
    /// promised order.
    abstract ListForSubject: subject: ConsentSubject -> Async<Result<GrantConsentRecord list, string>>

[<Literal>]
let private PlatformContainer = "_platform"

let private teamPrefix (teamId: string) = $"grant-consent/{teamId}/"

let private consentBlobName (teamId: string) (consentId: string) = $"{teamPrefix teamId}{consentId}.json"

/// A record is rejected before any write when its id would not be safe as
/// a single blob-name segment, or when its subject is incomplete. The
/// store mints ids itself (`GrantConsents.newConsentId`), so this guards a
/// hand-built or wire-supplied record steering a write outside its own
/// keyspace — a path-traversal check on an authorization artifact, not a
/// formatting preference.
let private validateForPut (record: GrantConsentRecord) =
    if not (GrantConsentRecord.isSafeId record.ConsentId) then
        Error "consent id must be a non-empty token of letters, digits, '-' or '_' (max 128 chars)"
    elif ConsentSubject.isIncomplete record.Subject then
        Error "consent subject must name a team, a subject and a module"
    elif
        String.IsNullOrWhiteSpace record.Subject.TeamId
        || record.Subject.TeamId.Contains "/"
        || record.Subject.TeamId.Contains "\\"
    then
        Error "consent team id must not contain a path separator"
    else
        Ok()

/// Dev/test in-process registry. **Not distributed-ready**, and the
/// consequence is sharper than for an ordinary cache: records live in one
/// process, so a second app instance does not see a revocation this one
/// accepted and would keep honouring a consent that has been withdrawn.
/// That is an authorization divergence, not a staleness annoyance —
/// `BlobGrantConsent` is the shape a real deployment composes.
type InMemoryGrantConsentStore() =
    let records =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, GrantConsentRecord>()

    interface IGrantConsentStore with
        member _.Put record = async {
            match validateForPut record with
            | Error e -> return Error e
            | Ok() ->
                records[(record.Subject.TeamId, record.ConsentId)] <- record
                return Ok()
        }

        member _.TryGet(teamId, consentId) = async {
            match records.TryGetValue((teamId, consentId)) with
            | true, r -> return Ok(Some r)
            | _ -> return Ok None
        }

        member _.ListForSubject subject = async {
            return records.Values |> Seq.filter (fun r -> r.Subject = subject) |> List.ofSeq |> Ok
        }

/// Blob-backed registry. One JSON document per record under
/// `_platform/grant-consent/{teamId}/{consentId}.json`.
///
/// **Flat per-team keyspace, filtered in memory.** The obvious layout
/// nests the subject and module into the path, and it is the wrong one
/// here: a user id or a module name may contain a path separator, so the
/// nested form needs an encoding, and an encoding that is not injective
/// lets one subject's records be filed under another's. A flat keyspace
/// keyed by a store-minted id has no such failure mode, and the cost — a
/// team's records are read to answer one subject — is bounded by the fact
/// that a consent is a human-scale act between two organisations, not a
/// per-request artifact.
///
/// **Distributed-ready.** No state between calls, no in-memory index, no
/// cache: every read goes to storage, so two instances cannot disagree
/// about whether a consent still stands. For a control whose entire value
/// is that revocation is immediate, a cache would be the defect.
type BlobGrantConsentStore(storage: IBlobStorage, ?logger: ILogger) =
    let logError (msg: string) =
        match logger with
        | Some(l: ILogger) -> l.Error(msg, None)
        | None -> ()

    let deserialise (bytes: byte[]) =
        try
            Some(JsonSerializer.Deserialize<GrantConsentRecord>(Encoding.UTF8.GetString bytes, jsonOptions))
        with _ ->
            None

    interface IGrantConsentStore with
        member _.Put record = async {
            match validateForPut record with
            | Error e -> return Error e
            | Ok() ->
                try
                    let bytes = JsonSerializer.Serialize(record, jsonOptions) |> Encoding.UTF8.GetBytes

                    let! result =
                        storage.Upload(PlatformContainer, consentBlobName record.Subject.TeamId record.ConsentId, bytes)

                    return
                        match result with
                        | Ok _ -> Ok()
                        | Error e -> Error e
                with ex ->
                    logError $"GrantConsentStore: could not persist consent record '{record.ConsentId}': {ex.Message}"

                    return Error ex.Message
        }

        member _.TryGet(teamId, consentId) = async {
            let blobName = consentBlobName teamId consentId
            let! exists = storage.Exists(PlatformContainer, blobName)

            if not exists then
                return Ok None
            else
                let! result = storage.Download(PlatformContainer, blobName)

                match result with
                | Error e ->
                    // Present but unreadable. Reported as an error rather
                    // than as absence: absence reads as "never consented",
                    // which an operator responds to very differently from
                    // "the store is broken" — and one of those responses is
                    // to go and get consent that already exists.
                    logError $"GrantConsentStore: consent '{consentId}' exists but could not be read ({e})."

                    return Error e
                | Ok bytes ->
                    match deserialise bytes with
                    | Some r -> return Ok(Some r)
                    | None ->
                        logError $"GrantConsentStore: consent '{consentId}' is present but unparseable."

                        return Error "consent record is present but unparseable"
        }

        member _.ListForSubject subject = async {
            try
                let! names = storage.List(PlatformContainer, teamPrefix subject.TeamId)

                let! loaded =
                    names
                    |> List.map (fun name -> async {
                        let! result = storage.Download(PlatformContainer, name)

                        return
                            match result with
                            | Ok bytes -> deserialise bytes
                            | Error _ -> None
                    })
                    |> Async.Sequential

                return
                    loaded
                    |> Array.toList
                    |> List.choose id
                    |> List.filter (fun r -> r.Subject = subject)
                    |> Ok
            with ex ->
                return Error ex.Message
        }

// ─── Key material + verification (552.C) ─────────────────────────────

/// A counterparty's registered verification key. **Server-side by
/// design** — it carries key bytes, and the store-substrate convention
/// puts crypto-bearing types in `Platform.Server` rather than in the
/// Fable-packed Core tier.
///
/// The two arms are the algorithm allowlist. There is no third case and
/// no string-keyed algorithm dispatch, so "which algorithms does this
/// verifier admit" is answered by the type rather than by a table someone
/// can extend at a call site.
type ConsentPartyKeyMaterial =
    /// ECDSA over NIST P-256, SHA-256 digest, IEEE-P1363 fixed-field
    /// (r||s) signature encoding. The public key is
    /// SubjectPublicKeyInfo DER, base64 — the `StoredSigningKey.PublicKey`
    /// convention the estate already uses.
    | ConsentEcdsaP256 of publicKeySpkiBase64: string
    /// HMAC-SHA256 with a shared secret, base64. Legitimate where the
    /// "counterparty" is another service under the same operator; NOT
    /// suitable where the point is that the verifying side cannot forge
    /// the signature, because with a symmetric key it can.
    | ConsentHmacSha256 of keyBase64: string

module ConsentPartyKeyMaterial =
    /// The token a `ConsentSignature.DeclaredAlgorithm` must match for
    /// this material. A record declaring anything else is refused rather
    /// than verified under the registered algorithm anyway: a
    /// disagreement is a downgrade attempt, and silently doing the right
    /// thing would hide it.
    let token =
        function
        | ConsentEcdsaP256 _ -> "EcdsaP256"
        | ConsentHmacSha256 _ -> "HmacSha256"

/// One registered key, bound to the party it speaks for. A key is
/// identified by `(Party, KeyId)`: two parties may legitimately register
/// the same key id, and a key registered for party A must never verify a
/// record claiming party B.
type ConsentPartyKey = {
    Party: PartyRef
    KeyId: string
    Material: ConsentPartyKeyMaterial
}

/// Verifies a consent record's signature against registered key material.
///
/// A seam rather than a concrete function so a deployment whose
/// counterparty signs through a KMS, or with Ed25519 (which the BCL does
/// not provide), composes its own — the `IModuleBindingVerifier` /
/// `DefaultModuleBindingVerifier` split, applied here.
type IGrantConsentVerifier =
    /// `Ok ()` when the record's signature validates under a key
    /// registered for the record's own party. Synchronous: registered keys
    /// carry their material by value, so this is a CPU-bound check with no
    /// I/O — the same rule `IModuleBindingVerifier.Verify` follows.
    abstract Verify: record: GrantConsentRecord -> Result<unit, ConsentDenial>

/// The shipped verifier, built over a value-typed keyring.
///
/// **The default registration carries an EMPTY keyring**, so a deployment
/// that composes a store but registers no counterparty keys denies every
/// record with `consent-unknown-key`, naming the key id it could not
/// resolve. That is the honest failure: it says "you have not registered
/// this party's key" rather than either admitting an unverified record or
/// failing silently in a way indistinguishable from a revocation.
///
/// **No `alg` is ever taken from the record** (552.C). The algorithm is
/// the registered key's; `DeclaredAlgorithm` is compared to it and a
/// disagreement is `AlgorithmNotAllowed`, naming what was declared. The
/// allowlist is `ConsentPartyKeyMaterial`'s cases — a closed set in the
/// type system rather than a string table.
type DefaultGrantConsentVerifier(keys: ConsentPartyKey list) =
    // Keyed by (party, keyId): a key registered for one party must never
    // verify another party's record, so the party is part of the lookup
    // key rather than a field checked afterwards and forgotten.
    let byPartyAndKeyId =
        keys
        |> List.map (fun k -> (PartyRef.value k.Party, k.KeyId), k.Material)
        |> Map.ofList

    interface IGrantConsentVerifier with
        member _.Verify record =
            match
                byPartyAndKeyId
                |> Map.tryFind (PartyRef.value record.Party, record.Signature.KeyId)
            with
            | None -> Error(ConsentDenial.UnknownKey record.Signature.KeyId)
            | Some material ->
                if record.Signature.DeclaredAlgorithm <> ConsentPartyKeyMaterial.token material then
                    Error(ConsentDenial.AlgorithmNotAllowed record.Signature.DeclaredAlgorithm)
                else
                    let payload = GrantConsentRecord.canonicalPayload record |> Encoding.UTF8.GetBytes

                    try
                        let signature = Convert.FromBase64String record.Signature.Value

                        match material with
                        | ConsentEcdsaP256 spkiBase64 ->
                            use ecdsa = ECDsa.Create()
                            let mutable read = 0
                            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String spkiBase64, &read)

                            if
                                ecdsa.VerifyData(
                                    payload,
                                    signature,
                                    HashAlgorithmName.SHA256,
                                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                                )
                            then
                                Ok()
                            else
                                Error ConsentDenial.SignatureInvalid
                        | ConsentHmacSha256 keyBase64 ->
                            use mac = new HMACSHA256(Convert.FromBase64String keyBase64)
                            let expected = mac.ComputeHash payload

                            // Constant-time: a MAC compared with an
                            // early-exit equality leaks the correct tag
                            // one byte at a time to anyone who can retry.
                            if
                                CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, ReadOnlySpan signature)
                            then
                                Ok()
                            else
                                Error ConsentDenial.SignatureInvalid
                    with _ ->
                        // Malformed base64, a key that will not import, a
                        // signature of the wrong length. All of it is
                        // "this does not verify" — never an exception that
                        // escapes into a dispatch path and turns a denial
                        // into a 500 an operator might retry past.
                        Error ConsentDenial.SignatureInvalid

/// The verifier a deployment gets when it composes a store but registers
/// nothing else. Denies with `NoVerifier` rather than admitting anything.
let denyingVerifier =
    { new IGrantConsentVerifier with
        member _.Verify _ = Error ConsentDenial.NoVerifier
    }

/// Build the shipped verifier over a keyring. The entry point a
/// composition root calls:
/// `services.AddSingleton<IGrantConsentVerifier>(GrantConsentStore.verifierOver keys)`.
let verifierOver (keys: ConsentPartyKey list) : IGrantConsentVerifier =
    DefaultGrantConsentVerifier(keys) :> IGrantConsentVerifier

// ─── Producing a signature (the reference implementation) ────────────

/// Produces the signature a consent record carries. The SDK is the
/// reference implementation of the canonical form, so a deployment does
/// not have to re-derive the byte layout to lodge a record — but note
/// what is NOT here: there is no key store, no private-key persistence and
/// no signing service. Custody of a counterparty's signing key is the
/// counterparty's, and an SDK that held it would have removed the property
/// the signature exists to provide.
module ConsentSigning =
    /// Sign with a caller-supplied `ECDsa` (P-256). The caller owns the
    /// key's lifetime and its custody; this only binds it to the canonical
    /// payload.
    let signEcdsaP256 (ecdsa: ECDsa) (keyId: string) (record: GrantConsentRecord) : ConsentSignature =
        let payload = GrantConsentRecord.canonicalPayload record |> Encoding.UTF8.GetBytes

        let raw =
            ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

        {
            KeyId = keyId
            DeclaredAlgorithm = "EcdsaP256"
            Value = Convert.ToBase64String raw
            SignedAtUtc = DateTimeOffset.UtcNow
        }

    /// Sign with a shared HMAC-SHA256 key. Symmetric, so the verifying
    /// side can also produce this — see `ConsentHmacSha256`'s note.
    let signHmacSha256 (keyBytes: byte[]) (keyId: string) (record: GrantConsentRecord) : ConsentSignature =
        let payload = GrantConsentRecord.canonicalPayload record |> Encoding.UTF8.GetBytes
        use mac = new HMACSHA256(keyBytes)

        {
            KeyId = keyId
            DeclaredAlgorithm = "HmacSha256"
            Value = Convert.ToBase64String(mac.ComputeHash payload)
            SignedAtUtc = DateTimeOffset.UtcNow
        }

// ─── Resolution + lifecycle (552.C / 552.E) ──────────────────────────

/// `HttpContext.Items` key carrying, per policy-bearing counterparty
/// module the caller has a grant record on, the consent verdict resolved
/// for THIS request. Stamped by `ScopeResolutionMiddleware` beside the
/// Phase 551 `ToolUp.ModuleGrants` stamp, and only when a consent store is
/// composed AND at least one module declares a counterparty arm — so a
/// deployment doing neither performs no consent work at all (GP 13).
[<Literal>]
let ModuleGrantConsentsItemsKey = "ToolUp.ModuleGrantConsents"

/// The consent verdicts stamped for this request, module-keyed. Empty
/// when nothing was stamped — which is the whole of a deployment with no
/// counterparty policy, and which is safe because the dispatch guard then
/// falls through to the Phase 551 refusal rather than to an admission.
let consentVerdictsFromItems
    (items: System.Collections.Generic.IDictionary<obj, obj>)
    : Map<string, Result<unit, ConsentDenial>> =
    match items.TryGetValue(box ModuleGrantConsentsItemsKey) with
    | true, (:? Map<string, Result<unit, ConsentDenial>> as verdicts) -> verdicts
    | _ -> Map.empty

/// Emit one audit row, best-effort. `schedule` is `Async.Start` in
/// production and a synchronous runner in a test, so one call yields both
/// the decision and the row and the two cannot be covered separately.
let private emit (auditLog: IAuditLog option) (schedule: Async<unit> -> unit) (scopeId: string) (event: AuditEvent) =
    match auditLog with
    | None -> ()
    | Some log ->
        schedule (
            async {
                try
                    do! log.Record(scopeId, event)
                with _ ->
                    ()
            }
        )

let private scopeOf (teamId: string) = $"team-{teamId}"

/// The tamper alert (552.E). Fires ONLY for a trust failure — a record
/// presenting itself as consent that is not. An ordinary revocation,
/// expiry or pending proposal is already fully described by the
/// `UnconsentedGrantRefused` row the dispatch refusal emits, and adding a
/// second row for each would bury the forgery signal in the volume of
/// routine ones.
let private emitTrustDenial
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (subject: ConsentSubject)
    (party: PartyRef)
    (record: GrantConsentRecord option)
    (denial: ConsentDenial)
    =
    if ConsentDenial.isTrustFailure denial then
        emit
            auditLog
            schedule
            (scopeOf subject.TeamId)
            (GrantConsentVerificationDenied {
                ConsentId =
                    match record with
                    | Some r -> r.ConsentId
                    | None -> ""
                TeamId = subject.TeamId
                SubjectId = subject.SubjectId
                ModuleName = subject.ModuleName
                Party = PartyRef.value party
                KeyId =
                    match record with
                    | Some r -> r.Signature.KeyId
                    | None -> ""
                DeclaredAlgorithm =
                    match record with
                    | Some r -> r.Signature.DeclaredAlgorithm
                    | None -> ""
                DenialCode = ConsentDenial.code denial
            })

/// **The resolution the whole phase turns on.** Is consent live, right
/// now, for this grant under this party?
///
/// Order is load-bearing and runs cheapest-and-most-specific first:
/// store read → lifecycle (`GrantConsent.current`, pure) → addressing
/// (right subject, right party) → signature. A misfiled record is
/// therefore reported as a misfiling rather than as a crypto failure, and
/// a revoked consent never reaches the verifier at all — which keeps the
/// tamper alert meaning what it says.
let resolveLive
    (store: IGrantConsentStore option)
    (verifier: IGrantConsentVerifier)
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (now: DateTimeOffset)
    (subject: ConsentSubject)
    (party: PartyRef)
    : Async<Result<GrantConsentRecord, ConsentDenial>> =
    async {
        match store with
        | None -> return Error ConsentDenial.NoConsentStore
        | Some s ->
            let! listed = s.ListForSubject subject

            match listed with
            | Error e -> return Error(ConsentDenial.StoreUnavailable e)
            | Ok records ->
                match GrantConsent.current now records with
                | Error denial ->
                    emitTrustDenial auditLog schedule subject party None denial
                    return Error denial
                | Ok record ->
                    match GrantConsent.addressesGrant subject party record with
                    | Error denial ->
                        emitTrustDenial auditLog schedule subject party (Some record) denial
                        return Error denial
                    | Ok() ->
                        match verifier.Verify record with
                        | Error denial ->
                            emitTrustDenial auditLog schedule subject party (Some record) denial
                            return Error denial
                        | Ok() -> return Ok record
    }

/// The lifecycle write paths. Each lodges a signed record and emits its
/// own audit row (552.E); none of them signs anything — the caller
/// presents a record its party already signed, which is the point.
module GrantConsents =

    /// Mint a fresh, blob-safe record id.
    let newConsentId () = Guid.NewGuid().ToString("N")

    /// Build an unsigned record ready to be signed. Separated from `Put`
    /// so the canonical payload is computed over the EXACT record that
    /// will be stored — building a record, signing a differently-shaped
    /// projection of it, and storing a third is how signature schemes rot.
    let draft
        (subject: ConsentSubject)
        (party: PartyRef)
        (status: ConsentStatus)
        (issuedAt: DateTimeOffset)
        (expiresAt: DateTimeOffset option)
        (supersedes: string option)
        (recordedBy: string)
        (keyId: string)
        (declaredAlgorithm: string)
        : GrantConsentRecord =
        {
            ConsentId = newConsentId ()
            Subject = subject
            Party = party
            Status = status
            IssuedAtUtc = issuedAt
            ExpiresAtUtc = expiresAt
            Signature = {
                KeyId = keyId
                DeclaredAlgorithm = declaredAlgorithm
                Value = ""
                SignedAtUtc = issuedAt
            }
            Supersedes = supersedes
            RecordedBy = recordedBy
        }

    let private lodge
        (store: IGrantConsentStore)
        (verifier: IGrantConsentVerifier)
        (auditLog: IAuditLog option)
        (schedule: Async<unit> -> unit)
        (expectedStatus: ConsentStatus)
        (record: GrantConsentRecord)
        (event: GrantConsentRecord -> AuditEvent)
        : Async<Result<GrantConsentRecord, ConsentDenial>> =
        async {
            if record.Status <> expectedStatus then
                return Error(ConsentDenial.NotApproved(ConsentStatus.toToken record.Status))
            else
                // Verified BEFORE it is stored, so the registry never holds
                // a record that cannot be honoured. A store that accepted
                // unverifiable records would turn every dispatch into a
                // re-litigation of a write that should never have landed.
                match verifier.Verify record with
                | Error denial ->
                    emitTrustDenial auditLog schedule record.Subject record.Party (Some record) denial
                    return Error denial
                | Ok() ->
                    let! written = store.Put record

                    match written with
                    | Error e -> return Error(ConsentDenial.StoreUnavailable e)
                    | Ok() ->
                        emit auditLog schedule (scopeOf record.Subject.TeamId) (event record)
                        return Ok record
        }

    /// Lodge a signed PROPOSAL. Confers nothing — it is the request the
    /// counterparty answers.
    let propose store verifier auditLog schedule (record: GrantConsentRecord) =
        lodge store verifier auditLog schedule ConsentStatus.Proposed record (fun r ->
            GrantConsentProposed {
                ConsentId = r.ConsentId
                TeamId = r.Subject.TeamId
                SubjectId = r.Subject.SubjectId
                ModuleName = r.Subject.ModuleName
                Party = PartyRef.value r.Party
                KeyId = r.Signature.KeyId
                RecordedBy = r.RecordedBy
                ExpiresAtUtc = r.ExpiresAtUtc
            })

    /// Lodge a counterparty's signed APPROVAL. `Supersedes` names the
    /// proposal it answers, or is `None` for an out-of-band agreement
    /// recorded in one act — legitimate, and visible as such on the audit
    /// row rather than being forced into a fake proposal.
    let approve store verifier auditLog schedule (record: GrantConsentRecord) =
        lodge store verifier auditLog schedule ConsentStatus.Approved record (fun r ->
            GrantConsentApproved {
                ConsentId = r.ConsentId
                TeamId = r.Subject.TeamId
                SubjectId = r.Subject.SubjectId
                ModuleName = r.Subject.ModuleName
                Party = PartyRef.value r.Party
                KeyId = r.Signature.KeyId
                RecordedBy = r.RecordedBy
                Supersedes = defaultArg r.Supersedes ""
                ExpiresAtUtc = r.ExpiresAtUtc
            })

    /// Lodge a signed REVOCATION. Appends — the approval it supersedes
    /// stays in the store as the evidence that consent was once given.
    /// Effective at the next call, because dispatch resolves consent on
    /// use.
    let revoke store verifier auditLog schedule (record: GrantConsentRecord) =
        lodge store verifier auditLog schedule ConsentStatus.Revoked record (fun r ->
            GrantConsentRevoked {
                ConsentId = r.ConsentId
                TeamId = r.Subject.TeamId
                SubjectId = r.Subject.SubjectId
                ModuleName = r.Subject.ModuleName
                Party = PartyRef.value r.Party
                KeyId = r.Signature.KeyId
                RecordedBy = r.RecordedBy
                Supersedes = defaultArg r.Supersedes ""
            })

// ─── The write-path oracle (552.D, write half) ───────────────────────

/// `CounterpartyConsentOracle` over the registry. This is what turns the
/// Phase 551 write guard's blanket counterparty refusal into a real
/// question, without that guard knowing anything about signatures.
type StoreCounterpartyConsentOracle
    (
        store: IGrantConsentStore option,
        verifier: IGrantConsentVerifier,
        now: unit -> DateTimeOffset,
        ?auditLog: IAuditLog
    ) =
    interface GrantPolicyGuard.CounterpartyConsentOracle with
        member _.IsConsentLive(teamId, subjectId, moduleName, party) = async {
            let subject = ConsentSubject.create teamId subjectId moduleName

            let! resolved = resolveLive store verifier auditLog Async.Start (now ()) subject party

            return Result.isOk resolved
        }

/// Grant a `RequiresCounterpartyApproval` module to a subject, on the
/// strength of a live consent record.
///
/// **Why this is not folded into `PermissionGrants.grantModuleAccess`.**
/// That entry point routes every arm through the pure, synchronous
/// `evaluateGrant`, whose counterparty case has nowhere to go: resolving
/// consent is a store read plus a signature check. Widening it would have
/// made a pure function async for one arm and rippled through Phase 551's
/// whole write path. A sibling entry point costs one name and leaves 551
/// byte-identical.
///
/// The record written is `Active` with `SatisfiedPolicy` set to the exact
/// declared arm — party included — so a module that later re-declares a
/// DIFFERENT counterparty invalidates this grant instead of grandfathering
/// it (`isCounterpartyRecordAdequate` compares by equality, not rank).
let grantWithCounterpartyApproval
    (store: IPermissionStore)
    (registry: GrantPolicyGuard.ModuleGrantPolicyRegistry)
    (consentStore: IGrantConsentStore option)
    (verifier: IGrantConsentVerifier)
    (auditLog: IAuditLog option)
    (now: DateTimeOffset)
    (teamId: string)
    (subjectId: string)
    (moduleName: string)
    (permissions: ModulePermission list)
    (justification: string)
    : Async<Result<GrantWriteOutcome, ConsentDenial>> =
    async {
        let policy = GrantPolicyGuard.ModuleGrantPolicyRegistry.resolve registry moduleName

        match policy with
        | GrantPolicy.RequiresCounterpartyApproval party ->
            let subject = ConsentSubject.create teamId subjectId moduleName

            let! resolved = resolveLive consentStore verifier auditLog Async.Start now subject party

            match resolved with
            | Error denial -> return Error denial
            | Ok consentRecord ->
                let! existing = store.GetTeamPermissions teamId

                let priorForUser =
                    existing.Members |> Map.tryFind subjectId |> Option.defaultValue Map.empty

                let priorGrants =
                    existing.Grants |> Map.tryFind subjectId |> Option.defaultValue Map.empty

                let grantRecord = {
                    State = GrantState.Active
                    SatisfiedPolicy = policy
                    // The consent id lands in the justification so the
                    // permission document itself names the artifact that
                    // satisfied the policy. Without it, reconciling a grant
                    // against the registry means a scan; with it, it is a
                    // lookup.
                    Justification =
                        if String.IsNullOrWhiteSpace justification then
                            $"counterparty consent {consentRecord.ConsentId}"
                        else
                            $"{justification.Trim()} (counterparty consent {consentRecord.ConsentId})"
                    ConsentedBy = None
                }

                let! written =
                    store.SetTeamPermissions(
                        teamId,
                        {
                            existing with
                                Members =
                                    existing.Members
                                    |> Map.add subjectId (priorForUser |> Map.add moduleName permissions)
                                Grants =
                                    existing.Grants
                                    |> Map.add subjectId (priorGrants |> Map.add moduleName grantRecord)
                        }
                    )

                match written with
                | Error e ->
                    // The inner store refused, or storage did. Surfaced
                    // with the store's own message rather than remapped
                    // onto a policy refusal: Phase 551's outcome recorded
                    // exactly that mislabelling as a defect (a dual-control
                    // queue result reported as `UnbackedGrant`), and
                    // repeating it here would have been a second instance
                    // of a known bug.
                    return Error(ConsentDenial.StoreUnavailable e)
                | Ok() -> return Ok GrantWriteOutcome.Granted
        // Not a counterparty module. Refused rather than quietly
        // delegating: a caller reaching for this entry point believes the
        // module requires third-party approval, and silently writing an
        // ordinary grant would confirm a belief that is wrong.
        | _ -> return Error(ConsentDenial.PartyMismatch("<counterparty-policy>", GrantPolicy.toToken policy))
    }

// ─── Dispatch-time enforcement (552.D) ───────────────────────────────

/// Phase 730 — the DECISION half of `guardDispatchWithConsent`, extracted
/// so it can be asked without emitting anything.
///
/// **Why this is a separate function rather than a flag.** Two callers
/// need the same verdict for different purposes. The Remoting seam refuses
/// a call and MUST audit it — an attempt on inert authority is exactly the
/// row Phase 551 exists to produce. The AI tool registry (Phase 730)
/// filters a governed module out of the list it offers the model, and must
/// NOT audit: nothing was attempted, the model was never told the module
/// existed, and a row per listing per turn would bury the refusals that do
/// mean something under a stream that means nothing. That is the same
/// list-filter-vs-boundary split Phase 36.A drew for RBAC.
///
/// What must never happen is the two answering differently — a module the
/// filter hides but the guard would admit, or worse the reverse. So there
/// is exactly one decision, here, and the two callers differ only in what
/// they do with it.
///
/// `Ok ()` when the caller's entry on `moduleName` carries live authority;
/// `Error inertReason` (the `GrantPolicy.inertReason` / `ConsentDenial.code`
/// vocabulary) when it is present but inert.
let dispatchVerdict
    (registry: GrantPolicyGuard.ModuleGrantPolicyRegistry)
    (grants: Map<string, ModuleGrantRecord>)
    (consentVerdicts: Map<string, Result<unit, ConsentDenial>>)
    (moduleName: string)
    : Result<unit, string> =
    if GrantPolicyGuard.ModuleGrantPolicyRegistry.isEmpty registry then
        // No module declares a policy — the pre-551 path, taken without
        // touching grants or verdicts.
        Ok()
    else
        match GrantPolicyGuard.ModuleGrantPolicyRegistry.resolve registry moduleName with
        | GrantPolicy.RequiresCounterpartyApproval _ as policy ->
            let record = Map.tryFind moduleName grants

            let recordAdequate =
                match record with
                | Some r -> r.State = GrantState.Active && r.SatisfiedPolicy = policy
                | None -> false

            let consentVerdict =
                consentVerdicts
                |> Map.tryFind moduleName
                |> Option.defaultValue (Error ConsentDenial.NoConsentStore)

            match consentVerdict, recordAdequate with
            | Ok(), true -> Ok()
            | _ ->
                match consentVerdict with
                | Error denial -> Error(ConsentDenial.code denial)
                // Consent stands; what is missing is the grant itself.
                // Named separately because "the counterparty agreed and
                // nobody wrote the grant" and "the grant exists and
                // consent was withdrawn" are opposite operator actions.
                | Ok() ->
                    match record with
                    | None -> Error "no-grant-record"
                    | Some _ -> Error "evidence-below-declared-policy"
        | policy ->
            // Every other arm is Phase 551's pure predicate, unchanged.
            if GrantPolicy.isGrantLive policy (Map.tryFind moduleName grants) then
                Ok()
            else
                Error(GrantPolicy.inertReason policy (Map.tryFind moduleName grants))

/// **The dispatch control, Phase 551's extended by exactly one arm.**
///
/// `RequiresCounterpartyApproval` is the only case that behaves
/// differently from `GrantPolicyGuard.guardDispatch`, and it needs BOTH
/// halves: a live consent verdict stamped for this request AND an
/// `Active` grant record whose `SatisfiedPolicy` is the same declared arm.
/// Every other case delegates unchanged, so a deployment with no
/// counterparty policy runs the pre-552 path and this function is a single
/// `match`.
///
/// The refusal's `InertReason` carries the CONSENT denial code
/// (`consent-revoked`, `consent-expired`, `consent-signature-invalid`, …)
/// rather than Phase 551's flat `"counterparty-approval-unavailable"`.
/// That is the difference between an operator seeing "this arm is not
/// implemented" and seeing "this consent was withdrawn at 14:02" — and it
/// arrives without a second audit event, because it is the same refusal
/// with better evidence.
///
/// Phase 730 — the decision itself now lives in `dispatchVerdict` above,
/// which the AI-side tool-list filter also runs. This function is that
/// decision plus the audit emission; see `dispatchVerdict` for why the two
/// callers must never be able to disagree.
let guardDispatchWithConsent
    (registry: GrantPolicyGuard.ModuleGrantPolicyRegistry)
    (grants: Map<string, ModuleGrantRecord>)
    (consentVerdicts: Map<string, Result<unit, ConsentDenial>>)
    (auditLog: IAuditLog option)
    (schedule: Async<unit> -> unit)
    (scopeId: string)
    (userId: string)
    (moduleName: string)
    : Result<unit, UnconsentedGrantRefusedPayload> =
    if GrantPolicyGuard.ModuleGrantPolicyRegistry.isEmpty registry then
        Ok()
    else
        match GrantPolicyGuard.ModuleGrantPolicyRegistry.resolve registry moduleName with
        | GrantPolicy.RequiresCounterpartyApproval _ as policy ->
            match dispatchVerdict registry grants consentVerdicts moduleName with
            | Ok() -> Ok()
            | Error inertReason ->
                let payload: UnconsentedGrantRefusedPayload = {
                    UserId = userId
                    ModuleName = moduleName
                    DeclaredPolicy = GrantPolicy.toToken policy
                    InertReason = inertReason
                }

                emit auditLog schedule scopeId (UnconsentedGrantRefused payload)
                Error payload
        | _ -> GrantPolicyGuard.guardDispatch registry grants auditLog schedule scopeId userId moduleName