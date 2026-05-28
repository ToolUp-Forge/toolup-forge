module ToolUp.Storage.AwsS3StorageHealth

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

// ─── Phase 9k AWS S3 health probe ────────────────────────────────────
//
// Mirrors `AzureBlobStorageHealth` — `IBlobStorage.Exists` round-trip
// against the configured S3 backend. See the Azure probe header for
// the rationale (the abstraction-level `blob_storage` probe also
// exercises this backend; the per-backend probe is for visibility +
// future backend-specific diagnostics).

[<Literal>]
let private healthContainer = "_platform"

[<Literal>]
let private healthProbeBlobName = "health/probe"

type AwsS3StorageHealthCheck(storage: IBlobStorage) =
    interface IHealthCheck with
        member _.Name = "blob_storage:aws-s3"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! _ = storage.Exists(healthContainer, healthProbeBlobName)
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

let create (storage: IBlobStorage) : IHealthCheck =
    AwsS3StorageHealthCheck(storage) :> IHealthCheck