# The signing key story — one key, several artefacts

A deployment signs more than one kind of thing: published artefacts, deploy records, compliance
export bundles, signed-statement envelopes, grounding certificates, audit-ledger heads. Each of
those arrived with its own seam, and each seam was right to be independent — a substrate that
demanded a particular key-management dependency would have been unusable to anyone who had a
different one.

The cost only shows up once a deployment composes several: several places a key is configured,
several rotations to remember, and no single answer to the question a relying party actually asks —
**what does this signature claim about the environment that produced it?** A signature whose key
custody is unstated is bounded by an unstated trusted computing base, which is the "valid provenance
from an under-isolated builder" failure mode.

This page is the one answer: which seam signs what, where the keys live, and how a rotation runs.

## The seams, and what each one signs

| Seam | Signs | Where it lives |
|---|---|---|
| `IArtefactSigner` | opaque bytes, as a detached JWS | `ToolUp.ArtefactSigning` |
| `IApplicationSigner` | an application payload, framed with a **purpose** and an **attestation level** | `ToolUp.ArtefactSigning` |
| `IStatementEnvelopeSigner` | a DSSE pre-authentication encoding | declared in `ToolUp.Platform.Server`, filled by `ToolUp.ArtefactSigning` |
| `IKeyedByteSigner` | opaque bytes, with the recording key id framed in | declared in `ToolUp.Platform.Server`, filled by `ToolUp.ArtefactSigning` |
| `ILedgerHeadSigner` | a chained-ledger head | declared in, and local to, the ledger package |

`IArtefactSigner` is the byte-level primitive every provider implements; the others sit above it.
The two declared in `ToolUp.Platform.Server` are declared there so that a recording substrate can
carry no key-management dependency at all (GP 1) and the signing companion, which already references
that project, can fill them.

**They are not interchangeable, and one is deliberately not expressible in terms of another.** A
DSSE signature must cover the pre-authentication encoding itself — that is what lets unmodified
standard tooling verify it — while a detached JWS covers the JWS signing input. The two sign
different messages. What they share is the key.

## What an application signature carries that a byte signature cannot

Three facts, all framed into the signed bytes rather than recorded beside them:

- **the purpose** — what the payload was signed AS, so a signature minted for one use cannot be
  replayed into another;
- **the attestation level** — `Attribution` when the private key is reachable from the signing
  process, `IsolatedSigner` when the process sends out a digest and never sees key material. Fixed
  at composition by the provider, because only the composition root knows how the key is held;
- **the key id**, judged against a recorded key history, so a signature outlives the rotation of the
  key that made it and a revoked key stops being accepted without anything being deleted.

Editing any of them makes the signature stop verifying. That is what makes the level a claim rather
than a label.

## Where keys live

In the deployment's own `ISecretStore`, under the key id the provider was composed with, and read
**per call** — there is no cached key material and no restart to schedule. A key absent from a
writable store is auto-provisioned on first use; a read-only store with no seeded key surfaces as an
error rather than an exception. The public half is served by `/_platform/signing-key/{keyId}`, which
is what makes every one of these artefacts checkable by a holder who has no access to the
deployment.

A key held by an external key-management service is composed through `ApplicationSigning.keyManaged`
instead, and claims `IsolatedSigner`. The distinction is about whether the private key can reach
process memory — not about how well-guarded the place it is fetched from is. A key read out of a
hardened store to sign locally is still a key the host can leak.

**A PEER's key is the one thing that does not live in the secret store, and that is deliberate.**
Everything above is about keys this deployment *signs* with. Verifying a document another deployment
signed needs the opposite: a public key, and no relationship to this deployment's key material at
all. Those anchors are composition-time DATA — `PeerTrustAnchor`, one per peer, handed to
`FactsCompose.withFactImport` — rather than entries in a store. The reason is that a store is
searchable: a verifier that resolves keys by looking them up will verify anything signed by any key
it can find, and there is then no answer to "whose fact is this". An anchor list makes the set of
peers a deployment accepts facts from readable in one place, and makes the absence of an anchor a
refusal rather than a fallback. See
[the fact-import migration note](../migrations/683-certificate-verified-fact-import.md).

## Composing it

One provider, one signer, and the adapters that put every other artefact on it:

