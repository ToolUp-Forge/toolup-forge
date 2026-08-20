// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

/// The application-facing signing seam: one composed dependency an
/// application injects to sign and verify its OWN payloads.
///
/// `IArtefactSigner` remains what it was — the byte-level primitive the
/// publish path uses, and the thing every provider implements. This seam
/// sits above it and is what application code should hold, because it
/// carries the three facts an application signature needs and a raw byte
/// signature cannot express (see `SignedPayloadEnvelope`): the purpose
/// the payload was signed as, the attestation level the signature
/// claims, and the recorded key history the signature is judged against.
///
/// **Nothing composes this by default.** A deployment that never calls
/// `ApplicationSigning.register` registers no service, starts no hosted
/// component, and allocates nothing (GP 13); its behaviour and its bytes
/// are unchanged by this surface existing (GP 11).
///
/// **Six portability rules (GP 12).** Inherited from the underlying
/// signer, and preserved here: identity by value (key ids and purposes
/// are strings, payloads are `byte[]`); async at every boundary; failure
/// as data (`Result` over typed errors, never exceptions or callbacks);
/// stateless between invocations (key material and key history are
/// re-read per call, so rotation and revocation take effect on the next
/// call with no restart and no cache to invalidate); no cross-shard
/// ordering promise; no timing-precision boundary.
type IApplicationSigner =
    /// What signatures from this signer claim. Fixed at composition by
    /// the provider — an application cannot upgrade its own claim, which
    /// is the point: the level describes how the key is held, and only
    /// the composition root knows that.
    abstract Level: unit -> AttestationLevel

    /// The key id new signatures are minted under. Signatures already
    /// made under an earlier key stay verifiable (see `KeyHistory`).
    abstract ActiveKeyId: unit -> string

    /// Sign `payload` as `purpose`. The purpose and the signer's
    /// attestation level are bound into the signed bytes, so neither can
    /// be edited afterwards without the signature failing.
    abstract SignPayload: purpose: string * payload: byte[] -> Async<Result<SignedPayloadEnvelope, SigningError>>

    /// Verify `envelope` covers `payload` signed as `purpose`.
    ///
    /// Checks, in order: the envelope's purpose matches the one being
    /// verified; the envelope's key has not been revoked in the recorded
    /// history; the bytes verify against the signature under the key the
    /// envelope names — which may be a rotated-out key, and still
    /// verifies, because the key that signed is resolved by id rather
    /// than assumed to be the active one.
    abstract VerifyPayload:
        purpose: string * payload: byte[] * envelope: SignedPayloadEnvelope ->
            Async<Result<unit, PayloadVerificationError>>

    /// The deployment's recorded signing-key history — what has been
    /// activated, retired, and revoked, by whom and why.
    abstract KeyHistory: unit -> Async<SigningKeyHistory>