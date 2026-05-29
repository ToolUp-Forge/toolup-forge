module ToolUp.Platform.Tests.InProcess.HeaderAuthProviderModeValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.ConfigValidation

// ─── Helpers ──────────────────────────────────────────────────────────

let private cfg (surfaces: SurfaceProfile list) (escapeHatch: bool) = {
    ServerConfig.defaults with
        Surfaces = surfaces
        AcceptHeaderAuthWhenAuthRequired = escapeHatch
}

let private headerAuthProvider () : IAuthProvider =
    HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider

/// Stand-in non-HeaderAuth provider — distinguishable by type. Used to
/// prove the validator is HeaderAuthProvider-specific rather than
/// blanket-refusing every IAuthProvider.
let private alternativeAuthProvider () : IAuthProvider =
    let cfg: StaticJwtAuthProvider.StaticJwtConfig = {
        Secret = "test-secret"
        Issuer = None
        Audience = None
    }

    StaticJwtAuthProvider.StaticJwtAuthProvider(cfg) :> IAuthProvider

let private validate (config: ServerConfig) (auth: IAuthProvider) : ValidationResult =
    let v =
        HeaderAuthProviderModeValidator.HeaderAuthProviderModeValidator(config, auth) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

// ─── Tests ────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 6l.A — HeaderAuthProvider mode validator" [

        test "Anonymous mode + HeaderAuthProvider → Ok (dev default keeps working)" {
            let result = validate (cfg Surfaces.anonymous false) (headerAuthProvider ())
            Expect.equal result Ok "Anonymous mode is the explicit no-auth path"
        }

        test "Individual mode + HeaderAuthProvider + no escape hatch → Error" {
            // The headline regression: production deployment in
            // Individual mode with the dev-default header provider.
            // Pre-6l.A this would boot with spoofable auth.
            let result = validate (cfg Surfaces.individual false) (headerAuthProvider ())

            match result with
            | Error msg ->
                Expect.stringContains msg "Individual" "names the offending Mode"

                Expect.stringContains msg "HeaderAuthProvider" "names the registered provider"

                Expect.stringContains msg "TOOLUP_AUTH_MODE=oidc" "points at the canonical fix"

                Expect.stringContains msg "AcceptHeaderAuthWhenAuthRequired" "documents the escape hatch"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Team mode + HeaderAuthProvider → Error" {
            let result = validate (cfg Surfaces.team false) (headerAuthProvider ())

            match result with
            | Error msg -> Expect.stringContains msg "Team" "names the offending Mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "MultiTeam mode + HeaderAuthProvider → Error" {
            let result = validate (cfg Surfaces.multiTeam false) (headerAuthProvider ())

            match result with
            | Error msg -> Expect.stringContains msg "MultiTeam" "names the offending Mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "AuthenticatedEphemeral mode + HeaderAuthProvider → Error" {
            let result = validate (cfg Surfaces.trial false) (headerAuthProvider ())

            match result with
            | Error msg -> Expect.stringContains msg "AuthenticatedEphemeral" "names the offending Mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Individual mode + HeaderAuthProvider + escape hatch → Ok" {
            // Behind-mTLS deployments with a verified-identity proxy
            // get to opt in. The validator passes; the operator owns
            // the trust boundary.
            let result = validate (cfg Surfaces.individual true) (headerAuthProvider ())
            Expect.equal result Ok "escape hatch passes"
        }

        test "Individual mode + non-HeaderAuth provider → Ok (validator is provider-specific)" {
            // A deployment running OIDC / StaticJwt / a custom
            // provider in Individual mode is the production path
            // we WANT — must not be refused.
            let result = validate (cfg Surfaces.individual false) (alternativeAuthProvider ())
            Expect.equal result Ok "non-HeaderAuth providers are not refused"
        }

        test "Validator name is stable + non-empty" {
            let v =
                HeaderAuthProviderModeValidator.HeaderAuthProviderModeValidator(
                    cfg Surfaces.anonymous false,
                    headerAuthProvider ()
                )
                :> IConfigValidator

            Expect.equal v.Name "header-auth-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]