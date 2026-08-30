// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcCoherenceValidatorTests

open Expecto
open ToolUp.Platform.ConfigValidation
open ToolUp.AuthProviders.Oidc.OidcAppConfig
open ToolUp.AuthProviders.Oidc.OidcPresets
open ToolUp.AuthProviders.Oidc.OidcCoherenceValidator

// ─── OidcCoherenceValidator (0.4.0 — OidcAppConfig) ──────────────────
//
// Per-rule coverage of the 13 rules. Each test asserts the expected
// per-rule outcome from `evaluate` rather than parsing the
// aggregated `ValidationResult` message — `evaluate` is the
// structured surface the future `/dev/inspect` validators panel
// reads, and pinning rule-level behaviour here keeps the
// aggregator's wording flexible.
//
// 0.4.0 BREAKING — validator now takes `OidcAppConfig` directly
// (preset provenance is on the config itself). Tests reshaped to
// construct OidcAppConfig values instead of
// `OidcUIConfig * PresetMetadata option`. Rule 12 (generic +
// ValidateIdToken opt-out) landed at 0.4.3; Rule 13 (google preset
// vs the fixed Google issuer) with the `google` preset.

let private validCfg =
    OidcAppConfig.create "https://issuer.example.test" "test-client-id" "https://app.example.test/auth/callback"

let private hasErrorMatching (substring: string) (outcomes: RuleOutcome list) : bool =
    outcomes
    |> List.exists (function
        | RuleError m -> m.Contains substring
        | _ -> false)

let private hasWarningMatching (substring: string) (outcomes: RuleOutcome list) : bool =
    outcomes
    |> List.exists (function
        | RuleWarning m -> m.Contains substring
        | _ -> false)

let private hasOkMatching (substring: string) (outcomes: RuleOutcome list) : bool =
    outcomes
    |> List.exists (function
        | RuleOk m -> m.Contains substring
        | _ -> false)

