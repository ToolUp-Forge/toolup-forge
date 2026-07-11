// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 85 — analytics → Narrative projection helpers.
///
/// The "analytics → readable, client-facing page body" bridge. A content
/// source ([`IContentSource`](IContentSource.fs), Phase 83) runs a backend
/// query, hands the result to a projector here, and gets back a
/// `NarrativeElement list` it can wrap in `ContentBody.Narrative` — which
/// then renders to HTML / Markdown / plaintext / Atom through the existing
/// Phase 80 renderers with NO hand-rolled markup.
///
/// Every projector is a PURE function from data to `NarrativeElement`
/// values (GP 5): the same input always produces the same element tree,
/// and every formatting decision is taken with `InvariantCulture` so a
/// prerender pass and a request-time render produce byte-identical output
/// regardless of the server's locale (the prerender-determinism rule). The
/// module is purely additive (GP 11) — nothing here changes the behaviour
/// of a page that doesn't use it.
///
/// ```fsharp
/// open ToolUp.PublicRendering
///
/// let body =
///     [ NarrativeFromData.table
///         [ "Region", TableAlignment.Left; "Spend", TableAlignment.Right ]
///         [ [ CellText "North"; CellMoney(12_500m, "£") ]
///           [ CellText "South"; CellMoney(9_300m, "£") ] ]
///       NarrativeFromData.metricGrid
///         [ "Total spend", "£21,800", Some 0.23
///           "Conversions", "1,204", Some -0.04 ]
///       NarrativeFromData.thresholdCallout
///         NarrativeFromData.spendThresholds 0.23 "Spend is 23% above target." ]
/// ```
///
/// The supporting types (`Cell`, `FormatOptions`, `Threshold`,
/// `SynthesisHook`, `NarrativeFromDataOptions`, …) sit at namespace level
/// — like `Slug` / `ContentBody` in `PublicContentTypes` — so a single
/// `open ToolUp.PublicRendering` brings the `Cell` constructors into
/// scope alongside the `NarrativeFromData.*` projector functions.
namespace ToolUp.PublicRendering

open System
open System.Globalization
open ToolUp.Platform.Narrative
open ProcessedDataTypes
open DataManagementTypes

// ─── Formatting (locale-stable) ──────────────────────────────────────

/// Display-formatting knobs for typed `Cell` values. Every default is
/// chosen so the output is deterministic and culture-independent: numbers
/// format under `InvariantCulture`, percentages and money carry FIXED
/// decimal places (so IEEE-754 fuzz on `fraction * 100` never leaks into
/// rendered bytes), and dates use ISO-8601-shaped patterns.
type FormatOptions = {
    /// Decimal places for `CellNumber` / `CellDecimal`. `None` =
    /// invariant round-trip (shortest exact representation, no grouping).
    Decimals: int option
    /// Group integer digits with the invariant thousands separator
    /// (`","`). Ignored on the `Decimals = None` round-trip path.
    GroupDigits: bool
    /// Fixed decimal places for `CellPercent`. Fixed (not round-trip)
    /// because `fraction * 100.0` surfaces IEEE-754 noise (`0.23 * 100.0
    /// = 23.000000000000004`) that a round-trip format would render
    /// verbatim. Default 1.
    PercentDecimals: int
    /// Fixed decimal places for `CellMoney`. Default 2.
    MoneyDecimals: int
    /// `CellDate` format string, applied with `InvariantCulture`.
    /// Default `"yyyy-MM-dd"`.
    DateFormat: string
    /// `CellTimestamp` format string, applied with `InvariantCulture`
    /// after normalising to UTC. Default `"yyyy-MM-dd HH:mm 'UTC'"`.
    TimestampFormat: string
    /// Labels for `CellBool` — `(whenTrue, whenFalse)`. Default
    /// `("Yes", "No")`.
    BoolLabels: string * string
    /// Placeholder text for `CellEmpty`. Default `"—"` (em dash).
    EmptyText: string
} with

    static member Default = {
        Decimals = None
        GroupDigits = true
        PercentDecimals = 1
        MoneyDecimals = 2
        DateFormat = "yyyy-MM-dd"
        TimestampFormat = "yyyy-MM-dd HH:mm 'UTC'"
        BoolLabels = ("Yes", "No")
        EmptyText = "—"
    }

