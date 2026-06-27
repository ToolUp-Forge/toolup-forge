// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.SignedExportBundleTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

// ─── Phase 162 — signed compliance export bundle ────────────────────────
//
// The `SignedExportBundle.adapter` bridges an `IArtefactSigner` onto the
// neutral `IExportEnvelopeSigner` seam `ToolUp.Platform.Server` exposes for
// the DSR download surface. These tests assert the bundle's core contract
// without standing up the full DSR pipeline: an envelope signed through the
// adapter produces a detached JWS that verifies against the shipped
// `DefaultArtefactVerifier` for the exact bytes, a tampered envelope fails,
// and the public-key URL points at the Phase 40 endpoint. The DSR-handler
// wiring (DownloadSignedExport + ExportSigned audit + fail-closed preflight)
// is exercised in `ToolUp.Platform.Tests`.

/// An export envelope stand-in — the `serialiseSegments` JSON shape the DSR
/// export job assembles is opaque bytes to the signer (§4.12 neutrality).
let private sampleEnvelope () : byte[] =
    Encoding.UTF8.GetBytes """{"segments":[{"name":"profile","mimeType":"application/json","bytes":"e30="}]}"""

/// Reconstruct an `ArtefactSignature` from the `ExportSignature` the adapter
/// returns so `DefaultArtefactVerifier` can validate it. `Verify` re-derives
/// the algorithm from the JWS header and resolves the public key by id, so
/// the `Algorithm`/`SignedAt` fields here are not load-bearing.
let private toArtefactSignature (sig_: ExportSignature) : ArtefactSignature = {
    KeyId = sig_.SigningKeyId
    Algorithm = EcdsaP256
    SignedAt = DateTimeOffset.UtcNow
    DetachedJws = sig_.DetachedJws
}

let private newBundle () =
    let secrets = InMemorySecretStore()
    let audit = InMemoryAuditLog()

    let signer =
        DefaultArtefactSigner.createSystem secrets audit "export-signing-v1" EcdsaP256

    let envelopeSigner = SignedExportBundle.adapter signer
    let verifier = DefaultArtefactVerifier.create secrets
    envelopeSigner, verifier

[<Tests>]
let tests =
    testList "Phase 162 — SignedExportBundle" [
        testCaseAsync "Adapter signs an envelope; the JWS verifies with DefaultArtefactVerifier"
        <| async {
            let envelopeSigner, verifier = newBundle ()
            let envelope = sampleEnvelope ()

            match! envelopeSigner.SignEnvelope envelope with
            | Error e -> failtestf "SignEnvelope must succeed; got %s" e
            | Ok signature ->
                let! verifyResult = verifier.Verify(envelope, toArtefactSignature signature)

                match verifyResult with
                | Ok() -> ()
                | Error e -> failtestf "signed envelope must verify; got %s" (VerificationError.describe e)
        }

        testCaseAsync "A tampered envelope fails verification (tamper-evident)"
        <| async {
            let envelopeSigner, verifier = newBundle ()
            let envelope = sampleEnvelope ()

            match! envelopeSigner.SignEnvelope envelope with
            | Error e -> failtestf "SignEnvelope must succeed; got %s" e
            | Ok signature ->
                let tampered = Array.copy envelope
                tampered[0] <- tampered[0] ^^^ 0xFFuy
                let! verifyResult = verifier.Verify(tampered, toArtefactSignature signature)

                match verifyResult with
                | Ok() -> failtest "a tampered envelope must not verify"
                | Error Tampered -> ()
                | Error other -> failtestf "expected Tampered; got %s" (VerificationError.describe other)
        }

        testCaseAsync "SigningKeyUrl points at the Phase 40 public-key endpoint"
        <| async {
            let envelopeSigner, _ = newBundle ()

            match! envelopeSigner.SignEnvelope(sampleEnvelope ()) with
            | Error e -> failtestf "SignEnvelope must succeed; got %s" e
            | Ok signature ->
                Expect.equal signature.SigningKeyId "export-signing-v1" "key id is stamped onto the signature"

                Expect.equal
                    signature.SigningKeyUrl
                    "/_platform/signing-key/export-signing-v1"
                    "key URL is the origin-relative Phase 40 endpoint path"
        }
    ]