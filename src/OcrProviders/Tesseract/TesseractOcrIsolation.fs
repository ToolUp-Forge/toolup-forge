// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.OcrProviders.Tesseract.TesseractOcrIsolation

open System
open System.Buffers.Binary
open System.IO
open System.Text
open ToolUp.Companions.Isolation
open ToolUp.Platform.IOcrProvider
open ToolUp.RAG.OcrProviders.Tesseract.TesseractOcrProvider

// ─── Phase 687 — the OCR upload path, isolated ────────────────────────
//
// Every byte the knowledge base OCRs is a byte a user uploaded, and the
// first thing `TesseractOcrProvider.ExtractText` does with it is
// `Pix.LoadFromMemory` — Leptonica's native PNG / JPEG / TIFF / GIF /
// WebP / BMP decoders, in the host process, followed by Tesseract's
// native recognition. That is the class of path the isolation seam
// exists for. `createIsolated` is the same `IOcrProvider` with
// `ExtractText` moved into the worker: the host sends the options and
// the bytes across the pipe, the worker builds a provider of its own
// (`create` — the same validation, the same fail-loud probe) and
// answers with the pages, and a Leptonica fault on a hostile TIFF is a
// `TesseractOcrException` naming the crash rather than the host going
// away.
//
// `IsScanned` stays in the host: its PDF inspection is PdfPig, which is
// managed, and its image arm reads nothing but the MIME type. The
// in-process `create` is untouched, so a deployment that composes it
// keeps byte-for-byte what it had (GP 11 / GP 13).

/// Wire shape between the host and the worker for one extraction. Args
/// carry the options and the MIME type; the input is the document; the
/// answer is a sequence of `[u32 page][u32 text length][utf-8 text]`.
[<RequireQualifiedAccess>]
module TesseractIsolationCodec =
    /// The options and the MIME type as the args the request carries.
    let encodeArgs (options: TesseractOcrOptions) (mimeType: string) : string list = [
        options.TessDataPath
        options.Language
        string options.MaxPages
        string (int64 options.DocumentTimeout.TotalMilliseconds)
        string options.MaxDocumentBytes
        string options.ScannedTextThreshold
        mimeType
    ]

    /// The args back into options (one engine — the worker serves one
    /// request) and the MIME type.
    let decodeArgs (args: string list) : Result<TesseractOcrOptions * string, string> =
        match args with
        | [ tessData; language; maxPages; timeoutMs; maxBytes; threshold; mimeType ] ->
            match
                Int32.TryParse maxPages, Int64.TryParse timeoutMs, Int64.TryParse maxBytes, Int32.TryParse threshold
            with
            | (true, maxPages), (true, timeoutMs), (true, maxBytes), (true, threshold) ->
                Ok(
                    {
                        TessDataPath = tessData
                        Language = language
                        MaxPages = maxPages
                        DocumentTimeout = TimeSpan.FromMilliseconds(float timeoutMs)
                        MaxConcurrency = 1
                        MaxDocumentBytes = maxBytes
                        ScannedTextThreshold = threshold
                    },
                    mimeType
                )
            | _ -> Error "one of MaxPages / DocumentTimeout / MaxDocumentBytes / ScannedTextThreshold is not numeric"
        | other -> Error $"expected 7 args, got {other.Length}"

    let private writeU32 (stream: Stream) (value: int) =
        let buffer = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(), uint32 value)
        stream.Write(buffer, 0, 4)

    /// The pages as the answer bytes.
    let encodePages (pages: OcrPage list) : byte[] =
        use stream = new MemoryStream()

        for page in pages do
            let text = Encoding.UTF8.GetBytes page.Text
            writeU32 stream page.PageNumber
            writeU32 stream text.Length
            stream.Write(text, 0, text.Length)

        stream.ToArray()

    /// The answer bytes back into pages. `Error` on a truncated answer,
    /// never a partial list.
    let decodePages (answer: byte[]) : Result<OcrPage list, string> =
        let readU32 (offset: int) =
            int (BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(answer, offset, 4)))

        let rec go offset acc =
            if offset = answer.Length then
                Ok(List.rev acc)
            elif offset + 8 > answer.Length then
                Error "answer truncated inside a page header"
            else
                let pageNumber = readU32 offset
                let length = readU32 (offset + 4)

                if length < 0 || offset + 8 + length > answer.Length then
                    Error "answer truncated inside a page's text"
                else
                    let text = Encoding.UTF8.GetString(answer, offset + 8, length)

                    go (offset + 8 + length) ({ PageNumber = pageNumber; Text = text } :: acc)

        go 0 []

/// The worker-side extraction: rebuilds a provider from the args and
/// runs `ExtractText` over the request bytes. Public with a
/// parameterless constructor because that is how the worker
/// instantiates it. `create`'s own fail-loud probe runs here too, so a
/// worker whose RID has no native library answers with that message
/// rather than crashing.
type TesseractOcrEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            match TesseractIsolationCodec.decodeArgs request.Args with
            | Error reason -> Error $"malformed isolation args: {reason}"
            | Ok(options, mimeType) ->
                let provider = create options

                try
                    provider.ExtractText request.Input mimeType
                    |> Async.RunSynchronously
                    |> TesseractIsolationCodec.encodePages
                    |> Ok
                finally
                    match provider with
                    | :? IDisposable as disposable -> disposable.Dispose()
                    | _ -> ()

/// Tesseract-backed `IOcrProvider` whose `ExtractText` runs in the
/// isolation worker. Same three compose-time checks as `create` — the
/// options, the tessdata directory, and the native library on this
/// RID — performed by building an in-process provider, which is then
/// kept for `IsScanned` alone (managed PDF inspection and a MIME
/// check; it never hands a byte to the native layer) and is never asked
/// to extract. Every `IsolationRefusal` on the extraction surfaces as
/// `TesseractOcrException`, the exception type the in-process provider
/// already raises, naming the refusal: a crash says it crashed, a
/// timeout says it timed out, and the knowledge base's ingestion status
/// records the document as failed exactly as it would any other OCR
/// failure.
let createIsolated (options: TesseractOcrOptions) (isolation: ICompanionIsolation) : IOcrProvider =
    let scanned = create options

    { new IOcrProvider with
        member _.Name = $"{scanned.Name}+isolated"

        member _.IsScanned documentBytes mimeType =
            scanned.IsScanned documentBytes mimeType

        member _.ExtractText documentBytes mimeType = async {
            if isNull documentBytes || documentBytes.Length = 0 then
                return []
            else
                let! outcome =
                    CompanionIsolation.run<TesseractOcrEntry> isolation {
                        Args = TesseractIsolationCodec.encodeArgs options mimeType
                        Input = documentBytes
                    }

                match outcome with
                | Ok answer ->
                    match TesseractIsolationCodec.decodePages answer with
                    | Ok pages -> return pages
                    | Error reason ->
                        return raise (TesseractOcrException $"isolated OCR answered outside its codec: {reason}")
                | Error refusal ->
                    return raise (TesseractOcrException $"isolated OCR refused: {IsolationRefusal.describe refusal}")
        }
    }