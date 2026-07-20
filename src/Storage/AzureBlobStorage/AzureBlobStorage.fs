module ToolUp.Storage.AzureBlobStorage

open System
open System.IO
open Azure
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open ToolUp.Platform.BlobStorage

// ─── Configuration ───────────────────────────────────────────────────

/// Configuration for `AzureBlobStorage`. Construct from a connection
/// string read out of `TOOLUP_AZURE_STORAGE_CONNECTION_STRING` (or
/// wherever the deployment keeps it) and choose a root container name.
///
/// Why a single root container: Azure Blob containers must follow a
/// naming convention — 3-63 characters, lowercase letters, digits,
/// hyphens, start with a letter or digit — which rules out ToolUp's
/// `_platform` scope and risks collisions with user-supplied scope
/// identifiers. A single Azure container holds every ToolUp scope as
/// a blob-name prefix (`{toolupContainer}/{blobName}`), mapping cleanly
/// onto Azure portal's virtual-directory view without leaking
/// naming-constraint complexity to callers. Also stays well under
/// Azure's per-account container quota at any realistic scale.
type AzureBlobStorageConfig = {
    /// Azure Storage connection string. `DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;`
    /// or the shorthand `UseDevelopmentStorage=true` for Azurite.
    ConnectionString: string
    /// Single Azure container that holds all ToolUp scopes. Defaults
    /// to `"toolup"`. Must satisfy Azure naming rules; the class
    /// creates it on construction if absent.
    RootContainer: string
    /// Phase 2c — optional per-call connection-string provider for
    /// out-of-band credential rotation. `None` (default) preserves
    /// today's behaviour: the static `ConnectionString` above is used
    /// and the `BlobServiceClient` is built once for the process
    /// lifetime. `Some f` calls `f ()` on each operation and rebuilds
    /// the client *only when the resolved connection string changes*
    /// (change-detection cache — not a per-call reconstruction), so a
    /// rotated AccountKey / regenerated SAS is picked up without a
    /// restart. The closure typically closes over an
    /// `ISecretStore.GetSecret` read, keeping this companion free of a
    /// direct dependency on the secret backend (GP 12).
    ConnectionStringProvider: (unit -> string) option
}

module AzureBlobStorageConfig =
    let defaults = {
        ConnectionString = ""
        RootContainer = "toolup"
        ConnectionStringProvider = None
    }

// ─── Helpers ─────────────────────────────────────────────────────────

let private blobKey (toolupContainer: string) (blobName: string) = $"{toolupContainer}/{blobName}"

// ─── IBlobStorage implementation ─────────────────────────────────────

