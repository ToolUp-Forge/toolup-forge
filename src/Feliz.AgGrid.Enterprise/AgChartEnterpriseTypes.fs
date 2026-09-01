// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module AgChartEnterpriseTypes

// Phase 12e — Enterprise AG Charts series builders, mirroring the
// `AgGridEnterpriseTypes.fs` shape. These are erased option-builder types that
// emit NO JS imports: an Enterprise series is just an options object with a
// distinguishing `type` string, passed to the SAME `AgCharts` React component
// that Community charts use (`Feliz.AgCharts.agChart`, imported from
// the community `ag-charts-react` package). The sole
// `import "AgChartsEnterpriseModule" "ag-charts-enterprise"` (which registers
// the Enterprise chart modules at module-eval time) stays in
// AgGridEnterprise.fs — this file references none of the enterprise packages.
//
// Series confirmed Enterprise-only in AG Charts 13.3.0 (verified against
// node_modules/ag-charts-enterprise): sankey, sunburst, treemap, candlestick,
// ohlc, heatmap, waterfall, box-plot, range-bar, range-area (+ nightingale,
// radial-*, radar-* which stay obj escape hatches until a module needs them).
// Sparklines are a preset rendered through the community `AgCharts` component.
//
// Compiled after AgGridEnterpriseTypes.fs (see Feliz.AgGrid.Enterprise.fsproj),
// so `open Feliz.AgCharts` resolves the Community chart types this file
// builds on (ChartTooltipContent, IChartTooltipParams, agChart, MarkerShape).

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open Feliz.AgCharts

// ─── Hierarchy data (sunburst / treemap) ─────────────────────────

/// A node in a hierarchy for sunburst / treemap series. `children` is the
/// (possibly empty) child array; `data` carries the original datum. Pass a
/// root node's `children` (or a single-root array) as the series `data`.
type HierarchyNode<'data> = {
    name: string
    size: float option
    children: HierarchyNode<'data> array
    data: 'data option
}

