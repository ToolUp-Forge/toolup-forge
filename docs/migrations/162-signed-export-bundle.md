# Phase 162 — Signed compliance export bundle

**What changes.** Composes the shipped async DSR export (Phase 9h.A) and
artefact signing (Phase 40) so an Article-15 export can ship
**tamper-evident**: a new `IDataSubjectRequestApi.DownloadSignedExport`
returns the export envelope plus a detached JWS over its exact bytes,
verifiable independently against the public key the Phase 40
`/_platform/signing-key/{keyId}` endpoint serves.

New surface:
- `DataSubjectRequestConfig.SignExports: bool` (+ `DataSubjectRequestConfig.signedBackground`).
- `IDataSubjectRequestApi.DownloadSignedExport: ExportTicket -> Async<Result<SignedExportEnvelope, string>>`
  (`SignedExportEnvelope = { Envelope: byte[]; Signature: ExportSignature }`).
- `IExportEnvelopeSigner` — a **neutral** server-side signing seam in
  `ToolUp.Platform.Server` (`SignEnvelope: byte[] -> Async<Result<ExportSignature, string>>`).
- `ToolUp.ArtefactSigning.SignedExportBundle.adapter` / `.register` — the
  glue that fills that seam over an `IArtefactSigner`.
- An `ExportSigned` DSR audit kind + a fail-closed `signed-export-deps`
  preflight `IConfigValidator`.

**Architecture note (deviation from the phase's stated key files).**
`ToolUp.ArtefactSigning` already references `ToolUp.Platform.Server`, so the
SDK core cannot reference the signer back (GP 1, no circular dependency).
The composition glue therefore lives in `ToolUp.ArtefactSigning`
(`Server/SignedExportBundle.fs`), not in `ToolUp.Platform.Server`. Platform.Server
exposes only the type-neutral `IExportEnvelopeSigner` seam + the
`SignedExportEnvelope` wire type; the bundle signs *any* opaque envelope and
carries no DSR/compliance policy (§4.12 substrate-neutrality). Signing happens
**lazily at download** (not persisted as a separate sidecar blob) to avoid a
breaking change to the GP-12 `IBackgroundExportStore` portability interface;
each download produces a fresh valid detached JWS over the identical envelope
bytes, and `ExportSigned` audits per signed download (SHA-256 only, never the
bytes).

**Consumer action: none by default (GP 11 / GP 13).** A deployment that
leaves `SignExports = false` (the default) gets the exact Phase 9h.A export
output, byte-for-byte. `DownloadExport` is unchanged. Adopt only to ship
tamper-evident exports.

## Adopt (opt-in)

```fsharp
open ToolUp.ArtefactSigning            // SignedExportBundle
open ToolUp.Platform

// 1. Register the signer adapter over your IArtefactSigner at compose:
services |> fun s -> SignedExportBundle.register s mySigner |> ignore

// 2. Turn on signed exports:
ServerConfig.defaults with
    DataSubjectRequests =
        DataSubjectRequestMode.Enabled(DataSubjectRequestConfig.signedBackground ErasurePolicy.Tombstone)
```

The `signed-export-deps` preflight refuses startup if `SignExports = true`
without `SignedExportBundle.register` (a clear "compose the signer, or set
`SignExports = false`" message), so the misconfiguration surfaces at deploy
time, not at audit time. The deployment must also mount Phase 40's
`SigningKeyHandler.routes` (anonymous) so a verifying party can fetch the
public key at the `SigningKeyUrl` the signature carries.

## Verify

```fsharp
// DownloadSignedExport returns { Envelope; Signature = { DetachedJws; SigningKeyId; SigningKeyUrl } }.
// The JWS verifies over Envelope against DefaultArtefactVerifier (or any JWS
// verifier holding the public key the SigningKeyUrl serves).
```

## Rollback

Set `SignExports = false` (or drop the `SignedExportBundle.register` call).
`DownloadExport` and the whole Phase 9h.A export path are untouched; no
persisted-state change.
