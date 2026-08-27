// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Text.Json

// ─── Phase 213 — Lighthouse / Core-Web-Vitals budget gate ────────────
//
// The public-rendering surface ships SSR pages a search crawler reads,
// and until now nothing asserted a performance, SEO or accessibility
// floor over them. A regression — a layout that shifts, a blocking
// script, a lost meta description — landed green and was discovered by
// the index weeks later.
//
// This module is the DECIDING half of the gate: a declarative budget
// file, a parser for Lighthouse's JSON report, and a pure check between
// them. The measuring half (build the site, serve it on a throwaway
// port, drive Lighthouse over a page set) is the CI runner script
// dev-scripts/cwv-budget-gate.ps1, which hands the reports here.
//
// Three properties are load-bearing:
//
//   * **Nothing passes silently.** A budget that names a metric the
//     report does not carry, or a page no report covers, is a BREACH,
//     not a skipped line. A gate that quietly measures nothing is worse
//     than no gate, because it reads as green.
//   * **Widening is a file change.** Every threshold lives in the
//     committed budget file. Raising a ceiling is a reviewable diff
//     with an author, never an inline override or an env-var escape.
//   * **Zero runtime cost (GP 13).** Nothing here is composed into a
//     deployment. The gate is a build-time reader of artefacts the CI
//     run produced; the rendered SSR output is byte-for-byte unchanged
//     whether or not it runs.
//
// The shape deliberately mirrors PackagedModuleConformance in this same
// package: pure check + loaders + a FAKE target on top, so the laws stay
// fixture-testable without a browser anywhere near them.

/// Phase 213 — the four Core-Web-Vitals-family metrics a budget may
/// place a CEILING on. Each maps to one Lighthouse audit id whose
/// `numericValue` carries the measurement (milliseconds, except the
/// unitless layout-shift score).
type CoreWebVitalsMetric =
    /// Largest Contentful Paint, in milliseconds.
    | LargestContentfulPaint
    /// Cumulative Layout Shift — a unitless score, not a duration.
    | CumulativeLayoutShift
    /// Total Blocking Time, in milliseconds.
    | TotalBlockingTime
    /// First Contentful Paint, in milliseconds.
    | FirstContentfulPaint

module CoreWebVitalsMetric =

    /// The key this metric is written as in a budget file.
    let key metric =
        match metric with
        | LargestContentfulPaint -> "largestContentfulPaintMs"
        | CumulativeLayoutShift -> "cumulativeLayoutShift"
        | TotalBlockingTime -> "totalBlockingTimeMs"
        | FirstContentfulPaint -> "firstContentfulPaintMs"

    /// The Lighthouse audit id whose numeric value this metric reads.
    let auditId metric =
        match metric with
        | LargestContentfulPaint -> "largest-contentful-paint"
        | CumulativeLayoutShift -> "cumulative-layout-shift"
        | TotalBlockingTime -> "total-blocking-time"
        | FirstContentfulPaint -> "first-contentful-paint"

    /// Unit suffix used when rendering an observed value.
    let unit metric =
        match metric with
        | CumulativeLayoutShift -> ""
        | _ -> " ms"

    let all = [
        LargestContentfulPaint
        CumulativeLayoutShift
        TotalBlockingTime
        FirstContentfulPaint
    ]

    /// Resolve a budget-file key to its metric, or nothing when the key
    /// is not one this gate knows. An unknown key is a parse ERROR
    /// rather than an ignored line — a typo'd threshold that silently
    /// asserts nothing is the exact failure this gate exists to prevent.
    let ofKey (k: string) =
        all |> List.tryFind (fun m -> String.Equals(key m, k, StringComparison.Ordinal))

/// Phase 213 — the Lighthouse category scores a budget may place a
/// FLOOR on. Scores are reported in the 0.0–1.0 range.
type LighthouseCategory =
    | Performance
    | Seo
    | Accessibility
    | BestPractices

module LighthouseCategory =

    /// The key this category is written as in a budget file.
    let key category =
        match category with
        | Performance -> "performance"
        | Seo -> "seo"
        | Accessibility -> "accessibility"
        | BestPractices -> "bestPractices"

    /// The category id Lighthouse's own report uses. Note it differs
    /// from the budget key for best-practices — the report spells it
    /// hyphenated, a budget file spells it camelCase like its siblings.
    let reportId category =
        match category with
        | Performance -> "performance"
        | Seo -> "seo"
        | Accessibility -> "accessibility"
        | BestPractices -> "best-practices"

    let all = [ Performance; Seo; Accessibility; BestPractices ]

    let ofKey (k: string) =
        all |> List.tryFind (fun c -> String.Equals(key c, k, StringComparison.Ordinal))

