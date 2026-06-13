module ToolUp.Platform.Tests.InProcess.SecureByDefaultValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 129 — secure-by-default refusals / warnings ──────────────

let private run (v: IConfigValidator) : ValidationResult = v.Validate() |> Async.RunSynchronously

// ── 129a — AutoBootstrapDevAdmin: Warning local, Error internet-facing ──
let private devAdminTests =
    let validator cfg =
        AutoBootstrapDevAdminModeValidator.AutoBootstrapDevAdminModeValidator(cfg) :> IConfigValidator

    testList "Phase 129a — AutoBootstrapDevAdmin escalation" [
        test "internet-facing (RequireHttps) + auth + dev-admin set → Error" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = true
                    AutoBootstrapDevAdmin = Some "dev-1"
            }

            match run (validator cfg) with
            | Error _ -> ()
            | other -> failtestf "expected Error on internet-facing shape, got %A" other
        }

        test "pure-local (no https / forwarded) + auth + dev-admin set → Warning" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    AutoBootstrapDevAdmin = Some "dev-1"
            }

            match run (validator cfg) with
            | Warning _ -> ()
            | other -> failtestf "expected Warning on pure-local dev shape, got %A" other
        }

        test "dev-admin unset → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    RequireHttps = true
            }

            Expect.equal (run (validator cfg)) Ok "no dev-admin → no finding"
        }
    ]

// ── 129c/129d — CSRF off-by-default under cookie auth (Error + opt-out) ──
let private csrfDefaultTests =
    let validator cfg =
        CsrfDefaultModeValidator.CsrfDefaultModeValidator(cfg) :> IConfigValidator

    testList "Phase 129d — CSRF default mode" [
        test "auth + CookieRequired + NoSecurityHardening → Error (refuse startup)" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = CookieRequired
                    SecurityHardening = NoSecurityHardening
            }

            match run (validator cfg) with
            | Error msg ->
                Expect.stringContains
                    msg
                    "AcceptSameSiteOnlyCsrfWhenAuthRequired"
                    "names the typed acknowledged-downgrade escape hatch"
            | other -> failtestf "expected CSRF-exposure Error, got %A" other
        }

        test "auth + CookieRequired + NoSecurityHardening + acknowledged → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = CookieRequired
                    SecurityHardening = NoSecurityHardening
                    AcceptSameSiteOnlyCsrfWhenAuthRequired = true
            }

            Expect.equal
                (run (validator cfg))
                Ok
                "explicit SameSite-only acknowledgement → conscious downgrade, no refusal"
        }

        test "auth + CookieRequired + DefaultSecurityHardening → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = CookieRequired
                    SecurityHardening = DefaultSecurityHardening
            }

            Expect.equal (run (validator cfg)) Ok "hardening enabled → no exposure"
        }

        test "auth + QueryParamFallback (not cookie auth) → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = QueryParamFallback
                    SecurityHardening = NoSecurityHardening
            }

            Expect.equal (run (validator cfg)) Ok "no cookie auth → no CSRF exposure here"
        }

        test "anonymous + CookieRequired + NoSecurityHardening → Ok (no auth surface)" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.anonymous
                    SseAuthMode = CookieRequired
                    SecurityHardening = NoSecurityHardening
            }

            Expect.equal (run (validator cfg)) Ok "no authenticated surface → gatedAuthValidation short-circuits"
        }
    ]

// ── 129b — OAuth redirect-base: Error in auth modes, Warning anonymous ──
let private withRedirectBaseUnset (body: unit -> unit) =
    let saved = Environment.GetEnvironmentVariable "TOOLUP_OAUTH_REDIRECT_BASE"

    try
        Environment.SetEnvironmentVariable("TOOLUP_OAUTH_REDIRECT_BASE", null)
        body ()
    finally
        Environment.SetEnvironmentVariable("TOOLUP_OAUTH_REDIRECT_BASE", saved)

let private redirectBaseTests =
    let validator cfg =
        OAuthFlowValidator.OAuthFlowValidator(cfg) :> IConfigValidator

    testSequenced
    <| testList "Phase 129b — OAuth redirect-base" [
        test "unset + auth mode → Error" {
            withRedirectBaseUnset (fun () ->
                let cfg = {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.individual
                }

                match run (validator cfg) with
                | Error _ -> ()
                | other -> failtestf "expected Error (host-poisoning) in auth mode, got %A" other)
        }

        test "unset + anonymous mode → Warning" {
            withRedirectBaseUnset (fun () ->
                let cfg = {
                    ServerConfig.defaults with
                        Surfaces = Surfaces.anonymous
                }

                match run (validator cfg) with
                | Warning _ -> ()
                | other -> failtestf "expected Warning in anonymous mode, got %A" other)
        }
    ]

[<Tests>]
let tests =
    testList "Phase 129 — secure-by-default" [ devAdminTests; csrfDefaultTests; redirectBaseTests ]