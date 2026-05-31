// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcCoherenceValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.AuthProviders.Oidc.OidcPresets
open ToolUp.AuthProviders.Oidc.OidcCoherenceValidator

// ─── OidcCoherenceValidator ──────────────────────────────────────────
//
// Per-rule coverage of the 11 rules. Each test asserts the expected
// per-rule outcome from `evaluate` rather than parsing the
// aggregated `ValidationResult` message — `evaluate` is the
// structured surface the future `/dev/inspect` validators panel
// reads, and pinning rule-level behaviour here keeps the
// aggregator's wording flexible.
//
// Aggregation behaviour itself (Error wins, Warning next, Ok else)
// is verified by a separate small test list at the bottom.

let private validCfg: OidcUIConfig = {
    Issuer = "https://issuer.example.test"
    ClientId = "test-client-id"
    RedirectUri = "https://app.example.test/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = None
    ValidateIdToken = None
}

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
            let outcomes = evaluate cfg None
            Expect.isTrue (hasErrorMatching "OidcUIConfig.Issuer is empty" outcomes) ""

        testCase "Rule 1: whitespace Issuer → ERROR"
        <| fun () ->
            let cfg = { validCfg with Issuer = "   " }
            let outcomes = evaluate cfg None
            Expect.isTrue (hasErrorMatching "OidcUIConfig.Issuer is empty" outcomes) ""

        // ─── Rule 2 — ClientId empty ────────────────────────────

        testCase "Rule 2: empty ClientId → ERROR"
        <| fun () ->
            let cfg = { validCfg with ClientId = "" }
            let outcomes = evaluate cfg None
            Expect.isTrue (hasErrorMatching "OidcUIConfig.ClientId is empty" outcomes) ""

        // ─── Rule 3 — RedirectUri empty ─────────────────────────

        testCase "Rule 3: empty RedirectUri → ERROR"
        <| fun () ->
            let cfg = { validCfg with RedirectUri = "" }
            let outcomes = evaluate cfg None
            Expect.isTrue (hasErrorMatching "OidcUIConfig.RedirectUri is empty" outcomes) ""

        // ─── Rule 4 — `openid` scope missing ────────────────────

        testCase "Rule 4: Scopes without `openid` → ERROR (OIDC spec)"
        <| fun () ->
            let cfg = {
                validCfg with
                    Scopes = [ "profile"; "email" ]
            }

            let outcomes = evaluate cfg None
            Expect.isTrue (hasErrorMatching "does not contain `openid`" outcomes) ""

        testCase "Rule 4: empty Scopes → ERROR (no openid)"
        <| fun () ->
            let cfg = { validCfg with Scopes = [] }
            let outcomes = evaluate cfg None
            Expect.isTrue (hasErrorMatching "does not contain `openid`" outcomes) ""

        // ─── Rule 5 — Issuer not HTTPS ──────────────────────────

        testCase "Rule 5: http:// Issuer (non-localhost) → WARN"
        <| fun () ->
            let cfg = {
                validCfg with
                    Issuer = "http://issuer.example.test"
            }

            let outcomes = evaluate cfg None

            Expect.isTrue
                (hasWarningMatching "OidcUIConfig.Issuer" outcomes
                 && hasWarningMatching "is not `https://`" outcomes)
                "non-https issuer must warn"

        testCase "Rule 5: http://localhost Issuer → no warning (dev exception)"
        <| fun () ->
            let cfg = {
                validCfg with
                    Issuer = "http://localhost:5000/auth"
            }

            let outcomes = evaluate cfg None
            // Make sure NO warning specifically about the Issuer
            // being non-https is present.
            let hasIssuerHttpsWarn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "OidcUIConfig.Issuer" && m.Contains "is not `https://`"
                    | _ -> false)

            Expect.isFalse hasIssuerHttpsWarn "localhost dev exception must NOT warn"

        testCase "Rule 5: http://127.0.0.1 Issuer → no warning"
        <| fun () ->
            let cfg = {
                validCfg with
                    Issuer = "http://127.0.0.1:5000"
            }

            let outcomes = evaluate cfg None

            let hasIssuerHttpsWarn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "OidcUIConfig.Issuer" && m.Contains "is not `https://`"
                    | _ -> false)

            Expect.isFalse hasIssuerHttpsWarn ""

        // ─── Rule 6 — RedirectUri not HTTPS ─────────────────────

        testCase "Rule 6: http:// RedirectUri (non-localhost) → WARN"
        <| fun () ->
            let cfg = {
                validCfg with
                    RedirectUri = "http://app.example.test/cb"
            }

            let outcomes = evaluate cfg None

            let hasRedirectHttpsWarn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "OidcUIConfig.RedirectUri" && m.Contains "is not `https://`"
                    | _ -> false)

            Expect.isTrue hasRedirectHttpsWarn ""

        testCase "Rule 6: http://localhost RedirectUri → no warning (dev exception)"
        <| fun () ->
            let cfg = {
                validCfg with
                    RedirectUri = "http://localhost:8080/auth/callback"
            }

            let outcomes = evaluate cfg None

            let hasRedirectHttpsWarn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "OidcUIConfig.RedirectUri" && m.Contains "is not `https://`"
                    | _ -> false)

            Expect.isFalse hasRedirectHttpsWarn ""

        // ─── Rule 7 — entra-workforce + non-workforce issuer ────

        testCase "Rule 7: workforce preset + non-workforce issuer → WARN"
        <| fun () ->
            // Preset declared but consumer overrode Issuer to point
            // at External ID — paste-mistake the validator catches.
            let presetCfg, meta = entraWorkforce "tenant-guid" "client-id" "https://app/cb"

            let badIssuerCfg = {
                presetCfg with
                    Issuer = "https://mytenant.ciamlogin.com/mytenant/v2.0"
            }

            let outcomes = evaluate badIssuerCfg (Some meta)
            Expect.isTrue (hasWarningMatching "Preset `entra-workforce` declared" outcomes) ""

        testCase "Rule 7: workforce preset + matching issuer → no rule-7 warning"
        <| fun () ->
            let cfg, meta = entraWorkforce "tenant-guid" "client-id" "https://app/cb"
            let outcomes = evaluate cfg (Some meta)

            let hasRule7Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `entra-workforce` declared"
                    | _ -> false)

            Expect.isFalse hasRule7Warn ""

        // ─── Rule 8 — entra-external-id + non-CIAM issuer ──────

        testCase "Rule 8: external-id preset + workforce-shaped issuer → WARN"
        <| fun () ->
            let presetCfg, meta = entraExternalId "mytenant" "client-id" "https://app/cb"

            let badIssuerCfg = {
                presetCfg with
                    Issuer = "https://login.microsoftonline.com/tenant-guid/v2.0"
            }

            let outcomes = evaluate badIssuerCfg (Some meta)
            Expect.isTrue (hasWarningMatching "Preset `entra-external-id` declared" outcomes) ""

        testCase "Rule 8: external-id preset + ciamlogin issuer → no rule-8 warning"
        <| fun () ->
            let cfg, meta = entraExternalId "mytenant" "client-id" "https://app/cb"
            let outcomes = evaluate cfg (Some meta)

            let hasRule8Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `entra-external-id` declared"
                    | _ -> false)

            Expect.isFalse hasRule8Warn ""

        testCase "Rule 8: external-id preset + custom-domain issuer → no rule-8 warning"
        <| fun () ->
            // Custom-domain v2.0 issuer should be accepted by the
            // looksLikeCiam heuristic (contains /v2.0 + NOT workforce).
            let cfg, meta =
                entraExternalIdWithDomain "mytenant" "login.mybrand.com" "client-id" "https://app/cb"

            let outcomes = evaluate cfg (Some meta)

            let hasRule8Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `entra-external-id` declared"
                    | _ -> false)

            Expect.isFalse hasRule8Warn "custom-domain CIAM must satisfy the rule-8 heuristic"

        // ─── Rule 9 — auth0 + Microsoft-shaped issuer ──────────

        testCase "Rule 9: auth0 preset + Microsoft issuer → WARN"
        <| fun () ->
            let _, meta = auth0 "mytenant.auth0.com" "client-id" "https://app/cb"

            let pasted = {
                validCfg with
                    Issuer = "https://login.microsoftonline.com/tenant/v2.0"
            }

            let outcomes = evaluate pasted (Some meta)
            Expect.isTrue (hasWarningMatching "Preset `auth0` declared" outcomes) ""

        testCase "Rule 9: auth0 preset + auth0 issuer → no rule-9 warning"
        <| fun () ->
            let cfg, meta = auth0 "mytenant.auth0.com" "client-id" "https://app/cb"
            let outcomes = evaluate cfg (Some meta)

            let hasRule9Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "Preset `auth0` declared"
                    | _ -> false)

            Expect.isFalse hasRule9Warn ""

        // ─── Rule 10 — preset auto-added scopes dropped ────────

        testCase "Rule 10: workforce preset + Scopes missing the api://...access_as_user → WARN"
        <| fun () ->
            // The single most dangerous regression — consumer
            // override of Scopes dropped the load-bearing scope.
            let presetCfg, meta = entraWorkforce "tenant-guid" "client-id" "https://app/cb"

            let stripped = {
                presetCfg with
                    Scopes = [ "openid"; "profile"; "email" ]
            }

            let outcomes = evaluate stripped (Some meta)

            Expect.isTrue
                (hasWarningMatching "AutoAddedScopes" outcomes
                 && hasWarningMatching "access_as_user" outcomes)
                "missing api scope must surface as a warning that names it"

        testCase "Rule 10: workforce preset + intact Scopes → no rule-10 warning"
        <| fun () ->
            let cfg, meta = entraWorkforce "tenant-guid" "client-id" "https://app/cb"
            let outcomes = evaluate cfg (Some meta)

            let hasRule10Warn =
                outcomes
                |> List.exists (function
                    | RuleWarning m -> m.Contains "AutoAddedScopes"
                    | _ -> false)

            Expect.isFalse hasRule10Warn ""

        testCase "Rule 10: External-ID preset + dropped offline_access → WARN"
        <| fun () ->
            let presetCfg, meta = entraExternalId "mytenant" "client-id" "https://app/cb"

            let stripped = {
                presetCfg with
                    Scopes = [ "openid"; "profile"; "email" ]
            }

            let outcomes = evaluate stripped (Some meta)

            Expect.isTrue
                (hasWarningMatching "AutoAddedScopes" outcomes
                 && hasWarningMatching "offline_access" outcomes)
                ""

        // ─── Rule 11 — preset-applied provenance (Ok) ──────────

        testCase "Rule 11: preset supplied → Ok-class outcome records the name"
        <| fun () ->
            let cfg, meta = entraWorkforce "tenant-guid" "client-id" "https://app/cb"
            let outcomes = evaluate cfg (Some meta)
            Expect.isTrue (hasOkMatching "preset `entra-workforce` applied" outcomes) ""

        testCase "Rule 11: no preset → no Ok-info outcome"
        <| fun () ->
            let outcomes = evaluate validCfg None

            let hasPresetOk =
                outcomes
                |> List.exists (function
                    | RuleOk m -> m.Contains "preset"
                    | _ -> false)

            Expect.isFalse hasPresetOk "without preset metadata, no provenance Ok message emitted"

        // ─── Happy path ─────────────────────────────────────────

        testCase "happy path: valid cfg + no preset → no errors or warnings"
        <| fun () ->
            let outcomes = evaluate validCfg None
            let errors = outcomes |> List.filter RuleOutcome.isError
            let warnings = outcomes |> List.filter RuleOutcome.isWarning
            Expect.isEmpty errors "no errors expected"
            Expect.isEmpty warnings "no warnings expected"

        testCase "happy path: preset + matching cfg → only Rule 11 Ok-info outcome"
        <| fun () ->
            let cfg, meta = entraWorkforce "tenant-guid" "client-id" "https://app/cb"
            let outcomes = evaluate cfg (Some meta)
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

                let v = OidcCoherenceValidator(cfg, None) :> IConfigValidator
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
                } // rule 5 warn

                let v = OidcCoherenceValidator(cfg, None) :> IConfigValidator
                let result = v.Validate() |> Async.RunSynchronously

                match result with
                | Warning msg -> Expect.stringContains msg "warnings:" ""
                | _ -> failtestf "expected Warning, got %A" result

            testCase "no findings → ValidationResult.Ok"
            <| fun () ->
                let v = OidcCoherenceValidator(validCfg, None) :> IConfigValidator
                let result = v.Validate() |> Async.RunSynchronously
                Expect.equal result ValidationResult.Ok ""

            testCase "Error trumps Warning when both present"
            <| fun () ->
                // An empty Issuer (Error) AND http://-redirect-uri
                // (Warning) — aggregate must surface Error, not
                // Warning. Production deployments rely on this
                // priority to abort startup on hard misconfig.
                let cfg = {
                    validCfg with
                        Issuer = ""
                        RedirectUri = "http://app.example.test/cb"
                }

                let v = OidcCoherenceValidator(cfg, None) :> IConfigValidator
                let result = v.Validate() |> Async.RunSynchronously

                match result with
                | Error _ -> ()
                | _ -> failtestf "expected Error (trumps Warning), got %A" result

            testCase "validator Name = \"oidc-coherence\""
            <| fun () ->
                let v = OidcCoherenceValidator(validCfg, None) :> IConfigValidator
                Expect.equal v.Name "oidc-coherence" ""
        ]
    ]