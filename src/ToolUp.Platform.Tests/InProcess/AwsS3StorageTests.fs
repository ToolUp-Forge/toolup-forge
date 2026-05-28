module ToolUp.Platform.Tests.InProcess.AwsS3StorageTests

open System
open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the `IBlobStorage` contract pack against a real S3 bucket (AWS,
// MinIO, Cloudflare R2, Backblaze B2, Linode — anything S3-compatible)
// when `TOOLUP_AWS_S3_BUCKET` is set. Factory spins up a GUID-suffixed
// key prefix per test so parallel runs don't collide on a shared
// bucket.
//
// NOTE: contract tests use `List` to assert container isolation, which
// requires the bucket to have no stray keys under `user-test-*` from
// other runs. Tests inside this pack always upload into a unique
// GUID-scoped container so the assertion holds per-test.

let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_AWS_S3_BUCKET" with
    | null
    | "" -> testList "AwsS3Storage" [ ptestCase "skipped — TOOLUP_AWS_S3_BUCKET not set" <| fun _ -> () ]
    | _ ->
        let factory () =
            match ToolUp.Storage.AwsS3Storage.fromEnv () with
            | Some s -> s
            | None -> failwith "AwsS3Storage.fromEnv returned None despite bucket env var being set"

        IBlobStorageContract.tests "AwsS3Storage" factory