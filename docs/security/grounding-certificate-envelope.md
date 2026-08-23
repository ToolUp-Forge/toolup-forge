# Grounding-certificate envelope — an open interchange shape

A grounding certificate seals an answer's provenance chain into a signed, third-party-checkable
artefact. It already verifies offline — but against this SDK's own verifier and its bespoke
canonical-JSON form. This document specifies the **standard-tooling** carrier: the certificate as a
DSSE-wrapped in-toto Statement, so a regulator, auditor or counterparty verifies it with stock
implementations of two open, vendor-neutral specifications and the deployment's public key.

The envelope **carries** the certificate's own detached-JWS seal rather than replacing it. A holder
can check either path, or both.

## The statement

`payloadType` is `application/vnd.in-toto+json`; the payload is an in-toto Statement v1:

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [ { "name": "<root id>", "digest": { "sha256": "<root id>" } } ],
  "predicateType": "https://toolup-forge.io/attestations/grounding-certificate/v1",
  "predicate": { "Body": { "Format": "grounding-certificate/v1", "Root": "…" }, "Signature": { "…": "…" } }
}
```

**Subject.** The certificate's root — the fact or answer the certificate is issued over. A fact id is
content-addressed (`hash(subject, metric, period, method, inputHashes)`), so it is
deployment-independent by construction: the digest value **is** the id, and a holder claim-checks the
statement against the id they already hold with nothing to translate. The digest key names how the id
is formed, never what would be convenient:

| Root shape | Digest key |
|---|---|
| 64-character lowercase hex — a content-addressed id | `sha256` |
| any other store identity (an answer's message id) | `toolupContentId` |

Labelling an opaque identity `sha256` would be a false claim, which is why the second key exists.

**Predicate.** The certificate record: the canonical body (chain nodes with kinds, method identities,
disclosure stances, per-node hashes, edges, policy refs, deployment key id) plus the detached-JWS seal
it was issued with. Field names are the certificate's published shape, unchanged.

**Selective disclosure carries over unchanged.** A withheld fact is collapsed to id + policy ref +
stance *before* the certificate exists, and no certificate carries a fact's value at all. Wrapping
re-derives nothing, so it cannot widen disclosure.

## The envelope

DSSE: `{ "payload": "<base64 statement>", "payloadType": "…", "signatures": [ { "keyid": "…", "sig": "<base64>" } ] }`.

The signature covers the **pre-authentication encoding**, not the payload:

```text
"DSSEv1" SP LEN(payloadType) SP payloadType SP LEN(payload) SP payload
```

`LEN` is the ASCII decimal **byte** length. Signing the PAE binds the payload type into the
signature, so a payload cannot be replayed under a different type.

`sig` is standard base64 (padded, not base64url) of the raw signature over those bytes:

| Algorithm | Signature encoding |
|---|---|
| ECDSA P-256 | ASN.1 DER `SEQUENCE { INTEGER r, INTEGER s }` — the DSSE/in-toto ecosystem convention |
| Ed25519 | the raw 64-byte signature |

Note the ECDSA encoding deliberately differs from the SDK's detached-JWS path, which uses the JWS
ES256 shape (raw `r‖s`). Different specification, different convention; the two are not
interchangeable.

## Verifying

Input is the envelope and a public key — no store, no deployment, no network. The key is the one the
`/_platform/signing-key/{keyId}` endpoint serves for the `keyid` in the signature entry, in SPKI PEM
or JWK.

1. A signature entry carries your key's id, and validates over this envelope's PAE.
2. `payloadType` is the in-toto media type.
3. The payload parses as a Statement v1 whose `predicateType` is the one you are prepared to
   interpret.
4. A subject digest matches the root id you independently hold.

**The signature is checked first, and the order is load-bearing.** It is what lets the structural
verdicts mean what they say: "a correctly-signed statement about a different artefact" would be a
false description if the subject were compared before anyone established the document was signed at
all — and a holder told their document is about the wrong artefact draws a very different conclusion
from one told it does not verify.

Each failure is reported distinctly — something unreadable (the envelope, the statement, or the key),
a signature for a different key, a signature that does not validate, and a validly-signed statement
that is about the wrong artefact or of the wrong shape. None of them is a pass. A signature
transplanted from another envelope signed by the *same* key is cryptographically indistinguishable
from tampering and is reported as such.

The predicate is returned only on a complete pass, so no caller reaches an unverified certificate by
ignoring a verdict.

## Signing seam

The envelope signer is `IStatementEnvelopeSigner` (in `ToolUp.Platform.Server`), filled by
`ToolUp.ArtefactSigning.DsseEnvelopeSigning.fromSecretStore` over the deployment's own key id and
algorithm — the same `ISecretStore` material, the same public key endpoint, and stateless between
calls, so rotation takes effect immediately.

It is deliberately **not** an `IArtefactSigner` adapter: that seam emits a detached JWS whose
signature covers the JWS signing input rather than the bytes handed to it, and a JWS wrapped in a DSSE
envelope verifies under no DSSE implementation. The two seams sign different messages. What they share
is the key.

## Scope

Nothing here is composed, registered or hosted. A deployment that never exports an envelope pays
nothing, and a deployment that does not issue certificates is untouched.

## Interop evidence

The test pack asserts the PAE against the DSSE specification's own worked example byte for byte,
pins a fixed Ed25519 seed to the exact signature it must produce over that PAE (Ed25519 is
deterministic, so this is a genuine cross-implementation vector), and verifies a produced envelope
through a path written from the specification text using BCL primitives only. **No independent DSSE
implementation runs in CI** — the evidence is reference vectors and an independent in-repo
verification path, which is a weaker claim than an external tool and is stated as such.
