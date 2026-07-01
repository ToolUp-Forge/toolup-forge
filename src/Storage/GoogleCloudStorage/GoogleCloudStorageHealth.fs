module ToolUp.Storage.GoogleCloudStorageHealth

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

// ─── Phase 9k Google Cloud Storage health probe (Phase 2c: live-list) ─
//
// Mirrors `AzureBlobStorageHealth` — a live authenticated round-trip
// against the configured GCS backend. See the Azure probe header for
// the rationale.
//
// Phase 2c strengthened this from `Exists` to `List`. `Exists` swallows
// every exception and returns `false`, so a `403` from a rolled
// service-account key read back as "object absent" → Healthy — masking
// the credential drift this probe should catch. `List` propagates the
// auth failure as `Unhealthy` with the GCS SDK's status message. ADC-
// based deployments refresh tokens through the metadata server /
// workload-identity chain (rotation-transparent); a `CredentialsJsonProvider`
// deployment has this live list exercise the current key.

[<Literal>]
let private healthContainer = "_platform"

[<Literal>]
let private healthProbePrefix = "health/probe"

type GoogleCloudStorageHealthCheck(storage: IBlobStorage) =
    interface IHealthCheck with
        member _.Name = "blob_storage:gcs"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                // A live object list against the probe prefix. Returns `[]`
                // on a healthy empty prefix; throws (→ `Unhealthy`) on a
                // `403` / revoked-credential / bucket-drift failure.
                let! _ = storage.List(healthContainer, healthProbePrefix)
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

let create (storage: IBlobStorage) : IHealthCheck =
    GoogleCloudStorageHealthCheck(storage) :> IHealthCheck