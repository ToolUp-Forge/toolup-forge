module ToolUp.Platform.Tests.InProcess.RateLimitConfigValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (rateLimit: RateLimitConfig option) : ServerConfig = {
    ServerConfig.defaults with
        RateLimit = rateLimit
}

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        RateLimitConfigValidator.RateLimitConfigValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private valid: RateLimitConfig = {
    PermitLimit = 100
    WindowSeconds = 60
    QueueLimit = 20
}

[<Tests>]
let tests =
    testList "RateLimitConfig range validator" [

        test "RateLimit = None → Ok (limiter disabled, not this validator's concern)" {
            Expect.equal (validate (cfg None)) Ok "no limiter to range-check"
        }

        test "Well-formed RateLimitConfig → Ok" {
            Expect.equal (validate (cfg (Some valid))) Ok "in-range record passes"
        }

        test "PermitLimit = 0 → Error (would 429 all traffic)" {
            match validate (cfg (Some { valid with PermitLimit = 0 })) with
            | Error msg ->
                Expect.stringContains msg "PermitLimit = 0" "names the bad field"
                Expect.stringContains msg "ServerConfig.RateLimit = Some" "points at the fix"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Negative WindowSeconds → Error" {
            match validate (cfg (Some { valid with WindowSeconds = -1 })) with
            | Error msg -> Expect.stringContains msg "WindowSeconds = -1" "names the bad field"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Negative QueueLimit → Error" {
            match validate (cfg (Some { valid with QueueLimit = -5 })) with
            | Error msg -> Expect.stringContains msg "QueueLimit = -5" "names the bad field"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Multiple out-of-range fields are all reported" {
            match
                validate (
                    cfg (
                        Some {
                            PermitLimit = 0
                            WindowSeconds = 0
                            QueueLimit = -1
                        }
                    )
                )
            with
            | Error msg ->
                Expect.stringContains msg "PermitLimit" "reports permit"
                Expect.stringContains msg "WindowSeconds" "reports window"
                Expect.stringContains msg "QueueLimit" "reports queue"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Validator metadata is well-formed" {
            let v =
                RateLimitConfigValidator.RateLimitConfigValidator(cfg (Some valid)) :> IConfigValidator

            Expect.equal v.Name "rate-limit-config" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]