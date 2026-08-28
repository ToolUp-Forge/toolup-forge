// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 578 — capturing a rendered AG chart for a document export.
///
/// A report's image placeholder is bytes plus a MIME type, and a client
/// that wants "export exactly what I am looking at" has to produce those
/// from a live chart. Every consumer that has done it hand-rolled the
/// same four steps: reach the chart instance, ask it for a data URL,
/// split that URL on its comma and base64-decode the tail, then build the
/// map entry the render call takes. Three of the four are fiddly for
/// reasons that have nothing to do with the consumer's domain, and the
/// fourth is where the mistakes land. This module is that sequence,
/// written once.
///
/// ── What this is NOT ─────────────────────────────────────────────────
///
/// It is not the deterministic chart handoff. `ChartArtifact` in the
/// reporting tier renders a chart SPEC through the deployment's own
/// server-side grammar and content-addresses the bytes, so a governed
/// document regenerated six months later can show the picture did not
/// move. This module photographs a browser: the bytes depend on the
/// viewport, the theme and the fonts the machine had. The two answer
/// different questions, and a consumer that needs the first should not
/// reach for this one. Use this for the chart kinds the deterministic
/// projector does not draw — scatter, sankey, custom series — and for the
/// honest "export what is on my screen" button.
///
/// ── Why the placeholder constructor arrives as a PARAMETER ───────────
///
/// `asPlaceholder` produces the reporting tier's image placeholder
/// without naming its type. Reporting is a companion package and this
/// file is core client tier: a companion sits at the SDK boundary and the
/// SDK never references one (GP 1). The layering argument is not the only
/// one — the reporting tier is not Fable-compilable, so a project
/// reference here would drag server-only code through every consumer's
/// Fable compile.
///
/// So the case constructor arrives as data. `PlaceholderValue.ImageValue`
/// takes exactly `byte[] * string`, which is exactly what a capture IS,
/// so the call site reads `AgChartExport.asPlaceholder ImageValue
/// "revenue" capture` and no adapter is written anywhere.
///
/// ── Two things that are deliberate ───────────────────────────────────
///
///   * **An unmounted chart is a value, not an exception.** AG Charts
///     throws on a destroyed instance and `AgCharts.getInstance` simply
///     answers nothing for an element that never held one. Both reach the
///     caller as `Error` carrying a sentence that names what was looked
///     for, because an Export button's honest failure is a toast, not a
///     stack trace.
///   * **The background is filled by default.** A dark-theme chart
///     captured as-is is light-on-dark, and light-on-dark on a white page
///     is unreadable. The default paints `#ffffff` under the capture;
///     `CaptureOptions.transparent` opts out where the destination is
///     itself dark.
module ToolUp.Platform.AgChartExport

open System
open Fable.Core
open Fable.Core.JsInterop

// ─── The capture's inputs and output ─────────────────────────────────

/// A completed capture: the encoded image bytes paired with the MIME type
/// they are encoded as.
///
/// A tuple rather than a record because it is precisely the pair a
/// reporting image placeholder takes, so `asPlaceholder` hands it straight
/// to the constructor with nothing to unpack and no field names to keep in
/// step with a companion this tier cannot see.
type ChartCapture = byte[] * string

/// How to capture.
type CaptureOptions = {
    /// Multiplier on the base pixel size. The chart is re-rendered at the
    /// scaled size rather than the captured raster being stretched, so
    /// this buys real detail: `2.0` against a 600 px chart yields a
    /// genuine 1200 px render. Print wants 2–3.
    ResolutionScale: float
    /// Colour painted UNDER the captured chart, as any CSS colour string.
    /// `None` leaves whatever the chart's own theme produced, transparent
    /// pixels included.
    BackgroundFill: string option
    /// Encoding of the returned bytes. AG Charts documents `"image/png"`
    /// and `"image/jpeg"`; the background-compositing pass re-encodes
    /// through a canvas, which accepts the same set.
    MimeType: string
    /// Base size in CSS pixels, BEFORE `ResolutionScale`. `None` reads the
    /// chart's current on-screen size, which is what "export what I am
    /// looking at" means; supply it explicitly to capture at a size the
    /// page never showed.
    BaseSize: (int * int) option
}

