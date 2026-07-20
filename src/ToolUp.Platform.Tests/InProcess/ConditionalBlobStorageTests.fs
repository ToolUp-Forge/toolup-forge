module ToolUp.Platform.Tests.InProcess.ConditionalBlobStorageTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── Phase 600 — IConditionalBlobStorage contract ────────────────────
//
// Parametrised conformance pack for the ETag seam: any backend claiming
// `IConditionalBlobStorage` binds the same cases. Bound here to
// `LocalFileStorage` (content-hash etags, per-path CAS locks),
// `InMemoryBlobStorage` (the shared test double), a
// `ResilientBlobStorage(LocalFileStorage)` stack to prove decorator
// forwarding end-to-end, and — env-gated — the three cloud companions
// (`AwsS3Storage` / `AzureBlobStorage` / `GoogleCloudStorage`).

let private bytes (s: string) = Encoding.UTF8.GetBytes s

let private contractTests (name: string) (factory: unit -> IConditionalBlobStorage) =
    let container = "team-cas"

    let freshName () =
        "cas/" + Guid.NewGuid().ToString("N") + ".json"

    testList $"{name} — IConditionalBlobStorage contract" [

        testCaseAsync "IfAbsent creates; a second IfAbsent loses with the current etag disclosed"
        <| async {
            let store = factory ()
            let blob = freshName ()

            let! first = store.UploadWithETag(container, blob, bytes "v1", IfAbsent)

            let etag1 =
                match first with
                | Ok e -> e
                | Error e -> failtestf "first IfAbsent should create: %A" e

            let! second = store.UploadWithETag(container, blob, bytes "v2", IfAbsent)

            match second with
            | Error(ETagMismatch(Some current)) -> Expect.equal current etag1 "loser sees the winner's etag"
            | other -> failtestf "second IfAbsent must lose with the current etag, got %A" other
        }

        testCaseAsync "read-modify-write with a fresh etag succeeds and mints a new etag"
        <| async {
            let store = factory ()
            let blob = freshName ()
            let! _ = store.UploadWithETag(container, blob, bytes "v1", IfAbsent)

            let! downloaded = store.DownloadWithETag(container, blob)

            let content, etag =
                match downloaded with
                | Ok(c, e) -> c, e
                | Error e -> failtestf "download failed: %s" e

            Expect.equal (Encoding.UTF8.GetString content) "v1" "content round-trips"

            let! updated = store.UploadWithETag(container, blob, bytes "v2", IfMatch etag)

            match updated with
            | Ok newEtag ->
                Expect.notEqual newEtag etag "a successful CAS mints a new etag"
                let! after = store.DownloadWithETag(container, blob)

                match after with
                | Ok(c, e) ->
                    Expect.equal (Encoding.UTF8.GetString c) "v2" "the CAS write landed"
                    Expect.equal e newEtag "the read-back etag is the minted one"
                | Error e -> failtestf "re-download failed: %s" e
            | Error e -> failtestf "fresh-etag CAS should succeed: %A" e
        }

        testCaseAsync "a stale etag loses and leaves the stored blob untouched"
        <| async {
            let store = factory ()
            let blob = freshName ()
            let! first = store.UploadWithETag(container, blob, bytes "v1", IfAbsent)

            let staleEtag =
                match first with
                | Ok e -> e
                | Error e -> failtestf "setup failed: %A" e

            // Another writer advances the blob.
            let! second = store.UploadWithETag(container, blob, bytes "v2", IfMatch staleEtag)

            let currentEtag =
                match second with
                | Ok e -> e
                | Error e -> failtestf "setup CAS failed: %A" e

            // The stale writer now loses — lost-update prevention.
            let! stale = store.UploadWithETag(container, blob, bytes "v3-lost", IfMatch staleEtag)

            match stale with
            | Error(ETagMismatch(Some current)) -> Expect.equal current currentEtag "loser sees the live etag"
            | other -> failtestf "stale CAS must lose, got %A" other

            let! after = store.DownloadWithETag(container, blob)

            match after with
            | Ok(c, _) -> Expect.equal (Encoding.UTF8.GetString c) "v2" "the refused write did not land"
            | Error e -> failtestf "re-download failed: %s" e
        }

        testCaseAsync "IfMatch on an absent blob loses with currentETag = None"
        <| async {
            let store = factory ()
            let blob = freshName ()

            let! result = store.UploadWithETag(container, blob, bytes "v1", IfMatch "deadbeef")

            match result with
            | Error(ETagMismatch None) -> ()
            | other -> failtestf "IfMatch on absent must report ETagMismatch None, got %A" other
        }
    ]

let private localFactory () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-cas-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    LocalFileStorage.LocalFileStorage(root) :> obj :?> IConditionalBlobStorage

let private inMemoryFactory () =
    InMemoryBlobStorage.InMemoryBlobStorage() :> obj :?> IConditionalBlobStorage

let private resilientOverLocalFactory () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-cas-resilient-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let inner = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage

    ToolUp.Platform.ResilientBlobStorage.ResilientBlobStorage(
        inner,
        ToolUp.Platform.TransientFault.TransientFaultPolicy.identity
    )
    :> obj
    :?> IConditionalBlobStorage

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
            | Some store -> store :> obj :?> IConditionalBlobStorage
            | None -> failwith $"{name} store factory returned None despite {envVar} being set"

        contractTests $"{name} (live)" factory

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
        contractTests "LocalFileStorage" localFactory
        contractTests "InMemoryBlobStorage" inMemoryFactory
        contractTests "ResilientBlobStorage(LocalFileStorage)" resilientOverLocalFactory

        // Env-gated live cloud arms — skip clean without credentials.
        awsCloudArm
        azureCloudArm
        gcsCloudArm

        test "a non-conditional backend is honestly probeable" {
            // The seam's consumer pattern: type-test, fall back when absent.
            let bare =
                { new IBlobStorage with
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