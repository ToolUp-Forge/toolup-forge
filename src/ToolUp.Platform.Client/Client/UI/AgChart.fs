// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AgChart

open Fable.Core
open Fable.Core.JsInterop
open Feliz

// AG Charts v13+ requires explicit module registration.
// Registration is deferred so AgGridEnterprise.registerCharts (called before Client.run)
// can register Enterprise modules first. If Enterprise hasn't registered by the
// time the first chart renders, Community modules are registered as fallback.
//
// Community fallback uses AgChartsCommunityModule.setup() which sets Integrated mode
// and registers all Community modules. This is fine for Community-only deployments.
// Enterprise registration must NOT use setup() — see AgGridEnterprise.registerCharts.

let private agChartsCommunityModule: obj =
    import "AgChartsCommunityModule" "ag-charts-community"

// Sanctioned module-level mutable: per-tab one-shot registration guard
// (AG Charts modules register once; Enterprise pre-registration flips
// it via `setChartsModulesRegistered` before the Community fallback).
let mutable private chartsModulesRegistered = false

/// Mark that chart modules have been registered externally (e.g. by AgGridEnterprise).
let setChartsModulesRegistered () = chartsModulesRegistered <- true

let ensureChartsModulesRegistered () =
    if not chartsModulesRegistered then
        agChartsCommunityModule?setup ()
        chartsModulesRegistered <- true

let agChart: obj = import "AgCharts" "ag-charts-react"

/// Chart theme/branding palette (GP 5 mutable exception). These are
/// deployment-branding overrides: the sole writer is the consumer's
/// composition/boot path (set once, before any chart renders); every
/// reader is `AgChart.options` on the render path. Intentionally NOT
/// `private` — `options` is a `static member inline` on the erased
/// `AgChart` type, so Fable inlines the body at each call site and must
/// export these module values (same constraint as `MemoizedChart`);
/// marking them private yields a runtime "does not provide an export"
/// SyntaxError.
module ChartPalette =
    let mutable fills = [| "#8066E8"; "#59229D"; "#9BC53D"; "#E55934"; "#FA7921" |]
    let mutable strokes = [| "#866BE8"; "#59229D"; "#9BC53D"; "#E55934"; "#FA7921" |]
    let mutable accentColor = "#59229D"
    let mutable markerFill = "#811682"
    let mutable markerStroke = "#811682"
    let mutable fontFamily = "Inter, sans-serif"

    // Phase 222: resolve a CSS custom property from :root to a literal string.
    // AG Charts series strokes/fills must be literal colours (not CSS utilities
    // or var() refs), so the live theme value is read here at render time. The
    // "#59229D" fallback equals the SDK brand default, so a no-DOM / no-theme
    // context is byte-for-byte unchanged.
    let private cssVar (name: string) (fallback: string) =
        try
            let style: obj =
                Browser.Dom.window?getComputedStyle (Browser.Dom.document.documentElement)

            let v: string = style?getPropertyValue (name)

            if System.String.IsNullOrWhiteSpace v then
                fallback
            else
                v.Trim()
        with _ ->
            fallback

    let mutable private themed = false

    /// Pull brand-derived chart colours from the live CSS theme (`--color-brand`)
    /// once, at first render — so charts follow a consumer's / per-team palette
    /// override instead of the frozen `#59229D` literal. Only the slots still at
    /// their built-in brand default are replaced, so an explicit consumer
    /// override (set at boot) is preserved. Must be public: `AgChart.options` is
    /// `inline` and calls this at each render site.
    let refreshFromTheme () =
        if not themed then
            themed <- true
            let brand = cssVar "--color-brand" "#59229D"

            if accentColor = "#59229D" then
                accentColor <- brand
                fills <- [| fills[0]; brand; fills[2]; fills[3]; fills[4] |]
                strokes <- [| strokes[0]; brand; strokes[2]; strokes[3]; strokes[4] |]

type ChartPosition =
    | Bottom
    | Left
    | Right
    | Top

