module ToolUp.Platform.Tests.InProcess.AgChartExportTests

// Phase 578 — the AG chart capture-to-placeholder helper. Pins the half of
// `ToolUp.Platform.AgChartExport` that needs no browser: options defaulting
// and validation, the resolution-scale arithmetic, data-URL decoding, the
// recoverable-error surface, and the placeholder assembly measured against
// the REAL `ToolUp.Reporting.PlaceholderValue` rather than a stand-in.
//
// The interop leg (`AgCharts.getInstance`, `getImageDataURL`, the canvas
// background composite) is deliberately thin and carries no .NET runtime —
// it is exercised by the `samples/MinimalClient` Fable transpile and the
// build type-check, per the `CellProvenanceTests` precedent.
//
// One capture path DOES run here, and it is the one that matters most: a
// null chart handle is refused before any interop is reached, which is the
// phase's "capture of an unmounted chart returns a recoverable error, not
// an exception" acceptance criterion executed rather than asserted.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Reporting

let private capture (chart: AgChartExport.ChartRef) options =
    AgChartExport.captureForExport chart options |> Async.RunSynchronously

let private pngDataUrl = "data:image/png;base64,iVBORw0KGgo="

let tests =
    testList "AgChartExport (Phase 578)" [

        // ── Options ──────────────────────────────────────────────────

        test "defaults are print-quality on white" {
            let d = AgChartExport.CaptureOptions.defaults
            Expect.equal d.ResolutionScale 2.0 "print wants more than one device pixel per CSS pixel"

            Expect.equal
                d.BackgroundFill
                (Some "#ffffff")
                "a dark-theme chart must stay legible on a white page by default"

            Expect.equal d.MimeType "image/png" "PNG is the lossless default for a chart"
            Expect.equal d.BaseSize None "the on-screen size is the default base"
        }

        test "screen options capture exactly what is rendered" {
            let s = AgChartExport.CaptureOptions.screen
            Expect.equal s.ResolutionScale 1.0 "no rescale"
            Expect.equal s.BackgroundFill None "nothing painted under the chart"
            Expect.equal s.MimeType "image/png" "encoding is unchanged from the defaults"
        }

        test "builders compose without disturbing the other fields" {
            let composed =
                AgChartExport.CaptureOptions.defaults
                |> AgChartExport.CaptureOptions.withScale 3.0
                |> AgChartExport.CaptureOptions.withBackground "#101317"
                |> AgChartExport.CaptureOptions.withBaseSize 800 450
                |> AgChartExport.CaptureOptions.withMimeType "image/jpeg"

            Expect.equal composed.ResolutionScale 3.0 "scale"
            Expect.equal composed.BackgroundFill (Some "#101317") "background"
            Expect.equal composed.BaseSize (Some(800, 450)) "base size"
            Expect.equal composed.MimeType "image/jpeg" "mime type"

            Expect.equal
                (AgChartExport.CaptureOptions.transparent composed).BackgroundFill
                None
                "transparent clears the fill and nothing else"

            Expect.equal
                (AgChartExport.CaptureOptions.transparent composed).ResolutionScale
                3.0
                "transparent leaves the scale alone"
        }

        test "validate refuses a scale that cannot be honoured" {
            let scaled v =
                AgChartExport.CaptureOptions.defaults
                |> AgChartExport.CaptureOptions.withScale v
                |> AgChartExport.CaptureOptions.validate

            Expect.isError (scaled 0.0) "a zero scale renders nothing"
            Expect.isError (scaled -1.0) "a negative scale is meaningless"
            Expect.isError (scaled Double.NaN) "NaN"
            Expect.isError (scaled Double.PositiveInfinity) "infinity"
            Expect.isError (scaled 8.5) "past the canvas ceiling"
            Expect.isOk (scaled 8.0) "the ceiling itself is admitted"
            Expect.isOk (scaled 0.5) "a downscale is legitimate — a thumbnail"
        }

        test "validate refuses a non-image media type" {
            let typed v =
                AgChartExport.CaptureOptions.defaults
                |> AgChartExport.CaptureOptions.withMimeType v
                |> AgChartExport.CaptureOptions.validate

            Expect.isError (typed "application/pdf") "a chart capture is an image"
            Expect.isError (typed "") "empty"
            Expect.isOk (typed "image/jpeg") "jpeg"
            Expect.isOk (typed "image/png") "png"
        }

        test "validate returns the options unchanged on success" {
            let options = AgChartExport.CaptureOptions.defaults

            match AgChartExport.CaptureOptions.validate options with
            | Ok validated -> Expect.equal validated options "validation is a check, not a rewrite"
            | Error e -> failtestf "expected Ok, got %s" e
        }

        // ── Sizing ───────────────────────────────────────────────────

        test "scaleDimension rounds to whole pixels and never floors to zero" {
            Expect.equal (AgChartExport.Sizing.scaleDimension 2.0 600) 1200 "clean doubling"
            Expect.equal (AgChartExport.Sizing.scaleDimension 1.5 601) 902 "901.5 rounds away from zero"
            Expect.equal (AgChartExport.Sizing.scaleDimension 0.001 100) 1 "a zero-pixel render would be a blank image"
        }

        test "requested prefers an explicit base size over the measured one" {
            let options =
                AgChartExport.CaptureOptions.defaults
                |> AgChartExport.CaptureOptions.withBaseSize 400 200

            Expect.equal
                (AgChartExport.Sizing.requested options (Some(1000, 900)))
                (Some(800, 400))
                "the explicit base wins, then the scale applies to it"
        }

        test "requested falls back to the measured on-screen size" {
            Expect.equal
                (AgChartExport.Sizing.requested AgChartExport.CaptureOptions.defaults (Some(640, 360)))
                (Some(1280, 720))
                "on-screen size scaled"
        }

        test "requested is None when no base size is known at all" {
            Expect.equal
                (AgChartExport.Sizing.requested AgChartExport.CaptureOptions.defaults None)
                None
                "nothing to scale, and the caller is told rather than silently served native size"
        }

        // ── Data-URL decoding ────────────────────────────────────────

        test "decode recovers the bytes a canvas encoded" {
            match AgChartExport.DataUrl.decode pngDataUrl with
            | Ok(bytes, mimeType) ->
                Expect.equal mimeType "image/png" "declared media type"
                Expect.equal bytes (Convert.FromBase64String "iVBORw0KGgo=") "payload bytes"
                Expect.equal bytes[0] 0x89uy "the PNG signature's first byte survived the round trip"
            | Error e -> failtestf "expected a decode, got %s" e
        }

        test "decode reports the DECLARED media type, not the requested one" {
            // A canvas silently substitutes PNG for a format it cannot
            // encode. Trusting the request would put a lying MIME type on
            // the placeholder, and the document is where that is found.
            match AgChartExport.DataUrl.decode "data:image/png;base64,QUJD" with
            | Ok(_, mimeType) -> Expect.equal mimeType "image/png" "read from the URL the canvas produced"
            | Error e -> failtestf "expected a decode, got %s" e
        }

        test "decode carries charset-bearing headers without confusing the media type" {
            match AgChartExport.DataUrl.decode "data:image/jpeg;charset=utf-8;base64,QUJD" with
            | Ok(_, mimeType) -> Expect.equal mimeType "image/jpeg" "media type is the first header segment"
            | Error e -> failtestf "expected a decode, got %s" e
        }

        test "decode refuses everything that is not a decodable image URL" {
            let refused label input =
                Expect.isError (AgChartExport.DataUrl.decode input) label

            refused "empty" ""
            refused "whitespace" "   "
            refused "not a data URL" "https://example.invalid/chart.png"
            refused "no separator" "data:image/png;base64"
            refused "not base64" "data:text/plain,hello"
            refused "no declared media type" "data:;base64,QUJD"
            refused "undecodable payload" "data:image/png;base64,!!!not-base64!!!"
        }

        test "every decode refusal names something actionable" {
            // The messages reach a user through a toast; "capture failed"
            // costs a support round trip that a sentence does not.
            let reasons =
                [
                    ""
                    "https://example.invalid/x.png"
                    "data:image/png;base64"
                    "data:text/plain,hello"
                ]
                |> List.map (fun input ->
                    match AgChartExport.DataUrl.decode input with
                    | Error reason -> reason
                    | Ok _ -> failtest "expected a refusal")

            for reason in reasons do
                Expect.isGreaterThan reason.Length 30 "a refusal is a sentence, not a token"
                Expect.stringContains reason " " "prose"
        }

        // ── Placeholder assembly (578.B) ─────────────────────────────

        test "a capture is exactly what the reporting image placeholder takes" {
            // The load-bearing claim of 578.B: the constructor is passed in
            // (this tier cannot reference the reporting companion) and it
            // fits with NO adapter — `ImageValue` applied straight to a
            // capture. If the shapes ever diverge, this stops compiling.
            let bytes = [| 1uy; 2uy; 3uy |]

            let key, value =
                AgChartExport.asPlaceholder ImageValue "revenue" (bytes, "image/png")

            Expect.equal key "revenue" "the key is handed through unchanged"
            Expect.equal value (ImageValue(bytes, "image/png")) "and the value is the placeholder the renderer fills"
        }

        test "asPlaceholders builds the values map a render call takes" {
            let values =
                AgChartExport.asPlaceholders ImageValue [
                    "revenue", ([| 1uy |], "image/png")
                    "spend", ([| 2uy |], "image/jpeg")
                ]

            Expect.equal values.Count 2 "one entry per chart"
            Expect.equal (values["revenue"]) (ImageValue([| 1uy |], "image/png")) "revenue"
            Expect.equal (values["spend"]) (ImageValue([| 2uy |], "image/jpeg")) "spend"
        }

        test "a later capture under a repeated key wins, as a map fold does" {
            let values =
                AgChartExport.asPlaceholders ImageValue [
                    "chart", ([| 1uy |], "image/png")
                    "chart", ([| 9uy |], "image/png")
                ]

            Expect.equal values.Count 1 "one key, one entry"
            Expect.equal (values["chart"]) (ImageValue([| 9uy |], "image/png")) "last write wins"
        }

        // ── Recoverable failure, executed ────────────────────────────

        test "capturing an unmounted chart is an Error, not an exception" {
            match capture (AgChartExport.ChartInstance null) AgChartExport.CaptureOptions.defaults with
            | Error reason ->
                Expect.stringContains reason "No AG chart is mounted" "names what was not found"
                Expect.stringContains reason "retry the capture" "and what to do about it"
            | Ok _ -> failtest "a null chart handle must not capture"
        }

        test "bad options are refused before any chart is touched" {
            let options =
                AgChartExport.CaptureOptions.defaults
                |> AgChartExport.CaptureOptions.withScale 0.0

            match capture (AgChartExport.ChartInstance null) options with
            | Error reason -> Expect.stringContains reason "ResolutionScale" "the options failure is the one reported"
            | Ok _ -> failtest "expected a refusal"
        }

        test "captureAll names the chart that stopped the export" {
            let outcome =
                AgChartExport.captureAll ImageValue AgChartExport.CaptureOptions.defaults [
                    "revenue", AgChartExport.ChartInstance null
                ]
                |> Async.RunSynchronously

            match outcome with
            | Error reason ->
                Expect.stringContains reason "revenue" "an Export button's toast has to say WHICH chart"
                Expect.stringContains reason "No AG chart is mounted" "and why"
            | Ok _ -> failtest "expected a refusal"
        }

        test "captureAll over no charts is an empty values map, not a failure" {
            let outcome =
                AgChartExport.captureAll ImageValue AgChartExport.CaptureOptions.defaults []
                |> Async.RunSynchronously

            match outcome with
            | Ok values -> Expect.isEmpty values "a page with no charts still renders its report"
            | Error e -> failtestf "expected Ok, got %s" e
        }

        test "captureEach keeps every result beside its key" {
            let results =
                AgChartExport.captureEach AgChartExport.CaptureOptions.defaults [
                    "a", AgChartExport.ChartInstance null
                    "b", AgChartExport.ChartInstance null
                ]
                |> Async.RunSynchronously

            Expect.equal (results |> List.map fst) [ "a"; "b" ] "order and keys preserved"
            Expect.isTrue (results |> List.forall (snd >> Result.isError)) "each carries its own outcome"
        }
    ]