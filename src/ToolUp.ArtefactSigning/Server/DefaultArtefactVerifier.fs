// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open ToolUp.Platform.Secrets

/// Phase 40 — default `IArtefactVerifier`. Resolves the public
/// verification key for the signature's `KeyId` from `ISecretStore`
/// (the same blob the signer persisted), so a signature produced under a
/// key that has since been rotated out of active signing still verifies
/// as long as its blob survives. Stateless between calls (GP 12 rule 4):
/// every `Verify` re-resolves the key.
///
/// The verifier deliberately has no audit dependency — verification is a
/// read-side check a relying party performs, often outside the signing
/// deployment. Deployments that want a `Verify`-side audit trail wrap
/// this verifier and emit their own event.
type DefaultArtefactVerifier(secrets: ISecretStore) =

    interface IArtefactVerifier with
        member _.Verify(artefact: byte[], signature: ArtefactSignature) : Async<Result<unit, VerificationError>> = async {
            match Jws.decode signature.DetachedJws with
            | Error e -> return Error e
            | Ok decoded ->
                match! SigningKeyMaterial.tryLoad secrets signature.KeyId with
                | None -> return Error(UnknownKey signature.KeyId)
                | Some key -> return Jws.verify decoded key.PublicKey artefact
        }

module DefaultArtefactVerifier =
    /// Construct a verifier that resolves public keys from `secrets`
    /// (the store the signer persisted keys into).
    let create (secrets: ISecretStore) : IArtefactVerifier =
        DefaultArtefactVerifier(secrets) :> IArtefactVerifier