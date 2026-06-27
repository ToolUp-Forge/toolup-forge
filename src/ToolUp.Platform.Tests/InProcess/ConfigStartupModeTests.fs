module ToolUp.Platform.Tests.InProcess.ConfigStartupModeTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator
open ToolUp.Platform.ConfigKeys
open ToolUp.Platform.StartupModes

// ─── Phase 214 — --print-config / --validate-config startup modes ─────
//
// Covers the flag detection, the effective-config redaction, the
// validation summary rendering, and the exit-code contract that the
// `SDK.Server.compose` tail relies on:
//
//   * a good config → `runConfigPreflight` / `validate` returns without
//     throwing → compose calls `exit 0`.
//   * a bad config  → `validate` raises `ConfigPreflightFailedException`
//     → it escapes compose / run / main and the process exits non-zero,
//     having bound no listener.

let private mkValidator (name: string) (body: unit -> Async<ValidationResult>) =
    { new IConfigValidator with
        member _.Name = name
        member _.Timeout = TimeSpan.FromSeconds 1.0
        member _.Validate() = body ()
    }

/// Run `body` with one env var temporarily set, restoring the prior value
/// (Expecto runs cases on a shared process, so leaking an env var would
/// contaminate a sibling test).
let private withEnv (name: string) (value: string option) (body: unit -> unit) =
    let prior = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

let tests =
    testList "ConfigStartupMode" [
        testCase "detect maps the flags to modes; anything else is NormalBoot"
        <| fun _ ->
            Expect.equal (detect [ "/path/app.dll"; "--print-config" ]) PrintConfig "print flag"
            Expect.equal (detect [ "app"; "--validate-config" ]) ValidateConfig "validate flag"
            Expect.equal (detect [ "app"; "--PRINT-CONFIG" ]) PrintConfig "flag is case-insensitive"
            Expect.equal (detect [ "app" ]) NormalBoot "no flag → NormalBoot"
            Expect.equal (detect [ "app"; "--serve"; "--port=5000" ]) NormalBoot "unknown args pass through"

            Expect.equal
                (detect [ "app"; "--print-config"; "--validate-config" ])
                PrintConfig
                "print wins when both present (never-fails action)"

        testCase "renderEffectiveConfig redacts set secrets and shows set/unset non-secrets"
        <| fun _ ->
            // A set secret → redacted (the literal value must not appear).
            withEnv Names.adminToken (Some "super-secret-token-value") (fun () ->
                // A set non-secret → shown verbatim.
                withEnv Names.logLevel (Some "Debug") (fun () ->
                    // An unset key → its default / unset marker.
                    withEnv Names.blobStorage None (fun () ->
                        let dump = renderEffectiveConfig all

                        Expect.isFalse
                            (dump.Contains "super-secret-token-value")
                            "a set secret value must never appear in the dump"

                        Expect.stringContains dump "TOOLUP_ADMIN_TOKEN = <redacted>" "set secret is redacted"
                        Expect.stringContains dump "TOOLUP_LOG_LEVEL = Debug" "set non-secret shows its value"

                        Expect.stringContains
                            dump
                            "TOOLUP_BLOB_STORAGE = local  (default)"
                            "unset key shows its default")))

        testCase "effectiveValue: unset key with no default reads (unset)"
        <| fun _ ->
            let noDefault = all |> List.find (fun d -> d.Default.IsNone && not d.IsSecret)

            withEnv noDefault.EnvVar None (fun () ->
                Expect.equal (effectiveValue noDefault) "(unset)" "no env + no default → (unset)")

        testCase "--validate-config contract: good config → preflight returns, summary reports 0 errors"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(mkValidator "ok-a" (fun () -> async { return Ok }))
            |> ignore

            services.AddSingleton<IConfigValidator>(
                mkValidator "warn-b" (fun () -> async { return Warning "heads up" })
            )
            |> ignore

            // Same call compose's ValidateConfig arm makes (via
            // runConfigPreflight). A good/warning-only set does NOT throw.
            let outcomes = validate services None false
            let summary = renderValidationSummary outcomes

            Expect.stringContains summary "0 error(s)" "good config reports zero errors → exit 0"
            Expect.stringContains summary "[OK]    ok-a" "ok validator listed"
            Expect.stringContains summary "[WARN]  warn-b" "warning validator listed"

        testCase "--validate-config contract: bad config → preflight raises (the non-zero-exit path)"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(mkValidator "ok-a" (fun () -> async { return Ok }))
            |> ignore

            services.AddSingleton<IConfigValidator>(
                mkValidator "bad-b" (fun () -> async { return Error "missing required secret" })
            )
            |> ignore

            // The throw is exactly what escapes compose / run / main to
            // produce a non-zero process exit without booting.
            Expect.throwsT<ConfigPreflightFailedException>
                (fun () -> validate services None false |> ignore)
                "an Error-returning validator aborts validate, yielding the non-zero exit"
    ]