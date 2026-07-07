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
/// `EmitAudit` gates `IAuditLog` emission.
type MediaLibraryOptions = {
    MaxBytes: int64
    AcceptedMimeTypes: Set<string>
    SignedUrlDefaultTtl: TimeSpan
    EmitAudit: bool
}

module MediaLibraryOptions =
    /// Default options: 2 GiB cap, common web video / audio MIME types,
    /// 1-hour signed-URL TTL, audit on.
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
    }

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