module ToolUp.Storage.AwsS3Storage

open System
open System.IO
open System.Net
open Amazon.S3
open Amazon.S3.Model
open ToolUp.Platform.BlobStorage

// ─── Configuration ───────────────────────────────────────────────────

/// Configuration for `AwsS3Storage`. Takes the S3 bucket name and the
/// AWS region; credentials flow through the AWS SDK's default resolution
/// chain (env vars `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY`, shared
/// credentials file `~/.aws/credentials`, EC2 instance profile, IAM
/// role for ECS tasks, etc.). Keeps this file free of credential-
/// handling logic and matches how most AWS shops deploy.
///
/// Like Azure, ToolUp's logical containers map to S3-key prefixes
/// inside a single bucket — S3 bucket names are globally unique across
/// all AWS accounts, so creating one-per-scope is impractical; using
/// prefixes keeps every deployment's data in one bucket and works
/// identically on S3-compatible stores (MinIO, Cloudflare R2,
/// Backblaze B2, Linode Object Storage).
type AwsS3StorageConfig = {
    /// Existing S3 bucket to store ToolUp data in. The class does NOT
    /// create the bucket — provisioning / ACL are the deployment's
    /// responsibility, and IAM permissions usually restrict the app's
    /// role to get/put/delete/list on a specific bucket.
    BucketName: string
    /// AWS region in SDK string form ("us-east-1", "eu-west-2", ...).
    Region: string
    /// Optional S3-compatible endpoint override (MinIO, R2, B2).
    /// `None` = use the default AWS endpoint for the region.
    EndpointUrl: string option
}

module AwsS3StorageConfig =
    let defaults = {
        BucketName = ""
        Region = "us-east-1"
        EndpointUrl = None
    }

// ─── Helpers ─────────────────────────────────────────────────────────

let private blobKey (toolupContainer: string) (blobName: string) = $"{toolupContainer}/{blobName}"

// Returns the concrete `AmazonS3Client`. Under AWS SDK v4 `IAmazonS3`
// carries static abstract members, so naming it as an ordinary type
// raises FS3536 (IWSAM-as-type); the client is only ever used through
// its instance methods, so the concrete type is sufficient.
let private buildClient (config: AwsS3StorageConfig) : AmazonS3Client =
    let clientConfig = AmazonS3Config()
    clientConfig.RegionEndpoint <- Amazon.RegionEndpoint.GetBySystemName config.Region

    match config.EndpointUrl with
    | Some url ->
        clientConfig.ServiceURL <- url
        // S3-compatible stores typically require path-style addressing —
        // `{host}/{bucket}/{key}` rather than `{bucket}.{host}/{key}`.
        clientConfig.ForcePathStyle <- true
    | None -> ()

    new AmazonS3Client(clientConfig)

// ─── IBlobStorage implementation ─────────────────────────────────────

