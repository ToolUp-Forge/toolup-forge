// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.OcrProviders.Tesseract.TesseractOcrProvider

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open Tesseract
open ToolUp.Platform.IOcrProvider

// ─── Phase 500 — Tesseract IOcrProvider companion ────────────────────
//
// **Posture: production-ready, distributed-ready, native-dependent.**
// Stateless between calls in the `IOcrProvider` sense — the engine pool
// below is a cache of loaded immutable model files, not per-call state,
// so two replicas of a deployment behave identically and a request may
// land on either (GP 12 rule 4). What it is NOT is dependency-free: it
// P/Invokes libtesseract + libleptonica, so it carries the
// native-companion obligations set out in the SDK's
// "Native-dependency companions (P/Invoke)" conventions.
//
// **Why this companion exists.** The `IOcrProvider` seam has shipped
// since Phase 14i with exactly one implementation — the no-op, whose
// `IsScanned` answers `false` for everything. A deployment that uploaded
// a scanned PDF therefore indexed approximately nothing and was told the
// ingestion had COMPLETED. Two halves are needed to close that, and this
// file is the first: a real OCR engine behind the seam. The second — the
// "OCR unavailable" signal for deployments that compose no companion —
// lives in the KnowledgeBase extractor, because the honest answer when
// no OCR exists is not something an OCR provider can supply.
//
// **GP 1 — vendor isolation.** The `Tesseract` managed wrapper and the
// native libraries beneath it live in this companion and never reach
// `ToolUp.Platform.*`. Nothing Tesseract-shaped crosses the
// `IOcrProvider` boundary: the contract is `byte[] * mimeType` in,
// `OcrPage list` out.
//
// **GP 2 — no paid default.** Tesseract is Apache-2.0 and the trained
// language data is Apache-2.0 (tessdata / tessdata_fast). Nothing here
// bills anyone.
//
// **GP 13 — zero cost when uncomposed.** A deployment that never calls
// `create` loads no native library, allocates no engine, and keeps the
// no-op default byte-for-byte. This package is not referenced by any
// `ToolUp.Platform.*` project.
//
// ── Native-dependency posture (the four conventions, answered) ────────
//
// 1. **RID-specific vendoring.** The native artefacts arrive through the
//    upstream `Tesseract` NuGet package, which ships
//    `leptonica-1.82.0` + `tesseract50` for **win-x64** and **win-x86**
//    and copies them beside the consuming assembly via its own
//    `build/Tesseract.targets`. Those are the only RIDs this companion
//    declares as vendored. On Linux / macOS the operator installs the
//    distribution's libtesseract (`apt install libtesseract5`,
//    `brew install tesseract`) and the same P/Invoke resolves against
//    it — a documented, supported configuration, not an accident. Either
//    way the absence is caught at `create` time by the probe below, with
//    an error naming the RID and the remedy, never at first call deep in
//    an ingestion path.
// 2. **Narrow facade.** This companion declares no `DllImport` of its
//    own; the extern surface is the upstream wrapper's, and everything
//    here is ordinary managed F# implementing `IOcrProvider`. There is
//    consequently no `Native.fs` to review as a wire contract — the
//    review surface is the pinned `Tesseract` package version.
// 3. **Hash-pinned artefacts.** The native binaries are the upstream
//    package's content, so their integrity is the NuGet package hash
//    recorded in the lock/`Directory.Packages.props` pin plus NuGet's
//    own signature verification. The version + source + licence are
//    recorded in this companion's README and in `NOTICE.md`.
// 4. **Licensing.** Tesseract and Leptonica are Apache-2.0 and
//    BSD-2-Clause respectively — permissive, no relinking obligation,
//    and dynamically loaded regardless. The language data files are NOT
//    shipped: the operator supplies `tessdata`, which keeps a ~4 MB
//    per-language blob out of the package and lets a deployment choose
//    `tessdata_fast` / `tessdata_best` for its own speed-accuracy point.
//
// ── Resource guards (500.D) ──────────────────────────────────────────
//
// OCR is the most expensive thing an ingestion path can do — orders of
// magnitude above a PdfPig text pull — so the work it may perform is
// bounded on four independent axes, all operator-set:
//
//   * `MaxDocumentBytes` — refuses an oversized document up front. This
//     one RAISES rather than truncating, because a half-OCR'd 500 MB
//     scan silently indexed as "complete" is worse than a visible
//     failure the user can act on.
//   * `MaxPages` — caps pages per document. Truncates rather than
//     raising: a partial index of a 900-page scan is genuinely useful,
//     and the cap is a deliberate operator lever, not an error.
//   * `DocumentTimeout` — a wall-clock deadline checked BETWEEN pages.
//     Deliberately not a per-page cancellation: `TesseractEngine.Process`
//     is a synchronous native call with no cancellation token, so a
//     per-page "timeout" could only abandon a thread that keeps running
//     and keeps holding native memory. A between-pages deadline bounds
//     the work honestly with no orphaned threads.
//   * `MaxConcurrency` — bounds how many pages OCR at once across the
//     whole process, which is what actually keeps memory in check. Each
//     concurrent slot holds one `TesseractEngine` (~tens of MB with the
//     LSTM model loaded), so this multiplied by the model size IS the
//     memory ceiling. Set it in coordination with the RAG ingestion
//     concurrency (`withIngestionConcurrency`) — the two bound the same
//     machine.

