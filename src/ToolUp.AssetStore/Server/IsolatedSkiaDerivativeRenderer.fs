// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

open System
open System.Buffers.Binary
open System.Text
open ToolUp.Companions.Isolation

// ─── Phase 687 — the asset store's untrusted-input path, isolated ─────
//
// `POST /api/assets/upload` hands the raw multipart body to
// `SkiaSharpDerivativeRenderer.Probe` synchronously on the request path
// (`DefaultAssetStore.Upload`) and to `Render` for every derivative —
// `SKCodec.Create` and `SKBitmap.Decode` over bytes a user chose, in the
// host process. That is the class of path the isolation seam exists
// for. This renderer is the same `IDerivativeRenderer` contract with the
// decode moved into the worker: the host sends the bytes and the spec
// across the pipe, the worker constructs a `SkiaSharpDerivativeRenderer`
// of its own and answers with the encoded derivative, and a Skia fault
// on a hostile PNG is `RenderFailed` in the host rather than the host
// going away.
//
// Composed through the seam the store already has —
// `AssetCompose.withRenderer (IsolatedSkiaDerivativeRenderer.create
// isolation)` — so a deployment that composes nothing keeps the
// in-process renderer byte-for-byte (GP 11 / GP 13).

/// Wire shape between the host and the worker for one render. Args
/// carry the `DerivativeSpec`; the input is the original bytes; the
/// answer is `[u32 mime length][mime][derivative bytes]`. A renderer
/// error crosses as the entry point's `Error`, tagged so the host can
/// rebuild the `DerivativeRenderError` case rather than flatten it.
[<RequireQualifiedAccess>]
module SkiaIsolationCodec =
    let private formatName =
        function
        | Jpeg -> "jpeg"
        | Png -> "png"
        | Webp -> "webp"
        | Avif -> "avif"

    let private parseFormat =
        function
        | "jpeg" -> Ok Jpeg
        | "png" -> Ok Png
        | "webp" -> Ok Webp
        | "avif" -> Ok Avif
        | other -> Error $"unknown image format '{other}'"

    let private optionalInt =
        function
        | Some(value: int) -> string value
        | None -> ""

    let private parseOptionalInt (field: string) (value: string) =
        if value = "" then
            Ok None
        else
            match Int32.TryParse value with
            | true, parsed -> Ok(Some parsed)
            | _ -> Error $"{field} is not an integer: '{value}'"

    /// The spec as the args list the request carries.
    let encodeSpec (spec: DerivativeSpec) : string list = [
        spec.Name
        optionalInt spec.MaxWidth
        optionalInt spec.MaxHeight
        formatName spec.Format
        string spec.Quality
    ]

    /// The args list back into a spec.
    let decodeSpec (args: string list) : Result<DerivativeSpec, string> =
        match args with
        | [ name; maxWidth; maxHeight; format; quality ] ->
            match parseOptionalInt "MaxWidth" maxWidth, parseOptionalInt "MaxHeight" maxHeight, parseFormat format with
            | Ok maxWidth, Ok maxHeight, Ok format ->
                match Int32.TryParse quality with
                | true, quality ->
                    Ok {
                        Name = name
                        MaxWidth = maxWidth
                        MaxHeight = maxHeight
                        Format = format
                        Quality = quality
                    }
                | _ -> Error $"Quality is not an integer: '{quality}'"
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
        | other -> Error $"expected 5 spec args, got {other.Length}"

    /// A rendered derivative as the answer bytes.
    let encodeRendered (bytes: byte[], mimeType: string) : byte[] =
        let mime = Encoding.UTF8.GetBytes mimeType
        let header = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(), uint32 mime.Length)
        Array.concat [ header; mime; bytes ]

    /// The answer bytes back into a rendered derivative.
    let decodeRendered (answer: byte[]) : Result<byte[] * string, string> =
        if answer.Length < 4 then
            Error "answer shorter than its header"
        else
            let mimeLength =
                int (BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(answer, 0, 4)))

            if mimeLength < 0 || 4 + mimeLength > answer.Length then
                Error "answer's mime length exceeds the answer"
            else
                let mimeType = Encoding.UTF8.GetString(answer, 4, mimeLength)
                Ok(answer[4 + mimeLength ..], mimeType)

    /// A renderer error as the entry point's `Error` message, tagged by
    /// case so it rebuilds on the host.
    let encodeError =
        function
        | DecodeFailed message -> "decode\t" + message
        | RenderFailed message -> "render\t" + message
        | EncodeFailed(format, message) -> "encode\t" + format + "\t" + message

    /// The tagged message back into the renderer error. An untagged
    /// message (the worker raised) is `RenderFailed`.
    let decodeError (message: string) : DerivativeRenderError =
        match message.Split('\t', 3) with
        | [| "decode"; text |] -> DecodeFailed text
        | [| "render"; text |] -> RenderFailed text
        | [| "encode"; format; text |] -> EncodeFailed(format, text)
        | _ -> RenderFailed message

    /// A probe answer: empty for "unrecognised", else `width height`.
    let encodeProbe =
        function
        | None -> [||]
        | Some(width: int, height: int) -> Encoding.UTF8.GetBytes $"{width} {height}"

    /// The probe answer back.
    let decodeProbe (answer: byte[]) : (int * int) option =
        if answer.Length = 0 then
            None
        else
            match Encoding.UTF8.GetString(answer).Split ' ' with
            | [| w; h |] ->
                match Int32.TryParse w, Int32.TryParse h with
                | (true, width), (true, height) -> Some(width, height)
                | _ -> None
            | _ -> None

