// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.MediaLibrary

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
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
// ─── Phase 468 — range serving reads O(range), not O(object) ─────────
//
// `OpenRange` (and the `IMediaRangeReader` derived path) serve a byte
// window through `IBlobStorage.DownloadRange` (Phase 455) in bounded
// `MediaLibraryOptions.RangeChunkBytes` chunks: a scrub into a 2 GiB
// video costs the requested window plus at most one chunk of
// look-ahead, where the pre-468 path cost a 2 GiB object read PER
// `Range` request. The size comes from `GetMetadata`, so no read is
// ever open-ended — the seam has no "offset to EOF" form for exactly
// that reason.
//
// The pre-468 download-and-slice path survives as the FALLBACK, taken
// when the store refuses ranged reads. The refusal that matters in
// practice is the Phase 22 `EncryptedBlobStorage` decorator: content is
// whole-blob AES-GCM, so a mid-blob ciphertext range is undecryptable
// and the decorator returns an honest `Error`. Encrypted originals are
// therefore correct but not cheap to seek — the `media_library:
// ranged-reads` config validator says so at startup as an ADVISORY,
// and `docs/companions/media-library.md` documents the trade.
//
// Poster / HLS production is delegated to the injected
// `IMediaDerivation` / `IMediaTranscoder`; the default `Noop*`
// providers declare no capability, so the default path stores the
// original and reports `Ready` immediately.

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

    /// Content type for a derived file by extension — HLS manifests /
    /// segments + poster images. Falls back to a generic octet-stream.
    let contentTypeFor (path: string) =
        let lower = path.ToLowerInvariant()

        if lower.EndsWith ".m3u8" then
            "application/vnd.apple.mpegurl"
        elif lower.EndsWith ".ts" then
            "video/mp2t"
        elif lower.EndsWith ".m4s" then
            "video/iso.segment"
        elif lower.EndsWith ".mp4" then
            "video/mp4"
        elif lower.EndsWith ".png" then
            "image/png"
        elif lower.EndsWith ".webp" then
            "image/webp"
        elif lower.EndsWith ".jpg" || lower.EndsWith ".jpeg" then
            "image/jpeg"
        else
            "application/octet-stream"

