// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.BlobStorage

// Phase 30a — default `IPublisherKeyStore` for edge instances. Writes
// trusted public-key bytes to `_platform/trusted-publishers/{keyId}.pub`
// via the deployment's `IBlobStorage`. Cluster-portable: any blob
// backend (filesystem / Azure / S3 / GCS) works, and every node reads
// the same trust set on each call.
//
// **Stateless between calls.** No in-memory cache — each
// `TryGetPublicKey` round-trips to the blob store. Production
// deployments wanting a TTL cache wrap this with a `CachingPublisherKeyStore`
// (not yet shipped; the substrate is open for extension).
//
// **Naming.** Key id forms part of a blob name. The store sanitises
// the id to allow alphanumerics + `-` + `_` + `.`; any other character
// is replaced with `_`. Forward slashes are NEVER allowed (they would
// escape the container prefix) — a key id containing `/` is rejected
// with `ArgumentException`. Standard publisher key ids
// (`toolup-official-2026`, `customer-{guid}`) pass through unchanged.

[<AutoOpen>]
module private BlobBackedPublisherKeyStoreInternal =
    let trustedPublishersContainer = "_platform"
    let trustedPublishersPrefix = "trusted-publishers/"

    let sanitiseKeyId (keyId: PublisherKeyId) : string =
        let raw = PublisherKeyId.value keyId

        if String.IsNullOrWhiteSpace raw then
            raise (ArgumentException("PublisherKeyId must be non-empty", nameof keyId))

        if raw.Contains('/') || raw.Contains('\\') then
            raise (
                ArgumentException(sprintf "PublisherKeyId must not contain path separators; got %s" raw, nameof keyId)
            )

        raw
        |> Seq.map (fun c ->
            if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then
                c
            else
                '_')
        |> Seq.toArray
        |> String

    let blobNameForKeyId (keyId: PublisherKeyId) : string =
        trustedPublishersPrefix + (sanitiseKeyId keyId) + ".pub"

/// Phase 30a — `IPublisherKeyStore` backed by `IBlobStorage`. Each
/// trusted key lives at `_platform/trusted-publishers/{keyId}.pub`.
type BlobBackedPublisherKeyStore(blobs: IBlobStorage) =
    interface IPublisherKeyStore with
        member _.AddTrustedKey(keyId: PublisherKeyId, publicKey: byte[]) : Async<unit> = async {
            if isNull (box publicKey) then
                raise (ArgumentNullException(nameof publicKey))

            let name = blobNameForKeyId keyId
            let! result = blobs.Upload(trustedPublishersContainer, name, publicKey)

            match result with
            | Result.Ok _ -> return ()
            | Result.Error err ->
                return raise (InvalidOperationException(sprintf "Failed to persist trusted publisher key: %s" err))
        }

        member _.RemoveTrustedKey(keyId: PublisherKeyId) : Async<unit> = async {
            let name = blobNameForKeyId keyId
            // Delete is idempotent at the IBlobStorage contract level —
            // deleting an unknown blob returns Ok per the interface
            // contract.
            let! result = blobs.Delete(trustedPublishersContainer, name)

            match result with
            | Result.Ok _ -> return ()
            | Result.Error err ->
                return raise (InvalidOperationException(sprintf "Failed to remove trusted publisher key: %s" err))
        }

        member _.TryGetPublicKey(keyId: PublisherKeyId) : Async<byte[] option> = async {
            let name = blobNameForKeyId keyId
            let! exists = blobs.Exists(trustedPublishersContainer, name)

            if not exists then
                return None
            else
                let! result = blobs.Download(trustedPublishersContainer, name)

                match result with
                | Result.Ok bytes -> return Some bytes
                | Result.Error _ -> return None
        }

        member _.ListTrustedKeyIds() : Async<PublisherKeyId list> = async {
            let! names = blobs.List(trustedPublishersContainer, trustedPublishersPrefix)
            // Strip the prefix + `.pub` suffix. The sanitisation step
            // on `AddTrustedKey` is lossy in principle (one-way `_`
            // substitution), but in practice publisher key ids are
            // alphanumeric tokens that survive round-tripping. Operators
            // dealing with a sanitised vs original mismatch should pin
            // the original id externally.
            let prefixLen = trustedPublishersPrefix.Length

            return
                names
                |> List.choose (fun n ->
                    if
                        n.StartsWith(trustedPublishersPrefix, StringComparison.Ordinal)
                        && n.EndsWith(".pub", StringComparison.Ordinal)
                    then
                        let core = n.Substring(prefixLen, n.Length - prefixLen - 4)

                        Some(PublisherKeyId core)
                    else
                        None)
        }

module BlobBackedPublisherKeyStore =
    /// Construct an `IPublisherKeyStore` backed by the given `IBlobStorage`.
    let create (blobs: IBlobStorage) : IPublisherKeyStore =
        BlobBackedPublisherKeyStore(blobs) :> IPublisherKeyStore