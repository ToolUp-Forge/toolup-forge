// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.MediaLibrary

open System

// ─── Phase 88 — IMediaLibrary shared types ───────────────────────────
//
// Time-based-media companion over `IBlobStorage`. These types are the
// companion's public vocabulary: identity-by-value `MediaId`, the pure
// `ByteRange` parse (the testable core of `206 Partial Content`
// serving), the ingestion-status DU surfaced over `INotificationChannel`,
// and the immutable `MediaRecord` (GP 5). No server-only or transcode
// dependency lives here — heavy media work is delivered by opt-in
// sub-companions behind `IMediaDerivation` (GP 1).

/// Identity-by-value handle for a media item (GP 12, rule 1). The
/// wrapped string is an opaque GUID-N; it is the storage key and the
/// public URL path segment.
type MediaId =
    | MediaId of string

    member this.Value =
        let (MediaId v) = this
        v

module MediaId =
    let create () : MediaId = MediaId(Guid.NewGuid().ToString("N"))
    let value (MediaId v) = v

/// A resolved, satisfiable byte range over a resource — inclusive
/// bounds, 0-based (HTTP `Range` semantics). `Length` is the count of
/// bytes the range covers.
type ByteRange = {
    Start: int64
    End: int64
} with

    member this.Length = this.End - this.Start + 1L

/// Outcome of parsing an HTTP `Range` header against a known total
/// length. `NoRange` → serve the whole resource (`200`); `Satisfiable`
/// → serve `206 Partial Content`; `Unsatisfiable` → `416 Range Not
/// Satisfiable`.
type RangeRequest =
    | NoRange
    | Satisfiable of ByteRange
    | Unsatisfiable

module ByteRange =
    /// Parse an HTTP `Range` header value against the resource's total
    /// length. Honours a single `bytes=` range (a comma-separated
    /// multi-range request collapses to its first range — multipart
    /// `206` is out of scope). Pure + deterministic, so the `206` /
    /// `416` decision is unit-testable without an HTTP context.
    ///
    /// Supported forms: `bytes=start-end`, `bytes=start-` (open-ended),
    /// `bytes=-suffix` (final `suffix` bytes). A `start` at or beyond
    /// the total length is `Unsatisfiable`; an `end` past the last byte
    /// is clamped.
    let parse (headerValue: string) (totalLength: int64) : RangeRequest =
        let tryParseInt64 (s: string) =
            match Int64.TryParse s with
            | true, v -> Some v
            | _ -> None

        if String.IsNullOrWhiteSpace headerValue then
            NoRange
        elif not (headerValue.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) then
            // A non-`bytes` unit is unrecognised — serve the whole body.
            NoRange
        elif totalLength <= 0L then
            Unsatisfiable
        else
            let spec = headerValue.Substring(6).Trim()
            let specParts = spec.Split(',')
            let firstSpec = specParts[0].Trim()
            let dash = firstSpec.IndexOf('-')

            if dash < 0 then
                Unsatisfiable
            else
                let startStr = firstSpec.Substring(0, dash).Trim()
                let endStr = firstSpec.Substring(dash + 1).Trim()

                match startStr, endStr with
                | "", "" -> Unsatisfiable
                | "", suffix ->
                    // `bytes=-N` — the final N bytes.
                    match tryParseInt64 suffix with
                    | Some n when n > 0L ->
                        let start = max 0L (totalLength - n)

                        Satisfiable {
                            Start = start
                            End = totalLength - 1L
                        }
                    | _ -> Unsatisfiable
                | startS, "" ->
                    // `bytes=N-` — from N to the end.
                    match tryParseInt64 startS with
                    | Some s when s >= 0L && s < totalLength -> Satisfiable { Start = s; End = totalLength - 1L }
                    | _ -> Unsatisfiable
                | startS, endS ->
                    // `bytes=N-M` — inclusive, M clamped to the last byte.
                    match tryParseInt64 startS, tryParseInt64 endS with
                    | Some s, Some e when s >= 0L && s <= e && s < totalLength ->
                        Satisfiable {
                            Start = s
                            End = min e (totalLength - 1L)
                        }
                    | _ -> Unsatisfiable

/// Ingestion lifecycle for a media item, surfaced over
/// `INotificationChannel` (key `"MediaLibrary.IngestionStatus"`) so an
/// admin UI can show progress. The default impl stores the original and
/// goes straight to `Ready`; a transcode sub-companion moves an item
/// through `Transcoding` while it produces renditions.
type MediaIngestionStatus =
    | Queued
    | Transcoding
    | Ready
    | Failed of reason: string

module MediaIngestionStatus =
    /// Stable wire token for the status (used in notification payloads
    /// and admin filters). Deterministic — no culture-sensitive ops.
    let token =
        function
        | Queued -> "queued"
        | Transcoding -> "transcoding"
        | Ready -> "ready"
        | Failed _ -> "failed"

