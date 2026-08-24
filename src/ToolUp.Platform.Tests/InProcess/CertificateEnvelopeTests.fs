module ToolUp.Platform.Tests.InProcess.CertificateEnvelopeTests

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Facts
open ToolUp.ArtefactSigning

// ─── Grounding-certificate envelope export (standard-statement interop) ─
//
// Covers the acceptance bar: a certificate exported as a DSSE-wrapped
// in-toto Statement verifies offline against the public key alone; any
// byte change fails; a wrong subject digest and a transplanted signature
// each fail distinctly; and withheld values never appear.
//
// **Cross-tool check — stated honestly.** No independent DSSE
// implementation runs in this repo's CI (adding one would mean a new
// toolchain job for a single fixture), so the interop evidence here is
// two **pinned reference vectors** plus an **independent verification
// path**, not an external tool:
//
//   1. the pre-authentication encoding is asserted against the DSSE
//      specification's own worked example, byte for byte;
//   2. a fixed Ed25519 key (deterministic signatures) over that exact PAE
//      is asserted to produce a fixed, pinned signature — the value any
//      conforming implementation holding the same seed must produce;
//   3. a produced envelope is verified by a path written from the
//      specification text in this file, using only BCL primitives — it
//      re-derives the PAE by hand rather than calling the shipped helper,
//      so a defect in the shipped encoder cannot make it pass.

/// Minimal in-memory `ISecretStore` — signing keys are auto-provisioned
/// into it on first use, and the pinned-vector tests pre-seed one.
type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

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

let private keyId = "grounding-v1"

/// A content-addressed root, the shape `FactId.compute` produces.
let private root =
    DsseEnvelope.sha256Hex (Encoding.UTF8.GetBytes "fact-under-certificate")

let private withheldPolicy = "policy:hr-restricted"
let private disclosedMethod = "computed:sum:v3:9f2c"

/// A certificate body with one disclosable and one withheld fact node —
/// the selective-disclosure shape the envelope must carry unchanged.
let private body: GroundingCertificateBody = {
    Format = GroundingCertificate.Format
    Root = root
    IssuedAt = DateTimeOffset(DateTime(2026, 8, 23, 9, 0, 0, DateTimeKind.Utc))
    DeploymentKeyId = keyId
    Nodes = [
        {
            Id = root
            Kind = "Fact"
            Disclosure = Some "Surfaceable"
            Method = Some disclosedMethod
            CertificateRef = None
            Hash = "aa"
            Withheld = false
        }
        {
            Id = "fact-withheld"
            Kind = "Fact"
            Disclosure = Some "Internal"
            Method = None
            CertificateRef = None
            Hash = "bb"
            Withheld = true
        }
    ]
    Edges = [
        {
            From = root
            To = "fact-withheld"
            Kind = "DerivedFrom"
        }
    ]
    PolicyRefs = [ withheldPolicy ]
}

/// Issue a certificate through the ordinary Phase 40 signing path, so the
/// envelope wraps a genuinely-sealed certificate.
let private issueCertificate (secrets: ISecretStore) (algorithm: SigningAlgorithm) = async {
    let audit = AuditLog.NoOpAuditLog() :> IAuditLog
    let signer = DefaultArtefactSigner.createSystem secrets audit keyId algorithm
    let canonical = GroundingCertificate.canonicalise body

    match! signer.Sign(GroundingCertificate.canonicalBytes canonical) with
    | Error e -> return failwithf "could not sign certificate: %s" (SigningError.describe e)
    | Ok signature ->
        let! publicKey = signer.VerifyKey()

        return
            {
                Body = canonical
                Signature = signature
            },
            publicKey
}

/// Issue a certificate and export it as a signed envelope.
let private exportEnvelope (secrets: ISecretStore) (algorithm: SigningAlgorithm) = async {
    let! certificate, publicKey = issueCertificate secrets algorithm
    let envelopeSigner = DsseEnvelopeSigning.fromSecretStore secrets keyId algorithm

    match! CertificateEnvelope.export envelopeSigner certificate with
    | Error e -> return failwithf "could not export envelope: %s" e
    | Ok envelope -> return certificate, publicKey, envelope
}

// ── the attested projection (Phase 692) ────────────────────────────────

/// An `IApplicationSigner` over `secrets` at a chosen attestation level.
///
/// `ApplicationSigning.inProcess` fixes the level at `Attribution`, which
/// is the honest claim for a key the signing process can read. A provider
/// record built by hand is how a test reaches the other levels — the
/// custody has not changed, so this is a test fixture and never a pattern
/// for a composition root.
let private applicationSignerAt
    (secrets: ISecretStore)
    (algorithm: SigningAlgorithm)
    (level: AttestationLevel)
    : IApplicationSigner =
    let audit = AuditLog.NoOpAuditLog() :> IAuditLog

    {
        ApplicationSigning.inProcess secrets audit keyId algorithm "system" with
            Level = level
    }
    |> ApplicationSigning.create

