// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AccessAttestation

open System
open System.Globalization
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.GrantPolicyGuard
open ToolUp.Platform.GrantConsentStore

// ─── Module-access attestation certificate (Phase 557) ───────────────
//
// Phase 551 let a module declare the precondition on being granted to
// anyone; Phase 552 made a counterparty's consent a signed artifact that
// is re-verified on every use; Phase 554 projected the write paths that
// can mint a grant. Every one of those is a control a deployment
// ENFORCES. None of them is a document a deployment can HAND OVER — and
// "prove no-one can be granted access to our data without our consent"
// is asked of a document, in a meeting where nobody can watch the
// dispatch guard refuse a request.
//
// This file is that document: a signed, third-party-verifiable statement
// derived from live composition state —
//
//   "module M's declared grant policy is P; the consent records live at
//    time T are {…}; the grant-authority surface hashes to H; attested by
//    deployment S at T."
//
// — carried as a DSSE-wrapped in-toto Statement, exactly the shape the
// grounding certificate already publishes (`CertificateEnvelope`), so a
// holder verifies it with stock tooling and the deployment's public key,
// offline, and reaches nothing the statement did not say (D16: prove the
// control, reveal nothing else).
//
// **What it attests, and what it deliberately does not.** The live
// approvals are resolved through `GrantConsentStore.resolveLive` — the
// SAME judgement the dispatch guard makes (store → lifecycle → addressing
// → signature) — so the certificate states who a request would find
// consented at T, not what a table happened to contain. A consent record
// appears as its id, its parties, its issue and expiry instants, and
// nothing else: never the counterparty's signature bytes, never the key
// material, never the lodging administrator. A subject whose grant is
// inert (no consent, revoked, expired, unverifiable) does not appear at
// all — the positive claim is the control, and enumerating the refused
// would disclose more than the question asked.
//
// **Why the signing seam is `IStatementEnvelopeSigner` and not
// `IArtefactSigner`.** The phase reuses the Phase 40 key material and
// its public-key endpoint — that is the whole of "no new crypto
// surface" — but through the envelope seam `DsseEnvelope.fs` already
// declares, for two reasons that are both structural. `IArtefactSigner`
// lives in `ToolUp.ArtefactSigning`, which `ProjectReference`s this
// assembly, so this file cannot name it (GP 1; the same reason
// `GrantConsentStore.fs` records for its own verifier). And a detached
// JWS covers the JWS signing input, not the bytes handed to it, so a
// JWS over a DSSE PAE verifies under no standard DSSE implementation —
// the shape a THIRD PARTY needs is the one that stock tooling reads.
// `ToolUp.ArtefactSigning.DsseEnvelopeSigning.fromSecretStore` fills the
// seam from the deployment's own Phase 40 key, under the same key id the
// `/_platform/signing-key/{keyId}` endpoint publishes, and its
// `verifySignature` is the offline cryptographic half a holder composes
// with `verify` below.
//
// **On demand, and only on demand (GP 13).** Nothing here is composed,
// registered, hosted or scheduled. A deployment that never asks for a
// certificate pays nothing; one that does calls `emit` from its own API
// handler or CLI verb, hands it the sources it already composes, and
// receives an envelope. There is no background emitter and there is not
// going to be one — a certificate is a claim about an instant, and the
// instant is the caller's to name.
//
// The format and the verification procedure a third party follows are
// in `docs/security/module-access-attestation.md`.

// ─── Identifiers ─────────────────────────────────────────────────────

/// The predicate-body format discriminator. A verifier refuses a
/// predicate declaring any other value rather than reading what it can.
[<Literal>]
let Format = "toolup.module-access-attestation/v1"

/// The versioned predicate type URI — the open interchange identifier
/// for a module-access attestation carried as an in-toto predicate.
/// Changes only with a new version segment and a compatibility note.
[<Literal>]
let PredicateType = "https://toolup-forge.io/attestations/module-access/v1"

/// The in-toto digest-algorithm key under which the statement's subject
/// carries the module name. A module name is an opaque registration
/// identifier, not a hash, and the in-toto DigestSet convention is that
/// the key says how the value is formed — labelling it `sha256` would be
/// a false claim (the `CertificateEnvelope.OpaqueIdDigestKey` rule).
[<Literal>]
let ModuleNameDigestKey = "toolupModuleName"

