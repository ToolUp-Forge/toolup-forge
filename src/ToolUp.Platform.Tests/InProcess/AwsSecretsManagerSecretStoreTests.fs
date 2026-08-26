module ToolUp.Platform.Tests.InProcess.AwsSecretsManagerSecretStoreTests

open System
open Expecto
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the `ISecretStore` contract pack against a real AWS Secrets
// Manager when `TOOLUP_AWS_SECRETS_REGION` is set. Caller identity
// flows through the AWS SDK default credential chain — locally that's
// usually `~/.aws/credentials` + `AWS_PROFILE`; in CI a credentials
// triple (`AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` / optional
// `AWS_SESSION_TOKEN`) or an OIDC-federated role is the conventional
// path.
//
// Tests use GUID-suffixed scope IDs to avoid cross-test collisions.
// AWS Secrets Manager defaults to scheduled-deletion with a 7-30 day
// recovery window — a re-run with the same scope ID would hit
// `InvalidRequestException` until the window elapses. The contract
// pack's per-test fresh scope IDs sidestep this; operators running
// these against a production-shaped account will see leftover
// `toolup/team-{guid}/*` entries until the recovery window expires.
//
// Recommended posture: dedicate a test AWS account / role for CI with
// least-privilege resource ARN scoping (`secret:toolup/*`). When the
// env var is unset, the pack emits a single `pending` test — the CI
// signal shows "skipped" rather than "green" so a missing CI-side
// region var is visible.

[<Tests>]
let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_AWS_SECRETS_REGION" with
    | null
    | "" ->
        testList "AwsSecretsManagerSecretStore" [
            ptestCase "skipped — TOOLUP_AWS_SECRETS_REGION not set" <| fun _ -> ()
        ]
    | region ->
        let factory () =
            ToolUp.Secrets.AwsSecretsManager.create {
                Region = region
                // Unchanged behaviour: this env-gated binding targets a real
                // account and always did. The emulator route is the parity
                // pack's LocalStack leg, which passes an explicit endpoint.
                EndpointUrl = None
            }

        ISecretStoreContract.tests "AwsSecretsManagerSecretStore" factory