type MarkerShape =
    | Circle
    | Cross
    | Diamond
    | Plus
    | Square
    | Triangle

type SeriesKind =
    | Line
    //| Column Doesn't exist in AG Charts -- use Bar
    | Area
    | Scatter
    | Bar
    // Phase 12e — Community cartesian addition. range-bar / range-area /
    // waterfall / box-plot are ENTERPRISE-only in AG Charts 13.3.0 (verified
    // against node_modules/ag-charts-enterprise) and live in the Enterprise
    // charts companion; only Histogram is Community.
    | Histogram

    member this.SeriesKindText =
        match this with
        | Line -> "line"
        | Area -> "area"
        | Scatter -> "scatter"
        | Bar -> "bar"
        | Histogram -> "histogram"

[<RequireQualifiedAccess>]
type AxisKind =
    | Category
    | Number
    | Time

// ─── Formatter / tooltip param records (Phase 12e) ───────────────
// Shared by axis + series value/label formatters and tooltip renderers.
// See node_modules/ag-charts-types/dist/types/src/.

/// Params passed to an axis / series value or label formatter.
type IChartFormatterParams = {
    value: obj
    index: int
    fractionDigits: int
    tickInterval: obj
}

/// Params passed to a series tooltip renderer. `datum` is the row bound to
/// the hovered node; the returned record shapes the tooltip DOM.
type IChartTooltipParams<'datum> = {
    datum: 'datum
    xKey: string
    yKey: string
    xValue: obj
    yValue: obj
    title: string
    color: string
}

/// Typed tooltip content returned from a renderer.
type ChartTooltipContent = {
    title: string
    content: string
    backgroundColor: string
    color: string
}

/// Typed axis-label configuration (Phase 12e). All fields optional — None
/// erases to undefined. Replaces the ad-hoc `label(int)` / `label(obj)`
/// overloads (both retained for back-compat).
type AxisLabel = {
    fontSize: int option
    fontFamily: string option
    fontWeight: string option
    color: string option
    format: string option
    formatter: (IChartFormatterParams -> string) option
    padding: int option
    rotation: int option
    autoRotate: bool option
    autoRotateAngle: int option
    avoidCollisions: bool option
    fractionDigits: int option
    minSpacing: int option
    enabled: bool option
}

module AxisLabel =
    let empty: AxisLabel = {
        fontSize = None
        fontFamily = None
        fontWeight = None
        color = None
        format = None
        formatter = None
        padding = None
        rotation = None
        autoRotate = None
        autoRotateAngle = None
        avoidCollisions = None
        fractionDigits = None
        minSpacing = None
        enabled = None
    }

