module ToolUp.Platform.Tests.InProcess.RateLimitConfigValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (rateLimit: RateLimitConfig) : ServerConfig = {
    ServerConfig.defaults with
        RateLimit = rateLimit
}

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        RateLimitConfigValidator.RateLimitConfigValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private validPolicy: RateLimitPolicy = {
    PermitLimit = 100
    WindowSeconds = 60
    QueueLimit = 20
}

[<Tests>]
let tests =
    testList "RateLimitConfig range validator" [

        test "RateLimitConfig.none → Ok (no policies to range-check)" {
            Expect.equal (validate (cfg RateLimitConfig.none)) Ok "no policy to range-check"
        }

        test "Well-formed uniform policy → Ok" {
            Expect.equal (validate (cfg (RateLimitConfig.uniform validPolicy))) Ok "in-range Default passes"
        }

        test "Default.PermitLimit = 0 → Error (would 429 all traffic)" {
            match validate (cfg (RateLimitConfig.uniform { validPolicy with PermitLimit = 0 })) with
            | Error msg ->
                Expect.stringContains msg "PermitLimit = 0" "names the bad field"
                Expect.stringContains msg "Default" "labels the offending policy"
                Expect.stringContains msg "RateLimitConfig.uniform" "points at the fix"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Negative Default.WindowSeconds → Error" {
            match validate (cfg (RateLimitConfig.uniform { validPolicy with WindowSeconds = -1 })) with
            | Error msg -> Expect.stringContains msg "WindowSeconds = -1" "names the bad field"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Negative Default.QueueLimit → Error" {
            match validate (cfg (RateLimitConfig.uniform { validPolicy with QueueLimit = -5 })) with
            | Error msg -> Expect.stringContains msg "QueueLimit = -5" "names the bad field"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Multiple out-of-range fields in Default are all reported" {
            match
                validate (
                    cfg (
                        RateLimitConfig.uniform {
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

        test "Out-of-range PerShape policy is range-checked and labelled by kind" {
            let config =
                RateLimitConfig.perShape (Map.ofList [ UserKind, { validPolicy with PermitLimit = 0 } ])

            match validate (cfg config) with
            | Error msg ->
                Expect.stringContains msg "PermitLimit = 0" "names the bad field"
                Expect.stringContains msg "PerShape" "labels the PerShape origin"
                Expect.stringContains msg "UserKind" "names the offending subject kind"
            | other -> failtestf "expected Error, got %A" other
        }

        test "withOverrides range-checks both Default and PerShape" {
            let config =
                RateLimitConfig.withOverrides
                    { validPolicy with WindowSeconds = 0 }
                    (Map.ofList [ TeamMemberKind, { validPolicy with QueueLimit = -2 } ])

            match validate (cfg config) with
            | Error msg ->
                Expect.stringContains msg "Default.WindowSeconds = 0" "reports the bad Default"
                Expect.stringContains msg "QueueLimit = -2" "reports the bad PerShape policy"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Validator metadata is well-formed" {
            let v =
                RateLimitConfigValidator.RateLimitConfigValidator(cfg (RateLimitConfig.uniform validPolicy))
                :> IConfigValidator

            Expect.equal v.Name "rate-limit-config" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]