/// Phase 468 — a forward-only read stream over ONE byte window of a
/// blob, pulled through `IBlobStorage.DownloadRange` in bounded chunks.
///
/// `fetch offset want` is the store's ranged read; `start` / `length`
/// delimit the window (`length` is already clamped to the object);
/// `chunkBytes` caps each fetch; `prefetched` is the first chunk, which
/// the opener has already read in order to learn whether the store
/// serves ranges at all — so constructing this stream costs no extra
/// round trip.
///
/// Peak memory is one chunk plus the caller's copy buffer, whatever the
/// object's size. Internal by design: it is an implementation of
/// `Stream`, not public surface, and the seam consumers hold is
/// `IMediaLibrary.OpenRange` / `IMediaRangeReader.OpenDerivedRange`.
type internal RangedBlobStream
    (
        fetch: int64 -> int -> Async<Result<byte[], string>>,
        start: int64,
        length: int64,
        chunkBytes: int,
        prefetched: byte[]
    ) =
    inherit Stream()

    /// The chunk currently being handed out, and the read cursor in it.
    let mutable chunk = prefetched
    let mutable chunkPos = 0
    /// Absolute blob offset of the next byte to FETCH (everything below
    /// it has been pulled into a chunk, not necessarily delivered).
    let mutable nextOffset = start + int64 prefetched.Length
    /// Bytes handed to the caller — the stream's `Position`.
    let mutable delivered = 0L

    /// Ensure `chunk` has an unread byte, pulling the next bounded
    /// window when it does not. `false` means end of window.
    member private _.FillAsync(ct: CancellationToken) : Task<bool> =
        if chunkPos < chunk.Length then
            Task.FromResult true
        elif nextOffset - start >= length then
            Task.FromResult false
        else
            task {
                let want = int (min (length - (nextOffset - start)) (int64 chunkBytes))
                let! result = Async.StartAsTask(fetch nextOffset want, cancellationToken = ct)

                match result with
                | Error e ->
                    // Mid-serve store failure. Ending the stream quietly
                    // would emit a body shorter than the declared
                    // `Content-Length` and look like a client-side
                    // truncation; raising lets the host abort the
                    // response, which is the honest signal.
                    return raise (IOException(sprintf "MediaLibrary: ranged read failed mid-stream: %s" e))
                | Ok bytes ->
                    if bytes.Length = 0 then
                        // Past EOF (`Ok [||]` per the `DownloadRange`
                        // contract) — the object shrank under us.
                        return false
                    else
                        chunk <- bytes
                        chunkPos <- 0
                        nextOffset <- nextOffset + int64 bytes.Length
                        return true
            }

    member private this.ReadCoreAsync(buffer: Memory<byte>, ct: CancellationToken) : Task<int> = task {
        if buffer.Length = 0 then
            return 0
        else
            let! more = this.FillAsync ct

            if not more then
                return 0
            else
                let n = min buffer.Length (chunk.Length - chunkPos)
                ReadOnlyMemory<byte>(chunk, chunkPos, n).CopyTo(buffer)
                chunkPos <- chunkPos + n
                delivered <- delivered + int64 n
                return n
    }

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false

    /// The window's length. Reported even though `CanSeek` is false —
    /// it is genuinely known up front (the opener read the object's
    /// size), and callers sizing a buffer from it get a useful answer.
    override _.Length = length

    override _.Position
        with get () = delivered
        and set (_: int64) = raise (NotSupportedException "RangedBlobStream is forward-only")

    override _.Flush() = ()

    override _.Seek(_: int64, _: SeekOrigin) : int64 =
        raise (NotSupportedException "RangedBlobStream is forward-only")

    override _.SetLength(_: int64) =
        raise (NotSupportedException "RangedBlobStream is read-only")

    override _.Write(_: byte[], _: int, _: int) =
        raise (NotSupportedException "RangedBlobStream is read-only")

    override this.ReadAsync(buffer: byte[], offset: int, count: int, ct: CancellationToken) : Task<int> =
        this.ReadCoreAsync(Memory<byte>(buffer, offset, count), ct)

    override this.ReadAsync(buffer: Memory<byte>, ct: CancellationToken) : ValueTask<int> =
        ValueTask<int>(this.ReadCoreAsync(buffer, ct))

    /// Synchronous compatibility shim. The SDK's own serve path never
    /// takes it — `RangeHandler` copies via `ReadAsync` throughout
    /// (GP 7: the chunk loop is async end to end) — but `Stream`
    /// consumers outside the SDK may call `Read` / `CopyTo`, and a
    /// stream that throws on the sync API would break them.
    override this.Read(buffer: byte[], offset: int, count: int) : int =
        this.ReadCoreAsync(Memory<byte>(buffer, offset, count), CancellationToken.None).GetAwaiter().GetResult()

