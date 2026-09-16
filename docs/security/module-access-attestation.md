# Module-access attestation — verifying a deployment's grant-consent control

**Audience:** a counterparty, auditor or regulator who has been handed a certificate and wants to
check it for themselves; and the operator who has to produce one.

A ToolUp deployment can let a module declare that nobody may be granted it without a named
counterparty's consent (`GrantPolicy.RequiresCounterpartyApproval`), and it enforces that at the
write path *and* on every request. Enforcement is not a document. The question a counterparty asks
in a meeting — *"prove no-one can be granted access to our data without our consent"* — is asked of
a document, and this is that document: a **signed statement of the module's declared policy, the
consent records live at one instant, and the surface that can mint a grant**, derived from live
composition state and verifiable by anyone holding the certificate and the deployment's public key.
No store, no deployment, no network.

The shape is the same open pair the grounding certificate uses
([`grounding-certificate-envelope.md`](grounding-certificate-envelope.md)): a **DSSE** envelope over
an **in-toto Statement v1**. Stock tooling reads it.

## 1. What is being claimed

> At instant **T**, deployment **S** declared module **M**'s grant policy to be **P**; the consent
> records that a request would have found live at **T**, resolved exactly as the dispatch guard
> resolves them, were **{…}**; the grant-authority surface rendered to hash **H**.

The approvals are resolved through the same judgement the dispatch guard makes — store, then
lifecycle (supersession, revocation, expiry), then addressing (the right grant, the right party),
then the counterparty's signature over the record. A record that fails any step is not live and does
not appear. What the certificate says is therefore *who a request at T would find consented*, not
what a table happened to contain.

### What it does not claim

- **It is a claim about an instant.** The `attestedAtUtc` member is covered by the signature, and a
  verifier applies a freshness window. Past that window it is history, not evidence about now.
- **Its scope is the teams it names.** `teamsExamined` lists the permission documents that were
  enumerated. An approval in a team not listed is neither asserted nor denied.
- **It reveals nothing beyond the control.** A live approval appears as the record id, the team,
  the subject, the counterparty, and the issue and expiry instants — never the counterparty's
  signature bytes, never key material, never the administrator who lodged it. A subject whose grant
  is inert (revoked, expired, never consented, unverifiable) does not appear at all: enumerating the
  refused would disclose more than the question asked.
- **It says nothing about a module that declares no policy.** Asking for a certificate over such a
  module yields an honest *nothing to attest*, not a signed statement — a signed "there is no
  control here" reads, at a glance, like a certificate, and nobody asked for one.

## 2. The statement

