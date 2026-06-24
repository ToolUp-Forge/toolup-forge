module ToolUp.Platform.Tests.InProcess.ForwardedHeadersTrustValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (surfaces: SurfaceProfile list) (trustForwarded: bool) (requireHttps: bool) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        TrustForwardedHeaders = trustForwarded
        RequireHttps = requireHttps
}

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        ForwardedHeadersTrustValidator.ForwardedHeadersTrustValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 6l.K — ForwardedHeaders trust validator" [

        test "Anonymous mode + TrustForwardedHeaders → Warning (rate-limit-bypass signal, no longer exempt)" {
            // The X-Forwarded-For spoof bypasses IP rate limiting + poisons
            // logs in anonymous mode too — anonymous deployments must get the
            // signal, not silence.
            let result = validate (cfg Surfaces.anonymous true false)

            match result with
            | Warning msg ->
                Expect.stringContains msg "X-Forwarded-For" "anonymous mode now gets the rate-limit-bypass signal"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Individual mode + no TrustForwardedHeaders → Ok (no spoof surface)" {
            let result = validate (cfg Surfaces.individual false false)
            Expect.equal result Ok "validator only fires when forwarded headers are trusted"
        }

        test "Individual mode + TrustForwardedHeaders + RequireHttps → Warning (rate-limit bypass persists)" {
            // RequireHttps closes the IsHttps spoof but NOT the X-Forwarded-For
            // rate-limit-bypass / log-poisoning (KnownProxies still cleared).
            let result = validate (cfg Surfaces.individual true true)

            match result with
            | Warning msg ->
                Expect.stringContains
                    msg
                    "X-Forwarded-For"
                    "the rate-limit-bypass surface is independent of RequireHttps"

                Expect.isFalse
                    (msg.Contains "TOOLUP_REQUIRE_HTTPS=1")
                    "no IsHttps clause when HTTPS is already required"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Individual mode + TrustForwardedHeaders + no HTTPS → Warning" {
            let result = validate (cfg Surfaces.individual true false)

            match result with
            | Warning msg ->
                Expect.stringContains msg "Individual" "names the offending mode"

                Expect.stringContains msg "TrustForwardedHeaders = true" "names the offending config"

                Expect.stringContains msg "RequireHttps = false" "names the missing config"
                Expect.stringContains msg "X-Forwarded-Proto" "explains the consequence"
                Expect.stringContains msg "TOOLUP_REQUIRE_HTTPS=1" "points at the canonical fix"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Team mode + TrustForwardedHeaders + no HTTPS → Warning" {
            let result = validate (cfg Surfaces.team true false)

            match result with
            | Warning msg -> Expect.stringContains msg "Team" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "MultiTeam mode + TrustForwardedHeaders + no HTTPS → Warning" {
            let result = validate (cfg Surfaces.multiTeam true false)

            match result with
            | Warning msg -> Expect.stringContains msg "MultiTeam" "names the offending mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Validator metadata is well-formed" {
            let v =
                ForwardedHeadersTrustValidator.ForwardedHeadersTrustValidator(cfg Surfaces.individual true false)
                :> IConfigValidator

            Expect.equal v.Name "forwarded-headers-trust" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]