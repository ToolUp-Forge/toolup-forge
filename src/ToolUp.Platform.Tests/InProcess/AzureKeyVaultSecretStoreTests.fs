module ToolUp.Platform.Tests.InProcess.AzureKeyVaultSecretStoreTests

open System
open Expecto
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the `ISecretStore` contract pack against a real Azure Key Vault
// when `TOOLUP_AZURE_KEY_VAULT_URL` is set. Caller identity comes from
// `DefaultAzureCredential` — locally that's `az login`; in CI a
// service-principal env-var triple (`AZURE_CLIENT_ID` / `_SECRET` /
// `_TENANT_ID`) is the conventional path.
//
// Tests use GUID-suffixed scope IDs to avoid cross-test collisions.
// Note that Azure Key Vault soft-deletes secrets on Delete with a
// 90-day retention window — a re-run with the same scope ID would
// hit `Conflict` until purge. The contract pack's per-test fresh
// scope IDs sidestep this without requiring `secrets/purge` permission
// on the test vault.
//
// Recommended posture: dedicate a test vault for CI with a short
// retention window (the minimum is 7 days). Operators running this
// against a production vault will see leftover `toolup-team-{guid}-*`
// secrets accumulate — manual purge or a scheduled cleanup task is
// required for tidiness.
//
// When the env var is unset, the pack emits a single `pending` test
// — the CI signal shows "skipped" rather than "green" so a missing
// CI-side vault URL is visible.

[<Tests>]
let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_AZURE_KEY_VAULT_URL" with
    | null
    | "" ->
        testList "AzureKeyVaultSecretStore" [ ptestCase "skipped — TOOLUP_AZURE_KEY_VAULT_URL not set" <| fun _ -> () ]
    | url ->
        let factory () =
            ToolUp.Secrets.AzureKeyVault.create { VaultUrl = url }

        ISecretStoreContract.tests "AzureKeyVaultSecretStore" factory