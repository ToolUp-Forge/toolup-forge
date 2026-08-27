module ToolUp.Storage.GoogleCloudStorage

// `GoogleCredential.FromJson` is marked deprecated in favour of the
// newer `CredentialFactory` API. For single-call credential
// construction from inline JSON, `FromJson` remains the simplest
// surface and carries no security risk in this use case (caller
// supplies a trusted service-account JSON read from config).
#nowarn "44"

open System
open System.Globalization
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
    /// Optional GCS-compatible endpoint override — the mirror of
    /// `AwsS3StorageConfig.EndpointUrl`, and the seam an emulator needs.
    /// `None` (default) preserves today's behaviour exactly: the client is
    /// built by `StorageClient.Create`, which resolves the real
    /// `storage.googleapis.com` endpoint and honours the ADC chain.
    ///
    /// `Some uri` builds through `StorageClientBuilder` with `BaseUri` set,
    /// which is the only surface in this SDK that can be pointed elsewhere
    /// — `StorageClient.Create` does NOT consult `STORAGE_EMULATOR_HOST`
    /// (verified: with that variable set it still walks the ADC chain and
    /// throws "Your default credentials were not found"). When an override
    /// is set and no credentials are supplied, the builder is put into
    /// unauthenticated mode, because an emulator has no credentials to
    /// present and the ADC chain would otherwise fail before the first
    /// call. Supplying `CredentialsJson` alongside an override keeps the
    /// credential, for an authenticating GCS-compatible endpoint.
    EndpointUrl: string option
}

module GoogleCloudStorageConfig =
    let defaults = {
        BucketName = ""
        CredentialsJson = None
        CredentialsJsonProvider = None
        EndpointUrl = None
    }

// ─── Helpers ─────────────────────────────────────────────────────────

let private blobKey (toolupContainer: string) (blobName: string) = $"{toolupContainer}/{blobName}"

/// GCS SDK exceptions can surface at this companion's `with` handlers
/// wrapped in `AggregateException` — proven by the first armed
/// cloud-parity run (2026-08-27): `Delete`'s direct
/// `:? GoogleApiException` NotFound test sat dead, breaking delete
/// idempotency on missing blobs. Match through the wrapper: flatten and
/// take the single inner exception a one-Task await carries; a bare
/// exception passes through unchanged, so an unmatched case still
/// rethrows the original.
let private (|Unwrapped|) (ex: exn) =
    match ex with
    | :? AggregateException as aggregate ->
        match Seq.tryHead (aggregate.Flatten().InnerExceptions) with
        | Some inner -> inner
        | None -> ex
    | _ -> ex