/// A typed table cell. Formatting to display text happens at projection
/// time (`NarrativeFromData.formatCell`) under `FormatOptions`, so the
/// rendered output is locale-stable. `CellSpans` is the escape hatch for
/// a cell whose content the caller has already assembled as inline spans
/// (a link, a metric, mixed emphasis).
type Cell =
    | CellText of string
    | CellNumber of float
    | CellInt of int64
    | CellDecimal of decimal
    | CellMoney of amount: decimal * symbol: string
    /// A fraction (`0.23` renders as `"23.0%"`), NOT an
    /// already-multiplied percentage.
    | CellPercent of fraction: float
    | CellDate of DateTime
    | CellTimestamp of DateTimeOffset
    | CellBool of bool
    | CellEmpty
    | CellSpans of InlineSpan list

/// A `value >= AtLeast → Severity` rung of a threshold ladder. Build a
/// ladder as a `Threshold list`; `NarrativeFromData.severityFor` selects
/// the highest rung whose `AtLeast` the value clears.
type Threshold = { AtLeast: float; Severity: Severity }

/// A synthesis hook: given a projector's elements, optionally produce a
/// single leading summary element (typically a `Paragraph` or `Callout`).
/// `None` = prepend nothing. Pure / synchronous — keeps a projector
/// composition pure; use `AsyncSynthesisHook` when the summary comes from
/// an async AI call.
type SynthesisHook = NarrativeElement list -> NarrativeElement option

/// An async synthesis hook — the real-AI shape, where producing the
/// executive summary is an `Async` round-trip (e.g. a Phase 91 synthesis
/// call). Returns `None` to prepend nothing.
type AsyncSynthesisHook = NarrativeElement list -> Async<NarrativeElement option>

/// A projector for one module's `ProcessedData`: decodes the JSON
/// `Payload` (the consumer knows the concrete type) and projects it to
/// narrative elements. Registered per `TypeName` in a projector map —
/// see [`NarrativeFromDataProjectors`](NarrativeFromDataProjectors.fs)
/// for the registry builders.
type ProcessedProjector = ProcessedData -> NarrativeElement list

/// Options for `NarrativeFromData.fromProcessed`: the per-`TypeName`
/// projector registry, the formatting defaults projectors may consult,
/// and the severity used for the graceful "no projector registered"
/// callout.
type NarrativeFromDataOptions = {
    Projectors: Map<string, ProcessedProjector>
    Format: FormatOptions
    /// Severity of the callout emitted when a `ProcessedData.TypeName`
    /// has no registered projector. Default `Notice`.
    UnknownTypeSeverity: Severity
} with

    static member Default = {
        Projectors = Map.empty
        Format = FormatOptions.Default
        UnknownTypeSeverity = Notice
    }