let tests: Test =
    testList "OidcClient.OidcCoherenceValidator" [

        // ─── Rule 1 — Issuer empty ──────────────────────────────

        testCase "Rule 1: empty Issuer → ERROR"
        <| fun () ->
            let cfg = { validCfg with Issuer = "" }
            let outcomes = evaluate cfg
            Expect.isTrue (hasErrorMatching "OidcAppConfig.Issuer is empty" outcomes) ""

        testCase "Rule 1: whitespace Issuer → ERROR"
        <| fun () ->
            let cfg = { validCfg with Issuer = "   " }
            let outcomes = evaluate cfg
            Expect.isTrue (hasErrorMatching "OidcAppConfig.Issuer is empty" outcomes) ""

        // ─── Rule 2 — ClientId empty ────────────────────────────

        testCase "Rule 2: empty ClientId → ERROR"
        <| fun () ->
            let cfg = { validCfg with ClientId = "" }
            let outcomes = evaluate cfg
            Expect.isTrue (hasErrorMatching "OidcAppConfig.ClientId is empty" outcomes) ""

        // ─── Rule 3 — RedirectUri empty ─────────────────────────

        testCase "Rule 3: empty RedirectUri → ERROR"
        <| fun () ->
            let cfg = { validCfg with RedirectUri = "" }
            let outcomes = evaluate cfg
            Expect.isTrue (hasErrorMatching "OidcAppConfig.RedirectUri is empty" outcomes) ""

        // ─── Rule 4 — `openid` scope missing ────────────────────

        testCase "Rule 4: Scopes without `openid` → ERROR (OIDC spec)"
        <| fun () ->
            let cfg = {
                validCfg with
                    Scopes = [ "profile"; "email" ]
            }

            let outcomes = evaluate cfg
            Expect.isTrue (hasErrorMatching "does not contain `openid`" outcomes) ""

        testCase "Rule 4: empty Scopes → ERROR (no openid)"
        <| fun () ->
            let cfg = { validCfg with Scopes = [] }
            let outcomes = evaluate cfg
            Expect.isTrue (hasErrorMatching "does not contain `openid`" outcomes) ""

        // ─── Rule 5 — Issuer not HTTPS ──────────────────────────

        testCase "Rule 5: http:// Issuer (non-localhost) → WARN"
        <| fun () ->
            let cfg = {
                validCfg with
                    Issuer = "http://issuer.example.test"
            }

            let outcomes = evaluate cfg

            Expect.isTrue
                (hasWarningMatching "OidcAppConfig.Issuer" outcomes
                 && hasWarningMatching "is not `https://`" outcomes)
                "non-https issuer must warn"

        testCase "Rule 5: http://localhost Issuer → no warning (dev exception)"
        <| fun () ->
            let cfg = {
                validCfg with
                    Issuer = "http://localhost:5000/auth"
            }

            let outcomes = evaluate cfg

            let hasIssuerHttpsWarn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "OidcAppConfig.Issuer" && m.Contains "is not `https://`"
                    | _ -> false)

            Expect.isFalse hasIssuerHttpsWarn "localhost dev exception must NOT warn"

        testCase "Rule 5: http://127.0.0.1 Issuer → no warning"
        <| fun () ->
            let cfg = {
                validCfg with
                    Issuer = "http://127.0.0.1:5000"
            }

            let outcomes = evaluate cfg

            let hasIssuerHttpsWarn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "OidcAppConfig.Issuer" && m.Contains "is not `https://`"
                    | _ -> false)

            Expect.isFalse hasIssuerHttpsWarn ""

        // ─── Rule 6 — RedirectUri not HTTPS ─────────────────────

        testCase "Rule 6: http:// RedirectUri (non-localhost) → WARN"
        <| fun () ->
            let cfg = {
                validCfg with
                    RedirectUri = "http://app.example.test/cb"
            }

            let outcomes = evaluate cfg

            let hasRedirectHttpsWarn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "OidcAppConfig.RedirectUri" && m.Contains "is not `https://`"
                    | _ -> false)

            Expect.isTrue hasRedirectHttpsWarn ""

        testCase "Rule 6: http://localhost RedirectUri → no warning (dev exception)"
        <| fun () ->
            let cfg = {
                validCfg with
                    RedirectUri = "http://localhost:8080/auth/callback"
            }

            let outcomes = evaluate cfg

            let hasRedirectHttpsWarn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "OidcAppConfig.RedirectUri" && m.Contains "is not `https://`"
                    | _ -> false)

            Expect.isFalse hasRedirectHttpsWarn ""

        // ─── Rule 7 — entra-workforce + non-workforce issuer ────

        testCase "Rule 7: workforce preset + non-workforce issuer → WARN"
        <| fun () ->
            let cfg = entraWorkforce "tenant-guid" "client-id" "https://app/cb"

            let badIssuerCfg = {
                cfg with
                    Issuer = "https://mytenant.ciamlogin.com/mytenant/v2.0"
            }

            let outcomes = evaluate badIssuerCfg
            Expect.isTrue (hasWarningMatching "Preset `entra-workforce` declared" outcomes) ""

        testCase "Rule 7: workforce preset + matching issuer → no rule-7 warning"
        <| fun () ->
            let cfg = entraWorkforce "tenant-guid" "client-id" "https://app/cb"
            let outcomes = evaluate cfg

            let hasRule7Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `entra-workforce` declared"
                    | _ -> false)

            Expect.isFalse hasRule7Warn ""

        // ─── Rule 8 — entra-external-id + non-CIAM issuer ──────

        testCase "Rule 8: external-id preset + workforce-shaped issuer → WARN"
        <| fun () ->
            let cfg = entraExternalId "mytenant" "client-id" "https://app/cb"

            let badIssuerCfg = {
                cfg with
                    Issuer = "https://login.microsoftonline.com/tenant-guid/v2.0"
            }

            let outcomes = evaluate badIssuerCfg
            Expect.isTrue (hasWarningMatching "Preset `entra-external-id` declared" outcomes) ""

        testCase "Rule 8: external-id preset + ciamlogin issuer → no rule-8 warning"
        <| fun () ->
            let cfg = entraExternalId "mytenant" "client-id" "https://app/cb"
            let outcomes = evaluate cfg

            let hasRule8Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `entra-external-id` declared"
                    | _ -> false)

            Expect.isFalse hasRule8Warn ""

        testCase "Rule 8: external-id preset + custom-domain issuer → no rule-8 warning"
        <| fun () ->
            let cfg =
                entraExternalIdWithDomain "mytenant" "login.mybrand.com" "client-id" "https://app/cb"

            let outcomes = evaluate cfg

            let hasRule8Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `entra-external-id` declared"
                    | _ -> false)

            Expect.isFalse hasRule8Warn "custom-domain CIAM must satisfy the rule-8 heuristic"

        // ─── Rule 9 — auth0 + Microsoft-shaped issuer ──────────

        testCase "Rule 9: auth0 preset + Microsoft issuer → WARN"
        <| fun () ->
            let cfg = auth0 "mytenant.auth0.com" "client-id" "https://app/cb"

            let pasted = {
                cfg with
                    Issuer = "https://login.microsoftonline.com/tenant/v2.0"
            }

            let outcomes = evaluate pasted
            Expect.isTrue (hasWarningMatching "Preset `auth0` declared" outcomes) ""

        testCase "Rule 9: auth0 preset + auth0 issuer → no rule-9 warning"
        <| fun () ->
            let cfg = auth0 "mytenant.auth0.com" "client-id" "https://app/cb"
            let outcomes = evaluate cfg

            let hasRule9Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `auth0` declared"
                    | _ -> false)

            Expect.isFalse hasRule9Warn ""

        // ─── Rule 10 — preset auto-added scopes dropped ────────

        testCase "Rule 10: workforce preset + Scopes missing the api://...access_as_user → ERROR"
        <| fun () ->
            // The single most dangerous regression — consumer
            // override of Scopes dropped the load-bearing scope.
            // 0.4.3 promoted this case from Warning to Error: Entra
            // mints an opaque Microsoft Graph token without the
            // scope, every authenticated request 401s post-auth, and
            // the app boots cleanly so the failure is invisible
            // until first API call — not recoverable at runtime.
            let cfg = entraWorkforce "tenant-guid" "client-id" "https://app/cb"

            let stripped = {
                cfg with
                    Scopes = [ "openid"; "profile"; "email" ]
            }

            let outcomes = evaluate stripped

            Expect.isTrue
                (hasErrorMatching "auto-adds" outcomes
                 && hasErrorMatching "access_as_user" outcomes)
                "missing api scope must surface as an error that names it"

        testCase "Rule 10: workforce preset + intact Scopes → no rule-10 warning"
        <| fun () ->
            let cfg = entraWorkforce "tenant-guid" "client-id" "https://app/cb"
            let outcomes = evaluate cfg

            let hasRule10Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "auto-adds"
                    | _ -> false)

            Expect.isFalse hasRule10Warn ""

        testCase "Rule 10: External-ID preset + dropped offline_access → WARN"
        <| fun () ->
            let cfg = entraExternalId "mytenant" "client-id" "https://app/cb"

            let stripped = {
                cfg with
                    Scopes = [ "openid"; "profile"; "email" ]
            }

            let outcomes = evaluate stripped

            Expect.isTrue
                (hasWarningMatching "auto-adds" outcomes
                 && hasWarningMatching "offline_access" outcomes)
                ""

        // ─── Rule 11 — preset-applied provenance (Ok) ──────────

        testCase "Rule 11: preset on config → Ok-class outcome records the name"
        <| fun () ->
            let cfg = entraWorkforce "tenant-guid" "client-id" "https://app/cb"
            let outcomes = evaluate cfg
            Expect.isTrue (hasOkMatching "preset `entra-workforce` applied" outcomes) ""

        testCase "Rule 11: Preset = None → no Ok-info outcome"
        <| fun () ->
            let outcomes = evaluate validCfg

            let hasPresetOk =
                outcomes
                |> List.exists (function
                    | RuleOk m -> m.Contains "preset"
                    | _ -> false)

            Expect.isFalse hasPresetOk "without Preset on config, no provenance Ok message emitted"

        // ─── Rule 12 — Generic preset + ValidateIdToken=None ───
        //
        // 0.4.3 flipped `generic.ValidateIdToken`'s default from
        // `None` to `Some true`. Landing back on `None` is now an
        // explicit opt-out from defence-in-depth id_token validation;
        // Rule 12 surfaces it as a Warning so the choice is visible
        // at startup rather than assumed-safe by silence.

        testCase "Rule 12: Generic preset + ValidateIdToken = None → WARN"
        <| fun () ->
            let cfg = generic "https://issuer.example.test" "client-id" "https://app/cb"

            let optedOut = { cfg with ValidateIdToken = None }

            let outcomes = evaluate optedOut

            Expect.isTrue
                (hasWarningMatching "Preset `generic`" outcomes
                 && hasWarningMatching "ValidateIdToken = None" outcomes)
                "explicit opt-out from id_token validation must surface as a warning that names the preset and the field"

        testCase "Rule 12: Generic preset + ValidateIdToken = Some true (default) → no rule-12 warning"
        <| fun () ->
            let cfg = generic "https://issuer.example.test" "client-id" "https://app/cb"

            let outcomes = evaluate cfg

            let hasRule12Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "ValidateIdToken = None"
                    | _ -> false)

            Expect.isFalse hasRule12Warn ""

        testCase "Rule 12: non-Generic preset + ValidateIdToken = None → no rule-12 warning"
        <| fun () ->
            // entraWorkforce defaults ValidateIdToken = None; Rule 12
            // is generic-specific, so this case must not trip it.
            let cfg = entraWorkforce "tenant-guid" "client-id" "https://app/cb"

            Expect.equal cfg.ValidateIdToken None "precondition: workforce preset defaults to None"

            let outcomes = evaluate cfg

            let hasRule12Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "ValidateIdToken = None"
                    | _ -> false)

            Expect.isFalse hasRule12Warn "rule 12 is generic-specific — other presets opt out without surfacing"

        // ─── Rule 13 — google preset + non-Google issuer ───────

        testCase "Rule 13: google preset + a pasted non-Google issuer → WARN"
        <| fun () ->
            let cfg = google "client-id" "https://app/cb"

            let pasted = {
                cfg with
                    Issuer = "https://mytenant.auth0.com/"
            }

            let outcomes = evaluate pasted
            Expect.isTrue (hasWarningMatching "Preset `google` declared" outcomes) ""

        testCase "Rule 13: google preset + the fixed Google issuer → no rule-13 warning"
        <| fun () ->
            let outcomes = evaluate (google "client-id" "https://app/cb")

            let hasRule13Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `google` declared"
                    | _ -> false)

            Expect.isFalse hasRule13Warn ""

        testCase "Rule 13: trailing slash tolerated (discovery normalises it)"
        <| fun () ->
            // A hand-edited config carrying `.../` is not a
            // misconfiguration — refusing it would report a
            // non-problem to an operator who has nothing to fix.
            let cfg = google "client-id" "https://app/cb"

            let slashed = {
                cfg with
                    Issuer = "https://accounts.google.com/"
            }

            let outcomes = evaluate slashed

            let hasRule13Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `google` declared"
                    | _ -> false)

            Expect.isFalse hasRule13Warn ""

        testCase "Rule 11 renders the Google issuer form + the opaque-token expectation"
        <| fun () ->
            // The preset's per-provider knowledge has to reach the
            // boot log / validators panel, not merely exist on the
            // DU — this is where an operator sees it.
            let outcomes = evaluate (google "client-id" "https://app/cb")

            Expect.isTrue (hasOkMatching "preset `google` applied" outcomes) ""
            Expect.isTrue (hasOkMatching "https://accounts.google.com" outcomes) "issuer form must render"

            Expect.isTrue
                (hasOkMatching "expects decodable access token: false" outcomes)
                "the opaque-access-token fact must render alongside the issuer form"

        // ─── Happy path ─────────────────────────────────────────

        testCase "happy path: valid cfg + no preset → no errors or warnings"
        <| fun () ->
            let outcomes = evaluate validCfg
            let errors = outcomes |> List.filter RuleOutcome.isError
            let warnings = outcomes |> List.filter RuleOutcome.isWarning
            Expect.isEmpty errors "no errors expected"
            Expect.isEmpty warnings "no warnings expected"

        testCase "happy path: preset + matching cfg → only Rule 11 Ok-info outcome"
        <| fun () ->
            let cfg = entraWorkforce "tenant-guid" "client-id" "https://app/cb"
            let outcomes = evaluate cfg
            let errors = outcomes |> List.filter RuleOutcome.isError
            let warnings = outcomes |> List.filter RuleOutcome.isWarning
            Expect.isEmpty errors ""
            Expect.isEmpty warnings ""
            Expect.isTrue (hasOkMatching "preset `entra-workforce` applied" outcomes) ""

        // ─── Aggregation behaviour ──────────────────────────────

        testList "aggregation (IConfigValidator.Validate result)" [
            testCase "all errors → ValidationResult.Error"
            <| fun () ->
                let cfg = {
                    validCfg with
                        Issuer = ""
                        ClientId = ""
                }

                let v = OidcCoherenceValidator(cfg) :> IConfigValidator
                let result = v.Validate() |> Async.RunSynchronously

                match result with
                | Error msg ->
                    Expect.stringContains msg "refused startup" ""
                    Expect.stringContains msg "Issuer is empty" ""
                    Expect.stringContains msg "ClientId is empty" ""
                | _ -> failtestf "expected Error, got %A" result

            testCase "warnings only → ValidationResult.Warning"
            <| fun () ->
                let cfg = {
                    validCfg with
                        Issuer = "http://issuer.example.test"
                }

                let v = OidcCoherenceValidator(cfg) :> IConfigValidator
                let result = v.Validate() |> Async.RunSynchronously

                match result with
                | Warning msg -> Expect.stringContains msg "warnings:" ""
                | _ -> failtestf "expected Warning, got %A" result

            testCase "no findings → ValidationResult.Ok"
            <| fun () ->
                let v = OidcCoherenceValidator(validCfg) :> IConfigValidator
                let result = v.Validate() |> Async.RunSynchronously
                Expect.equal result ValidationResult.Ok ""

            testCase "Error trumps Warning when both present"
            <| fun () ->
                let cfg = {
                    validCfg with
                        Issuer = ""
                        RedirectUri = "http://app.example.test/cb"
                }

                let v = OidcCoherenceValidator(cfg) :> IConfigValidator
                let result = v.Validate() |> Async.RunSynchronously

                match result with
                | Error _ -> ()
                | _ -> failtestf "expected Error (trumps Warning), got %A" result

            testCase "validator Name = \"oidc-coherence\""
            <| fun () ->
                let v = OidcCoherenceValidator(validCfg) :> IConfigValidator
                Expect.equal v.Name "oidc-coherence" ""
        ]
    ]