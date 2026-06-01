module ToolUp.Platform.Tests.InProcess.GcpSecretManagerSecretStoreTests

open System
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