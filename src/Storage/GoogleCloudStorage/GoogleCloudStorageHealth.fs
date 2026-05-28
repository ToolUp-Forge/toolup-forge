module ToolUp.Storage.GoogleCloudStorageHealth

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

// ─── Phase 9k Google Cloud Storage health probe ──────────────────────
//
// Mirrors `AzureBlobStorageHealth` — `IBlobStorage.Exists` round-trip
// against the configured GCS backend. See the Azure probe header for
// the rationale.

[<Literal>]
let private healthContainer = "_platform"

[<Literal>]
let private healthProbeBlobName = "health/probe"

type GoogleCloudStorageHealthCheck(storage: IBlobStorage) =
    interface IHealthCheck with
        member _.Name = "blob_storage:gcs"
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
    GoogleCloudStorageHealthCheck(storage) :> IHealthCheck