// ─── Recoverable failures ────────────────────────────────────────────

/// The sentences `captureForExport` returns on the `Error` side.
///
/// Named functions rather than inline strings so the wording is one
/// thing, pinned by test: these reach a user through a toast, and an
/// export failure that says only "capture failed" costs a support round
/// trip that "no AG chart is mounted at #revenue-chart" does not.
[<RequireQualifiedAccess>]
module CaptureFailure =

    let elementNotFound (elementId: string) =
        $"No element with id '%s{elementId}' is in the document, so there is no chart to capture. "
        + "Capture from inside the same render pass that mounted the chart."

    let chartNotMounted (where: string) =
        $"No AG chart is mounted at %s{where}. The chart may not have rendered yet, or may already "
        + "have been unmounted; retry the capture once the chart is on screen."

    let notAChartInstance =
        "The supplied value is not an AG chart instance — it exposes no `getImageDataURL`. Pass the "
        + "value an `ag-charts-react` ref yields, or capture by element id instead."

    let invalidScale (scale: float) =
        $"ResolutionScale must be a positive number; got {scale}."

    let scaleTooLarge (scale: float) =
        $"ResolutionScale {scale} exceeds the ceiling of 8. Above that a chart of ordinary on-screen "
        + "size asks the browser for a canvas it will refuse to allocate, which fails as an opaque "
        + "rendering error rather than as this sentence."

    let invalidMimeType (mimeType: string) =
        $"MimeType must be an image media type such as 'image/png'; got '%s{mimeType}'."

    let sizeUnknown (where: string) =
        $"The on-screen size of the chart at %s{where} could not be read, so ResolutionScale has "
        + "nothing to scale. Set CaptureOptions.BaseSize explicitly, or capture by element id."

    let captureRaised (reason: string) =
        $"The chart refused to produce an image: %s{reason}"

    let emptyDataUrl = "The chart produced an empty image; nothing was captured."

    let notADataUrl =
        "The chart produced something that is not a data URL, so no image bytes could be recovered."

    let malformedDataUrl (why: string) =
        $"The captured data URL could not be read: %s{why}."

    let undecodablePayload = "The captured data URL's base64 payload is not decodable."

// ─── Options: defaults and the pure sizing arithmetic ────────────────

[<RequireQualifiedAccess>]
module CaptureOptions =

    /// The scale ceiling. A browser canvas has a hard per-dimension and
    /// per-area limit; asking past it fails inside the canvas rather than
    /// here, so the refusal is made explicit at the boundary.
    [<Literal>]
    let MaxResolutionScale = 8.0

    /// Print-quality PNG on white — the default an Export button wants.
    let defaults = {
        ResolutionScale = 2.0
        BackgroundFill = Some "#ffffff"
        MimeType = "image/png"
        BaseSize = None
    }

    /// The chart exactly as rendered: no rescale, no background painted
    /// under it. For a preview, or for a destination whose own background
    /// the chart should sit on.
    let screen = {
        defaults with
            ResolutionScale = 1.0
            BackgroundFill = None
    }

    let withScale (scale: float) (options: CaptureOptions) = { options with ResolutionScale = scale }

    let withBackground (fill: string) (options: CaptureOptions) = {
        options with
            BackgroundFill = Some fill
    }

    let transparent (options: CaptureOptions) = { options with BackgroundFill = None }

    let withBaseSize (width: int) (height: int) (options: CaptureOptions) = {
        options with
            BaseSize = Some(width, height)
    }

    let withMimeType (mimeType: string) (options: CaptureOptions) = { options with MimeType = mimeType }

    /// Reject the option values that would fail later and further away.
    /// Total and pure — the whole of the options check lives here so it is
    /// testable without a browser.
    let validate (options: CaptureOptions) : Result<CaptureOptions, string> =
        let scale = options.ResolutionScale

        if Double.IsNaN scale || Double.IsInfinity scale || scale <= 0.0 then
            Error(CaptureFailure.invalidScale scale)
        elif scale > MaxResolutionScale then
            Error(CaptureFailure.scaleTooLarge scale)
        elif
            isNull (box options.MimeType)
            || not (options.MimeType.StartsWith("image/", StringComparison.Ordinal))
        then
            Error(CaptureFailure.invalidMimeType options.MimeType)
        else
            Ok options