/// The worker-side render: rebuilds a `SkiaSharpDerivativeRenderer`
/// and runs `Render` over the request. Public with a parameterless
/// constructor because that is how the worker instantiates it.
type SkiaDerivativeRenderEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            match SkiaIsolationCodec.decodeSpec request.Args with
            | Error reason -> Error(SkiaIsolationCodec.encodeError (RenderFailed $"malformed spec: {reason}"))
            | Ok spec ->
                let renderer = SkiaSharpDerivativeRenderer() :> IDerivativeRenderer

                match renderer.Render(request.Input, spec) |> Async.RunSynchronously with
                | Ok rendered -> Ok(SkiaIsolationCodec.encodeRendered rendered)
                | Error error -> Error(SkiaIsolationCodec.encodeError error)

/// The worker-side probe: `SKCodec.Create` over the request bytes.
type SkiaDerivativeProbeEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            let renderer = SkiaSharpDerivativeRenderer() :> IDerivativeRenderer

            renderer.Probe request.Input
            |> Async.RunSynchronously
            |> SkiaIsolationCodec.encodeProbe
            |> Ok

/// `IDerivativeRenderer` over the isolation seam.
[<RequireQualifiedAccess>]
module IsolatedSkiaDerivativeRenderer =
    /// Build the renderer. Constructs a `SkiaSharpDerivativeRenderer`
    /// once in the host and discards it, so a host with no SkiaSharp
    /// native for its RID fails here, at composition, with that
    /// renderer's own message — never at the first upload. Nothing that
    /// probe decodes is user-supplied.
    ///
    /// `Render` maps the seam's refusals onto the renderer's own error
    /// vocabulary: an entry-point error rebuilds the case the worker's
    /// renderer returned; a crash, a timeout, a memory cap or an
    /// unavailable worker is `RenderFailed` naming the refusal. `Probe`
    /// answers `None` for every refusal, which is what the contract says
    /// an unreadable header answers — the render that follows carries
    /// the typed reason.
    let create (isolation: ICompanionIsolation) : IDerivativeRenderer =
        SkiaSharpDerivativeRenderer() |> ignore

        { new IDerivativeRenderer with
            member _.Render(originalBytes, spec) = async {
                let! outcome =
                    CompanionIsolation.run<SkiaDerivativeRenderEntry> isolation {
                        Args = SkiaIsolationCodec.encodeSpec spec
                        Input = originalBytes
                    }

                match outcome with
                | Ok answer -> return SkiaIsolationCodec.decodeRendered answer |> Result.mapError RenderFailed
                | Error(IsolationRefusal.EntryFailed message) -> return Error(SkiaIsolationCodec.decodeError message)
                | Error refusal -> return Error(RenderFailed(IsolationRefusal.describe refusal))
            }

            member _.Probe(originalBytes) = async {
                let! outcome =
                    CompanionIsolation.run<SkiaDerivativeProbeEntry> isolation { Args = []; Input = originalBytes }

                match outcome with
                | Ok answer -> return SkiaIsolationCodec.decodeProbe answer
                | Error _ -> return None
            }
        }