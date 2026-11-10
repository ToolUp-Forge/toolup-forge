module ToolUp.Platform.Tests.InProcess.LocalStorageEncryptionValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Testing

let private newTempDir () = TempDir.create "validator-test"

let tests =
    testList "LocalStorageEncryptionValidator" [
        testCaseAsync "Returns Warning when underlying storage is LocalFileStorage"
        <| async {
            use dir = newTempDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir.Path) :> IBlobStorage

            let validator =
                LocalStorageEncryptionValidator.LocalFileStorageEncryptionAtRestValidator(storage) :> IConfigValidator

            let! result = validator.Validate()

            match result with
            | Warning msg ->
                Expect.stringContains msg "LocalFileStorage" "warning message mentions LocalFileStorage"
                Expect.stringContains msg "encryption" "warning message mentions encryption"
            | Ok -> failtest "expected Warning, got Ok"
            | Error e -> failtestf "expected Warning, got Error %s" e
        }

        testCaseAsync "Returns Ok when storage is wrapped (e.g. EncryptedBlobStorage)"
        <| async {
            // Build a storage instance that is NOT LocalFileStorage by
            // wrapping it in EncryptedBlobStorage. The validator should
            // see the wrapper, not the inner LocalFileStorage, and
            // return Ok.
            use innerDir = newTempDir ()
            use secretsDir = newTempDir ()
            let inner = LocalFileStorage.LocalFileStorage(innerDir.Path) :> IBlobStorage

            let secrets =
                FileSecretStore.FileSecretStore(baseDir = secretsDir.Path) :> Secrets.ISecretStore

            let resolver = SingleKeyResolver.create secrets

            let wrapped =
                EncryptedBlobStorage.EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let validator =
                LocalStorageEncryptionValidator.LocalFileStorageEncryptionAtRestValidator(wrapped) :> IConfigValidator

            let! result = validator.Validate()

            match result with
            | Ok -> ()
            | Warning msg -> failtestf "expected Ok for wrapped storage, got Warning: %s" msg
            | Error e -> failtestf "expected Ok for wrapped storage, got Error: %s" e
        }
    ]