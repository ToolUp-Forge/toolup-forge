// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

open System
open System.Security.Cryptography
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

/// Wire shape persisted under `assets/records/{assetId}.json`.
/// `AssetRecord` itself uses an `AssetId` (single-case DU) and
/// `DerivativeProfileId` (single-case DU). FableJsonConverter
/// serialises those cleanly already; this alias exists so future
/// schema changes can ship a `_schemaVersion` field without
/// renaming the on-disk shape.
type private AssetRecordWire = AssetRecord

module private AssetJson =
    let private options = FableConverters.create ()

    let toJson (v: 'T) = JsonSerializer.Serialize(v, options)

    let fromJson<'T> (s: string) =
        JsonSerializer.Deserialize<'T>(s, options)

/// Default `IAssetStore` implementation. Wraps the SDK's
/// configured `IBlobStorage`:
///
///   - Originals at `assets/originals/{contentHash}` (raw bytes,
///     content-hash-keyed so two uploads of the same image share
///     storage).
///   - Records at `assets/records/{assetId}.json` (one JSON blob
///     per record — alt-text, caption, uploader, dimensions).
///   - Derivatives at
///     `assets/derivatives/{contentHash}/{name}.{ext}` (lazily
///     generated; cache miss triggers a render via
///     `IDerivativeRenderer`).
///
/// All blob names are scope-derived through the `scopeContainer`
/// parameter passed by the handler layer (which resolves it
/// from `ctx.Items["ToolUp.StorageScope"]`). Scope discipline
/// matches every other `IBlobStorage` consumer — paths inside
/// the scope's container are flat keys; cross-scope writes are
/// structurally impossible because the container is the scope
/// boundary.
///
/// Audit emission is optional (`AssetStoreOptions.EmitAudit`)
/// and routed through `IAuditLog` so external sinks see asset
/// activity via the same replication path as every other audit
/// event.
type DefaultAssetStore
    (
        blobStorage: IBlobStorage,
        renderer: IDerivativeRenderer,
        profiles: DerivativeProfileRegistry,
        auditLog: IAuditLog,
        logger: ILogger,
        options: AssetStoreOptions
    ) =

    static let recordsPrefix = "assets/records/"
    static let originalsPrefix = "assets/originals/"
    static let derivativesPrefix = "assets/derivatives/"

    static let contentHash (bytes: byte[]) : string =
        use sha = SHA256.Create()
        let hash = sha.ComputeHash bytes
        hash |> Array.map (sprintf "%02x") |> String.concat ""

    let recordKey (id: AssetId) =
        recordsPrefix + AssetId.value id + ".json"

    let originalKey (hash: string) = originalsPrefix + hash

    let derivativeKey (hash: string) (spec: DerivativeSpec) =
        sprintf "%s%s/%s.%s" derivativesPrefix hash spec.Name (ImageFormat.fileExtension spec.Format)

    let derivativePrefix (hash: string) = sprintf "%s%s/" derivativesPrefix hash

    let recordExistsForHash (container: string) (hash: string) : Async<bool> = async {
        let! recordNames = blobStorage.List(container, recordsPrefix)
        // If any other record references this hash, the original
        // is still referenced. Cheap-enough sweep — a scope with
        // thousands of records issues one List per delete, not
        // per upload.
        let! anyMatch =
            recordNames
            |> List.map (fun name -> async {
                let! result = blobStorage.Download(container, name)

                return
                    match result with
                    | Ok bytes ->
                        try
                            let text = System.Text.Encoding.UTF8.GetString bytes
                            let record: AssetRecord = AssetJson.fromJson text
                            record.ContentHash = hash
                        with _ ->
                            false
                    | Error _ -> false
            })
            |> Async.Parallel

        return anyMatch |> Array.exists id
    }

    let emitAudit (scopeId: string) (event: AuditEvent) =
        if options.EmitAudit then
            async {
                try
                    do! auditLog.Record(scopeId, event)
                with ex ->
                    logger.Warn(sprintf "[AssetStore] audit emission failed: %s" ex.Message)
            }
        else
            async.Return()

    interface IAssetStore with

        member _.Upload(scopeContainer, request) = async {
            try
                let hash = contentHash request.Bytes
                let assetId = AssetId.create ()

                let! uploadOriginal = blobStorage.Upload(scopeContainer, originalKey hash, request.Bytes)

                match uploadOriginal with
                | Error msg -> return Error(AssetUploadError.StorageError msg)
                | Ok _ ->
                    let! dims = renderer.Probe request.Bytes

                    let record: AssetRecord = {
                        Id = assetId
                        ContentHash = hash
                        OriginalFilename = request.OriginalFilename
                        MimeType = request.MimeType
                        SizeBytes = int64 request.Bytes.Length
                        AltText = request.AltText
                        Caption = request.Caption
                        UploadedBy = request.UploadedBy
                        UploadedAt = DateTimeOffset.UtcNow
                        Width = dims |> Option.map fst
                        Height = dims |> Option.map snd
                        DerivativeProfile = request.Profile
                    }

                    let recordBytes = System.Text.Encoding.UTF8.GetBytes(AssetJson.toJson record)
                    let! uploadRecord = blobStorage.Upload(scopeContainer, recordKey assetId, recordBytes)

                    match uploadRecord with
                    | Error msg ->
                        // Best-effort cleanup — leave the original
                        // in place (it may be referenced by another
                        // record). The next upload of the same hash
                        // overwrites idempotently.
                        return Error(AssetUploadError.StorageError msg)
                    | Ok _ ->
                        do!
                            emitAudit
                                scopeContainer
                                (AuditEvent.AssetUploaded {
                                    UserId = request.UploadedBy
                                    AssetId = AssetId.value assetId
                                    ContentHash = hash
                                    OriginalFilename = request.OriginalFilename
                                    MimeType = request.MimeType
                                    SizeBytes = record.SizeBytes
                                    Profile = DerivativeProfileId.value request.Profile
                                })

                        return Ok record
            with ex ->
                return Error(AssetUploadError.StorageError ex.Message)
        }

        member _.Get(scopeContainer, id) = async {
            let! download = blobStorage.Download(scopeContainer, recordKey id)

            return
                match download with
                | Error _ -> None
                | Ok bytes ->
                    try
                        let text = System.Text.Encoding.UTF8.GetString bytes
                        Some(AssetJson.fromJson<AssetRecord> text)
                    with _ ->
                        None
        }

        member this.GetDerivative(scopeContainer, id, derivativeName) = async {
            let store = this :> IAssetStore
            let! recordOpt = store.Get(scopeContainer, id)

            match recordOpt with
            | None -> return Error AssetDerivativeError.AssetNotFound
            | Some record ->
                match DerivativeProfileRegistry.resolveSpec record.DerivativeProfile derivativeName profiles with
                | None -> return Error(AssetDerivativeError.UnknownDerivative derivativeName)
                | Some spec ->
                    let cacheKey = derivativeKey record.ContentHash spec
                    let! cached = blobStorage.Download(scopeContainer, cacheKey)

                    match cached with
                    | Ok bytes -> return Ok(bytes, ImageFormat.mimeType spec.Format)
                    | Error _ ->
                        let! originalResult = blobStorage.Download(scopeContainer, originalKey record.ContentHash)

                        match originalResult with
                        | Error msg -> return Error(AssetDerivativeError.StorageError msg)
                        | Ok originalBytes ->
                            let! rendered = renderer.Render(originalBytes, spec)

                            match rendered with
                            | Error err ->
                                let msg =
                                    match err with
                                    | DecodeFailed m
                                    | DerivativeRenderError.RenderFailed m -> m
                                    | EncodeFailed(fmt, m) -> sprintf "%s encode failed: %s" fmt m

                                return Error(AssetDerivativeError.RenderFailed msg)
                            | Ok(bytes, mime) ->
                                let! cacheWrite = blobStorage.Upload(scopeContainer, cacheKey, bytes)

                                match cacheWrite with
                                | Error msg ->
                                    // Render succeeded but cache write
                                    // failed — serve the bytes anyway,
                                    // log the cache miss. Next request
                                    // re-renders.
                                    logger.Warn(sprintf "[AssetStore] cache write failed for %s: %s" cacheKey msg)
                                    return Ok(bytes, mime)
                                | Ok _ -> return Ok(bytes, mime)
        }

        member this.Delete(scopeContainer, id) = async {
            let store = this :> IAssetStore
            let! recordOpt = store.Get(scopeContainer, id)

            match recordOpt with
            | None ->
                // Idempotent — unknown id is a no-op. No audit
                // emission (no state change).
                return Ok()
            | Some record ->
                let! deleteRecord = blobStorage.Delete(scopeContainer, recordKey id)

                match deleteRecord with
                | Error msg -> return Error(AssetDeleteError.StorageError msg)
                | Ok() ->
                    // Cascade derivative cache for this hash.
                    let! derivativeNames = blobStorage.List(scopeContainer, derivativePrefix record.ContentHash)

                    let! _ =
                        derivativeNames
                        |> List.map (fun name -> blobStorage.Delete(scopeContainer, name))
                        |> Async.Parallel

                    // Original is shared across records on
                    // content-hash. Only delete it if no other
                    // record still references this hash.
                    let! stillReferenced = recordExistsForHash scopeContainer record.ContentHash

                    let! _ =
                        if stillReferenced then
                            async.Return(Ok())
                        else
                            blobStorage.Delete(scopeContainer, originalKey record.ContentHash)

                    do!
                        emitAudit
                            scopeContainer
                            (AuditEvent.AssetDeleted {
                                UserId = record.UploadedBy
                                AssetId = AssetId.value id
                                ContentHash = record.ContentHash
                            })

                    return Ok()
        }

        member _.List(scopeContainer, prefix, page) = async {
            let pageSize = 50
            let! names = blobStorage.List(scopeContainer, recordsPrefix)

            let filtered =
                names
                |> List.filter (fun n ->
                    if String.IsNullOrEmpty prefix then
                        true
                    else
                        let bare = n.Substring(recordsPrefix.Length).Replace(".json", "")
                        bare.StartsWith prefix)

            let! records =
                filtered
                |> List.map (fun name -> async {
                    let! download = blobStorage.Download(scopeContainer, name)

                    return
                        match download with
                        | Ok bytes ->
                            try
                                let text = System.Text.Encoding.UTF8.GetString bytes
                                Some(AssetJson.fromJson<AssetRecord> text)
                            with _ ->
                                None
                        | Error _ -> None
                })
                |> Async.Parallel

            let sorted =
                records
                |> Array.choose id
                |> Array.sortByDescending (fun r -> r.UploadedAt, AssetId.value r.Id)
                |> Array.toList

            let skip = max 0 page * pageSize

            return sorted |> List.skip (min skip sorted.Length) |> List.truncate pageSize
        }