module ToolUp.Platform.Tests.InProcess.GoogleCloudStorageTests

open System
open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the `IBlobStorage` contract pack against a real GCS bucket when
// `TOOLUP_GCS_BUCKET` is set. Credentials resolve via the GCS SDK's
// Application Default Credentials chain (env `GOOGLE_APPLICATION_CREDENTIALS`,
// gcloud user login, GCE metadata server, GKE workload identity) — or
// via `TOOLUP_GCS_CREDENTIALS_JSON` for inline JSON.
//
// Contract tests use GUID-suffixed scope identifiers internally so
// a shared bucket across runs doesn't leak keys between tests.

let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_GCS_BUCKET" with
    | null
    | "" -> testList "GoogleCloudStorage" [ ptestCase "skipped — TOOLUP_GCS_BUCKET not set" <| fun _ -> () ]
    | _ ->
        let factory () =
            match ToolUp.Storage.GoogleCloudStorage.fromEnv () with
            | Some s -> s
            | None -> failwith "GoogleCloudStorage.fromEnv returned None despite bucket env var being set"

        IBlobStorageContract.tests "GoogleCloudStorage" factory