// ─── The payload (557.A) ─────────────────────────────────────────────

/// One live approval, summarised to what the claim needs: which record,
/// for whom, from whom, valid from when until when. Deliberately NOT the
/// `GrantConsentRecord` — that carries the counterparty's signature and
/// the lodging administrator, neither of which the certificate reveals.
type AttestedApproval = {
    /// The consent record's own id, so a counterparty can join the line
    /// to the record they signed.
    ConsentId: string
    /// The team whose permission document carries the grant.
    TeamId: string
    /// The principal the grant is for.
    SubjectId: string
    /// The counterparty that approved — always the party the declared
    /// policy names, because `resolveLive` refuses a record addressed to
    /// any other.
    Party: string
    /// When the approval was issued, by the issuing party's clock.
    IssuedAtUtc: DateTimeOffset
    /// When it stops conferring authority; `None` is an open-ended
    /// consent, stated as such rather than hidden behind a sentinel.
    ExpiresAtUtc: DateTimeOffset option
}

/// The attested facts. Every field is either read from composition state
/// at emission or supplied by the caller (the deployment identity), and
/// every field is covered by the envelope signature.
type AccessAttestationPayload = {
    /// The module the statement is about — the `ServerModule.Name` the
    /// policy registry resolves by.
    ModuleName: string
    /// The module's declared `GrantPolicy`, as its stable wire token
    /// (`GrantPolicy.toToken`; the counterparty arm carries its party
    /// after the colon). Kept as the token rather than the union so a
    /// holder reads exactly what was signed: `GrantPolicy.ofToken` is
    /// fail-closed and would silently re-interpret an arm a newer
    /// deployment wrote.
    DeclaredPolicy: string
    /// The teams whose permission documents were enumerated for grants
    /// on the module, sorted ordinally. The certificate's scope, stated
    /// on the certificate: an approval in a team not listed here is
    /// neither asserted nor denied.
    TeamsExamined: string list
    /// The consent records live at `AttestedAtUtc`, sorted by
    /// `(TeamId, SubjectId, ConsentId)`. Empty for a policy that names
    /// no counterparty — the Phase 552 registry holds counterparty
    /// consent only — and empty when nobody is currently consented.
    LiveApprovals: AttestedApproval list
    /// SHA-256 (lowercase hex) over the UTF-8 bytes of the Phase 554
    /// grant-authority manifest (`GrantAuthoritySurface.render`), when
    /// the caller supplied the surface. Absent — never fabricated — when
    /// it did not.
    GrantAuthorityManifestSha256: string option
    /// The deployment's identity, as the caller names it (a build SHA, a
    /// release tag). Opaque to this module.
    DeploymentSha: string
    /// The instant the state was read at, supplied by the caller and
    /// used as the clock for every liveness judgement inside the
    /// certificate. Covered by the signature, so freshness is a checkable
    /// claim rather than a metadata field.
    AttestedAtUtc: DateTimeOffset
}

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

/// UTC round-trip stamp with the `Z` designator — `2026-09-16T12:00:00.0000000Z`
/// — rather than the `+00:00` offset form, which the JSON encoder would
/// escape to `\u002B` and a hand-written verifier would then fail to match.
let private stamp (t: DateTimeOffset) =
    t.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)

