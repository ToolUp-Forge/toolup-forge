module ToolUp.Platform.Tests.InProcess.OidcAudienceBindingValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// `OidcAudienceBindingValidator` reads TOOLUP_AUTH_MODE +
// TOOLUP_OIDC_AUDIENCE from the environment (mirrors
// OidcConfigCompletenessValidator). Env vars are process-global, so the
// list is `testSequenced` and every case saves/restores the two vars.

let private cfg (surfaces: SurfaceProfile list) (escapeHatch: bool) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        AcceptUnboundAudienceWhenAuthRequired = escapeHatch
}

/// Set TOOLUP_AUTH_MODE / TOOLUP_OIDC_AUDIENCE (None = unset), run the
/// validator, restore the prior env values unconditionally.
let private validateWithEnv
    (authMode: string option)
    (audience: string option)
    (config: ServerConfig)
    : ValidationResult =
    let priorMode = Environment.GetEnvironmentVariable "TOOLUP_AUTH_MODE"
    let priorAud = Environment.GetEnvironmentVariable "TOOLUP_OIDC_AUDIENCE"

    let set name value =
        Environment.SetEnvironmentVariable(name, Option.toObj value)

    try
        set "TOOLUP_AUTH_MODE" authMode
        set "TOOLUP_OIDC_AUDIENCE" audience

        let v =
            OidcAudienceBindingValidator.OidcAudienceBindingValidator(config) :> IConfigValidator

        v.Validate() |> Async.RunSynchronously
    finally
        Environment.SetEnvironmentVariable("TOOLUP_AUTH_MODE", priorMode)
        Environment.SetEnvironmentVariable("TOOLUP_OIDC_AUDIENCE", priorAud)

[<Tests>]
let tests =
    testSequenced
    <| testList "OIDC audience binding validator" [

        test "Anonymous mode + oidc + no audience → Ok (anonymous is exempt)" {
            let result = validateWithEnv (Some "oidc") None (cfg Surfaces.anonymous false)
            Expect.equal result Ok "no real identity in Anonymous mode — nothing to confuse"
        }

        test "Individual mode + auth mode unset → Ok (only fires for oidc)" {
            let result = validateWithEnv None None (cfg Surfaces.individual false)
            Expect.equal result Ok "non-oidc auth modes are out of scope for this validator"
        }

        test "Individual mode + oidc + audience set → Ok" {
            let result =
                validateWithEnv (Some "oidc") (Some "my-app") (cfg Surfaces.individual false)

            Expect.equal result Ok "a bound audience is the production path"
        }

        test "Individual mode + oidc + audience unset + no escape hatch → Error" {
            let result = validateWithEnv (Some "oidc") None (cfg Surfaces.individual false)

            match result with
            | Error msg ->
                Expect.stringContains msg "Individual" "names the offending mode"
                Expect.stringContains msg "TOOLUP_OIDC_AUDIENCE" "points at the canonical fix"
                Expect.stringContains msg "token reuse" "explains the consequence"

                Expect.stringContains msg "AcceptUnboundAudienceWhenAuthRequired" "documents the escape hatch"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Team mode + oidc + audience unset → Error" {
            let result = validateWithEnv (Some "oidc") None (cfg Surfaces.team false)

            match result with
            | Error msg -> Expect.stringContains msg "Team" "names the offending mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "MultiTeam mode + oidc + audience unset → Error" {
            let result = validateWithEnv (Some "oidc") None (cfg Surfaces.multiTeam false)

            match result with
            | Error msg -> Expect.stringContains msg "MultiTeam" "names the offending mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "AuthenticatedEphemeral mode + oidc + audience unset → Error" {
            let result = validateWithEnv (Some "oidc") None (cfg Surfaces.trial false)

            match result with
            | Error msg -> Expect.stringContains msg "AuthenticatedEphemeral" "names the offending mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Individual mode + oidc + audience unset + escape hatch → Ok" {
            let result = validateWithEnv (Some "oidc") None (cfg Surfaces.individual true)
            Expect.equal result Ok "explicit opt-in passes"
        }

        test "Case-insensitive auth mode (OIDC) still fires" {
            let result = validateWithEnv (Some "OIDC") None (cfg Surfaces.individual false)

            match result with
            | Error _ -> ()
            | other -> failtestf "expected Error for upper-case OIDC, got %A" other
        }

        test "Validator metadata is well-formed" {
            let v =
                OidcAudienceBindingValidator.OidcAudienceBindingValidator(cfg Surfaces.individual false)
                :> IConfigValidator

            Expect.equal v.Name "oidc-audience-binding" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]