[<Erase>]
type Axis =
    static member inline axisKind(axisKind: AxisKind) =
        "type" ==> axisKind.ToString().ToLower()

    static member inline position(v: ChartPosition) = "position" ==> v.ToString().ToLower()

    static member inline title(v: string) =
        "title" ==> {| enabled = true; text = v |}

    static member inline create v = createObj v
    static member inline label(rotation: int) = "label" ==> {| rotation = rotation |}
    static member inline label(options: obj) = "label" ==> options

    /// Typed axis label config (Phase 12e).
    static member inline label(cfg: AxisLabel) = "label" ==> cfg

    // ─── Axis completion (Phase 12e) ────────────────────────────

    static member inline gridLine(enabled: bool) = "gridLine" ==> {| enabled = enabled |}

    static member inline gridLine(enabled: bool, style: obj) =
        "gridLine" ==> {| enabled = enabled; style = style |}

    static member inline tick(enabled: bool) = "tick" ==> {| enabled = enabled |}

    /// Typed tick config: size / color / count / interval / values are all
    /// optional (pass an anonymous record with the fields you need).
    static member inline tick(cfg: obj) = "tick" ==> cfg

    /// Auto-extend the domain to "nice" round numbers.
    static member inline nice(v: bool) = "nice" ==> v

    /// Invert the axis (reverse the value direction).
    static member inline inverted(v: bool) = "reverse" ==> v

    /// Category-axis reversal (order of categories).
    static member inline reverse(v: bool) = "reverse" ==> v

    /// Multi-key stacked time / category axis.
    static member inline keys(v: string seq) = "keys" ==> Seq.toArray v

    /// Numeric / time tick interval.
    static member inline interval(v: float) = "interval" ==> {| step = v |}

    static member inline interval(cfg: obj) = "interval" ==> cfg

    static member inline crosshair(enabled: bool) = "crosshair" ==> {| enabled = enabled |}
    static member inline min(value: float) = "min" ==> value
    static member inline max(value: float) = "max" ==> value
    //static member inline thickness(value: int) = "thickness" ==> value
    static member inline line(show: bool) =
        "line"
        ==> {|
                enabled = true
                width = 1
                stroke = "#000000"
            |}

    static member inline crossAt(v: float) =
        "crossAt" ==> {| value = v; sticky = true |}

    static member inline crosslines(f: float seq) =
        "crossLines"
        ==> [|
            for v in f do
                {|
                    ``type`` = "line"
                    value = v
                    stroke = "#000000"
                    strokeWidth = 1
                |}
        |]

    static member inline crosslines(f: CrosslineRange seq) =
        "crossLines"
        ==> [|
            for v in f do
                {|
                    ``type`` = "range"
                    range = JS.Constructors.Array.from [| v.low; v.high |]
                    fill = v.colour
                    strokeWidth = 1
                    stroke = "#000000"
                    fillOpacity = v.fillOpacity
                |}
        |]

and CrosslineRange = {
    low: float
    high: float
    colour: string
    fillOpacity: float
}

type IMarkerParams<'T> = {
    datum: 'T
    fill: string
    stroke: string
    strokeWidth: int
    size: int
    highlighted: bool
    xKey: string
    yKey: string
}

type SeriesMarker =
    | NoMarker
    | Marker of MarkerShape * int * string * string

[<Erase>]
type ErrorBarCap =
    static member inline stroke(v: string) = "stroke" ==> v
    static member inline strokeWidth(v: int) = "strokeWidth" ==> v
    static member inline length(v: float) = "length" ==> v
    static member inline lengthRatio(v: float) = "lengthRatio" ==> v
    static member inline create v = createObj v

[<Erase>]
type ErrorBar =
    static member inline yLowerKey(v: string) = "yLowerKey" ==> v
    static member inline yUpperKey(v: string) = "yUpperKey" ==> v
    static member inline xLowerKey(v: string) = "xLowerKey" ==> v
    static member inline xUpperKey(v: string) = "xUpperKey" ==> v
    static member inline visible(v: bool) = "visible" ==> v
    static member inline stroke(v: string) = "stroke" ==> v
    static member inline strokeWidth(v: int) = "strokeWidth" ==> v
    static member inline cap(v: obj) = "cap" ==> v
    static member inline create v = createObj v

