module ToolUp.Platform.Tests.InProcess.RateLimitModeValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg
    (mode: PlatformMode)
    (requireHttps: bool)
    (rateLimit: RateLimitConfig option)
    (escapeHatch: bool)
    : ServerConfig =
    {
        ServerConfig.defaults with
            Surfaces = Surfaces.fromLegacyMode mode
            RequireHttps = requireHttps
            RateLimit = rateLimit
            AcceptNoRateLimitWhenAuthRequired = escapeHatch
    }

let private validate (config: ServerConfig) : ValidationResult =
    let v = RateLimitModeValidator.RateLimitModeValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private rl: RateLimitConfig = {
    PermitLimit = 100
    WindowSeconds = 60
    QueueLimit = 20
}

[<Tests>]
let tests =
    testList "Phase 6l.G — RateLimit mode validator" [

        test "Anonymous mode + RequireHttps + no rate limit → Ok (anonymous never warned)" {
            let result = validate (cfg Anonymous true None false)
            Expect.equal result Ok "anonymous mode is exempt"
        }

        test "Individual mode + no HTTPS + no forwarded headers + no rate limit → Ok (not internet-facing)" {
            let result = validate (cfg Individual false None false)
            Expect.equal result Ok "internet-facing heuristic requires RequireHttps or TrustForwardedHeaders"
        }

        test "Individual mode + TrustForwardedHeaders (TLS-terminated proxy) + no rate limit → Warning" {
            let proxied = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = false
                    TrustForwardedHeaders = true
                    RateLimit = None
                    AcceptNoRateLimitWhenAuthRequired = false
            }

            match validate proxied with
            | Warning msg -> Expect.stringContains msg "Individual" "proxied topology is internet-facing"
            | other -> failtestf "expected Warning for proxied deployment, got %A" other
        }

        test "Individual mode + RequireHttps + rate limit set → Ok" {
            let result = validate (cfg Individual true (Some rl) false)
            Expect.equal result Ok "rate limit configured satisfies the validator"
        }

        test "Individual mode + RequireHttps + no rate limit + no escape hatch → Warning" {
            let result = validate (cfg Individual true None false)

            match result with
            | Warning msg ->
                Expect.stringContains msg "Individual" "names the offending mode"
                Expect.stringContains msg "RateLimit = None" "names the missing config"
                Expect.stringContains msg "DoSed" "explains the consequence"

                Expect.stringContains
                    msg
                    "ServerConfig.RateLimit = Some"
                    "names the real fix API (not a non-existent env var)"

                Expect.stringContains msg "AcceptNoRateLimitWhenAuthRequired" "documents the escape hatch"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Team mode + RequireHttps + no rate limit → Warning" {
            let result = validate (cfg Team true None false)

            match result with
            | Warning msg -> Expect.stringContains msg "Team" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "MultiTeam mode + RequireHttps + no rate limit → Warning" {
            let result = validate (cfg MultiTeam true None false)

            match result with
            | Warning msg -> Expect.stringContains msg "MultiTeam" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "AuthenticatedEphemeral mode + RequireHttps + no rate limit → Warning" {
            let result = validate (cfg AuthenticatedEphemeral true None false)

            match result with
            | Warning msg -> Expect.stringContains msg "AuthenticatedEphemeral" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Individual mode + RequireHttps + no rate limit + escape hatch → Ok" {
            let result = validate (cfg Individual true None true)
            Expect.equal result Ok "explicit opt-in silences the warning"
        }

        test "Validator metadata is well-formed" {
            let v =
                RateLimitModeValidator.RateLimitModeValidator(cfg Individual true None false) :> IConfigValidator

            Expect.equal v.Name "rate-limit-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]