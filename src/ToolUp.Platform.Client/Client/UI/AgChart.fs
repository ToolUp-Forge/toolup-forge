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

[<RequireQualifiedAccess>]
type AxisKind =
    | Category
    | Number
    | Time

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