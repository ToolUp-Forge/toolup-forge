// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Encryption.GoogleCloudKms

open System
open System.Security.Cryptography
open Google.Cloud.Kms.V1
open Google.Protobuf
open Grpc.Core
open ToolUp.Platform
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

// ─── Phase 22a — GCP Cloud KMS-backed IBlobEncryptionKeyResolver ───────
//
// Envelope encryption against a GCP Cloud KMS symmetric CryptoKey (the
// KEK) that never leaves KMS. The resolver mints a random AES-256 data
// key (DEK) locally, `Encrypt`s it under the KEK, and stamps the
// ciphertext (plus the KEK resource name) into the `EncryptionKey.KeyId`;
// a later read `Decrypt`s it. The resolver holds no key state between
// calls (GP 12 rule 4).
//
// Like the Azure mirror, the KMS ciphertext does not self-describe the
// KEK for the Decrypt call, so the `KeyId` encodes both:
//   gcp-kms:v1:{base64url(keyName)}.{base64(ciphertext)}
// A single-KEK deployment uses one resource name for every scope; a
// multi-tenant deployment routes per scope via `keyNameForScope`.

/// `EncryptionKey.KeyId` prefix for a GCP-KMS-encrypted DEK.
[<AutoOpen>]
module internal Constants =
    [<Literal>]
    let KeyIdPrefix = "gcp-kms:v1:"

    let private b64Url (bytes: byte[]) : string =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private b64UrlDecode (s: string) : byte[] =
        let padded =
            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))

    /// `prefix + base64url(keyName) + "." + base64(ciphertext)`.
    let encodeKeyId (keyName: string) (ciphertext: byte[]) : string =
        KeyIdPrefix
        + b64Url (Text.Encoding.UTF8.GetBytes keyName)
        + "."
        + Convert.ToBase64String ciphertext

    /// Recover `(keyName, ciphertext)` from a stamped `KeyId`.
    let decodeKeyId (keyId: string) : Result<string * byte[], unit> =
        if not (keyId.StartsWith(KeyIdPrefix, StringComparison.Ordinal)) then
            Error()
        else
            let body = keyId.Substring KeyIdPrefix.Length

            match body.IndexOf '.' with
            | i when i > 0 ->
                try
                    let keyName = Text.Encoding.UTF8.GetString(b64UrlDecode (body.Substring(0, i)))
                    let ciphertext = Convert.FromBase64String(body.Substring(i + 1))
                    Ok(keyName, ciphertext)
                with _ ->
                    Error()
            | _ -> Error()

/// GCP Cloud KMS-backed key resolver. `client` is constructed by the
/// deployment (`KeyManagementServiceClient.Create()` picks up ADC);
/// `keyNameForScope` routes a scope to its KEK resource name
/// (`projects/p/locations/l/keyRings/r/cryptoKeys/k`).
type GoogleCloudKmsKeyResolver(client: KeyManagementServiceClient, keyNameForScope: StorageScope -> string) =

    // Vendor SDK exceptions can arrive here wrapped in
    // `AggregateException` (the class the first armed cloud-parity run
    // proved live in the AWS + GCS companions, 2026-08-27), which would
    // degrade the KeyNotFound / KeyDestroyed (crypto-shred)
    // classification to a generic StorageFailure — so unwrap before the
    // type test: flatten and take the single inner exception a one-Task
    // await carries; a bare exception passes through unchanged.
    let mapFailure (keyId: string) (ex: exn) : KeyResolutionError =
        let ex =
            match ex with
            | :? AggregateException as aggregate ->
                match Seq.tryHead (aggregate.Flatten().InnerExceptions) with
                | Some inner -> inner
                | None -> ex
            | _ -> ex

        match ex with
        | :? RpcException as rpc ->
            match rpc.StatusCode with
            | StatusCode.NotFound -> KeyNotFound keyId
            // A disabled / destroyed key version surfaces as
            // FailedPrecondition; PermissionDenied likewise means the DEK
            // is unrecoverable for this caller (crypto-shred equivalent).
            | StatusCode.FailedPrecondition
            | StatusCode.PermissionDenied -> KeyDestroyed keyId
            | _ -> StorageFailure rpc.Message
        | _ -> StorageFailure ex.Message

    /// Convenience ctor — one KEK resource name for every scope.
    new(client: KeyManagementServiceClient, keyName: string) = GoogleCloudKmsKeyResolver(client, (fun _ -> keyName))

    interface IBlobEncryptionKeyResolver with
        member _.ResolveKey(scope: StorageScope) : Async<EncryptionKey> = async {
            let keyName = keyNameForScope scope
            // Mint a fresh AES-256 data key locally; KMS only ever sees it
            // as the Encrypt plaintext and returns ciphertext.
            let dek = RandomNumberGenerator.GetBytes 32
            let req = EncryptRequest(Name = keyName, Plaintext = ByteString.CopyFrom dek)
            let! resp = client.EncryptAsync req |> Async.AwaitTask

            return {
                KeyId = encodeKeyId keyName (resp.Ciphertext.ToByteArray())
                Material = dek
            }
        }

        member _.ResolveKeyById(keyId: string) : Async<Result<EncryptionKey, KeyResolutionError>> = async {
            match decodeKeyId keyId with
            | Error() -> return Error(KeyNotFound keyId)
            | Ok(keyName, ciphertext) ->
                try
                    let req =
                        DecryptRequest(Name = keyName, Ciphertext = ByteString.CopyFrom ciphertext)

                    let! resp = client.DecryptAsync req |> Async.AwaitTask

                    return
                        Ok {
                            KeyId = keyId
                            Material = resp.Plaintext.ToByteArray()
                        }
                with ex ->
                    return Error(mapFailure keyId ex)
        }

module GoogleCloudKmsKeyResolver =
    /// Construct a GCP-KMS-backed `IBlobEncryptionKeyResolver` against one
    /// KEK resource name
    /// (`projects/p/locations/l/keyRings/r/cryptoKeys/k`). Wire via
    /// `ServerApp.withEncryptedBlobStorage`.
    let create (client: KeyManagementServiceClient) (keyName: string) : IBlobEncryptionKeyResolver =
        GoogleCloudKmsKeyResolver(client, keyName) :> IBlobEncryptionKeyResolver

    /// Construct a resolver that routes each scope to its own KEK
    /// (per-scope key custody).
    let createPerScope
        (client: KeyManagementServiceClient)
        (keyNameForScope: StorageScope -> string)
        : IBlobEncryptionKeyResolver =
        GoogleCloudKmsKeyResolver(client, keyNameForScope) :> IBlobEncryptionKeyResolver