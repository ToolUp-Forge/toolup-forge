module ToolUp.Platform.Tests.InProcess.ConditionalBlobStorageTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── Phase 600 — IConditionalBlobStorage contract ────────────────────
//
// Bindings of the `IConditionalBlobStorageContract` pack (the cases moved
// into `Contracts/IConditionalBlobStorageContract.fs` at Phase 864, which
// also added the guarded read-modify-write cases). Bound here to
// `LocalFileStorage` (content-hash etags, per-path CAS locks),
// `InMemoryBlobStorage` (the shared test double), a
// `ResilientBlobStorage(LocalFileStorage)` stack to prove decorator
// forwarding end-to-end, an `EncryptedBlobStorage(InMemoryBlobStorage)`
// stack (Phase 864), and — env-gated — the three cloud companions
// (`AwsS3Storage` / `AzureBlobStorage` / `GoogleCloudStorage`).

let private localFactory () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-cas-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    LocalFileStorage.LocalFileStorage(root) :> IBlobStorage

let private inMemoryFactory () =
    InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

let private resilientOverLocalFactory () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-cas-resilient-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let inner = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage

    ToolUp.Platform.ResilientBlobStorage.ResilientBlobStorage(
        inner,
        ToolUp.Platform.TransientFault.TransientFaultPolicy.identity
    )
    :> IBlobStorage

// Phase 864 — the encryption-at-rest decorator over the hermetic double:
// etags are the inner store's, computed over the ciphertext envelope, so
// the pack proves CAS survives the decorator end to end.
let private encryptedOverInMemoryFactory () =
    let keys =
        ToolUp.Platform.SingleKeyResolver.create (ToolUp.Platform.Testing.Fakes.TestSecretStore())

    ToolUp.Platform.EncryptedBlobStorage.EncryptedBlobStorage(
        InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage,
        keys
    )
    :> IBlobStorage

// ─── Env-gated cloud arms (Phase 600 follow-up) ──────────────────────
//
// The same contract pack bound to the real cloud backends — S3
// conditional PUT (If-Match / If-None-Match), Azure ETag preconditions,
// GCS generation-match. Mirrors the AIProviders / cloud-storage
// env-gated live-test pattern: each arm activates only when its
// credential env var is set, and emits a single `pending` case
// otherwise so a missing credential shows as "skipped", never as a
// silent green. Env vars match the companions' `fromEnv` readers
// (see AwsS3StorageTests / AzureBlobStorageTests /
// GoogleCloudStorageTests).

let private envGatedCloudArm (name: string) (envVar: string) (mkStore: unit -> IBlobStorage option) =
    match Environment.GetEnvironmentVariable envVar with
    | null
    | "" -> testList $"{name} (live)" [ ptestCase $"skipped — {envVar} not set" <| fun _ -> () ]
    | _ ->
        let factory () =
            match mkStore () with
            | Some store -> store
            | None -> failwith $"{name} store factory returned None despite {envVar} being set"

        IConditionalBlobStorageContract.tests $"{name} (live)" factory

let private awsCloudArm =
    envGatedCloudArm "AwsS3Storage" "TOOLUP_AWS_S3_BUCKET" ToolUp.Storage.AwsS3Storage.fromEnv

let private azureCloudArm =
    envGatedCloudArm "AzureBlobStorage" "TOOLUP_AZURE_STORAGE_CONNECTION_STRING" (fun () ->
        ToolUp.Storage.AzureBlobStorage.fromEnv (Some "cas-contract-tests"))

let private gcsCloudArm =
    envGatedCloudArm "GoogleCloudStorage" "TOOLUP_GCS_BUCKET" ToolUp.Storage.GoogleCloudStorage.fromEnv

[<Tests>]
let tests =
    testList "Phase 600 — blob conditional writes (ETag seam)" [
        IConditionalBlobStorageContract.tests "LocalFileStorage" localFactory
        IConditionalBlobStorageContract.tests "InMemoryBlobStorage" inMemoryFactory
        IConditionalBlobStorageContract.tests "ResilientBlobStorage(LocalFileStorage)" resilientOverLocalFactory
        IConditionalBlobStorageContract.tests "EncryptedBlobStorage(InMemoryBlobStorage)" encryptedOverInMemoryFactory

        // Env-gated live cloud arms — skip clean without credentials.
        awsCloudArm
        azureCloudArm
        gcsCloudArm

        test "a non-conditional backend is honestly probeable" {
            // The seam's consumer pattern: type-test, fall back when absent.
            let bare =
                { new IBlobStorage with
                    // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
                    member _.CanComposeFrom = false

                    member _.ComposeFrom(_, _, _) =
                        ToolUp.Platform.BlobStorage.composeNotSupported "test double"

                    member _.Upload(_, _, _) = async { return Ok "" }
                    member _.Download(_, _) = async { return Error "nope" }
                    member _.DownloadRange(_, _, _, _) = async { return Error "nope" }
                    member _.Delete(_, _) = async { return Ok() }
                    member _.List(_, _) = async { return [] }
                    member _.Exists(_, _) = async { return false }
                    member _.GetMetadata(_, _) = async { return Error "nope" }
                    member _.Erase(_, _, _, _) = async { return Ok(Unchecked.defaultof<_>) }
                }

            match box bare with
            | :? IConditionalBlobStorage -> failtest "a bare IBlobStorage must not satisfy the capability probe"
            | _ -> ()
        }
    ]