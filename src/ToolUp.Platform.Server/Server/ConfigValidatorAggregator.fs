module ToolUp.Platform.ConfigValidatorAggregator

open System
open System.Text
open System.Threading
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 9m aggregator + snapshot ──────────────────────────────────
//
// Walks the `IServiceCollection` for every `AddSingleton<IConfigValidator>(instance)`
// registration, runs each in parallel with a per-validator timeout
// (capped at the 10s aggregator budget), captures outcomes for the
// `/dev/inspect` validators panel, and aborts startup if any returns
// `Error`. `ServerConfig.SkipPreflight = true` skips only the
// **external-probe** class — the validators that reach a dependency which
// may be down (storage sentinels, OIDC discovery, SMTP connects), which
// is the whole reason the lever exists. Two marker-opted classes always
// run and still abort on `Error`:
//
//   * `ISecurityClassValidator` — auth / secret / CSRF /
//     cross-instance-auth-state guards. Bypassing one is an
//     identity-spoofing or unauthenticated-access hole.
//   * `IStructuralClassValidator` (Phase 585) — in-process identity /
//     integrity invariants over the composed surface (duplicate component
//     ids, companion-slot legality, orphaned tool references). They cost
//     microseconds and touch nothing external, so they were never what
//     `SkipPreflight` was built to skip; riding through a dependency
//     outage is a legitimate operator choice, booting a composition whose
//     identities collide is not.
//
// The always-run set is derived from the validators themselves (a
// type-test for the two markers), not a name set the aggregator
// maintains, so a newly-authored security or structural validator cannot
// drift out of it.
//
// **Registration timing**: must be called near the END of `compose`,
// after every companion has had a chance to call
// `services.AddSingleton<IConfigValidator>(...)`. Calling it earlier
// means later registrations are silently ignored.
//
// **Companion contract**: registrations must use
// `AddSingleton<IConfigValidator>(instance)`. The aggregator reads
// `desc.ImplementationInstance` to invoke the validator at compose
// time. Constructor-injected impls cannot be inspected without
// building a separate `IServiceProvider`, which would create different
// singleton instances than the runtime container — failing loudly is
// safer than silently invoking a divergent validator.

/// Overall budget for the aggregator. Any per-validator declared
/// timeout above this is clamped down. Picked so the deploy pipeline
/// never blocks more than ten seconds on preflight even if every
/// validator stalls simultaneously.
let aggregatorBudget = TimeSpan.FromSeconds 10.0

/// One validator's recorded outcome for the snapshot service.
type ValidatorOutcome = {
    Name: string
    Result: ValidationResult
    ElapsedMs: int64
}

/// Snapshot of the most recent preflight run, surfaced to
/// `/dev/inspect` so an operator can confirm the deploy passed
/// preflight without reading the startup log.
type IPreflightSnapshot =
    abstract member LastRun: ValidatorOutcome list

/// Mutable holder backing `IPreflightSnapshot`. Set once at end of
/// `compose` after the validator run completes.
type PreflightSnapshot() =
    let mutable outcomes: ValidatorOutcome list = []
    member _.Set(o: ValidatorOutcome list) = outcomes <- o

    interface IPreflightSnapshot with
        member _.LastRun = outcomes

/// Thrown by `validate` when one or more validators returned `Error`
/// and `SkipPreflight = false`. Propagates through Kestrel
/// construction and crashes the process with non-zero exit code.
[<Sealed>]
type ConfigPreflightFailedException(summary: string) =
    inherit System.Exception(summary)

let private truncate (max: int) (s: string) =
    if isNull s then ""
    elif s.Length <= max then s
    else s.Substring(0, max)

/// Throw with a cryptic-startup-friendly message when two or more
/// validators share the same `Name`. Names are the dictionary key for
/// outcome aggregation and the `/dev/inspect` validators panel — a
/// collision silently overwrites whichever entry was registered last and
/// the operator sees only one of the two probes running, with no
/// diagnostic surface. Called from `validate` immediately after
/// `collectValidators` so the failure fires BEFORE the first `Validate`
/// invocation.
///
/// Surfaces the colliding validators' .NET type names so the operator
/// can grep `src/` for the registration sites. The interface itself
/// doesn't carry registration-site info, so the type name (or
/// `<unknown>` if the impl is anonymous, e.g. an object expression in a
/// test) is the closest stand-in available.
let assertUniqueValidatorNames (validators: IConfigValidator list) : unit =
    let collisions =
        validators
        |> List.groupBy _.Name
        |> List.filter (fun (_, vs) -> List.length vs > 1)

    if not collisions.IsEmpty then
        let renderOne (name, vs) =
            let typeNames =
                vs
                |> List.map (fun v ->
                    let t = v.GetType()

                    if isNull t.FullName then "<unknown>" else t.FullName)
                |> String.concat " + "

            sprintf
                "Compose-time defect: IConfigValidator name collision on \"%s\". Found in: %s. Validator names are dictionary-keyed in aggregation; a duplicate silently overwrites the prior entry. Rename one (and update the consumer-side `[<HostedConfigKey>]` if any depends on the name) or merge the two validators into one."
                name
                typeNames

        let summary = collisions |> List.map renderOne |> String.concat "\n"
        failwith summary