// ─── Failure ─────────────────────────────────────────────────────────

/// Raised for every companion-level failure: option validation, the
/// `create`-time tessdata / native-library probe, and an oversized
/// document at `ExtractText`. One exception type so a composing app can
/// catch this companion's failures without matching on the upstream
/// wrapper's internals.
exception TesseractOcrException of message: string

let private fail (message: string) = raise (TesseractOcrException message)

// ─── Options ─────────────────────────────────────────────────────────

/// Compose-time configuration. `TessDataPath` is the only value with no
/// sensible default — it names a directory the operator provisions.
type TesseractOcrOptions = {
    /// Directory holding `<language>.traineddata`. Probed at `create`.
    TessDataPath: string
    /// Tesseract language spec — a single language (`"eng"`) or a
    /// `+`-joined set (`"eng+deu"`). Every named language must have a
    /// `.traineddata` file under `TessDataPath`; `create` checks each.
    Language: string
    /// Hard ceiling on pages OCR'd per document. Pages beyond it are
    /// not returned (truncation, not failure).
    MaxPages: int
    /// Wall-clock ceiling per document, checked between pages.
    DocumentTimeout: TimeSpan
    /// Maximum concurrent OCR operations process-wide. Also the engine
    /// pool's ceiling, so this times the loaded model size is the
    /// companion's memory bound.
    MaxConcurrency: int
    /// Documents larger than this are refused at `ExtractText` with a
    /// `TesseractOcrException` rather than partially processed.
    MaxDocumentBytes: int64
    /// `IsScanned` reports `true` for a PDF whose *whole* extractable
    /// text layer is shorter than this and which carries at least one
    /// embedded image. Below the threshold a PDF is treated as
    /// image-only even if it carries a stray text artefact (a scanner
    /// watermark, a page-number stamp).
    ScannedTextThreshold: int
}

