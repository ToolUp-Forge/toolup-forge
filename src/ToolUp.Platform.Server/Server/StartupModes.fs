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

/// Phase 696 — modifier for `--print-config`: print only the keys some
/// layer actually supplied a value for, dropping every key still sitting
/// on its declared default. On a surface of several hundred keys that is
/// the difference between a dump an operator scrolls past and a review
/// artefact they can read.
///
/// A modifier rather than a mode of its own: it never changes what is
/// resolved, only how much of it is printed, and `detect` stays a total
/// function over argv with the same four answers it had before.
[<Literal>]
let DiffFlag = "--diff"

/// Whether `--diff` accompanies `--print-config` in this argv.
let diffRequested (argv: string seq) : bool =
    argv
    |> Seq.exists (fun a -> String.Equals(a, DiffFlag, StringComparison.OrdinalIgnoreCase))

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

/// The effective value of one config key for `--print-config`: whatever
/// the resolution seam supplies (an environment variable, else the
/// deployment configuration manifest), else the declared default tagged
/// `(default)`, else `(unset)`. Secrets that are *set* are redacted; an
/// unset secret shows its (non-sensitive) default / unset marker so the
/// operator still sees whether it took effect.
let effectiveValue (d: ConfigKeyDescriptor) : string =
    match ConfigResolution.tryValue d.EnvVar with
    | None ->
        match d.Default with
        | Some def -> sprintf "%s  (default)" def
        | None -> "(unset)"
    | Some _ when d.IsSecret -> RedactedMarker
    | Some v -> v

/// Phase 696 — the value and the layer that supplied it. Provenance
/// answers the question `--print-config` was always really being asked:
/// not "what is this set to" but "why is it set to that".
///
/// Only the two layers this process can observe are reported — a
/// consumer literal never traverses a reader at all, and an overrides
/// record is applied above the seam by the reader that owns it, so
/// neither is visible from here. Saying `default` where a literal in
/// fact won would be a false claim, so the trailing note on the report
/// says plainly what the column does and does not cover.
let effectiveEntry (d: ConfigKeyDescriptor) : string * ConfigResolution.ConfigSource =
    effectiveValue d, ConfigResolution.sourceOf d.EnvVar

/// Render the effective-config dump grouped by category (same section
/// order as the reference doc), each key carrying the layer its value
/// came from. Pure with respect to its argument so a test can assert on
/// it; it reads the installed manifest, which a test installs and clears.
///
/// `diffOnly` drops every key still sitting on its declared default,
/// leaving exactly the deployment's stated deviations from stock.
let renderConfigReport (diffOnly: bool) (keys: ConfigKeyDescriptor list) : string =
    let sb = StringBuilder()

    sb.AppendLine(
        if diffOnly then
            "── Effective configuration, non-defaults only (--print-config --diff) ──"
        else
            "── Effective configuration (--print-config) ──"
    )
    |> ignore

    sb.AppendLine "" |> ignore

    match ConfigResolution.snapshot () with
    | Some m ->
        sb.AppendLine(sprintf "Manifest: %s" m.Path) |> ignore
        sb.AppendLine(sprintf "Manifest sha256: %s" m.Hash) |> ignore
    | None ->
        sb.AppendLine "Manifest: none loaded (every value below came from the environment or a declared default)."
        |> ignore

    sb.AppendLine "" |> ignore

    let shown =
        if diffOnly then
            keys
            |> List.filter (fun k -> ConfigResolution.sourceOf k.EnvVar <> ConfigResolution.DefaultConfigSource)
        else
            keys

    let orderedCategories =
        shown
        |> List.fold
            (fun acc k ->
                if List.contains k.Category acc then
                    acc
                else
                    acc @ [ k.Category ])
            []

    if shown.IsEmpty then
        sb.AppendLine "  (no key is set by any layer — this deployment runs entirely on declared defaults.)"
        |> ignore

        sb.AppendLine "" |> ignore

    for category in orderedCategories do
        sb.AppendLine(sprintf "[%s]" category) |> ignore

        for k in shown |> List.filter (fun k -> k.Category = category) |> List.sortBy _.EnvVar do
            let value, source = effectiveEntry k

            sb.AppendLine(sprintf "  %s = %s  [%s]" k.EnvVar value (ConfigResolution.ConfigSource.label source))
            |> ignore

        sb.AppendLine "" |> ignore

    sb.AppendLine "Secrets are shown as <redacted>. Values marked (default) are not set by any layer."
    |> ignore

    sb.AppendLine
        "The [source] column reports the layer this process resolved the value from: env, manifest, or default. A value written as a literal in composition-root code, or supplied by an overrides record, is applied above this seam and reads as 'default' here."
    |> ignore

    sb.ToString()

/// Render the full effective-config dump. Preserved shape for callers
/// that predate the `--diff` modifier.
let renderEffectiveConfig (keys: ConfigKeyDescriptor list) : string = renderConfigReport false keys

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

/// Print the effective config, honouring the `--diff` modifier (the
/// `--print-config` action as of Phase 696).
let printConfigReport (logger: ILogger) (diffOnly: bool) (keys: ConfigKeyDescriptor list) : unit =
    logger.Info(renderConfigReport diffOnly keys)
    Console.Out.Flush()

/// Print the validation summary (the success branch of
/// `--validate-config`; the failure branch never reaches here because
/// `ConfigValidatorAggregator.validate` raises on the first `Error`).
let printValidationSummary (logger: ILogger) (outcomes: ValidatorOutcome list) : unit =
    logger.Info(renderValidationSummary outcomes)
    Console.Out.Flush()