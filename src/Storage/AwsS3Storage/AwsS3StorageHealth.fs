module ToolUp.Storage.AwsS3StorageHealth

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

// ─── Phase 9k AWS S3 health probe (Phase 2c: live-list) ──────────────
//
// Mirrors `AzureBlobStorageHealth` — a live authenticated round-trip
// against the configured S3 backend. See the Azure probe header for
// the rationale (the abstraction-level `blob_storage` probe also
// exercises this backend; the per-backend probe is for visibility +
// future backend-specific diagnostics).
//
// Phase 2c strengthened this from `Exists` to `List`. `Exists` swallows
// every exception and returns `false`, so a `403` from a revoked key /
// changed IAM policy read back as "blob absent" → Healthy — masking
// exactly the credential drift this probe should catch. `List` has no
// such swallow: an auth failure propagates and surfaces as `Unhealthy`
// with the SDK's status message. AWS credentials flow through the
// default chain (env / profile / IMDS / role), which the SDK refreshes
// itself, so key *rotation* is transparent here — but a revoked role,
// a policy change, or a bucket rename still fail the list, which is the
// point.

[<Literal>]
let private healthContainer = "_platform"

[<Literal>]
let private healthProbePrefix = "health/probe"

type AwsS3StorageHealthCheck(storage: IBlobStorage) =
    interface IHealthCheck with
        member _.Name = "blob_storage:aws-s3"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                // A live `ListObjectsV2` against the probe prefix. Returns
                // `[]` on a healthy empty prefix; throws (→ `Unhealthy`) on
                // a `403` / revoked-credential / bucket-drift failure.
                let! _ = storage.List(healthContainer, healthProbePrefix)
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

let create (storage: IBlobStorage) : IHealthCheck =
    AwsS3StorageHealthCheck(storage) :> IHealthCheck