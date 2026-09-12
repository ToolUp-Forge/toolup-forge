// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Text.Json

// ─── Phase 192 — cold-start / hot-path perf-budget gate ──────────────
//
// Phase 16 shipped cold-start MITIGATION and quoted one number as its
// acceptance criterion — "Cold start < 2s" — measured once, by hand, at
// the moment it shipped. Nothing has measured it since. A budget
// asserted once is not a budget; it is a claim with a date on it, and
// the substrate has accreted for months underneath it.
//
// This module is the DECIDING half of the recurring gate: a declarative
// budget file, a reader for the measurement run, and a pure check
// between them. The MEASURING half is dev-scripts/perf-budget-gate.ps1,
// which builds the minimal sample, boots it repeatedly, drives the
// hot-path request, and hands the numbers here.
//
// The split is the one Phase 213's Core-Web-Vitals gate uses in this
// same package, for the same reason: the laws stay fixture-testable
// with no process, no port and no clock anywhere near them.
//
// Four properties are load-bearing.
//
//   * **Nothing passes silently.** A budgeted metric the run did not
//     measure is a BREACH. So is a measurement the runner could not
//     CONFIRM — a boot whose ready line never appeared timed a process
//     that may never have served anything, and a gate that reports that
//     as a fast cold start is worse than no gate. `Observed` carries
//     that confirmation and the check refuses without it.
//
//   * **Widening is a file change.** Every ceiling, and the sample
//     count each statistic must be drawn from, lives in the committed
//     budget file. Raising one is a reviewable diff with an author,
//     never an inline override or an env-var escape.
//
//   * **Slack is visible on a GREEN run.** Every satisfied ceiling is
//     still rendered, with the measured value and the headroom ratio
//     against it. A ceiling set generously enough never to flake will
//     go slack as machines get faster, and the only way that gets
//     noticed is if every passing run says so.
//
//   * **Zero runtime cost (GP 13).** Nothing here is composed into a
//     deployment. Every shipped app is byte-for-byte identical whether
//     or not this gate exists — which is also the invariant it defends:
//     the `absentAssemblies` rule asserts that a companion nobody
//     registered is not in the minimal app's output at all.
//
// ── Why the gate reads MIN and not mean or median ───────────────────
//
// Wallclock on a shared CI runner is a floor plus noise, and the noise
// is one-sided: a neighbouring job can only ever make a boot slower,
// never faster. The minimum over N runs is therefore the least
// contaminated estimate of the thing being defended, and it is the only
// statistic whose distribution does not widen as the runner gets
// busier. The budget file names the statistic explicitly rather than
// leaving it implied, and the check refuses a run drawn from fewer
// samples than the budget requires — a "min" over one run is just that
// run.

/// Phase 192 — the two wallclock quantities a perf budget places a
/// ceiling on. Both are milliseconds.
type PerfMetric =
    /// Process start to the host's ready line: what a serverless cold
    /// invocation pays before it can serve anything.
    | ColdStartMs
    /// One representative request through the composed pipeline with
    /// every `No*` default active: the per-request cost the platform
    /// imposes on a deployment that opted into nothing.
    | HotPathMs

module PerfMetric =

    /// The token a budget file and a measurement file both spell it
    /// with. One function, so the two sides cannot drift.
    let key metric =
        match metric with
        | ColdStartMs -> "coldStartMs"
        | HotPathMs -> "hotPathMs"

    /// Every metric the gate knows, in report order.
    let all = [ ColdStartMs; HotPathMs ]

    let tryParse (token: string) =
        all
        |> List.tryFind (fun m -> String.Equals(key m, token, StringComparison.Ordinal))

    /// What a regression in this metric would mean, rendered into the
    /// failure so a CI log is actionable without opening this file.
    let describe metric =
        match metric with
        | ColdStartMs -> "process start to the host's ready line"
        | HotPathMs -> "one representative request through the composed pipeline"