/// Phase 213 — the cheap server-side companion signal, cross-checked
/// alongside the browser measurement. Both fields read counters the
/// public-rendering tier already emits: the render-duration histogram
/// and the conditional-GET outcome counter whose two tag values give
/// the 304 / 200 split a crawl-budget runbook chases.
///
/// Every field is optional: a budget asserting only browser metrics
/// declares no server signals at all, and nothing here is measured.
type CoreWebVitalsServerSignals = {
    /// Ceiling on the worst observed server-side render duration, in
    /// milliseconds.
    MaxRenderMs: float option
    /// Floor on the conditional-GET 304 rate — 304 responses over all
    /// conditional-GET outcomes. A collapsed rate means revalidating
    /// crawlers are being served full bodies.
    MinConditionalGet304Rate: float option
    /// When true, a run that supplies no snapshot BREACHES rather than
    /// reporting the signals as unsampled. False (the default) keeps the
    /// gate runnable where no metrics snapshot is reachable, while still
    /// naming the omission in the report.
    Required: bool
}

module CoreWebVitalsServerSignals =

    /// Declares nothing — the value a budget with no `serverSignals`
    /// block contributes, and the starting point for one that has it.
    let none = {
        MaxRenderMs = None
        MinConditionalGet304Rate = None
        Required = false
    }

/// Phase 213 — a parsed budget file. Thresholds are held as association
/// lists rather than maps so the rendered report walks them in the order
/// the file declared, which keeps a diff between two runs stable.
type CoreWebVitalsBudget = {
    /// Human label used in report headers (the budget file's own
    /// `label`, or its file name).
    Label: string
    /// URL paths the budget covers, e.g. `/` and `/pricing`. Every one
    /// must be covered by a report, or the run breaches.
    Pages: string list
    /// Per-metric ceilings — an observed value ABOVE the threshold is a
    /// breach.
    MetricCeilings: (CoreWebVitalsMetric * float) list
    /// Per-category floors — an observed score BELOW the threshold is a
    /// breach.
    CategoryFloors: (LighthouseCategory * float) list
    /// The optional server-side companion signals.
    ServerSignals: CoreWebVitalsServerSignals option
}

/// Phase 213 — one Lighthouse run's report, reduced to the values this
/// gate decides on. Everything else in a Lighthouse JSON report (the
/// full audit set, screenshots, the trace) is deliberately dropped.
type LighthousePageReport = {
    /// Label used in findings — conventionally the report file name.
    ReportLabel: string
    /// The URL path the run covered, normalised the same way budget
    /// pages are, so the two match without host or trailing-slash noise.
    PagePath: string
    /// Metric values the report carried, by audit.
    MetricValues: (CoreWebVitalsMetric * float) list
    /// Category scores the report carried, in the 0.0–1.0 range.
    CategoryScores: (LighthouseCategory * float) list
}

/// Phase 213 — a snapshot of the server-side counters, taken over the
/// same run the Lighthouse reports came from. Sourced by the runner
/// script from whatever the deployment exposes; absent when the run had
/// no metrics surface to read.
type RenderMetricsSnapshot = {
    /// Label used in findings (the snapshot file name).
    SnapshotLabel: string
    /// Worst observed render duration in milliseconds over the run.
    RenderMsMax: float option
    /// Count of conditional-GET responses that were 304.
    ConditionalGet304: int option
    /// Count of conditional-GET responses that were 200.
    ConditionalGet200: int option
}