let private buildClientFor (endpointUrl: string option) (credentialsJson: string option) : StorageClient =
    match endpointUrl with
    | None ->
        // Unchanged pre-existing path (GP 11) — no builder, no behaviour
        // difference for any deployment that does not set an override.
        match credentialsJson with
        | Some json ->
            let credential = GoogleCredential.FromJson json
            StorageClient.Create credential
        | None ->
            // Application Default Credentials — the SDK resolves env /
            // metadata server / workload identity itself.
            StorageClient.Create()
    | Some uri ->
        let builder = StorageClientBuilder(BaseUri = uri)

        match credentialsJson with
        | Some json -> builder.Credential <- GoogleCredential.FromJson json
        | None ->
            // No credential to present, and an emulator wants none — say so
            // explicitly rather than letting the ADC chain throw first.
            builder.UnauthenticatedAccess <- true

        builder.Build()


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
                    cachedClient <- buildClientFor config.EndpointUrl config.CredentialsJson

                cachedClient)
        | Some provider ->
            let resolved = provider ()

            lock gate (fun () ->
                if isNull cachedClient || resolved <> cachedJson then
                    cachedJson <- resolved
                    cachedClient <- buildClientFor config.EndpointUrl (Some resolved)

                cachedClient)

    // Eager build at construction — preserves the original fail-fast on a
    // malformed service-account JSON / credential-chain failure at startup.
    do client () |> ignore

    // Phase 600 follow-up — live-generation disclosure read for a
    // refused conditional write. `Ok None` = the object is absent (the
    // only case the seam may report `ETagMismatch None`); `Error` = the
    // disclosure read itself failed, surfaced as
    // `ConditionalWriteFailure` rather than a fabricated verdict.
    let currentGeneration (key: string) : Async<Result<string option, string>> = async {
        try
            let! obj = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask

            match Option.ofNullable obj.Generation with
            | Some g -> return Ok(Some(string g))
            | None -> return Error "GCS returned an object without a generation"
        with
        | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound -> return Ok None
        | Unwrapped ex -> return Error ex.Message
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
                use ms = new MemoryStream(content)
                let key = blobKey toolupContainer blobName
                // ContentType `null` lets GCS sniff from the object
                // name; we don't force octet-stream because the
                // platform doesn't track MIME per blob.
                let! obj =
                    (client ()).UploadObjectAsync(config.BucketName, key, null, ms)
                    |> Async.AwaitTask

                return Ok $"gs://{obj.Bucket}/{obj.Name}"
            with Unwrapped ex ->
                return Error ex.Message
        }

        member _.Download(toolupContainer, blobName) = async {
            try
                use ms = new MemoryStream()
                let key = blobKey toolupContainer blobName
                let! _ = (client ()).DownloadObjectAsync(config.BucketName, key, ms) |> Async.AwaitTask
                return Ok(ms.ToArray())
            with
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | Unwrapped ex -> return Error ex.Message
        }

        member _.DownloadRange(toolupContainer, blobName, offset, length) = async {
            if offset < 0L then
                return Error "DownloadRange: offset must be non-negative"
            elif length <= 0 then
                return Error "DownloadRange: length must be positive"
            else
                try
                    use ms = new MemoryStream()
                    let key = blobKey toolupContainer blobName
                    // Native range request; RangeHeaderValue end is
                    // inclusive. GCS clamps a range that overshoots EOF
                    // and 416s a range starting past it.
                    let options =
                        DownloadObjectOptions(Range = Http.Headers.RangeHeaderValue(offset, offset + int64 length - 1L))

                    let! _ =
                        (client ()).DownloadObjectAsync(config.BucketName, key, ms, options)
                        |> Async.AwaitTask

                    return Ok(ms.ToArray())
                with
                | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                    return Error $"Blob not found: {toolupContainer}/{blobName}"
                | Unwrapped(:? GoogleApiException as ex) when
                    ex.HttpStatusCode = HttpStatusCode.RequestedRangeNotSatisfiable
                    ->
                    // Past-EOF clamp per the interface contract.
                    return Ok Array.empty
                | Unwrapped ex -> return Error ex.Message
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
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound -> return Ok()
            | Unwrapped ex -> return Error ex.Message
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
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound -> return false
            | _ -> return false
        }

        member _.GetMetadata(toolupContainer, blobName) = async {
            try
                let key = blobKey toolupContainer blobName
                let! obj = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask

                let size = if obj.Size.HasValue then int64 obj.Size.Value else 0L

                // The Google.Apis `UpdatedDateTimeOffset` getter re-parses the
                // raw RFC3339 `updated` wire string with an exact format on
                // every read, so even the `.HasValue` probe throws
                // FormatException on variants a GCS-compatible emulator can
                // emit (fake-gcs-server sometimes trims fractional seconds).
                // Real GCS always emits the exact shape; a lenient re-parse of
                // the raw string degrades the timestamp, not the whole
                // GetMetadata call.
                let lastModified =
                    try
                        if obj.UpdatedDateTimeOffset.HasValue then
                            obj.UpdatedDateTimeOffset.Value.UtcDateTime
                        else
                            DateTime.UtcNow
                    with :? FormatException ->
                        match
                            DateTimeOffset.TryParse(
                                obj.UpdatedRaw,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal
                            )
                        with
                        | true, parsed -> parsed.UtcDateTime
                        | false, _ -> DateTime.UtcNow

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
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | Unwrapped ex -> return Error ex.Message
        }

    // ─── Phase 600 follow-up — conditional writes (the ETag seam) ────
    //
    // GCS optimistic concurrency rides object GENERATIONS, not HTTP
    // ETags (a GCS ETag is not a stable comparison token across
    // transcoding). The seam's etag token is therefore the decimal
    // generation number, surfaced as an opaque string — legal because
    // the contract makes tokens opaque per-provider: callers only
    // round-trip them, never parse them. `IfAbsent` maps to
    // `ifGenerationMatch=0`, the documented create-only precondition;
    // a refused precondition surfaces as HTTP 412, with the live
    // generation recovered by a follow-up metadata read (`None` only
    // when the object is absent). A token this backend never minted
    // (non-numeric) can match no generation and is refused without a
    // write round-trip.
    interface IConditionalBlobStorage with
        member _.DownloadWithETag(toolupContainer, blobName) = async {
            try
                let key = blobKey toolupContainer blobName
                // Two calls made coherent by generation pinning: read
                // the live generation, then download exactly that
                // generation — a concurrent overwrite between the two
                // calls cannot tear the (content, token) pair.
                let! obj = (client ()).GetObjectAsync(config.BucketName, key) |> Async.AwaitTask

                match Option.ofNullable obj.Generation with
                | None -> return Error $"GCS returned no generation for {toolupContainer}/{blobName}"
                | Some gen ->
                    use ms = new MemoryStream()
                    let options = DownloadObjectOptions(Generation = Nullable gen)

                    let! _ =
                        (client ()).DownloadObjectAsync(config.BucketName, key, ms, options)
                        |> Async.AwaitTask

                    return Ok(ms.ToArray(), string gen)
            with
            | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | Unwrapped ex -> return Error ex.Message
        }

        member _.UploadWithETag(toolupContainer, blobName, content, condition) = async {
            let key = blobKey toolupContainer blobName

            let requiredGeneration =
                match condition with
                | IfAbsent -> Some 0L
                | IfMatch token ->
                    match Int64.TryParse token with
                    | true, gen -> Some gen
                    | false, _ -> None

            match requiredGeneration with
            | None ->
                // A foreign token — refused as a mismatch, with the live
                // generation disclosed.
                match! currentGeneration key with
                | Ok current -> return Error(ETagMismatch current)
                | Error msg ->
                    return Error(ConditionalWriteFailure $"precondition refused; generation read failed: {msg}")
            | Some gen ->
                try
                    use ms = new MemoryStream(content)
                    let options = UploadObjectOptions(IfGenerationMatch = Nullable gen)

                    let! obj =
                        (client ()).UploadObjectAsync(config.BucketName, key, null, ms, options)
                        |> Async.AwaitTask

                    match Option.ofNullable obj.Generation with
                    | Some g -> return Ok(string g)
                    | None -> return Error(ConditionalWriteFailure "upload succeeded but GCS returned no generation")
                with
                | Unwrapped(:? GoogleApiException as ex) when ex.HttpStatusCode = HttpStatusCode.PreconditionFailed ->
                    match! currentGeneration key with
                    | Ok current -> return Error(ETagMismatch current)
                    | Error msg ->
                        return Error(ConditionalWriteFailure $"precondition refused; generation read failed: {msg}")
                | Unwrapped ex -> return Error(ConditionalWriteFailure ex.Message)
        }

    // ─── Phase 108 — time-bound direct-download URLs ─────────────────
    //
    // A V4-signed GET URL, computed locally from the service-account
    // key — no request is issued and no existence check is made (a
    // signed URL for an absent object is well-formed and 404s on fetch,
    // per the seam contract).
    //
    // GCS signing needs an RSA private key, which only a service-account
    // credential carries. A deployment on the Application Default
    // Credentials chain (`CredentialsJson = None`) may be running under
    // a user credential, a workload identity, or the metadata server —
    // none of which expose a signable key to this process — so that
    // shape reports `NotConfigured` and the caller falls back to
    // proxying, rather than throwing on a path the deployment never
    // opted into. Deployments that want signed originals supply the
    // service-account JSON explicitly (`CredentialsJson` /
    // `CredentialsJsonProvider`).
    interface ISignedUrlBlobStorage with
        member _.SignedUrl(toolupContainer, blobName, ttl) = async {
            let resolvedJson =
                match config.CredentialsJsonProvider with
                | Some provider -> Some(provider ())
                | None -> config.CredentialsJson

            match resolvedJson with
            | None ->
                return
                    Error(
                        SignedUrlRefusal.NotConfigured
                            "GCS URL signing needs an explicit service-account key; this client resolved Application Default Credentials, which expose no signable private key to the process"
                    )
            | Some json ->
                try
                    let signer = UrlSigner.FromCredential(GoogleCredential.FromJson json)
                    let key = blobKey toolupContainer blobName
                    let! url = signer.SignAsync(config.BucketName, key, ttl) |> Async.AwaitTask
                    return Ok url
                with Unwrapped ex ->
                    return Error(SignedUrlRefusal.SigningFailed ex.Message)
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

    match read ToolUp.Platform.ConfigKeys.Names.gcsBucket with
    | null
    | "" -> None
    | bucket ->
        let credsJson =
            match read ToolUp.Platform.ConfigKeys.Names.gcsCredentialsJson with
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
                // No env key for the endpoint override, deliberately. It
                // exists for emulator / GCS-compatible-endpoint use, which
                // is a `create` concern; a deployment that could set an env
                // var to redirect production storage is a footgun, not a
                // feature.
                EndpointUrl = None
            }
        )