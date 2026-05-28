module ToolUp.Platform.LocalStorageEncryptionValidator

open System
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation

// ─── Phase 22 — encryption-at-rest preflight (LocalFileStorage) ─────
//
// Surfaces a `Warning` when the registered `IBlobStorage` is
// `LocalFileStorage` — local disk has no app-level encryption-at-rest
// guarantee unless the deployment wraps it with `EncryptedBlobStorage`.
// Cloud blob-storage companions (AWS S3, Azure Blob, GCS) ship their
// own validators that check the bucket-level encryption setting.
//
// The validator does NOT fire when:
//   * The registered `IBlobStorage` is a cloud-companion impl — those
//     emit their own at-rest validators with provider-specific logic.
//   * The registered `IBlobStorage` is `EncryptedBlobStorage` (i.e.
//     the deployment wrapped LocalFileStorage with the Phase 22
//     decorator). In this case, `EncryptedBlobStorage` is the
//     registered singleton, not `LocalFileStorage`, so the cast check
//     below fails and the validator returns `Ok`.
//
// Warning rather than Error because LocalFileStorage is a legitimate
// configuration for development, single-user deployments running on a
// trusted machine, or air-gapped environments where physical access
// control supersedes file-system encryption. Operators who know
// they're production-bound see the warning and either wire a cloud
// companion or layer the EncryptedBlobStorage decorator.
//
// Auto-registered by `compose` whenever the resolved `IBlobStorage` is
// `LocalFileStorage`. Operators who want to silence this warning on a
// development deployment can either set
// `ServerConfig.SkipPreflight = true` (skips ALL validators — not
// recommended) or wrap LocalFileStorage with EncryptedBlobStorage.

type LocalFileStorageEncryptionAtRestValidator(storage: IBlobStorage, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "local-storage-encryption-at-rest"
        member _.Timeout = timeout

        member _.Validate() = async {
            // The cast is the entire validator. `LocalFileStorage` lives
            // in `ToolUp.Platform.LocalFileStorage`; downcasting from
            // `IBlobStorage` is the cleanest way to detect the impl
            // without leaking the concrete type into `IBlobStorage`'s
            // public surface.
            match storage with
            | :? LocalFileStorage.LocalFileStorage ->
                return
                    Warning
                        "LocalFileStorage does not provide application-level encryption at rest. \
                         For production deployments, either wire a cloud blob-storage companion \
                         (Azure Blob / AWS S3 / GCS) or wrap LocalFileStorage with EncryptedBlobStorage \
                         via ServerApp.withEncryptedBlobStorage. Acceptable for local development, \
                         single-user instances, or air-gapped environments where physical access \
                         control governs the data."
            | _ -> return Ok
        }