/// The canonical predicate JSON for a payload: fixed member order, fixed
/// member names, compact, optional members OMITTED when absent, every
/// instant rendered as a UTC round-trip stamp. Byte-reproducible given
/// identical state — which is what lets two emissions over the same
/// composition be compared, and what the tests assert.
///
/// Hand-assembled rather than serialised from the record, for the
/// reason `GrantConsentRecord.canonicalPayload` gives: a signature is a
/// security-relevant identity and a serialiser's property order is an
/// implementation detail.
let canonicalJson (payload: AccessAttestationPayload) : string =
    let approvals = JsonArray()

    for a in payload.LiveApprovals do
        let o = JsonObject()
        o["consentId"] <- JsonValue.Create(a.ConsentId)
        o["teamId"] <- JsonValue.Create(a.TeamId)
        o["subjectId"] <- JsonValue.Create(a.SubjectId)
        o["party"] <- JsonValue.Create(a.Party)
        o["issuedAtUtc"] <- JsonValue.Create(stamp a.IssuedAtUtc)

        match a.ExpiresAtUtc with
        | Some e -> o["expiresAtUtc"] <- JsonValue.Create(stamp e)
        | None -> ()

        approvals.Add(o)

    let teams = JsonArray()

    for t in payload.TeamsExamined do
        teams.Add(JsonValue.Create(t))

    let o = JsonObject()
    o["format"] <- JsonValue.Create(Format)
    o["module"] <- JsonValue.Create(payload.ModuleName)
    o["declaredPolicy"] <- JsonValue.Create(payload.DeclaredPolicy)
    o["teamsExamined"] <- teams
    o["liveApprovals"] <- approvals

    match payload.GrantAuthorityManifestSha256 with
    | Some h -> o["grantAuthorityManifestSha256"] <- JsonValue.Create(h)
    | None -> ()

    o["deploymentSha"] <- JsonValue.Create(payload.DeploymentSha)
    o["attestedAtUtc"] <- JsonValue.Create(stamp payload.AttestedAtUtc)
    o.ToJsonString()

let private requiredString (node: JsonNode) (name: string) : Result<string, string> =
    match node[name] with
    | null -> Error $"predicate has no '{name}'"
    | v ->
        try
            Ok(v.GetValue<string>())
        with _ ->
            Error $"predicate '{name}' is not a string"

let private parseStamp (name: string) (raw: string) : Result<DateTimeOffset, string> =
    match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
    | true, t -> Ok t
    | false, _ -> Error $"predicate '{name}' is not a round-trip timestamp: {raw}"

let private optionalString (node: JsonNode) (name: string) : Result<string option, string> =
    match node[name] with
    | null -> Ok None
    | _ -> requiredString node name |> Result.map Some

let private stringList (node: JsonNode) (name: string) : Result<string list, string> =
    match node[name] with
    | :? JsonArray as arr ->
        try
            Ok(arr |> Seq.map (fun v -> v.GetValue<string>()) |> List.ofSeq)
        with _ ->
            Error $"predicate '{name}' is not an array of strings"
    | _ -> Error $"predicate has no '{name}' array"

let private readApproval (node: JsonNode) : Result<AttestedApproval, string> =
    let (>>=) r f = Result.bind f r

    requiredString node "consentId"
    >>= fun consentId ->
        requiredString node "teamId"
        >>= fun teamId ->
            requiredString node "subjectId"
            >>= fun subjectId ->
                requiredString node "party"
                >>= fun party ->
                    requiredString node "issuedAtUtc"
                    >>= parseStamp "issuedAtUtc"
                    >>= fun issued ->
                        optionalString node "expiresAtUtc"
                        >>= (function
                        | None -> Ok None
                        | Some raw -> parseStamp "expiresAtUtc" raw |> Result.map Some)
                        |> Result.map (fun expires -> {
                            ConsentId = consentId
                            TeamId = teamId
                            SubjectId = subjectId
                            Party = party
                            IssuedAtUtc = issued
                            ExpiresAtUtc = expires
                        })

/// Read a payload back out of its canonical predicate JSON. Refuses a
/// predicate that is not this format, or that is missing any member the
/// claim depends on, rather than defaulting it. `canonicalJson` and this
/// function round-trip byte-for-byte.
let readPayload (predicateJson: string) : Result<AccessAttestationPayload, string> =
    try
        let node = JsonNode.Parse(predicateJson)

        match node with
        | null -> Error "predicate is not a JSON object"
        | node ->
            let (>>=) r f = Result.bind f r

            requiredString node "format"
            >>= fun format ->
                if format <> Format then
                    Error $"unsupported attestation format: {format}"
                else
                    requiredString node "module"
                    >>= fun moduleName ->
                        requiredString node "declaredPolicy"
                        >>= fun policy ->
                            stringList node "teamsExamined"
                            >>= fun teams ->
                                (match node["liveApprovals"] with
                                 | :? JsonArray as arr ->
                                     arr
                                     |> Seq.fold
                                         (fun acc a ->
                                             acc >>= fun items -> readApproval a |> Result.map (fun x -> x :: items))
                                         (Ok [])
                                     |> Result.map List.rev
                                 | _ -> Error "predicate has no 'liveApprovals' array")
                                >>= fun approvals ->
                                    optionalString node "grantAuthorityManifestSha256"
                                    >>= fun manifest ->
                                        requiredString node "deploymentSha"
                                        >>= fun sha ->
                                            requiredString node "attestedAtUtc"
                                            >>= parseStamp "attestedAtUtc"
                                            |> Result.map (fun attestedAt -> {
                                                ModuleName = moduleName
                                                DeclaredPolicy = policy
                                                TeamsExamined = teams
                                                LiveApprovals = approvals
                                                GrantAuthorityManifestSha256 = manifest
                                                DeploymentSha = sha
                                                AttestedAtUtc = attestedAt
                                            })
    with ex ->
        Error $"predicate is not parseable JSON: {ex.Message}"