/// Phase 213 — one thing the gate found. Every case names its subject
/// and both numbers, so a failed CI log is actionable without opening
/// the report JSON.
type CoreWebVitalsFinding =
    /// An observed metric exceeded its declared ceiling.
    | MetricCeilingBreached of page: string * metric: CoreWebVitalsMetric * observed: float * ceiling: float
    /// An observed category score fell below its declared floor.
    | CategoryFloorBreached of page: string * category: LighthouseCategory * observed: float * floor: float
    /// The budget places a ceiling on a metric the page's report does
    /// not carry. A breach, not a skip — an unmeasured budget line is
    /// indistinguishable from a passing one otherwise.
    | MetricNotReported of page: string * metric: CoreWebVitalsMetric
    /// The budget places a floor on a category the page's report does
    /// not carry. A breach, for the same reason.
    | CategoryNotReported of page: string * category: LighthouseCategory
    /// The budget covers a page no supplied report measured.
    | PageNotReported of page: string
    /// A report was supplied for a page the budget does not cover. Not a
    /// breach — extra coverage is harmless — but reported so a page-set
    /// drift between the runner and the budget is visible.
    | PageNotBudgeted of page: string * reportLabel: string
    /// The worst server-side render duration exceeded its ceiling.
    | RenderMsBreached of observed: float * ceiling: float
    /// The conditional-GET 304 rate fell below its floor.
    | ConditionalGet304RateBreached of observed: float * floor: float
    /// The budget declares server signals but the run supplied no
    /// snapshot. A breach only when the budget marked them required.
    | ServerSignalsNotSampled of required: bool

module CoreWebVitalsFinding =

    /// Does this finding fail the gate? Everything does except an
    /// unbudgeted extra page and an unsampled optional server signal —
    /// both of which are still RENDERED, so neither disappears.
    let isBreach finding =
        match finding with
        | PageNotBudgeted _ -> false
        | ServerSignalsNotSampled required -> required
        | _ -> true

    let private num (v: float) =
        v.ToString("0.###", Globalization.CultureInfo.InvariantCulture)

    /// One line, naming the subject and both numbers.
    let render finding =
        match finding with
        | MetricCeilingBreached(page, metric, observed, ceiling) ->
            let u = CoreWebVitalsMetric.unit metric

            $"[metric] {page} — {CoreWebVitalsMetric.key metric} was {num observed}{u}, budget allows at most {num ceiling}{u}"
        | CategoryFloorBreached(page, category, observed, floor) ->
            $"[category] {page} — {LighthouseCategory.key category} scored {num observed}, budget requires at least {num floor}"
        | MetricNotReported(page, metric) ->
            $"[unmeasured] {page} — budget places a ceiling on {CoreWebVitalsMetric.key metric}, but the report carries no '{CoreWebVitalsMetric.auditId metric}' numeric value"
        | CategoryNotReported(page, category) ->
            $"[unmeasured] {page} — budget places a floor on {LighthouseCategory.key category}, but the report carries no '{LighthouseCategory.reportId category}' category score"
        | PageNotReported page -> $"[uncovered] {page} — budget covers this page, but no supplied report measured it"
        | PageNotBudgeted(page, reportLabel) ->
            $"[extra] {page} — measured by '{reportLabel}' but not covered by the budget (not a breach)"
        | RenderMsBreached(observed, ceiling) ->
            $"[server] render_ms peaked at {num observed} ms, budget allows at most {num ceiling} ms"
        | ConditionalGet304RateBreached(observed, floor) ->
            $"[server] conditional-GET 304 rate was {num observed}, budget requires at least {num floor}"
        | ServerSignalsNotSampled required ->
            let severity = if required then "required" else "advisory"

            $"[server] the budget declares server signals ({severity}) but the run supplied no metrics snapshot"

/// Phase 213 — everything the gate needs to decide one run.
type CoreWebVitalsGateOptions = {
    /// Path to the declarative budget file.
    BudgetFile: string
    /// Directory of Lighthouse JSON reports, one per measured page.
    ReportsDirectory: string
    /// Optional server-counter snapshot covering the same run.
    ServerMetricsFile: string option
}

module CoreWebVitalsGateOptions =

    /// Budget + reports, with no server snapshot — the shape a run
    /// without a reachable metrics surface takes.
    let create budgetFile reportsDirectory = {
        BudgetFile = budgetFile
        ReportsDirectory = reportsDirectory
        ServerMetricsFile = None
    }

    /// Environment-variable names the FAKE target reads. Named
    /// constants rather than literals at the read site so the runner
    /// script and the target cannot drift apart silently.
    [<Literal>]
    let BudgetVariable = "TOOLUP_CWV_BUDGET"

    [<Literal>]
    let ReportsVariable = "TOOLUP_CWV_REPORTS"

    [<Literal>]
    let ServerMetricsVariable = "TOOLUP_CWV_SERVER_METRICS"

    let private readVar name =
        match Environment.GetEnvironmentVariable name with
        | null -> None
        | v when String.IsNullOrWhiteSpace v -> None
        | v -> Some(v.Trim())

    /// Resolve options from the environment. Both required variables
    /// are reported together when missing, so one run names every
    /// omission rather than one per invocation.
    let fromEnvironment () : Result<CoreWebVitalsGateOptions, string list> =
        let budget = readVar BudgetVariable
        let reports = readVar ReportsVariable

        let errors = [
            if Option.isNone budget then
                $"{BudgetVariable} is not set — it must name the budget file to check against."

            if Option.isNone reports then
                $"{ReportsVariable} is not set — it must name the directory of Lighthouse JSON reports."
        ]

        match budget, reports with
        | Some b, Some r ->
            Ok {
                BudgetFile = b
                ReportsDirectory = r
                ServerMetricsFile = readVar ServerMetricsVariable
            }
        | _ -> Error errors