let private collectValidators (services: IServiceCollection) : IConfigValidator list =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
    |> Seq.map (fun desc ->
        match desc.ImplementationInstance with
        | :? IConfigValidator as v -> v
        | _ ->
            let implTypeName =
                if isNull desc.ImplementationType then
                    "<unknown>"
                else
                    desc.ImplementationType.FullName

            failwithf
                "IConfigValidator must be registered as an instance via services.AddSingleton<IConfigValidator>(instance). Descriptor for implementation type %s uses a factory or constructor-injected pattern that the aggregator cannot introspect at compose time. See TECHNICAL_GUIDE.md 'Companion authoring an IConfigValidator'."
                implTypeName)
    |> List.ofSeq

let private runOne (validator: IConfigValidator) : Async<ValidatorOutcome> = async {
    let stopwatch = System.Diagnostics.Stopwatch.StartNew()

    let effectiveTimeout =
        if validator.Timeout > aggregatorBudget then
            aggregatorBudget
        else
            validator.Timeout

    try
        try
            use cts = new CancellationTokenSource(effectiveTimeout)
            let probeTask = Async.StartImmediateAsTask(validator.Validate(), cts.Token)
            let! result = probeTask |> Async.AwaitTask
            stopwatch.Stop()

            return {
                Name = validator.Name
                Result = result
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }
        with
        | :? OperationCanceledException ->
            stopwatch.Stop()
            let ms = int effectiveTimeout.TotalMilliseconds

            return {
                Name = validator.Name
                Result = Error(sprintf "validator exceeded timeout (%dms)" ms)
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }
        | ex ->
            stopwatch.Stop()
            let msg = "validator threw: " + truncate 500 ex.Message

            return {
                Name = validator.Name
                Result = Error msg
                ElapsedMs = stopwatch.ElapsedMilliseconds
            }
    finally
        stopwatch.Stop()
}

let private formatSummary (outcomes: ValidatorOutcome list) : string =
    let sb = StringBuilder()
    sb.AppendLine("Config preflight failed — startup aborted.") |> ignore

    for o in outcomes do
        match o.Result with
        | Error msg -> sb.AppendLine(sprintf "  [ERROR] %s: %s" o.Name msg) |> ignore
        | _ -> ()

    let warnings =
        outcomes |> List.filter (fun o -> ValidationResult.status o.Result = "Warning")

    if not warnings.IsEmpty then
        sb.AppendLine("Warnings (non-blocking, included for context):") |> ignore

        for o in warnings do
            match o.Result with
            | Warning msg -> sb.AppendLine(sprintf "  [WARN] %s: %s" o.Name msg) |> ignore
            | _ -> ()

    // Phase 700 — name the imported posture on the refusal. A profile is
    // a CLAIM the preflight checks, so a refusal evaluated against a
    // combination a profile contributed to has to say which claim it is
    // about; otherwise the operator reads a message about keys they never
    // typed and concludes the refusal is about something else entirely.
    // Silent when no profile is in force, so an existing deployment's
    // refusal is byte-for-byte what it was (GP 11).
    ConfigResolution.profileContextLine ()
    |> Option.iter (fun line -> sb.AppendLine("").AppendLine(line) |> ignore)

    sb.ToString().TrimEnd()

let private logOutcome (logger: ILogger option) (o: ValidatorOutcome) =
    match logger with
    | None -> ()
    | Some l ->
        match o.Result with
        | Ok -> l.Info(sprintf "[preflight] %s: Ok (%dms)" o.Name o.ElapsedMs)
        | Warning msg -> l.Warn(sprintf "[preflight] %s: Warning — %s (%dms)" o.Name msg o.ElapsedMs)
        | Error msg -> l.Error(sprintf "[preflight] %s: Error — %s (%dms)" o.Name msg o.ElapsedMs, None)

/// Phase 585 — the preflight class of a registered validator, derived by
/// type-testing the opt-in markers rather than read from a name set the
/// aggregator maintains (a name set drifts the moment someone authors a
/// validator without updating it).
///
///   * `SecurityClass` — implements `ISecurityClassValidator`. Bypassing
///     it is an identity-spoofing, unauthenticated-access,
///     plaintext-secret, CSRF, or cross-instance-auth-state hole.
///   * `StructuralClass` — implements `IStructuralClassValidator`. A pure
///     in-process identity / integrity invariant over the composed
///     surface; microseconds, no external dependency.
///   * `ExternalProbeClass` — the unmarked default, and the only class
///     `ServerConfig.SkipPreflight` skips. These reach a dependency that
///     may be down, which is precisely what the emergency-boot lever
///     exists to ride through.
///
/// A validator carrying both markers reads as `SecurityClass`: the two
/// agree on the outcome that matters (always-run) and the security label
/// is the one an operator needs to see in the log.
type ValidatorClass =
    | SecurityClass
    | StructuralClass
    | ExternalProbeClass