[<RequireQualifiedAccess>]
module Sizing =

    /// One scaled dimension, rounded to whole pixels and never below 1 —
    /// a zero-width render is a blank image rather than an error, which is
    /// the worst of both.
    let scaleDimension (scale: float) (pixels: int) =
        max 1 (int (Math.Round(float pixels * scale)))

    /// The pixel size to ask the chart to re-render at: the explicit base
    /// size if one was given, else the measured on-screen size, multiplied
    /// by the resolution scale.
    ///
    /// `None` means no base size is known. That is only reachable when the
    /// caller supplied a bare chart instance whose container could not be
    /// measured; `captureForExport` turns it into a refusal that names the
    /// remedy rather than silently capturing at native size and leaving the
    /// operator wondering why the scale did nothing.
    let requested (options: CaptureOptions) (onScreen: (int * int) option) =
        options.BaseSize
        |> Option.orElse onScreen
        |> Option.map (fun (width, height) ->
            scaleDimension options.ResolutionScale width, scaleDimension options.ResolutionScale height)

// ─── Data-URL decoding — the step this phase takes off consumers ─────

[<RequireQualifiedAccess>]
module DataUrl =

    /// Decode a base64 `data:` URL into its bytes and its declared MIME
    /// type.
    ///
    /// The declared type is returned rather than the requested one because
    /// a canvas silently substitutes PNG for a format it cannot encode; a
    /// placeholder that says `image/webp` over PNG bytes renders as a
    /// broken image in the document, and the document is where that is
    /// discovered.
    let decode (dataUrl: string) : Result<ChartCapture, string> =
        if String.IsNullOrWhiteSpace dataUrl then
            Error CaptureFailure.emptyDataUrl
        elif not (dataUrl.StartsWith("data:", StringComparison.Ordinal)) then
            Error CaptureFailure.notADataUrl
        else
            let comma = dataUrl.IndexOf ','

            if comma < 0 then
                Error(CaptureFailure.malformedDataUrl "it carries no ',' separating the header from the payload")
            else
                let header = dataUrl.Substring(5, comma - 5)
                let payload = dataUrl.Substring(comma + 1)
                let segments = header.Split ';'
                let declaredType = segments[0]
                let mimeType = declaredType.Trim()
                let isBase64 = segments |> Array.exists (fun segment -> segment.Trim() = "base64")

                if not isBase64 then
                    Error(
                        CaptureFailure.malformedDataUrl
                            "only base64 data URLs carry image bytes, and a canvas emits no other kind"
                    )
                elif mimeType = "" then
                    Error(CaptureFailure.malformedDataUrl "it declares no MIME type")
                else
                    try
                        Ok(Convert.FromBase64String payload, mimeType)
                    with _ ->
                        Error CaptureFailure.undecodablePayload

// ─── 578.B — placeholder assembly ────────────────────────────────────

