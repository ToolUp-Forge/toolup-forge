# Phase 1179 — the grounding certificate signs every admission

**What changes.** A grounded narrative may state a number that is not a Fact when the gate admits it:
a number the wording in force asked for (`instruction:<version>#<section>`) or, at the repair loop's
late stage, a count of what the document itself lists (`structural-count:<location>`). Until now the
certificate listed those admissions BESIDE its signed chain, as `NarrativeGroundingCertificate.Admissions`,
so they could be edited after publication without breaking the seal. The certificate is now issued
as `grounding-certificate/v2`, whose signed body carries an `Admissions` array — each entry's kind,
its reference as cited, its location in the document and, for an instruction echo, the wording
version and section — in document order. Editing, adding, removing or reordering an admission fails
verification.

The full description is in [`docs/platform/grounded-narratives.md`](../platform/grounded-narratives.md)
under "The grounding certificate".

## Who reads certificates

- **A holder or auditor verifying offline** — `GroundingCertificate.verify` with the deployment's
  public key, or the DSSE envelope through `CertificateEnvelope.verifyAndRead`.
- **A deployment importing another's Facts** — the import door names a certificate by the digest of
  its canonical bytes.
- **A deployment's own issuance log** — the `CertificateIssued` audit row records the body's format.

## What changes for a verifier: accept both versions

- **Read the format string.** Certificates issued before this phase are `grounding-certificate/v1`;
  certificates issued after it are `grounding-certificate/v2` — every certificate, not only narrative
  ones, since the issuer stamps one format. `GroundingCertificate.Formats` lists both, and
  `GroundingCertificate.isKnownFormat` tests one. A verifier that compared against
  `GroundingCertificate.Format` to accept a certificate must now accept either: `Format` names the
  version the issuer STAMPS (`FormatV2`), not the only one that verifies.
- **The canonical rules are v1's.** A body with no admissions is serialised exactly as v1 serialised
  it; an empty array is omitted, so a v2 certificate that admitted nothing differs from a v1 one only
  in its format string. A v1 certificate keeps verifying byte for byte, and is never re-issued or
  re-signed. A body with admissions serialises the v1 fields, then `Admissions`.
- **`GroundingCertificateBody` gains `Admissions: CertificateAdmission list option`.** A full record
  literal adds `Admissions = None` (FS0764). A v1 document read from JSON reads it as `None`.
- **The interchange JSON keeps the signed shape.** `GroundingCertificate.certificateJson` (which
  `CertificateEnvelope.predicateJson` uses) writes the body as `bodyJson` does: a certificate with no
  admissions reads exactly as a v1 document always has, with no `Admissions` member. Serialising the
  record directly would add `"Admissions":null`; use `certificateJson` instead.
- **The attested path's purpose is unchanged** (`GroundingCertificate.AttestationPurpose` stays
  `grounding-certificate/v1`): it names the kind of payload, and the version is inside the signed
  body. The in-toto predicate type URIs are unchanged too.

```fsharp skip=fragment
// before
if certificate.Body.Format <> GroundingCertificate.Format then refuse ()
// after
if not (GroundingCertificate.isKnownFormat certificate.Body.Format) then refuse ()

let admissions = certificate.Body.Admissions |> Option.defaultValue []
```

## What changes for a consumer that only publishes

Nothing. A grounded run certifies through `GroundingCertification.run`, which hands the verdict's
citations to the fact tier's gate; the gate re-checks the document, seals the admissions and issues.
Two source changes reach code that touched the certificate directly:

- **`NarrativeGroundingCertificate.Admissions` is removed.** Read the admissions from the sealed
  certificate: deserialise `CertificateJson` as a `GroundingCertificate` and read `Body.Admissions`.
  A full record literal of `NarrativeGroundingCertificate` drops the field.
- **An `IInstructedNarrativeGroundingGate` implementation adds `CertifyWith`** (the interface is new in
  this release). A gate implementing only `INarrativeGroundingGate` is unaffected: it admits nothing
  without a Fact, so it certifies the Facts alone.

## Adding an admission kind

A new kind is a new `Kind` value whose entries use the same four fields, with anything kind-specific in
`Attributes` (name, ordered values). Never add a field to `CertificateAdmission`: it would change the
signed bytes of every certificate already issued.

## Verification

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter "ToolUp.Platform.Tests.Phase 565"`
  verifies a stored v1 certificate byte for byte and refuses edited v2 admissions.

## Rollback

Revert the phase's commit. Certificates issued as v2 meanwhile then fail the envelope reader's format
check. Through `GroundingCertificate.verify`, one that admitted nothing still verifies (its bytes are
v1's shape); one that carries admissions does not, because the reverted code cannot serialise them.
