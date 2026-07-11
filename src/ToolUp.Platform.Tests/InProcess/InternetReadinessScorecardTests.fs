module ToolUp.Platform.Tests.InProcess.InternetReadinessScorecardTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator
open ToolUp.Platform.InternetReadinessScorecard

// ─── Phase 209 — internet-readiness secure-default scorecard ─────────
//
// The scorecard is a PURE read-side projection over the aggregated
// preflight `ValidatorOutcome` list. These tests pin the acceptance
// contract: an all-satisfied run grades Ready; flipping any single control
// downgrades the grade and names the offender; and the scorecard adds no
// failure the underlying validators did not already report.

/// Synthetic preflight outcome for a named control.
let private outcome (name: string) (result: ValidationResult) : ValidatorOutcome = {
    Name = name
    Result = result
    ElapsedMs = 1L
}

/// A preflight run in which every catalog control passed.
let private allPass: ValidatorOutcome list =
    catalog |> List.map (fun d -> outcome d.Name Ok)

/// Replace one control's outcome in an otherwise-passing run.
let private withResult (name: string) (result: ValidationResult) (outcomes: ValidatorOutcome list) =
    outcomes
    |> List.map (fun o -> if o.Name = name then { o with Result = result } else o)

/// Live secure-by-default validator instances whose `.Name` the catalog
/// claims to key — each built from `ServerConfig.defaults` exactly as
/// `registerFirstPartyConfigValidators` builds them. Ties the catalog's
/// enumeration to real validator names so a rename on either side is caught.
let private liveValidatorNames: string list =
    let c = ServerConfig.defaults

    [
        SecurityHeadersValidator.SecurityHeadersValidator(c) :> IConfigValidator
        CorsConfigValidator.CorsConfigValidator(c)
        CsrfDefaultModeValidator.CsrfDefaultModeValidator(c)
        CsrfHardeningValidator.CsrfHardeningValidator(c)
        ForwardedHeadersTrustValidator.ForwardedHeadersTrustValidator(c)
        PublicBaseUrlFormatValidator.PublicBaseUrlFormatValidator(c)
        AutoBootstrapDevAdminModeValidator.AutoBootstrapDevAdminModeValidator(c)
        SseAuthModeValidator.SseAuthModeValidator(c)
        MaxRequestBodyBytesValidator.MaxRequestBodyBytesValidator(c)
        RateLimitModeValidator.RateLimitModeValidator(c)
    ]
    |> List.map _.Name

