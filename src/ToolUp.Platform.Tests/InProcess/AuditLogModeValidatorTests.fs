module ToolUp.Platform.Tests.InProcess.AuditLogModeValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (surfaces: SurfaceProfile list) (auditMode: AuditLogMode) = {
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

        test "Anonymous mode + NoAuditLog → Ok (dev default)" {
            let result = validate (cfg Surfaces.anonymous NoAuditLog)
            Expect.equal result Ok "dev/anon path passes"
        }

        test "Anonymous mode + EnabledAuditLog → Ok (audit-on in anon is fine)" {
            let result = validate (cfg Surfaces.anonymous EnabledAuditLog)
            Expect.equal result Ok "anon-with-audit-on still passes"
        }

        test "Individual mode + NoAuditLog → Warning (compliance gap)" {
            let result = validate (cfg Surfaces.individual NoAuditLog)

            match result with
            | Warning msg ->
                Expect.stringContains msg "Individual" "names the mode"
                Expect.stringContains msg "NoAuditLog" "names the audit setting"
                Expect.stringContains msg "no compliance trail" "explains the consequence"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Team mode + NoAuditLog → Warning" {
            let result = validate (cfg Surfaces.team NoAuditLog)

            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %A" other
        }

        test "MultiTeam mode + NoAuditLog → Warning" {
            let result = validate (cfg Surfaces.multiTeam NoAuditLog)

            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %A" other
        }

        test "AuthenticatedEphemeral + NoAuditLog → Warning" {
            let result = validate (cfg Surfaces.trial NoAuditLog)

            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Individual mode + EnabledAuditLog → Ok (the production path)" {
            let result = validate (cfg Surfaces.individual EnabledAuditLog)
            Expect.equal result Ok "production path passes"
        }

        test "Team mode + EnabledAuditLog → Ok" {
            let result = validate (cfg Surfaces.team EnabledAuditLog)
            Expect.equal result Ok "production path passes"
        }

        test "Validator metadata is well-formed" {
            let v =
                AuditLogModeValidator.AuditLogModeValidator(cfg Surfaces.anonymous NoAuditLog) :> IConfigValidator

            Expect.equal v.Name "audit-log-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]