/// Phase 192 — one statistic the measuring half produced, with the
/// evidence that it measured a real thing.
type PerfSample = {
    Metric: PerfMetric
    /// The statistic the value is (`min`). Carried rather than assumed
    /// so a runner that changes it cannot silently change the meaning
    /// of the ceiling it is checked against.
    Statistic: string
    /// Milliseconds.
    Value: float
    /// How many observations the statistic was drawn from.
    Samples: int
    /// Did the runner CONFIRM the measured thing happened — the ready
    /// line appeared, the request returned a success status. `false`
    /// means the number timed something that may never have started.
    Observed: bool
    /// What the confirmation was, verbatim, so the check's refusal can
    /// quote it.
    Evidence: string
}

/// Phase 192 — a parsed budget file. Ceilings and sample floors are
/// association lists rather than maps so a rendered report walks them
/// in the order the file declared, keeping a diff between two runs
/// stable.
type PerfBudget = {
    /// Human label, echoed into every report.
    Label: string
    /// What is measured — the sample's repo-relative path. Compared
    /// against nothing; carried so a failing log says WHICH app.
    Subject: string
    /// The statistic every ceiling is checked against.
    Statistic: string
    /// Ceilings, milliseconds.
    Ceilings: (PerfMetric * float) list
    /// The minimum number of observations each statistic must be drawn
    /// from before the gate will believe it.
    MinimumSamples: (PerfMetric * int) list
    /// The measured baseline each ceiling was set against, recorded so
    /// the headroom line means something. Informational — a baseline
    /// never fails a run.
    Baselines: (PerfMetric * float) list
    /// Assembly names (no extension) that MUST NOT appear in the
    /// measured app's output directory. The zero-cost-when-unused
    /// invariant made byte-level: a companion nobody registered is not
    /// merely idle, it is absent.
    AbsentAssemblies: string list
}

/// Phase 192 — one measurement run, as the runner script wrote it.
type PerfMeasurementRun = {
    Label: string
    /// The measured app's output directory, as the runner saw it.
    AppDirectory: string
    Samples: PerfSample list
    /// Every assembly file name found in `AppDirectory`. An EMPTY list
    /// is refused rather than read as "nothing forbidden is present" —
    /// see `AssemblyInventoryEmpty`.
    OutputAssemblies: string list
}

/// Phase 192 — one thing the gate found. Every case names its subject
/// and, where there are two numbers, both of them.
type PerfFinding =
    /// A measured statistic exceeded its declared ceiling.
    | CeilingBreached of metric: PerfMetric * observed: float * ceiling: float
    /// The budget places a ceiling on a metric the run did not measure.
    /// A breach, not a skip — an unmeasured budget line is
    /// indistinguishable from a passing one otherwise.
    | MetricNotMeasured of metric: PerfMetric
    /// The run produced a number but could not confirm it measured
    /// anything: no ready line, no success status. A breach — this is
    /// the failure mode where a gate reports a fast boot for a process
    /// that never booted.
    | MeasurementUnobserved of metric: PerfMetric * evidence: string
    /// The statistic was drawn from fewer observations than the budget
    /// requires.
    | TooFewSamples of metric: PerfMetric * samples: int * required: int
    /// The run's statistic is not the one the budget's ceilings are
    /// declared against.
    | StatisticMismatch of budgetStatistic: string * runStatistic: string * metric: PerfMetric
    /// An assembly the budget requires to be absent is in the measured
    /// app's output.
    | ZeroCostAssemblyPresent of assembly: string
    /// The budget names assemblies that must be absent, but the run
    /// listed no assemblies at all — so absence cannot be asserted. A
    /// breach, for the same reason `MetricNotMeasured` is.
    | AssemblyInventoryEmpty of required: int
    /// A satisfied ceiling. NOT a breach — rendered so slack is visible
    /// on a green run.
    | WithinCeiling of metric: PerfMetric * observed: float * ceiling: float * baseline: float option