/// Seal the shared body through the application signing seam — the Phase
/// 682 attested form of the very certificate `issueCertificate` produces
/// directly. Same body, same bytes; a different seal.
let private issueAttested (secrets: ISecretStore) (algorithm: SigningAlgorithm) (level: AttestationLevel) = async {
    let signer = applicationSignerAt secrets algorithm level
    let canonical = GroundingCertificate.canonicalise body
    let payload = GroundingCertificate.canonicalBytes canonical

    match! signer.SignPayload(GroundingCertificate.AttestationPurpose, payload) with
    | Error e -> return failwithf "could not seal the attested certificate: %s" (SigningError.describe e)
    | Ok envelope ->
        // The DSSE envelope is signed by the deployment's own key, read
        // back through the artefact signer the application signer
        // provisioned it with — one key story, two artefacts.
        let audit = AuditLog.NoOpAuditLog() :> IAuditLog
        let byteSigner = DefaultArtefactSigner.createSystem secrets audit keyId algorithm
        let! publicKey = byteSigner.VerifyKey()

        return
            {
                Body = canonical
                Envelope = envelope
            },
            publicKey,
            signer
}

/// Issue an attested certificate and export it as a signed envelope.
let private exportAttestedEnvelope (secrets: ISecretStore) (algorithm: SigningAlgorithm) (level: AttestationLevel) = async {
    let! certificate, publicKey, signer = issueAttested secrets algorithm level
    let envelopeSigner = DsseEnvelopeSigning.fromSecretStore secrets keyId algorithm

    match! CertificateEnvelope.exportAttested envelopeSigner certificate with
    | Error e -> return failwithf "could not export the attested envelope: %s" e
    | Ok envelope -> return certificate, publicKey, envelope, signer
}

/// The statement an attested envelope carries, as text.
let private statementTextOf (envelope: DsseEnvelope) =
    match DsseEnvelope.payloadBytes envelope with
    | Ok b -> Encoding.UTF8.GetString b
    | Error e -> failwithf "payload not decodable: %s" e

