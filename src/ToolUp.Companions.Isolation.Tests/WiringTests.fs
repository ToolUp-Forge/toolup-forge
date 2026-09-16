// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the two forge paths wired onto the seam: the asset
/// store's Skia decode of uploaded images (run for real, both sides of
/// the boundary, and compared) and the Tesseract OCR of uploaded
/// documents (its codec and its compose-time refusal — the native
/// engine needs operator-supplied language data the gate does not
/// carry, so the extraction itself is exercised by the seam's own
/// process cases and by a deployment that composes it).
module ToolUp.Companions.Isolation.Tests.WiringTests

open System
open System.IO
open Expecto
open SkiaSharp
open ToolUp.AssetStore
open ToolUp.Companions.Isolation
open ToolUp.Platform.IOcrProvider
open ToolUp.RAG.OcrProviders.Tesseract
open ToolUp.RAG.OcrProviders.Tesseract.TesseractOcrIsolation

/// A real 48×32 PNG, rendered here so the case carries no fixture.
let private samplePng =
    lazy
        (use bitmap = new SKBitmap(48, 32)
         use canvas = new SKCanvas(bitmap)
         canvas.Clear SKColors.CornflowerBlue
         use paint = new SKPaint(Color = SKColors.White)
         canvas.DrawCircle(24.0f, 16.0f, 10.0f, paint)
         use image = SKImage.FromBitmap bitmap
         use data = image.Encode(SKEncodedImageFormat.Png, 100)
         data.ToArray())

let private thumbnail: DerivativeSpec = {
    Name = "thumbnail"
    MaxWidth = Some 24
    MaxHeight = None
    Format = Png
    Quality = 90
}

let private isolation =
    ProcessIsolation.create {
        Timeout = TimeSpan.FromSeconds 60.0
        MemoryCap = None
    }

let private skiaTests =
    testList "IsolatedSkiaDerivativeRenderer" [
        testCase "the spec, the rendered answer, the error and the probe each round-trip the codec"
        <| fun () ->
            Expect.equal (SkiaIsolationCodec.decodeSpec (SkiaIsolationCodec.encodeSpec thumbnail)) (Ok thumbnail) "spec"

            Expect.equal
                (SkiaIsolationCodec.decodeSpec (
                    SkiaIsolationCodec.encodeSpec {
                        thumbnail with
                            MaxWidth = None
                            MaxHeight = Some 7
                            Format = Webp
                    }
                ))
                (Ok {
                    thumbnail with
                        MaxWidth = None
                        MaxHeight = Some 7
                        Format = Webp
                })
                "spec with the other option shape"

            Expect.equal
                (SkiaIsolationCodec.decodeRendered (
                    SkiaIsolationCodec.encodeRendered ([| 1uy; 2uy; 0uy |], "image/png")
                ))
                (Ok([| 1uy; 2uy; 0uy |], "image/png"))
                "rendered"

            for error in
                [
                    DecodeFailed "bad header"
                    DerivativeRenderError.RenderFailed "oom"
                    EncodeFailed("avif", "no encoder")
                ] do
                Expect.equal (SkiaIsolationCodec.decodeError (SkiaIsolationCodec.encodeError error)) error "error"

            Expect.equal
                (SkiaIsolationCodec.decodeProbe (SkiaIsolationCodec.encodeProbe (Some(640, 480))))
                (Some(640, 480))
                "probe"

            Expect.equal (SkiaIsolationCodec.decodeProbe (SkiaIsolationCodec.encodeProbe None)) None "probe none"

        testCase "Probe answers the same dimensions from the worker as in the host"
        <| fun () ->
            let inProcess = SkiaSharpDerivativeRenderer() :> IDerivativeRenderer
            let isolated = IsolatedSkiaDerivativeRenderer.create isolation
            let expected = inProcess.Probe samplePng.Value |> Async.RunSynchronously
            Expect.equal expected (Some(48, 32)) "the sample decodes in the host"
            Expect.equal (isolated.Probe samplePng.Value |> Async.RunSynchronously) expected "the worker agrees"

        testCase "Render answers the same derivative bytes and MIME type from the worker as in the host"
        <| fun () ->
            let inProcess = SkiaSharpDerivativeRenderer() :> IDerivativeRenderer
            let isolated = IsolatedSkiaDerivativeRenderer.create isolation

            let expected =
                inProcess.Render(samplePng.Value, thumbnail) |> Async.RunSynchronously

            let actual = isolated.Render(samplePng.Value, thumbnail) |> Async.RunSynchronously

            match expected, actual with
            | Ok(expectedBytes, expectedMime), Ok(actualBytes, actualMime) ->
                Expect.equal actualMime expectedMime "mime"
                Expect.equal actualBytes expectedBytes "bytes"
                Expect.isGreaterThan actualBytes.Length 0 "a real derivative"
            | _ -> failtestf "expected both renders to succeed; host %A, worker %A" expected actual

        testCase "bytes that are not an image are DecodeFailed on both sides, and None from Probe"
        <| fun () ->
            let garbage = Text.Encoding.ASCII.GetBytes "<svg onload='alert(1)'/>"
            let inProcess = SkiaSharpDerivativeRenderer() :> IDerivativeRenderer
            let isolated = IsolatedSkiaDerivativeRenderer.create isolation

            let classify =
                function
                | Error(DecodeFailed _) -> "decode-failed"
                | other -> sprintf "%A" other

            Expect.equal
                (classify (isolated.Render(garbage, thumbnail) |> Async.RunSynchronously))
                (classify (inProcess.Render(garbage, thumbnail) |> Async.RunSynchronously))
                "same class of refusal"

            Expect.equal
                (classify (isolated.Render(garbage, thumbnail) |> Async.RunSynchronously))
                "decode-failed"
                "and it is DecodeFailed"

            Expect.equal (isolated.Probe garbage |> Async.RunSynchronously) None "probe"

        testCase "a worker that cannot start is RenderFailed naming the refusal, never an exception"
        <| fun () ->
            let unstartable =
                ProcessIsolation.createWith
                    {
                        Timeout = TimeSpan.FromSeconds 5.0
                        MemoryCap = None
                    }
                    {
                        FileName = Path.Combine(Path.GetTempPath(), "no-such-worker-" + Guid.NewGuid().ToString "N")
                        Arguments = []
                    }

            let isolated = IsolatedSkiaDerivativeRenderer.create unstartable

            match isolated.Render(samplePng.Value, thumbnail) |> Async.RunSynchronously with
            | Error(DerivativeRenderError.RenderFailed message) ->
                Expect.stringContains message "no isolation worker" "names the refusal"
            | other -> failtestf "expected RenderFailed; got %A" other

            Expect.equal (isolated.Probe samplePng.Value |> Async.RunSynchronously) None "probe answers None"
    ]