module PerfFinding =

    /// Does this finding fail the gate? Everything except the
    /// satisfied-ceiling line, which exists to be READ rather than to
    /// pass or fail.
    let isBreach finding =
        match finding with
        | WithinCeiling _ -> false
        | _ -> true

    let private num (v: float) =
        v.ToString("0.###", Globalization.CultureInfo.InvariantCulture)

    let private ratio (observed: float) (ceiling: float) =
        if observed <= 0.0 then
            "-"
        else
            (ceiling / observed).ToString("0.0", Globalization.CultureInfo.InvariantCulture)
            + "x"

    /// One line, naming the subject and both numbers.
    let render finding =
        match finding with
        | CeilingBreached(metric, observed, ceiling) ->
            $"[breach] {PerfMetric.key metric} was {num observed} ms, budget allows at most {num ceiling} ms ({PerfMetric.describe metric})"
        | MetricNotMeasured metric ->
            $"[unmeasured] {PerfMetric.key metric} — the budget places a ceiling on it, but the run carries no sample for it"
        | MeasurementUnobserved(metric, evidence) ->
            $"[unobserved] {PerfMetric.key metric} — the run produced a number but never confirmed it measured anything (evidence: '{evidence}'). A boot whose ready line never appeared timed a process that may never have served"
        | TooFewSamples(metric, samples, required) ->
            $"[undersampled] {PerfMetric.key metric} — statistic drawn from {samples} observation(s), budget requires at least {required}"
        | StatisticMismatch(budgetStatistic, runStatistic, metric) ->
            $"[statistic] {PerfMetric.key metric} — the run reports '{runStatistic}' but the budget's ceilings are declared against '{budgetStatistic}'"
        | ZeroCostAssemblyPresent assembly ->
            $"[zero-cost] '{assembly}' is in the measured app's output — the budget requires it to be absent, because a companion nobody registered should cost the deployment nothing at all, not merely be idle"
        | AssemblyInventoryEmpty required ->
            $"[zero-cost] the budget requires {required} assembly(ies) to be absent, but the run listed no assemblies — absence cannot be asserted against an empty inventory"
        | WithinCeiling(metric, observed, ceiling, baseline) ->
            let baselineText =
                match baseline with
                | Some b -> $", baseline {num b} ms"
                | None -> ""

            $"[ok] {PerfMetric.key metric} {num observed} ms / {num ceiling} ms ({ratio observed ceiling} headroom{baselineText})"

/// Phase 192 — everything the gate needs to decide one run.
type PerfBudgetGateOptions = {
    /// Path to the declarative budget file.
    BudgetFile: string
    /// Path to the measurement run the runner script wrote.
    MeasurementsFile: string
}

module PerfBudgetGateOptions =

    /// Environment-variable names the FAKE target reads. Named
    /// constants rather than literals at the read site so the runner
    /// script and the target cannot drift apart silently.
    [<Literal>]
    let BudgetVariable = "TOOLUP_PERF_BUDGET"

    [<Literal>]
    let MeasurementsVariable = "TOOLUP_PERF_MEASUREMENTS"

    let create budgetFile measurementsFile = {
        BudgetFile = budgetFile
        MeasurementsFile = measurementsFile
    }

    let private readVar name =
        match Environment.GetEnvironmentVariable name with
        | null -> None
        | v when String.IsNullOrWhiteSpace v -> None
        | v -> Some(v.Trim())

    /// Resolve options from the environment. Both required variables
    /// are reported together when missing, so one run names every
    /// omission rather than one per invocation.
    let fromEnvironment () : Result<PerfBudgetGateOptions, string list> =
        let budget = readVar BudgetVariable
        let measurements = readVar MeasurementsVariable

        let errors = [
            if Option.isNone budget then
                $"{BudgetVariable} is not set — it must name the perf-budget file to check against."

            if Option.isNone measurements then
                $"{MeasurementsVariable} is not set — it must name the measurement file the runner script wrote."
        ]

        match budget, measurements with
        | Some b, Some m -> Ok(create b m)
        | _ -> Error errors