/// SHA-256 (lowercase hex) over the UTF-8 bytes of the Phase 554
/// grant-authority manifest's rendered text — `GrantAuthoritySurface.
/// render` is byte-identical across runs over one composition, which is
/// what makes a hash of it a claim about the composition rather than
/// about a run.
let manifestHash (surface: GrantAuthoritySurface) : string =
    DsseEnvelope.sha256Hex (utf8 (GrantAuthoritySurface.render surface))

/// The in-toto subject for a module: the module name, verbatim, under
/// the digest key that says so. A holder claim-checks the statement
/// against the module they are asking about with nothing to translate.
let subjectFor (moduleName: string) : InTotoSubject = {
    Name = moduleName
    Digest = [ ModuleNameDigestKey, moduleName ]
}

/// The unsigned in-toto statement JSON for a payload. Exposed for tests
/// and for a caller signing through its own path.
let statementJson (payload: AccessAttestationPayload) : string =
    DsseEnvelope.statementJson [ subjectFor payload.ModuleName ] PredicateType (canonicalJson payload)

// ─── The emitter (557.B) ─────────────────────────────────────────────

/// What a caller asks to be attested. `TeamIds` is the scope: the
/// permission documents enumerated for grants on the module. The Phase
/// 552 store lists by subject and has no enumeration by module — a
/// deliberate absence, the store cannot be asked who exists — so the
/// grants are found on the documents and the registry is asked about
/// each. `AttestedAt` is the clock every liveness judgement runs on.
type AccessAttestationRequest = {
    /// The module to attest — the registration `Name` the policy
    /// registry resolves by.
    ModuleName: string
    /// The teams whose permission documents are enumerated. Trimmed,
    /// deduplicated and ordinally sorted before they reach the payload.
    TeamIds: string list
    /// The deployment's identity as the caller names it. Opaque here.
    DeploymentSha: string
    /// The instant the state is read at — the certificate's clock.
    AttestedAt: DateTimeOffset
}

/// The composition state the emitter reads. Handed in by the caller
/// rather than resolved from DI so the emitter is a function of its
/// inputs — testable without a host, and never reaching for a service
/// the caller did not mean it to see.
type AccessAttestationSources = {
    /// The Phase 551 registry — the declared policies.
    Registry: ModuleGrantPolicyRegistry
    /// The permission documents the grants are recorded on.
    Permissions: IPermissionStore
    /// The Phase 552 registry. `None` when the deployment composes none,
    /// in which case no counterparty consent can be live and the
    /// certificate says so by listing no approvals.
    Consents: IGrantConsentStore option
    /// The consent-signature verifier dispatch uses. A record that does
    /// not verify is not live and is not attested.
    ConsentVerifier: IGrantConsentVerifier
    /// The Phase 554 surface, when the caller has one to bind. Absent
    /// means the manifest-hash member is omitted, not faked.
    Manifest: GrantAuthoritySurface option
}

/// The result of asking for a certificate. Three arms, because "here is
/// your document", "there is honestly nothing to say" and "I could not
/// find out" must never be confused — and in particular the second must
/// never be shipped as an empty-but-signed claim.
[<RequireQualifiedAccess>]
type AttestationOutcome =
    /// A signed envelope, plus the payload it carries.
    | Attested of envelope: DsseEnvelope * payload: AccessAttestationPayload
    /// The module declares no grant policy (`AdminDiscretion`, or it is
    /// not registered), so there is no control to attest. No envelope is
    /// produced: a signed statement that a module has no policy would be
    /// a document nobody asked for and one that reads, at a glance, like
    /// a certificate.
    | NothingToAttest of reason: string
    /// A source could not be read, or the signer refused. Reported rather
    /// than attested-around: a storage blip must not become a signed
    /// statement that nobody is consented.
    | AttestationFailed of reason: string

