module ToolUp.Platform.Tests.InProcess.AuditLogModeValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (surfaces: SurfaceProfile list) (auditMode: FeatureMode) = {
    ServerConfig.defaults with
        Surfaces = surfaces
        AuditLog = auditMode
}

let private validate (config: ServerConfig) : ValidationResult =
    let v = AuditLogModeValidator.AuditLogModeValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 6l.B — AuditLog mode validator" [

        test "Anonymous mode + FeatureMode.Disabled → Ok (dev default)" {
            let result = validate (cfg Surfaces.anonymous FeatureMode.Disabled)
            Expect.equal result Ok "dev/anon path passes"
        }

        test "Anonymous mode + FeatureMode.Enabled → Ok (audit-on in anon is fine)" {
            let result = validate (cfg Surfaces.anonymous FeatureMode.Enabled)
            Expect.equal result Ok "anon-with-audit-on still passes"
        }

        test "Individual mode + FeatureMode.Disabled → Warning (compliance gap)" {
            let result = validate (cfg Surfaces.individual FeatureMode.Disabled)

            match result with
            | Warning msg ->
                Expect.stringContains msg "Individual" "names the mode"
                Expect.stringContains msg "FeatureMode.Disabled" "names the audit setting"
                Expect.stringContains msg "no compliance trail" "explains the consequence"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Team mode + FeatureMode.Disabled → Warning" {
            let result = validate (cfg Surfaces.team FeatureMode.Disabled)

            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %A" other
        }

        test "MultiTeam mode + FeatureMode.Disabled → Warning" {
            let result = validate (cfg Surfaces.multiTeam FeatureMode.Disabled)

            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %A" other
        }

        test "AuthenticatedEphemeral + FeatureMode.Disabled → Warning" {
            let result = validate (cfg Surfaces.trial FeatureMode.Disabled)

            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Individual mode + FeatureMode.Enabled → Ok (the production path)" {
            let result = validate (cfg Surfaces.individual FeatureMode.Enabled)
            Expect.equal result Ok "production path passes"
        }

        test "Team mode + FeatureMode.Enabled → Ok" {
            let result = validate (cfg Surfaces.team FeatureMode.Enabled)
            Expect.equal result Ok "production path passes"
        }

        test "Validator metadata is well-formed" {
            let v =
                AuditLogModeValidator.AuditLogModeValidator(cfg Surfaces.anonymous FeatureMode.Disabled)
                :> IConfigValidator

            Expect.equal v.Name "audit-log-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]