module TesseractOcrOptions =

    /// Defaults chosen for a single web-server process sharing a machine
    /// with the rest of the deployment: three concurrent engines, a
    /// 200-page ceiling, a five-minute per-document deadline, and a
    /// 128 MB document cap.
    let forTessData (tessDataPath: string) : TesseractOcrOptions = {
        TessDataPath = tessDataPath
        Language = "eng"
        MaxPages = 200
        DocumentTimeout = TimeSpan.FromMinutes 5.0
        MaxConcurrency = 3
        MaxDocumentBytes = 128L * 1024L * 1024L
        ScannedTextThreshold = 32
    }

    /// The individual languages named by `Language`.
    let languages (options: TesseractOcrOptions) : string list =
        match options.Language with
        | null -> []
        | spec ->
            spec.Split('+')
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s.Length > 0)
            |> Array.toList

    /// Pure option validation. Returns every problem at once rather than
    /// the first — an operator fixing a config wants the whole list.
    let validate (options: TesseractOcrOptions) : Result<unit, string> =
        let problems = [
            if String.IsNullOrWhiteSpace options.TessDataPath then
                "TessDataPath is empty — it must name the directory holding the .traineddata files."

            if (languages options).IsEmpty then
                "Language is empty — set it to a tessdata language code such as \"eng\", or a '+'-joined set such as \"eng+deu\"."

            if options.MaxPages <= 0 then
                sprintf
                    "MaxPages must be positive (got %d) — a non-positive cap would OCR nothing at all."
                    options.MaxPages

            if options.DocumentTimeout <= TimeSpan.Zero then
                sprintf
                    "DocumentTimeout must be positive (got %O) — a non-positive deadline expires before the first page."
                    options.DocumentTimeout

            if options.MaxConcurrency <= 0 then
                sprintf
                    "MaxConcurrency must be positive (got %d) — it bounds the engine pool, so zero admits no work."
                    options.MaxConcurrency

            if options.MaxDocumentBytes <= 0L then
                sprintf
                    "MaxDocumentBytes must be positive (got %d) — a non-positive cap refuses every document."
                    options.MaxDocumentBytes

            if options.ScannedTextThreshold < 0 then
                sprintf "ScannedTextThreshold must not be negative (got %d)." options.ScannedTextThreshold
        ]

        match problems with
        | [] -> Ok()
        | problems -> Error(String.Join("\n  • ", "Invalid TesseractOcrOptions:" :: problems))

// ─── MIME handling ───────────────────────────────────────────────────

module Mime =

    /// Image types leptonica reads directly from memory. Anything here
    /// goes straight to the engine as a single page.
    let private imageTypes =
        set [
            "image/png"
            "image/jpeg"
            "image/jpg"
            "image/tiff"
            "image/bmp"
            "image/gif"
            "image/webp"
            "image/x-portable-anymap"
        ]

    let private normalise (mimeType: string) =
        match mimeType with
        | null -> ""
        | m ->
            // Strip any `; charset=…` parameter and case-fold.
            let head = m.Split(';') |> Array.head
            head.Trim().ToLowerInvariant()

    let isImage (mimeType: string) : bool = imageTypes.Contains(normalise mimeType)

    let isPdf (mimeType: string) : bool = normalise mimeType = "application/pdf"

    /// `true` when this companion can do anything at all with the type.
    let isSupported (mimeType: string) : bool = isImage mimeType || isPdf mimeType

// ─── PDF inspection ──────────────────────────────────────────────────
//
// Both `IsScanned` and `ExtractText` need the same read of a PDF: how
// much text does the native layer actually yield, and does the page
// carry raster images we could OCR instead. Kept in one place so the
// two answers can never disagree.

module private Pdf =

    /// Total characters of extractable text across every page, and
    /// whether any page carries at least one embedded image. A
    /// malformed PDF answers `(0, false)` rather than throwing — the
    /// caller's job is to decide whether OCR helps, not to adjudicate
    /// PDF validity (the KB extractor's error classifier does that).
    let inspect (bytes: byte[]) : int * bool =
        try
            use doc = UglyToad.PdfPig.PdfDocument.Open(bytes)
            let mutable textChars = 0
            let mutable hasImage = false

            for page in doc.GetPages() do
                let text = page.Text

                if not (isNull text) then
                    textChars <- textChars + text.Trim().Length

                if not hasImage then
                    hasImage <- page.GetImages() |> Seq.isEmpty |> not

            textChars, hasImage
        with _ ->
            0, false

    /// Every embedded raster image on `pageNumber`, rendered to PNG
    /// bytes leptonica can load. Images whose encoding PdfPig cannot
    /// re-encode are skipped rather than failing the page — a page that
    /// yields one of three images still yields text.
    let pageImages (doc: UglyToad.PdfPig.PdfDocument) (pageNumber: int) : byte[] list = [
        let page = doc.GetPage pageNumber

        for image in page.GetImages() do
            match image.TryGetPng() with
            | true, png when not (isNull png) && png.Length > 0 -> png
            | _ -> ()
    ]

