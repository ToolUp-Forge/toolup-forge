module ToolUp.Platform.Tests.InProcess.GcpSecretManagerSecretStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the `ISecretStore` contract pack against a real GCP Secret
// Manager when `TOOLUP_GCP_PROJECT_ID` is set. Caller identity flows
// through Application Default Credentials — on a GCP-attached test
// runner that's the workload-identity-bound service account; off-GCP
// `GOOGLE_APPLICATION_CREDENTIALS` points at a service-account JSON
// key file.
//
// Tests use GUID-suffixed scope IDs to avoid cross-test collisions.
// Unlike Azure Key Vault (soft-delete) and AWS Secrets Manager
// (scheduled deletion), GCP Secret Manager removes secrets
// irreversibly on DeleteSecret — re-creating the same name
// immediately after delete is unconstrained, so the contract pack's
// per-test fresh scope IDs are belt-and-braces rather than required.
//
// Recommended posture: dedicate a test GCP project for CI with a
// service account scoped to the two roles the companion needs
// (`secretmanager.secretAccessor` + a custom role granting
// `secretmanager.secrets.create` / `.delete` / `.list` +
// `secretmanager.versions.add`). Operators running these against a
// production-shaped project will see leftover `toolup_team_{guid}_*`
// entries — Secret Manager has no built-in TTL, so a periodic sweep
// or one-shot cleanup is required for tidiness.
//
// When the env var is unset, the pack emits a single `pending` test
// — the CI signal shows "skipped" rather than "green" so a missing
// CI-side project ID is visible.

// ─── Service-account key-file parsing (always-on, no GCP access) ─────
//
// The companion's `Auth` module parses the service-account JSON at
// store construction (the `TokenProvider` is built eagerly in the
// store's constructor), so these tests pin the parse path without a
// project, credentials, or network. They exist because the original
// implementation deserialised into non-public CLIMutable records via
// plain `JsonSerializerOptions` — System.Text.Json's reflection
// serialiser only sees public property getters, so construction threw
// `NotSupportedException` on every off-GCP deployment that set
// `GOOGLE_APPLICATION_CREDENTIALS`, and the defect stayed latent
// because the contract pack above is env-gated.
//
// `testSequenced` because the cases mutate the process-global
// `GOOGLE_APPLICATION_CREDENTIALS` env var.

let private withCredentialsFile (json: string) (action: unit -> unit) =
    let path =
        Path.Combine(Path.GetTempPath(), sprintf "toolup-gcp-sa-%s.json" (Guid.NewGuid().ToString "N"))

    File.WriteAllText(path, json)
    let prior = Environment.GetEnvironmentVariable "GOOGLE_APPLICATION_CREDENTIALS"
    Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", path)

    try
        action ()
    finally
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", prior)
        File.Delete path

let private wellFormedKeyJson =
    """{
  "type": "service_account",
  "project_id": "parse-test",
  "client_email": "test@parse-test.iam.gserviceaccount.com",
  "private_key": "-----BEGIN PRIVATE KEY-----\nnot-parsed-at-construction\n-----END PRIVATE KEY-----\n",
  "token_uri": "https://oauth2.googleapis.com/token"
}"""

[<Tests>]
let serviceAccountParseTests =
    testSequenced
    <| testList "GcpSecretManagerSecretStore — service-account key parsing" [
        testCase "well-formed key file parses at store construction"
        <| fun _ ->
            withCredentialsFile wellFormedKeyJson (fun () ->
                ToolUp.Secrets.GcpSecretManager.create { ProjectId = "parse-test" } |> ignore)

        testCase "key file missing client_email fails with an actionable message"
        <| fun _ ->
            let json =
                """{ "type": "service_account", "private_key": "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n" }"""

            withCredentialsFile json (fun () ->
                let ex =
                    try
                        ToolUp.Secrets.GcpSecretManager.create { ProjectId = "parse-test" } |> ignore
                        failtest "expected ArgumentException for missing client_email"
                    with :? ArgumentException as e ->
                        e

                Expect.stringContains ex.Message "client_email" "error names the missing field")

        testCase "key file missing private_key fails with an actionable message"
        <| fun _ ->
            let json =
                """{ "type": "service_account", "client_email": "test@parse-test.iam.gserviceaccount.com" }"""

            withCredentialsFile json (fun () ->
                let ex =
                    try
                        ToolUp.Secrets.GcpSecretManager.create { ProjectId = "parse-test" } |> ignore
                        failtest "expected ArgumentException for missing private_key"
                    with :? ArgumentException as e ->
                        e

                Expect.stringContains ex.Message "private_key" "error names the missing field")
    ]

[<Tests>]
let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_GCP_PROJECT_ID" with
    | null
    | "" ->
        testList "GcpSecretManagerSecretStore" [ ptestCase "skipped — TOOLUP_GCP_PROJECT_ID not set" <| fun _ -> () ]
    | projectId ->
        let factory () =
            ToolUp.Secrets.GcpSecretManager.create { ProjectId = projectId }

        ISecretStoreContract.tests "GcpSecretManagerSecretStore" factory