/// The subjects a team's permission document records a grant for on
/// `moduleName`, in ordinal order. Only grants written under a declared
/// policy leave a record (Phase 551), so this is exactly the set the
/// consent registry can have an opinion about.
let private grantedSubjects (moduleName: string) (perms: TeamPermissions) : string list =
    perms.Grants
    |> Map.toList
    |> List.choose (fun (subjectId, byModule) ->
        if Map.containsKey moduleName byModule then
            Some subjectId
        else
            None)
    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

/// Resolve the approvals live at `now` for one team, through the same
/// judgement dispatch makes. A store failure aborts the whole enumeration
/// — `Error` — because a partially-read registry cannot be attested. Any
/// other denial simply means the subject is not live and is left out.
let private liveApprovalsFor
    (sources: AccessAttestationSources)
    (now: DateTimeOffset)
    (party: PartyRef)
    (moduleName: string)
    (teamId: string)
    : Async<Result<AttestedApproval list, string>> =
    async {
        let! perms = async {
            try
                let! p = sources.Permissions.GetTeamPermissions teamId
                return Ok p
            with ex ->
                return Error $"permission document for team '{teamId}' could not be read: {ex.Message}"
        }

        match perms with
        | Error e -> return Error e
        | Ok perms ->
            let subjects = grantedSubjects moduleName perms
            let approvals = ResizeArray<AttestedApproval>()
            let mutable failure = None

            for subjectId in subjects do
                if failure.IsNone then
                    let subject = ConsentSubject.create teamId subjectId moduleName

                    // No audit log and no scheduler: an attestation is a
                    // read, and reading must not raise the tamper alert a
                    // request would (the alert belongs to the guard).
                    let! resolved = resolveLive sources.Consents sources.ConsentVerifier None ignore now subject party

                    match resolved with
                    | Ok record ->
                        approvals.Add {
                            ConsentId = record.ConsentId
                            TeamId = teamId
                            SubjectId = subjectId
                            Party = PartyRef.value record.Party
                            IssuedAtUtc = record.IssuedAtUtc
                            ExpiresAtUtc = record.ExpiresAtUtc
                        }
                    | Error(ConsentDenial.StoreUnavailable reason) ->
                        failure <- Some $"consent registry could not be read for team '{teamId}': {reason}"
                    | Error _ -> ()

            match failure with
            | Some e -> return Error e
            | None -> return Ok(List.ofSeq approvals)
    }

/// Assemble the payload from live state, unsigned. The deterministic
/// half of `emit`, exposed so a test can fix the state and assert the
/// bytes, and so a caller can inspect what WOULD be attested before
/// asking for a signature.
let compose
    (sources: AccessAttestationSources)
    (request: AccessAttestationRequest)
    : Async<Result<AccessAttestationPayload option, string>> =
    async {
        let policy = ModuleGrantPolicyRegistry.resolve sources.Registry request.ModuleName

        match policy with
        | GrantPolicy.AdminDiscretion -> return Ok None
        | policy ->
            let teams =
                request.TeamIds
                |> List.map (fun t -> if isNull (box t) then "" else t.Trim())
                |> List.filter (fun t -> t <> "")
                |> List.distinct
                |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

            let! approvals = async {
                match policy with
                | GrantPolicy.RequiresCounterpartyApproval party ->
                    let mutable acc = Ok []

                    for teamId in teams do
                        match acc with
                        | Error _ -> ()
                        | Ok sofar ->
                            let! forTeam = liveApprovalsFor sources request.AttestedAt party request.ModuleName teamId

                            acc <- forTeam |> Result.map (fun a -> sofar @ a)

                    return acc
                | _ ->
                    // The Phase 552 registry holds counterparty consent
                    // only; an acknowledgement or subject-consent policy
                    // has no record there to enumerate, and the
                    // certificate attests the policy and the surface.
                    return Ok []
            }

            match approvals with
            | Error e -> return Error e
            | Ok approvals ->
                let ordered =
                    approvals
                    |> List.sortWith (fun a b ->
                        let byTeam = String.CompareOrdinal(a.TeamId, b.TeamId)

                        if byTeam <> 0 then
                            byTeam
                        else
                            let bySubject = String.CompareOrdinal(a.SubjectId, b.SubjectId)

                            if bySubject <> 0 then
                                bySubject
                            else
                                String.CompareOrdinal(a.ConsentId, b.ConsentId))

                return
                    Ok(
                        Some {
                            ModuleName = request.ModuleName
                            DeclaredPolicy = GrantPolicy.toToken policy
                            TeamsExamined = teams
                            LiveApprovals = ordered
                            GrantAuthorityManifestSha256 = sources.Manifest |> Option.map manifestHash
                            DeploymentSha = request.DeploymentSha
                            AttestedAtUtc = request.AttestedAt
                        }
                    )
    }