/// S3 implementation of `IBlobStorage`. Each deployment uses one S3
/// bucket with ToolUp logical containers encoded as key prefixes.
/// Thread-safe — `IAmazonS3` / `AmazonS3Client` is documented reusable.
type AwsS3Storage(config: AwsS3StorageConfig) =
    let client = buildClient config

    // Phase 600 follow-up — live-etag disclosure read for a refused
    // conditional write. `Ok None` = the object is absent (the only
    // case the seam may report `ETagMismatch None`); `Error` = the
    // disclosure read itself failed, surfaced as
    // `ConditionalWriteFailure` rather than a fabricated verdict.
    let currentETag (key: string) : Async<Result<string option, string>> = async {
        try
            let req = GetObjectMetadataRequest()
            req.BucketName <- config.BucketName
            req.Key <- key
            let! response = client.GetObjectMetadataAsync req |> Async.AwaitTask
            return Ok(Some response.ETag)
        with
        | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound -> return Ok None
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
                let req = PutObjectRequest()
                req.BucketName <- config.BucketName
                req.Key <- blobKey toolupContainer blobName
                req.InputStream <- new MemoryStream(content)
                let! _ = client.PutObjectAsync req |> Async.AwaitTask
                return Ok $"s3://{config.BucketName}/{req.Key}"
            with ex ->
                return Error ex.Message
        }

        member _.Download(toolupContainer, blobName) = async {
            try
                let req = GetObjectRequest()
                req.BucketName <- config.BucketName
                req.Key <- blobKey toolupContainer blobName
                use! response = client.GetObjectAsync req |> Async.AwaitTask
                use ms = new MemoryStream()
                do! response.ResponseStream.CopyToAsync ms |> Async.AwaitTask
                return Ok(ms.ToArray())
            with
            | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound ->
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
                    let req = GetObjectRequest()
                    req.BucketName <- config.BucketName
                    req.Key <- blobKey toolupContainer blobName
                    // Native range request; ByteRange end is inclusive. S3
                    // clamps a range that overshoots EOF and 416s a range
                    // starting past it.
                    req.ByteRange <- ByteRange(offset, offset + int64 length - 1L)
                    use! response = client.GetObjectAsync req |> Async.AwaitTask
                    use ms = new MemoryStream()
                    do! response.ResponseStream.CopyToAsync ms |> Async.AwaitTask
                    return Ok(ms.ToArray())
                with
                | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound ->
                    return Error $"Blob not found: {toolupContainer}/{blobName}"
                | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable ->
                    // Past-EOF clamp per the interface contract.
                    return Ok Array.empty
                | ex -> return Error ex.Message
        }

        member _.Delete(toolupContainer, blobName) = async {
            // S3 DeleteObject is idempotent — succeeds whether the
            // key exists or not. Matches the contract's Delete
            // semantic without any special-case handling.
            try
                let req = DeleteObjectRequest()
                req.BucketName <- config.BucketName
                req.Key <- blobKey toolupContainer blobName
                let! _ = client.DeleteObjectAsync req |> Async.AwaitTask
                return Ok()
            with ex ->
                return Error ex.Message
        }

        member _.List(toolupContainer, prefix) = async {
            let fullPrefix = blobKey toolupContainer prefix
            let stripLen = (toolupContainer + "/").Length
            let results = System.Collections.Generic.List<string>()

            let mutable continuationToken: string = null
            let mutable keepGoing = true

            while keepGoing do
                let req = ListObjectsV2Request()
                req.BucketName <- config.BucketName
                req.Prefix <- fullPrefix

                if continuationToken <> null then
                    req.ContinuationToken <- continuationToken

                let! response = client.ListObjectsV2Async req |> Async.AwaitTask

                // AWS SDK v4 returns null (not an empty list) for response
                // collections when the page has no objects.
                if not (isNull response.S3Objects) then
                    for obj in response.S3Objects do
                        results.Add(obj.Key.Substring stripLen)

                if response.IsTruncated.GetValueOrDefault() then
                    continuationToken <- response.NextContinuationToken
                else
                    keepGoing <- false

            return results |> List.ofSeq
        }

        member _.Exists(toolupContainer, blobName) = async {
            try
                let req = GetObjectMetadataRequest()
                req.BucketName <- config.BucketName
                req.Key <- blobKey toolupContainer blobName
                let! _ = client.GetObjectMetadataAsync req |> Async.AwaitTask
                return true
            with
            | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound -> return false
            | _ -> return false
        }

        member _.GetMetadata(toolupContainer, blobName) = async {
            try
                let req = GetObjectMetadataRequest()
                req.BucketName <- config.BucketName
                req.Key <- blobKey toolupContainer blobName
                let! response = client.GetObjectMetadataAsync req |> Async.AwaitTask

                let contentType =
                    if String.IsNullOrEmpty response.Headers.ContentType then
                        None
                    else
                        Some response.Headers.ContentType

                return
                    Ok {
                        Size = response.Headers.ContentLength
                        LastModified = response.LastModified.GetValueOrDefault().ToUniversalTime()
                        ContentType = contentType
                    }
            with
            | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | ex -> return Error ex.Message
        }

    // ─── Phase 600 follow-up — conditional writes (the ETag seam) ────
    //
    // S3 conditional PUT (GA since late 2024): `If-Match` /
    // `If-None-Match: *` headers on PutObject, carried by AWSSDK.S3
    // 4.x as `PutObjectRequest.IfMatch` / `IfNoneMatch`. The etag
    // token is the native S3 ETag, opaque per the seam contract —
    // callers only round-trip it, never parse it. A refused
    // precondition surfaces as 412 PreconditionFailed; concurrent
    // conditional writers on the same key can instead observe 409
    // ConditionalRequestConflict — both map to `ETagMismatch`, with
    // the live etag recovered by a follow-up HEAD. An `If-Match` PUT
    // against an absent key 404s, which is the seam's
    // `ETagMismatch None`.
    //
    // Caveat: S3-compatible stores (MinIO / R2 / B2) vary in
    // conditional-write support — the env-gated contract arm in
    // `ConditionalBlobStorageTests` is the conformance check per
    // endpoint.
    interface IConditionalBlobStorage with
        member _.DownloadWithETag(toolupContainer, blobName) = async {
            try
                let req = GetObjectRequest()
                req.BucketName <- config.BucketName
                req.Key <- blobKey toolupContainer blobName
                use! response = client.GetObjectAsync req |> Async.AwaitTask
                use ms = new MemoryStream()
                do! response.ResponseStream.CopyToAsync ms |> Async.AwaitTask
                return Ok(ms.ToArray(), response.ETag)
            with
            | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound ->
                return Error $"Blob not found: {toolupContainer}/{blobName}"
            | ex -> return Error ex.Message
        }

        member _.UploadWithETag(toolupContainer, blobName, content, condition) = async {
            let key = blobKey toolupContainer blobName

            try
                let req = PutObjectRequest()
                req.BucketName <- config.BucketName
                req.Key <- key
                req.InputStream <- new MemoryStream(content)

                match condition with
                | IfMatch etag -> req.IfMatch <- etag
                | IfAbsent -> req.IfNoneMatch <- "*"

                let! response = client.PutObjectAsync req |> Async.AwaitTask
                return Ok response.ETag
            with
            | :? AmazonS3Exception as ex when
                ex.StatusCode = HttpStatusCode.PreconditionFailed
                || ex.StatusCode = HttpStatusCode.Conflict
                ->
                match! currentETag key with
                | Ok current -> return Error(ETagMismatch current)
                | Error msg ->
                    return Error(ConditionalWriteFailure $"precondition refused; etag disclosure read failed: {msg}")
            | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound ->
                // `If-Match` against an absent key — the blob the caller
                // expected is gone.
                return Error(ETagMismatch None)
            | ex -> return Error(ConditionalWriteFailure ex.Message)
        }

    // ─── Phase 108 — time-bound direct-download URLs ─────────────────
    //
    // A presigned GET, computed locally from the credentials the client
    // already resolved — no request is issued, and no existence check
    // is made (a presigned URL for an absent key is well-formed and
    // 404s on fetch, which is the contract the seam documents).
    //
    // Works unchanged against S3-compatible stores (MinIO / R2 / B2):
    // the endpoint + path-style settings the client was built with are
    // reflected in the signed URL. Deployments running on an IAM role
    // whose session credentials expire before the requested TTL get a
    // URL that stops working at whichever bound comes first — which is
    // AWS's semantics, not something this seam can widen.
    interface ISignedUrlBlobStorage with
        member _.SignedUrl(toolupContainer, blobName, ttl) = async {
            try
                let req = GetPreSignedUrlRequest()
                req.BucketName <- config.BucketName
                req.Key <- blobKey toolupContainer blobName
                req.Verb <- HttpVerb.GET
                req.Expires <- DateTime.UtcNow.Add ttl
                let! url = client.GetPreSignedURLAsync req |> Async.AwaitTask
                return Ok url
            with ex ->
                return Error(SignedUrlRefusal.SigningFailed ex.Message)
        }

