// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

// Phase 880 — the server half of `BlobStorage` (Phase 108): the signing
// capability probe. It lived in ToolUp.Platform.Core behind
// `#if !FABLE_COMPILER`, because Fable cannot type-test an interface and
// says so with a warning; no client ever holds an `IBlobStorage` to probe.
// It compiles here now, in the tier that calls it. The seam's types stay in
// Core; F# resolves `BlobStorage.X` across both modules.
module ToolUp.Platform.BlobStorage

open System

/// Probe `storage` for the Phase 108 signing capability and mint.
///
///   * `Ok (Some url)` — a time-bound URL valid for `ttl`.
///   * `Ok None`       — this deployment cannot sign (the backend does
///                       not implement the capability, or implements it
///                       and is not configured for it). The caller falls
///                       back to proxying the bytes; this is the local-
///                       filesystem answer and is not an error.
///   * `Error msg`     — signing was attempted and failed. Do NOT fall
///                       back: report it.
///
/// A non-positive `ttl` is a caller defect, not a backend one — it
/// mints a link that is already expired — so it is refused here,
/// before any implementation is reached.
let trySignedUrl
    (storage: BlobStorage.IBlobStorage)
    (container: string)
    (blobName: string)
    (ttl: TimeSpan)
    : Async<Result<string option, string>> =
    match storage with
    | :? BlobStorage.ISignedUrlBlobStorage as signer ->
        if ttl <= TimeSpan.Zero then
            async.Return(
                Error
                    "trySignedUrl: ttl must be strictly positive — a non-positive TTL mints links that are already expired"
            )
        else
            async {
                let! minted = signer.SignedUrl(container, blobName, ttl)

                match minted with
                | Ok url -> return Ok(Some url)
                | Error(BlobStorage.SignedUrlRefusal.NotConfigured _) -> return Ok None
                | Error(BlobStorage.SignedUrlRefusal.SigningFailed message) -> return Error message
            }
    | _ -> async.Return(Ok None)