/// Emit a signed certificate for one module: compose the payload from
/// live state, then sign it once as a DSSE envelope over an in-toto
/// statement whose subject is the module. On demand only — this is the
/// function an API handler or a CLI verb calls, and nothing calls it on
/// a schedule.
let emit
    (signer: IStatementEnvelopeSigner)
    (sources: AccessAttestationSources)
    (request: AccessAttestationRequest)
    : Async<AttestationOutcome> =
    async {
        match! compose sources request with
        | Error e -> return AttestationOutcome.AttestationFailed e
        | Ok None ->
            return
                AttestationOutcome.NothingToAttest
                    $"module '{request.ModuleName}' declares no grant policy (admin discretion), so there is no control to attest"
        | Ok(Some payload) ->
            match! DsseEnvelope.sign signer [ subjectFor payload.ModuleName ] PredicateType (canonicalJson payload) with
            | Error e -> return AttestationOutcome.AttestationFailed $"could not sign the attestation: {e}"
            | Ok envelope -> return AttestationOutcome.Attested(envelope, payload)
    }

// ─── The independent verifier (557.C) ────────────────────────────────

/// What a holder requires of a certificate before it will read the
/// payload back. Every field is something the holder brought — never
/// something read out of the document.
type AttestationExpectation = {
    /// The module the holder is asking about. `None` skips the subject
    /// and module checks, which is right only when the holder has no
    /// module in mind and will read whichever the document names.
    ModuleName: string option
    /// The holder's clock.
    Now: DateTimeOffset
    /// How old an attestation may be and still be accepted. A certificate
    /// is a claim about the instant it names; past this window it is
    /// history, not evidence about now.
    MaxAge: TimeSpan
    /// How far into the holder's future the attested instant may sit
    /// before the document is refused as not-yet-valid — two honest
    /// clocks a few seconds apart must not produce a refusal.
    ClockSkew: TimeSpan
}

module AttestationExpectation =
    /// The default tolerance for two honest clocks.
    let DefaultClockSkew = TimeSpan.FromMinutes 5.0

    /// An expectation about `moduleName`, judged at `now`, accepting
    /// certificates up to `maxAge` old.
    let create (moduleName: string) (now: DateTimeOffset) (maxAge: TimeSpan) = {
        ModuleName = Some moduleName
        Now = now
        MaxAge = maxAge
        ClockSkew = DefaultClockSkew
    }

/// Why a certificate was refused. Every arm is a refusal; the success
/// case is the payload itself. A holder must never be able to read a
/// refusal as a pass, nor have to tell "this did not verify" from "this
/// is out of date" by inspecting a string.
[<RequireQualifiedAccess>]
type AttestationRefusal =
    /// The envelope did not pass: signature, payload type, predicate
    /// type, or subject. The verdict names which.
    | Envelope of EnvelopeVerdict
    /// The envelope verified but its predicate is not a readable
    /// module-access attestation, or the predicate disagrees with the
    /// statement's own subject about which module it concerns — a
    /// correctly-signed document that says two things. Never a pass.
    | Unreadable of reason: string
    /// The attested instant is older than the holder's window.
    | Stale of attestedAt: DateTimeOffset * now: DateTimeOffset * maxAge: TimeSpan
    /// The attested instant is further into the holder's future than the
    /// skew tolerance — a clock the holder cannot reconcile.
    | NotYetValid of attestedAt: DateTimeOffset * now: DateTimeOffset * skew: TimeSpan

