// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.MediaLibrary

open System
open System.IO
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 469 — resumable chunked uploads ───────────────────────────
//
// `BlobUploadSessionStore` is the default `IUploadSessionStore`: a
// tus-shaped session over the SAME `IBlobStorage` the media library
// already uses, with no new substrate and no hosted service.
//
//   {container}/media/uploads/{sessionId}/session.json   [manifest]
//   {container}/media/uploads/{sessionId}/chunks/{offset} [chunk bytes]
//
// The chunk file name is the chunk's ABSOLUTE START OFFSET, zero-padded
// to 20 digits so a lexical listing is also the numeric order. That one
// choice buys three properties the manifest would otherwise have to
// carry: assembly needs no chunk index (list, sort, concatenate),
// re-appending an already-accepted chunk is discoverable with a single
// `GetMetadata` rather than a scan, and the manifest stays a bounded
// record however many chunks arrive — so a 32,000-chunk upload does not
// rewrite a 32,000-entry list on every append.
//
// ─── Commit is the ORDINARY upload path, deliberately ────────────────
//
// `CommitUpload` assembles the chunks and hands them to
// `IMediaLibrary.Upload`. It does not write `media/originals/{mediaId}`
// itself, and that is the point: the acceptance criterion is that a
// resumed upload commits to a record content-hash-equal to a
// single-shot upload of the same bytes, with ingestion flowing
// identically. Re-implementing the ingestion sequence here would make
// that a claim to be tested; routing through `Upload` makes it true by
// construction, and keeps derivation / transcode / status notification
// in exactly one place.
//
// The honest limit that follows: assembly materialises the object in
// memory, because `MediaUploadRequest` carries `byte[]`. That is the
// same ceiling single-shot upload has today — this phase removes the
// NETWORK single point of failure, not the server-side memory one. A
// streaming ingestion path is a separate, larger piece of work.
//
// ─── No background service (GP 13) ───────────────────────────────────
//
// Stale sessions are reclaimed by an opportunistic sweep at the top of
// `BeginUpload`, not by a hosted timer. A deployment that never opens a
// session never runs the sweep, never allocates, and never registers
// anything — the sweep's cost is paid by the act that creates the
// obligation.

/// Persisted session state. Public for STJ round-tripping (the same
/// reason `MediaIngestionStatusUpdate` is), never a consumer-facing
/// vocabulary — consumers hold `UploadProgress`.
///
/// `LastTouchedAt` is the TTL clock. It is a FIELD rather than the
/// manifest blob's `LastModified` so expiry means the same thing on
/// every backing store, and so a test can drive it from an injected
/// clock instead of the wall.
type UploadSessionManifest = {
    SessionId: string
    ScopeContainer: string
    OriginalFilename: string
    MimeType: string
    DeclaredSizeBytes: int64
    UploadedBy: string
    Caption: string option
    CreatedAt: DateTimeOffset
    LastTouchedAt: DateTimeOffset
    ReceivedBytes: int64
}

/// Notification payload published over `INotificationChannel` (key
/// `"MediaLibrary.UploadProgress"`). Public for STJ round-tripping.
///
/// `Phase` is a stable wire token — `"appending"` / `"committed"` /
/// `"aborted"` — mirroring how `MediaIngestionStatus.token` keeps the
/// ingestion stream's vocabulary out of the payload's type.
type MediaUploadProgressUpdate = {
    SessionId: string
    /// The committed item's id — `None` until commit succeeds.
    MediaId: string option
    FileName: string
    ReceivedBytes: int64
    DeclaredSizeBytes: int64
    Phase: string
}

module UploadSessionPhase =
    [<Literal>]
    let appending = "appending"

    [<Literal>]
    let committed = "committed"

    [<Literal>]
    let aborted = "aborted"

