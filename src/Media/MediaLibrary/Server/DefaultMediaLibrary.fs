// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.MediaLibrary

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 88 — DefaultMediaLibrary ───────────────────────────────────
//
// Blob-backed `IMediaLibrary` over the SDK's configured `IBlobStorage`,
// with ZERO transcode dependency (GP 1 / GP 2). Originals, metadata
// records, and any derived poster / renditions live under the owning
// scope's container:
//
//   {container}/media/originals/{mediaId}            [raw bytes]
//   {container}/media/records/{mediaId}.json         [MediaRecord JSON]
//   {container}/media/derived/{mediaId}/poster.{ext} [poster frame]
//   {container}/media/derived/{mediaId}/{suffix}     [HLS manifest/segments]
//
// Range serving (`OpenRange`) downloads the whole original and slices in
// memory — `IBlobStorage` has no native byte-range read, so this is the
// portable default. A range-capable `IBlobStorage` implementation could
// back a streaming override without changing this interface. Poster /
// HLS production is delegated to the injected `IMediaDerivation` /
// `IMediaTranscoder`; the default `Noop*` providers declare no
// capability, so the default path stores the original and reports
// `Ready` immediately.

[<AutoOpen>]
module private MediaPrefixes =
    [<Literal>]
    let originalsPrefix = "media/originals/"

    [<Literal>]
    let recordsPrefix = "media/records/"

    [<Literal>]
    let derivedPrefix = "media/derived/"

/// Notification payload published over `INotificationChannel` (key
/// `"MediaLibrary.IngestionStatus"`). Public for STJ round-tripping.
type MediaIngestionStatusUpdate = {
    MediaId: string
    FileName: string
    Status: string
    Reason: string option
}

