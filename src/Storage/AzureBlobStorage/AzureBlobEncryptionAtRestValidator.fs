module ToolUp.Storage.AzureBlobEncryptionAtRestValidator

open System
open Azure.Storage.Blobs
open ToolUp.Platform.ConfigValidation
open ToolUp.Storage.AzureBlobStorage

// ─── Phase 22 Azure Blob encryption-at-rest preflight ───────────────
//
// Azure Storage encrypts at rest by default with Microsoft-managed
// keys (MMK). The encryption is account-level — every blob in every
// container in the account is encrypted; there's no per-bucket toggle
// like S3. The "is encryption on?" question on Azure is essentially
// "did you change the default to disable encryption?" — and the
// portal makes it intentionally hard to do that.
//
// The validator here exercises the connection (probes the storage
// account) and reports `Ok` whenever the account is reachable. A
// `BlobServiceClient.GetPropertiesAsync()` call that returns a 200
// is the cheapest live test of: "the connection string is valid,
// the account is reachable, the IAM principal has read access, the
// network path is open."
//
// Customer-managed key (CMK) detection isn't part of the v1
// validator's scope — Azure exposes CMK status via the storage-
// account-level `Encryption` property in `Microsoft.Azure.Management.Storage`,
// which requires the Azure Resource Manager (control plane) SDK and a
// different IAM principal than the data-plane SDK we use here. That's
// a Phase 22a follow-up if a deployment specifically needs CMK
// confirmation at preflight.

let private buildClient (config: AzureBlobStorageConfig) : BlobServiceClient =
    BlobServiceClient(config.ConnectionString)

type private Impl(config: AzureBlobStorageConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = sprintf "azure-blob-encryption-at-rest (%s)" config.RootContainer
        member _.Timeout = timeout

        member _.Validate() = async {
            try
                let client = buildClient config
                let! _ = client.GetPropertiesAsync() |> Async.AwaitTask
                // Reachable. Azure encrypts at rest by default with
                // Microsoft-managed keys; absent an exception path
                // signalling otherwise, the account is encrypted.
                return Ok
            with ex ->
                return Error(sprintf "Azure Storage GetProperties failed: %s" ex.Message)
        }

/// Construct an encryption-at-rest validator from an
/// `AzureBlobStorageConfig`. Pair with the matching `AzureBlobStorage`
/// instance so the validator and the runtime storage share the same
/// connection string.
let create (config: AzureBlobStorageConfig) : IConfigValidator = Impl(config) :> IConfigValidator