`payloadType` is `application/vnd.in-toto+json`; the payload is an in-toto Statement v1:

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [ { "name": "SkuAnalysis", "digest": { "toolupModuleName": "SkuAnalysis" } } ],
  "predicateType": "https://toolup-forge.io/attestations/module-access/v1",
  "predicate": { "format": "toolup.module-access-attestation/v1", "module": "SkuAnalysis", "…": "…" }
}
```

**Subject.** The module, by its registration name. The digest key is `toolupModuleName` rather than
`sha256` because a module name is an opaque identifier and the in-toto DigestSet convention is that
the key says how the value is formed — naming a non-hash `sha256` would be a false claim. A holder
claim-checks the statement against the module they are asking about with nothing to translate.

**Predicate.** The attested facts, in the canonical form below.

## 3. The canonical predicate

Members in this order, compact, no whitespace, optional members **omitted** when absent (never
`null`), every instant a UTC round-trip stamp with the `Z` designator:

```json
{"format":"toolup.module-access-attestation/v1","module":"SkuAnalysis","declaredPolicy":"requires-counterparty-approval:acme-dpo","teamsExamined":["team-a","team-b"],"liveApprovals":[{"consentId":"c-alice","teamId":"team-a","subjectId":"alice","party":"acme-dpo","issuedAtUtc":"2026-08-17T12:00:00.0000000Z"},{"consentId":"c-frank","teamId":"team-b","subjectId":"frank","party":"acme-dpo","issuedAtUtc":"2026-08-17T12:00:00.0000000Z","expiresAtUtc":"2026-12-15T12:00:00.0000000Z"}],"deploymentSha":"3f2a9c1","attestedAtUtc":"2026-09-16T12:00:00.0000000Z"}
```

| Member | Meaning |
|---|---|
| `format` | `toolup.module-access-attestation/v1`. A verifier refuses any other value. |
| `module` | The module's registration name — the same value as the statement subject. A document where the two disagree is refused as unreadable. |
| `declaredPolicy` | The module's declared policy as its stable wire token: `admin-discretion` (never attested — see §1), `requires-acknowledgement`, `requires-subject-consent`, or `requires-counterparty-approval:<party>`. |
| `teamsExamined` | The team ids whose permission documents were enumerated, deduplicated and sorted ordinally. |
| `liveApprovals` | The consent records live at `attestedAtUtc`, sorted by `(teamId, subjectId, consentId)`. Empty when nobody is consented, and empty by construction for a policy naming no counterparty — the consent registry holds counterparty consent only. `expiresAtUtc` is omitted for an open-ended consent. |
| `grantAuthorityManifestSha256` | Present only when the emitter was handed the grant-authority surface: lowercase-hex SHA-256 over the UTF-8 bytes of that surface's rendered text (`GrantAuthoritySurface.render` — deterministic over a composition, with no timestamp or machine identity in it). Omitted, never fabricated, otherwise. |
| `deploymentSha` | The deployment's identity as the operator names it — a build SHA, a release tag. Opaque. |
| `attestedAtUtc` | The instant the state was read at. Every liveness judgement inside the certificate ran on this clock. |

The form is hand-assembled rather than serialised from a record, for the reason the consent record's
own canonical payload gives: a signature is a security-relevant identity, and a serialiser's property
order is an implementation detail that would silently re-canonicalise every certificate in the
estate on an upgrade. Byte-reproducible given identical state: two emissions over the same
composition produce the same statement bytes, and under Ed25519 the same envelope.

## 4. The envelope

DSSE: `{ "payload": "<base64 statement>", "payloadType": "…", "signatures": [ { "keyid": "…", "sig": "<base64>" } ] }`.

The signature covers the **pre-authentication encoding**, not the payload:

```text
"DSSEv1" SP LEN(payloadType) SP payloadType SP LEN(payload) SP payload
```

with `LEN` the ASCII decimal **byte** length. `sig` is standard base64 of the raw signature over
those bytes — ASN.1 DER `SEQUENCE { r, s }` for ECDSA P-256, the raw 64 bytes for Ed25519 — exactly
as [`grounding-certificate-envelope.md`](grounding-certificate-envelope.md#the-envelope) specifies,
because it is the same envelope.

The signing key is the deployment's own: the same key id and material the Phase 40 artefact signer
uses, and the same public key the `/_platform/signing-key/{keyId}` endpoint serves. One key to
resolve, whichever artefact you are checking.

## 5. The procedure — with only the certificate and the public key

Input: the envelope JSON, and the public key (SPKI PEM or JWK) for the `keyid` in its signature
entry. Then, **in this order**:

1. A signature entry carries your key's id, and validates over this envelope's PAE.
2. `payloadType` is the in-toto media type, and the payload parses as a Statement v1 whose
   `predicateType` is `https://toolup-forge.io/attestations/module-access/v1`.
3. A subject digest equals the module name you are asking about.
4. The predicate is a readable `toolup.module-access-attestation/v1` document whose `module` agrees
   with the subject.
5. `attestedAtUtc` is within your freshness window and not further ahead of your clock than your
   skew tolerance.

The signature comes first so every later verdict means what it says: "a correctly-signed statement
about a different module" would be a false description if the subject were compared before anyone
had established the document was signed at all.

Each failure is a distinct, typed refusal — something unreadable, a signature for a different key, a
signature that does not validate, the wrong module, a predicate at odds with its own subject, stale,
not yet valid. **None of them is a pass**, and the payload is only ever returned on a complete pass.
A signature transplanted from another certificate signed under the *same* key is cryptographically
indistinguishable from tampering and is reported as such.

Any standard DSSE / in-toto implementation performs steps 1–3. Steps 4–5 are a JSON read and two
clock comparisons. The SDK's own verifier does all five:

```fsharp
open ToolUp.Platform
open ToolUp.Platform.AccessAttestation
open ToolUp.ArtefactSigning

/// A counterparty checks a certificate they were handed, for module
/// `moduleName`, with only the envelope JSON and the deployment's
/// published public key. `DsseEnvelopeSigning.verifySignature` is the
/// cryptographic half — public key and envelope, nothing else — and the
/// attestation verifier runs the shape, agreement and freshness checks
/// after it. The payload comes back only on a complete pass.
let checkCertificate (publicKey: PublicKeyMetadata) (moduleName: string) (envelopeJson: string) =
    let expectation =
        AttestationExpectation.create moduleName DateTimeOffset.UtcNow (TimeSpan.FromHours 24.0)

    match verifyJson (DsseEnvelopeSigning.verifySignature publicKey) expectation envelopeJson with
    | Ok payload ->
        printfn "module %s declares %s; %d live approval(s) at %O" payload.ModuleName payload.DeclaredPolicy payload.LiveApprovals.Length payload.AttestedAtUtc

        for approval in payload.LiveApprovals do
            printfn "  %s/%s consented by %s (record %s)" approval.TeamId approval.SubjectId approval.Party approval.ConsentId
    | Error refusal -> printfn "REFUSED: %s" (AttestationRefusal.describe refusal)
```

