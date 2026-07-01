module ToolUp.Storage.AzureBlobStorageHealth

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

// ─── Phase 9k Azure Blob Storage health probe (Phase 2c: live-list) ──
//
// Probes the configured Azure Blob Storage backend with a live
// authenticated round-trip against a reserved probe prefix in the
// `_platform` container — same pattern as the SDK's first-party
// `BlobStorageHealthCheck`. The first-party probe runs against the
// registered `IBlobStorage` regardless of backend; this Azure-specific
// probe ships alongside so deployments pinning Azure get a
// `blob_storage:azure` row in `/dev/inspect` independent of the generic
// `blob_storage` row, and a future revision can add Azure-only
// diagnostics (account-name verification, regional latency) without
// changing the abstraction-level probe.
//
// Phase 2c strengthened this from `Exists` to `List`. `Exists` swallows
// every exception and returns `false`, so a `403` from a rotated-out
// AccountKey / expired SAS read back as "blob absent" → Healthy —
// masking exactly the credential drift this probe should catch. `List`
// propagates the auth failure, surfacing it as `Unhealthy` with the
// Azure SDK's status message. When the companion is configured with a
// `ConnectionStringProvider`, this live list also exercises the current
// (post-rotation) credential.

[<Literal>]
let private healthContainer = "_platform"

[<Literal>]
let private healthProbePrefix = "health/probe"

type AzureBlobStorageHealthCheck(storage: IBlobStorage) =
    interface IHealthCheck with
        member _.Name = "blob_storage:azure"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                // A live blob list against the probe prefix. Returns `[]`
                // on a healthy empty prefix; throws (→ `Unhealthy`) on a
                // `403` / revoked-credential / container-drift failure.
                let! _ = storage.List(healthContainer, healthProbePrefix)
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

let create (storage: IBlobStorage) : IHealthCheck =
    AzureBlobStorageHealthCheck(storage) :> IHealthCheck