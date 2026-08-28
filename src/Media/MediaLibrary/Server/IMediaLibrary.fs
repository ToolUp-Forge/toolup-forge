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

// ─── Phase 469 — IUploadSessionStore (the resumable-upload seam) ──────
//
// A SEPARATE interface rather than four more `IMediaLibrary` members,
// for the reason the whole file already turns on: `IMediaLibrary` is
// what every implementation must answer, and a CDN-direct or
// cloud-native library brokering a provider's own multipart protocol
// has no session of ours to open. Resumability is therefore a composed
// service a deployment either has or has not, and `IMediaLibrary` stays
// byte-for-byte source-compatible (GP 11).
//
// It is NOT a probe-style capability interface like `IMediaRangeReader`
// above, and the difference is deliberate: `IMediaRangeReader` refines
// how an EXISTING member serves, so the consumer must be able to fall
// back to it. There is no member to fall back to here — a deployment
// without an upload-session store simply has no chunked endpoints, and
// a caller learns that from DI, not from a type test.
//
// Six-portability-rule clean (GP 12): identity-by-value
// `UploadSessionId`, `Async` at every boundary, failure-as-data
// `Result`, no state between calls (the session lives in blob storage,
// so any instance on any node can answer for it), no cross-shard
// ordering promise, and a `TimeSpan`-precision session TTL.
//
// **Scope isolation (GP 4).** Every method takes the scope container it
// operates within, and the session's blobs live under it — so a scope
// cannot even address another's session. The recorded container is
// re-checked on every call as the second line, for a store that does
// not isolate by container.
type IUploadSessionStore =
    /// Open a session for a validated declaration. Also the point at
    /// which stale sessions in this scope are opportunistically swept
    /// (469.C — no `BackgroundService`, per GP 13).
    abstract BeginUpload:
        scopeContainer: string * declaration: MediaUploadDeclaration ->
            Async<Result<UploadSessionId, UploadSessionError>>

    /// Append one chunk at an absolute `offset`. Idempotent: re-sending
    /// a chunk already accepted at that offset, with the same length,
    /// is a no-op that returns the current cursor, so a client that
    /// retries a request whose response it never saw cannot corrupt the
    /// object. Any other offset is refused with `OffsetMismatch`
    /// carrying the expected cursor.
    abstract AppendChunk:
        scopeContainer: string * sessionId: UploadSessionId * offset: int64 * chunk: byte[] ->
            Async<Result<UploadProgress, UploadSessionError>>

    /// Assemble the accepted chunks and ingest them through the
    /// ordinary upload path, so the committed item is indistinguishable
    /// from a single-shot upload of the same bytes — same
    /// `media/originals/{mediaId}` layout, same content hash, same
    /// `Queued → … → Ready` ingestion. The session is deleted on
    /// success. A commit whose assembled size disagrees with the
    /// declaration fails closed.
    abstract CommitUpload:
        scopeContainer: string * sessionId: UploadSessionId -> Async<Result<MediaRecord, UploadSessionError>>

    /// Abandon a session and delete its chunks. Idempotent from the
    /// caller's view only in that a second abort reports
    /// `SessionNotFound` — the bytes are gone either way.
    abstract AbortUpload: scopeContainer: string * sessionId: UploadSessionId -> Async<Result<unit, UploadSessionError>>
// ─── Phase 741 — IComposedMediaIngest (the streaming-commit seam) ─────
//
// [Phase 469](…) shipped resumable uploads with an honest ceiling:
// commit assembles the chunks into one `byte[]` and hands it to
// `IMediaLibrary.Upload`, so a 2 GiB commit pins ~2 GiB of heap. That
// ceiling is `MediaUploadRequest`'s, and `MediaUploadRequest` is not
// moving — it is the single-shot path's public surface and every
// consumer constructs it (GP 11).
//
// So the streaming path is a SEAM rather than a widened verb: a session
// commit that can avoid materialising asks the composed library to
// ingest an original the store will assemble FROM PARTS IT ALREADY
// HOLDS, and hands over the two facts it measured while walking those
// parts — the size and the content hash — instead of the bytes.
//
// A probe-style capability interface (the `IConditionalBlobStorage` /
// `ISignedUrlBlobStorage` shape), NOT a member on `IMediaLibrary`:
// every implementation of that interface would otherwise have to answer
// a question only a blob-backed one can, and a cloud-native library
// brokering a provider's own multipart protocol has no notion of "our
// chunks" at all. Callers type-test and fall back:
//
//     match library with
//     | :? IComposedMediaIngest as ingest when ingest.CanIngestComposed -> // O(chunk)
//     | _ -> // materialised assembly, exactly as before
//
// **Why the capability is a PROPERTY and not just a refusal case.** The
// caller's two strategies diverge before the first byte is read — one
// walks the chunks discarding them, the other walks them accumulating.
// Discovering the refusal after the walk would mean walking twice, and
// the refusing configurations are not exotic: every encrypted
// deployment (Phase 22's whole-blob AES-GCM decorator cannot compose)
// and every deployment with a real derivation or transcode provider
// installed (poster extraction and HLS transcoding both take the
// original BYTES, so an ingest that never sees them cannot run either).

/// The measured facts about an original that has NOT been materialised:
/// the parts it will be composed from, in order, and what the caller
/// computed while walking them. Both are the caller's measurement of
/// the same bytes the compose will concatenate — an implementation
/// records them, it does not re-derive them (re-hashing would mean
/// reading the object back, which is the cost being avoided).
type ComposedOriginal = {
    /// Blob names within the same scope container, in assembly order.
    Parts: string list
    /// Total length of the concatenation.
    SizeBytes: int64
    /// Lowercase hex SHA-256 of the concatenation — the same value
    /// `IMediaLibrary.Upload` computes over the materialised bytes, so
    /// a streamed commit and a materialised commit of the same bytes
    /// produce content-hash-equal records.
    ContentHash: string
}

/// Phase 741 — ingest an original from parts already in blob storage,
/// without the bytes. Implemented alongside `IMediaLibrary` by
/// blob-backed libraries; probed by type test.
type IComposedMediaIngest =
    /// Whether THIS composition can ingest without the bytes. Folds
    /// together the two independent reasons it might not: the composed
    /// `IBlobStorage` declares no bounded multi-part commit
    /// (`CanComposeFrom = false`), or a derivation / transcode provider
    /// is installed and needs the original bytes. Cheap and
    /// side-effect-free — see the seam note above for why the caller
    /// must be able to ask before it starts work.
    abstract CanIngestComposed: bool

    /// Compose `original.Parts` into this item's `media/originals/{id}`
    /// and write its record, returning the record. The parts are NOT
    /// deleted — the session that owns them deletes them, as it does on
    /// the materialised path.
    ///
    /// `declaration` supplies filename / MIME / uploader / caption and
    /// has already passed the same validation the materialised path
    /// applies. An implementation MUST fail without writing a record if
    /// the compose fails, so a failed commit leaves no half-ingested
    /// item.
    abstract IngestComposed:
        scopeContainer: string * original: ComposedOriginal * declaration: MediaUploadDeclaration ->
            Async<Result<MediaRecord, MediaUploadError>>