[<Erase>]
type Series =
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline normalizedTo(v: int) = "normalizedTo" ==> v
    static member inline seriesKind(v: SeriesKind) = "type" ==> v.ToString().ToLower()
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline xKey(v: 'a -> string) = "xKey" ==> v (unbox null)
    static member inline xName v = "xName" ==> v
    static member inline yKey(v: string) = "yKey" ==> v
    static member inline yName(v: string) = "yName" ==> v
    static member inline yKeys(v: string seq) = "yKeys" ==> Seq.toArray v

    static member inline interpolation = "interpolation" ==> {| ``type`` = "smooth" |}

    static member inline yKeys(v: 'a -> #seq<string>) =
        "yKeys" ==> (v (unbox null) |> Seq.toArray)

    static member inline yNames(v: string seq) = "yNames" ==> Seq.toArray v
    static member inline visible(v: bool) = "visible" ==> v
    static member inline showInLegend(v: bool) = "showInLegend" ==> v
    static member inline tooltipEnabled(v: bool) = "tooltipEnabled" ==> v
    static member inline highlight(enabled: bool) = "highlight" ==> {| enabled = enabled |}
    static member inline id(v: string) = "id" ==> v
    //static member inline label(v: bool) = "label" ==> {| enabled = v |}
    static member inline label(enabled: bool) = "label" ==> {| enabled = enabled |}
    static member inline labelKey(v: string) = "labelKey" ==> v

    static member inline marker(m: SeriesMarker) = //v: MarkerShape, size: int, fill: string, stroke: string

        match m with
        | NoMarker -> "marker" ==> {| enabled = false |}
        | Marker(shape, size, fill, stroke) ->

            "marker"
            ==> {|
                    shape = shape.ToString().ToLower()
                    size = size
                    fill = fill
                    stroke = stroke
                |}

    static member inline fill(v: string) = "fill" ==> v
    static member inline fills(v: string seq) = "fills" ==> Seq.toArray v
    static member inline strokes(v: string seq) = "strokes" ==> v
    static member inline stroke(v: string) = "stroke" ==> v
    static member inline strokeWidth(v: int) = "strokeWidth" ==> v
    static member inline fillOpacity(v: float) = "fillOpacity" ==> v
    static member inline errorBar(v: obj) = "errorBar" ==> v

    // ─── Series completion (Phase 12e) ──────────────────────────
    static member inline title(v: string) = "title" ==> v

    /// Stacked bar / area within the default stack group.
    static member inline stacked(v: bool) = "stacked" ==> v

    /// Named stack group (multiple independent stacks on one chart).
    static member inline stackGroup(v: string) = "stackGroup" ==> v

    static member inline grouped(v: bool) = "grouped" ==> v
    static member inline cornerRadius(v: int) = "cornerRadius" ==> v
    static member inline connectMissingData(v: bool) = "connectMissingData" ==> v

    // Histogram (Community): bins as explicit ranges, or a target count.
    static member inline bins(v: (float * float) seq) =
        "bins" ==> [| for (lo, hi) in v -> [| lo; hi |] |]

    static member inline binCount(v: int) = "binCount" ==> v

    /// Histogram aggregation — "count" | "sum" | "mean".
    static member inline aggregation(v: string) = "aggregation" ==> v

    static member inline areaPlot(v: bool) = "areaPlot" ==> v

    /// Typed tooltip renderer (Phase 12e). Receives the hovered datum + keys,
    /// returns the tooltip content record.
    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| renderer = f |}

    /// Typed data-label formatter for the series.
    static member inline labelFormatter(f: IChartFormatterParams -> string) =
        "label" ==> {| enabled = true; formatter = f |}

    static member inline create v = createObj v

// ─── Pie / Donut series (Phase 12e — Community) ──────────────────
// Separate builder: the polar option shape (angleKey / radiusKey / callout)
// diverges from the cartesian xKey/yKey families. `type` is "pie" | "donut".
// See node_modules/ag-charts-types/dist/types/src/series/polar/pieOptions.d.ts.
[<Erase>]
type PieSeries =
    static member inline pie = "type" ==> "pie"
    static member inline donut = "type" ==> "donut"
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline angleKey(v: string) = "angleKey" ==> v
    static member inline angleName(v: string) = "angleName" ==> v
    static member inline radiusKey(v: string) = "radiusKey" ==> v
    static member inline radiusName(v: string) = "radiusName" ==> v
    static member inline sectorLabelKey(v: string) = "sectorLabelKey" ==> v
    static member inline calloutLabelKey(v: string) = "calloutLabelKey" ==> v
    static member inline legendItemKey(v: string) = "legendItemKey" ==> v
    static member inline innerRadius(v: float) = "innerRadius" ==> v
    static member inline innerRadiusRatio(v: float) = "innerRadiusRatio" ==> v
    static member inline rotation(v: float) = "rotation" ==> v
    static member inline fills(v: string seq) = "fills" ==> Seq.toArray v
    static member inline strokes(v: string seq) = "strokes" ==> Seq.toArray v
    static member inline fillOpacity(v: float) = "fillOpacity" ==> v
    static member inline strokeWidth(v: int) = "strokeWidth" ==> v
    static member inline title(v: string) = "title" ==> {| text = v |}
    static member inline showInLegend(v: bool) = "showInLegend" ==> v

    static member inline calloutLabel(enabled: bool) =
        "calloutLabel" ==> {| enabled = enabled |}

    static member inline sectorLabel(enabled: bool) =
        "sectorLabel" ==> {| enabled = enabled |}

    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| renderer = f |}

    static member inline create v = createObj v

