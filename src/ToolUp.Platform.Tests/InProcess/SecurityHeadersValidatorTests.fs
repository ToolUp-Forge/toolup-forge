module ToolUp.Platform.Tests.InProcess.SecurityHeadersValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (surfaces: SurfaceProfile list) (requireHttps: bool) (headers: Map<string, string>) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        RequireHttps = requireHttps
        // Phase 16d — `TrustForwardedHeaders` flipped default-on. The
        // `cfg` helper pins it false so the `requireHttps` parameter
        // is the sole knob controlling the internet-facing heuristic
        // for these tests; the proxied-topology case below inline-
        // builds its own `ServerConfig` and doesn't use this helper.
        TrustForwardedHeaders = false
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

        test "Individual mode + RequireHttps + empty headers + DefaultSecurityHardening → Ok (CSP via hardening)" {
            // Phase 129d — when SecurityHardening provides an aggregated
            // CSP, the empty-SecurityHeaders warning is spurious and must
            // not fire (the floor + CspMiddleware cover the gap).
            let hardened = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = true
                    TrustForwardedHeaders = false
                    SecurityHeaders = Map.empty
                    SecurityHardening = DefaultSecurityHardening
            }

            Expect.equal (validate hardened) Ok "hardening stamps a CSP → no remaining gap"
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

[<Tests>]
let baselineFloorTests =
    testList "Phase 129d — SecurityHeaders baseline floor (effective)" [

        test "non-HTTPS bind: floor is the three always-safe headers, no HSTS" {
            let eff = SecurityHeaders.effective false Map.empty
            Expect.equal eff.["X-Frame-Options"] "DENY" "clickjacking lockdown by default"
            Expect.equal eff.["X-Content-Type-Options"] "nosniff" "MIME-sniff lockdown by default"
            Expect.equal eff.["Referrer-Policy"] "strict-origin-when-cross-origin" "referrer trimmed by default"
            Expect.isFalse (eff.ContainsKey "Strict-Transport-Security") "no HSTS over a non-HTTPS bind"
            Expect.isFalse (eff.ContainsKey "Content-Security-Policy") "no default CSP in the floor"
        }

        test "HTTPS bind: floor adds HSTS" {
            let eff = SecurityHeaders.effective true Map.empty

            Expect.equal
                eff.["Strict-Transport-Security"]
                SecurityHeaders.hstsHeaderValue
                "HSTS added under RequireHttps"

            Expect.isFalse
                (eff.["Strict-Transport-Security"].Contains "preload")
                "baseline HSTS does not assume preload"
        }

        test "consumer map wins over the floor on a key collision" {
            let configured = Map.ofList [ "X-Frame-Options", "SAMEORIGIN"; "X-Custom", "v" ]
            let eff = SecurityHeaders.effective true configured

            Expect.equal
                eff.["X-Frame-Options"]
                "SAMEORIGIN"
                "consumer-set frame header is not overwritten by the floor"

            Expect.equal eff.["X-Custom"] "v" "consumer-only headers pass through"
            Expect.equal eff.["X-Content-Type-Options"] "nosniff" "untouched floor keys remain"
        }

        test "empty config on a non-HTTPS bind still yields a non-empty floor" {
            Expect.isFalse (SecurityHeaders.effective false Map.empty).IsEmpty "stock deployment is never header-less"
        }
    ]