/// Classify a validator from its markers. Unmarked ⇒ `ExternalProbeClass`,
/// which preserves every pre-marker validator's prior `SkipPreflight`
/// behaviour byte-for-byte (GP 11).
let classify (v: IConfigValidator) : ValidatorClass =
    match box v with
    | :? ISecurityClassValidator -> SecurityClass
    | :? IStructuralClassValidator -> StructuralClass
    | _ -> ExternalProbeClass

/// Whether a class runs regardless of `SkipPreflight`. One boolean must
/// never be the single switch that disables the auth-class guards or the
/// composition-integrity checks.
let alwaysRuns (cls: ValidatorClass) : bool =
    match cls with
    | SecurityClass
    | StructuralClass -> true
    | ExternalProbeClass -> false

let private classLabel (cls: ValidatorClass) =
    match cls with
    | SecurityClass -> "security-class"
    | StructuralClass -> "structural-class"
    | ExternalProbeClass -> "external-probe-class"

/// Run a validator list in parallel (per-validator + global timeout),
/// log each outcome, throw `ConfigPreflightFailedException` if any
/// returned `Error`, and return the outcomes.
let private runSet (logger: ILogger option) (validators: IConfigValidator list) : ValidatorOutcome list =
    let outcomes =
        validators
        |> List.map runOne
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.toList

    for o in outcomes do
        logOutcome logger o

    let errors =
        outcomes |> List.filter (fun o -> ValidationResult.status o.Result = "Error")

    if not errors.IsEmpty then
        raise (ConfigPreflightFailedException(formatSummary outcomes))

    outcomes

/// Walk `services` for every `IConfigValidator` registration, run each
/// in parallel with per-validator + global timeouts, log outcomes, and
/// throw `ConfigPreflightFailedException` if any returned `Error`.
///
/// `skipPreflight = true` skips only the **external-probe** class (the
/// emergency-boot lever for a companion probe whose dependency is down)
/// but still runs every `ISecurityClassValidator` and every
/// `IStructuralClassValidator`, and still aborts on their `Error` — a
/// single boolean must not silently disable the auth-class guards or the
/// composition-integrity checks. The skipped validators' names are
/// enumerated in the log so the bypass is visible at `Warn` level, not
/// just in the `/dev/inspect` panel, and the always-run set is listed
/// beside it with its class. Returns the outcomes so the caller can
/// populate the snapshot service.
let validate (services: IServiceCollection) (logger: ILogger option) (skipPreflight: bool) : ValidatorOutcome list =
    let validators = collectValidators services

    // Fire BEFORE any Validate call — a name collision otherwise
    // silently drops one probe's outcome in the dictionary-keyed
    // aggregation paths (e.g. /dev/inspect). Diagnostic surface > silent
    // overwrite.
    assertUniqueValidatorNames validators

    if skipPreflight then
        let classified = validators |> List.map (fun v -> classify v, v)

        let alwaysRun, skipped =
            classified |> List.partition (fun (cls, _) -> alwaysRuns cls)

        match logger with
        | Some l ->
            if skipped.IsEmpty then
                l.Warn "[preflight] ServerConfig.SkipPreflight = true. 0 external-probe-class validator(s) skipped."
            else
                let names =
                    skipped |> List.map (fun (_, v) -> v.Name) |> List.sort |> String.concat ", "

                l.Warn(
                    sprintf
                        "[preflight] ServerConfig.SkipPreflight = true. %d external-probe-class validator(s) skipped: %s"
                        skipped.Length
                        names
                )

            if not alwaysRun.IsEmpty then
                let alwaysNames =
                    alwaysRun
                    |> List.map (fun (cls, v) -> sprintf "%s [%s]" v.Name (classLabel cls))
                    |> List.sort
                    |> String.concat ", "

                l.Warn(
                    sprintf
                        "[preflight] %d validator(s) run despite SkipPreflight (not bypassable): %s"
                        alwaysRun.Length
                        alwaysNames
                )
        | None -> ()

        if alwaysRun.IsEmpty then
            []
        else
            runSet logger (alwaysRun |> List.map snd)
    elif validators.IsEmpty then
        // GP 13 — lightweight default. Zero-config deployments stay
        // green and start normally with no log noise.
        []
    else
        runSet logger validators