/// A transcoded / adaptive-bitrate rendition produced by a transcode
/// sub-companion. `BlobName` is the rendition's path within the owning
/// scope's container; `Name` is a stable label (`"720p"`, `"hls"`).
type MediaRendition = {
    Name: string
    BlobName: string
    MimeType: string
    SizeBytes: int64
}

/// Immutable metadata record for a stored media item (GP 5). Persisted
/// as JSON alongside the original blob; returned by `Get` / `List` and
/// over the Fable.Remoting `IMediaApi`.
type MediaRecord = {
    Id: MediaId
    OriginalFilename: string
    MimeType: string
    SizeBytes: int64
    /// SHA-256 hex (lowercase) of the original bytes — content-address
    /// for dedup and the strong-validator source for `ETag` / `If-Range`.
    ContentHash: string
    UploadedBy: string
    UploadedAt: DateTimeOffset
    Status: MediaIngestionStatus
    /// Derived poster-frame blob path within the scope, when a
    /// derivation produced one; `None` for audio or un-derived video.
    PosterBlob: string option
    /// Transcoded renditions (HLS manifest + variants). Empty when the
    /// item is served as a single-file progressive download.
    Renditions: MediaRendition list
    Caption: string option
    /// Media duration in seconds when a derivation probed it.
    DurationSeconds: float option
}

// ─── Errors ──────────────────────────────────────────────────────────

type MediaUploadError =
    | InvalidFilename
    | UnsupportedMimeType of mime: string
    | FileTooLarge of size: int64 * cap: int64
    | StorageError of message: string

type MediaDeleteError =
    | NotFound
    | StorageError of message: string

/// Failure shapes for `OpenRange`. `Unsatisfiable` is the `416` path.
type MediaRangeError =
    | NotFound
    | Unsatisfiable
    | StorageError of message: string

/// Failure shapes for signed-URL minting / verification.
type SignedUrlError =
    | NotFound
    | Malformed
    | InvalidSignature
    | Expired
    | KeyResolutionFailed of message: string

// ─── Options ─────────────────────────────────────────────────────────

/// Compose-time tunables for the media library. `MaxBytes` caps upload
/// size; `AcceptedMimeTypes` gates allowed content; `SignedUrlDefaultTtl`
/// is the lifetime used when a caller passes a non-positive TTL;
/// `EmitAudit` gates `IAuditLog` emission; `RangeChunkBytes` bounds each
/// blob read taken while range-serving; `MaxChunkBytes` /
/// `UploadSessionTtl` bound the Phase 469 resumable-upload path.
type MediaLibraryOptions = {
    MaxBytes: int64
    AcceptedMimeTypes: Set<string>
    SignedUrlDefaultTtl: TimeSpan
    EmitAudit: bool
    /// Phase 468 — bytes pulled per `IBlobStorage.DownloadRange` call
    /// while serving a byte range. A `Range` request therefore costs
    /// O(range) reads of at most this size each, never O(object): the
    /// seam has no open-ended "offset to EOF" form precisely so no
    /// implementation can be tempted to materialise a whole object
    /// (see the `DownloadRange` docs). Raising it trades peak memory
    /// per in-flight response for fewer round trips; lowering it does
    /// the reverse. A non-positive value falls back to the default at
    /// read time rather than failing, so a hand-built options record
    /// that omits it cannot break serving.
    RangeChunkBytes: int
    /// Phase 469 — the per-chunk body cap on the resumable upload path.
    /// A single `AppendChunk` carrying more than this is refused with
    /// `ChunkTooLarge` before a byte is written, so one oversized POST
    /// cannot undo the point of chunking. It bounds the *transport*
    /// unit, not the object: `MaxBytes` still caps the assembled item.
    /// Non-positive falls back to the default at read time rather than
    /// failing, exactly as `RangeChunkBytes` does.
    MaxChunkBytes: int
    /// Phase 469 — how long an upload session survives without being
    /// touched. `BeginUpload` opportunistically sweeps sessions whose
    /// last append is older than this (GP 13 — no `BackgroundService`,
    /// so a deployment that never begins a session runs no sweep at
    /// all). Measured from the session's own recorded
    /// `LastTouchedAt`, not from blob metadata, so the semantics do not
    /// vary with the backing store's timestamp fidelity. Non-positive
    /// falls back to the default.
    UploadSessionTtl: TimeSpan
}