```fsharp
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning

/// The deployment's one composed signer. `Attribution` because the key is
/// held in this deployment's own secret store and loaded to sign; a
/// key-management-backed provider is `ApplicationSigning.keyManaged`.
let composeSigner (secrets: ISecretStore) (audit: IAuditLog) : Async<IApplicationSigner> =
    ApplicationSigning.inProcess secrets audit "deployment-key-v1" EcdsaP256 "system"
    |> ApplicationSigning.createActivated "system"

/// The ledger head, on the same signer. One signer per purpose: the
/// purpose is what stops a head signature being replayed as something
/// else, so a signer serving several purposes gives that away at compose.
let headSigner (signer: IApplicationSigner) : IKeyedByteSigner =
    ApplicationKeyedSigning.signer "audit.ledger.head" signer

let headVerifier (signer: IApplicationSigner) : IKeyedByteVerifier =
    ApplicationKeyedSigning.verifier "audit.ledger.head" signer
```

`ApplicationSigning.registerProvider` registers the application signer for application code plus the
byte-level `IArtefactSigner` / `IArtefactVerifier` / `ISigningKeyLedger` for the publish path and the
public-key endpoint, using `TryAddSingleton` so a signer the deployment already composed is never
displaced.

The two adapters then bridge that signer onto the recording surfaces:

```fsharp skip=fragment
// Chained audit ledger — the package keeps its own local seam; this is a
// pure re-shaping, and composing no signer at all is still the default.
let sink =
    ChainedLedger.createSigned "audit" settings blobStorage (LedgerHeadSigning.ofKeyedSigner (headSigner signer))

// Grounding certificates — the attested issuer beside the direct one.
// `graph`, `store`, `gate` and `events` are the fact substrate the
// deployment already composed.
let issuer =
    GroundingCertificate.createAttestedIssuer graph store gate events (Some signer)
```

Nothing above is composed by default. A deployment that calls none of it registers no service,
starts no hosted component and allocates nothing (GP 13), and one that has implemented a seam itself
keeps exactly the behaviour it had (GP 11).

## How rotation runs

Rotation is composing a signer under a **new key id** and recording the transition; it is not an
edit to anything already signed.

1. Compose the provider under the new id and record its activation —
   `ApplicationSigning.createActivated` does this idempotently, so calling it on every start does not
   accumulate duplicate events.
2. Record the old key's retirement: `ApplicationSigning.retire ledger actor keyId`.
3. There is no step three. Nothing is re-signed and nothing is re-issued.

**Retirement is rotation, not distrust.** A signature made under a retired key still verifies,
because verification resolves the key the signature *names* rather than assuming the active one — so
a certificate issued last year and a ledger head written last week both keep verifying after today's
rotation, against public key material that outlives the key's use.

**Revocation is different, and deliberately harsher.** `ApplicationSigning.revoke` refuses every
signature under that key from that point on, *including ones made before the revocation*: a
compromised key was compromised for longer than anyone knew, so refusing only later signatures would
be a comforting fiction. The reason is mandatory — it is what a relying party is shown, and an
unexplained revocation is indistinguishable from a mistake.

Both take effect on the next call. Key material and key history are re-read per call, so there is no
cache to invalidate and no restart to schedule.

## What a refusal means

Verification distinguishes its failures rather than collapsing them, because they send a holder to
different places. Only one of them implicates the bytes:

| Verdict | What happened |
|---|---|
| signature rejected | the bytes, the purpose or the level were edited after signing — this is the tampering answer |
| purpose mismatch | a correctly-made signature, offered for a use it was not minted for |
| key revoked | a correctly-made signature under a key that is no longer trusted |
| key-id mismatch | the record names one key; its signature was made under another |
| scheme mismatch | the recorded attestation level disagrees with the one the signature carries |
| subject mismatch | a correctly-signed certificate about a different answer |

Where a signature and a structural claim are both checked, **the signature is checked first**. "A
correctly-signed statement about the wrong artefact" would be a false description if the subject were
compared before anyone established the document was signed at all.

## See also

- [`grounding-certificate-envelope.md`](grounding-certificate-envelope.md) — the standard-tooling
  carrier for a certificate, and the one seam that is deliberately not an `IArtefactSigner` adapter.
- [`PLATFORM-SECURITY-RULES.md`](PLATFORM-SECURITY-RULES.md) — the rule set this sits under.
