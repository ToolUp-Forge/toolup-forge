module ToolUp.Cloud.Parity.Tests.DivergentBlobStorage

open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 193 — the divergence fixture's subject ─────────────────────
//
// A conformance matrix that only ever reports green is indistinguishable
// from a matrix that is not running, and the difference is invisible in a
// CI log. So the matrix ships with a control: a provider that diverges
// from the contract on purpose, which the SAME shared packs must fail.
//
// The perturbations are not arbitrary — each is a divergence class that
// really does differ between cloud object stores, and each is caught by a
// specific existing case in `IBlobStorageContract`:
//
//   DeleteNotIdempotent   → "Delete is idempotent on missing blobs".
//                           Object-store DELETE semantics genuinely differ:
//                           some SDKs surface a 404 as an error, and a
//                           provider that propagated it would break every
//                           idempotent-cleanup path in the SDK.
//   ListReturnsBackslashNames → "List returns `/`-delimited names for
//                           nested blobs, never the OS separator". A
//                           provider that round-trips names through a
//                           filesystem path API yields `\` on Windows and
//                           `/` on Linux — the same build passing on one
//                           runner and failing on the other. The
//                           perturbation emits a LITERAL backslash rather
//                           than `Path.DirectorySeparatorChar`: on Linux
//                           the separator IS `/`, so the platform-derived
//                           form would be a silent no-op and this control
//                           would stop controlling anything on exactly the
//                           runner CI uses.
//   UploadDoesNotOverwrite → "Upload overwrites existing blob". A provider
//                           configured for if-absent semantics silently
//                           keeps stale content on re-upload.
//
// Each is a ONE-behaviour perturbation of the estate's own in-memory
// double, so a failure localises to the perturbation rather than to a
// hand-written stub's incidental gaps.

/// The single contract behaviour a `DivergentBlobStorage` gets wrong.
type Divergence =
    | DeleteNotIdempotent
    | ListReturnsBackslashNames
    | UploadDoesNotOverwrite

module Divergence =

    let all = [ DeleteNotIdempotent; ListReturnsBackslashNames; UploadDoesNotOverwrite ]

    let name divergence =
        match divergence with
        | DeleteNotIdempotent -> "Delete of a missing blob returns Error"
        | ListReturnsBackslashNames -> "List returns backslash-delimited names"
        | UploadDoesNotOverwrite -> "Upload does not overwrite an existing blob"

    /// The contract case each divergence is expected to trip. Asserted by
    /// name in the fixture, so a perturbation that starts failing a
    /// DIFFERENT case — or the same case for a new reason — is visible
    /// rather than absorbed into a green "something failed" check.
    let expectedFailingCase divergence =
        match divergence with
        | DeleteNotIdempotent -> "Delete is idempotent on missing blobs"
        | ListReturnsBackslashNames -> "List returns `/`-delimited names for nested blobs, never the OS separator"
        | UploadDoesNotOverwrite -> "Upload overwrites existing blob"

/// An `IBlobStorage` that conforms to the contract in every respect but
/// one. Delegates to a fresh `InMemoryBlobStorage` and perturbs exactly the
/// behaviour named by `divergence`.
type DivergentBlobStorage(divergence: Divergence) =
    let inner = InMemoryBlobStorage() :> IBlobStorage

    interface IBlobStorage with
        // Phase 741 — this double perturbs one named behaviour at a
        // time; compose is not among the modelled divergences, so it
        // passes through to the inner store unaltered.
        member _.CanComposeFrom = inner.CanComposeFrom

        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) =
            inner.ComposeFrom(container, targetBlobName, sourceBlobNames)

        member this.Erase(container, prefix, policy, dryRun) =
            // Routed through the perturbed members on purpose — erasure is
            // defined in terms of List + Delete, so a provider that gets
            // either wrong gets erasure wrong too.
            eraseByPrefix (this :> IBlobStorage) container prefix policy dryRun

        member _.Upload(container, blobName, content) = async {
            match divergence with
            | UploadDoesNotOverwrite ->
                let! exists = inner.Exists(container, blobName)

                if exists then
                    // Reports success while discarding the write — the
                    // failure mode that makes this class expensive to find
                    // in production.
                    return Ok blobName
                else
                    return! inner.Upload(container, blobName, content)
            | DeleteNotIdempotent
            | ListReturnsBackslashNames -> return! inner.Upload(container, blobName, content)
        }

        member _.Download(container, blobName) = inner.Download(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Delete(container, blobName) = async {
            match divergence with
            | DeleteNotIdempotent ->
                let! exists = inner.Exists(container, blobName)

                if exists then
                    return! inner.Delete(container, blobName)
                else
                    return Error $"not found: {container}/{blobName}"
            | ListReturnsBackslashNames
            | UploadDoesNotOverwrite -> return! inner.Delete(container, blobName)
        }

        member _.List(container, prefix) = async {
            let! names = inner.List(container, prefix)

            match divergence with
            | ListReturnsBackslashNames -> return names |> List.map (fun name -> name.Replace("/", "\\"))
            | DeleteNotIdempotent
            | UploadDoesNotOverwrite -> return names
        }

        member _.Exists(container, blobName) = inner.Exists(container, blobName)

        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)