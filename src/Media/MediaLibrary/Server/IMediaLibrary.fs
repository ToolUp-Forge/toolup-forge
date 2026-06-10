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