// ─── Engine pool ─────────────────────────────────────────────────────
//
// `TesseractEngine` is NOT thread-safe and costs ~100 ms + tens of MB to
// construct (it loads the LSTM model), so it is neither shareable nor
// per-call-disposable. A bounded pool gives both: the semaphore is the
// concurrency guard AND the pool ceiling, so at most `MaxConcurrency`
// engines ever exist.

type private EnginePool(options: TesseractOcrOptions) =
    let engines = ConcurrentBag<TesseractEngine>()
    let gate = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency)
    let mutable disposed = false

    let newEngine () =
        new TesseractEngine(options.TessDataPath, options.Language, EngineMode.Default)

    /// Borrow an engine, run `work`, return it. The semaphore is
    /// released in `finally`, so a native throw cannot leak a slot and
    /// deadlock every later document.
    member _.Run(work: TesseractEngine -> 'T) : Async<'T> = async {
        do! gate.WaitAsync() |> Async.AwaitTask

        let engine =
            match engines.TryTake() with
            | true, e -> e
            | _ -> newEngine ()

        try
            return work engine
        finally
            // A faulted engine is still reusable — Tesseract's failures
            // are per-image, not per-engine.
            engines.Add engine
            gate.Release() |> ignore
    }

    /// Construct one engine eagerly. Used by `create` so a missing
    /// native library or unreadable tessdata surfaces at compose time.
    member _.Probe() =
        use engine = newEngine ()
        engine.DefaultPageSegMode <- PageSegMode.Auto

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true

                for engine in engines do
                    engine.Dispose()

                gate.Dispose()

// ─── Provider ────────────────────────────────────────────────────────

/// Tesseract-backed `IOcrProvider`. Module-private on purpose: `create`
/// is the only way in, so the fail-loud tessdata / native-library probe
/// cannot be bypassed by a caller who reaches for `new` instead. The
/// public surface of this companion is therefore the *interface* plus
/// its options — nothing Tesseract-shaped escapes (GP 1).
type private TesseractOcrProvider(options: TesseractOcrOptions, pool: EnginePool) =

    /// OCR one already-encoded image (PNG / JPEG / TIFF / …).
    let ocrImage (bytes: byte[]) : Async<string> =
        pool.Run(fun engine ->
            use pix = Pix.LoadFromMemory bytes
            use page = engine.Process pix
            let text = page.GetText()
            if isNull text then "" else text.Trim())

    let extractPdf (bytes: byte[]) : Async<OcrPage list> = async {
        let deadline = Stopwatch.StartNew()
        use doc = UglyToad.PdfPig.PdfDocument.Open(bytes)
        let pageCount = min doc.NumberOfPages options.MaxPages
        let results = ResizeArray<OcrPage>()
        let mutable pageNumber = 1
        let mutable expired = false

        while pageNumber <= pageCount && not expired do
            if deadline.Elapsed >= options.DocumentTimeout then
                // Bounded work (500.D): stop at the deadline and return
                // what OCR'd. The pages already recovered are indexed;
                // the rest are simply absent, exactly as they would be
                // above `MaxPages`.
                expired <- true
            else
                let images = Pdf.pageImages doc pageNumber

                let mutable pageText = ""

                for image in images do
                    if deadline.Elapsed < options.DocumentTimeout then
                        let! text = ocrImage image

                        if text.Length > 0 then
                            pageText <- if pageText = "" then text else pageText + "\n" + text

                if pageText.Length > 0 then
                    results.Add {
                        PageNumber = pageNumber
                        Text = pageText
                    }

                pageNumber <- pageNumber + 1

        return List.ofSeq results
    }

    member _.Options = options

    interface IOcrProvider with
        member _.Name = sprintf "tesseract-%s" options.Language

        member _.IsScanned (documentBytes: byte[]) (mimeType: string) = async {
            if isNull documentBytes || documentBytes.Length = 0 then
                return false
            elif Mime.isImage mimeType then
                // An image upload has no text layer by construction.
                return true
            elif Mime.isPdf mimeType then
                let textChars, hasImage = Pdf.inspect documentBytes
                return hasImage && textChars < options.ScannedTextThreshold
            else
                return false
        }

        member _.ExtractText (documentBytes: byte[]) (mimeType: string) = async {
            if isNull documentBytes || documentBytes.Length = 0 then
                return []
            elif int64 documentBytes.Length > options.MaxDocumentBytes then
                return
                    fail (
                        sprintf
                            "Document is %d bytes, above the configured MaxDocumentBytes of %d. OCR was refused rather than run partially — raise MaxDocumentBytes if this size is expected."
                            documentBytes.Length
                            options.MaxDocumentBytes
                    )
            elif Mime.isImage mimeType then
                let! text = ocrImage documentBytes

                return
                    if text.Length = 0 then
                        []
                    else
                        [ { PageNumber = 1; Text = text } ]
            elif Mime.isPdf mimeType then
                return! extractPdf documentBytes
            else
                return []
        }

    interface IDisposable with
        member _.Dispose() = (pool :> IDisposable).Dispose()

// ─── Construction ────────────────────────────────────────────────────

/// The probe convention for native companions: everything that can be
/// wrong is discovered HERE, at compose time, with an error naming the
/// remedy — never at first P/Invoke inside an ingestion path where the
/// only visible symptom is a document stuck at `Failed`.
///
/// Three checks, in the order an operator hits them:
///   1. options are internally coherent;
///   2. `TessDataPath` exists and holds a `.traineddata` for every
///      named language;
///   3. the native library actually loads on this RID — proved by
///      constructing an engine and throwing it away.
let create (options: TesseractOcrOptions) : IOcrProvider =
    match TesseractOcrOptions.validate options with
    | Error problem -> fail problem
    | Ok() ->

        if not (Directory.Exists options.TessDataPath) then
            fail (
                sprintf
                    "tessdata directory '%s' does not exist. Download the language data (e.g. https://github.com/tesseract-ocr/tessdata_fast) and point TessDataPath at the directory containing the .traineddata files."
                    options.TessDataPath
            )

        let missing =
            TesseractOcrOptions.languages options
            |> List.filter (fun language ->
                not (File.Exists(Path.Combine(options.TessDataPath, language + ".traineddata"))))

        if not missing.IsEmpty then
            fail (
                sprintf
                    "tessdata directory '%s' is missing %s. Each language named in Language needs its own .traineddata file in that directory."
                    options.TessDataPath
                    (missing |> List.map (fun l -> l + ".traineddata") |> String.concat ", ")
            )

        let pool = new EnginePool(options)

        try
            pool.Probe()
        with ex ->
            (pool :> IDisposable).Dispose()

            fail (
                sprintf
                    "Tesseract could not initialise on this platform (RID '%s'): %s\nThe native libtesseract / libleptonica binaries are vendored for win-x64 and win-x86 by the upstream Tesseract package; on Linux install libtesseract (e.g. 'apt-get install -y libtesseract5 libleptonica-dev') and on macOS 'brew install tesseract'. Composing this companion on a RID with no native library available fails here, at create, by design."
                    Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
                    ex.Message
            )

        new TesseractOcrProvider(options, pool) :> IOcrProvider

/// Convenience over `create` for the common single-language case.
let createForTessData (tessDataPath: string) : IOcrProvider =
    create (TesseractOcrOptions.forTessData tessDataPath)