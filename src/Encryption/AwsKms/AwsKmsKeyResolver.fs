// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Encryption.AwsKms

// AWS SDK v4 declares IAmazonKeyManagementService as an interface with static
// abstract members; F# flags FS3536 whenever such an interface is used as an
// ordinary type rather than a generic constraint. We use it as a plain
// constructor/parameter type by design (DI of the deployment's KMS client).
#nowarn "3536"

open System
open Amazon.KeyManagementService
open Amazon.KeyManagementService.Model
open ToolUp.Platform
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

// ─── Phase 22a — AWS KMS-backed IBlobEncryptionKeyResolver ─────────────
//
// Envelope encryption: the deployment's KMS customer master key (CMK)
// never leaves AWS. Each blob gets its own AES-256 data key (DEK) minted
// by `GenerateDataKey`; KMS returns the plaintext DEK (used to encrypt
// the blob) plus the CMK-wrapped DEK ciphertext. The wrapped ciphertext
// is stamped as the `EncryptionKey.KeyId`, so a later read calls
// `Decrypt` to unwrap the DEK — the resolver holds no key state between
// calls (GP 12 rule 4).
//
// One master CMK with a per-blob DEK envelope (the default here). A
// deployment wanting per-scope CMKs supplies a scope→CMK map via the
// `cmkForScope` resolver argument.

/// `EncryptionKey.KeyId` prefix identifying a KMS-wrapped DEK envelope.
/// The remainder is the base64 of the CMK-wrapped DEK ciphertext.
[<AutoOpen>]
module internal Constants =
    [<Literal>]
    let KeyIdPrefix = "aws-kms:v1:"

/// AWS KMS-backed key resolver. Pass an `IAmazonKeyManagementService`
/// (constructed by the deployment with its region + credentials) and the
/// CMK id/ARN to mint data keys under. `cmkForScope` lets a multi-tenant
/// deployment route different scopes to different CMKs; the default uses
/// one master CMK for every scope.
type AwsKmsKeyResolver(kms: IAmazonKeyManagementService, cmkForScope: StorageScope -> string) =

    let unwrapKeyId (keyId: string) : Result<byte[], KeyResolutionError> =
        if not (keyId.StartsWith(KeyIdPrefix, StringComparison.Ordinal)) then
            Error(KeyNotFound keyId)
        else
            try
                Ok(Convert.FromBase64String(keyId.Substring KeyIdPrefix.Length))
            with _ ->
                Error(KeyNotFound keyId)

    /// Convenience ctor — one master CMK for every scope.
    new(kms: IAmazonKeyManagementService, masterCmk: string) = AwsKmsKeyResolver(kms, (fun _ -> masterCmk))

    interface IBlobEncryptionKeyResolver with
        member _.ResolveKey(scope: StorageScope) : Async<EncryptionKey> = async {
            let req =
                GenerateDataKeyRequest(KeyId = cmkForScope scope, KeySpec = DataKeySpec.AES_256)

            let! resp = kms.GenerateDataKeyAsync req |> Async.AwaitTask
            let plaintext = resp.Plaintext.ToArray()
            let wrapped = resp.CiphertextBlob.ToArray()

            return {
                KeyId = KeyIdPrefix + Convert.ToBase64String wrapped
                Material = plaintext
            }
        }

        member _.ResolveKeyById(keyId: string) : Async<Result<EncryptionKey, KeyResolutionError>> = async {
            match unwrapKeyId keyId with
            | Error e -> return Error e
            | Ok wrapped ->
                try
                    let req = DecryptRequest(CiphertextBlob = new IO.MemoryStream(wrapped))
                    let! resp = kms.DecryptAsync req |> Async.AwaitTask

                    return
                        Ok {
                            KeyId = keyId
                            Material = resp.Plaintext.ToArray()
                        }
                with
                | :? NotFoundException -> return Error(KeyNotFound keyId)
                // A disabled / pending-deletion / deleted CMK surfaces as
                // KMSInvalidStateException or DisabledException — the data
                // key is permanently unrecoverable (crypto-shred).
                | :? KMSInvalidStateException -> return Error(KeyDestroyed keyId)
                | :? DisabledException -> return Error(KeyDestroyed keyId)
                | ex -> return Error(StorageFailure ex.Message)
        }

module AwsKmsKeyResolver =
    /// Construct a KMS-backed `IBlobEncryptionKeyResolver` against one
    /// master CMK. Wire via `ServerApp.withEncryptedBlobStorage`.
    let create (kms: IAmazonKeyManagementService) (masterCmk: string) : IBlobEncryptionKeyResolver =
        AwsKmsKeyResolver(kms, masterCmk) :> IBlobEncryptionKeyResolver

    /// Construct a KMS-backed resolver that routes each scope to a CMK
    /// via `cmkForScope` (per-scope key custody).
    let createPerScope
        (kms: IAmazonKeyManagementService)
        (cmkForScope: StorageScope -> string)
        : IBlobEncryptionKeyResolver =
        AwsKmsKeyResolver(kms, cmkForScope) :> IBlobEncryptionKeyResolver