module MediaLibraryOptions =
    /// Default chunk for `RangeChunkBytes` — 1 MiB. Large enough that a
    /// typical `<video>` scrub (a few hundred KiB) is one round trip,
    /// small enough that a hundred concurrent responses are bounded.
    [<Literal>]
    let DefaultRangeChunkBytes = 1024 * 1024

    /// Phase 469 — default `MaxChunkBytes`: 8 MiB. Large enough that a
    /// 2 GiB upload is ~256 round trips rather than thousands, small
    /// enough that a dropped connection loses at most 8 MiB of work —
    /// which is the whole point of the resumable path.
    [<Literal>]
    let DefaultMaxChunkBytes = 8 * 1024 * 1024

    /// Phase 469 — default `UploadSessionTtl`: 24 hours. Long enough
    /// that a client resuming the next morning still finds its cursor;
    /// short enough that an abandoned multi-gigabyte session is not
    /// billed indefinitely.
    let DefaultUploadSessionTtl = TimeSpan.FromHours 24.0

    /// Default options: 2 GiB cap, common web video / audio MIME types,
    /// 1-hour signed-URL TTL, audit on, 1 MiB range chunks, 8 MiB
    /// upload chunks, 24-hour upload-session TTL.
    let defaults: MediaLibraryOptions = {
        MaxBytes = 2L * 1024L * 1024L * 1024L
        AcceptedMimeTypes =
            Set.ofList [
                "video/mp4"
                "video/webm"
                "video/ogg"
                "video/quicktime"
                "audio/mpeg"
                "audio/mp4"
                "audio/ogg"
                "audio/wav"
                "audio/webm"
                "application/x-mpegURL"
            ]
        SignedUrlDefaultTtl = TimeSpan.FromHours 1.0
        EmitAudit = true
        RangeChunkBytes = DefaultRangeChunkBytes
        MaxChunkBytes = DefaultMaxChunkBytes
        UploadSessionTtl = DefaultUploadSessionTtl
    }

    /// The effective chunk size for an options record — the configured
    /// value when positive, the default otherwise.
    let effectiveRangeChunkBytes (options: MediaLibraryOptions) =
        if options.RangeChunkBytes > 0 then
            options.RangeChunkBytes
        else
            DefaultRangeChunkBytes

    /// Phase 469 — the effective per-chunk body cap: the configured
    /// value when positive, the default otherwise.
    let effectiveMaxChunkBytes (options: MediaLibraryOptions) =
        if options.MaxChunkBytes > 0 then
            options.MaxChunkBytes
        else
            DefaultMaxChunkBytes

    /// Phase 469 — the effective upload-session TTL: the configured
    /// value when positive, the default otherwise.
    let effectiveUploadSessionTtl (options: MediaLibraryOptions) =
        if options.UploadSessionTtl > TimeSpan.Zero then
            options.UploadSessionTtl
        else
            DefaultUploadSessionTtl

/// Smart-constructed, validated upload request. Construct via
/// `MediaUploadRequest.create`, which enforces `MaxBytes` /
/// `AcceptedMimeTypes` / filename validity up front so the store's
/// `Upload` only ever sees a well-formed request.
type MediaUploadRequest = private {
    bytes: byte[]
    originalFilename: string
    mimeType: string
    uploadedBy: string
    caption: string option
} with

    member this.Bytes = this.bytes
    member this.OriginalFilename = this.originalFilename
    member this.MimeType = this.mimeType
    member this.UploadedBy = this.uploadedBy
    member this.Caption = this.caption

module MediaUploadRequest =
    let create
        (options: MediaLibraryOptions)
        (bytes: byte[])
        (originalFilename: string)
        (mimeType: string)
        (uploadedBy: string)
        (caption: string option)
        : Result<MediaUploadRequest, MediaUploadError> =
        if String.IsNullOrWhiteSpace originalFilename then
            Error InvalidFilename
        elif not (options.AcceptedMimeTypes.Contains mimeType) then
            Error(UnsupportedMimeType mimeType)
        elif int64 bytes.Length > options.MaxBytes then
            Error(FileTooLarge(int64 bytes.Length, options.MaxBytes))
        else
            Ok {
                bytes = bytes
                originalFilename = originalFilename
                mimeType = mimeType
                uploadedBy = uploadedBy
                caption = caption
            }

// ─── Phase 469 — resumable chunked uploads ───────────────────────────
//
// The 2 GiB cap above is only reachable if a single request survives
// end to end, so a drop at 1.9 GiB restarts from zero. The session
// vocabulary below is the tus-shaped alternative: declare, append
// chunks addressed by absolute offset, commit. Nothing here is
// server-only — the types are the client's half of the protocol too,
// which is why they sit beside `MediaUploadRequest` rather than in the
// server tier.
//
// Validation is deliberately SPLIT across the two ends of a session:
// filename / MIME / declared size are checked at `BeginUpload` so a
// doomed upload fails before its first byte, and actual size +
// content-hash are computed at commit, where they are the only honest
// measurement. A commit whose assembled bytes disagree with the
// declaration fails closed and takes the session with it.

