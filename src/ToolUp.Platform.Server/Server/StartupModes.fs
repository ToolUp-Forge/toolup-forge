// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.StartupModes

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.ConfigKeys
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Phase 214 — opt-in config-introspection startup modes ───────────
//
// Two CLI flags let an operator introspect a deployment's resolved config
// without booting the HTTP server:
//
//   --print-config     resolve every config key and print its effective
//                      value (env value or declared default), secrets
//                      redacted. Answers "why didn't my flag take effect?"
//                      Runs BEFORE preflight so it prints even when the
//                      config would fail validation.
//
//   --validate-config  run the ConfigValidatorAggregator preflight over
//                      every registered IConfigValidator (the
//                      ComposeConfigValidators first-party set + every
//                      companion validator) and exit 0/non-zero with the
//                      outcome summary, without binding Kestrel.
//
// Both are opt-in: when neither flag is present `current ()` is
// `NormalBoot` and the boot path is byte-for-byte unchanged (GP 13). The
// modes are wired at the tail of `SDK.Server.compose`, where the resolved
// `ServerConfig` + the fully-registered validator `IServiceCollection`
// are both in hand.

/// The startup mode selected by a CLI flag. `NormalBoot` is the default
/// and the only mode that binds a listener; the other two run an
/// introspection task and terminate the process without serving.
type StartupMode =
    | NormalBoot
    | PrintConfig
    | ValidateConfig
    /// Phase 686 — run the deployment verification report and exit with
    /// its exit code, without binding a listener. The CI-invokable form
    /// of the Platform-Admin read: one command, a rendered report on
    /// stdout, non-zero only when a COMPOSED section failed or would not
    /// answer.
    | VerifyDeployment

[<Literal>]
let PrintConfigFlag = "--print-config"

[<Literal>]
let ValidateConfigFlag = "--validate-config"

[<Literal>]
let VerifyDeploymentFlag = "--verify-deployment"

/// The actor recorded on a `--verify-deployment` audited read. A CI
/// invocation has no authenticated principal, and naming one would be a
/// claim the process cannot support — so the row says plainly which
/// surface the read came through.
[<Literal>]
let CliVerificationActor = "cli:--verify-deployment"

/// Detect the mode from a process argv. Pure + testable. Precedence when
/// more than one flag is somehow present: `--print-config`, then
/// `--validate-config`, then `--verify-deployment` — safest-and-never-
/// fails first, so a confused invocation degrades toward the action that
/// cannot report a false verdict. Any unrecognised argv ⇒ `NormalBoot`,
/// so the flags an SDK consumer's own composition root may already parse
/// pass straight through.
let detect (argv: string seq) : StartupMode =
    let has (flag: string) =
        argv
        |> Seq.exists (fun a -> String.Equals(a, flag, StringComparison.OrdinalIgnoreCase))

    if has PrintConfigFlag then PrintConfig
    elif has ValidateConfigFlag then ValidateConfig
    elif has VerifyDeploymentFlag then VerifyDeployment
    else NormalBoot

/// The live process mode, read from `Environment.GetCommandLineArgs()`.
/// (`GetCommandLineArgs()[0]` is the executable path — `detect` only
/// looks for the two flags, so the argv[0] entry is inert.)
let current () : StartupMode =
    detect (Environment.GetCommandLineArgs())

[<Literal>]
let private RedactedMarker = "<redacted>"

/// The effective value of one config key for `--print-config`: the env
/// value when set, else the declared default tagged `(default)`, else
/// `(unset)`. Secrets that are *set* are redacted; an unset secret shows
/// its (non-sensitive) default / unset marker so the operator still sees
/// whether it took effect.
let effectiveValue (d: ConfigKeyDescriptor) : string =
    match Environment.GetEnvironmentVariable d.EnvVar with
    | null
    | "" ->
        match d.Default with
        | Some def -> sprintf "%s  (default)" def
        | None -> "(unset)"
    | _ when d.IsSecret -> RedactedMarker
    | v -> v

/// Render the effective-config dump grouped by category (same section
/// order as the reference doc). Pure so a test can assert on it.
let renderEffectiveConfig (keys: ConfigKeyDescriptor list) : string =
    let sb = StringBuilder()
    sb.AppendLine "── Effective configuration (--print-config) ──" |> ignore
    sb.AppendLine "" |> ignore

    let orderedCategories =
        keys
        |> List.fold
            (fun acc k ->
                if List.contains k.Category acc then
                    acc
                else
                    acc @ [ k.Category ])
            []

    for category in orderedCategories do
        sb.AppendLine(sprintf "[%s]" category) |> ignore

        for k in keys |> List.filter (fun k -> k.Category = category) |> List.sortBy _.EnvVar do
            sb.AppendLine(sprintf "  %s = %s" k.EnvVar (effectiveValue k)) |> ignore

        sb.AppendLine "" |> ignore

    sb.AppendLine "Secrets are shown as <redacted>. Values marked (default) are not set in the environment."
    |> ignore

    sb.ToString()

/// Render the preflight outcome summary for `--validate-config`. Lists
/// every validator's status; pure for testability.
let renderValidationSummary (outcomes: ValidatorOutcome list) : string =
    let sb = StringBuilder()
    sb.AppendLine "── Config validation (--validate-config) ──" |> ignore

    if outcomes.IsEmpty then
        sb.AppendLine "  No config validators registered." |> ignore
    else
        for o in outcomes |> List.sortBy _.Name do
            match o.Result with
            | Ok -> sb.AppendLine(sprintf "  [OK]    %s (%dms)" o.Name o.ElapsedMs) |> ignore
            | Warning msg -> sb.AppendLine(sprintf "  [WARN]  %s — %s" o.Name msg) |> ignore
            | Error msg -> sb.AppendLine(sprintf "  [ERROR] %s — %s" o.Name msg) |> ignore

    let count status =
        outcomes
        |> List.filter (fun o -> ValidationResult.status o.Result = status)
        |> List.length

    sb.AppendLine(
        sprintf
            "  %d validator(s): %d ok, %d warning(s), %d error(s)."
            outcomes.Length
            (count "Ok")
            (count "Warning")
            (count "Error")
    )
    |> ignore

    sb.ToString()

/// Print the effective config to stdout (the `--print-config` action).
/// Uses the logger so the output rides the same stream as the rest of
/// startup, then flushes — the caller terminates the process immediately
/// after.
let printEffectiveConfig (logger: ILogger) (keys: ConfigKeyDescriptor list) : unit =
    logger.Info(renderEffectiveConfig keys)
    Console.Out.Flush()

/// Print the validation summary (the success branch of
/// `--validate-config`; the failure branch never reaches here because
/// `ConfigValidatorAggregator.validate` raises on the first `Error`).
let printValidationSummary (logger: ILogger) (outcomes: ValidatorOutcome list) : unit =
    logger.Info(renderValidationSummary outcomes)
    Console.Out.Flush()