/// Pure projectors from backend data into `NarrativeElement` trees. See
/// the file header for the end-to-end shape and the type docs above for
/// the supporting `Cell` / `FormatOptions` / `Threshold` vocabulary.
module NarrativeFromData =

    let private inv = CultureInfo.InvariantCulture

    let private numberFormatString (group: bool) (decimals: int) : string =
        let intPart = if group then "#,0" else "0"

        if decimals > 0 then
            intPart + "." + String('0', decimals)
        else
            intPart

    let private formatFloat (opts: FormatOptions) (f: float) : string =
        match opts.Decimals with
        | Some d -> f.ToString(numberFormatString opts.GroupDigits d, inv)
        | None -> f.ToString(inv)

    let private formatDecimal (opts: FormatOptions) (d: decimal) : string =
        match opts.Decimals with
        | Some n -> d.ToString(numberFormatString opts.GroupDigits n, inv)
        | None -> d.ToString(inv)

    let private formatInt (opts: FormatOptions) (i: int64) : string =
        if opts.GroupDigits then
            i.ToString("#,0", inv)
        else
            i.ToString(inv)

    let private formatMoney (opts: FormatOptions) (amount: decimal) : string =
        amount.ToString(numberFormatString opts.GroupDigits opts.MoneyDecimals, inv)

    let private formatPercent (opts: FormatOptions) (fraction: float) : string =
        (fraction * 100.0).ToString(numberFormatString false opts.PercentDecimals, inv)
        + "%"

    /// Human-readable byte size with a fixed one-decimal scale and
    /// `InvariantCulture` formatting (`1536L → "1.5 KB"`). Deterministic
    /// and allocation-light; used by the file-snapshot projector.
    let private formatBytes (bytes: int64) : string =
        let units = [| "B"; "KB"; "MB"; "GB"; "TB" |]
        let mutable size = float bytes
        let mutable i = 0

        while size >= 1024.0 && i < units.Length - 1 do
            size <- size / 1024.0
            i <- i + 1

        if i = 0 then
            sprintf "%d B" bytes
        else
            size.ToString("0.0", inv) + " " + units[i]

    /// Project one typed cell to the inline spans a `Table` row cell
    /// holds. Pure; every numeric / date branch formats under
    /// `InvariantCulture`.
    let formatCell (opts: FormatOptions) (cell: Cell) : InlineSpan list =
        match cell with
        | CellText s -> [ Text s ]
        | CellNumber f -> [ Text(formatFloat opts f) ]
        | CellInt i -> [ Text(formatInt opts i) ]
        | CellDecimal d -> [ Text(formatDecimal opts d) ]
        | CellMoney(amount, symbol) -> [ Text(symbol + formatMoney opts amount) ]
        | CellPercent fraction -> [ Text(formatPercent opts fraction) ]
        | CellDate d -> [ Text(d.ToString(opts.DateFormat, inv)) ]
        | CellTimestamp ts -> [ Text(ts.ToUniversalTime().ToString(opts.TimestampFormat, inv)) ]
        | CellBool b -> [ Text(if b then fst opts.BoolLabels else snd opts.BoolLabels) ]
        | CellEmpty -> [ Text opts.EmptyText ]
        | CellSpans spans -> spans

    // ─── Tabular projector ───────────────────────────────────────────

    /// Project columns + typed rows into a `Table` element, formatting
    /// every cell under the supplied `FormatOptions`. `columns` carries
    /// each header and its horizontal alignment (honoured by the Markdown
    /// and aligned-plaintext renderers); rows are formatted left-to-right.
    let tableWith
        (opts: FormatOptions)
        (columns: (string * TableAlignment) list)
        (rows: Cell list list)
        : NarrativeElement =
        Table(columns, rows |> List.map (List.map (formatCell opts)))

    /// `tableWith` under `FormatOptions.Default`. The canonical
    /// `columns -> rows -> NarrativeElement` projector.
    let table (columns: (string * TableAlignment) list) (rows: Cell list list) : NarrativeElement =
        tableWith FormatOptions.Default columns rows

    // ─── Metric / KPI projector ──────────────────────────────────────

    /// The inline delta span for a signed change fraction: an up / down
    /// arrow glyph carried as the metric label (the styling hook —
    /// renders inside `<span class="narrative-metric">`) plus the signed
    /// percentage. `+0.23 → ▲ +23.0%`, `-0.04 → ▼ -4.0%`, `0.0 → ■ 0.0%`.
    let private deltaSpan (opts: FormatOptions) (delta: float) : InlineSpan =
        let arrow =
            if delta > 0.0 then "▲"
            elif delta < 0.0 then "▼"
            else "■"

        let signPrefix = if delta > 0.0 then "+" else ""

        let magnitude =
            (delta * 100.0).ToString(numberFormatString false opts.PercentDecimals, inv)

        Metric(arrow, sprintf "%s%s%%" signPrefix magnitude, None)

    /// Project a list of `(label, value, delta)` KPIs into a
    /// `KeyValueGrid`: each label keys a row whose value is the
    /// (already-formatted) display string in `<strong>`, optionally
    /// followed by an up / down delta span (`Some fraction`) with arrow
    /// styling hooks. `None` delta = value only.
    let metricGridWith (opts: FormatOptions) (kpis: (string * string * float option) list) : NarrativeElement =
        let row (label: string, value: string, delta: float option) =
            let valueSpans =
                match delta with
                | Some d -> [ Strong value; Text " "; deltaSpan opts d ]
                | None -> [ Strong value ]

            (label, valueSpans)

        KeyValueGrid(kpis |> List.map row)

    /// `metricGridWith` under `FormatOptions.Default`.
    let metricGrid (kpis: (string * string * float option) list) : NarrativeElement =
        metricGridWith FormatOptions.Default kpis

    // ─── Fact-referencing projectors (Phase 521) ─────────────────────
    //
    // The fact-bearing counterparts of the metric projectors: they emit
    // `Metric` spans carrying the content-addressed id of the fact each
    // number was quoted from, so a narrative references facts rather than
    // copying them (plan D4). The fact-less overloads above are unchanged
    // (plan D17 — a fact-free app pays nothing and is byte-identical), and
    // a `None` fact ref on any row degrades to the fact-less rendering.

    /// A fact-referencing metric inline span — a labelled number that
    /// points into the fact base (Phase 521). `factRef = None` is
    /// byte-identical to a plain metric span; `Some factId` carries the
    /// live pointer, which renderers surface as an HTML `data-fact`
    /// attribute / a Markdown annotation. `factId` is an opaque id string,
    /// so this projector takes no dependency on the fact companion.
    let factMetric (label: string) (value: string) (factRef: string option) : InlineSpan = Metric(label, value, factRef)

    /// Fact-bearing KPI grid: like `metricGridWith`, but each KPI carries a
    /// fourth `factRef` slot. When a KPI's `factRef` is `Some`, its value
    /// renders as a fact-referencing `Metric` span (the number gains a live
    /// fact pointer); the grid key already names the KPI, so the span's own
    /// label is left empty. When `None`, the value renders as the plain
    /// `Strong value` the fact-less grid emits — so a null-fact row is
    /// byte-identical to `metricGridWith` (GP 11 / plan D17).
    let metricGridWithFactsWith
        (opts: FormatOptions)
        (kpis: (string * string * float option * string option) list)
        : NarrativeElement =
        let row (label: string, value: string, delta: float option, factRef: string option) =
            let valueSpan =
                match factRef with
                | Some _ -> Metric("", value, factRef)
                | None -> Strong value

            let valueSpans =
                match delta with
                | Some d -> [ valueSpan; Text " "; deltaSpan opts d ]
                | None -> [ valueSpan ]

            (label, valueSpans)

        KeyValueGrid(kpis |> List.map row)

    /// `metricGridWithFactsWith` under `FormatOptions.Default`.
    let metricGridWithFacts (kpis: (string * string * float option * string option) list) : NarrativeElement =
        metricGridWithFactsWith FormatOptions.Default kpis

    // ─── File-snapshot projector ─────────────────────────────────────

    /// Project a `FileListSnapshot` (the data-room / client-deliverables
    /// shape) into a processed-file status table: one row per uploaded
    /// file with type, size, row count, upload date, and a processed /
    /// pending / error status derived by joining `Processed` to `Files`
    /// on file name (case-insensitive). An empty snapshot degrades to a
    /// thoughtful empty-state callout rather than an empty table.
    let fromFileSnapshotWith (opts: FormatOptions) (snapshot: FileListSnapshot) : NarrativeElement list =
        if List.isEmpty snapshot.Files then
            [ Callout(Info, [ Text "No files have been processed yet." ]) ]
        else
            let processedByName =
                snapshot.Processed
                |> List.map (fun p -> p.FileName.ToLowerInvariant(), p)
                |> Map.ofList

            let columns = [
                "File", TableAlignment.Left
                "Type", TableAlignment.Left
                "Size", TableAlignment.Right
                "Rows", TableAlignment.Right
                "Uploaded", TableAlignment.Left
                "Status", TableAlignment.Left
            ]

            let statusCell (file: UploadedFileInfo) : Cell =
                match processedByName.TryFind(file.FileName.ToLowerInvariant()) with
                | Some p ->
                    match p.Error with
                    | Some e -> CellSpans [ Strong "Error"; Text(": " + e) ]
                    | None -> CellSpans [ Strong "OK" ]
                | None -> CellText "Pending"

            let row (file: UploadedFileInfo) : Cell list = [
                CellText file.FileName
                CellText file.DataType
                CellText(formatBytes file.SizeBytes)
                CellInt(int64 file.RowCount)
                CellDate file.UploadedAt
                statusCell file
            ]

            [ tableWith opts columns (snapshot.Files |> List.map row) ]

    /// `fromFileSnapshotWith` under `FormatOptions.Default`.
    let fromFileSnapshot (snapshot: FileListSnapshot) : NarrativeElement list =
        fromFileSnapshotWith FormatOptions.Default snapshot

    // ─── Callout / annotation helpers ────────────────────────────────

    /// A spend-vs-target delta-fraction ladder: ≥ 50% over → Critical,
    /// ≥ 20% over → Warning, ≥ 0 → Notice, below target → Info. A
    /// convenient default for the "spend up N% vs target" client-page
    /// shape; deployments with different tolerances build their own
    /// ladder.
    let spendThresholds: Threshold list = [
        { AtLeast = 0.50; Severity = Critical }
        { AtLeast = 0.20; Severity = Warning }
        { AtLeast = 0.0; Severity = Notice }
    ]

    /// Select the `Severity` for a value against a threshold ladder — the
    /// highest rung whose `AtLeast` the value meets or exceeds,
    /// defaulting to `Info` when the value clears no rung.
    let severityFor (thresholds: Threshold list) (value: float) : Severity =
        thresholds
        |> List.filter (fun t -> value >= t.AtLeast)
        |> List.sortByDescending _.AtLeast
        |> List.tryHead
        |> Option.map _.Severity
        |> Option.defaultValue Info

    /// Build a `Callout` whose severity is chosen by mapping `value`
    /// through a threshold ladder — the "spend up 23% vs target →
    /// Warning callout" shape for at-a-glance client pages.
    let thresholdCallout (thresholds: Threshold list) (value: float) (message: string) : NarrativeElement =
        Callout(severityFor thresholds value, [ Text message ])

    /// A plain severity-tagged annotation callout. Thin sugar over
    /// `Callout(severity, [ Text message ])` for the common single-line
    /// case.
    let annotate (severity: Severity) (message: string) : NarrativeElement = Callout(severity, [ Text message ])

    // ─── Composition with AI synthesis (optional) ────────────────────
    //
    // A projector's pure-data output can be prefaced by an AI-generated
    // executive-summary element when the deployment has composed an AI /
    // synthesis capability. The SDK ships NO implementation and takes NO
    // dependency on the AI / synthesis substrate — the hook is a plain
    // function the consumer supplies; absent one, the pure-data path is
    // used unchanged (GP 13 — zero cost when not composed).

    /// The no-op synthesis hook — never prepends a summary. The default
    /// for a deployment with no synthesis capability composed.
    let withoutSynthesis: SynthesisHook = fun _ -> None

    /// Prepend a synthesis hook's summary element (when it returns
    /// `Some`) to a projector's output. With `withoutSynthesis`, returns
    /// `elements` unchanged.
    let withSynthesis (hook: SynthesisHook) (elements: NarrativeElement list) : NarrativeElement list =
        match hook elements with
        | Some summary -> summary :: elements
        | None -> elements

    /// Prepend an async synthesis hook's summary element (when it returns
    /// `Some`) to a projector's output.
    let withSynthesisAsync (hook: AsyncSynthesisHook) (elements: NarrativeElement list) : Async<NarrativeElement list> = async {
        let! summary = hook elements

        return
            match summary with
            | Some s -> s :: elements
            | None -> elements
    }

    // ─── ProcessedData projector ─────────────────────────────────────

    /// Project a module's `ProcessedData` into narrative elements by
    /// routing on `TypeName` through the registered projector map. An
    /// unknown type degrades to a graceful callout (never an exception);
    /// a registered projector that throws is contained and degrades to a
    /// `Critical` callout, so one mis-decoding projector can't 500 the
    /// page.
    let fromProcessed (data: ProcessedData) (opts: NarrativeFromDataOptions) : NarrativeElement list =
        match opts.Projectors.TryFind data.TypeName with
        | Some projector ->
            try
                projector data
            with ex -> [
                Callout(
                    Critical,
                    [
                        Text(sprintf "Projector for data type '%s' failed: %s" data.TypeName ex.Message)
                    ]
                )
            ]
        | None -> [
            Callout(
                opts.UnknownTypeSeverity,
                [ Text(sprintf "No projector registered for data type '%s'." data.TypeName) ]
            )
          ]

    // ─── Chart projector (Phase 94) ──────────────────────────────────
    //
    // Project a labelled series into a `Component("chart", …)` block — the
    // sanctioned Narrative type-erasure seam (Phase 87), so no
    // `NarrativeElement` DU fork. The HTML render is supplied by
    // `NarrativeCharts` (a deterministic inline-SVG renderer registered
    // into the layout's component registry); Markdown / plaintext degrade
    // to the standard `[component: chart]` placeholder, so `chartTable`
    // gives a data-fidelity `Table` fallback for feeds / exports / print.
    //
    // The prop wire format mirrors `NarrativeCharts` exactly:
    //   "chart.kind" / "chart.title" / "chart.points" ("label=value;…").

    /// Chart kind. `Line` (default) / `Bar` / `Area`.
    type ChartKind =
        | Line
        | Bar
        | Area

    let private chartKindToken =
        function
        | Line -> "line"
        | Bar -> "bar"
        | Area -> "area"

    let private encodePoints (points: (string * float) list) : string =
        points
        |> List.map (fun (label, v) ->
            // Labels must not carry the `;` / `=` separators; sanitise.
            let safe = label.Replace(';', ' ').Replace('=', ' ')
            sprintf "%s=%s" safe (v.ToString(inv)))
        |> String.concat ";"

    /// Project a labelled numeric series into a chart `Component` block.
    /// `kind` selects line / bar / area; `title` is an optional caption.
    /// Renders to inline SVG in HTML (via `NarrativeCharts.registry`),
    /// degrades to a placeholder in Markdown / plaintext (pair with
    /// `chartTable` for a data fallback).
    let chart (kind: ChartKind) (title: string option) (points: (string * float) list) : NarrativeElement =
        let props =
            [ "chart.kind", chartKindToken kind; "chart.points", encodePoints points ]
            @ (match title with
               | Some t -> [ "chart.title", t ]
               | None -> [])
            |> Map.ofList

        Component("chart", props)

    /// A compact inline `Line` chart with no caption — the metric-row
    /// sparkline shape.
    let sparkline (points: (string * float) list) : NarrativeElement = chart Line None points

    /// The same series as a `Table` (label column + value column) — the
    /// data-fidelity fallback for Markdown / plaintext / Atom / CSV where
    /// an SVG isn't meaningful. Pair with `chart` (a chart for HTML, a
    /// table everywhere else) when full-fidelity degradation matters.
    let chartTable (labelHeader: string) (valueHeader: string) (points: (string * float) list) : NarrativeElement =
        let columns = [ labelHeader, TableAlignment.Left; valueHeader, TableAlignment.Right ]
        let rows = points |> List.map (fun (l, v) -> [ CellText l; CellNumber v ])
        tableWith FormatOptions.Default columns rows