// ─── Public entry points ─────────────────────────────────────────────

let create (config: AwsS3StorageConfig) : IBlobStorage = AwsS3Storage config :> IBlobStorage

/// Read bucket + region from environment variables and construct an
/// `AwsS3Storage`. Credentials flow through the AWS SDK's default
/// chain — usually set via `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY`
/// or an IAM role; the deployment chooses.
///
/// Required: `TOOLUP_AWS_S3_BUCKET`.
/// Optional: `TOOLUP_AWS_S3_REGION` (defaults to `us-east-1`),
///           `TOOLUP_AWS_S3_ENDPOINT` (for MinIO / R2 / B2).
///
/// Returns `None` when the bucket env var is unset — deployment falls
/// back to whatever `compose` has wired.
let fromEnv () : IBlobStorage option =
    let read name = Environment.GetEnvironmentVariable name

    match read "TOOLUP_AWS_S3_BUCKET" with
    | null
    | "" -> None
    | bucket ->
        let region =
            match read "TOOLUP_AWS_S3_REGION" with
            | null
            | "" -> "us-east-1"
            | r -> r

        let endpoint =
            match read "TOOLUP_AWS_S3_ENDPOINT" with
            | null
            | "" -> None
            | e -> Some e

        Some(
            create {
                BucketName = bucket
                Region = region
                EndpointUrl = endpoint
            }
        )