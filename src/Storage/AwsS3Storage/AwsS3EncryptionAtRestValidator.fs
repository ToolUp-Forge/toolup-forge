module ToolUp.Storage.AwsS3EncryptionAtRestValidator

open System
open Amazon.S3
open Amazon.S3.Model
open ToolUp.Platform.ConfigValidation
open ToolUp.Storage.AwsS3Storage

// ─── Phase 22 AWS S3 encryption-at-rest preflight ───────────────────
//
// Calls `GetBucketEncryption` on the configured bucket. Verdict:
//
// - SSE configured (any SSE algorithm — SSE-S3 / SSE-KMS / SSE-C) →
//   `Ok`. The bucket is encrypted at rest by AWS.
// - No SSE configured → `Warning`. The deployment may have intentionally
//   turned it off (rare but legitimate — some compliance scenarios
//   require app-level encryption only). Operators see the warning and
//   either turn SSE on at the bucket level or layer
//   `EncryptedBlobStorage` on top.
// - API call fails (missing bucket, IAM permission denied, transient
//   network) → `Error`. Aborts startup with a `ConfigPreflightFailedException`
//   so misconfigured deployments fail fast at boot rather than on first
//   blob op.
//
// The validator builds its own `IAmazonS3` client from the supplied
// config — same shape `AwsS3Storage` uses internally. This keeps the
// validator construction symmetric with the storage construction
// (deployment passes the same config to both).

// Returns the concrete `AmazonS3Client`, not `IAmazonS3`. Under AWS SDK
// v4 the `IAmazonS3` interface carries static abstract members, so it
// cannot be used as a generic type argument — and `use client = ...`
// inside the `async { }` below binds through `AsyncBuilder.Using<'T>`,
// which would otherwise instantiate `'T = IAmazonS3` (FS3868).
let private buildClient (config: AwsS3StorageConfig) : AmazonS3Client =
    let clientConfig = AmazonS3Config()
    clientConfig.RegionEndpoint <- Amazon.RegionEndpoint.GetBySystemName config.Region

    match config.EndpointUrl with
    | Some url ->
        clientConfig.ServiceURL <- url
        clientConfig.ForcePathStyle <- true
    | None -> ()

    new AmazonS3Client(clientConfig)

type private Impl(config: AwsS3StorageConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = sprintf "aws-s3-encryption-at-rest (%s)" config.BucketName
        member _.Timeout = timeout

        member _.Validate() = async {
            use client = buildClient config

            try
                let request = GetBucketEncryptionRequest()
                request.BucketName <- config.BucketName
                let! response = client.GetBucketEncryptionAsync request |> Async.AwaitTask

                // AWS SDK v4 leaves response collections null when unset, so
                // walk the config → rules chain defensively rather than
                // dereferencing straight through.
                let ruleCount =
                    response.ServerSideEncryptionConfiguration
                    |> Option.ofObj
                    |> Option.bind (fun c -> Option.ofObj c.ServerSideEncryptionRules)
                    |> Option.map _.Count
                    |> Option.defaultValue 0

                if ruleCount > 0 then
                    return Ok
                else
                    return
                        Warning
                            "S3 bucket has no server-side encryption configuration. \
                             Either enable bucket encryption (SSE-S3, SSE-KMS, or SSE-C) \
                             at the bucket level, or wrap the IBlobStorage with \
                             EncryptedBlobStorage via ServerApp.withEncryptedBlobStorage."
            with
            | :? AmazonS3Exception as ex when ex.ErrorCode = "ServerSideEncryptionConfigurationNotFoundError" ->
                return
                    Warning
                        "S3 bucket has no server-side encryption configuration. \
                         Enable bucket encryption at the bucket level or use \
                         EncryptedBlobStorage."
            | :? AmazonS3Exception as ex when ex.ErrorCode = "NoSuchBucket" ->
                return Error(sprintf "S3 bucket %s does not exist" config.BucketName)
            | :? AmazonS3Exception as ex when ex.ErrorCode = "AccessDenied" ->
                return
                    Error(
                        sprintf
                            "S3 GetBucketEncryption denied on %s — the IAM principal needs s3:GetEncryptionConfiguration"
                            config.BucketName
                    )
            | ex -> return Error(sprintf "S3 GetBucketEncryption failed: %s" ex.Message)
        }

/// Construct an encryption-at-rest validator from an
/// `AwsS3StorageConfig`. Pair with the matching `AwsS3Storage`
/// instance so the validator and the runtime storage share the same
/// bucket / region / endpoint settings.
let create (config: AwsS3StorageConfig) : IConfigValidator = Impl(config) :> IConfigValidator