// ─── Bubble series (Phase 12e — Community) ───────────────────────
// Cartesian series with a third (size) dimension. `sizeDomain` is the v13.3
// key (the spec's `domain` was renamed upstream).
[<Erase>]
type BubbleSeries =
    static member inline data(v: _ seq) = "data" ==> Seq.toArray v
    static member inline xKey(v: string) = "xKey" ==> v
    static member inline yKey(v: string) = "yKey" ==> v
    static member inline sizeKey(v: string) = "sizeKey" ==> v
    static member inline sizeName(v: string) = "sizeName" ==> v
    static member inline sizeDomain(lo: float, hi: float) = "sizeDomain" ==> [| lo; hi |]
    static member inline labelKey(v: string) = "labelKey" ==> v
    static member inline xName(v: string) = "xName" ==> v
    static member inline yName(v: string) = "yName" ==> v
    static member inline title(v: string) = "title" ==> v
    static member inline fill(v: string) = "fill" ==> v
    static member inline stroke(v: string) = "stroke" ==> v
    static member inline fillOpacity(v: float) = "fillOpacity" ==> v
    static member inline marker(m: SeriesMarker) = Series.marker m
    static member inline showInLegend(v: bool) = "showInLegend" ==> v

    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| renderer = f |}

    /// Bubble series always carries `type = "bubble"`; include this in the
    /// builder list (or set xKey once — see below).
    static member inline seriesType = "type" ==> "bubble"
    static member inline create v = createObj v

/// Wrapper component that memoizes the React-props object for ag-charts-react
/// by JSON equality. AG Charts v13.2.1 regression: any chart.update() call
/// while an animation is active sets _performUpdateSkipAnimations=true,
/// killing the animation. Because ag-charts-react's useEffect([options]) fires
/// on every parent re-render (createObj always produces a new reference),
/// spurious chart.update() calls were killing animations. This wrapper ensures
/// ag-charts-react only receives a new reference when props have semantically
/// changed.
///
/// The caller (AgChart.chart) produces a React-props object already shaped as
/// { options: {...chartConfig...} } — which is what ag-charts-react expects.
/// We pass it straight to createElement; wrapping it again would produce
/// { options: { options: {...} } }, and AG Charts would warn
/// "Unknown option `options`, ignoring" when iterating the outer object.
[<ReactComponent>]
let MemoizedChart (reactProps: obj) =
    let prevJsonRef = React.useRef ""
    let stableRef = React.useRef reactProps
    let json = JS.JSON.stringify reactProps

    if json <> prevJsonRef.current then
        prevJsonRef.current <- json
        stableRef.current <- reactProps

    ReactLegacy.createElement (unbox<ReactElement> agChart, stableRef.current)

// ─── Legend / theme option records (Phase 12e) ───────────────────

/// Typed legend configuration, replacing the 3-positional `legend` overload
/// (retained for back-compat). All fields optional.
/// See node_modules/ag-charts-types/dist/types/src/chart/legendOptions.d.ts.
type LegendOptions = {
    enabled: bool option
    position: ChartPosition option
    spacing: int option
    /// item = {| marker = {| shape; size; padding; strokeWidth |};
    ///           label = {| fontSize; fontFamily; color; maxLength |};
    ///           paddingX; paddingY |}
    item: obj option
    /// pagination = {| marker = {| size |} |}
    pagination: obj option
    listeners: obj option
    reverseOrder: bool option
    maxWidth: int option
    maxHeight: int option
}

module LegendOptions =
    let empty: LegendOptions = {
        enabled = None
        position = None
        spacing = None
        item = None
        pagination = None
        listeners = None
        reverseOrder = None
        maxWidth = None
        maxHeight = None
    }