module HierarchyNode =
    let leaf (name: string) (size: float) : HierarchyNode<'data> = {
        name = name
        size = Some size
        children = [||]
        data = None
    }

    let branch (name: string) (children: HierarchyNode<'data> array) : HierarchyNode<'data> = {
        name = name
        size = None
        children = children
        data = None
    }

    /// Pre-order flatten of a hierarchy into a flat array (root-first).
    let flatten (root: HierarchyNode<'data>) : HierarchyNode<'data> array =
        let acc = ResizeArray<HierarchyNode<'data>>()

        let rec go (n: HierarchyNode<'data>) =
            acc.Add n
            n.children |> Array.iter go

        go root
        acc.ToArray()

// ─── Standalone hierarchy series ─────────────────────────────────

/// Sankey flow diagram. `data` is the array of links (`from`/`to`/`size`).
[<Erase>]
type SankeyChartSeries =
    static member inline series = "type" ==> "sankey"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline fromKey(v: string) = "fromKey" ==> v
    static member inline toKey(v: string) = "toKey" ==> v
    static member inline sizeKey(v: string) = "sizeKey" ==> v
    static member inline labelKey(v: string) = "labelKey" ==> v
    static member inline nodeSizeKey(v: string) = "nodeSizeKey" ==> v
    /// `nodes` supplies explicit node metadata (label, fill, …).
    static member inline nodes(v: _ seq) = "nodes" ==> Seq.toArray v

    static member inline node(width: int, spacing: int, alignment: string) =
        "node"
        ==> {|
                width = width
                spacing = spacing
                alignment = alignment
            |}

    static member inline node(cfg: obj) = "node" ==> cfg
    static member inline link(cfg: obj) = "link" ==> cfg

    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| renderer = f |}

    static member inline create v = createObj v

/// Sunburst radial hierarchy. Data is `HierarchyNode` roots.
[<Erase>]
type SunburstChartSeries =
    static member inline series = "type" ==> "sunburst"
    static member inline data(v: HierarchyNode<'data> seq) = "data" ==> Seq.toArray v
    static member inline labelKey(v: string) = "labelKey" ==> v
    static member inline secondaryLabelKey(v: string) = "secondaryLabelKey" ==> v
    static member inline sizeKey(v: string) = "sizeKey" ==> v
    static member inline colorKey(v: string) = "colorKey" ==> v
    static member inline colorName(v: string) = "colorName" ==> v
    static member inline colorRange(v: string seq) = "colorRange" ==> Seq.toArray v
    static member inline childrenKey(v: string) = "childrenKey" ==> v
    static member inline gradient(v: bool) = "gradient" ==> v
    static member inline tile(cfg: obj) = "tile" ==> cfg
    static member inline padding(v: int) = "padding" ==> v

    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| renderer = f |}

    static member inline create v = createObj v

/// Treemap nested-rectangle hierarchy. Data is `HierarchyNode` roots.
[<Erase>]
type TreemapChartSeries =
    static member inline series = "type" ==> "treemap"
    static member inline data(v: HierarchyNode<'data> seq) = "data" ==> Seq.toArray v
    static member inline labelKey(v: string) = "labelKey" ==> v
    static member inline secondaryLabelKey(v: string) = "secondaryLabelKey" ==> v
    static member inline sizeKey(v: string) = "sizeKey" ==> v
    static member inline colorKey(v: string) = "colorKey" ==> v
    static member inline colorRange(v: string seq) = "colorRange" ==> Seq.toArray v
    static member inline childrenKey(v: string) = "childrenKey" ==> v
    static member inline nodePadding(v: int) = "nodePadding" ==> v

    static member inline tile(gap: int, padding: int) =
        "tile" ==> {| gap = gap; padding = padding |}

    static member inline tile(cfg: obj) = "tile" ==> cfg

    static member inline group(label: obj, fill: string, stroke: string) =
        "group"
        ==> {|
                label = label
                fill = fill
                stroke = stroke
            |}

    static member inline group(cfg: obj) = "group" ==> cfg

    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| renderer = f |}

    static member inline create v = createObj v

// ─── Financial cartesian series ──────────────────────────────────

/// Candlestick series (OHLC with filled bodies). `xKey` is the x-axis key
/// (the spec's `dateKey` — upstream renamed to xKey in 13.x; `dateKey`
/// alias provided for convenience).
[<Erase>]
type CandlestickSeries =
    static member inline series = "type" ==> "candlestick"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline dateKey(v: string) = "xKey" ==> v
    static member inline openKey(v: string) = "openKey" ==> v
    static member inline highKey(v: string) = "highKey" ==> v
    static member inline lowKey(v: string) = "lowKey" ==> v
    static member inline closeKey(v: string) = "closeKey" ==> v

    static member inline up(fill: string, stroke: string) =
        "item"
        ==> {|
                up = {| fill = fill; stroke = stroke |}
            |}

    static member inline down(fill: string, stroke: string) =
        "item"
        ==> {|
                down = {| fill = fill; stroke = stroke |}
            |}

    static member inline item(cfg: obj) = "item" ==> cfg

    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| renderer = f |}

    static member inline create v = createObj v

/// OHLC (open-high-low-close) bar series.
[<Erase>]
type OhlcSeries =
    static member inline series = "type" ==> "ohlc"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline dateKey(v: string) = "xKey" ==> v
    static member inline openKey(v: string) = "openKey" ==> v
    static member inline highKey(v: string) = "highKey" ==> v
    static member inline lowKey(v: string) = "lowKey" ==> v
    static member inline closeKey(v: string) = "closeKey" ==> v
    static member inline item(cfg: obj) = "item" ==> cfg

    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| renderer = f |}

    static member inline create v = createObj v

// ─── Range cartesian series (Enterprise in 13.3.0) ───────────────

/// Range bar (floating bar between yLowKey and yHighKey).
[<Erase>]
type RangeBarSeries =
    static member inline series = "type" ==> "range-bar"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline yLowKey(v: string) = "yLowKey" ==> v
    static member inline yHighKey(v: string) = "yHighKey" ==> v
    static member inline yName(v: string) = "yName" ==> v
    static member inline direction(v: string) = "direction" ==> v
    static member inline fill(v: string) = "fill" ==> v
    static member inline stroke(v: string) = "stroke" ==> v
    static member inline create v = createObj v

/// Range area (filled band between yLowKey and yHighKey).
[<Erase>]
type RangeAreaSeries =
    static member inline series = "type" ==> "range-area"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline yLowKey(v: string) = "yLowKey" ==> v
    static member inline yHighKey(v: string) = "yHighKey" ==> v
    static member inline yName(v: string) = "yName" ==> v
    static member inline fill(v: string) = "fill" ==> v
    static member inline fillOpacity(v: float) = "fillOpacity" ==> v
    static member inline stroke(v: string) = "stroke" ==> v
    static member inline create v = createObj v

