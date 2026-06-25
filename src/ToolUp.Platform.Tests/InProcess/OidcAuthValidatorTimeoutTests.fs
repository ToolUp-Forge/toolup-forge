module ToolUp.Platform.Tests.InProcess.OidcAuthValidatorTimeoutTests

open System
open Expecto
open ToolUp.AuthProviders
open ToolUp.Platform.ConfigValidation

// Phase 248 — `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` wiring in
// `OidcAuthValidator.tryFromEnv`. Asserts the env var tunes the probe
// deadline, defaults to 5s when unset, and is *rejected with a message*
// (never silently defaulted) when non-numeric / non-positive / absurd.

let private issuerVar = "TOOLUP_OIDC_ISSUER"
let private timeoutVar = "TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS"

/// Run `f` with the two env vars set, restoring whatever was there
/// before. The whole Expecto pack shares one process, so leaking either
/// var would poison every later OIDC test.
let private withEnv (issuer: string option) (timeoutMs: string option) (f: unit -> 'a) : 'a =
    let priorIssuer = Environment.GetEnvironmentVariable issuerVar
    let priorTimeout = Environment.GetEnvironmentVariable timeoutVar

    try
        Environment.SetEnvironmentVariable(issuerVar, Option.toObj issuer)
        Environment.SetEnvironmentVariable(timeoutVar, Option.toObj timeoutMs)
        f ()
    finally
        Environment.SetEnvironmentVariable(issuerVar, priorIssuer)
        Environment.SetEnvironmentVariable(timeoutVar, priorTimeout)

let private issuer = "https://login.example.com"

[<Tests>]
let tests =
    testList "OIDC preflight timeout knob (Phase 248)" [

        test "TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS=15000 yields a 15s validator timeout" {
            let v = withEnv (Some issuer) (Some "15000") OidcAuthValidator.tryFromEnv
            Expect.isSome v "issuer set ⇒ validator registered"
            Expect.equal v.Value.Timeout (TimeSpan.FromSeconds 15.0) "env var tunes the probe deadline"
        }

        test "Unset TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS yields the 5s default" {
            let v = withEnv (Some issuer) None OidcAuthValidator.tryFromEnv
            Expect.isSome v "issuer set ⇒ validator registered"

            Expect.equal
                v.Value.Timeout
                IConfigValidator.defaultTimeout
                "absent override ⇒ byte-for-byte the prior 5s default"
        }

        test "Whitespace around a valid value is tolerated" {
            let v = withEnv (Some issuer) (Some " 8000 ") OidcAuthValidator.tryFromEnv
            Expect.equal v.Value.Timeout (TimeSpan.FromSeconds 8.0) "trimmed then parsed"
        }

        test "Non-numeric value is rejected with a clear message, not silently defaulted" {
            let v = withEnv (Some issuer) (Some "fifteen") OidcAuthValidator.tryFromEnv
            Expect.isSome v "still registers a validator (one that fails preflight)"

            match v.Value.Validate() |> Async.RunSynchronously with
            | Error msg ->
                Expect.stringContains msg timeoutVar "names the offending env var"
                Expect.stringContains msg "fifteen" "echoes the bad value"
            | other -> failtestf "expected Error (rejected), got %A" other
        }

        test "Non-positive value is rejected with a clear message" {
            let v = withEnv (Some issuer) (Some "-5") OidcAuthValidator.tryFromEnv

            match v.Value.Validate() |> Async.RunSynchronously with
            | Error msg -> Expect.stringContains msg timeoutVar "names the offending env var"
            | other -> failtestf "expected Error (rejected), got %A" other
        }

        test "Absurdly large value (above the sanity bound) is rejected" {
            let v = withEnv (Some issuer) (Some "9000000") OidcAuthValidator.tryFromEnv

            match v.Value.Validate() |> Async.RunSynchronously with
            | Error msg -> Expect.stringContains msg timeoutVar "names the offending env var"
            | other -> failtestf "expected Error (rejected), got %A" other
        }

        test "No issuer ⇒ no validator regardless of the timeout var" {
            let v = withEnv None (Some "15000") OidcAuthValidator.tryFromEnv
            Expect.isNone v "without OIDC, the stray timeout var is harmless"
        }
    ]