/// Typed one-chart theme override (Phase 12e). Complements the deployment-wide
/// `ChartPalette` mutable brand defaults — this lets a single chart override
/// base theme + palette + params + overrides.
/// See node_modules/ag-charts-types/dist/types/src/chart/themeOptions.d.ts.
type ChartThemeBuilder = {
    /// "ag-default" | "ag-material" | "ag-sheets" | "ag-polychroma" | ...,
    /// or "ag-default-dark" variants.
    baseTheme: string option
    /// palette = {| fills = string[]; strokes = string[] |}
    palette: obj option
    /// params = {| fontFamily; accentColor; backgroundColor; ... |}
    ``params``: obj option
    /// overrides = {| common = {| ... |}; line = {| ... |}; bar = {| ... |} |}
    overrides: obj option
}

module ChartThemeBuilder =
    let empty: ChartThemeBuilder = {
        baseTheme = None
        palette = None
        ``params`` = None
        overrides = None
    }

    /// Project the typed builder to the `theme` option object AG Charts reads.
    let toTheme (b: ChartThemeBuilder) : obj = box b

[<Erase>]
type AgChart =
    static member inline title(v: string) = "title" ==> {| text = v |}
    static member inline subtitle(v: string) = "subtitle" ==> {| text = v |}
    static member inline navigator = "navigator" ==> {| enabled = true |}
    static member inline width(v: int) = "width" ==> v
    static member inline height(v: int) = "height" ==> v
    static member inline autoSize = "autoSize" ==> true
    static member inline animation(enabled: bool) = "animation" ==> {| enabled = enabled |}

    static member inline animation(enabled: bool, duration: int) =
        "animation"
        ==> {|
                enabled = enabled
                duration = duration
            |}

    static member inline listeners(v: obj) = "listeners" ==> v

    static member inline legend(enabled: bool, spacing: int, position: ChartPosition) =
        "legend"
        ==> {|
                enabled = enabled
                spacing = spacing
                position = position.ToString().ToLower()
            |}

    /// Typed legend options (Phase 12e). `position` maps to its lower-case
    /// string; other fields pass through. None-valued fields erase.
    static member inline legend(opts: LegendOptions) =
        let positioned: obj =
            match opts.position with
            | Some p -> box (p.ToString().ToLower())
            | None -> box null

        "legend"
        ==> {|
                enabled = opts.enabled
                position = positioned
                spacing = opts.spacing
                item = opts.item
                pagination = opts.pagination
                listeners = opts.listeners
                reverseOrder = opts.reverseOrder
                maxWidth = opts.maxWidth
                maxHeight = opts.maxHeight
            |}

    // ─── AgChart completion (Phase 12e) ─────────────────────────

    /// Solid background fill.
    static member inline background(fill: string) = "background" ==> {| fill = fill |}

    /// Background image / gradient (pass the AG Charts `IBackground` object).
    static member inline backgroundImage(image: obj) = "background" ==> {| image = image |}

    /// Typed chart-level tooltip (enabled / range / delay / renderer).
    static member inline tooltip(enabled: bool) = "tooltip" ==> {| enabled = enabled |}

    static member inline tooltip(cfg: obj) = "tooltip" ==> cfg

    static member inline tooltipRenderer(f: IChartTooltipParams<'datum> -> ChartTooltipContent) =
        "tooltip" ==> {| enabled = true; renderer = f |}

    /// Chart-level crosshair (applies via axis overrides).
    static member inline crosshair(enabled: bool, snap: bool, label: bool) =
        "crosshair"
        ==> {|
                enabled = enabled
                snap = snap
                label = {| enabled = label |}
            |}

    /// Chart-group synchronisation: charts sharing a `group` sync the named
    /// dimensions ("x" / "y" both by default) and optionally zoom.
    static member inline sync(group: string, axes: string, zoom: bool) =
        "sync"
        ==> {|
                enabled = true
                group = group
                axes = axes
                nodeInteraction = true
                zoom = zoom
            |}

    /// Chart caption (below title). Position / spacing optional via `cfg`.
    static member inline caption(text: string) = "caption" ==> {| text = text |}
    static member inline caption(cfg: obj) = "caption" ==> cfg

    /// Chart footnote.
    static member inline footnote(text: string) = "footnote" ==> {| text = text |}
    static member inline footnote(cfg: obj) = "footnote" ==> cfg

    static member inline padding(top: int, right: int, bottom: int, left: int) =
        "padding"
        ==> {|
                top = top
                right = right
                bottom = bottom
                left = left
            |}

    static member inline padding(all: int) =
        "padding"
        ==> {|
                top = all
                right = all
                bottom = all
                left = all
            |}

    static member inline minWidth(v: int) = "minWidth" ==> v
    static member inline minHeight(v: int) = "minHeight" ==> v

    /// Locale object (`AgChartLocale`) for number / date formatting + a11y text.
    static member inline locale(v: obj) = "locale" ==> v

    // Typed listener wrappers (raw `listeners(obj)` remains as escape hatch).
    static member inline onClick(f: obj -> unit) = "listeners" ==> {| click = f |}

    static member inline onDoubleClick(f: obj -> unit) = "listeners" ==> {| doubleClick = f |}

    static member inline onSeriesNodeClick(f: obj -> unit) =
        "listeners" ==> {| seriesNodeClick = f |}

    static member inline onLegendItemClick(f: obj -> unit) =
        "legend"
        ==> {|
                listeners = {| legendItemClick = f |}
            |}

    /// Apply a typed one-chart theme override (Phase 12e).
    static member inline chartTheme(b: ChartThemeBuilder) = "theme" ==> b

    static member inline data(v: _ seq) = "data" ==> Seq.toArray v

    static member inline series v = "series" ==> Seq.toArray v

    static member inline axes(v: obj seq) =
        "axes"
        ==> (v
             |> Seq.map (fun axis ->
                 let pos: string = axis?position

                 let key =
                     if isNull (box pos) then
                         // No explicit position: use direction key so AG Charts v13's
                         // getPrimaryAxisKeys fallback (`direction in options.axes`) resolves them.
                         let axisType: string = axis?``type``

                         match axisType with
                         | "time"
                         | "category" -> "x"
                         | _ -> "y"
                     else
                         // Explicit position: use it as the key so the secondary-axis
                         // loop (`"position" in axisOptions`) can find it.
                         pos

                 key ==> axis)
             |> Seq.toList
             |> createObj)

    static member inline options value =
        ChartPalette.refreshFromTheme ()

        let value =
            value
            |> Seq.append [
                "theme"
                ==> {|
                        palette = {|
                            fills = ChartPalette.fills
                            strokes = ChartPalette.strokes
                        |}
                        ``params`` = {|
                            fontFamily = ChartPalette.fontFamily
                            accentColor = ChartPalette.accentColor
                        |}
                        overrides = {|
                            common = {|
                                axes = {|
                                    time = {|
                                        tick = {| enabled = true |}
                                        gridLine = {| enabled = true |}
                                        crosshair = {| enabled = false |}
                                    |}
                                    number = {|
                                        title = {| fontWeight = "bold" |}
                                        crosshair = {| enabled = false |}
                                    |}
                                    category = {| crosshair = {| enabled = false |} |}
                                |}
                            |}

                            line = {|
                                series = {|
                                    marker = {|
                                        enabled = true
                                        fill = ChartPalette.markerFill
                                        stroke = ChartPalette.markerStroke
                                    |}
                                    strokeWidth = 3
                                    highlight = {| enabled = false |}
                                |}
                            |}
                            scatter = {|
                                series = {| highlight = {| enabled = false |} |}
                            |}
                            bar = {|
                                series = {| highlight = {| enabled = false |} |}
                            |}
                        |}
                    |}
            ]

        prop.custom ("options", createObj value)

    static member inline chart props =
        ensureChartsModulesRegistered ()
        MemoizedChart(createObj !!props)