module private MediaRangeRead =

    /// The pre-468 path, retained as the fallback: download the whole
    /// object and slice in memory. Correct against every store, cheap
    /// against none.
    let viaDownload
        (blobStorage: IBlobStorage)
        (container: string)
        (blobName: string)
        (range: ByteRange)
        : Async<Result<Stream, MediaRangeError>> =
        async {
            match! blobStorage.Download(container, blobName) with
            | Error _ -> return Error MediaRangeError.NotFound
            | Ok bytes ->
                let total = int64 bytes.Length

                if range.Start < 0L || range.Start >= total || range.End < range.Start then
                    return Error MediaRangeError.Unsatisfiable
                else
                    let endIdx = min range.End (total - 1L)
                    let length = int (endIdx - range.Start + 1L)
                    return Ok(new MemoryStream(bytes, int range.Start, length, false) :> Stream)
        }

    /// Phase 468 fast path: size the object with `GetMetadata`, validate
    /// the window, then serve it from bounded `DownloadRange` chunks.
    ///
    /// Two conditions fall back to `viaDownload`, and both are
    /// deliberate:
    ///
    ///   - **The store refuses ranged reads** (the Phase 22 encryption
    ///     decorator, or any custom implementation returning `Error`).
    ///     The refusal is discovered by taking the window's FIRST chunk
    ///     up front, which the stream then consumes — so the probe is
    ///     not an extra round trip, it is the first real read.
    ///   - **`GetMetadata` is unreadable.** Without a size the read
    ///     cannot be bounded, and "no metadata" cannot be told apart
    ///     from "absent" — so the fast path must never convert a blob
    ///     that used to serve into a 404.
    let openRange
        (blobStorage: IBlobStorage)
        (chunkBytes: int)
        (container: string)
        (blobName: string)
        (range: ByteRange)
        : Async<Result<Stream, MediaRangeError>> =
        async {
            match! blobStorage.GetMetadata(container, blobName) with
            | Error _ -> return! viaDownload blobStorage container blobName range
            | Ok meta ->
                let total = meta.Size

                if range.Start < 0L || range.Start >= total || range.End < range.Start then
                    return Error MediaRangeError.Unsatisfiable
                else
                    let endIdx = min range.End (total - 1L)
                    let length = endIdx - range.Start + 1L
                    let firstWant = int (min length (int64 chunkBytes))

                    match! blobStorage.DownloadRange(container, blobName, range.Start, firstWant) with
                    | Error _ -> return! viaDownload blobStorage container blobName range
                    | Ok first ->
                        let fetch offset want =
                            blobStorage.DownloadRange(container, blobName, offset, want)

                        return Ok(new RangedBlobStream(fetch, range.Start, length, chunkBytes, first) :> Stream)
        }

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
        logger: ILogger,
        /// Phase 471 — the AES-128 HLS key store. `None` means this
        /// deployment composed no `ISecretStore`-backed key store, and
        /// an upload that asks for encryption is refused rather than
        /// silently produced in the clear.
        hlsKeys: HlsKeyDelivery.MediaHlsKeyStore option
    ) =

    /// Bytes pulled per ranged blob read while serving (Phase 468).
    let chunkBytes = MediaLibraryOptions.effectiveRangeChunkBytes options

    /// Resolve a derived blob's path, rejecting directory traversal —
    /// derived paths are always flat under the item's derived directory.
    let derivedPath (id: MediaId) (relativePath: string) =
        if relativePath.Contains ".." || relativePath.StartsWith "/" then
            None
        else
            Some(MediaPaths.derivedDir id + relativePath)

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

    /// Phase 471 — run the HLS pass, encrypted or not.
    ///
    /// The key is minted only once BOTH preconditions hold (a composed
    /// key store AND a transcoder that declares
    /// `IMediaHlsEncryptingTranscoder`), so a refused request never
    /// leaves an orphan secret behind it in the scope.
    ///
    /// Every refusal here is an `Error`, which the caller turns into
    /// `MediaIngestionStatus.Failed`. That is the fail-closed choice
    /// spelled out on `IMediaHlsEncryptingTranscoder`: an upload that
    /// asked to be encrypted and got a bare rendition would be the
    /// exact exposure the encryption exists to prevent, and it would be
    /// invisible — the manifest plays, the segments are readable, and
    /// nothing anywhere says so.
    let transcodeHls (container: string) (id: MediaId) (bytes: byte[]) (mimeType: string) (encrypt: bool) = async {
        if not encrypt then
            return! transcoder.TranscodeToHls(bytes, mimeType)
        else
            match hlsKeys, box transcoder with
            | Some keys, (:? IMediaHlsEncryptingTranscoder as encrypting) ->
                match! keys.Mint(container, id) with
                | Error e -> return Error(sprintf "HLS encryption requested but the key could not be minted: %s" e)
                | Ok keyBytes ->
                    let key: HlsEncryptionKey = {
                        KeyBytes = keyBytes
                        KeyUri = HlsKeyDelivery.relativeKeyUri id
                        Iv = None
                    }

                    return! encrypting.TranscodeToHlsEncrypted(bytes, mimeType, key)
            | None, _ ->
                return
                    Error
                        "HLS encryption requested but no key store is composed (the media library needs an ISecretStore)"
            | _, _ ->
                return
                    Error
                        "HLS encryption requested but the composed transcoder cannot encrypt (it does not declare IMediaHlsEncryptingTranscoder)"
    }

    /// Run the optional poster + HLS derivation passes, returning the
    /// derived poster blob path, the produced renditions, the probed
    /// duration, and the terminal status.
    let derive (container: string) (id: MediaId) (bytes: byte[]) (mimeType: string) (encryptHls: bool) = async {
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
                match! transcodeHls container id bytes mimeType encryptHls with
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

    /// Pre-Phase-471 constructor shape. Existing call sites compile and
    /// behave byte-for-byte unchanged (GP 11): with no key store the
    /// encryption path is structurally unreachable, so every upload
    /// takes the plain transcode exactly as before.
    ///
    /// An explicit secondary constructor rather than an optional
    /// parameter — `?hlsKeys` would fold into ONE widened constructor,
    /// making the pre-471 seven-argument token disappear, which the
    /// public-API baseline reads as a removal (a genuine break, not a
    /// false positive).
    new
        (
            blobStorage: IBlobStorage,
            signer: SignedUrl.MediaUrlSigner,
            derivation: IMediaDerivation,
            transcoder: IMediaTranscoder,
            notifications: INotificationChannel option,
            options: MediaLibraryOptions,
            logger: ILogger
        ) =
        DefaultMediaLibrary(blobStorage, signer, derivation, transcoder, notifications, options, logger, None)

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

                // Phase 471 — the per-upload preference wins where it is
                // stated; every pre-471 call site states nothing and
                // takes the deployment default (which is `false`).
                let encryptHls = MediaLibraryOptions.effectiveEncryptHls options request.EncryptHls

                let! posterBlob, renditions, duration, status =
                    derive scopeContainer id bytes request.MimeType encryptHls

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

                // Phase 471 — the item's HLS key lives in `ISecretStore`,
                // not in the container, so deleting derived blobs does
                // not reach it. Best-effort and idempotent: a deleted
                // video must not leave live key material behind it.
                match hlsKeys with
                | Some keys -> do! keys.Delete(scopeContainer, id)
                | None -> ()

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

        /// Whole-derived-blob read. Unchanged by Phase 468 — the
        /// interface hands back every byte, so there is no window to
        /// bound. Callers that want a window take `IMediaRangeReader`
        /// below.
        member _.OpenDerived(scopeContainer, id, relativePath) = async {
            match derivedPath id relativePath with
            | None -> return Error MediaRangeError.NotFound
            | Some path ->
                match! blobStorage.Download(scopeContainer, path) with
                | Ok bytes -> return Ok(bytes, MediaPaths.contentTypeFor relativePath)
                | Error _ -> return Error MediaRangeError.NotFound
        }

        /// Phase 468 — serve the window from bounded ranged reads, with
        /// the pre-468 download-and-slice retained as the fallback for
        /// stores and decorators that refuse them.
        member _.OpenRange(scopeContainer, id, range) =
            MediaRangeRead.openRange blobStorage chunkBytes scopeContainer (MediaPaths.original id) range

    interface IMediaRangeReader with

        member _.DerivedContentLength(scopeContainer, id, relativePath) = async {
            match derivedPath id relativePath with
            | None -> return Error MediaRangeError.NotFound
            | Some path ->
                // A store that will not report metadata gets `NotFound`
                // here, which the range handler reads as "this blob has
                // no bounded path" and serves whole via `OpenDerived` —
                // where a genuinely absent blob then earns its 404. No
                // second whole-object download is spent guessing.
                match! blobStorage.GetMetadata(scopeContainer, path) with
                | Ok meta -> return Ok meta.Size
                | Error _ -> return Error MediaRangeError.NotFound
        }

        member _.OpenDerivedRange(scopeContainer, id, relativePath, range) = async {
            match derivedPath id relativePath with
            | None -> return Error MediaRangeError.NotFound
            | Some path ->
                match! MediaRangeRead.openRange blobStorage chunkBytes scopeContainer path range with
                | Ok stream -> return Ok(stream, MediaPaths.contentTypeFor relativePath)
                | Error e -> return Error e
        }