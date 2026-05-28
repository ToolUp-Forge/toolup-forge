module ToolUp.Storage.AzureBlobStorageHealth

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

// ─── Phase 9k Azure Blob Storage health probe ────────────────────────
//
// Probes the configured Azure Blob Storage backend via `IBlobStorage.Exists`
// against a reserved probe blob in the `_platform` container — same
// pattern as the SDK's first-party `BlobStorageHealthCheck`. The first-
// party probe runs against the registered `IBlobStorage` regardless of
// backend; this Azure-specific probe ships alongside so deployments
// pinning Azure get a `blob_storage:azure` row in `/dev/inspect`
// independent of the generic `blob_storage` row, and a future revision
// can add Azure-only diagnostics (account-name verification, regional
// latency) without changing the abstraction-level probe.

[<Literal>]
let private healthContainer = "_platform"

[<Literal>]
let private healthProbeBlobName = "health/probe"

type AzureBlobStorageHealthCheck(storage: IBlobStorage) =
    interface IHealthCheck with
        member _.Name = "blob_storage:azure"
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
    AzureBlobStorageHealthCheck(storage) :> IHealthCheck