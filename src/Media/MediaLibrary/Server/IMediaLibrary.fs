// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.MediaLibrary

open System
open System.IO
open ToolUp.Platform

// ─── Phase 88 — IMediaLibrary ─────────────────────────────────────────
//
// The media companion's primary interface. Six-portability-rule clean
// (GP 12): identity-by-value `MediaId`, `Async` at every boundary,
// failure-as-data `Result`, stateless between calls (every method takes
// the scope it operates within), no cross-shard ordering promise, and a
// `TimeSpan`-precision TTL on signed URLs.
//
// `scopeContainer` is the resolved `StorageScope.Container` (e.g.
// `team-abc`), so cross-scope reads are structurally impossible — the
// container is the isolation boundary (GP 4). `SignedUrl` takes the full
// `StorageScope` because the minted URL binds the viewing scope into its
// signature, so a gated video is reachable only via a freshly-signed URL
// for the correct scope.

type IMediaLibrary =
    /// Store an original media item. Validation (size / MIME / filename)
    /// is enforced by `MediaUploadRequest.create` before this is called.
    abstract Upload:
        scopeContainer: string * request: MediaUploadRequest -> Async<Result<MediaRecord, MediaUploadError>>

    /// Fetch a media item's metadata record, or `None` if absent.
    abstract Get: scopeContainer: string * id: MediaId -> Async<MediaRecord option>

    /// List media items under a path prefix, 50 per page, newest first.
    abstract List: scopeContainer: string * prefix: string * page: int -> Async<MediaRecord list>

    /// Delete a media item and its derived blobs (poster, renditions).
    abstract Delete: scopeContainer: string * id: MediaId -> Async<Result<unit, MediaDeleteError>>

    /// Mint a scope-signed, TTL'd URL for a gated media item. The
    /// returned URL carries an HMAC signature binding `(MediaId, scope,
    /// expiry)`; the range endpoint serves the item only when the
    /// signature validates and has not expired. A non-positive `ttl`
    /// falls back to the configured `SignedUrlDefaultTtl`.
    abstract SignedUrl: id: MediaId * scope: StorageScope * ttl: TimeSpan -> Async<Result<string, SignedUrlError>>

    /// Open a readable stream over a satisfiable byte range of the
    /// original. Used by the `206 Partial Content` range handler. The
    /// caller resolves `ByteRange` from the request's `Range` header via
    /// `ByteRange.parse`; an out-of-bounds range never reaches here
    /// (it short-circuits to `416`), but `OpenRange` re-validates
    /// defensively and returns `Unsatisfiable` if the stored size has
    /// changed under it.
    abstract OpenRange:
        scopeContainer: string * id: MediaId * range: ByteRange -> Async<Result<Stream, MediaRangeError>>

    /// Total byte length of the stored original — read without
    /// downloading the body (drives the range handler's `Content-Range`
    /// total and the `Satisfiable` / `Unsatisfiable` decision).
    abstract ContentLength: scopeContainer: string * id: MediaId -> Async<Result<int64, MediaRangeError>>

    /// Read a derived blob (poster, HLS master / variant manifest or
    /// segment) by its path relative to the item's derived directory.
    /// Returns the bytes plus the content type inferred from the file
    /// extension. Backs the HLS-serving route; `NotFound` when absent or
    /// when `relativePath` attempts directory traversal.
    abstract OpenDerived:
        scopeContainer: string * id: MediaId * relativePath: string -> Async<Result<byte[] * string, MediaRangeError>>

// ─── Phase 468 — IMediaRangeReader (the derived-path range seam) ──────
//
// `OpenDerived` returns the WHOLE derived blob, which is right for a
// manifest and wrong for a multi-megabyte HLS segment a player
// range-requests. The bounded-read affordance is therefore an OPTIONAL
// capability interface rather than a new `IMediaLibrary` member — the
// same shape `IConditionalBlobStorage` / `ISignedUrlBlobStorage` take
// over `IBlobStorage`, and for the same two reasons:
//
//   1. `IMediaLibrary` stays byte-for-byte source-compatible (GP 11).
//      Every existing implementation keeps compiling.
//   2. Not every implementation CAN serve a mid-blob window. A
//      CDN-direct or cloud-native library that answers with a redirect
//      has no window to open; a capability the default cannot promise
//      belongs behind a probe, not in the contract everyone must
//      answer (GP 3).
//
// Consumers probe with a type test and fall back to `OpenDerived`:
//
//     match box mediaLibrary with
//     | :? IMediaRangeReader as ranged -> // bounded window
//     | _ -> // whole-blob OpenDerived
//
// `DefaultMediaLibrary` implements it over `IBlobStorage.DownloadRange`
// (Phase 455), degrading to whole-blob download-and-slice when the
// backing store or a decorator refuses ranged reads.
type IMediaRangeReader =
    /// Total byte length of a derived blob (poster, HLS manifest or
    /// segment) without downloading it — drives the `Content-Range`
    /// total and the `Satisfiable` / `Unsatisfiable` decision, exactly
    /// as `ContentLength` does for the original. `NotFound` when absent
    /// or when `relativePath` attempts directory traversal.
    abstract DerivedContentLength:
        scopeContainer: string * id: MediaId * relativePath: string -> Async<Result<int64, MediaRangeError>>

    /// Open a readable stream over a satisfiable byte range of a derived
    /// blob, plus the content type inferred from its extension. The
    /// stream pulls bounded chunks on demand — reading a 1 MiB window of
    /// a 200 MiB segment costs O(window), not O(segment).
    abstract OpenDerivedRange:
        scopeContainer: string * id: MediaId * relativePath: string * range: ByteRange ->
            Async<Result<Stream * string, MediaRangeError>>