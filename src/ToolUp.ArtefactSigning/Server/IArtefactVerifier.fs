// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

/// Phase 40 — verifier for detached-JWS `ArtefactSignature`s. Resolves
/// the public verification key for the signature's `KeyId` (so signatures
/// produced under a rotated-out key still verify, as long as the key's
/// public component remains discoverable) and checks the signature
/// against the supplied artefact bytes.
///
/// **Stateless between calls (GP 12 rule 4).** Each `Verify` re-resolves
/// the public key — the verifier holds no key cache. A `Verify` for an
/// artefact signed under key `v1` and a `Verify` under `v2` both work
/// against the same verifier instance without reconfiguration.
type IArtefactVerifier =
    /// Verify `artefact` against `signature`. `Result.Ok ()` means the
    /// bytes match the signature and the key was discoverable;
    /// `Result.Error VerificationError` otherwise (tampered bytes,
    /// unknown key, malformed/unsupported signature).
    abstract Verify: artefact: byte[] * signature: ArtefactSignature -> Async<Result<unit, VerificationError>>