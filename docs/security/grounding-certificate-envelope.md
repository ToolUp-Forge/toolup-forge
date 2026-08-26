# Grounding-certificate envelope — an open interchange shape

A grounding certificate seals an answer's provenance chain into a signed, third-party-checkable
artefact. It already verifies offline — but against this SDK's own verifier and its bespoke
canonical-JSON form. This document specifies the **standard-tooling** carrier: the certificate as a
DSSE-wrapped in-toto Statement, so a regulator, auditor or counterparty verifies it with stock
implementations of two open, vendor-neutral specifications and the deployment's public key.

The envelope **carries** the certificate's own seal rather than replacing it. A holder can check
either path, or both.

## Two projections, one subject

A grounding certificate can be sealed two ways, and each has its own carrier here:

| Certificate | Seal | `predicateType` |
|---|---|---|
| direct | detached JWS over the canonical body (`IArtefactSigner`) | `…/attestations/grounding-certificate/v1` |
| attested | the application signing seam, with the purpose and attestation level framed into the signed bytes | `…/attestations/attested-grounding-certificate/v1` |

**The bodies are byte-identical, so the subject is the same value on both.** Both issuers build the
body through one shared builder; the canonical bytes match exactly, and therefore so does the
`subject` digest each projection publishes — the certificate's root id, verbatim, under the digest key
the table below assigns. A holder brings the one root id they already possess and claim-checks
*either* document against it, with nothing to translate and no second identity to reconcile.

The predicate types are deliberately distinct because the predicate **shapes** differ. A verifier
keys on `predicateType` to decide what it is about to read; two shapes under one URI would make that
key meaningless. Offering either document to the other's reader is refused as a predicate-type
mismatch, not parsed hopefully.

## The statement

`payloadType` is `application/vnd.in-toto+json`; the payload is an in-toto Statement v1. Shown for
the direct projection; the attested one differs only in `predicateType` and `predicate`:

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

**Predicate (direct projection).** The certificate record: the canonical body (chain nodes with kinds,
method identities, disclosure stances, per-node hashes, edges, policy refs, deployment key id) plus
the detached-JWS seal it was issued with. Field names are the certificate's published shape,
unchanged. The attested projection's predicate is below.

**Selective disclosure carries over unchanged.** A withheld fact is collapsed to id + policy ref +
stance *before* the certificate exists, and no certificate carries a fact's value at all. Wrapping
re-derives nothing, so it cannot widen disclosure — on either projection.

### The attested predicate

The attested projection's predicate is the attested certificate record under a `certificate` member,
plus three members that **surface** what the seam bound into the signed bytes:

```json
{
  "certificate": { "Body": { "…": "…" }, "Envelope": { "Purpose": "…", "Level": "…", "Signature": { "…": "…" } } },
  "attestationLevel": "attribution",
  "purpose": "grounding-certificate/v1",
  "signingKeyId": "deployment-key-v1"
}
```

`attestationLevel` is the stable wire name the signature covers (`attribution` / `isolated-signer` /
`reserved:<label>`) — the same string the framing hashes, not a re-rendering of it. It says what the
signature claims about the environment that produced it, and what it does not: see
[`signing-key-story.md`](signing-key-story.md) for the per-level semantics. A `reserved:` label this
build does not recognise round-trips rather than making the document unreadable, and carrying a label
is not evidence for the claim the label names.

**The surfaced members are a projection, never an independent claim.** All three facts are already
inside the seal — the level and purpose in the seam's framing, the key id in the signed body — which
is exactly what makes them trustworthy; they are republished here only so a reader that does not know
this SDK's framing can reach them. The verifier reconciles every one against the sealed certificate
and refuses a document where they disagree or where one is absent, so a reader trusting a surfaced
member is never trusting something the seal did not cover. That refusal is reported as *unreadable*,
not as a signature failure: such a document may be perfectly signed and still say two incompatible
things about one certificate, and nothing in the format can choose between them.

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
ignoring a verdict. Both projections run through **one** verification path — same code, same order,
same verdicts — so there is no second implementation to keep in step.

**What "offline" covers, precisely.** The envelope signature is checked against the public key and
nothing else. The certificate's own seal is a second, independent check the returned document
carries rather than replaces, and on the attested projection that one is deliberately *not* offline:
refusing a revoked key means consulting the deployment's recorded key history. A holder with only a
public key gets the envelope's answer; a holder with the key history gets both.

| Projection | Envelope check | The certificate's own seal |
|---|---|---|
| direct | offline, public key | `IArtefactVerifier` over the canonical body bytes — also offline |
| attested | offline, public key | `IApplicationSigner` over the same bytes — needs the key history, so not offline |