// ─── Other Enterprise series (moved from Community — 13.3.0) ──────

/// Heatmap (x/y grid coloured by colorKey).
[<Erase>]
type HeatmapChartSeries =
    static member inline series = "type" ==> "heatmap"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline yKey(v: string) = "yKey" ==> v
    static member inline colorKey(v: string) = "colorKey" ==> v
    static member inline colorName(v: string) = "colorName" ==> v
    static member inline colorRange(v: string seq) = "colorRange" ==> Seq.toArray v
    static member inline xName(v: string) = "xName" ==> v
    static member inline yName(v: string) = "yName" ==> v
    static member inline create v = createObj v

/// Waterfall (running-total bar series).
[<Erase>]
type WaterfallChartSeries =
    static member inline series = "type" ==> "waterfall"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline yKey(v: string) = "yKey" ==> v
    static member inline xName(v: string) = "xName" ==> v
    static member inline yName(v: string) = "yName" ==> v
    static member inline item(cfg: obj) = "item" ==> cfg
    static member inline totals(v: _ seq) = "totals" ==> Seq.toArray v
    static member inline create v = createObj v

/// Box plot (min / q1 / median / q3 / max).
[<Erase>]
type BoxPlotSeries =
    static member inline series = "type" ==> "box-plot"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline minKey(v: string) = "minKey" ==> v
    static member inline q1Key(v: string) = "q1Key" ==> v
    static member inline medianKey(v: string) = "medianKey" ==> v
    static member inline q3Key(v: string) = "q3Key" ==> v
    static member inline maxKey(v: string) = "maxKey" ==> v
    static member inline yName(v: string) = "yName" ==> v
    static member inline fill(v: string) = "fill" ==> v
    static member inline stroke(v: string) = "stroke" ==> v
    static member inline create v = createObj v

// ─── Sparkline (preset component) ────────────────────────────────
//
// A sparkline is a small chart rendered through the SAME `AgCharts` component
// as full charts (no separate import). `MemoizedSparkline` mirrors the
// `MemoizedChart` JSON-memo pattern for animation stability. It MUST be
// non-`private` for the same reason `MemoizedChart` is: `SparklineOptions.chart`
// is a `static member inline` on an `[<Erase>]` type, so Fable inlines the body
// at each call site and imports `MemoizedSparkline` directly — a `private`
// binding yields a runtime "does not provide an export" SyntaxError.

[<ReactComponent>]
let MemoizedSparkline (reactProps: obj) =
    let prevJsonRef = React.useRef ""
    let stableRef = React.useRef reactProps
    let json = JS.JSON.stringify reactProps

    if json <> prevJsonRef.current then
        prevJsonRef.current <- json
        stableRef.current <- reactProps

    ReactLegacy.createElement (unbox<ReactElement> agChart, stableRef.current)

/// Sparkline preset builder. `type` is "bar" | "line" | "area". Build with
/// the member functions, then render via `SparklineOptions.chart`.
[<Erase>]
type SparklineOptions =
    static member inline bar = "type" ==> "bar"
    static member inline line = "type" ==> "line"
    static member inline area = "type" ==> "area"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline yKey(v: string) = "yKey" ==> v
    static member inline min(v: float) = "min" ==> v
    static member inline max(v: float) = "max" ==> v
    static member inline fill(v: string) = "fill" ==> v
    static member inline stroke(v: string) = "stroke" ==> v
    static member inline strokeWidth(v: int) = "strokeWidth" ==> v
    /// "vertical" | "horizontal".
    static member inline direction(v: string) = "direction" ==> v
    static member inline marker(enabled: bool) = "marker" ==> {| enabled = enabled |}
    static member inline width(v: int) = "width" ==> v
    static member inline height(v: int) = "height" ==> v

    static member inline padding(top: int, right: int, bottom: int, left: int) =
        "padding"
        ==> {|
                top = top
                right = right
                bottom = bottom
                left = left
            |}

    static member inline create v = createObj v

    /// Render the sparkline. Ensures Community modules are registered as a
    /// fallback (Enterprise pre-registration by AgGridEnterprise.fs wins).
    static member inline chart props =
        ensureChartsModulesRegistered ()
        MemoizedSparkline(createObj [ "options" ==> createObj !!props ])