let private tesseractTests =
    testList "TesseractOcrIsolation" [
        testCase "the options and MIME type round-trip the args codec, with the worker pinned to one engine"
        <| fun () ->
            let options = {
                TesseractOcrProvider.TesseractOcrOptions.forTessData "/opt/tessdata" with
                    Language = "eng+deu"
                    MaxPages = 7
                    DocumentTimeout = TimeSpan.FromSeconds 90.0
                    MaxConcurrency = 4
                    MaxDocumentBytes = 12345L
                    ScannedTextThreshold = 9
            }

            match TesseractIsolationCodec.decodeArgs (TesseractIsolationCodec.encodeArgs options "image/tiff") with
            | Ok(decoded, mimeType) ->
                Expect.equal mimeType "image/tiff" "mime"
                Expect.equal decoded { options with MaxConcurrency = 1 } "every option but the pool size"
            | Error reason -> failtestf "did not decode: %s" reason

            Expect.isTrue
                (TesseractIsolationCodec.decodeArgs [ "only"; "two" ] |> Result.isError)
                "a short args list is refused"

        testCase "pages round-trip the answer codec, including empty text and non-ASCII"
        <| fun () ->
            let pages: OcrPage list = [
                { PageNumber = 1; Text = "" }
                { PageNumber = 2; Text = "Straße — 東京" }
                { PageNumber = 40; Text = "plain" }
            ]

            Expect.equal
                (TesseractIsolationCodec.decodePages (TesseractIsolationCodec.encodePages pages))
                (Ok pages)
                "pages"

            Expect.equal (TesseractIsolationCodec.decodePages [||]) (Ok []) "no pages"

        testCase "a truncated answer is an Error, never a partial page list"
        <| fun () ->
            let whole =
                TesseractIsolationCodec.encodePages [
                    {
                        PageNumber = 1
                        Text = "twelve chars"
                    }
                ]

            Expect.isTrue
                (TesseractIsolationCodec.decodePages whole[.. whole.Length - 3] |> Result.isError)
                "truncated text"

            Expect.isTrue (TesseractIsolationCodec.decodePages whole[..5] |> Result.isError) "truncated header"

        testCase "createIsolated fails at composition on a missing tessdata directory, like create"
        <| fun () ->
            let options =
                TesseractOcrProvider.TesseractOcrOptions.forTessData (
                    Path.Combine(Path.GetTempPath(), "no-tessdata-" + Guid.NewGuid().ToString "N")
                )

            Expect.throwsT<TesseractOcrProvider.TesseractOcrException>
                (fun () -> TesseractOcrIsolation.createIsolated options isolation |> ignore)
                "fails loud at compose time"
    ]

let tests =
    testList "Phase 687 — untrusted paths wired onto the seam" [ skiaTests; tesseractTests ]