The public key is fetched once from `/_platform/signing-key/{keyId}` (or read from the metadata the
operator published) and pinned. A holder who resolves keys by looking them up on demand at verify
time will verify anything signed by any key they can find — pin it.

## 6. Producing one — the operator's side

Emission is **on demand and only on demand**. Nothing is composed, registered, hosted or scheduled;
a deployment that never asks for a certificate pays nothing, and a certificate is a claim about an
instant the caller names. An API handler or a CLI verb hands the emitter the sources the deployment
already composes and receives an envelope:

```fsharp
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.GrantPolicyGuard
open ToolUp.Platform.GrantConsentStore
open ToolUp.Platform.AccessAttestation
open ToolUp.ArtefactSigning

/// Emit a certificate for `moduleName` over the named teams. The signer
/// is the deployment's own key, through the envelope seam; the sources
/// are what the composition root already holds.
let emitCertificate
    (secrets: ISecretStore)
    (registry: ModuleGrantPolicyRegistry)
    (permissions: IPermissionStore)
    (consents: IGrantConsentStore option)
    (consentVerifier: IGrantConsentVerifier)
    (surface: GrantAuthoritySurface option)
    (deploymentSha: string)
    (moduleName: string)
    (teamIds: string list)
    : Async<Result<string, string>> =
    async {
        let signer = DsseEnvelopeSigning.fromSecretStore secrets "deployment-key-v1" Ed25519

        let sources = {
            Registry = registry
            Permissions = permissions
            Consents = consents
            ConsentVerifier = consentVerifier
            Manifest = surface
        }

        let request = {
            ModuleName = moduleName
            TeamIds = teamIds
            DeploymentSha = deploymentSha
            AttestedAt = DateTimeOffset.UtcNow
        }

        match! emit signer sources request with
        | AttestationOutcome.Attested(envelope, _) -> return Ok(DsseEnvelope.toJson envelope)
        | AttestationOutcome.NothingToAttest reason -> return Error reason
        | AttestationOutcome.AttestationFailed reason -> return Error reason
    }
```

Three outcomes, and the distinction is the point:

| Outcome | Meaning |
|---|---|
| `Attested` | A signed envelope, plus the payload it carries. |
| `NothingToAttest` | The module declares no grant policy (or is not registered). No envelope is produced — see §1. |
| `AttestationFailed` | A permission document or the consent registry could not be read, or the signer refused. Reported rather than attested around: a storage blip must not become a signed statement that nobody is consented. |

`AccessAttestation.compose` is the unsigned half of `emit`, for inspecting what *would* be attested
before asking for a signature.

**Why the signing seam is `IStatementEnvelopeSigner` rather than `IArtefactSigner`.** The
certificate reuses the Phase 40 key material and its public-key endpoint — that is the whole of
"no new crypto surface" — but through the envelope seam. A detached JWS covers the JWS signing
input, not the bytes handed to it, so a JWS over a DSSE PAE verifies under no standard DSSE
implementation; the shape a third party needs is the one stock tooling reads. What the two seams
share is the key.

## 7. Reading a refusal

| Refusal | What it means | What to do |
|---|---|---|
| `EnvelopeUnsignedForKey` | No signature entry names your key id. | You have the wrong key, or the certificate was issued under a rotated key — fetch the key the entry names. |
| `EnvelopeSignatureInvalid` | A signature for your key is present and does not validate. The payload was altered after signing, or the signature was transplanted from another certificate under the same key. | Treat the document as tampered. |
| `EnvelopePredicateTypeMismatch` | A correctly-signed statement of a different shape. | You were handed a different kind of attestation. |
| `EnvelopeSubjectMismatch` | A correctly-signed certificate about a different module. | Check which module you asked for. |
| `Unreadable` | Signed and shaped, but the predicate is not this format, is missing a member, or names a module other than its own subject. | Refuse it; a document that says two things cannot be read as either. |
| `Stale` / `NotYetValid` | Signed and readable, but outside your window. | Ask for a fresh one. |

## See also

- [`grounding-certificate-envelope.md`](grounding-certificate-envelope.md) — the same envelope,
  carrying a different claim; the encoding tables and interop evidence apply here verbatim.
- [`permission-audit-chain.md`](permission-audit-chain.md) — the tamper-evident record of *how* the
  grants the certificate summarises came to be written.
- [`signing-key-story.md`](signing-key-story.md) — what the deployment's signature does and does
  not claim about the environment that produced it.
