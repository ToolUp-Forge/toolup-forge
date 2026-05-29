module ToolUp.Platform.Tests.InProcess.RateLimitModeValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg
    (surfaces: SurfaceProfile list)
    (requireHttps: bool)
    (rateLimit: RateLimitConfig)
    (escapeHatch: bool)
    : ServerConfig =
    {
        ServerConfig.defaults with
            Surfaces = surfaces
            RequireHttps = requireHttps
            RateLimit = rateLimit
            AcceptNoRateLimitWhenAuthRequired = escapeHatch
    }

let private validate (config: ServerConfig) : ValidationResult =
    let v = RateLimitModeValidator.RateLimitModeValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private policy: RateLimitPolicy = {
    PermitLimit = 100
    WindowSeconds = 60
    QueueLimit = 20
}

let private rl: RateLimitConfig = RateLimitConfig.uniform policy

[<Tests>]
let tests =
    testList "Phase 6l.G — RateLimit mode validator" [

        test "Anonymous mode + RequireHttps + no rate limit → Ok (anonymous never warned)" {
            let result = validate (cfg Surfaces.anonymous true RateLimitConfig.none false)
            Expect.equal result Ok "anonymous mode is exempt"
        }

        test "Individual mode + no HTTPS + no forwarded headers + no rate limit → Ok (not internet-facing)" {
            let result = validate (cfg Surfaces.individual false RateLimitConfig.none false)
            Expect.equal result Ok "internet-facing heuristic requires RequireHttps or TrustForwardedHeaders"
        }

        test "Individual mode + TrustForwardedHeaders (TLS-terminated proxy) + no rate limit → Warning" {
            let proxied = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = false
                    TrustForwardedHeaders = true
                    RateLimit = RateLimitConfig.none
                    AcceptNoRateLimitWhenAuthRequired = false
            }

            match validate proxied with
            | Warning msg -> Expect.stringContains msg "Individual" "proxied topology is internet-facing"
            | other -> failtestf "expected Warning for proxied deployment, got %A" other
        }

        test "Individual mode + RequireHttps + rate limit set → Ok" {
            let result = validate (cfg Surfaces.individual true rl false)
            Expect.equal result Ok "rate limit configured satisfies the validator"
        }

        test "Individual mode + RequireHttps + no rate limit + no escape hatch → Warning" {
            let result = validate (cfg Surfaces.individual true RateLimitConfig.none false)

            match result with
            | Warning msg ->
                Expect.stringContains msg "Individual" "names the offending mode"
                Expect.stringContains msg "RateLimit = RateLimitConfig.none" "names the missing config"
                Expect.stringContains msg "DoSed" "explains the consequence"

                Expect.stringContains
                    msg
                    "RateLimitConfig.uniform"
                    "names the real fix API (not a non-existent env var)"

                Expect.stringContains msg "AcceptNoRateLimitWhenAuthRequired" "documents the escape hatch"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Team mode + RequireHttps + no rate limit → Warning" {
            let result = validate (cfg Surfaces.team true RateLimitConfig.none false)

            match result with
            | Warning msg -> Expect.stringContains msg "Team" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "MultiTeam mode + RequireHttps + no rate limit → Warning" {
            let result = validate (cfg Surfaces.multiTeam true RateLimitConfig.none false)

            match result with
            | Warning msg -> Expect.stringContains msg "MultiTeam" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "AuthenticatedEphemeral mode + RequireHttps + no rate limit → Warning" {
            let result = validate (cfg Surfaces.trial true RateLimitConfig.none false)

            match result with
            | Warning msg -> Expect.stringContains msg "AuthenticatedEphemeral" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Individual mode + RequireHttps + no rate limit + escape hatch → Ok" {
            let result = validate (cfg Surfaces.individual true RateLimitConfig.none true)
            Expect.equal result Ok "explicit opt-in silences the warning"
        }

        test "PerShape policy keyed on a kind no Surface admits → orphan Warning" {
            // Individual surface admits only UserKind; a TeamMemberKind policy
            // is dead config — no request resolves to it. A PerShape policy
            // also means the limiter is enabled, so Rule 1 (DoS) stays quiet.
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = true
                    RateLimit = RateLimitConfig.perShape (Map.ofList [ TeamMemberKind, policy ])
                    AcceptNoRateLimitWhenAuthRequired = false
            }

            match validate config with
            | Warning msg ->
                Expect.stringContains msg "PerShape" "names the offending config surface"
                Expect.stringContains msg "TeamMemberKind" "names the orphaned subject kind"
                Expect.stringContains msg "dead config" "explains why it never fires"
            | other -> failtestf "expected orphan Warning, got %A" other
        }

        test "PerShape policy keyed on a kind a Surface admits → Ok (no orphan)" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = true
                    RateLimit = RateLimitConfig.perShape (Map.ofList [ UserKind, policy ])
                    AcceptNoRateLimitWhenAuthRequired = false
            }

            Expect.equal (validate config) Ok "UserKind is admitted by the Individual surface"
        }

        test "Validator metadata is well-formed" {
            let v =
                RateLimitModeValidator.RateLimitModeValidator(cfg Surfaces.individual true RateLimitConfig.none false)
                :> IConfigValidator

            Expect.equal v.Name "rate-limit-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]