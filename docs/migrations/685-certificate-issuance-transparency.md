# Migration — certificate issuance transparency (Phase 685)

**Status:** additive. The read side (inclusion check, enumeration) is net-new and opt-in — nothing reads a log unless you build one. The write side is on by default *for deployments that issue certificates*: `FactsCompose.withFactStore` now composes an issuer that appends one identifier-only audit row per issuance. A deployment that never issues records nothing, and offline verification is byte-for-byte what it was. **No consumer action is required to upgrade.**

## Why

A grounding certificate could always be verified and never be enumerated. Those are different properties, and only the first was built: a holder could check the document in their hand, but nobody could ask *what has this deployment certified?* — so a deployment that issued a certificate could later behave as though it never had, with nothing to contradict it, and a suppressed certificate left no trace of any kind.

The audit trail closes it by reuse rather than new machinery. Each issuance appends one `CertificateIssued` row, and under the chained audit ledger that trail is tamper-evident — so a *suppressed* issuance is not an absence but a chain break the verifier positions. This is the open, self-hosted first rung of the trust-anchor registry the certificate format was designed for: the deployment's own issuance log now, an operated anchor later, no closure needed at either end.

Revocation is deliberately out of scope. An issuance log is its prerequisite either way.

## What is new

| Type | Where | Purpose |
|---|---|---|
| `AuditEvent.CertificateIssued` / `CertificateIssuedPayload` | `ToolUp.Platform.Core` | One row per issuance: `Digest`, `Subject`, `KeyId`, `Seal`, `Format`, `OccurredAt`. **Never the certificate body.** |
| `CertificateIssuance` | `ToolUp.Facts.Server` | The same four identifiers, as an enumerator reads them back. |
| `CertificateInclusionVerdict` | `ToolUp.Facts.Server` | `CertificateIncluded` / `CertificateNotIssued` / `IssuanceLogUnverifiable`, plus `describe`. |
| `ICertificateIssuanceLog` | `ToolUp.Facts.Server` | One read-only operation: `Issued(scopeId) : Async<Result<CertificateIssuance list, string>>`. |
| `GroundingCertificate.auditTrailLog` / `auditTrailLogWithIntegrity` | `ToolUp.Facts.Server` | The log over the ordinary audit read path, with and without a deployment-supplied ledger-integrity check. |
| `GroundingCertificate.listIssued` | `ToolUp.Facts.Server` | The enumeration surface. |
| `GroundingCertificate.checkInclusion` / `checkInclusionAttested` / `checkInclusionOfDigest` | `ToolUp.Facts.Server` | The optional inclusion check, from a certificate or from a bare digest. |
| `GroundingCertificate.certificateDigest` | `ToolUp.Facts.Server` | SHA-256 over the canonical signed bytes — the identity both the log and a holder compute independently. |
| `createIssuerAudited` / `createIssuerWithClockAudited` / `createAttestedIssuerAudited` / `createAttestedIssuerWithClockAudited` | `ToolUp.Facts.Server` | The logging issuers. Separate entry points, not optional arguments — see below. |

`createIssuer` / `createIssuerWithClock` / `createAttestedIssuer` / `createAttestedIssuerWithClock` are **unchanged in signature and behaviour** and record nothing.

## Enumerating what a deployment has issued

```fsharp
open ToolUp.Facts

let log = GroundingCertificate.auditTrailLog auditLog   // auditLog from DI

match! GroundingCertificate.listIssued log scopeId with
| Ok issuances ->
    for i in issuances do
        printfn "%s over %s (key %s, %s)" i.Digest i.Subject i.KeyId i.Seal
| Error reason ->
    printfn "the issuance log could not be trusted: %s" reason
```

Scope-filtered by the ordinary audit read path, so an enumerator sees exactly the scopes their audit access already covers.

## Checking inclusion — and the one way to get it wrong

```fsharp
// 1. Verify FIRST. This is unchanged, needs no log, and is what
//    establishes the document is genuine.
match! GroundingCertificate.verify verifier certificate with
| Error e -> // tampered or unresolvable key — stop here
| Ok () ->
    // 2. THEN ask the issuer's log whether it admits to having issued it.
    match! GroundingCertificate.checkInclusion log scopeId certificate with
    | CertificateIncluded issuance -> // recorded, at issuance.IssuedAt
    | CertificateNotIssued          -> // the log has no such issuance
    | IssuanceLogUnverifiable why   -> // the LOG is in question, not the certificate
```

**Never call the inclusion check instead of verifying.** Inclusion is computed from a digest over bytes nobody has checked a signature on, so on its own it establishes only that *a* document with these bytes was issued — which is not a claim about the document in your hand until the seal has been verified.

**And treat the third verdict as being about the log, not the certificate.** A deployment can make an inconvenient inclusion query fail by breaking its own ledger. If that read as "not issued", tampering would present as evidence against the certificate — which is exactly backwards. `IssuanceLogUnverifiable` exists so that a caller cannot accidentally draw the wrong conclusion, and `auditTrailLogWithIntegrity` checks integrity **before** reading any rows so a not-found verdict is never derived from evidence already known to be worthless.

## Wiring tamper evidence

The plain `auditTrailLog` is the honest floor: it enumerates, and it cannot demonstrate that nothing was removed. For a holder verifying against an adversarial deployment, compose the chained audit ledger and hand its verifier in:

```fsharp
let log =
    GroundingCertificate.auditTrailLogWithIntegrity auditLog (fun () -> async {
        match! ChainedLedger.verify ledgerSettings blobStorage headVerifier with
        | Ok (LedgerVerified _) -> return Ok ()
        | Ok (LedgerBroken b)   -> return Error (sprintf "%A at position %d" b.Kind b.Position)
        | Ok (LedgerHeadUntrusted (_, _, s)) -> return Error (sprintf "head untrusted: %A" s)
        | Error e -> return Error e
    })
```

The integrity half is a **function**, not a package reference: tamper evidence belongs to the sink that owns the chain, and the fact tier taking a dependency on one would nail every deployment to that choice (GP 1).

## Three things worth knowing

1. **The row carries identifiers and never the body.** A certificate body is a provenance chain filtered through the disclosure predicate at the `FactExport` egress surface. Copying any of it onto an audit row would move that content to a surface the predicate never ran at. A digest is sufficient for inclusion — a holder recomputes it from the bytes they hold — and insufficient for anything else, which is precisely the property wanted.

2. **Both issue paths log, and one log serves both.** The direct (`detached-jws`) and attested (`application-seal`) paths seal the *same* canonical body, so they produce the same digest and a deployment that migrated between them has one issuance history rather than two. The `Seal` field records which path produced a row. Note the attested issuer remains **uncomposed** by `FactsCompose` (Phase 682's deliberate posture, GP 13) — the emission lives on the issuer rather than on the registration, so the log's claim to enumerate issuance does not depend on which path a composition root chose.

3. **A refused issuance appends nothing.** Unlike the verification / import / mutation event pairs around it, issuance has one event type and no refusal counterpart. A certificate that failed to seal was never issued, and recording that it nearly was would put documents on the log that no holder can ever present. What this log answers is *what exists*, so the only rows on it are things that do.

## Rollback

Remove the `withFactStore` composition, or construct the issuer with `GroundingCertificate.createIssuer` (the non-audited entry point) directly. Nothing else changes: existing certificates verify exactly as before, and rows already on the trail are inert to every other reader.