/// The persisted shape of a signing key in `ISecretStore` — written by
/// hand so a pinned key can be seeded without generating one.
let private storedKeyJson (algorithm: string) (privateKey: string) (publicKey: string) =
    let o = JsonObject()
    o["alg"] <- JsonValue.Create(algorithm)
    o["private"] <- JsonValue.Create(privateKey)
    o["public"] <- JsonValue.Create(publicKey)
    o["createdAt"] <- JsonValue.Create(DateTimeOffset(DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToString("O"))
    o.ToJsonString()

// The DSSE specification's own worked example.
let private specPayloadType = "http://example.com/HelloWorld"
let private specPayload = "hello world"
let private specPae = "DSSEv1 29 http://example.com/HelloWorld 11 hello world"

// A fixed Ed25519 seed (bytes 0x01..0x20) and the signature it must
// produce over `specPae`. Ed25519 signatures are deterministic, so this
// is a genuine cross-implementation vector: any conforming signer holding
// this seed produces exactly these bytes.
let private pinnedSeedB64 = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA="
let private pinnedPublicB64 = "ebVWLo/mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ="

let private pinnedSignatureB64 =
    "gsW+/22ZLLBkSDnEUODXvAxct/LnTGmqZZeEK/UdnwLgH2x7+2uXk07zvthSIA2e5+ccUDOx7xvLKregwslcCQ=="

// ── the attested projection's reference vector (Phase 692) ─────────────
//
// Every input below is a literal, including the inner application seal —
// a real seal carries a wall-clock `SignedAt`, so a certificate issued
// through a live signer cannot be pinned. What IS pinned is therefore the
// PROJECTION: given exactly this attested certificate, the statement
// bytes and the envelope signature over their PAE are fixed.
//
// **The claim, stated at its real strength** (the Phase 681 / 1028
// convention). The signature vector is genuinely cross-implementation:
// Ed25519 is deterministic, so any conforming signer holding the pinned
// seed must produce exactly these bytes over exactly these statement
// bytes. The STATEMENT bytes are the weaker half — they are what this
// SDK's serialiser emits for this record, so the digest pins the
// predicate shape against drift rather than asserting that an independent
// implementation would emit the same JSON. A change to either is caught,
// and the digest is what names WHICH changed.
let private pinnedAttested: AttestedGroundingCertificate = {
    Body = GroundingCertificate.canonicalise body
    Envelope = {
        Purpose = GroundingCertificate.AttestationPurpose
        Level = Attribution
        Signature = {
            KeyId = keyId
            Algorithm = Ed25519
            SignedAt = DateTimeOffset(DateTime(2026, 8, 23, 9, 0, 5, DateTimeKind.Utc))
            DetachedJws = "eyJhbGciOiJFZERTQSIsImtpZCI6Imdyb3VuZGluZy12MSJ9..cGlubmVkLXNlYWw"
        }
    }
}

/// SHA-256 over the pinned attested statement's UTF-8 bytes.
let private pinnedAttestedStatementDigest =
    "d2eb5d03f716661e5187fde6ee22ba22cc1b26e4e98a67cbc4e486ec78b95f5d"

/// The Ed25519 signature the pinned seed must produce over the PAE of
/// those statement bytes.
let private pinnedAttestedSignatureB64 =
    "AvpUlie9tVNqXblGl1sK4ug4U5S474c/MTLSdWJ7d5U8ZmS43BOu6gTixPbkxlyfW7bCtBWCo73ON6yDwGbWCw=="

let tests =
    testList "grounding-certificate envelope export" [

        // ── the encoding, against the specification ──────────────────────

        test "pre-authentication encoding matches the DSSE specification's worked example" {
            let pae = DsseEnvelope.pae specPayloadType (Encoding.UTF8.GetBytes specPayload)

            Expect.equal (Encoding.UTF8.GetString pae) specPae "PAE must be byte-identical to the spec example"
        }

        test "pre-authentication encoding counts bytes, not characters" {
            // "héllo" is 5 characters but 6 UTF-8 bytes. A
            // character-count length would make two different payloads
            // encode identically, which is the ambiguity PAE exists to
            // remove.
            let payload = Encoding.UTF8.GetBytes "héllo"
            let pae = DsseEnvelope.pae "test" payload

            Expect.equal (Encoding.UTF8.GetString pae) "DSSEv1 4 test 6 héllo" "length prefixes are byte counts"
        }

        testAsync "a pinned Ed25519 key produces the pinned signature over the spec PAE" {
            let secrets = InMemorySecretStore() :> ISecretStore

            do!
                secrets.SetSecret(
                    "_platform",
                    $"signing/{keyId}",
                    storedKeyJson "Ed25519" pinnedSeedB64 pinnedPublicB64
                )
                |> Async.Ignore

            let signer = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519
            let pae = DsseEnvelope.pae specPayloadType (Encoding.UTF8.GetBytes specPayload)

            match! signer.SignPreAuthenticated pae with
            | Error e -> failtestf "pinned-key signing failed: %s" e
            | Ok signature ->
                Expect.equal
                    (Convert.ToBase64String signature.Signature)
                    pinnedSignatureB64
                    "the signature over the pinned key + spec PAE is fixed across implementations"
        }

        // ── round trip ───────────────────────────────────────────────────

        testAsync "an exported certificate verifies offline against the public key alone (Ed25519)" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! certificate, publicKey, envelope = exportEnvelope secrets Ed25519

            match CertificateEnvelope.verifyAndRead publicKey (Some root) envelope with
            | Error v -> failtestf "expected a valid envelope, got: %s" (EnvelopeVerdict.describe v)
            | Ok read ->
                Expect.equal read.Body.Root certificate.Body.Root "the certificate root round-trips"
                Expect.equal read.Signature.KeyId certificate.Signature.KeyId "the inner seal round-trips"
        }

        testAsync "an exported certificate verifies offline against the public key alone (ECDSA P-256)" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets EcdsaP256

            match CertificateEnvelope.verifyAndRead publicKey (Some root) envelope with
            | Error v -> failtestf "expected a valid envelope, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> ()
        }

        testAsync "the envelope survives its JSON wire form" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets Ed25519
            let json = DsseEnvelope.toJson envelope

            // The wire field names are the DSSE ones, not F# record names.
            Expect.stringContains json "\"payloadType\"" "carries payloadType"
            Expect.stringContains json "\"keyid\"" "signature entries carry keyid"

            match CertificateEnvelope.verifyAndReadJson publicKey (Some root) json with
            | Error v -> failtestf "expected a valid envelope from JSON, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> ()
        }

        testAsync "the statement carries the in-toto shape the predicate type promises" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, _, envelope = exportEnvelope secrets Ed25519

            Expect.equal envelope.PayloadType DsseEnvelope.InTotoPayloadType "payload type is the in-toto media type"

            let statement =
                Encoding.UTF8.GetString(
                    match DsseEnvelope.payloadBytes envelope with
                    | Ok b -> b
                    | Error e -> failtestf "payload not decodable: %s" e
                )

            let node = JsonNode.Parse statement

            Expect.equal (node["_type"].GetValue<string>()) DsseEnvelope.StatementType "statement _type"

            Expect.equal
                (node["predicateType"].GetValue<string>())
                CertificateEnvelope.PredicateType
                "predicate type is the versioned certificate URI"

            let subjectNode = node["subject"]
            let firstSubject = subjectNode.Item 0
            let digestSet = firstSubject.Item "digest"
            let digestNode = digestSet.Item "sha256"

            Expect.equal
                (digestNode.GetValue<string>())
                root
                "a content-addressed root is published under the sha256 digest key, verbatim"
        }

        testAsync "the certificate round-trips through the envelope with its sealed bytes intact" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! certificate, publicKey, envelope = exportEnvelope secrets Ed25519

            match CertificateEnvelope.verifyAndRead publicKey (Some root) envelope with
            | Error v -> failtestf "expected a valid envelope, got: %s" (EnvelopeVerdict.describe v)
            | Ok read ->
                // The envelope carries the Phase 565 seal rather than
                // replacing it: the bytes that seal covers must survive
                // the round trip byte-for-byte, or the second,
                // independent verification path is lost.
                Expect.equal
                    (CertificateEnvelope.sealedBytes read)
                    (GroundingCertificate.canonicalBytes certificate.Body)
                    "the canonical sealed bytes are unchanged by wrapping"
        }

        // ── refusals, each distinct ──────────────────────────────────────

        testAsync "any byte change to the payload fails verification" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets Ed25519

            let original =
                match DsseEnvelope.payloadBytes envelope with
                | Ok b -> b
                | Error e -> failtestf "payload not decodable: %s" e

            let mutated = Array.copy original
            mutated[mutated.Length / 2] <- mutated[mutated.Length / 2] ^^^ 0x01uy

            let tampered = {
                envelope with
                    Payload = Convert.ToBase64String mutated
            }

            match CertificateEnvelope.verifyAndRead publicKey (Some root) tampered with
            | Error EnvelopeSignatureInvalid -> ()
            | Error v -> failtestf "expected EnvelopeSignatureInvalid, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> failtest "a tampered envelope must never verify"
        }

        testAsync "a correctly-signed statement about a different artefact is refused on the subject" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets Ed25519
            let otherRoot = DsseEnvelope.sha256Hex (Encoding.UTF8.GetBytes "a different fact")

            match CertificateEnvelope.verifyAndRead publicKey (Some otherRoot) envelope with
            | Error(EnvelopeSubjectMismatch(expected, _)) ->
                Expect.equal expected otherRoot "the verdict names the digest the holder brought"
            | Error v -> failtestf "expected EnvelopeSubjectMismatch, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> failtest "an envelope about another artefact must not satisfy this holder"
        }

        testAsync "the signature is checked before the subject, so a structural verdict implies a valid signature" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets Ed25519

            let original =
                match DsseEnvelope.payloadBytes envelope with
                | Ok b -> b
                | Error e -> failtestf "payload not decodable: %s" e

            let mutated = Array.copy original
            mutated[0] <- mutated[0] ^^^ 0x01uy

            let tampered = {
                envelope with
                    Payload = Convert.ToBase64String mutated
            }

            // Both things are wrong: the bytes were altered AND the holder
            // is asking about a different artefact. The answer must be the
            // signature one — reporting a subject mismatch here would tell
            // a holder the document is authentic and merely about the
            // wrong thing.
            let otherRoot = DsseEnvelope.sha256Hex (Encoding.UTF8.GetBytes "yet another fact")

            match CertificateEnvelope.verifyAndRead publicKey (Some otherRoot) tampered with
            | Error EnvelopeSignatureInvalid -> ()
            | Error v ->
                failtestf "expected EnvelopeSignatureInvalid to take precedence, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> failtest "a tampered envelope must never verify"
        }

        testAsync "a signature block transplanted from another key is refused as unsigned for this key" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets Ed25519

            let transplanted = {
                envelope with
                    Signatures = [
                        {
                            KeyId = "some-other-deployment-key"
                            Sig = envelope.Signatures.Head.Sig
                        }
                    ]
            }

            match CertificateEnvelope.verifyAndRead publicKey (Some root) transplanted with
            | Error(EnvelopeUnsignedForKey k) -> Expect.equal k keyId "the verdict names the key that was expected"
            | Error v -> failtestf "expected EnvelopeUnsignedForKey, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> failtest "an envelope carrying no signature for this key must not verify"
        }

        testAsync "a signature transplanted from another envelope under the SAME key still fails" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets Ed25519

            // A statement about a different root, signed by the same
            // deployment. Lifting its signature onto this envelope is
            // cryptographically indistinguishable from tampering — and is
            // reported as such, which is the honest verdict.
            let envelopeSigner = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519
            let audit = AuditLog.NoOpAuditLog() :> IAuditLog
            let artefactSigner = DefaultArtefactSigner.createSystem secrets audit keyId Ed25519

            let otherBody =
                GroundingCertificate.canonicalise {
                    body with
                        Root = DsseEnvelope.sha256Hex (Encoding.UTF8.GetBytes "another fact entirely")
                }

            let! otherSeal = artefactSigner.Sign(GroundingCertificate.canonicalBytes otherBody)

            let otherCertificate = {
                Body = otherBody
                Signature =
                    match otherSeal with
                    | Ok s -> s
                    | Error e -> failtestf "could not seal the second certificate: %s" (SigningError.describe e)
            }

            let! other = CertificateEnvelope.export envelopeSigner otherCertificate

            let otherEnvelope =
                match other with
                | Ok e -> e
                | Error e -> failtestf "could not export the second envelope: %s" e

            let transplanted = {
                envelope with
                    Signatures = otherEnvelope.Signatures
            }

            match CertificateEnvelope.verifyAndRead publicKey (Some root) transplanted with
            | Error EnvelopeSignatureInvalid -> ()
            | Error v -> failtestf "expected EnvelopeSignatureInvalid, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> failtest "a transplanted signature must not verify"
        }

        testAsync "a statement of another predicate type is refused rather than parsed hopefully" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! certificate, publicKey = issueCertificate secrets Ed25519
            let envelopeSigner = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519

            let! foreign =
                DsseEnvelope.sign
                    envelopeSigner
                    [ CertificateEnvelope.subjectFor certificate.Body.Root ]
                    "https://example.com/some-other-claim/v1"
                    (CertificateEnvelope.predicateJson certificate)

            let envelope =
                match foreign with
                | Ok e -> e
                | Error e -> failtestf "could not export the foreign envelope: %s" e

            match CertificateEnvelope.verifyAndRead publicKey (Some root) envelope with
            | Error(EnvelopePredicateTypeMismatch(expected, actual)) ->
                Expect.equal expected CertificateEnvelope.PredicateType "the verdict names the expected type"
                Expect.equal actual "https://example.com/some-other-claim/v1" "and the one found"
            | Error v -> failtestf "expected EnvelopePredicateTypeMismatch, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> failtest "a foreign predicate type must not be read as a certificate"
        }

        testAsync "a malformed envelope is refused as malformed, never as a signature failure" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets Ed25519

            let broken = {
                envelope with
                    Payload = "not base64 at all!!"
            }

            match CertificateEnvelope.verifyAndRead publicKey (Some root) broken with
            | Error(EnvelopeMalformed _) -> ()
            | Error v -> failtestf "expected EnvelopeMalformed, got: %s" (EnvelopeVerdict.describe v)
            | Ok _ -> failtest "an unreadable envelope must never verify"
        }

        test "no verdict other than EnvelopeValid reads as valid" {
            let refusals = [
                EnvelopeMalformed "x"
                EnvelopePayloadTypeMismatch("a", "b")
                EnvelopePredicateTypeMismatch("a", "b")
                EnvelopeSubjectMismatch("a", "b")
                EnvelopeUnsignedForKey "k"
                EnvelopeSignatureInvalid
            ]

            Expect.isTrue (EnvelopeVerdict.isValid EnvelopeValid) "the pass reads as a pass"

            for r in refusals do
                Expect.isFalse
                    (EnvelopeVerdict.isValid r)
                    $"refusal must not read as a pass: {EnvelopeVerdict.describe r}"
        }

        // ── disclosure ───────────────────────────────────────────────────

        testAsync "a withheld node contributes id and policy ref only — never its method" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, _, envelope = exportEnvelope secrets Ed25519

            let statement =
                Encoding.UTF8.GetString(
                    match DsseEnvelope.payloadBytes envelope with
                    | Ok b -> b
                    | Error e -> failtestf "payload not decodable: %s" e
                )

            Expect.stringContains statement "fact-withheld" "the withheld node's id is disclosed"
            Expect.stringContains statement withheldPolicy "and the policy it was withheld under"
            Expect.stringContains statement disclosedMethod "a disclosable node keeps its method identity"

            // The projection the issuer produced is carried verbatim;
            // wrapping cannot widen it.
            let statementNode = JsonNode.Parse statement
            let predicateNode = statementNode.Item "predicate"
            let bodyNode = predicateNode.Item "Body"
            let nodesNode = bodyNode.Item "Nodes"

            let withheldNode =
                (nodesNode :?> JsonArray)
                |> Seq.find (fun n ->
                    let id = n.Item "Id"
                    id.GetValue<string>() = "fact-withheld")

            let withheldFlag = withheldNode.Item "Withheld"

            Expect.isTrue (withheldFlag.GetValue<bool>()) "the withheld flag survives"
        }

        // ── independent verification path ────────────────────────────────

        testAsync "an envelope verifies through a path written from the specification, not from our helper" {
            let secrets = InMemorySecretStore() :> ISecretStore
            let! _, publicKey, envelope = exportEnvelope secrets EcdsaP256

            // Everything below is derived from the DSSE / in-toto
            // specification text and BCL primitives only. It does not call
            // DsseEnvelope.pae or DsseEnvelopeSigning.verify, so a defect
            // in either cannot make this pass.
            let payload = Convert.FromBase64String envelope.Payload
            let typeBytes = Encoding.UTF8.GetBytes envelope.PayloadType

            let message =
                Array.append
                    (Encoding.UTF8.GetBytes(
                        "DSSEv1 "
                        + string typeBytes.Length
                        + " "
                        + envelope.PayloadType
                        + " "
                        + string payload.Length
                        + " "
                    ))
                    payload

            let signature = Convert.FromBase64String envelope.Signatures.Head.Sig

            use ecdsa = ECDsa.Create()
            ecdsa.ImportFromPem publicKey.Pem

            Expect.isTrue
                (ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                "a verifier built from the spec text accepts the envelope"
        }

        // ── the attested certificate's projection (Phase 692) ────────────
        //
        // Phase 682 sealed the same body through the application signing
        // seam, binding the purpose and the attestation level into the
        // signed bytes — and left that form with no standard-tooling
        // carrier, so a holder of the levels-bound certificate had to fall
        // back to the direct path to get one. These cases cover the
        // projection that closes it: the same subject, the same envelope
        // machinery, the same distinct tamper verdicts, and the level and
        // key id readable as plain strings by a party that has never heard
        // of this SDK's framing.

        testList "attested certificate projection" [

            // ── round trip ───────────────────────────────────────────────

            testAsync "an exported attested certificate verifies offline against the public key alone (Ed25519)" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! certificate, publicKey, envelope, _ = exportAttestedEnvelope secrets Ed25519 Attribution

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) envelope with
                | Error v -> failtestf "expected a valid attested envelope, got: %s" (EnvelopeVerdict.describe v)
                | Ok read ->
                    Expect.equal read.Body.Root certificate.Body.Root "the certificate root round-trips"
                    Expect.equal read.Envelope.Level certificate.Envelope.Level "the attestation level round-trips"

                    Expect.equal
                        read.Envelope.Purpose
                        GroundingCertificate.AttestationPurpose
                        "and the purpose it was sealed under"
            }

            testAsync "an exported attested certificate verifies offline against the public key alone (ECDSA P-256)" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, publicKey, envelope, _ = exportAttestedEnvelope secrets EcdsaP256 Attribution

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) envelope with
                | Error v -> failtestf "expected a valid attested envelope, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> ()
            }

            testAsync "the attested envelope survives its JSON wire form" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, publicKey, envelope, _ = exportAttestedEnvelope secrets Ed25519 Attribution
                let json = DsseEnvelope.toJson envelope

                match CertificateEnvelope.verifyAndReadAttestedJson publicKey (Some root) json with
                | Error v ->
                    failtestf "expected a valid attested envelope from JSON, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> ()
            }

            // ── what the predicate surfaces ──────────────────────────────

            testAsync "the predicate publishes the attestation level, purpose and key id as plain strings" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, _, envelope, _ = exportAttestedEnvelope secrets Ed25519 Attribution

                let node = JsonNode.Parse(statementTextOf envelope)

                Expect.equal
                    (node["predicateType"].GetValue<string>())
                    CertificateEnvelope.AttestedPredicateType
                    "the attested projection has its own versioned predicate type"

                let predicate = node.Item "predicate"

                // A reader that knows nothing of this SDK's DU
                // serialisation or the seam's framing can still read all
                // three, because they are strings in the stable wire names
                // the signature covers.
                Expect.equal
                    ((predicate.Item CertificateEnvelope.AttestationLevelField).GetValue<string>())
                    "attribution"
                    "the attestation level is surfaced under its stable wire name"

                Expect.equal
                    ((predicate.Item CertificateEnvelope.PurposeField).GetValue<string>())
                    GroundingCertificate.AttestationPurpose
                    "as is the purpose the seal was minted for"

                Expect.equal
                    ((predicate.Item CertificateEnvelope.SigningKeyIdField).GetValue<string>())
                    keyId
                    "and the signing-key id the body binds"

                // And the certificate itself is still there, whole.
                let bodyNode = (predicate.Item CertificateEnvelope.CertificateField).Item "Body"

                Expect.equal (bodyNode.Item("Root").GetValue<string>()) root "the certificate record travels intact"
            }

            testAsync "the surfaced level is the one the seal claims, not a constant" {
                // The same code path at a different level must say so. A
                // hardcoded string would pass every test above and this one
                // is what makes the surfaced value evidence rather than
                // decoration.
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, _, envelope, _ = exportAttestedEnvelope secrets Ed25519 IsolatedSigner

                let node = JsonNode.Parse(statementTextOf envelope)
                let predicate = node.Item "predicate"

                Expect.equal
                    ((predicate.Item CertificateEnvelope.AttestationLevelField).GetValue<string>())
                    "isolated-signer"
                    "a stronger claim is surfaced as the stronger claim"
            }

            testAsync "an attestation level this build does not understand round-trips rather than failing to parse" {
                // `AttestationLevel.parse` promises a reserved label
                // round-trips; the projection must not be where that
                // promise is quietly broken.
                let secrets = InMemorySecretStore() :> ISecretStore
                let level = Reserved "hardware-quote"
                let! _, publicKey, envelope, _ = exportAttestedEnvelope secrets Ed25519 level

                let node = JsonNode.Parse(statementTextOf envelope)
                let predicate = node.Item "predicate"

                Expect.equal
                    ((predicate.Item CertificateEnvelope.AttestationLevelField).GetValue<string>())
                    "reserved:hardware-quote"
                    "the reserved label is surfaced verbatim"

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) envelope with
                | Error v ->
                    failtestf "a reserved level must not make the document unreadable: %s" (EnvelopeVerdict.describe v)
                | Ok read -> Expect.equal read.Envelope.Level level "and the level itself round-trips"
            }

            testAsync "both projections publish the same subject digest over the same body" {
                // Phase 682 pinned the two issuers' bodies byte-identical.
                // This is the consequence a holder actually uses: one root
                // id claim-checks either document, with nothing to
                // translate.
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, _, direct = exportEnvelope secrets Ed25519
                let! _, _, attested, _ = exportAttestedEnvelope secrets Ed25519 Attribution

                let digestsOf envelope =
                    let node = JsonNode.Parse(statementTextOf envelope)
                    let subject = (node.Item "subject").Item 0

                    (subject.Item "digest").AsObject()
                    |> Seq.map (fun kv -> kv.Key, kv.Value.GetValue<string>())
                    |> List.ofSeq

                Expect.equal (digestsOf attested) (digestsOf direct) "one subject, two projections"
                Expect.equal (digestsOf attested) [ "sha256", root ] "and it is the content-addressed root, verbatim"
            }

            testAsync "the attested certificate round-trips with the bytes its application seal covers intact" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! certificate, publicKey, envelope, signer = exportAttestedEnvelope secrets Ed25519 Attribution

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) envelope with
                | Error v -> failtestf "expected a valid attested envelope, got: %s" (EnvelopeVerdict.describe v)
                | Ok read ->
                    Expect.equal
                        (CertificateEnvelope.attestedSealedBytes read)
                        (GroundingCertificate.canonicalBytes certificate.Body)
                        "the canonical sealed bytes are unchanged by wrapping"

                    // The envelope carries the Phase 682 seal rather than
                    // replacing it: the second, independent check must
                    // still pass on the document that came back out.
                    match! GroundingCertificate.verifyAttestedFor signer root read with
                    | AttestedCertificateValid -> ()
                    | verdict ->
                        failtestf
                            "the inner application seal must survive the round trip; got %s"
                            (AttestedCertificateVerdict.describe verdict)
            }

            // ── refusals, each distinct (the Phase 681 triple) ───────────

            testAsync "any byte change to the attested payload fails verification" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, publicKey, envelope, _ = exportAttestedEnvelope secrets Ed25519 Attribution

                let original =
                    match DsseEnvelope.payloadBytes envelope with
                    | Ok b -> b
                    | Error e -> failtestf "payload not decodable: %s" e

                let mutated = Array.copy original
                mutated[mutated.Length / 2] <- mutated[mutated.Length / 2] ^^^ 0x01uy

                let tampered = {
                    envelope with
                        Payload = Convert.ToBase64String mutated
                }

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) tampered with
                | Error EnvelopeSignatureInvalid -> ()
                | Error v -> failtestf "expected EnvelopeSignatureInvalid, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> failtest "a tampered attested envelope must never verify"
            }

            testAsync "a correctly-signed attested statement about a different artefact is refused on the subject" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, publicKey, envelope, _ = exportAttestedEnvelope secrets Ed25519 Attribution
                let otherRoot = DsseEnvelope.sha256Hex (Encoding.UTF8.GetBytes "a different fact")

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some otherRoot) envelope with
                | Error(EnvelopeSubjectMismatch(expected, _)) ->
                    Expect.equal expected otherRoot "the verdict names the digest the holder brought"
                | Error v -> failtestf "expected EnvelopeSubjectMismatch, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> failtest "an attested envelope about another artefact must not satisfy this holder"
            }

            testAsync "an attested signature block from another key is refused as unsigned for this key" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, publicKey, envelope, _ = exportAttestedEnvelope secrets Ed25519 Attribution

                let transplanted = {
                    envelope with
                        Signatures = [
                            {
                                KeyId = "some-other-deployment-key"
                                Sig = envelope.Signatures.Head.Sig
                            }
                        ]
                }

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) transplanted with
                | Error(EnvelopeUnsignedForKey k) -> Expect.equal k keyId "the verdict names the key that was expected"
                | Error v -> failtestf "expected EnvelopeUnsignedForKey, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> failtest "an attested envelope carrying no signature for this key must not verify"
            }

            testAsync "the signature is checked before the shape on the attested path too" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, publicKey, envelope, _ = exportAttestedEnvelope secrets Ed25519 Attribution

                let original =
                    match DsseEnvelope.payloadBytes envelope with
                    | Ok b -> b
                    | Error e -> failtestf "payload not decodable: %s" e

                let mutated = Array.copy original
                mutated[0] <- mutated[0] ^^^ 0x01uy

                let tampered = {
                    envelope with
                        Payload = Convert.ToBase64String mutated
                }

                // Both things are wrong at once. The answer must be the
                // signature one — a subject verdict here would tell a
                // holder the document is authentic and merely misfiled.
                let otherRoot = DsseEnvelope.sha256Hex (Encoding.UTF8.GetBytes "yet another fact")

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some otherRoot) tampered with
                | Error EnvelopeSignatureInvalid -> ()
                | Error v ->
                    failtestf
                        "expected EnvelopeSignatureInvalid to take precedence, got: %s"
                        (EnvelopeVerdict.describe v)
                | Ok _ -> failtest "a tampered attested envelope must never verify"
            }

            // ── the two projections do not substitute for each other ─────

            testAsync "a direct certificate envelope is refused by the attested reader, and the reverse" {
                // Same key, same subject, same body — and different
                // claims. Reading one as the other would hand a holder a
                // document with no level in it while they believed they
                // had checked one.
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, publicKey, direct = exportEnvelope secrets Ed25519
                let! _, _, attested, _ = exportAttestedEnvelope secrets Ed25519 Attribution

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) direct with
                | Error(EnvelopePredicateTypeMismatch(expected, actual)) ->
                    Expect.equal expected CertificateEnvelope.AttestedPredicateType "the attested reader names its type"
                    Expect.equal actual CertificateEnvelope.PredicateType "and the direct one it was handed"
                | Error v -> failtestf "expected EnvelopePredicateTypeMismatch, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> failtest "a direct envelope must not be read as an attested one"

                match CertificateEnvelope.verifyAndRead publicKey (Some root) attested with
                | Error(EnvelopePredicateTypeMismatch(expected, actual)) ->
                    Expect.equal expected CertificateEnvelope.PredicateType "the direct reader names its type"
                    Expect.equal actual CertificateEnvelope.AttestedPredicateType "and the attested one it was handed"
                | Error v -> failtestf "expected EnvelopePredicateTypeMismatch, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> failtest "an attested envelope must not be read as a direct one"
            }

            // ── the surfaced fields cannot outrun the seal ───────────────

            testAsync "a surfaced level contradicting the seal is refused, however well the envelope is signed" {
                // This is the failure the surfacing invites: a document
                // whose plain-string level says more than the sealed one,
                // signed perfectly by the deployment's own envelope key. A
                // reader who trusted the surfaced field would be trusting
                // a claim the seal never made.
                let secrets = InMemorySecretStore() :> ISecretStore
                let! certificate, publicKey, _ = issueAttested secrets Ed25519 Attribution
                let envelopeSigner = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519

                let predicate =
                    JsonNode.Parse(CertificateEnvelope.attestedPredicateJson certificate)

                predicate[CertificateEnvelope.AttestationLevelField] <- JsonValue.Create "isolated-signer"

                let! forged =
                    DsseEnvelope.sign
                        envelopeSigner
                        [ CertificateEnvelope.subjectFor root ]
                        CertificateEnvelope.AttestedPredicateType
                        (predicate.ToJsonString())

                let envelope =
                    match forged with
                    | Ok e -> e
                    | Error e -> failtestf "could not sign the doctored statement: %s" e

                // The envelope signature itself is sound — which is the
                // whole point of the case.
                Expect.equal
                    (DsseEnvelopeSigning.verifySignature publicKey envelope)
                    EnvelopeValid
                    "the doctored statement is genuinely signed"

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) envelope with
                | Error(EnvelopeMalformed reason) ->
                    Expect.stringContains reason "isolated-signer" "the refusal names the surfaced claim"
                    Expect.stringContains reason "attribution" "and the sealed one it contradicts"
                | Error v -> failtestf "expected EnvelopeMalformed, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> failtest "a surfaced field that contradicts the seal must never be read as a certificate"
            }

            testAsync "a predicate omitting a surfaced field is refused rather than read as far as it goes" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! certificate, publicKey, _ = issueAttested secrets Ed25519 Attribution
                let envelopeSigner = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519

                let predicate =
                    JsonNode.Parse(CertificateEnvelope.attestedPredicateJson certificate)

                predicate.AsObject().Remove CertificateEnvelope.AttestationLevelField |> ignore

                let! stripped =
                    DsseEnvelope.sign
                        envelopeSigner
                        [ CertificateEnvelope.subjectFor root ]
                        CertificateEnvelope.AttestedPredicateType
                        (predicate.ToJsonString())

                let envelope =
                    match stripped with
                    | Ok e -> e
                    | Error e -> failtestf "could not sign the stripped statement: %s" e

                match CertificateEnvelope.verifyAndReadAttested publicKey (Some root) envelope with
                | Error(EnvelopeMalformed reason) ->
                    Expect.stringContains
                        reason
                        CertificateEnvelope.AttestationLevelField
                        "the refusal names the field that is missing"
                | Error v -> failtestf "expected EnvelopeMalformed, got: %s" (EnvelopeVerdict.describe v)
                | Ok _ -> failtest "an incomplete attested predicate must not be read as a certificate"
            }

            // ── disclosure ───────────────────────────────────────────────

            testAsync "the attested projection carries a withheld node's disclosure unchanged" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, _, envelope, _ = exportAttestedEnvelope secrets Ed25519 Attribution
                let statement = statementTextOf envelope

                Expect.stringContains statement "fact-withheld" "the withheld node's id is disclosed"
                Expect.stringContains statement withheldPolicy "and the policy it was withheld under"
                Expect.stringContains statement disclosedMethod "a disclosable node keeps its method identity"

                // Wrapping re-derives nothing, so it cannot widen
                // disclosure — on either projection.
                let predicate = (JsonNode.Parse statement).Item "predicate"
                let bodyNode = (predicate.Item CertificateEnvelope.CertificateField).Item "Body"
                let nodesNode = bodyNode.Item "Nodes"

                let withheldNode =
                    (nodesNode :?> JsonArray)
                    |> Seq.find (fun n ->
                        let id = n.Item "Id"
                        id.GetValue<string>() = "fact-withheld")

                Expect.isTrue ((withheldNode.Item "Withheld").GetValue<bool>()) "the withheld flag survives"
                Expect.isTrue (isNull (withheldNode.Item "Method")) "and the redacted method does not reappear"
            }

            // ── reference vector ─────────────────────────────────────────

            testAsync "the pinned attested certificate projects to the pinned statement and signature" {
                let secrets = InMemorySecretStore() :> ISecretStore

                do!
                    secrets.SetSecret(
                        "_platform",
                        $"signing/{keyId}",
                        storedKeyJson "Ed25519" pinnedSeedB64 pinnedPublicB64
                    )
                    |> Async.Ignore

                let statement = CertificateEnvelope.attestedStatementJson pinnedAttested
                let statementBytes = Encoding.UTF8.GetBytes statement

                Expect.equal
                    (DsseEnvelope.sha256Hex statementBytes)
                    pinnedAttestedStatementDigest
                    "the projection of this certificate is fixed — a change here is predicate-shape drift"

                let signer = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519
                let pae = DsseEnvelope.pae DsseEnvelope.InTotoPayloadType statementBytes

                match! signer.SignPreAuthenticated pae with
                | Error e -> failtestf "pinned-key signing failed: %s" e
                | Ok signature ->
                    Expect.equal
                        (Convert.ToBase64String signature.Signature)
                        pinnedAttestedSignatureB64
                        "any conforming Ed25519 signer holding this seed produces exactly these bytes"
            }

            testAsync "an attested envelope verifies through a path written from the specification, not from our helper" {
                let secrets = InMemorySecretStore() :> ISecretStore
                let! _, publicKey, envelope, _ = exportAttestedEnvelope secrets EcdsaP256 Attribution

                // Derived from the DSSE specification text and BCL
                // primitives only — it does not call DsseEnvelope.pae or
                // DsseEnvelopeSigning.verify, so a defect in either cannot
                // make this pass.
                let payload = Convert.FromBase64String envelope.Payload
                let typeBytes = Encoding.UTF8.GetBytes envelope.PayloadType

                let message =
                    Array.append
                        (Encoding.UTF8.GetBytes(
                            "DSSEv1 "
                            + string typeBytes.Length
                            + " "
                            + envelope.PayloadType
                            + " "
                            + string payload.Length
                            + " "
                        ))
                        payload

                let signature = Convert.FromBase64String envelope.Signatures.Head.Sig

                use ecdsa = ECDsa.Create()
                ecdsa.ImportFromPem publicKey.Pem

                Expect.isTrue
                    (ecdsa.VerifyData(
                        message,
                        signature,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence
                    ))
                    "a verifier built from the spec text accepts the attested envelope"
            }
        ]
    ]