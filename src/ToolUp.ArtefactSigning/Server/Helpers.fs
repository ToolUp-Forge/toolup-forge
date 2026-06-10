// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.Text
open System.Text.Json.Nodes

/// Phase 40 — convenience helpers for the common artefact-signing
/// shapes: a sidecar `.sig` file, a self-describing JSON envelope, and a
/// PDF metadata pair. Each wraps `IArtefactSigner.Sign` so callers don't
/// re-derive the embedding format.
module ArtefactSigning =

    /// JSON object form of an `ArtefactSignature` — the shape embedded in
    /// envelopes and emitted by helpers. Keys mirror the JWS / JWK
    /// vocabulary so external tooling recognises them.
    let signatureJson (sig_: ArtefactSignature) : JsonObject =
        let o = JsonObject()
        o["keyId"] <- JsonValue.Create(sig_.KeyId)
        o["alg"] <- JsonValue.Create(SigningAlgorithm.jwsAlg sig_.Algorithm)
        o["signedAt"] <- JsonValue.Create(sig_.SignedAt.ToString("O"))
        o["detachedJws"] <- JsonValue.Create(sig_.DetachedJws)
        o

    /// Result of `signAndEmbed`: the original artefact (unchanged) plus
    /// the bytes of the sidecar `.sig` file to write alongside it. The
    /// sidecar contains the detached JWS — a verifier reads
    /// `{artefact}` + `{artefact}.sig` and validates the pair.
    type SidecarResult = {
        Artefact: byte[]
        /// UTF-8 bytes of the detached JWS, suitable to write to
        /// `{artefactPath}.sig`.
        Sidecar: byte[]
        Signature: ArtefactSignature
    }

    /// Sign `artefact` and produce a sidecar `.sig` payload. The artefact
    /// bytes are returned unchanged (detached signing never mutates the
    /// artefact).
    let signAndEmbed (signer: IArtefactSigner) (artefact: byte[]) : Async<Result<SidecarResult, SigningError>> = async {
        match! signer.Sign artefact with
        | Error e -> return Error e
        | Ok sig_ ->
            return
                Ok {
                    Artefact = artefact
                    Sidecar = Encoding.UTF8.GetBytes sig_.DetachedJws
                    Signature = sig_
                }
    }

    /// Sign a JSON payload and wrap it as `{ "payload": <payload>,
    /// "signature": { keyId, alg, signedAt, detachedJws } }`. `payloadJson`
    /// is parsed and re-embedded as a node (not a string), so the result
    /// is a single well-formed JSON document. The signature is computed
    /// over the UTF-8 bytes of the *canonical* re-serialisation of
    /// `payloadJson` — verifiers re-serialise the `payload` node the same
    /// way before checking.
    let signedJsonEnvelope (signer: IArtefactSigner) (payloadJson: string) : Async<Result<string, SigningError>> = async {
        // Canonicalise so sign-side + verify-side agree on the exact
        // bytes: round-trip through JsonNode and re-serialise compactly.
        let payloadNode = JsonNode.Parse(payloadJson)
        let canonical = payloadNode.ToJsonString()
        let bytes = Encoding.UTF8.GetBytes canonical

        match! signer.Sign bytes with
        | Error e -> return Error e
        | Ok sig_ ->
            let envelope = JsonObject()
            envelope["payload"] <- JsonNode.Parse(canonical)
            envelope["signature"] <- signatureJson sig_
            return Ok(envelope.ToJsonString())
    }

    /// Verify a `signedJsonEnvelope` document: re-serialise the `payload`
    /// node canonically, reconstruct the `ArtefactSignature`, and check
    /// it via `verifier`. Returns the raw `payload` JSON on success.
    let verifyJsonEnvelope
        (verifier: IArtefactVerifier)
        (envelopeJson: string)
        : Async<Result<string, VerificationError>> =
        async {
            try
                let node = JsonNode.Parse(envelopeJson)
                let payloadNode = node["payload"]
                let sigNode = node["signature"]
                let canonical = payloadNode.ToJsonString()
                let bytes = Encoding.UTF8.GetBytes canonical
                let alg = sigNode["alg"].GetValue<string>()

                match SigningAlgorithm.tryParse alg with
                | None -> return Error(VerificationError.UnsupportedAlgorithm alg)
                | Some algorithm ->
                    let sig_ = {
                        KeyId = sigNode["keyId"].GetValue<string>()
                        Algorithm = algorithm
                        SignedAt = DateTimeOffset.Parse(sigNode["signedAt"].GetValue<string>())
                        DetachedJws = sigNode["detachedJws"].GetValue<string>()
                    }

                    match! verifier.Verify(bytes, sig_) with
                    | Ok() -> return Ok canonical
                    | Error e -> return Error e
            with ex ->
                return Error(MalformedSignature ex.Message)
        }

    /// Metadata key under which the detached JWS is conventionally
    /// embedded in a PDF's document-information / `/Properties`
    /// dictionary.
    [<Literal>]
    let PdfMetadataKey = "ToolUpArtefactSignature"

    /// Produce the `(key, value)` pair to inject into a PDF's
    /// `/Properties` (or XMP) dictionary. PDF writing itself is library-
    /// specific, so this helper does not rewrite the PDF — it signs the
    /// PDF bytes and returns the metadata pair the caller injects via
    /// their PDF toolkit. The signature covers the *pre-embed* bytes;
    /// verifiers strip the metadata key before checking (or verify
    /// against the originally-signed bytes).
    let signedPdfMetadata (signer: IArtefactSigner) (pdfBytes: byte[]) : Async<Result<string * string, SigningError>> = async {
        match! signer.Sign pdfBytes with
        | Error e -> return Error e
        | Ok sig_ -> return Ok(PdfMetadataKey, sig_.DetachedJws)
    }