module private UploadSessionPaths =
    [<Literal>]
    let uploadsPrefix = "media/uploads/"

    [<Literal>]
    let manifestLeaf = "session.json"

    let sessionDir (sessionId: UploadSessionId) =
        uploadsPrefix + UploadSessionId.value sessionId + "/"

    let manifest (sessionId: UploadSessionId) = sessionDir sessionId + manifestLeaf

    let chunksDir (sessionId: UploadSessionId) = sessionDir sessionId + "chunks/"

    /// Chunk blob name — the absolute start offset, zero-padded so the
    /// store's lexical `List` order is the numeric order. 20 digits
    /// covers `Int64.MaxValue` (19) with a digit to spare.
    let chunk (sessionId: UploadSessionId) (offset: int64) =
        chunksDir sessionId + offset.ToString("D20")

    /// Recover a chunk's offset from its blob name. `None` for anything
    /// that is not one of ours, so a stray blob under the prefix is
    /// skipped rather than mis-assembled.
    let chunkOffset (blobName: string) : int64 option =
        let leaf =
            match blobName.LastIndexOf '/' with
            | -1 -> blobName
            | i -> blobName.Substring(i + 1)

        match Int64.TryParse leaf with
        | true, v when v >= 0L -> Some v
        | _ -> None

module private UploadSessionJson =
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

