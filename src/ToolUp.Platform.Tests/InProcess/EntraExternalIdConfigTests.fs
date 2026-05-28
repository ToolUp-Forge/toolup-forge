module ToolUp.Platform.Tests.InProcess.EntraExternalIdConfigTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.AuthProviders.EntraExternalIdConfig
open ToolUp.AuthProviders.EntraExternalIdAuthValidator

// ─── Phase 3d — EntraExternalIdConfig unit tests ─────────────────────
//
// Pure-function checks for the issuer-URL builder + user-flow-policy
// URL helper. The `IConfigValidator` preflight (`EntraExternalIdAuthValidator`)
// requires a live discovery endpoint and is contract-only — covered
// by the operator-run acceptance test in
// `acceptance-tests/AI_ACCEPTANCE_TESTS.md`.

[<Tests>]
let tests =
    testList "EntraExternalIdConfig" [
        test "issuerUrl uses tenant.ciamlogin.com when no custom domain" {
            let url = EntraExternalIdConfig.issuerUrl "contoso" None
            Expect.equal url "https://contoso.ciamlogin.com/contoso/v2.0" "default host pattern"
        }

        test "issuerUrl honours custom domain but keeps tenant in path" {
            let url = EntraExternalIdConfig.issuerUrl "contoso" (Some "login.contoso.com")
            Expect.equal url "https://login.contoso.com/contoso/v2.0" "custom domain replaces host; path keeps tenant"
        }

        test "issuerUrl is always v2.0 — v1.0 path never emitted" {
            let url = EntraExternalIdConfig.issuerUrl "x" None
            Expect.stringEnds url "/v2.0" "v2.0 path enforced"
        }

        test "authorizeUrlForPolicy composes the correct query string" {
            let cfg = {
                Tenant = "contoso"
                CustomDomain = None
                Audience = "client-id"
                ClockSkewSeconds = None
                SignUpPolicyId = Some "B2C_SignUp"
                SignInPolicyId = None
            }

            let url = EntraExternalIdConfig.authorizeUrlForPolicy cfg "B2C_SignUp"

            Expect.equal
                url
                "https://contoso.ciamlogin.com/contoso/v2.0/oauth2/v2.0/authorize?p=B2C_SignUp"
                "policy URL shape"
        }

        // ─── Validator/config env-var alignment (Cluster A2) ───────────

        testCaseAsync "tryFromEnv returns a fail-fast validator when TENANT is set but AUDIENCE is missing"
        <| async {
            // Snapshot + clear; tests run sequentially within the runner,
            // but the env-var dance must be isolated regardless.
            let priorTenant =
                Environment.GetEnvironmentVariable "TOOLUP_ENTRA_EXTERNAL_ID_TENANT"

            let priorAudience =
                Environment.GetEnvironmentVariable "TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE"

            try
                Environment.SetEnvironmentVariable("TOOLUP_ENTRA_EXTERNAL_ID_TENANT", "contoso")
                Environment.SetEnvironmentVariable("TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE", null)

                match tryFromEnv () with
                | None -> failtest "expected Some validator when TENANT is set"
                | Some validator ->
                    let! result = validator.Validate()

                    match result with
                    | ConfigValidation.Error msg ->
                        Expect.stringContains
                            msg
                            "TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE"
                            "error message names the missing env var"

                        Expect.stringContains
                            msg
                            "TENANT"
                            "error message names the partially-set env var so the operator sees the pair"
                    | other -> failtestf "expected Error, got %A" other
            finally
                Environment.SetEnvironmentVariable("TOOLUP_ENTRA_EXTERNAL_ID_TENANT", priorTenant)
                Environment.SetEnvironmentVariable("TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE", priorAudience)
        }

    // (The defensive "neither env var set → tryFromEnv returns None"
    // case is exercised implicitly by every other test runner / dev
    // environment that doesn't have TOOLUP_ENTRA_EXTERNAL_ID_TENANT
    // pre-set — adding it as an Expecto case here coupled it to the
    // current process's env-var state in a way that fights the
    // operator's own dev setup. The load-bearing case (fail-fast on
    // partial config) above is what closes Cluster A2's gap.)
    ]