## At the import door

The certificate-verified fact import door accepts **both projections**. Which reader runs is decided
by the `predicateType` the document itself declares — the caller nominates nothing, and there is no
try-one-then-the-other fallback, because a fallback's verdict would name whichever attempt happened
to run second and so describe a check that was not the one that mattered. A statement declaring
neither type is refused as an *unknown projection*, quoting the type it declared, rather than as a
mismatch against one of the two arbitrarily chosen.

Routing reads a field out of the payload before any signature has been established. That is safe here
only because of what follows it: every route verifies the signature over the PAE first and then
re-checks the predicate type inside the signed statement against that projection's own expectation, so
a document that lies about its own shape is routed to a reader that refuses it. The worst a liar
achieves is being refused with one verdict rather than another.

### What the attestation level decides, and what it does not

**It decides admission.** A trust anchor declares the levels it will accept from that peer as a **set**
— never a threshold. `AttestationLevel` is not totally ordered: `reserved:<label>` exists so a level a
build does not understand round-trips, and carrying such a label is not evidence for the claim it
names. Any `>=` comparison would therefore admit `reserved:anything` above `isolated-signer` on the
strength of a string the peer chose, inverting the one rule the type states about itself. A reserved
label is refused under **every** policy, including one that names it.

The default set is `attribution` + `isolated-signer`. That is not a widening relative to the path that
already worked: a direct certificate carries no level at all and is admitted unconditionally, so an
`attribution`-level document — which says at least as much, bound into its signed bytes — cannot be
the stricter case. Raising the bar to `isolated-signer` alone is the opt-in.

**It does not decide disclosure.** The level and the anchor's disclosure ceiling are orthogonal. The
ceiling governs how widely an imported fact may be surfaced once it is here; the level governs whether
the peer's document is admitted at all. Folding one into the other — "an `attribution` fact is forced
to restricted" — would make the effective stance the product of two lattices that do not compose.

**It does not decide identity, and it is not defaulted.** A level a document did not claim is recorded
as absent, never as the weakest one: the direct projection makes no statement about the signing key's
custody, and inventing one would put an assertion into the audit trail that no signature covers. An
import refused on level grounds is reported distinctly from one that failed to verify — "your key's
custody does not meet my bar" and "this did not verify" send an operator to entirely different places.

Ordering: a document whose *surfaced* level disagrees with its seal is refused by the reconciliation
above **before** any level policy is consulted. A policy is never applied to a level the signature does
not cover.

## Signing seam

The envelope signer is `IStatementEnvelopeSigner` (in `ToolUp.Platform.Server`), filled by
`ToolUp.ArtefactSigning.DsseEnvelopeSigning.fromSecretStore` over the deployment's own key id and
algorithm — the same `ISecretStore` material, the same public key endpoint, and stateless between
calls, so rotation takes effect immediately.

It is deliberately **not** an `IArtefactSigner` adapter: that seam emits a detached JWS whose
signature covers the JWS signing input rather than the bytes handed to it, and a JWS wrapped in a DSSE
envelope verifies under no DSSE implementation. The two seams sign different messages. What they share
is the key.

Both projections are exported through that one envelope signer. The seal *inside* the predicate is
whichever the certificate was issued with — the difference between the projections lives in the
predicate, never in how the envelope itself is signed.

## Scope

Nothing here is composed, registered or hosted. A deployment that never exports an envelope pays
nothing, and a deployment that does not issue certificates is untouched.

## Interop evidence

The test pack asserts the PAE against the DSSE specification's own worked example byte for byte,
pins a fixed Ed25519 seed to the exact signature it must produce over that PAE (Ed25519 is
deterministic, so this is a genuine cross-implementation vector), and verifies a produced envelope
through a path written from the specification text using BCL primitives only. Both projections are
covered by that last one.

The attested projection adds a second reference vector, over a certificate whose every input —
including the inner seal — is a literal, because a real seal carries a wall-clock stamp and a
live-issued document cannot be pinned. It fixes two values: the SHA-256 of the statement bytes and
the Ed25519 signature over their PAE. **The two halves are not equally strong, and the split is the
point.** The signature is cross-implementation — any conforming signer holding that seed must produce
those bytes over those statement bytes. The statement digest is the weaker half: it is what this
SDK's serialiser emits for that record, so it pins the predicate shape against drift rather than
asserting an independent implementation would emit the same JSON. Having both means a failure names
*which* moved.

**No independent DSSE implementation runs in CI** — the evidence is reference vectors and an
independent in-repo verification path, which is a weaker claim than an external tool and is stated as
such.
