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
// non-security-class validators — `securityClassValidatorNames` (auth /
// secret / cross-instance-auth-state guards) always run and still abort
// on `Error`, so one boolean cannot silently disable the auth-class
// safety net.
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

    sb.ToString().TrimEnd()

let private logOutcome (logger: ILogger option) (o: ValidatorOutcome) =
    match logger with
    | None -> ()
    | Some l ->
        match o.Result with
        | Ok -> l.Info(sprintf "[preflight] %s: Ok (%dms)" o.Name o.ElapsedMs)
        | Warning msg -> l.Warn(sprintf "[preflight] %s: Warning — %s (%dms)" o.Name msg o.ElapsedMs)
        | Error msg -> l.Error(sprintf "[preflight] %s: Error — %s (%dms)" o.Name msg o.ElapsedMs, None)

/// Validators whose bypass is an identity-spoofing, unauthenticated-
/// access, plaintext-secret, or cross-instance-auth-state hole. These
/// run even when `ServerConfig.SkipPreflight = true`. `SkipPreflight`
/// is an emergency-boot lever for a noisy companion probe — it must
/// never be the single switch that silently disables every auth-class
/// guard at once. Keyed by the validator's `Name`, which
/// `IConfigValidator` defines as a stable identifier contract
/// (see `IConfigValidator.fs` — Name is the registration key).
let securityClassValidatorNames: Set<string> =
    Set.ofList [
        "header-auth-mode" // HeaderAuthProvider in an auth Mode (spoofable X-User-Id)
        "oidc-config-completeness" // auth=oidc with unset issuer (insecure fallback)
        "oidc-audience-binding" // auth=oidc with unbound audience (token reuse)
        "sse-auth-mode" // QueryParamFallback SSE in an auth Mode (userId leak)
        "encrypted-secret-store-mode" // plaintext secrets in an auth Mode
        "oauth-state-store-instance" // in-memory OAuth state under multi-instance
        "per-scope-key-resolver-distributed" // crypto-shred key resolver under multi-instance
        "auto-bootstrap-dev-admin-mode" // first sign-in auto-promotes to Platform Admin (privilege-escalation surface) — warning must survive the SkipPreflight bypass lever
    ]

let private isSecurityClass (v: IConfigValidator) =
    securityClassValidatorNames.Contains v.Name

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
/// `skipPreflight = true` skips the *non*-security-class validators
/// (the emergency-boot lever for a noisy companion probe) but still
/// runs every `securityClassValidatorNames` validator and still aborts
/// on their `Error` — a single boolean must not silently disable the
/// auth-class guards. The skipped validators' names are enumerated in
/// the log so the bypass is visible at `Warn` level, not just in the
/// `/dev/inspect` panel. Returns the outcomes so the caller can
/// populate the snapshot service.
let validate (services: IServiceCollection) (logger: ILogger option) (skipPreflight: bool) : ValidatorOutcome list =
    let validators = collectValidators services

    if skipPreflight then
        let securityClass, skipped = validators |> List.partition isSecurityClass

        match logger with
        | Some l ->
            if skipped.IsEmpty then
                l.Warn "[preflight] ServerConfig.SkipPreflight = true. 0 non-security-class validator(s) skipped."
            else
                let names = skipped |> List.map _.Name |> List.sort |> String.concat ", "

                l.Warn(
                    sprintf
                        "[preflight] ServerConfig.SkipPreflight = true. %d non-security-class validator(s) skipped: %s"
                        skipped.Length
                        names
                )

            if not securityClass.IsEmpty then
                let secNames = securityClass |> List.map _.Name |> List.sort |> String.concat ", "

                l.Warn(
                    sprintf
                        "[preflight] %d security-class validator(s) run despite SkipPreflight (not bypassable): %s"
                        securityClass.Length
                        secNames
                )
        | None -> ()

        if securityClass.IsEmpty then
            []
        else
            runSet logger securityClass
    elif validators.IsEmpty then
        // GP 13 — lightweight default. Zero-config deployments stay
        // green and start normally with no log noise.
        []
    else
        runSet logger validators