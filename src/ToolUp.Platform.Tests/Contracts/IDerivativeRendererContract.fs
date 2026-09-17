// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the `IDerivativeRenderer` contract pack: the laws the
/// interface's own doc comment states (aspect ratio preserved inside the
/// bounding box, never upscaled, MIME type matching the spec, a corrupt
/// source a typed `DecodeFailed` rather than an exception, a header-only
/// probe), pinned so that an implementation on either side of a process
/// boundary answers alike.
///
/// Bound by `SkiaSharpDerivativeRenderer` (in-process, the Platform
/// pack) and by `IsolatedSkiaDerivativeRenderer` (through the isolation
/// worker, `ToolUp.Companions.Isolation.Tests`). The fixture is a PNG
/// the pack ENCODES ITSELF with the BCL — a deflate stream and a CRC —
/// so the pack carries no image library and a third implementation can
/// bind it with nothing but the interface.
module ToolUp.Platform.Tests.Contracts.IDerivativeRendererContract

open System
open System.IO
open System.IO.Compression
open Expecto
open ToolUp.AssetStore

// ─── A BCL-only PNG encoder, enough for one opaque RGBA fixture ──────

let private crcTable =
    Array.init 256 (fun n ->
        let mutable c = uint32 n

        for _ in 0..7 do
            c <- if c &&& 1u = 1u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1

        c)

let private crc32 (bytes: byte[]) =
    let mutable c = 0xFFFFFFFFu

    for b in bytes do
        c <- crcTable[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)

    c ^^^ 0xFFFFFFFFu

let private bigEndian (value: uint32) = [| byte (value >>> 24); byte (value >>> 16); byte (value >>> 8); byte value |]

let private chunk (kind: string) (payload: byte[]) =
    let kindBytes = Text.Encoding.ASCII.GetBytes kind

    Array.concat [
        bigEndian (uint32 payload.Length)
        kindBytes
        payload
        bigEndian (crc32 (Array.append kindBytes payload))
    ]

/// An opaque RGBA PNG of `width` × `height`, every pixel `(r, g, b)`.
let encodePng (width: int) (height: int) (r: byte, g: byte, b: byte) : byte[] =
    let raw = Array.zeroCreate<byte> (height * (1 + width * 4))

    for y in 0 .. height - 1 do
        let row = y * (1 + width * 4)
        raw[row] <- 0uy // filter: None

        for x in 0 .. width - 1 do
            let at = row + 1 + x * 4
            raw[at] <- r
            raw[at + 1] <- g
            raw[at + 2] <- b
            raw[at + 3] <- 255uy

    let deflated =
        use output = new MemoryStream()

        (use zlib = new ZLibStream(output, CompressionLevel.Optimal, true)
         zlib.Write(raw, 0, raw.Length))

        output.ToArray()

    let header =
        Array.concat [
            bigEndian (uint32 width)
            bigEndian (uint32 height)
            [| 8uy; 6uy; 0uy; 0uy; 0uy |] // 8-bit, RGBA, deflate, no filter, no interlace
        ]

    Array.concat [
        [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
        chunk "IHDR" header
        chunk "IDAT" deflated
        chunk "IEND" [||]
    ]

// ─── The pack ────────────────────────────────────────────────────────

/// The fixture: 48 × 32, so a 24-wide box halves it exactly.
let fixture = lazy (encodePng 48 32 (200uy, 40uy, 40uy))

let private garbage = Array.init 512 (fun i -> byte ((i * 131 + 7) % 251))

let private spec name (maxWidth: int option) (maxHeight: int option) (format: ImageFormat) : DerivativeSpec = {
    Name = name
    MaxWidth = maxWidth
    MaxHeight = maxHeight
    Format = format
    Quality = 80
}

let private render (renderer: IDerivativeRenderer) (bytes: byte[]) (spec: DerivativeSpec) =
    renderer.Render(bytes, spec) |> Async.RunSynchronously

let private probe (renderer: IDerivativeRenderer) (bytes: byte[]) =
    renderer.Probe bytes |> Async.RunSynchronously

/// Bind the pack: `name` labels the binding, `factory` builds the
/// implementation under test.
let tests (name: string) (factory: unit -> IDerivativeRenderer) =
    testList $"{name} — IDerivativeRenderer contract" [
        testCase "Probe reads the fixture's dimensions from its header"
        <| fun () -> Expect.equal (probe (factory ()) fixture.Value) (Some(48, 32)) "48 × 32"

        testCase "Probe of bytes that are not an image is None, never an exception"
        <| fun () -> Expect.equal (probe (factory ()) garbage) None "no dimensions"

        testCase "Render of bytes that are not an image is DecodeFailed, never an exception"
        <| fun () ->
            match render (factory ()) garbage (spec "thumb" (Some 24) None Png) with
            | Error(DecodeFailed _) -> ()
            | other -> failtestf "expected DecodeFailed; got %A" other

        testCase "Render fits the bounding box and preserves the aspect ratio"
        <| fun () ->
            let renderer = factory ()

            match render renderer fixture.Value (spec "thumb" (Some 24) None Png) with
            | Ok(bytes, mime) ->
                Expect.equal mime "image/png" "the MIME type matches the spec's format"
                Expect.equal (probe renderer bytes) (Some(24, 16)) "halved on both axes"
            | Error err -> failtestf "expected a derivative; got %A" err

        testCase "Render honours the tighter of two bounds"
        <| fun () ->
            let renderer = factory ()

            match render renderer fixture.Value (spec "square" (Some 100) (Some 8) Png) with
            | Ok(bytes, _) -> Expect.equal (probe renderer bytes) (Some(12, 8)) "height-bound"
            | Error err -> failtestf "expected a derivative; got %A" err

        testCase "Render never upscales — a box larger than the source passes it through at native size"
        <| fun () ->
            let renderer = factory ()

            match render renderer fixture.Value (spec "large" (Some 960) (Some 640) Png) with
            | Ok(bytes, _) -> Expect.equal (probe renderer bytes) (Some(48, 32)) "native resolution"
            | Error err -> failtestf "expected a derivative; got %A" err

        testCase "Render encodes to the spec's format and reports its MIME type"
        <| fun () ->
            let renderer = factory ()

            match render renderer fixture.Value (spec "jpeg" (Some 24) None Jpeg) with
            | Ok(bytes, mime) ->
                Expect.equal mime "image/jpeg" "image/jpeg"
                Expect.equal (probe renderer bytes) (Some(24, 16)) "a JPEG the renderer can probe back"
            | Error err -> failtestf "expected a derivative; got %A" err

        testCase "Render is deterministic — the same source and spec produce the same bytes"
        <| fun () ->
            let renderer = factory ()
            let spec = spec "thumb" (Some 24) None Png
            let first = render renderer fixture.Value spec
            let second = render renderer fixture.Value spec
            Expect.equal second first "same bytes, same MIME type"
    ]