/// Phase 192 — the perf-budget gate: parsing, the pure check, and the
/// call shapes on top of it.
module PerfBudgetGate =

    /// The one schema token a budget file must declare. A file without
    /// it is refused rather than guessed at — the gate would otherwise
    /// happily read an unrelated JSON document as a budget asserting
    /// nothing.
    [<Literal>]
    let BudgetSchemaToken = "toolup.perf-budget/v1"

    /// The measurement file's own token. Separate from the budget's:
    /// the two documents are written by different halves of the gate
    /// and pointing one variable at the other must fail loudly.
    [<Literal>]
    let MeasurementSchemaToken = "toolup.perf-measurements/v1"

    /// The only statistic this gate's reasoning is sound for. See the
    /// module header: wallclock noise on a shared runner is one-sided,
    /// so the minimum is the least contaminated estimate.
    [<Literal>]
    let MinStatistic = "min"

    // ─── Parsing ─────────────────────────────────────────────────────

    let private tryProperty (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, v -> Some v
        | _ -> None

    let private asNumber (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Number then
            match element.TryGetDouble() with
            | true, v -> Some v
            | _ -> None
        else
            None

    let private asString (element: JsonElement) =
        if element.ValueKind = JsonValueKind.String then
            match element.GetString() with
            | null -> None
            | s -> Some s
        else
            None

    /// Read a `{ "coldStartMs": 2000, … }` object into metric-keyed
    /// pairs, in the order the file declared them. Every unknown key
    /// and every non-numeric value is an error, never a skipped line.
    let private readMetricMap (label: string) (section: string) (element: JsonElement) (errors: ResizeArray<string>) =
        if element.ValueKind <> JsonValueKind.Object then
            errors.Add $"'{label}' — '{section}' must be a JSON object, but is {element.ValueKind}."
            []
        else
            [
                for property in element.EnumerateObject() do
                    match PerfMetric.tryParse property.Name with
                    | None ->
                        let known = String.Join(", ", PerfMetric.all |> List.map PerfMetric.key)

                        errors.Add
                            $"'{label}' — '{section}' names an unknown metric '{property.Name}'. Known metrics: {known}."
                    | Some metric ->
                        match asNumber property.Value with
                        | Some v -> yield metric, v
                        | None ->
                            errors.Add
                                $"'{label}' — '{section}.{property.Name}' must be a number, but is {property.Value.ValueKind}."
            ]

    let private readSchema (label: string) (expected: string) (root: JsonElement) (errors: ResizeArray<string>) =
        match tryProperty root "schema" |> Option.bind asString with
        | Some s when s = expected -> ()
        | Some s -> errors.Add $"'{label}' declares schema '{s}' — this gate reads '{expected}'."
        | None -> errors.Add $"'{label}' declares no 'schema' — it must declare '{expected}'."

    let private parseDocument (label: string) (json: string) (read: JsonElement -> ResizeArray<string> -> 'a option) =
        let mutable document = Unchecked.defaultof<JsonDocument>

        let parsed =
            try
                document <- JsonDocument.Parse json
                Ok document.RootElement
            with ex ->
                Error [ $"'{label}' is not valid JSON — {ex.Message}" ]

        try
            match parsed with
            | Error e -> Error e
            | Ok root when root.ValueKind <> JsonValueKind.Object ->
                Error [ $"'{label}' must be a JSON object at its root, but is {root.ValueKind}." ]
            | Ok root ->
                let errors = ResizeArray<string>()
                let value = read root errors

                match value with
                | Some v when errors.Count = 0 -> Ok v
                | _ -> Error(List.ofSeq errors)
        finally
            if not (obj.ReferenceEquals(document, null)) then
                document.Dispose()

    /// Parse a budget document. Returns EVERY defect found rather than
    /// the first, so one run tells an author the whole story about a
    /// file they are editing by hand.
    let parseBudget (label: string) (json: string) : Result<PerfBudget, string list> =
        parseDocument label json (fun root errors ->
            readSchema label BudgetSchemaToken root errors

            let budgetLabel =
                tryProperty root "label" |> Option.bind asString |> Option.defaultValue label

            let subject =
                match tryProperty root "subject" |> Option.bind asString with
                | Some s when not (String.IsNullOrWhiteSpace s) -> s
                | _ ->
                    errors.Add
                        $"'{label}' declares no 'subject' — it must name the app the gate measures, so a failing log says which one."

                    ""

            let statistic =
                match tryProperty root "statistic" |> Option.bind asString with
                | Some s when s = MinStatistic -> s
                | Some s ->
                    errors.Add
                        $"'{label}' declares statistic '{s}' — this gate reasons only about '{MinStatistic}' (wallclock noise on a shared runner is one-sided, so the minimum is the only estimate that does not widen as the runner gets busier)."

                    s
                | None ->
                    errors.Add $"'{label}' declares no 'statistic' — it must declare '{MinStatistic}'."
                    ""

            let ceilings =
                match tryProperty root "ceilings" with
                | Some e ->
                    let parsed = readMetricMap label "ceilings" e errors

                    if List.isEmpty parsed && e.ValueKind = JsonValueKind.Object then
                        errors.Add
                            $"'{label}' declares an empty 'ceilings' object — a budget with no ceiling asserts nothing."

                    parsed
                | None ->
                    errors.Add $"'{label}' declares no 'ceilings' — a budget with no ceiling asserts nothing."
                    []

            let minimumSamples =
                match tryProperty root "minimumSamples" with
                | Some e ->
                    readMetricMap label "minimumSamples" e errors
                    |> List.map (fun (m, v) -> m, int v)
                | None ->
                    errors.Add
                        $"'{label}' declares no 'minimumSamples' — every ceiling needs the observation count its statistic must be drawn from, or a 'min' over one run passes as a measurement."

                    []

            for metric, _ in ceilings do
                if not (minimumSamples |> List.exists (fun (m, _) -> m = metric)) then
                    errors.Add
                        $"'{label}' places a ceiling on '{PerfMetric.key metric}' but declares no 'minimumSamples.{PerfMetric.key metric}'."

            let baselines =
                match tryProperty root "baselines" with
                | Some e -> readMetricMap label "baselines" e errors
                | None -> []

            let absentAssemblies =
                match tryProperty root "absentAssemblies" with
                | None -> []
                | Some e when e.ValueKind <> JsonValueKind.Array ->
                    errors.Add $"'{label}' — 'absentAssemblies' must be a JSON array, but is {e.ValueKind}."
                    []
                | Some e -> [
                    for item in e.EnumerateArray() do
                        match asString item with
                        | Some s when not (String.IsNullOrWhiteSpace s) -> yield s.Trim()
                        | _ -> errors.Add $"'{label}' — every 'absentAssemblies' entry must be a non-empty string."
                  ]

            Some {
                Label = budgetLabel
                Subject = subject
                Statistic = statistic
                Ceilings = ceilings
                MinimumSamples = minimumSamples
                Baselines = baselines
                AbsentAssemblies = absentAssemblies
            })

    /// Parse a measurement run. Same all-defects-at-once contract as
    /// the budget parser.
    let parseMeasurements (label: string) (json: string) : Result<PerfMeasurementRun, string list> =
        parseDocument label json (fun root errors ->
            readSchema label MeasurementSchemaToken root errors

            let runLabel =
                tryProperty root "label" |> Option.bind asString |> Option.defaultValue label

            let appDirectory =
                match tryProperty root "appDirectory" |> Option.bind asString with
                | Some s when not (String.IsNullOrWhiteSpace s) -> s
                | _ ->
                    errors.Add $"'{label}' declares no 'appDirectory' — it must name the output directory it measured."
                    ""

            let outputAssemblies =
                match tryProperty root "outputAssemblies" with
                | None -> []
                | Some e when e.ValueKind <> JsonValueKind.Array ->
                    errors.Add $"'{label}' — 'outputAssemblies' must be a JSON array, but is {e.ValueKind}."
                    []
                | Some e -> [
                    for item in e.EnumerateArray() do
                        match asString item with
                        | Some s -> yield s.Trim()
                        | None -> errors.Add $"'{label}' — every 'outputAssemblies' entry must be a string."
                  ]

            let samples =
                match tryProperty root "samples" with
                | None ->
                    errors.Add $"'{label}' declares no 'samples' — a measurement run with no sample measured nothing."
                    []
                | Some e when e.ValueKind <> JsonValueKind.Array ->
                    errors.Add $"'{label}' — 'samples' must be a JSON array, but is {e.ValueKind}."
                    []
                | Some e -> [
                    for item in e.EnumerateArray() do
                        let metric =
                            tryProperty item "metric"
                            |> Option.bind asString
                            |> Option.bind PerfMetric.tryParse

                        let value = tryProperty item "value" |> Option.bind asNumber
                        let samples = tryProperty item "samples" |> Option.bind asNumber

                        let statistic =
                            tryProperty item "statistic" |> Option.bind asString |> Option.defaultValue ""

                        let observed =
                            match tryProperty item "observed" with
                            | Some o when o.ValueKind = JsonValueKind.True -> Some true
                            | Some o when o.ValueKind = JsonValueKind.False -> Some false
                            | _ -> None

                        let evidence =
                            tryProperty item "evidence" |> Option.bind asString |> Option.defaultValue ""

                        match metric, value, samples, observed with
                        | Some m, Some v, Some n, Some o ->
                            yield {
                                Metric = m
                                Statistic = statistic
                                Value = v
                                Samples = int n
                                Observed = o
                                Evidence = evidence
                            }
                        | _ ->
                            errors.Add
                                $"'{label}' — every 'samples' entry needs a known 'metric', a numeric 'value', a numeric 'samples' count and a boolean 'observed'."
                  ]

            Some {
                Label = runLabel
                AppDirectory = appDirectory
                Samples = samples
                OutputAssemblies = outputAssemblies
            })

    // ─── The pure check ──────────────────────────────────────────────

    /// Compare a run against a budget. Total: every budgeted ceiling
    /// produces exactly one finding — a breach, an unmeasured line, an
    /// unobserved line, or a satisfied `WithinCeiling` — so a report can
    /// never omit a budget line by falling through a branch.
    let check (budget: PerfBudget) (run: PerfMeasurementRun) : PerfFinding list =
        let sampleFor metric =
            run.Samples |> List.tryFind (fun s -> s.Metric = metric)

        let requiredSamples metric =
            budget.MinimumSamples
            |> List.tryFind (fun (m, _) -> m = metric)
            |> Option.map snd
            |> Option.defaultValue 1

        let baselineFor metric =
            budget.Baselines |> List.tryFind (fun (m, _) -> m = metric) |> Option.map snd

        let ceilingFindings = [
            for metric, ceiling in budget.Ceilings do
                match sampleFor metric with
                | None -> yield MetricNotMeasured metric
                | Some sample when not sample.Observed -> yield MeasurementUnobserved(metric, sample.Evidence)
                | Some sample when sample.Statistic <> budget.Statistic ->
                    yield StatisticMismatch(budget.Statistic, sample.Statistic, metric)
                | Some sample when sample.Samples < requiredSamples metric ->
                    yield TooFewSamples(metric, sample.Samples, requiredSamples metric)
                | Some sample when sample.Value > ceiling -> yield CeilingBreached(metric, sample.Value, ceiling)
                | Some sample -> yield WithinCeiling(metric, sample.Value, ceiling, baselineFor metric)
        ]

        // `Path.GetFileNameWithoutExtension` is WRONG here and was the
        // first defect this pack caught: it strips the last dot segment,
        // so a dotted assembly name reduces to a prefix
        // ("ToolUp.Metrics.OpenTelemetry" -> "ToolUp.Metrics") and the
        // comparison silently never matches. Only a real assembly suffix
        // is trimmed.
        let normaliseAssemblyName (name: string) =
            let trimmed = (if isNull name then "" else name).Trim()

            if trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
                trimmed.Substring(0, trimmed.Length - 4)
            elif trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) then
                trimmed.Substring(0, trimmed.Length - 4)
            else
                trimmed

        let assemblyFindings =
            if List.isEmpty budget.AbsentAssemblies then
                []
            elif List.isEmpty run.OutputAssemblies then
                [ AssemblyInventoryEmpty(List.length budget.AbsentAssemblies) ]
            else
                let present = run.OutputAssemblies |> List.map normaliseAssemblyName |> Set.ofList

                [
                    for forbidden in budget.AbsentAssemblies do
                        if present.Contains(normaliseAssemblyName forbidden) then
                            yield ZeroCostAssemblyPresent forbidden
                ]

        ceilingFindings @ assemblyFindings

    /// The findings that fail the run.
    let breaches findings =
        findings |> List.filter PerfFinding.isBreach

    /// The whole verdict as one block of text — every budget line,
    /// breach or not, so a green run still shows its headroom.
    let report (budget: PerfBudget) (findings: PerfFinding list) =
        let lines = [
            $"[perf-budget] {budget.Label}"
            $"[perf-budget] subject: {budget.Subject} (statistic: {budget.Statistic})"
            yield! findings |> List.map PerfFinding.render

            match breaches findings with
            | [] -> $"[perf-budget] within budget — {List.length budget.Ceilings} ceiling(s) checked"
            | b -> $"[perf-budget] {List.length b} breach(es)"
        ]

        String.Join(Environment.NewLine, lines)

    // ─── Call shapes ─────────────────────────────────────────────────

    let private readFile (kind: string) (path: string) =
        if String.IsNullOrWhiteSpace path then
            Error [ $"no {kind} file was named." ]
        elif not (File.Exists path) then
            Error [ $"the {kind} file '{path}' does not exist." ]
        else
            try
                Ok(File.ReadAllText path)
            with ex ->
                Error [ $"the {kind} file '{path}' could not be read — {ex.Message}" ]

    /// Load both documents and check them. Errors are everything that
    /// stopped the gate from DECIDING; findings are what it decided.
    let verify (options: PerfBudgetGateOptions) : Result<PerfBudget * PerfFinding list, string list> =
        match readFile "budget" options.BudgetFile, readFile "measurements" options.MeasurementsFile with
        | Error a, Error b -> Error(a @ b)
        | Error a, _ -> Error a
        | _, Error b -> Error b
        | Ok budgetJson, Ok measurementJson ->
            match
                parseBudget (Path.GetFileName options.BudgetFile) budgetJson,
                parseMeasurements (Path.GetFileName options.MeasurementsFile) measurementJson
            with
            | Error a, Error b -> Error(a @ b)
            | Error a, _ -> Error a
            | _, Error b -> Error b
            | Ok budget, Ok run -> Ok(budget, check budget run)

    /// FAKE's `Target` module cannot be reached fully-qualified from
    /// here — the same binding collision the Core-Web-Vitals target
    /// documents — so the FAKE surface is reached through a nested
    /// module that opens Fake.Core locally.
    module private FakeSurface =
        open Fake.Core

        let createTarget (name: string) (body: unit -> unit) = Target.create name (fun _ -> body ())

        let trace (text: string) = Trace.tracefn "%s" text

    /// Register the `VerifyPerfBudget` FAKE target. The runner script
    /// sets the two environment variables and invokes it as the last
    /// step of a gate run, after the boots and requests have written
    /// their measurements:
    ///
    /// ```text
    /// TOOLUP_PERF_BUDGET=perf-budgets.json
    /// TOOLUP_PERF_MEASUREMENTS=artifacts/perf-budget/measurements.json
    /// dotnet run --project Build.fsproj -- VerifyPerfBudget
    /// ```
    ///
    /// Options are resolved INSIDE the target body, not at
    /// registration: a repo registering this target must stay runnable
    /// for every other target with neither variable set.
    let registerTarget () : unit =
        FakeSurface.createTarget "VerifyPerfBudget" (fun () ->
            match PerfBudgetGateOptions.fromEnvironment () with
            | Error errors ->
                failwithf "VerifyPerfBudget: %s%s" Environment.NewLine (String.Join(Environment.NewLine, errors))
            | Ok options ->
                match verify options with
                | Error errors ->
                    failwithf
                        "VerifyPerfBudget: could not run.%s%s"
                        Environment.NewLine
                        (String.Join(Environment.NewLine, errors))
                | Ok(budget, findings) ->
                    let text = report budget findings
                    FakeSurface.trace text

                    if not (List.isEmpty (breaches findings)) then
                        failwithf
                            "VerifyPerfBudget: %d budget breach(es) against '%s'.%s%s"
                            (List.length (breaches findings))
                            budget.Label
                            Environment.NewLine
                            text)