module AttestationRefusal =
    /// Human-readable rendering.
    let describe =
        function
        | AttestationRefusal.Envelope v -> EnvelopeVerdict.describe v
        | AttestationRefusal.Unreadable r -> $"unreadable attestation: {r}"
        | AttestationRefusal.Stale(at, now, maxAge) ->
            $"attestation is stale: attested {stamp at}, judged {stamp now}, window {maxAge}"
        | AttestationRefusal.NotYetValid(at, now, skew) ->
            $"attestation is not yet valid: attested {stamp at}, judged {stamp now}, skew tolerance {skew}"

/// The envelope-level expectation a module-access attestation must meet:
/// this predicate type, and — when the holder named one — the module as
/// the statement's subject.
let envelopeExpectation (moduleName: string option) : EnvelopeExpectation = {
    PredicateType = PredicateType
    SubjectDigest = moduleName
}

/// The freshness judgement, on its own: pure, so it is testable at the
/// boundaries without a signature in sight.
let checkFreshness
    (expectation: AttestationExpectation)
    (payload: AccessAttestationPayload)
    : Result<AccessAttestationPayload, AttestationRefusal> =
    let at = payload.AttestedAtUtc
    let now = expectation.Now

    if at > now + expectation.ClockSkew then
        Error(AttestationRefusal.NotYetValid(at, now, expectation.ClockSkew))
    elif now - at > expectation.MaxAge then
        Error(AttestationRefusal.Stale(at, now, expectation.MaxAge))
    else
        Ok payload

/// Verify a certificate and read its payload back. The payload is
/// returned only on a complete pass, in this order:
///
///   1. the signature, through `checkSignature` — the cryptographic half,
///      supplied by the holder (`ToolUp.ArtefactSigning.DsseEnvelopeSigning.
///      verifySignature publicKey` over the deployment's published key,
///      or any standard DSSE verifier); this assembly carries no
///      signature crypto (GP 1) and so cannot supply it;
///   2. the envelope's shape — payload type, predicate type, subject;
///   3. the predicate — a readable attestation whose `module` agrees
///      with the statement's subject;
///   4. freshness against the holder's clock and window.
///
/// The signature is checked FIRST so every later verdict means what it
/// says: "a correctly-signed statement about a different module" is a
/// claim, and it would be a false one if the subject were compared before
/// anyone had established the document was signed at all
/// (`DsseEnvelopeSigning.verify` gives the same reason for the same
/// order).
let verify
    (checkSignature: DsseEnvelope -> EnvelopeVerdict)
    (expectation: AttestationExpectation)
    (envelope: DsseEnvelope)
    : Result<AccessAttestationPayload, AttestationRefusal> =
    match checkSignature envelope with
    | EnvelopeValid ->
        match DsseEnvelope.checkShape (envelopeExpectation expectation.ModuleName) envelope with
        | Error verdict -> Error(AttestationRefusal.Envelope verdict)
        | Ok statement ->
            match readPayload statement.PredicateJson with
            | Error reason -> Error(AttestationRefusal.Unreadable reason)
            | Ok payload ->
                // The statement's subject and the predicate's own module
                // member are two places one fact is written. A document
                // in which they disagree is refused as unreadable, not as
                // a mismatch against either: it may be perfectly signed
                // and still say two incompatible things.
                if not (statement.SubjectDigests |> List.contains payload.ModuleName) then
                    Error(
                        AttestationRefusal.Unreadable
                            $"predicate names module '{payload.ModuleName}' but the statement's subject does not"
                    )
                else
                    checkFreshness expectation payload
    | verdict -> Error(AttestationRefusal.Envelope verdict)

/// Verify a certificate supplied as JSON text — the shape a holder
/// actually receives (a file, an HTTP body). A parse failure is an
/// envelope-malformed refusal, never a pass.
let verifyJson
    (checkSignature: DsseEnvelope -> EnvelopeVerdict)
    (expectation: AttestationExpectation)
    (json: string)
    : Result<AccessAttestationPayload, AttestationRefusal> =
    match DsseEnvelope.parse json with
    | Error e -> Error(AttestationRefusal.Envelope(EnvelopeMalformed e))
    | Ok envelope -> verify checkSignature expectation envelope