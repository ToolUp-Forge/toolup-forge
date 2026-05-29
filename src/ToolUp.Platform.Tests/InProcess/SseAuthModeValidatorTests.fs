module ToolUp.Platform.Tests.InProcess.SseAuthModeValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (surfaces: SurfaceProfile list) (sseAuth: SseAuthMode) (escapeHatch: bool) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        SseAuthMode = sseAuth
        AcceptQueryParamSseAuthWhenAuthRequired = escapeHatch
}

let private validate (config: ServerConfig) : ValidationResult =
    let v = SseAuthModeValidator.SseAuthModeValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 6l.I — SSE auth mode validator" [

        test "Anonymous mode + QueryParamFallback → Ok (anonymous never refused)" {
            let result = validate (cfg Surfaces.anonymous QueryParamFallback false)
            Expect.equal result Ok "anonymous mode is exempt — userId is a session marker"
        }

        test "Individual mode + CookieRequired → Ok" {
            let result = validate (cfg Surfaces.individual CookieRequired false)
            Expect.equal result Ok "cookie auth is the production path"
        }

        test "Individual mode + QueryParamFallback + no escape hatch → Error" {
            let result = validate (cfg Surfaces.individual QueryParamFallback false)

            match result with
            | Error msg ->
                Expect.stringContains msg "Individual" "names the offending mode"
                Expect.stringContains msg "QueryParamFallback" "names the offending fallback"
                Expect.stringContains msg "userId" "explains the consequence"
                Expect.stringContains msg "TOOLUP_SSE_AUTH=cookie" "points at the canonical fix"

                Expect.stringContains msg "AcceptQueryParamSseAuthWhenAuthRequired" "documents the escape hatch"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Team mode + QueryParamFallback → Error" {
            let result = validate (cfg Surfaces.team QueryParamFallback false)

            match result with
            | Error msg -> Expect.stringContains msg "Team" "names the offending mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "MultiTeam mode + QueryParamFallback → Error" {
            let result = validate (cfg Surfaces.multiTeam QueryParamFallback false)

            match result with
            | Error msg -> Expect.stringContains msg "MultiTeam" "names the offending mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "AuthenticatedEphemeral mode + QueryParamFallback → Error" {
            let result = validate (cfg Surfaces.trial QueryParamFallback false)

            match result with
            | Error msg -> Expect.stringContains msg "AuthenticatedEphemeral" "names the offending mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Individual mode + QueryParamFallback + escape hatch → Ok" {
            let result = validate (cfg Surfaces.individual QueryParamFallback true)
            Expect.equal result Ok "explicit opt-in passes"
        }

        test "Validator metadata is well-formed" {
            let v =
                SseAuthModeValidator.SseAuthModeValidator(cfg Surfaces.individual QueryParamFallback false)
                :> IConfigValidator

            Expect.equal v.Name "sse-auth-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]