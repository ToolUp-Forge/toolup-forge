module ToolUp.Platform.Tests.InProcess.HashiCorpVaultSecretStoreTests

open System
open Expecto
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the `ISecretStore` contract pack against a real HashiCorp Vault
// when `VAULT_ADDR` and `VAULT_TOKEN` are both set. Optionally
// `VAULT_NAMESPACE` selects an Enterprise namespace.
//
// Tests use GUID-suffixed scope IDs to avoid cross-test collisions.
// Vault's KV v2 metadata-delete (the call this companion uses for
// DeleteSecret) wipes every version of the secret, so re-runs with
// fresh scope IDs are clean. Leftover paths under
// `secret/metadata/toolup/team-{guid}/*` accumulate at the metadata
// layer; an operator running these against a long-lived Vault should
// run `vault kv metadata delete secret/toolup/...` periodically or
// drive the tests against a dedicated CI mount.
//
// Recommended posture: dedicate a test Vault mount for CI with a
// short-TTL token scoped to `secret/data/toolup/*` +
// `secret/metadata/toolup/*` (read + write + delete + list).
//
// When either of the required env vars is unset, the pack emits a
// single `pending` test — the CI signal shows "skipped" rather than
// "green" so a missing CI-side address / token is visible.

[<Tests>]
let tests =
    let addr = Environment.GetEnvironmentVariable "VAULT_ADDR"
    let token = Environment.GetEnvironmentVariable "VAULT_TOKEN"

    if String.IsNullOrWhiteSpace addr || String.IsNullOrWhiteSpace token then
        testList "HashiCorpVaultSecretStore" [ ptestCase "skipped — VAULT_ADDR or VAULT_TOKEN not set" <| fun _ -> () ]
    else
        let ns =
            match Environment.GetEnvironmentVariable "VAULT_NAMESPACE" with
            | null
            | "" -> None
            | s -> Some s

        let factory () =
            ToolUp.Secrets.HashiCorpVault.create {
                Address = addr
                Token = token
                Namespace = ns
                MountPath = "secret"
            }

        ISecretStoreContract.tests "HashiCorpVaultSecretStore" factory