/// Identity-by-value handle for a resumable upload session (GP 12,
/// rule 1). The wrapped string is an opaque GUID-N and is the session's
/// storage-path segment; it is minted server-side, never supplied by a
/// client.
type UploadSessionId =
    | UploadSessionId of string

    member this.Value =
        let (UploadSessionId v) = this
        v

module UploadSessionId =
    let create () : UploadSessionId =
        UploadSessionId(Guid.NewGuid().ToString("N"))

    let value (UploadSessionId v) = v

/// The client's resume cursor, returned by every accepted
/// `AppendChunk`. `ReceivedBytes` is both "how far we got" and "the
/// offset the next chunk must carry" — one number, so a resuming
/// client has nothing to compute.
type UploadProgress = {
    SessionId: UploadSessionId
    /// Bytes durably accepted so far. The next chunk's offset.
    ReceivedBytes: int64
    /// The size declared at `BeginUpload`. Commit requires equality.
    DeclaredSizeBytes: int64
}

/// Failure shapes for the upload-session surface. A separate DU from
/// `MediaUploadError` on purpose: the single-shot vocabulary is reused
/// verbatim inside `InvalidDeclaration` (a consumer keeps ONE
/// validation surface across both upload paths) while the session's own
/// failures — which are about protocol state, not about the file — stay
/// out of a DU that existing consumers match exhaustively.
type UploadSessionError =
    /// The declaration failed fast at `BeginUpload`, or the assembled
    /// object failed the same checks at commit. Carries the single-shot
    /// upload's error unchanged.
    | InvalidDeclaration of MediaUploadError
    /// No such session in this scope: never opened, already committed,
    /// aborted, or swept after its TTL.
    | SessionNotFound
    /// The session was opened by a different scope (GP 4). Reachable
    /// only where a store does not isolate by container — the container
    /// is the primary boundary, this is the second one.
    | SessionScopeMismatch
    /// The chunk did not start where the session expects. `expected` is
    /// the resume cursor: re-send from there.
    | OffsetMismatch of expected: int64 * received: int64
    /// The chunk exceeded `MediaLibraryOptions.MaxChunkBytes`.
    | ChunkTooLarge of size: int * cap: int
    /// Appending or committing would exceed the declared size.
    | DeclaredSizeExceeded of attempted: int64 * declared: int64
    /// Commit found fewer bytes than declared — the client committed
    /// early. The session survives so the remaining chunks can be sent.
    | IncompleteUpload of received: int64 * declared: int64
    /// The assembled object was rejected by the ordinary upload path.
    | UploadFailed of MediaUploadError
    /// The session's own storage failed (manifest or chunk IO).
    | SessionStorageError of message: string

/// Smart-constructed declaration for a resumable upload — the chunked
/// path's `MediaUploadRequest`, minus the bytes it cannot have yet.
/// Construct via `MediaUploadDeclaration.create`, which applies exactly
/// the filename / MIME / size checks `MediaUploadRequest.create`
/// applies, against the DECLARED size, so the two paths reject the same
/// uploads for the same reasons.
type MediaUploadDeclaration = private {
    originalFilename: string
    mimeType: string
    declaredSizeBytes: int64
    uploadedBy: string
    caption: string option
} with

    member this.OriginalFilename = this.originalFilename
    member this.MimeType = this.mimeType
    member this.DeclaredSizeBytes = this.declaredSizeBytes
    member this.UploadedBy = this.uploadedBy
    member this.Caption = this.caption

module MediaUploadDeclaration =
    /// Fail-fast validation (469.B). A non-positive declared size is
    /// reported as `FileTooLarge(size, cap)` rather than a new error
    /// case: it is the same question — "is this size acceptable?" —
    /// and adding a case to `MediaUploadError` would break every
    /// consumer that matches it exhaustively.
    let create
        (options: MediaLibraryOptions)
        (originalFilename: string)
        (mimeType: string)
        (declaredSizeBytes: int64)
        (uploadedBy: string)
        (caption: string option)
        : Result<MediaUploadDeclaration, MediaUploadError> =
        if String.IsNullOrWhiteSpace originalFilename then
            Error InvalidFilename
        elif not (options.AcceptedMimeTypes.Contains mimeType) then
            Error(UnsupportedMimeType mimeType)
        elif declaredSizeBytes <= 0L || declaredSizeBytes > options.MaxBytes then
            Error(FileTooLarge(declaredSizeBytes, options.MaxBytes))
        else
            Ok {
                originalFilename = originalFilename
                mimeType = mimeType
                declaredSizeBytes = declaredSizeBytes
                uploadedBy = uploadedBy
                caption = caption
            }