/// Azure Blob Storage implementation of `IBlobStorage`. Maps ToolUp's
/// per-scope logical containers onto blob-name prefixes inside a single
/// Azure container. Thread-safe via the underlying Azure SDK clients
/// (designed for reuse).
type AzureBlobStorage(config: AzureBlobStorageConfig) =
    // Change-detection cache (Phase 2c). The container client is
    // (re)built whenever the resolved connection string differs from the
    // cached one — for the static (`ConnectionStringProvider = None`)
    // path that is exactly once, reproducing the original build-once
    // behaviour; for the provider path it rebuilds only on rotation, not
    // per call, since `BlobServiceClient` construction parses the
    // connection string and builds an HTTP pipeline. Instance-level
    // `mutable` guarded by `gate` — justified by the caching intent.
    let gate = obj ()
    let mutable cachedConnStr = ""
    let mutable containerClient: BlobContainerClient = null

    let resolveConnStr () =
        match config.ConnectionStringProvider with
        | Some provider -> provider ()
        | None -> config.ConnectionString

    // Return the current container client, rebuilding on a connection-
    // string change. One-time eager creation of the container itself is
    // preserved: Azure bills per-container only for content, not
    // existence, so `CreateIfNotExists` on (re)build costs nothing.
    let container () =
        let resolved = resolveConnStr ()

        lock gate (fun () ->
            if isNull containerClient || resolved <> cachedConnStr then
                let serviceClient = BlobServiceClient(resolved)
                let cc = serviceClient.GetBlobContainerClient(config.RootContainer)
                cc.CreateIfNotExists() |> ignore
                cachedConnStr <- resolved
                containerClient <- cc

            containerClient)

    // Eager build at construction — preserves the original fail-fast on a
    // malformed connection string / unreachable account at startup.
    do container () |> ignore

    // Phase 600 follow-up — live-etag disclosure read for a refused
    // conditional write. `Ok None` = the blob is absent (the only case
    // the seam may report `ETagMismatch None`); `Error` = the
    // disclosure read itself failed, surfaced as
    // `ConditionalWriteFailure` rather than a fabricated verdict.
    let currentETag (key: string) : Async<Result<string option, string>> = async {
        try
            let blob = (container ()).GetBlobClient key
            let! response = blob.GetPropertiesAsync() |> Async.AwaitTask
            return Ok(Some(response.Value.ETag.ToString()))
        with
        | :? RequestFailedException as ex when ex.Status = 404 -> return Ok None
        | ex -> return Error ex.Message
    }

    interface IBlobStorage with
        member this.Erase(container, prefix, policy, dryRun) =
            ToolUp.Platform.BlobStorage.eraseByPrefix
                (this :> ToolUp.Platform.BlobStorage.IBlobStorage)
                container
                prefix
                policy
                dryRun

        member _.Upload(toolupContainer, blobName, content) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                use ms = new MemoryStream(content)
                let! _ = blob.UploadAsync(ms, overwrite = true) |> Async.AwaitTask
                return Ok(blob.Uri.ToString())
            with ex ->
                return Error ex.Message
        }

        member _.Download(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.DownloadContentAsync() |> Async.AwaitTask
                return Ok(response.Value.Content.ToArray())
            with
            | :? RequestFailedException as ex when ex.Status = 404 ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | ex -> return Error ex.Message
        }

        member _.DownloadRange(toolupContainer, blobName, offset, length) = async {
            if offset < 0L then
                return Error "DownloadRange: offset must be non-negative"
            elif length <= 0 then
                return Error "DownloadRange: length must be positive"
            else
                try
                    let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                    // Native range request — Azure clamps a range that
                    // overshoots EOF and 416s a range starting past it.
                    let options = BlobDownloadOptions(Range = HttpRange(offset, int64 length))
                    let! response = blob.DownloadContentAsync options |> Async.AwaitTask
                    return Ok(response.Value.Content.ToArray())
                with
                | :? RequestFailedException as ex when ex.Status = 404 ->
                    return Error $"Blob not found: {toolupContainer}/{blobName}"
                | :? RequestFailedException as ex when ex.Status = 416 ->
                    // Past-EOF clamp per the interface contract.
                    return Ok Array.empty
                | ex -> return Error ex.Message
        }

        member _.Delete(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                // `DeleteIfExists` is idempotent by design — matches
                // the contract's promise that deleting a missing
                // blob succeeds.
                let! _ = blob.DeleteIfExistsAsync() |> Async.AwaitTask
                return Ok()
            with ex ->
                return Error ex.Message
        }

        member _.List(toolupContainer, prefix) = async {
            let fullPrefix = blobKey toolupContainer prefix
            let stripLen = (toolupContainer + "/").Length
            let results = System.Collections.Generic.List<string>()

            // All four positional args — F# can't resolve the C#-
            // style optional-params overload from a subset, so we
            // pass the default CancellationToken explicitly.
            let enumerable: AsyncPageable<BlobItem> =
                (container ())
                    .GetBlobsAsync(BlobTraits.None, BlobStates.None, fullPrefix, System.Threading.CancellationToken())

            let enumerator = enumerable.GetAsyncEnumerator()

            try
                let mutable keepGoing = true

                while keepGoing do
                    let! hasNext = enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask

                    if hasNext then
                        results.Add(enumerator.Current.Name.Substring stripLen)
                    else
                        keepGoing <- false
            finally
                enumerator.DisposeAsync().AsTask().Wait()

            return results |> List.ofSeq
        }

        member _.Exists(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.ExistsAsync() |> Async.AwaitTask
                return response.Value
            with _ ->
                return false
        }

        member _.GetMetadata(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.GetPropertiesAsync() |> Async.AwaitTask
                let props = response.Value

                let contentType =
                    if String.IsNullOrEmpty props.ContentType then
                        None
                    else
                        Some props.ContentType

                return
                    Ok {
                        Size = props.ContentLength
                        LastModified = props.LastModified.UtcDateTime
                        ContentType = contentType
                    }
            with
            | :? RequestFailedException as ex when ex.Status = 404 ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | ex -> return Error ex.Message
        }

    // ─── Phase 600 follow-up — conditional writes (the ETag seam) ────
    //
    // Azure-native ETag preconditions: `BlobRequestConditions.IfMatch`
    // for the read-modify-write guard, `IfNoneMatch = ETag.All` (the
    // wire's `If-None-Match: *`) for create-only. The etag token is the
    // native Azure ETag, opaque per the seam contract. A refused
    // `If-Match` surfaces as 412 ConditionNotMet (including against an
    // absent blob); a losing `If-None-Match: *` create surfaces as 409
    // BlobAlreadyExists — both map to `ETagMismatch`, with the live
    // etag recovered by a follow-up properties read (`None` only when
    // the blob is absent).
    interface IConditionalBlobStorage with
        member _.DownloadWithETag(toolupContainer, blobName) = async {
            try
                let blob = (container ()).GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.DownloadContentAsync() |> Async.AwaitTask
                let result = response.Value
                return Ok(result.Content.ToArray(), result.Details.ETag.ToString())
            with
            | :? RequestFailedException as ex when ex.Status = 404 ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | ex -> return Error ex.Message
        }

        member _.UploadWithETag(toolupContainer, blobName, content, condition) = async {
            let key = blobKey toolupContainer blobName

            try
                let blob = (container ()).GetBlobClient key
                let conditions = BlobRequestConditions()

                match condition with
                | IfMatch etag -> conditions.IfMatch <- Nullable(ETag etag)
                | IfAbsent -> conditions.IfNoneMatch <- Nullable ETag.All

                let options = BlobUploadOptions(Conditions = conditions)
                use ms = new MemoryStream(content)
                let! response = blob.UploadAsync(ms, options) |> Async.AwaitTask
                return Ok(response.Value.ETag.ToString())
            with
            | :? RequestFailedException as ex when ex.Status = 412 || ex.Status = 409 ->
                match! currentETag key with
                | Ok current -> return Error(ETagMismatch current)
                | Error msg ->
                    return Error(ConditionalWriteFailure $"precondition refused; etag disclosure read failed: {msg}")
            | ex -> return Error(ConditionalWriteFailure ex.Message)
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `IBlobStorage` from an `AzureBlobStorageConfig`. One-
/// time startup work: verifies the root container exists (creates it
/// if absent). Thread-safe thereafter — reuse the same instance for
/// every request.
let create (config: AzureBlobStorageConfig) : IBlobStorage = AzureBlobStorage config :> IBlobStorage

/// Read the connection string from `TOOLUP_AZURE_STORAGE_CONNECTION_STRING`
/// and construct an `AzureBlobStorage`. Returns `None` when the env var
/// is unset — the deployment falls back to `LocalFileStorage` or
/// whatever it has wired. `rootContainer` overrides the default
/// `"toolup"` name when `Some`.
let fromEnv (rootContainer: string option) : IBlobStorage option =
    match Environment.GetEnvironmentVariable "TOOLUP_AZURE_STORAGE_CONNECTION_STRING" with
    | null
    | "" -> None
    | connectionString ->
        Some(
            create {
                ConnectionString = connectionString
                RootContainer = rootContainer |> Option.defaultValue "toolup"
                // `fromEnv` reads the connection string once. Deployments
                // that rotate the AccountKey out of band construct via
                // `create` with `ConnectionStringProvider = Some f`
                // (Phase 2c) to survive rotation without a restart.
                ConnectionStringProvider = None
            }
        )