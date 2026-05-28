module ToolUp.Storage.GoogleCloudStorage

// `GoogleCredential.FromJson` is marked deprecated in favour of the
// newer `CredentialFactory` API. For single-call credential
// construction from inline JSON, `FromJson` remains the simplest
// surface and carries no security risk in this use case (caller
// supplies a trusted service-account JSON read from config).
#nowarn "44"

open System
open System.IO
open System.Net
open Google
open Google.Apis.Auth.OAuth2
open Google.Cloud.Storage.V1
open ToolUp.Platform.BlobStorage

// ─── Configuration ───────────────────────────────────────────────────

/// Configuration for `GoogleCloudStorage`. Takes the bucket name plus
/// optional service-account JSON credentials. When `CredentialsJson`
/// is `None`, the GCS SDK follows its Application Default Credentials
/// chain (env var `GOOGLE_APPLICATION_CREDENTIALS`, gcloud-login
/// user creds, GCE metadata server, GKE workload identity) — the
/// standard GCP deployment path.
///
/// Like Azure and S3, ToolUp's logical containers map to object-name
/// prefixes inside a single GCS bucket — bucket names are globally
/// unique across all GCP projects, so one-per-scope is impractical,
/// and prefixes let a deployment use one bucket for all data.
type GoogleCloudStorageConfig = {
    /// Existing GCS bucket. The class does NOT create the bucket —
    /// provisioning is the deployment's responsibility, and the
    /// service account's IAM role usually restricts access to a
    /// specific bucket.
    BucketName: string
    /// Service-account JSON credentials string. `None` uses the ADC
    /// resolution chain (`GOOGLE_APPLICATION_CREDENTIALS` path, gcloud
    /// auth, metadata server, workload identity).
    CredentialsJson: string option
}

module GoogleCloudStorageConfig =
    let defaults = {
        BucketName = ""
        CredentialsJson = None
    }

// ─── Helpers ─────────────────────────────────────────────────────────

let private blobKey (toolupContainer: string) (blobName: string) = $"{toolupContainer}/{blobName}"

let private buildClient (config: GoogleCloudStorageConfig) : StorageClient =
    match config.CredentialsJson with
    | Some json ->
        let credential = GoogleCredential.FromJson json
        StorageClient.Create credential
    | None ->
        // Application Default Credentials — the SDK resolves env /
        // metadata server / workload identity itself.
        StorageClient.Create()

// ─── IBlobStorage implementation ─────────────────────────────────────

/// Google Cloud Storage implementation of `IBlobStorage`. Single
/// bucket, ToolUp logical scopes encoded as object-name prefixes.
/// Thread-safe — `StorageClient` is documented reusable across
/// concurrent calls.
type GoogleCloudStorage(config: GoogleCloudStorageConfig) =
    let client = buildClient config

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
                use ms = new MemoryStream(content)
                let key = blobKey toolupContainer blobName
                // ContentType `null` lets GCS sniff from the object
                // name; we don't force octet-stream because the
                // platform doesn't track MIME per blob.
                let! obj = client.UploadObjectAsync(config.BucketName, key, null, ms) |> Async.AwaitTask
                return Ok $"gs://{obj.Bucket}/{obj.Name}"
            with ex ->
                return Error ex.Message
        }

        member _.Download(toolupContainer, blobName) = async {
            try
                use ms = new MemoryStream()
                let key = blobKey toolupContainer blobName
                let! _ = client.DownloadObjectAsync(config.BucketName, key, ms) |> Async.AwaitTask
                return Ok(ms.ToArray())
            with
            | :? GoogleApiException as ex when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | ex -> return Error ex.Message
        }

        member _.Delete(toolupContainer, blobName) = async {
            // GCS DeleteObject raises 404 for missing objects; we
            // swallow that to keep the idempotent contract. Any
            // other error surfaces as `Error`.
            try
                let key = blobKey toolupContainer blobName
                do! client.DeleteObjectAsync(config.BucketName, key) |> Async.AwaitTask
                return Ok()
            with
            | :? GoogleApiException as ex when ex.HttpStatusCode = HttpStatusCode.NotFound -> return Ok()
            | ex -> return Error ex.Message
        }

        member _.List(toolupContainer, prefix) = async {
            let fullPrefix = blobKey toolupContainer prefix
            let stripLen = (toolupContainer + "/").Length
            let results = System.Collections.Generic.List<string>()

            // `fullPrefix` is the 2nd positional arg; no options
            // needed for a simple prefix list.
            let enumerable = client.ListObjectsAsync(config.BucketName, fullPrefix)
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
                let key = blobKey toolupContainer blobName
                let! _ = client.GetObjectAsync(config.BucketName, key) |> Async.AwaitTask
                return true
            with
            | :? GoogleApiException as ex when ex.HttpStatusCode = HttpStatusCode.NotFound -> return false
            | _ -> return false
        }

        member _.GetMetadata(toolupContainer, blobName) = async {
            try
                let key = blobKey toolupContainer blobName
                let! obj = client.GetObjectAsync(config.BucketName, key) |> Async.AwaitTask

                let size = if obj.Size.HasValue then int64 obj.Size.Value else 0L

                let lastModified =
                    if obj.UpdatedDateTimeOffset.HasValue then
                        obj.UpdatedDateTimeOffset.Value.UtcDateTime
                    else
                        DateTime.UtcNow

                let contentType =
                    if String.IsNullOrEmpty obj.ContentType then
                        None
                    else
                        Some obj.ContentType

                return
                    Ok {
                        Size = size
                        LastModified = lastModified
                        ContentType = contentType
                    }
            with
            | :? GoogleApiException as ex when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | ex -> return Error ex.Message
        }

// ─── Public entry points ─────────────────────────────────────────────

let create (config: GoogleCloudStorageConfig) : IBlobStorage =
    GoogleCloudStorage config :> IBlobStorage

/// Read bucket + credentials from environment and construct a
/// `GoogleCloudStorage`. Returns `None` when `TOOLUP_GCS_BUCKET` is
/// unset — deployment falls back to whatever `compose` has wired.
///
/// Required: `TOOLUP_GCS_BUCKET`.
/// Optional: `TOOLUP_GCS_CREDENTIALS_JSON` (service-account JSON
/// inline). When unset, GCS SDK's Application Default Credentials
/// chain resolves automatically — typically via
/// `GOOGLE_APPLICATION_CREDENTIALS` pointing at a JSON file.
let fromEnv () : IBlobStorage option =
    let read name = Environment.GetEnvironmentVariable name

    match read "TOOLUP_GCS_BUCKET" with
    | null
    | "" -> None
    | bucket ->
        let credsJson =
            match read "TOOLUP_GCS_CREDENTIALS_JSON" with
            | null
            | "" -> None
            | json -> Some json

        Some(
            create {
                BucketName = bucket
                CredentialsJson = credsJson
            }
        )