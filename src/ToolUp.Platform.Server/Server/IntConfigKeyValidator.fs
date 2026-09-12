module ToolUp.Platform.IntConfigKeyValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 465 — the format and range gate over every int-valued key ─
//
// Investigate-gaps G9: several numeric knobs were read with no format
// check at all, so an operator's mistake did not surface as a mistake.
// `TOOLUP_MAX_REQUEST_BODY_BYTES=1MB` was warned about once by
// `envInt64Opt` and then DROPPED — leaving Kestrel's 30 MB default in
// place, which is the opposite of what setting a cap asked for — and
// `TOOLUP_MAX_FILE_BYTES=1MB` fell back with no message whatsoever. In
// both cases the deployment boots, reports healthy, and enforces a
// limit nobody chose. That is the worst shape a config defect can take:
// it is invisible until the day the limit matters.
//
// **One validator over the registry, not one per knob.** Since Phases
// 696/698 every reader resolves through `ConfigResolution` against a
// `ConfigKeyDescriptor` whose `Type` already says `IntKey`, so the
// question "is this value a number?" is answerable once, for all of
// them, from the registry — and a new int key inherits the check by
// being registered rather than by someone remembering to write a
// validator. The per-key contract (range, accepted tokens, numeric
// form) is declared beside the descriptors as `ConfigKeys.intKeyRules`;
// this module is only the enforcement half. The generated configuration
// reference renders its Type column from those same rows, so the range
// an operator is refused against is the range the table showed them.
//
// **It reads the RAW value through the resolution seam, deliberately.**
// By the time the preflight runs, `ServerConfig.fromEnv` has already
// parsed — and already silently discarded — the bad value, so a
// validator that inspected the composed `ServerConfig` would see the
// default and report nothing. Reading the seam also means the check
// covers the manifest and profile lanes, and can name which one
// supplied the value.
//
// Severity: Error. A dropped cap is not a degraded mode — it is a
// deployment enforcing a limit the operator did not choose and cannot
// see. Refusing at preflight, naming the key, the value, the layer it
// came from and the accepted form, is strictly better than booting.

/// Redacted stand-in for a secret key's value. No int key is secret
/// today; the guard is here because a refusal quotes the raw value, and
/// a preflight message is written to the startup log.
[<Literal>]
let private redacted = "<redacted>"

/// The `Int64.TryParse` / `Double.TryParse` split the registry actually
/// has — mirroring each reader rather than imposing one form on all of
/// them, so the validator never refuses a value its reader honours.
let private parses (form: ConfigKeys.IntKeyNumericForm) (raw: string) : bool =
    match form with
    | ConfigKeys.IntegerOnly -> fst (Int64.TryParse raw)
    | ConfigKeys.IntegerOrDecimal -> fst (Double.TryParse raw)

/// The problem with `raw` under `rule`, or `None` when it is acceptable.
/// Exposed for the test suite, which asserts the message names the key,
/// the value and the accepted form.
let problemWith (rule: ConfigKeys.IntKeyRule) (isSecret: bool) (source: string) (raw: string) : string option =
    let shown = if isSecret then redacted else raw

    let accepted =
        match ConfigKeys.IntKeyRule.describe rule with
        | Some contract -> sprintf "a whole number (%s)" contract
        | None -> "a whole number"

    let tokenMatch =
        rule.AcceptedTokens
        |> List.exists (fun t -> String.Equals(t, raw.Trim(), StringComparison.OrdinalIgnoreCase))

    if tokenMatch then
        None
    elif not (parses rule.Form (raw.Trim())) then
        Some(
            sprintf
                "%s = %s (from %s) is not a number. Accepted: %s. The reader cannot use this value, so without this refusal it would be discarded and the deployment would run on the default instead — enforcing a setting nobody chose. Unset the variable if the default is what you want."
                rule.EnvVar
                shown
                source
                accepted
        )
    else
        // Range is checked on the integer reading only. A key that
        // declares bounds is a key whose reader takes a whole number;
        // the one fractional key in the registry declares none.
        match Int64.TryParse(raw.Trim()) with
        | true, n ->
            match rule.Min, rule.Max with
            | Some lo, _ when n < lo ->
                Some(
                    sprintf
                        "%s = %s (from %s) is below the accepted minimum of %d. Accepted: %s. A value this small is almost always a unit mistake — the key is in bytes, so 1 MB is 1048576, not 1."
                        rule.EnvVar
                        shown
                        source
                        lo
                        accepted
                )
            | _, Some hi when n > hi ->
                Some(
                    sprintf
                        "%s = %s (from %s) is above the accepted maximum of %d. Accepted: %s. Past this ceiling the value is not a limit the platform can honour; if the deployment genuinely moves objects this large, move them through the blob store's own path rather than the request body."
                        rule.EnvVar
                        shown
                        source
                        hi
                        accepted
                )
            | _ -> None
        | _ -> None

/// Phase 465 — refuses startup when any registered `IntKey` carries a
/// value that is not a number, or that falls outside the range the key
/// declares. Reads through the config-resolution seam, so it covers the
/// environment, the manifest and the selected profile alike.
type IntConfigKeyValidator(?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "int-config-keys"
        member _.Timeout = timeout

        member _.Validate() = async {
            let problems =
                ConfigKeys.all
                |> List.filter (fun k -> k.Type = ConfigKeys.IntKey)
                // A build / test / analyzer key is not read by a running
                // server, so a leftover value on a development machine
                // must not refuse its boot — the same exclusion the
                // unknown-key guard makes, and for the same reason.
                |> List.filter (fun k -> not (ConfigKeys.isToolingKey k.EnvVar))
                |> List.choose (fun k ->
                    match ConfigResolution.tryResolve k.EnvVar with
                    | None -> None
                    | Some(raw, source) ->
                        problemWith
                            (ConfigKeys.intKeyRuleFor k.EnvVar)
                            k.IsSecret
                            (ConfigResolution.ConfigSource.label source)
                            raw)

            match problems with
            | [] -> return Ok
            | _ ->
                return
                    Error(
                        sprintf
                            "Numeric configuration values the readers cannot use: %s Run --print-config to see every key's effective value and the layer it came from."
                            (problems |> List.map (fun p -> p + ".") |> String.concat " ")
                    )
        }