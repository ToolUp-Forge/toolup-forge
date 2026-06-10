# ToolUp.ArtefactSigning — technical guide

Phase 40 substrate internals: the detached-JWS shape, the crypto
primitives, key persistence, and the verification flow.

## Detached JWS shape

A signature is the compact JWS with an **empty payload segment**:

```
base64url(protectedHeader) + ".." + base64url(signature)
```

The protected header is `{ "alg": "ES256"|"EdDSA", "kid": "<keyId>", "typ": "JOSE" }`.
The signature is computed over the ASCII bytes of:

```
base64url(protectedHeader) + "." + base64url(artefact)
```

i.e. the standard JWS signing input, with the artefact bytes filling the
payload position even though they are not embedded in the final string.
A verifier re-derives the signing input from `(header, artefact)` and
checks the signature — so the artefact must be supplied alongside the
signature (detached).

## Crypto

| Algorithm   | `alg` | Provider | Signature |
|-------------|-------|----------|-----------|
| `EcdsaP256` | ES256 | `System.Security.Cryptography.ECDsa` (nistP256, SHA-256) | raw R‖S, IEEE P1363 (64 bytes) — the JWS ES256 shape |
| `Ed25519`   | EdDSA | BouncyCastle `Ed25519Signer` | 64 bytes, deterministic |

ECDSA uses the BCL directly. Ed25519 uses BouncyCastle because the BCL
has no native Ed25519 signer — this mirrors the Phase 30a artefact
substrate's crypto choice.

## Key persistence

`StoredSigningKey` is JSON in `ISecretStore` (`_platform` / `signing/{keyId}`):

```json
{ "alg": "EcdsaP256", "private": "<base64>", "public": "<base64>", "createdAt": "<iso>" }
```

- ECDSA: `private` = PKCS#8 DER, `public` = SubjectPublicKeyInfo DER.
- Ed25519: `private` = raw 32-byte seed, `public` = raw 32-byte key.

`loadOrCreate` reads the blob, generating + persisting a fresh key on
first use. A read-only store with no pre-seeded key makes `Sign` return
`KeyUnavailable` (it cannot auto-provision).

## Public-key export

- **PEM** — SubjectPublicKeyInfo `-----BEGIN PUBLIC KEY-----`. ECDSA SPKI
  is exported by the BCL; the Ed25519 SPKI is the fixed 12-byte RFC 8410
  prefix (`302a300506032b6570032100`) followed by the 32 raw public bytes.
- **JWK** — `EC` (`crv P-256`, `x`/`y`) for ECDSA, `OKP` (`crv Ed25519`,
  `x`) for Ed25519, with `kid` + `alg` + `use:sig`.

## Verification flow

1. Parse the detached JWS → `(encodedHeader, algorithm, signature)`.
   A malformed string returns `MalformedSignature`; an unknown `alg`
   returns `UnsupportedAlgorithm`.
2. Resolve the public key for `signature.KeyId` from `ISecretStore`.
   Absent → `UnknownKey`.
3. Re-derive the signing input and verify. Mismatch → `Tampered`.

Because step 2 keys off the signature's `KeyId`, a rotated-out key still
verifies as long as its blob survives.

## Audit

`DefaultArtefactSigner` records `AuditEvent.ArtefactSigned` (source
module `_platform.signing`) on each successful sign, carrying the actor,
key id, algorithm name, and the artefact SHA-256 — never the bytes or key
material. `SigningKeyRotated` is reserved for an explicit operator
rotation helper. Audit emission is best-effort (swallowed on failure per
`IAuditLog`'s contract).