/// Default `IUploadSessionStore` over `IBlobStorage`, committing
/// through the composed `IMediaLibrary`.
///
/// `now` is injected so the session TTL is testable without sleeping;
/// the secondary constructor supplies the wall clock, which is the
/// shape every composition root uses. (An explicit secondary ctor
/// rather than an optional argument — an `?arg` folds the two into one
/// widened constructor and reads as a REMOVAL to the public-API
/// approval gate.)
type BlobUploadSessionStore
    (
        blobStorage: IBlobStorage,
        library: IMediaLibrary,
        notifications: INotificationChannel option,
        options: MediaLibraryOptions,
        logger: ILogger,
        now: unit -> DateTimeOffset
    ) =

    let maxChunkBytes = MediaLibraryOptions.effectiveMaxChunkBytes options
    let sessionTtl = MediaLibraryOptions.effectiveUploadSessionTtl options

    let publishProgress (uploadedBy: string) (payload: MediaUploadProgressUpdate) = async {
        match notifications with
        | None -> ()
        | Some channel ->
            try
                let json = UploadSessionJson.serializeString payload
                do! channel.Publish(uploadedBy, CustomNotification("MediaLibrary.UploadProgress", json))
            with ex ->
                logger.Error("[MediaLibrary] Failed to publish upload progress", Some ex)
    }

    let progressOf (manifest: UploadSessionManifest) (phase: string) (mediaId: string option) = {
        SessionId = manifest.SessionId
        MediaId = mediaId
        FileName = manifest.OriginalFilename
        ReceivedBytes = manifest.ReceivedBytes
        DeclaredSizeBytes = manifest.DeclaredSizeBytes
        Phase = phase
    }

    let writeManifest (container: string) (manifest: UploadSessionManifest) = async {
        let path = UploadSessionPaths.manifest (UploadSessionId manifest.SessionId)

        match! blobStorage.Upload(container, path, UploadSessionJson.serialize manifest) with
        | Ok _ -> return Ok()
        | Error e -> return Error(SessionStorageError e)
    }

    /// Load a session, enforcing both isolation layers: the container
    /// the blob was read from (structural — a foreign scope cannot
    /// address it at all) and the container recorded in the manifest
    /// (the second line, for a store that does not isolate).
    let loadManifest (container: string) (sessionId: UploadSessionId) = async {
        match! blobStorage.Download(container, UploadSessionPaths.manifest sessionId) with
        | Error _ -> return Error SessionNotFound
        | Ok bytes ->
            match UploadSessionJson.tryDeserialize<UploadSessionManifest> bytes with
            | None -> return Error SessionNotFound
            | Some manifest ->
                if manifest.ScopeContainer <> container then
                    return Error SessionScopeMismatch
                else
                    return Ok manifest
    }

    /// Delete every blob under a session's prefix. Best-effort per blob
    /// — a store that fails one delete must not strand the rest.
    let deleteSession (container: string) (sessionId: UploadSessionId) = async {
        let! names = blobStorage.List(container, UploadSessionPaths.sessionDir sessionId)

        for name in names do
            let! _ = blobStorage.Delete(container, name)
            ()
    }

    /// 469.C — reclaim sessions whose last append is older than the
    /// TTL. Opportunistic: runs at `BeginUpload`, never on a timer, and
    /// every failure is swallowed after logging. A sweep that throws
    /// must not fail the upload the caller actually asked for.
    let sweepStale (container: string) = async {
        try
            let cutoff = now () - sessionTtl
            let! names = blobStorage.List(container, UploadSessionPaths.uploadsPrefix)

            let manifests =
                names
                |> List.filter (fun n -> n.EndsWith("/" + UploadSessionPaths.manifestLeaf, StringComparison.Ordinal))

            for path in manifests do
                match! blobStorage.Download(container, path) with
                | Error _ -> ()
                | Ok bytes ->
                    match UploadSessionJson.tryDeserialize<UploadSessionManifest> bytes with
                    | None -> ()
                    | Some manifest ->
                        if manifest.LastTouchedAt < cutoff then
                            do! deleteSession container (UploadSessionId manifest.SessionId)
        with ex ->
            logger.Warn(sprintf "[MediaLibrary] upload-session sweep failed: %s" ex.Message)
    }

    /// Read every accepted chunk in offset order and concatenate.
    /// Verifies contiguity from zero as it goes: a gap means the
    /// session's chunk set does not describe a whole object, and
    /// assembling it anyway would produce a plausible, wrong file.
    let assemble (container: string) (sessionId: UploadSessionId) = async {
        let! names = blobStorage.List(container, UploadSessionPaths.chunksDir sessionId)

        let ordered =
            names
            |> List.choose (fun n -> UploadSessionPaths.chunkOffset n |> Option.map (fun o -> o, n))
            |> List.sortBy fst

        use buffer = new MemoryStream()
        let mutable expected = 0L
        let mutable failure = None

        for offset, name in ordered do
            if failure.IsNone then
                if offset <> expected then
                    failure <-
                        Some(SessionStorageError(sprintf "chunk gap: expected offset %d, found %d" expected offset))
                else
                    match! blobStorage.Download(container, name) with
                    | Error e -> failure <- Some(SessionStorageError e)
                    | Ok bytes ->
                        buffer.Write(bytes, 0, bytes.Length)
                        expected <- expected + int64 bytes.Length

        match failure with
        | Some e -> return Error e
        | None -> return Ok(buffer.ToArray())
    }

    /// Wall-clock composition — the shape every composition root uses.
    new(blobStorage, library, notifications, options, logger) =
        BlobUploadSessionStore(blobStorage, library, notifications, options, logger, (fun () -> DateTimeOffset.UtcNow))

    interface IUploadSessionStore with

        member _.BeginUpload(scopeContainer, declaration) = async {
            do! sweepStale scopeContainer

            let sessionId = UploadSessionId.create ()
            let at = now ()

            let manifest = {
                SessionId = UploadSessionId.value sessionId
                ScopeContainer = scopeContainer
                OriginalFilename = declaration.OriginalFilename
                MimeType = declaration.MimeType
                DeclaredSizeBytes = declaration.DeclaredSizeBytes
                UploadedBy = declaration.UploadedBy
                Caption = declaration.Caption
                CreatedAt = at
                LastTouchedAt = at
                ReceivedBytes = 0L
            }

            match! writeManifest scopeContainer manifest with
            | Error e -> return Error e
            | Ok() ->
                do! publishProgress declaration.UploadedBy (progressOf manifest UploadSessionPhase.appending None)
                return Ok sessionId
        }

        member _.AppendChunk(scopeContainer, sessionId, offset, chunk) = async {
            match! loadManifest scopeContainer sessionId with
            | Error e -> return Error e
            | Ok manifest ->
                let length = if isNull chunk then 0 else chunk.Length

                if length > maxChunkBytes then
                    return Error(ChunkTooLarge(length, maxChunkBytes))
                elif offset < 0L || offset > manifest.ReceivedBytes then
                    // A gap (or a negative offset) is never recoverable
                    // by guessing — hand back the cursor and let the
                    // client resume from it.
                    return Error(OffsetMismatch(manifest.ReceivedBytes, offset))
                elif offset < manifest.ReceivedBytes then
                    // Behind the cursor: the only benign reading is a
                    // retry of a chunk we already accepted, so require
                    // it to be byte-for-byte the same LENGTH at the
                    // same offset before calling it a no-op. Anything
                    // else is a client re-writing history.
                    match! blobStorage.GetMetadata(scopeContainer, UploadSessionPaths.chunk sessionId offset) with
                    | Ok meta when meta.Size = int64 length ->
                        return
                            Ok {
                                SessionId = sessionId
                                ReceivedBytes = manifest.ReceivedBytes
                                DeclaredSizeBytes = manifest.DeclaredSizeBytes
                            }
                    | _ -> return Error(OffsetMismatch(manifest.ReceivedBytes, offset))
                elif length = 0 then
                    // At the cursor with nothing to add. Accepting it
                    // as a no-op keeps a client's keep-alive or
                    // zero-length final chunk from being an error.
                    return
                        Ok {
                            SessionId = sessionId
                            ReceivedBytes = manifest.ReceivedBytes
                            DeclaredSizeBytes = manifest.DeclaredSizeBytes
                        }
                elif manifest.ReceivedBytes + int64 length > manifest.DeclaredSizeBytes then
                    return
                        Error(DeclaredSizeExceeded(manifest.ReceivedBytes + int64 length, manifest.DeclaredSizeBytes))
                else
                    match! blobStorage.Upload(scopeContainer, UploadSessionPaths.chunk sessionId offset, chunk) with
                    | Error e -> return Error(SessionStorageError e)
                    | Ok _ ->
                        let advanced = {
                            manifest with
                                ReceivedBytes = manifest.ReceivedBytes + int64 length
                                LastTouchedAt = now ()
                        }

                        // The manifest is written AFTER the chunk, so a
                        // crash between the two leaves an orphan chunk
                        // the cursor does not count — which the next
                        // append overwrites at the same offset. The
                        // reverse order would advertise bytes that are
                        // not there.
                        match! writeManifest scopeContainer advanced with
                        | Error e -> return Error e
                        | Ok() ->
                            do!
                                publishProgress
                                    advanced.UploadedBy
                                    (progressOf advanced UploadSessionPhase.appending None)

                            return
                                Ok {
                                    SessionId = sessionId
                                    ReceivedBytes = advanced.ReceivedBytes
                                    DeclaredSizeBytes = advanced.DeclaredSizeBytes
                                }
        }

        member _.CommitUpload(scopeContainer, sessionId) = async {
            match! loadManifest scopeContainer sessionId with
            | Error e -> return Error e
            | Ok manifest ->
                match! assemble scopeContainer sessionId with
                | Error e -> return Error e
                | Ok bytes ->
                    let actual = int64 bytes.Length

                    // 469.B — the actual measurement, taken where it is
                    // the only honest one. Under-delivery keeps the
                    // session (the client can send the rest); any
                    // over-delivery or cap breach fails CLOSED and
                    // takes the session with it, so a client cannot
                    // retry its way past the cap.
                    if actual < manifest.DeclaredSizeBytes then
                        return Error(IncompleteUpload(actual, manifest.DeclaredSizeBytes))
                    elif actual > manifest.DeclaredSizeBytes then
                        do! deleteSession scopeContainer sessionId
                        return Error(DeclaredSizeExceeded(actual, manifest.DeclaredSizeBytes))
                    elif actual > options.MaxBytes then
                        do! deleteSession scopeContainer sessionId
                        return Error(InvalidDeclaration(FileTooLarge(actual, options.MaxBytes)))
                    else
                        match
                            MediaUploadRequest.create
                                options
                                bytes
                                manifest.OriginalFilename
                                manifest.MimeType
                                manifest.UploadedBy
                                manifest.Caption
                        with
                        | Error e ->
                            do! deleteSession scopeContainer sessionId
                            return Error(InvalidDeclaration e)
                        | Ok request ->
                            match! library.Upload(scopeContainer, request) with
                            // A storage failure is not the client's
                            // fault and is not fatal to the session —
                            // leave it standing so commit can be
                            // retried without re-sending gigabytes.
                            | Error e -> return Error(UploadFailed e)
                            | Ok record ->
                                do! deleteSession scopeContainer sessionId

                                do!
                                    publishProgress
                                        manifest.UploadedBy
                                        (progressOf
                                            { manifest with ReceivedBytes = actual }
                                            UploadSessionPhase.committed
                                            (Some(MediaId.value record.Id)))

                                return Ok record
        }

        member _.AbortUpload(scopeContainer, sessionId) = async {
            match! loadManifest scopeContainer sessionId with
            | Error e -> return Error e
            | Ok manifest ->
                do! deleteSession scopeContainer sessionId
                do! publishProgress manifest.UploadedBy (progressOf manifest UploadSessionPhase.aborted None)
                return Ok()
        }