/// Phase 213 — the budget gate: parsing, the pure check, and the three
/// call shapes on top of it.
module CoreWebVitalsBudgetGate =

    /// The one schema token a budget file must declare. A file without
    /// it is refused rather than guessed at — the gate would otherwise
    /// happily read an unrelated JSON document as a budget asserting
    /// nothing.
    [<Literal>]
    let SchemaToken = "toolup.cwv-budget/v1"

    // ─── Page-path normalisation ─────────────────────────────────────
    //
    // A budget writes paths (`/pricing`); a Lighthouse report writes the
    // absolute URL it requested, whose host carries the throwaway port
    // the runner picked. Both are reduced to a leading-slash path with
    // no trailing slash, so a budget file never mentions a port and
    // `/pricing` and `/pricing/` are one page.

    /// Reduce a page reference — a path or a full URL — to its
    /// comparable path form.
    let normalisePagePath (raw: string) =
        let trimmed = (if isNull raw then "" else raw).Trim()

        let path =
            match Uri.TryCreate(trimmed, UriKind.Absolute) with
            | true, uri -> uri.AbsolutePath
            | _ -> trimmed

        let path =
            if path.StartsWith("/", StringComparison.Ordinal) then
                path
            else
                "/" + path

        if path.Length > 1 then path.TrimEnd '/' else "/"

    // ─── Budget parsing ──────────────────────────────────────────────

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

    /// Parse a budget document. Returns EVERY defect found rather than
    /// the first, so one run of the gate tells an author the whole story
    /// about a file they are editing by hand.
    let parseBudget (label: string) (json: string) : Result<CoreWebVitalsBudget, string list> =
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

                // ── schema token ──
                match tryProperty root "schema" with
                | Some s when s.ValueKind = JsonValueKind.String && s.GetString() = SchemaToken -> ()
                | Some s when s.ValueKind = JsonValueKind.String ->
                    errors.Add $"'{label}' declares schema '{s.GetString()}' — this gate reads '{SchemaToken}'."
                | _ -> errors.Add $"'{label}' declares no 'schema' — a budget file must declare '{SchemaToken}'."

                // ── label ──
                let budgetLabel =
                    match tryProperty root "label" with
                    | Some l when
                        l.ValueKind = JsonValueKind.String
                        && not (String.IsNullOrWhiteSpace(l.GetString()))
                        ->
                        l.GetString()
                    | _ -> label

                // ── pages ──
                let pages = ResizeArray<string>()

                match tryProperty root "pages" with
                | Some p when p.ValueKind = JsonValueKind.Array ->
                    for entry in p.EnumerateArray() do
                        if entry.ValueKind <> JsonValueKind.String then
                            errors.Add $"'{label}' has a non-string entry in 'pages' ({entry.ValueKind})."
                        elif String.IsNullOrWhiteSpace(entry.GetString()) then
                            errors.Add $"'{label}' has an empty entry in 'pages'."
                        else
                            pages.Add(normalisePagePath (entry.GetString()))

                    if pages.Count = 0 && errors.Count = 0 then
                        errors.Add $"'{label}' declares an empty 'pages' array — a budget must cover at least one page."
                | Some p -> errors.Add $"'{label}' has a 'pages' that is {p.ValueKind}, expected an array of URL paths."
                | None -> errors.Add $"'{label}' declares no 'pages' — a budget must name the pages it covers."

                let duplicates =
                    pages
                    |> Seq.countBy id
                    |> Seq.filter (fun (_, n) -> n > 1)
                    |> Seq.map fst
                    |> List.ofSeq

                for d in duplicates do
                    errors.Add $"'{label}' lists page '{d}' more than once."

                // ── metric ceilings ──
                let metrics = ResizeArray<CoreWebVitalsMetric * float>()

                match tryProperty root "metrics" with
                | Some m when m.ValueKind = JsonValueKind.Object ->
                    for prop in m.EnumerateObject() do
                        match CoreWebVitalsMetric.ofKey prop.Name with
                        | None ->
                            let known =
                                CoreWebVitalsMetric.all
                                |> List.map CoreWebVitalsMetric.key
                                |> String.concat ", "

                            errors.Add $"'{label}' declares unknown metric '{prop.Name}' — known metrics are {known}."
                        | Some metric ->
                            match asNumber prop.Value with
                            | Some v when v >= 0.0 -> metrics.Add(metric, v)
                            | Some v -> errors.Add $"'{label}' gives metric '{prop.Name}' a negative ceiling ({v})."
                            | None ->
                                errors.Add
                                    $"'{label}' gives metric '{prop.Name}' a non-numeric ceiling ({prop.Value.ValueKind})."
                | Some m -> errors.Add $"'{label}' has a 'metrics' that is {m.ValueKind}, expected an object."
                | None -> ()

                // ── category floors ──
                let categories = ResizeArray<LighthouseCategory * float>()

                match tryProperty root "categories" with
                | Some c when c.ValueKind = JsonValueKind.Object ->
                    for prop in c.EnumerateObject() do
                        match LighthouseCategory.ofKey prop.Name with
                        | None ->
                            let known =
                                LighthouseCategory.all |> List.map LighthouseCategory.key |> String.concat ", "

                            errors.Add
                                $"'{label}' declares unknown category '{prop.Name}' — known categories are {known}."
                        | Some category ->
                            match asNumber prop.Value with
                            | Some v when v >= 0.0 && v <= 1.0 -> categories.Add(category, v)
                            | Some v ->
                                errors.Add
                                    $"'{label}' gives category '{prop.Name}' a floor of {v} — Lighthouse category scores are in the 0.0-1.0 range."
                            | None ->
                                errors.Add
                                    $"'{label}' gives category '{prop.Name}' a non-numeric floor ({prop.Value.ValueKind})."
                | Some c -> errors.Add $"'{label}' has a 'categories' that is {c.ValueKind}, expected an object."
                | None -> ()

                if metrics.Count = 0 && categories.Count = 0 then
                    errors.Add
                        $"'{label}' asserts no thresholds at all — declare at least one 'metrics' ceiling or 'categories' floor."

                // ── server signals ──
                let serverSignals =
                    match tryProperty root "serverSignals" with
                    | None -> None
                    | Some s when s.ValueKind <> JsonValueKind.Object ->
                        errors.Add $"'{label}' has a 'serverSignals' that is {s.ValueKind}, expected an object."
                        None
                    | Some s ->
                        let mutable signals = CoreWebVitalsServerSignals.none

                        for prop in s.EnumerateObject() do
                            match prop.Name with
                            | "maxRenderMs" ->
                                match asNumber prop.Value with
                                | Some v when v >= 0.0 -> signals <- { signals with MaxRenderMs = Some v }
                                | _ ->
                                    errors.Add
                                        $"'{label}' gives serverSignals.maxRenderMs a value that is not a non-negative number."
                            | "minConditionalGet304Rate" ->
                                match asNumber prop.Value with
                                | Some v when v >= 0.0 && v <= 1.0 ->
                                    signals <- {
                                        signals with
                                            MinConditionalGet304Rate = Some v
                                    }
                                | _ ->
                                    errors.Add
                                        $"'{label}' gives serverSignals.minConditionalGet304Rate a value outside the 0.0-1.0 rate range."
                            | "required" ->
                                match prop.Value.ValueKind with
                                | JsonValueKind.True -> signals <- { signals with Required = true }
                                | JsonValueKind.False -> signals <- { signals with Required = false }
                                | k -> errors.Add $"'{label}' gives serverSignals.required a {k}, expected a boolean."
                            | other ->
                                errors.Add
                                    $"'{label}' declares unknown serverSignals key '{other}' — known keys are maxRenderMs, minConditionalGet304Rate, required."

                        if
                            Option.isNone signals.MaxRenderMs
                            && Option.isNone signals.MinConditionalGet304Rate
                        then
                            errors.Add
                                $"'{label}' declares a 'serverSignals' block that asserts nothing — give it a maxRenderMs or a minConditionalGet304Rate, or drop the block."

                        Some signals

                if errors.Count > 0 then
                    Error(List.ofSeq errors)
                else
                    Ok {
                        Label = budgetLabel
                        Pages = List.ofSeq pages
                        MetricCeilings = List.ofSeq metrics
                        CategoryFloors = List.ofSeq categories
                        ServerSignals = serverSignals
                    }
        finally
            if not (isNull (box document)) then
                document.Dispose()

    // ─── Lighthouse report parsing ───────────────────────────────────

    /// Reduce a Lighthouse JSON report to the values this gate decides
    /// on. An audit or category the report omits is simply absent from
    /// the result — the CHECK decides whether that absence matters,
    /// because only the budget knows what was supposed to be measured.
    let parseReport (label: string) (json: string) : Result<LighthousePageReport, string list> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error [ $"'{label}' must be a JSON object at its root, but is {root.ValueKind}." ]
            else
                let url =
                    [ "finalDisplayedUrl"; "finalUrl"; "requestedUrl" ]
                    |> List.tryPick (fun name ->
                        match tryProperty root name with
                        | Some v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                        | _ -> None)

                match url with
                | None ->
                    Error [
                        $"'{label}' carries no requestedUrl / finalUrl / finalDisplayedUrl — it is not a Lighthouse report."
                    ]
                | Some url ->
                    let audits = tryProperty root "audits"
                    let categories = tryProperty root "categories"

                    let metricValues = [
                        match audits with
                        | Some a when a.ValueKind = JsonValueKind.Object ->
                            for metric in CoreWebVitalsMetric.all do
                                match tryProperty a (CoreWebVitalsMetric.auditId metric) with
                                | Some audit when audit.ValueKind = JsonValueKind.Object ->
                                    match tryProperty audit "numericValue" |> Option.bind asNumber with
                                    | Some v -> metric, v
                                    | None -> ()
                                | _ -> ()
                        | _ -> ()
                    ]

                    let categoryScores = [
                        match categories with
                        | Some c when c.ValueKind = JsonValueKind.Object ->
                            for category in LighthouseCategory.all do
                                match tryProperty c (LighthouseCategory.reportId category) with
                                | Some entry when entry.ValueKind = JsonValueKind.Object ->
                                    match tryProperty entry "score" |> Option.bind asNumber with
                                    | Some v -> category, v
                                    | None -> ()
                                | _ -> ()
                        | _ -> ()
                    ]

                    Ok {
                        ReportLabel = label
                        PagePath = normalisePagePath url
                        MetricValues = metricValues
                        CategoryScores = categoryScores
                    }
        with ex ->
            Error [ $"'{label}' is not valid JSON — {ex.Message}" ]

    /// Parse a server-counter snapshot. The two conditional-GET counts
    /// are read under the tag values the public-rendering tier emits, so
    /// a snapshot is a straight transcription of the counter rather than
    /// a shape someone has to translate.
    let parseSnapshot (label: string) (json: string) : Result<RenderMetricsSnapshot, string list> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error [ $"'{label}' must be a JSON object at its root, but is {root.ValueKind}." ]
            else
                let renderMs =
                    tryProperty root "renderMsMax"
                    |> Option.bind asNumber
                    |> Option.orElseWith (fun () ->
                        tryProperty root "publicrendering.render_ms"
                        |> Option.bind (fun e -> tryProperty e "max")
                        |> Option.bind asNumber)

                let condGet name =
                    tryProperty root "conditionalGet"
                    |> Option.orElseWith (fun () -> tryProperty root "publicrendering.conditional_get")
                    |> Option.bind (fun e -> tryProperty e name)
                    |> Option.bind asNumber
                    |> Option.map int

                Ok {
                    SnapshotLabel = label
                    RenderMsMax = renderMs
                    ConditionalGet304 = condGet "304"
                    ConditionalGet200 = condGet "200"
                }
        with ex ->
            Error [ $"'{label}' is not valid JSON — {ex.Message}" ]

    // ─── The check (pure) ────────────────────────────────────────────

    /// Decide one run. Findings come back page-by-page in budget order,
    /// then the server signals, so a report diffs cleanly between runs.
    let check
        (budget: CoreWebVitalsBudget)
        (reports: LighthousePageReport list)
        (snapshot: RenderMetricsSnapshot option)
        : CoreWebVitalsFinding list =
        let byPage = reports |> List.map (fun r -> r.PagePath, r) |> Map.ofList
        let budgeted = budget.Pages |> Set.ofList

        let pageFindings = [
            for page in budget.Pages do
                match Map.tryFind page byPage with
                | None -> PageNotReported page
                | Some report ->
                    for (metric, ceiling) in budget.MetricCeilings do
                        match report.MetricValues |> List.tryFind (fst >> (=) metric) with
                        | None -> MetricNotReported(page, metric)
                        | Some(_, observed) when observed > ceiling ->
                            MetricCeilingBreached(page, metric, observed, ceiling)
                        | Some _ -> ()

                    for (category, floor) in budget.CategoryFloors do
                        match report.CategoryScores |> List.tryFind (fst >> (=) category) with
                        | None -> CategoryNotReported(page, category)
                        | Some(_, observed) when observed < floor ->
                            CategoryFloorBreached(page, category, observed, floor)
                        | Some _ -> ()
        ]

        let extraFindings = [
            for report in reports do
                if not (budgeted.Contains report.PagePath) then
                    PageNotBudgeted(report.PagePath, report.ReportLabel)
        ]

        let serverFindings = [
            match budget.ServerSignals, snapshot with
            | None, _ -> ()
            | Some signals, None -> ServerSignalsNotSampled signals.Required
            | Some signals, Some snap ->
                match signals.MaxRenderMs, snap.RenderMsMax with
                | Some ceiling, Some observed when observed > ceiling -> RenderMsBreached(observed, ceiling)
                | Some _, None -> ServerSignalsNotSampled signals.Required
                | _ -> ()

                match signals.MinConditionalGet304Rate, snap.ConditionalGet304, snap.ConditionalGet200 with
                | Some floor, Some hits, Some misses when hits + misses > 0 ->
                    let rate = float hits / float (hits + misses)

                    if rate < floor then
                        ConditionalGet304RateBreached(rate, floor)
                | Some _, _, _ -> ServerSignalsNotSampled signals.Required
                | _ -> ()
        ]

        pageFindings @ extraFindings @ (serverFindings |> List.distinct)

    /// The findings that fail the gate.
    let breaches (findings: CoreWebVitalsFinding list) =
        findings |> List.filter CoreWebVitalsFinding.isBreach

    /// Human-readable multi-line report. A clean run still names what
    /// was checked — a gate whose passing output is a blank line is one
    /// nobody notices has stopped running.
    let report (budget: CoreWebVitalsBudget) (findings: CoreWebVitalsFinding list) =
        let failing = breaches findings
        let advisory = findings |> List.filter (CoreWebVitalsFinding.isBreach >> not)

        let checkedCount =
            (List.length budget.Pages)
            * (List.length budget.MetricCeilings + List.length budget.CategoryFloors)

        let header =
            if List.isEmpty failing then
                $"[cwv-budget] '{budget.Label}' — within budget ({checkedCount} threshold(s) over {List.length budget.Pages} page(s))."
            else
                $"[cwv-budget] '{budget.Label}' — {List.length failing} budget breach(es) over {List.length budget.Pages} page(s):"

        let lines =
            (failing |> List.map (fun f -> "  " + CoreWebVitalsFinding.render f))
            @ (advisory |> List.map (fun f -> "  (advisory) " + CoreWebVitalsFinding.render f))

        String.Join(Environment.NewLine, header :: lines)

    // ─── Loading (the impure half) ───────────────────────────────────

    /// Reading budget / report / snapshot files off disk. Kept separate
    /// from `check` so the decision stays pure and fixture-testable.
    module Load =

        /// Parse the budget at `path`.
        let budget (path: string) : Result<CoreWebVitalsBudget, string list> =
            if not (File.Exists path) then
                Error [ $"budget file '{path}' does not exist." ]
            else
                parseBudget (Path.GetFileName path) (File.ReadAllText path)

        /// Parse every `*.json` in `directory` as a Lighthouse report,
        /// in a deterministic order. A directory with no reports is an
        /// ERROR — a run that measured nothing must never read as one
        /// that measured everything successfully.
        let reports (directory: string) : Result<LighthousePageReport list, string list> =
            if not (Directory.Exists directory) then
                Error [ $"reports directory '{directory}' does not exist." ]
            else
                let files =
                    Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                    |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
                    |> List.ofSeq

                if List.isEmpty files then
                    Error [ $"reports directory '{directory}' carries no *.json Lighthouse report." ]
                else
                    let parsed =
                        files
                        |> List.map (fun f -> parseReport (Path.GetFileName f) (File.ReadAllText f))

                    let errors =
                        parsed
                        |> List.collect (function
                            | Error e -> e
                            | Ok _ -> [])

                    if List.isEmpty errors then
                        Ok(
                            parsed
                            |> List.choose (function
                                | Ok r -> Some r
                                | Error _ -> None)
                        )
                    else
                        Error errors

        /// Parse the optional server-counter snapshot.
        let snapshot (path: string) : Result<RenderMetricsSnapshot, string list> =
            if not (File.Exists path) then
                Error [ $"server-metrics snapshot '{path}' does not exist." ]
            else
                parseSnapshot (Path.GetFileName path) (File.ReadAllText path)

    // ─── Call shapes ─────────────────────────────────────────────────

    /// Load everything the options name and run the check. A load or
    /// parse failure comes back as `Error` with every defect named — it
    /// is a gate failure in its own right, never a pass.
    let verify
        (options: CoreWebVitalsGateOptions)
        : Result<CoreWebVitalsBudget * CoreWebVitalsFinding list, string list> =
        match Load.budget options.BudgetFile, Load.reports options.ReportsDirectory with
        | Error b, Error r -> Error(b @ r)
        | Error b, Ok _ -> Error b
        | Ok _, Error r -> Error r
        | Ok budget, Ok reports ->
            match options.ServerMetricsFile with
            | None -> Ok(budget, check budget reports None)
            | Some path ->
                match Load.snapshot path with
                | Error e -> Error e
                | Ok snap -> Ok(budget, check budget reports (Some snap))

    /// Test-helper shape, mirroring the packaged-module conformance
    /// check in this same package: raises with the full report on any
    /// breach, returns unit when within budget. Framework-neutral — this
    /// package carries no test-framework dependency.
    let assertWithinBudget (options: CoreWebVitalsGateOptions) : unit =
        match verify options with
        | Error errors -> failwith (String.Join(Environment.NewLine, "[cwv-budget] could not run:" :: errors))
        | Ok(budget, findings) ->
            if not (List.isEmpty (breaches findings)) then
                failwith (report budget findings)

    /// FAKE's `Target` module cannot be reached fully-qualified from
    /// here — the same binding collision the packaged-module conformance
    /// target documents — so the FAKE surface is reached through a
    /// nested module that opens Fake.Core locally.
    module private FakeSurface =
        open Fake.Core

        let createTarget (name: string) (body: unit -> unit) = Target.create name (fun _ -> body ())

        let trace (text: string) = Trace.tracefn "%s" text

    /// Register the `VerifyCoreWebVitalsBudget` FAKE target. The runner
    /// script sets the three environment variables and invokes it as the
    /// last step of a gate run, after the browser measurement has
    /// written its reports:
    ///
    /// ```text
    /// TOOLUP_CWV_BUDGET=samples/PublicSite/cwv-budget.json
    /// TOOLUP_CWV_REPORTS=artifacts/cwv-reports
    /// TOOLUP_CWV_SERVER_METRICS=artifacts/cwv-reports/server-metrics.json   (optional)
    /// dotnet run --project Build.fsproj -- VerifyCoreWebVitalsBudget
    /// ```
    ///
    /// Options are resolved INSIDE the target body, not at registration:
    /// a repo registering this target must stay runnable for every other
    /// target with none of the variables set.
    let registerTarget () : unit =
        FakeSurface.createTarget "VerifyCoreWebVitalsBudget" (fun () ->
            match CoreWebVitalsGateOptions.fromEnvironment () with
            | Error errors ->
                failwithf
                    "VerifyCoreWebVitalsBudget: %s%s"
                    Environment.NewLine
                    (String.Join(Environment.NewLine, errors))
            | Ok options ->
                match verify options with
                | Error errors ->
                    failwithf
                        "VerifyCoreWebVitalsBudget: could not run.%s%s"
                        Environment.NewLine
                        (String.Join(Environment.NewLine, errors))
                | Ok(budget, findings) ->
                    let text = report budget findings
                    FakeSurface.trace text

                    if not (List.isEmpty (breaches findings)) then
                        failwithf
                            "VerifyCoreWebVitalsBudget: %d budget breach(es) against '%s'.%s%s"
                            (List.length (breaches findings))
                            budget.Label
                            Environment.NewLine
                            text)