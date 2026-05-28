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
}

module AzureBlobStorageConfig =
    let defaults = {
        ConnectionString = ""
        RootContainer = "toolup"
    }

// ─── Helpers ─────────────────────────────────────────────────────────

let private blobKey (toolupContainer: string) (blobName: string) = $"{toolupContainer}/{blobName}"

// ─── IBlobStorage implementation ─────────────────────────────────────

/// Azure Blob Storage implementation of `IBlobStorage`. Maps ToolUp's
/// per-scope logical containers onto blob-name prefixes inside a single
/// Azure container. Thread-safe via the underlying Azure SDK clients
/// (designed for reuse).
type AzureBlobStorage(config: AzureBlobStorageConfig) =
    let serviceClient = BlobServiceClient(config.ConnectionString)
    let containerClient = serviceClient.GetBlobContainerClient(config.RootContainer)

    // One-time eager creation. Azure accounts are billed per-container
    // only for content, not existence, so creating on startup costs
    // nothing even if the deployment hasn't persisted yet.
    do containerClient.CreateIfNotExists() |> ignore

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
                let blob = containerClient.GetBlobClient(blobKey toolupContainer blobName)
                use ms = new MemoryStream(content)
                let! _ = blob.UploadAsync(ms, overwrite = true) |> Async.AwaitTask
                return Ok(blob.Uri.ToString())
            with ex ->
                return Error ex.Message
        }

        member _.Download(toolupContainer, blobName) = async {
            try
                let blob = containerClient.GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.DownloadContentAsync() |> Async.AwaitTask
                return Ok(response.Value.Content.ToArray())
            with
            | :? RequestFailedException as ex when ex.Status = 404 ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | ex -> return Error ex.Message
        }

        member _.Delete(toolupContainer, blobName) = async {
            try
                let blob = containerClient.GetBlobClient(blobKey toolupContainer blobName)
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
                containerClient.GetBlobsAsync(
                    BlobTraits.None,
                    BlobStates.None,
                    fullPrefix,
                    System.Threading.CancellationToken()
                )

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
                let blob = containerClient.GetBlobClient(blobKey toolupContainer blobName)
                let! response = blob.ExistsAsync() |> Async.AwaitTask
                return response.Value
            with _ ->
                return false
        }

        member _.GetMetadata(toolupContainer, blobName) = async {
            try
                let blob = containerClient.GetBlobClient(blobKey toolupContainer blobName)
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
            }
        )