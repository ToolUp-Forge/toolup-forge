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
    /// Phase 2c — optional per-call service-account-JSON provider for
    /// out-of-band credential rotation. `None` (default) preserves
    /// today's behaviour: the `StorageClient` is built once from
    /// `CredentialsJson` (or the ADC chain when that is `None`) for the
    /// process lifetime. `Some f` calls `f ()` on each operation and
    /// rebuilds the client *only when the resolved JSON changes*
    /// (change-detection cache — not a per-call reconstruction), so a
    /// rolled service-account key is picked up without a restart. The
    /// closure typically closes over an `ISecretStore.GetSecret` read.
    /// Note: ADC-based deployments (`CredentialsJson = None`,
    /// `CredentialsJsonProvider = None`) are already rotation-transparent
    /// — the metadata server / workload-identity chain refreshes tokens
    /// itself — so the provider is only needed for the inline-JSON path.
    CredentialsJsonProvider: (unit -> string) option
}

module GoogleCloudStorageConfig =
    let defaults = {
        BucketName = ""
        CredentialsJson = None
        CredentialsJsonProvider = None
    }

// ─── Helpers ─────────────────────────────────────────────────────────

let private blobKey (toolupContainer: string) (blobName: string) = $"{toolupContainer}/{blobName}"

let private buildClientFromJson (credentialsJson: string option) : StorageClient =
    match credentialsJson with
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
    // Change-detection cache (Phase 2c). The `StorageClient` is (re)built
    // when the resolved service-account JSON differs from the cached one —
    // for the static (`CredentialsJsonProvider = None`) path that is
    // exactly once, reproducing the original build-once behaviour; for the
    // provider path it rebuilds only on key rotation, not per call.
    // Instance-level `mutable` guarded by `gate` — justified by the
    // caching intent.
    let gate = obj ()
    let mutable cachedJson: string = null
    let mutable cachedClient: StorageClient = null

    let client () =
        match config.CredentialsJsonProvider with
        | None ->
            // Static / ADC path: build once, never rebuild.
            lock gate (fun () ->
                if isNull cachedClient then
                    cachedClient <- buildClientFromJson config.CredentialsJson

                cachedClient)
        | Some provider ->
            let resolved = provider ()

            lock gate (fun () ->
                if isNull cachedClient || resolved <> cachedJson then
                    cachedJson <- resolved
                    cachedClient <- buildClientFromJson (Some resolved)

                cachedClient)

    // Eager build at construction — preserves the original fail-fast on a
    // malformed service-account JSON / credential-chain failure at startup.
    do client () |> ignore

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
                let! obj =
                    (client ()).UploadObjectAsync(config.BucketName, key, null, ms)
                    |> Async.AwaitTask

                return Ok $"gs://{obj.Bucket}/{obj.Name}"
            with ex ->
                return Error ex.Message
        }

        member _.Download(toolupContainer, blobName) = async {
            try
                use ms = new MemoryStream()
                let key = blobKey toolupContainer blobName
                let! _ = (client ()).DownloadObjectAsync(config.BucketName, key, ms) |> Async.AwaitTask
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
                do! (client ()).DeleteObjectAsync(config.BucketName, key) |> Async.AwaitTask
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
            let enumerable = (client ()).ListObjectsAsync(config.BucketName, fullPrefix)
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
                let! _ = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask
                return true
            with
            | :? GoogleApiException as ex when ex.HttpStatusCode = HttpStatusCode.NotFound -> return false
            | _ -> return false
        }

        member _.GetMetadata(toolupContainer, blobName) = async {
            try
                let key = blobKey toolupContainer blobName
                let! obj = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask

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
                // `fromEnv` reads the service-account JSON once. Deployments
                // that roll the key out of band construct via `create` with
                // `CredentialsJsonProvider = Some f` (Phase 2c) to survive
                // rotation without a restart; ADC deployments need neither.
                CredentialsJsonProvider = None
            }
        )