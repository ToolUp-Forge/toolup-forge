// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 344 — backward-compatibility re-export for the AG Charts binding.
///
/// The binding moved to the standalone `Feliz.AgCharts` package and its
/// module was renamed `ToolUp.Platform.AgChart` -> `Feliz.AgCharts`. This
/// file keeps `open ToolUp.Platform.AgChart` compiling (GP 11). The two
/// residuals F# re-export cannot carry — bare record-label construction and
/// unqualified union-case forms — are documented on the sibling
/// `AgGridCompat.fs`; the same applies here.
///
/// ── Why `ChartPalette` is a TYPE here and a module there ─────────────
///
/// The palette is deployment branding held in module-level mutables, and
/// its whole point is that a consumer's boot path ASSIGNS to it
/// (`ChartPalette.accentColor <- ...`). A forwarding `let` would be a
/// read-only copy — silently accepting the write into a dead binding is
/// worse than not compiling — so the shim exposes it as a class with
/// static settable properties instead. `ChartPalette.accentColor <- x` and
/// `ChartPalette.refreshFromTheme ()` are source-identical either way; the
/// assignment lands on the real slot in `Feliz.AgCharts.ChartPalette`.
[<System.Obsolete "The AG Charts binding moved to the standalone Feliz.AgCharts package — `open Feliz.AgCharts` instead. This compat module is retired in a future minor.">]
module ToolUp.Platform.AgChart

#nowarn "44"

// ─── Module registration ─────────────────────────────────────────

/// Mark that chart modules have been registered externally (e.g. by AgGridEnterprise).
let setChartsModulesRegistered () =
    Feliz.AgCharts.setChartsModulesRegistered ()

let ensureChartsModulesRegistered () =
    Feliz.AgCharts.ensureChartsModulesRegistered ()

let agChart: obj = Feliz.AgCharts.agChart

// ─── Deployment brand palette ────────────────────────────────────

/// Chart theme/branding palette. Static properties forward to the mutable
/// slots in `Feliz.AgCharts.ChartPalette` — see the file header for why
/// this is a type rather than a module.
type ChartPalette =
    static member fills
        with get () = Feliz.AgCharts.ChartPalette.fills
        and set (v: string array) = Feliz.AgCharts.ChartPalette.fills <- v

    static member strokes
        with get () = Feliz.AgCharts.ChartPalette.strokes
        and set (v: string array) = Feliz.AgCharts.ChartPalette.strokes <- v

    static member accentColor
        with get () = Feliz.AgCharts.ChartPalette.accentColor
        and set (v: string) = Feliz.AgCharts.ChartPalette.accentColor <- v

    static member markerFill
        with get () = Feliz.AgCharts.ChartPalette.markerFill
        and set (v: string) = Feliz.AgCharts.ChartPalette.markerFill <- v

    static member markerStroke
        with get () = Feliz.AgCharts.ChartPalette.markerStroke
        and set (v: string) = Feliz.AgCharts.ChartPalette.markerStroke <- v

    static member fontFamily
        with get () = Feliz.AgCharts.ChartPalette.fontFamily
        and set (v: string) = Feliz.AgCharts.ChartPalette.fontFamily <- v

    /// Pull brand-derived chart colours from the live CSS theme once.
    static member refreshFromTheme() =
        Feliz.AgCharts.ChartPalette.refreshFromTheme ()

// ─── Enums + parameter shapes ────────────────────────────────────

type ChartPosition = Feliz.AgCharts.ChartPosition
type MarkerShape = Feliz.AgCharts.MarkerShape
type SeriesKind = Feliz.AgCharts.SeriesKind
type AxisKind = Feliz.AgCharts.AxisKind
type IChartFormatterParams = Feliz.AgCharts.IChartFormatterParams
type IChartTooltipParams<'datum> = Feliz.AgCharts.IChartTooltipParams<'datum>
type ChartTooltipContent = Feliz.AgCharts.ChartTooltipContent
type IMarkerParams<'T> = Feliz.AgCharts.IMarkerParams<'T>
type SeriesMarker = Feliz.AgCharts.SeriesMarker

// Case re-exports for the unions that are NOT [<RequireQualifiedAccess>] —
// see the union-case note in `AgGridCompat.fs`. `AxisKind` is
// qualified-access by declaration, so its abbreviation already carries it.
let Bottom = ChartPosition.Bottom
let Left = ChartPosition.Left
let Right = ChartPosition.Right
let Top = ChartPosition.Top

let Circle = MarkerShape.Circle
let Cross = MarkerShape.Cross
let Diamond = MarkerShape.Diamond
let Plus = MarkerShape.Plus
let Square = MarkerShape.Square
let Triangle = MarkerShape.Triangle

let Line = SeriesKind.Line
let Area = SeriesKind.Area
let Scatter = SeriesKind.Scatter
let Bar = SeriesKind.Bar
let Histogram = SeriesKind.Histogram

let NoMarker = SeriesMarker.NoMarker

/// `SeriesMarker.Marker` re-exported as a constructor function so
/// `Marker (shape, size, fill, stroke)` reads identically to the case
/// application it replaces.
let Marker (shape: MarkerShape, size: int, fill: string, stroke: string) =
    SeriesMarker.Marker(shape, size, fill, stroke)

type AxisLabel = Feliz.AgCharts.AxisLabel

module AxisLabel =
    let empty = Feliz.AgCharts.AxisLabel.empty

// ─── Axes, series, chart builders ────────────────────────────────

type Axis = Feliz.AgCharts.Axis
type CrosslineRange = Feliz.AgCharts.CrosslineRange
type ErrorBarCap = Feliz.AgCharts.ErrorBarCap
type ErrorBar = Feliz.AgCharts.ErrorBar
type Series = Feliz.AgCharts.Series
type PieSeries = Feliz.AgCharts.PieSeries
type BubbleSeries = Feliz.AgCharts.BubbleSeries

let MemoizedChart reactProps = Feliz.AgCharts.MemoizedChart reactProps

type LegendOptions = Feliz.AgCharts.LegendOptions

module LegendOptions =
    let empty = Feliz.AgCharts.LegendOptions.empty

type ChartThemeBuilder = Feliz.AgCharts.ChartThemeBuilder

module ChartThemeBuilder =
    let empty = Feliz.AgCharts.ChartThemeBuilder.empty

    /// Project the typed builder to the `theme` option object AG Charts reads.
    let toTheme (b: ChartThemeBuilder) =
        Feliz.AgCharts.ChartThemeBuilder.toTheme b

type AgChart = Feliz.AgCharts.AgChart