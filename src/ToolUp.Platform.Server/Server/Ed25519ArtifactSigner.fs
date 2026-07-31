// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Crypto.Signers

// Phase 30a — default hub-side `IArtifactSigner`. Ed25519 over
// `canonical(manifest) || payload` using a held private key.
//
// **Crypto provider.** .NET 10 BCL does not ship a standalone Ed25519
// primitive (only post-quantum MLDsa / SLHDsa are surfaced in
// `System.Security.Cryptography` as of `10.0.8`). The implementation
// therefore uses `BouncyCastle.Cryptography` (MIT-licensed; declared in
// `Directory.Packages.props` at 2.6.2) — satisfies GP 2 (no paid deps)
// and adds no vendor SDK to the dependency graph beyond a single
// well-vetted OSS crypto library.
//
// **Private key custody.** Held in memory as a `Ed25519PrivateKeyParameters`
// instance for the lifetime of the signer. Production hub deployments
// that need HSM / KMS custody compose a different `IArtifactSigner`
// implementation; this default is the developer / test default.
//
// **Audit emission.** The signer does not emit audit events directly —
// the caller (typically the publish pipeline) is responsible for
// recording `AuditEvent.ModuleArtefactSigned` after a successful `Sign`. This
// keeps the signer pure (input → output, no `IAuditLog` dependency) so
// the same instance can be unit-tested without a full SDK composition.

/// Phase 30a — Ed25519 hub-side signer. Holds the 32-byte private key
/// in process memory and signs each artefact synchronously (Ed25519 is
/// fast — sub-millisecond per signature); the `Async` boundary exists
/// to match the interface contract, not because the operation is
/// awaitable.
type Ed25519ArtifactSigner(publisherKeyId: PublisherKeyId, privateKeyBytes: byte[]) =
    do
        if isNull (box privateKeyBytes) then
            raise (ArgumentNullException(nameof privateKeyBytes))

        if privateKeyBytes.Length <> 32 then
            raise (
                ArgumentException(
                    sprintf "Ed25519 private key must be 32 bytes; got %d" privateKeyBytes.Length,
                    nameof privateKeyBytes
                )
            )

    let privateKey = Ed25519PrivateKeyParameters(privateKeyBytes, 0)

    interface IArtifactSigner with
        member _.PublisherKeyId = publisherKeyId

        member _.Sign(manifest: ArtifactManifest, payload: byte[]) : Async<SignedArtifact> = async {
            let message = ArtifactCanonical.messageToSign manifest payload

            let signer = Ed25519Signer()
            signer.Init(true, privateKey)
            signer.BlockUpdate(message, 0, message.Length)
            let signature = signer.GenerateSignature()

            return {
                Manifest = manifest
                Payload = payload
                Signature = signature
            }
        }

module Ed25519ArtifactSigner =
    /// Construct an `IArtifactSigner` from a 32-byte private key.
    let create (publisherKeyId: PublisherKeyId) (privateKeyBytes: byte[]) : IArtifactSigner =
        Ed25519ArtifactSigner(publisherKeyId, privateKeyBytes) :> IArtifactSigner

    /// Derive the matching 32-byte Ed25519 public key from a 32-byte
    /// private key. Used by tooling that mints a publisher key pair —
    /// the public bytes go into `IPublisherKeyStore` at the edge, the
    /// private bytes stay with the hub signer.
    let derivePublicKey (privateKeyBytes: byte[]) : byte[] =
        if isNull (box privateKeyBytes) then
            raise (ArgumentNullException(nameof privateKeyBytes))

        if privateKeyBytes.Length <> 32 then
            raise (
                ArgumentException(
                    sprintf "Ed25519 private key must be 32 bytes; got %d" privateKeyBytes.Length,
                    nameof privateKeyBytes
                )
            )

        let priv = Ed25519PrivateKeyParameters(privateKeyBytes, 0)
        let pub = priv.GeneratePublicKey()
        pub.GetEncoded()