/// One capture as the map entry a report render takes.
///
/// `imageValue` is the reporting tier's image-placeholder constructor —
/// `PlaceholderValue.ImageValue` — supplied by the caller because this
/// tier does not reference that companion (see the module header). Its
/// shape IS a capture, so this is a naming step, which is exactly what a
/// consumer should not have to remember to get right.
let asPlaceholder (imageValue: ChartCapture -> 'value) (key: string) (capture: ChartCapture) = key, imageValue capture

/// Several captures as the values map a report render takes.
let asPlaceholders (imageValue: ChartCapture -> 'value) (captures: (string * ChartCapture) seq) =
    captures
    |> Seq.map (fun (key, capture) -> asPlaceholder imageValue key capture)
    |> Map.ofSeq

// ─── The chart handle ────────────────────────────────────────────────

/// A handle to the chart to capture.
///
/// Three shapes because a page has whichever one it has. The element and
/// element-id shapes go through `AgCharts.getInstance`, which is keyed on
/// the chart's own container — so a wrapper element resolves too, by
/// searching its descendants for the container the chart registered.
type ChartRef =
    /// The instance an `ag-charts-react` `ref` yields.
    | ChartInstance of instance: obj
    /// The element the chart is mounted in, or any ancestor of it.
    | ChartElement of element: Browser.Types.Element
    /// The id of that element — the shape a page already has when it
    /// wrote `Html.div [ prop.id "revenue-chart" ] [ AgChart.chart … ]`.
    | ChartElementId of elementId: string

let private describe (chart: ChartRef) =
    match chart with
    | ChartInstance _ -> "the supplied chart instance"
    | ChartElement _ -> "the supplied element"
    | ChartElementId elementId -> $"#%s{elementId}"

// ─── Interop (thin by design) ────────────────────────────────────────
//
// Nothing here runs at module load: the `import` sits inside a function
// body, so this file's initialiser is pure and the .NET test pack can
// exercise everything above without a JS host.

/// `AgCharts.getInstance` is keyed on the element the chart registered as
/// its container, and answers `undefined` for anything else.
///
/// The import and the member access are one function ON PURPOSE. A
/// function whose body is ONLY an `import` is aliased by Fable to the
/// imported value itself — `export const agChartsApi = AgCharts` — and the
/// call site then reads `agChartsApi()`, invoking an abstract class. That
/// compiles on both hosts and fails only in a browser, which is the worst
/// available shape for a defect. Keeping the access in the same body
/// leaves Fable emitting `AgCharts.getInstance(element)`.
let private chartInstanceFor (element: obj) : obj =
    let agCharts: obj = import "AgCharts" "ag-charts-community"
    emitJsExpr (agCharts, element) "$0.getInstance($1)"

[<Emit("!!$0 && typeof $0.getImageDataURL === 'function'")>]
let private canExport (candidate: obj) : bool = jsNative

[<Emit("[$0, ...$0.querySelectorAll('*')]")>]
let private selfAndDescendants (element: obj) : obj[] = jsNative

[<Emit("[($0 && $0.clientWidth) || 0, ($0 && $0.clientHeight) || 0]")>]
let private elementPixelSize (element: obj) : int[] = jsNative

// Best effort and never throwing: `ag-charts-react` passes its own
// container div in the options it creates the chart with, so an instance
// handed over by a ref can still be measured. A chart created some other
// way may only carry an explicit width / height, and may carry neither.
[<Emit("""(function (i) {
    try {
        var o = i.getOptions && i.getOptions();
        var c = o && o.container;
        if (c && c.clientWidth && c.clientHeight) { return [c.clientWidth, c.clientHeight]; }
        if (o && o.width && o.height) { return [o.width, o.height]; }
    } catch (e) { }
    return [0, 0];
})($0)""")>]
let private instancePixelSize (instance: obj) : int[] = jsNative

[<Emit("$0.getImageDataURL($1)")>]
let private getImageDataUrl (instance: obj) (options: obj) : JS.Promise<string> = jsNative

// Paint the fill, then draw the capture over it at 1:1 — the scaling was
// already done by re-rendering the chart, so this pass adds a backdrop
// without touching the resolution.
[<Emit("""new Promise(function (resolve, reject) {
    var img = new Image();
    img.onload = function () {
        try {
            var canvas = document.createElement('canvas');
            canvas.width = img.naturalWidth || img.width;
            canvas.height = img.naturalHeight || img.height;
            var ctx = canvas.getContext('2d');
            ctx.fillStyle = $1;
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            ctx.drawImage(img, 0, 0);
            resolve(canvas.toDataURL($2));
        } catch (e) { reject(e); }
    };
    img.onerror = function () { reject(new Error('the captured image could not be decoded')); };
    img.src = $0;
})""")>]
let private compositeOnBackground (dataUrl: string) (fill: string) (mimeType: string) : JS.Promise<string> = jsNative

let private measured (pixels: int[]) =
    if pixels.Length = 2 && pixels[0] > 0 && pixels[1] > 0 then
        Some(pixels[0], pixels[1])
    else
        None

/// Resolve a handle to the chart instance plus whatever on-screen size
/// could be measured for it.
let private resolve (chart: ChartRef) : Result<obj * (int * int) option, string> =
    let inElement (element: obj) =
        selfAndDescendants element
        |> Array.tryPick (fun candidate ->
            let found = chartInstanceFor candidate

            if isNull (box found) || not (canExport found) then
                None
            else
                Some(found, measured (elementPixelSize candidate)))

    let fromElement (element: obj) =
        match inElement element with
        | Some found -> Ok found
        | None -> Error(CaptureFailure.chartNotMounted (describe chart))

    match chart with
    | ChartInstance instance ->
        if isNull (box instance) then
            Error(CaptureFailure.chartNotMounted (describe chart))
        elif not (canExport instance) then
            Error CaptureFailure.notAChartInstance
        else
            Ok(instance, measured (instancePixelSize instance))
    | ChartElement element -> fromElement (box element)
    | ChartElementId elementId ->
        let element = Browser.Dom.document.getElementById elementId

        if isNull (box element) then
            Error(CaptureFailure.elementNotFound elementId)
        else
            fromElement (box element)

// ─── 578.A — capture ─────────────────────────────────────────────────

/// Capture a mounted AG chart as export-ready image bytes.
///
/// Never raises: a chart that is not mounted, an options value that
/// cannot be honoured, and a renderer that refuses all arrive as `Error`
/// carrying a sentence fit to show a user.
let captureForExport (chart: ChartRef) (options: CaptureOptions) : Async<Result<ChartCapture, string>> = async {
    match CaptureOptions.validate options with
    | Error reason -> return Error reason
    | Ok options ->
        match resolve chart with
        | Error reason -> return Error reason
        | Ok(instance, onScreen) ->
            let size = Sizing.requested options onScreen

            if size.IsNone && options.ResolutionScale <> 1.0 then
                return Error(CaptureFailure.sizeUnknown (describe chart))
            else
                let sizeProps =
                    match size with
                    | Some(width, height) -> [ "width" ==> width; "height" ==> height ]
                    | None -> []

                // Ask the chart for lossless PNG whenever a composite pass
                // follows: that pass re-encodes to the requested type
                // anyway, and encoding a JPEG twice puts ringing on chart
                // text and axis labels for nothing. With no fill there is
                // no second encode, so the chart is asked for the target
                // type directly.
                let chartFormat =
                    match options.BackgroundFill with
                    | Some _ -> "image/png"
                    | None -> options.MimeType

                let requestedOptions = ("fileFormat" ==> chartFormat) :: sizeProps |> createObj

                try
                    let! captured = getImageDataUrl instance requestedOptions |> Async.AwaitPromise

                    let! finished =
                        match options.BackgroundFill with
                        | None -> async { return captured }
                        | Some fill -> compositeOnBackground captured fill options.MimeType |> Async.AwaitPromise

                    return DataUrl.decode finished
                with ex ->
                    return Error(CaptureFailure.captureRaised ex.Message)
}

/// Capture several charts, keeping each result beside its key.
///
/// The partial view: a caller whose page can render a report without one
/// of its figures decides that for itself.
let captureEach
    (options: CaptureOptions)
    (charts: (string * ChartRef) seq)
    : Async<(string * Result<ChartCapture, string>) list> =
    async {
        let results = ResizeArray()

        for key, chart in charts do
            let! captured = captureForExport chart options
            results.Add(key, captured)

        return List.ofSeq results
    }

/// Capture several charts straight into the values map a report render
/// takes — 578.B's one call for a multi-chart page.
///
/// Fails on the first chart that cannot be captured, naming its key.
/// All-or-nothing rather than partial because this is the Export-button
/// path: a report silently missing one of its figures is worse for its
/// reader than an export that did not happen and said which chart stopped
/// it. `captureEach` is the partial view for a caller that wants it.
let captureAll
    (imageValue: ChartCapture -> 'value)
    (options: CaptureOptions)
    (charts: (string * ChartRef) seq)
    : Async<Result<Map<string, 'value>, string>> =
    async {
        let! captured = captureEach options charts

        let failure =
            captured
            |> List.tryPick (fun (key, result) ->
                match result with
                | Error reason -> Some(key, reason)
                | Ok _ -> None)

        match failure with
        | Some(key, reason) -> return Error $"Chart '%s{key}': %s{reason}"
        | None ->
            return
                captured
                |> List.choose (fun (key, result) ->
                    match result with
                    | Ok capture -> Some(key, capture)
                    | Error _ -> None)
                |> asPlaceholders imageValue
                |> Ok
    }