module private MediaJson =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let serializeString (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            Some(JsonSerializer.Deserialize<'T>(Encoding.UTF8.GetString bytes, options))
        with _ ->
            None

module private MediaPaths =
    let original (id: MediaId) = originalsPrefix + MediaId.value id

    let record (id: MediaId) =
        recordsPrefix + MediaId.value id + ".json"

    let derivedDir (id: MediaId) = derivedPrefix + MediaId.value id + "/"

    let sha256Hex (bytes: byte[]) =
        use sha = SHA256.Create()
        Convert.ToHexString(sha.ComputeHash bytes).ToLowerInvariant()

    let posterExt (mimeType: string) =
        match mimeType.ToLowerInvariant() with
        | "image/png" -> "png"
        | "image/webp" -> "webp"
        | _ -> "jpg"

/// Default `IMediaLibrary`. `derivation` / `transcoder` default to the
/// `Noop*` providers (no transcode dependency); `notifications` is
/// `None` when the deployment runs without an `INotificationChannel`.
type DefaultMediaLibrary
    (
        blobStorage: IBlobStorage,
        signer: SignedUrl.MediaUrlSigner,
        derivation: IMediaDerivation,
        transcoder: IMediaTranscoder,
        notifications: INotificationChannel option,
        options: MediaLibraryOptions,
        logger: ILogger
    ) =

    let publishStatus (uploadedBy: string) (record: MediaRecord) = async {
        match notifications with
        | None -> ()
        | Some channel ->
            try
                let payload: MediaIngestionStatusUpdate = {
                    MediaId = MediaId.value record.Id
                    FileName = record.OriginalFilename
                    Status = MediaIngestionStatus.token record.Status
                    Reason =
                        match record.Status with
                        | MediaIngestionStatus.Failed r -> Some r
                        | _ -> None
                }

                let json = MediaJson.serializeString payload
                do! channel.Publish(uploadedBy, CustomNotification("MediaLibrary.IngestionStatus", json))
            with ex ->
                logger.Error("[MediaLibrary] Failed to publish ingestion status", Some ex)
    }

    let writeRecord (container: string) (record: MediaRecord) = async {
        let! result = blobStorage.Upload(container, MediaPaths.record record.Id, MediaJson.serialize record)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error e
    }

    /// Run the optional poster + HLS derivation passes, returning the
    /// derived poster blob path, the produced renditions, the probed
    /// duration, and the terminal status.
    let derive (container: string) (id: MediaId) (bytes: byte[]) (mimeType: string) = async {
        // Probe (duration / dimensions) — best-effort.
        let! probe = derivation.Probe(bytes, mimeType)

        // Poster extraction when the provider can.
        let! posterBlob = async {
            if derivation.Capabilities.CanExtractPoster then
                match! derivation.ExtractPoster(bytes, mimeType) with
                | Ok poster ->
                    let path =
                        MediaPaths.derivedDir id + "poster." + MediaPaths.posterExt poster.MimeType

                    match! blobStorage.Upload(container, path, poster.Bytes) with
                    | Ok _ -> return Some path
                    | Error e ->
                        logger.Warn(sprintf "[MediaLibrary] poster upload failed: %s" e)
                        return None
                | Error e ->
                    logger.Warn(sprintf "[MediaLibrary] poster extraction failed: %s" e)
                    return None
            else
                return None
        }

        // HLS transcode when the provider can.
        let! renditions, status = async {
            if transcoder.Capabilities.CanTranscodeHls then
                match! transcoder.TranscodeToHls(bytes, mimeType) with
                | Ok files ->
                    // Persist each produced file under the item's
                    // derived dir; the master manifest pins the
                    // rendition entry.
                    let mutable masterBlob = None
                    let mutable totalBytes = 0L

                    for file in files do
                        let path = MediaPaths.derivedDir id + file.BlobSuffix
                        let! _ = blobStorage.Upload(container, path, file.Bytes)
                        totalBytes <- totalBytes + int64 file.Bytes.Length

                        if file.IsMasterManifest then
                            masterBlob <- Some(path, file.MimeType, file.RenditionName)

                    let rendition =
                        match masterBlob with
                        | Some(path, mime, name) -> [
                            {
                                Name = name
                                BlobName = path
                                MimeType = mime
                                SizeBytes = totalBytes
                            }
                          ]
                        | None -> []

                    return rendition, MediaIngestionStatus.Ready
                | Error e -> return [], MediaIngestionStatus.Failed e
            else
                return [], MediaIngestionStatus.Ready
        }

        return posterBlob, renditions, probe.DurationSeconds, status
    }

    interface IMediaLibrary with

        member _.Upload(scopeContainer, request) = async {
            let id = MediaId.create ()
            let bytes = request.Bytes
            let hash = MediaPaths.sha256Hex bytes

            // Persist the original first — everything else derives
            // from it, and a derivation failure must not lose the upload.
            match! blobStorage.Upload(scopeContainer, MediaPaths.original id, bytes) with
            | Error e -> return Error(MediaUploadError.StorageError e)
            | Ok _ ->
                let transcoding = transcoder.Capabilities.CanTranscodeHls

                // Surface a Queued/Transcoding status up-front when a
                // transcode will run (so an admin UI sees progress).
                let provisional = {
                    Id = id
                    OriginalFilename = request.OriginalFilename
                    MimeType = request.MimeType
                    SizeBytes = int64 bytes.Length
                    ContentHash = hash
                    UploadedBy = request.UploadedBy
                    UploadedAt = DateTimeOffset.UtcNow
                    Status =
                        (if transcoding then
                             MediaIngestionStatus.Transcoding
                         else
                             MediaIngestionStatus.Queued)
                    PosterBlob = None
                    Renditions = []
                    Caption = request.Caption
                    DurationSeconds = None
                }

                do! publishStatus request.UploadedBy provisional

                let! posterBlob, renditions, duration, status = derive scopeContainer id bytes request.MimeType

                let record = {
                    provisional with
                        Status = status
                        PosterBlob = posterBlob
                        Renditions = renditions
                        DurationSeconds = duration
                }

                match! writeRecord scopeContainer record with
                | Error e -> return Error(MediaUploadError.StorageError e)
                | Ok() ->
                    do! publishStatus request.UploadedBy record
                    return Ok record
        }

        member _.Get(scopeContainer, id) = async {
            match! blobStorage.Download(scopeContainer, MediaPaths.record id) with
            | Ok bytes -> return MediaJson.tryDeserialize<MediaRecord> bytes
            | Error _ -> return None
        }

        member _.List(scopeContainer, prefix, page) = async {
            let listPrefix = recordsPrefix + prefix
            let! blobNames = blobStorage.List(scopeContainer, listPrefix)

            let! records =
                blobNames
                |> List.map (fun name -> async {
                    match! blobStorage.Download(scopeContainer, name) with
                    | Ok bytes -> return MediaJson.tryDeserialize<MediaRecord> bytes
                    | Error _ -> return None
                })
                |> Async.Sequential

            let ordered =
                records
                |> Array.toList
                |> List.choose id
                |> List.sortByDescending (fun r -> r.UploadedAt, MediaId.value r.Id)

            let skip = min (max 0 page * 50) (List.length ordered)
            return ordered |> List.skip skip |> List.truncate 50
        }

        member _.Delete(scopeContainer, id) = async {
            let! recordExists = blobStorage.Exists(scopeContainer, MediaPaths.record id)

            if not recordExists then
                return Error MediaDeleteError.NotFound
            else
                // Best-effort delete of derived blobs, then original + record.
                let! derived = blobStorage.List(scopeContainer, MediaPaths.derivedDir id)

                for d in derived do
                    let! _ = blobStorage.Delete(scopeContainer, d)
                    ()

                let! _ = blobStorage.Delete(scopeContainer, MediaPaths.original id)
                let! recDelete = blobStorage.Delete(scopeContainer, MediaPaths.record id)

                match recDelete with
                | Ok() -> return Ok()
                | Error e -> return Error(MediaDeleteError.StorageError e)
        }

        member _.SignedUrl(id, scope, ttl) = async {
            let! exists = blobStorage.Exists(scope.Container, MediaPaths.original id)

            if not exists then
                return Error SignedUrlError.NotFound
            else
                let effectiveTtl =
                    if ttl <= TimeSpan.Zero then
                        options.SignedUrlDefaultTtl
                    else
                        ttl

                match! signer.SignAsync(id, scope, effectiveTtl, DateTimeOffset.UtcNow) with
                | Error e -> return Error e
                | Ok token ->
                    return Ok(sprintf "/media/signed/%s?token=%s" (MediaId.value id) (Uri.EscapeDataString token))
        }

        member _.ContentLength(scopeContainer, id) = async {
            match! blobStorage.GetMetadata(scopeContainer, MediaPaths.original id) with
            | Ok meta -> return Ok meta.Size
            | Error _ -> return Error MediaRangeError.NotFound
        }

        member _.OpenRange(scopeContainer, id, range) = async {
            match! blobStorage.Download(scopeContainer, MediaPaths.original id) with
            | Error _ -> return Error MediaRangeError.NotFound
            | Ok bytes ->
                let total = int64 bytes.Length

                if range.Start < 0L || range.Start >= total || range.End < range.Start then
                    return Error MediaRangeError.Unsatisfiable
                else
                    let endIdx = min range.End (total - 1L)
                    let length = int (endIdx - range.Start + 1L)
                    let stream = new MemoryStream(bytes, int range.Start, length, false) :> Stream
                    return Ok stream
        }