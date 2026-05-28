module ToolUp.Platform.Tests.InProcess.LocalStorageEncryptionValidatorTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation

let private newTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-validator-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let tests =
    testList "LocalStorageEncryptionValidator" [
        testCaseAsync "Returns Warning when underlying storage is LocalFileStorage"
        <| async {
            let storage = LocalFileStorage.LocalFileStorage(newTempDir ()) :> IBlobStorage

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
            let inner = LocalFileStorage.LocalFileStorage(newTempDir ()) :> IBlobStorage

            let secrets =
                FileSecretStore.FileSecretStore(baseDir = newTempDir ()) :> Secrets.ISecretStore

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