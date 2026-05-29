module ToolUp.Platform.Tests.InProcess.SecurityHeadersValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (surfaces: SurfaceProfile list) (requireHttps: bool) (headers: Map<string, string>) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        RequireHttps = requireHttps
        SecurityHeaders = headers
}

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        SecurityHeadersValidator.SecurityHeadersValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 6l.K — SecurityHeaders mode validator" [

        test "Anonymous mode + RequireHttps + empty headers → Ok (anonymous never warned)" {
            let result = validate (cfg Surfaces.anonymous true Map.empty)
            Expect.equal result Ok "anonymous mode is exempt"
        }

        test "Individual mode + no HTTPS + no forwarded headers + empty headers → Ok (not internet-facing)" {
            let result = validate (cfg Surfaces.individual false Map.empty)
            Expect.equal result Ok "internet-facing heuristic requires RequireHttps or TrustForwardedHeaders"
        }

        test "Individual mode + TrustForwardedHeaders (TLS-terminated proxy) + empty headers → Warning" {
            let proxied = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = false
                    TrustForwardedHeaders = true
                    SecurityHeaders = Map.empty
            }

            match validate proxied with
            | Warning msg -> Expect.stringContains msg "Individual" "proxied topology is internet-facing"
            | other -> failtestf "expected Warning for proxied deployment, got %A" other
        }

        test "Individual mode + RequireHttps + productionDefaults → Ok" {
            let result =
                validate (cfg Surfaces.individual true SecurityHeaders.productionDefaults)

            Expect.equal result Ok "production-defaults map satisfies the validator"
        }

        test "Individual mode + RequireHttps + empty headers → Warning" {
            let result = validate (cfg Surfaces.individual true Map.empty)

            match result with
            | Warning msg ->
                Expect.stringContains msg "Individual" "names the offending mode"
                Expect.stringContains msg "SecurityHeaders = Map.empty" "names the missing config"
                Expect.stringContains msg "HSTS" "names the missing protections"
                Expect.stringContains msg "productionDefaults" "points at the canonical fix"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Team mode + RequireHttps + empty headers → Warning" {
            let result = validate (cfg Surfaces.team true Map.empty)

            match result with
            | Warning msg -> Expect.stringContains msg "Team" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "MultiTeam mode + RequireHttps + empty headers → Warning" {
            let result = validate (cfg Surfaces.multiTeam true Map.empty)

            match result with
            | Warning msg -> Expect.stringContains msg "MultiTeam" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Individual mode + RequireHttps + non-empty (single header) → Ok" {
            let result =
                validate (cfg Surfaces.individual true (Map.ofList [ "X-Custom-Header", "v" ]))

            Expect.equal result Ok "any non-empty map satisfies the validator"
        }

        test "productionDefaults contains the four critical headers" {
            let defaults = SecurityHeaders.productionDefaults
            Expect.isTrue (defaults.ContainsKey "Strict-Transport-Security") "HSTS"
            Expect.isTrue (defaults.ContainsKey "X-Content-Type-Options") "nosniff"
            Expect.isTrue (defaults.ContainsKey "X-Frame-Options") "X-Frame-Options"
            Expect.isTrue (defaults.ContainsKey "Content-Security-Policy") "CSP"
        }

        test "Validator metadata is well-formed" {
            let v =
                SecurityHeadersValidator.SecurityHeadersValidator(cfg Surfaces.individual true Map.empty)
                :> IConfigValidator

            Expect.equal v.Name "security-headers-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]