[<Tests>]
let tests =
    testList "Phase 209 — internet-readiness scorecard" [

        test "all controls satisfied → Ready, no offenders, score 100" {
            let report = ofOutcomes allPass
            Expect.equal report.Grade ReadinessGrade.Ready "every assessed control passes"
            Expect.isEmpty report.Failing "no failing controls"
            Expect.isEmpty report.Warnings "no warning controls"
            Expect.isEmpty report.NotAssessed "every catalog control was assessed"
            Expect.equal report.Score 100 "all-pass is a perfect weighted score"
        }

        test "flipping one control to Error downgrades to NotReady and names only that control" {
            let offender = "cors-config"

            let report =
                allPass
                |> withResult offender (Error "AllowCredentials + wildcard origin")
                |> ofOutcomes

            Expect.equal report.Grade ReadinessGrade.NotReady "a hard failure blocks readiness"
            Expect.equal report.Failing [ offender ] "the offender is named, and only the offender"
            Expect.isEmpty report.Warnings "no warnings in this run"
        }

        test "flipping one control to Warning downgrades to ReadyWithWarnings and names the offender" {
            let offender = "security-headers-mode"
            let report = allPass |> withResult offender (Warning "no CSP") |> ofOutcomes

            Expect.equal report.Grade ReadinessGrade.ReadyWithWarnings "a soft signal degrades but does not block"
            Expect.equal report.Warnings [ offender ] "the warning offender is named"
            Expect.isEmpty report.Failing "a warning is not a failure"
        }

        test "scorecard adds no failure the validators did not report (warnings only ⇒ never NotReady)" {
            let report =
                allPass
                |> withResult "security-headers-mode" (Warning "no CSP")
                |> withResult "rate-limit-mode" (Warning "no rate limiter")
                |> ofOutcomes

            Expect.equal report.Grade ReadinessGrade.ReadyWithWarnings "no underlying Error ⇒ never NotReady"
            Expect.isEmpty report.Failing "the scorecard invents no failure"
            Expect.equal (List.length report.Warnings) 2 "both underlying warnings surface, no more"
        }

        test "an Error on a non-catalog validator does not fail the scorecard (scoped to its catalog)" {
            let report =
                allPass @ [ outcome "some-unrelated-validator" (Error "boom") ] |> ofOutcomes

            Expect.equal report.Grade ReadinessGrade.Ready "an error outside the catalog is not the scorecard's concern"
            Expect.isEmpty report.Failing "no catalog control failed"
        }

        test "a control whose validator did not run is NotAssessed, never a fabricated pass or failure" {
            let absent = "sse-auth-mode"
            let report = allPass |> List.filter (fun o -> o.Name <> absent) |> ofOutcomes

            Expect.contains report.NotAssessed absent "the un-run control is reported NotAssessed"
            Expect.isFalse (List.contains absent report.Failing) "NotAssessed is not a failure"
            Expect.equal report.Grade ReadinessGrade.Ready "a NotAssessed control among passes does not block readiness"
        }

        test "an empty preflight run grades ReadyWithWarnings (cannot attest) with score 0" {
            let report = ofOutcomes []
            Expect.equal report.Grade ReadinessGrade.ReadyWithWarnings "an empty scorecard cannot attest full readiness"
            Expect.equal report.NotAssessed.Length catalog.Length "every catalog control is NotAssessed"
            Expect.equal report.Score 0 "nothing assessed ⇒ no score"
        }

        test "control detail carries the underlying validator's message verbatim" {
            let msg = "ServerConfig.Surfaces = Individual + SecurityHeaders = Map.empty"

            let report =
                allPass |> withResult "security-headers-mode" (Warning msg) |> ofOutcomes

            let assessed =
                report.Controls |> List.find (fun c -> c.Name = "security-headers-mode")

            Expect.equal assessed.Status ControlStatus.Warn "projected to Warn"
            Expect.equal assessed.Detail msg "the validator's own wording is preserved, unaltered"
        }

        test "gradeOf is worst-status over the assessed controls" {
            Expect.equal (gradeOf [ ControlStatus.Pass; ControlStatus.Pass ]) ReadinessGrade.Ready "all pass"

            Expect.equal
                (gradeOf [ ControlStatus.Pass; ControlStatus.Warn ])
                ReadinessGrade.ReadyWithWarnings
                "a warn degrades"

            Expect.equal
                (gradeOf [ ControlStatus.Warn; ControlStatus.Fail ])
                ReadinessGrade.NotReady
                "a fail dominates a warn"

            Expect.equal
                (gradeOf [ ControlStatus.Pass; ControlStatus.NotAssessed ])
                ReadinessGrade.Ready
                "a NotAssessed among passes does not degrade"

            Expect.equal
                (gradeOf [ ControlStatus.NotAssessed; ControlStatus.NotAssessed ])
                ReadinessGrade.ReadyWithWarnings
                "nothing assessed cannot attest"
        }

        test "catalog is well-formed: non-empty, unique names, weights 1..3, all four categories present" {
            Expect.isNonEmpty catalog "the catalog enumerates the secure-by-default controls"

            let names = catalog |> List.map _.Name
            Expect.equal (List.distinct names |> List.length) names.Length "control names are unique"

            for d in catalog do
                Expect.isTrue (d.Weight >= 1 && d.Weight <= 3) (sprintf "%s weight in 1..3" d.Name)

            let categories = catalog |> List.map _.Category |> List.distinct

            for cat in
                [
                    ReadinessCategory.Edge
                    ReadinessCategory.Auth
                    ReadinessCategory.Transport
                    ReadinessCategory.Limits
                ] do
                Expect.contains categories cat (sprintf "category %s is represented" (ReadinessCategory.label cat))
        }

        test "every catalog name keys a real secure-by-default validator (enumeration is not stale)" {
            let catalogNames = catalog |> List.map _.Name |> Set.ofList

            for name in liveValidatorNames do
                Expect.isTrue
                    (Set.contains name catalogNames)
                    (sprintf "live validator '%s' is enumerated in the scorecard catalog" name)
        }

        test "render is a stable multi-line summary naming the grade and each control" {
            let text = render (ofOutcomes allPass)
            Expect.stringContains text "READY" "the grade headlines the summary"
            Expect.stringContains text "cors-config" "each control is listed"
            Expect.stringContains text "edge" "the category label is shown"
        }

        test "logIfEnabled false is a no-op that reads nothing and returns None" {
            let sink =
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = failtest "must not log when disabled"
                    member _.Warn _ = failtest "must not log when disabled"
                    member _.Error(_, _) = failtest "must not log when disabled"
                }

            Expect.isNone (logIfEnabled false sink allPass) "disabled ⇒ no report, no log"
        }
    ]