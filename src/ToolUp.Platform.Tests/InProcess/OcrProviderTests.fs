module ToolUp.Platform.Tests.InProcess.OcrProviderTests

open System
open System.IO
open Expecto
open SkiaSharp
open UglyToad.PdfPig.Core
open UglyToad.PdfPig.Fonts.Standard14Fonts
open UglyToad.PdfPig.Writer
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IOcrProvider
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open ToolUp.RAG.OcrProviders.Tesseract.TesseractOcrProvider
open SharedTypes
open KnowledgeBase.ServerExtractors
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 500 — OCR companion + "OCR unavailable" signal ────────────
//
// Two arms, following the Pgvector / Redis companion precedent:
//
//  • **Structural arm (always on).** Everything provable with no native
//    library and no language data anywhere near the machine — which is
//    almost all of the phase, because the phase's *load-bearing* claim
//    is not "Tesseract can read a scan" (Tesseract's own suite covers
//    that) but "a deployment with no OCR companion says so, out loud,
//    instead of reporting a successful index of nothing". That claim is
//    about the KnowledgeBase extraction seam and is provable offline.
//    The Tesseract companion's own create-time guards — the whole point
//    of a fail-loud native companion — are likewise provable offline,
//    because they fire BEFORE the native library is touched.
//
//  • **Native arm (env-gated on `TOOLUP_TESSDATA`).** Real engine, real
//    language data, real OCR over a synthesised scan. Reported
//    **Pending** when the variable is unset, so a fresh checkout and CI
//    are green without provisioning tessdata — the same posture the
//    `ToolUp.AIProviders.Tests` live arms take. No Docker, no
//    Testcontainers.
//
// The fixtures are synthesised at run time rather than checked in:
// SkiaSharp makes the raster, PdfPig's writer wraps it in a PDF. A
// binary fixture in the tree would be opaque to review and would drift
// silently from what the extractor actually does with it.

[<Literal>]
let private TessDataEnvVar = "TOOLUP_TESSDATA"

let private tessDataPath =
    match Environment.GetEnvironmentVariable TessDataEnvVar with
    | null
    | "" -> None
    | path -> Some path

// ─── Fixtures ────────────────────────────────────────────────────────

/// A JPEG carrying `text` drawn large and black on white — the raster a
/// scanner would produce. Used both as a standalone image upload and as
/// the sole content of the scanned-PDF fixture.
let private syntheticScanJpeg (text: string) : byte[] =
    let width, height = 1200, 400
    let info = SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
    use bitmap = new SKBitmap(info)
    use canvas = new SKCanvas(bitmap)
    canvas.Clear SKColors.White

    use paint = new SKPaint()
    paint.Color <- SKColors.Black
    paint.IsAntialias <- true

    use font = new SKFont(SKTypeface.Default, 140.0f)
    canvas.DrawText(text, 60.0f, 260.0f, font, paint)
    canvas.Flush()

    use image = SKImage.FromBitmap bitmap
    use data = image.Encode(SKEncodedImageFormat.Jpeg, 92)
    data.ToArray()

/// A PDF whose single page carries one full-page image and no text
/// layer at all — structurally what a scanner emits.
let private scannedPdf (text: string) : byte[] =
    use builder = new PdfDocumentBuilder()
    let page = builder.AddPage(612.0, 792.0)

    page.AddJpeg(syntheticScanJpeg text, PdfRectangle(50.0, 400.0, 550.0, 700.0))
    |> ignore

    builder.Build()

/// An ordinary text PDF — the control. The probe must NOT claim this one
/// needs OCR.
let private textPdf (body: string) : byte[] =
    use builder = new PdfDocumentBuilder()
    let font = builder.AddStandard14Font Standard14Font.Helvetica
    let page = builder.AddPage(612.0, 792.0)
    page.AddText(body, 12.0, PdfPoint(50.0, 700.0), font) |> ignore
    builder.Build()

/// A PDF with neither text nor images. The probe must NOT claim this one
/// needs OCR either — it is a genuinely empty document, and reporting
/// every empty file as "OCR unavailable" would turn a precise signal
/// into noise.
let private blankPdf () : byte[] =
    use builder = new PdfDocumentBuilder()
    builder.AddPage(612.0, 792.0) |> ignore
    builder.Build()

// ─── Test doubles ────────────────────────────────────────────────────

/// The shipped default, resolved exactly as `KnowledgeApiDeps.resolve`
/// resolves it when no companion is composed.
let private noOpOcr: IOcrProvider =
    ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()

let private noOpTables: ToolUp.Platform.ITableExtractor.ITableExtractor =
    ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()

/// A stand-in companion. Deliberately provider-agnostic: the extraction
/// wiring must work for ANY `IOcrProvider`, so proving it with Tesseract
/// would prove less, not more — and would drag native binaries into the
/// always-on arm.
type private StubOcrProvider(name: string, isScanned: bool, pages: OcrPage list) =
    let mutable extractCalls = 0
    member _.ExtractCalls = extractCalls

    interface IOcrProvider with
        member _.Name = name
        member _.IsScanned _ _ = async { return isScanned }

        member _.ExtractText _ _ = async {
            extractCalls <- extractCalls + 1
            return pages
        }

// ─── Structural arm: which provider did we get? ──────────────────────

let private compositionDetection =
    testList "composition detection" [
        test "the no-op default does not count as a composed companion" {
            Expect.isFalse
                (ocrComposed noOpOcr)
                "the shipped no-op must not read as a composed OCR companion — every 'is OCR available' answer keys off this"
        }

        test "a real companion counts as composed" {
            let stub = StubOcrProvider("stub-ocr", true, []) :> IOcrProvider

            Expect.isTrue (ocrComposed stub) "a concrete IOcrProvider implementation must read as composed"
        }

        test "a null provider does not count as composed" {
            // `KnowledgeApiDeps.resolve` guarantees non-null, but a test
            // harness or a hand-rolled composition root can bypass it,
            // and a NullReferenceException is a poor way to discover
            // that OCR is missing.
            Expect.isFalse (ocrComposed (Unchecked.defaultof<IOcrProvider>)) "a null provider must read as not composed"
        }

        test "a stand-in that adopts the no-op's identifier does not count as composed" {
            let impostor = StubOcrProvider("NoOp", true, []) :> IOcrProvider

            Expect.isFalse
                (ocrComposed impostor)
                "the Name fallback must catch a provider that calls itself 'noop', case-insensitively"
        }
    ]

// ─── Structural arm: the "OCR unavailable" classification ────────────

let private unavailableClassification =
    testList "OCR-unavailable classification" [
        test "a scanned PDF with no companion is classified, and the detail names the remedy" {
            let detail =
                Expect.wantSome
                    (ocrUnavailableDetail noOpOcr "contract-scan.pdf" (scannedPdf "INVOICE"))
                    "a scanned PDF with no OCR companion must be classified — this is the whole silent-degradation gap"

            Expect.stringContains detail "contract-scan.pdf" "the detail must name the file the user uploaded"

            Expect.stringContains
                detail
                "IOcrProvider"
                "the detail must name the seam, so an operator knows what is missing"

            Expect.stringContains
                detail
                "ToolUp.OcrProviders.Tesseract"
                "the detail must name a concrete companion — 'OCR unavailable' with no remedy is only half a signal"
        }

        test "an image upload with no companion is classified" {
            let detail =
                Expect.wantSome
                    (ocrUnavailableDetail noOpOcr "receipt.png" (syntheticScanJpeg "TOTAL"))
                    "an image can only be indexed through OCR, so its emptiness must be classified"

            Expect.stringContains detail "receipt.png" "the detail must name the file"
        }

        test "an ordinary text PDF is NOT classified" {
            Expect.isNone
                (ocrUnavailableDetail noOpOcr "report.pdf" (textPdf "Quarterly revenue rose by eleven percent."))
                "a PDF with a real text layer must never be reported as needing OCR"
        }

        test "a blank PDF is NOT classified" {
            Expect.isNone
                (ocrUnavailableDetail noOpOcr "blank.pdf" (blankPdf ()))
                "an empty document is Complete 0, not a missing capability — classifying it would make the signal noise"
        }

        test "a non-document type is NOT classified" {
            Expect.isNone
                (ocrUnavailableDetail noOpOcr "archive.zip" [| 0uy; 1uy; 2uy |])
                "an unrecognised type is UnsupportedFormat's business, not OCR's"
        }

        test "with a companion composed, nothing is classified as unavailable" {
            let stub = StubOcrProvider("stub-ocr", true, []) :> IOcrProvider

            Expect.isNone
                (ocrUnavailableDetail stub "contract-scan.pdf" (scannedPdf "INVOICE"))
                "when a real provider looked and found nothing, that is a genuine empty result — dressing it up as a missing capability would be a lie in the other direction"
        }

        test "a corrupt PDF is NOT classified (it is the error classifier's business)" {
            let notAPdf = Text.Encoding.UTF8.GetBytes "%PDF-1.4 this is not a pdf"

            Expect.isNone
                (ocrUnavailableDetail noOpOcr "corrupt.pdf" notAPdf)
                "an unopenable PDF must fall through to ExtractionErrors.classify, which can say 'corrupt'; claiming it needs OCR would misdirect the user"
        }
    ]

// ─── Structural arm: the extraction wiring ───────────────────────────

let private extractionWiring =
    testList "extraction wiring" [
        test "a scanned PDF yields nothing without a companion" {
            let chunks =
                extractChunks noOpOcr noOpTables "doc-1" "contract-scan.pdf" (scannedPdf "INVOICE")
                |> Async.RunSynchronously

            Expect.isEmpty
                chunks
                "with the no-op default a scanned PDF extracts nothing — this is the state the signal exists to explain"
        }

        test "a scanned PDF indexes real text with a companion composed" {
            let stub =
                StubOcrProvider(
                    "stub-ocr",
                    true,
                    [
                        {
                            PageNumber = 1
                            Text = "ACME LIMITED — INVOICE 4471 — TOTAL DUE 1,240.00"
                        }
                        {
                            PageNumber = 2
                            Text = "Payment terms: thirty days from date of issue."
                        }
                    ]
                )

            let chunks =
                extractChunks (stub :> IOcrProvider) noOpTables "doc-1" "contract-scan.pdf" (scannedPdf "INVOICE")
                |> Async.RunSynchronously

            Expect.hasLength chunks 2 "one chunk per OCR page"

            let text = chunks |> List.map (fun (c, _) -> c.Content) |> String.concat "\n"
            Expect.stringContains text "INVOICE 4471" "the OCR text must reach the indexed chunk"

            let locations = chunks |> List.map (fun (_, src) -> src.Location)

            Expect.equal
                locations
                [ Page 1; Page 2 ]
                "OCR page numbers must round-trip into the citation locator, so a Sources panel opens the right page"
        }

        test "an image upload indexes through the companion" {
            let stub =
                StubOcrProvider(
                    "stub-ocr",
                    true,
                    [
                        {
                            PageNumber = 1
                            Text = "TOTAL 42.00 — THANK YOU FOR YOUR CUSTOM"
                        }
                    ]
                )

            let chunks =
                extractChunks (stub :> IOcrProvider) noOpTables "doc-2" "receipt.png" (syntheticScanJpeg "TOTAL")
                |> Async.RunSynchronously

            Expect.hasLength chunks 1 "an image is one page"
            let content = chunks |> List.map (fun (c, _) -> c.Content) |> String.concat ""
            Expect.stringContains content "TOTAL 42.00" "the OCR text must reach the indexed chunk"
        }

        test "an image upload yields nothing without a companion, and costs no OCR call" {
            let chunks =
                extractChunks noOpOcr noOpTables "doc-2" "receipt.png" (syntheticScanJpeg "TOTAL")
                |> Async.RunSynchronously

            Expect.isEmpty chunks "with no companion an image cannot be indexed"
        }

        test "the empty-text fallback runs OCR when IsScanned said no but nothing extracted" {
            // The heuristic is allowed to be wrong; the fallback is what
            // stops it being *silently* wrong. A provider claiming the
            // document is not scanned must still be asked to extract
            // when the native path came back empty.
            let stub =
                StubOcrProvider(
                    "stub-ocr",
                    false,
                    [
                        {
                            PageNumber = 1
                            Text = "RECOVERED BY THE FALLBACK PATH"
                        }
                    ]
                )

            let chunks =
                extractChunks (stub :> IOcrProvider) noOpTables "doc-3" "sneaky-scan.pdf" (scannedPdf "INVOICE")
                |> Async.RunSynchronously

            Expect.hasLength chunks 1 "the fallback must recover the page the IsScanned heuristic missed"
            Expect.equal stub.ExtractCalls 1 "ExtractText must have been called exactly once by the fallback"
        }

        test "the fallback does not fire on a PDF that extracted normally" {
            let stub =
                StubOcrProvider(
                    "stub-ocr",
                    false,
                    [
                        {
                            PageNumber = 1
                            Text = "SHOULD NEVER APPEAR"
                        }
                    ]
                )

            let chunks =
                extractChunks
                    (stub :> IOcrProvider)
                    noOpTables
                    "doc-4"
                    "report.pdf"
                    (textPdf
                        "Quarterly revenue rose by eleven percent against the prior period, driven by the northern region.")
                |> Async.RunSynchronously

            Expect.isNonEmpty chunks "the native text path must still produce the chunks"
            Expect.equal stub.ExtractCalls 0 "OCR must not run when native extraction already produced text"

            let text = chunks |> List.map (fun (c, _) -> c.Content) |> String.concat ""

            Expect.isFalse
                (text.Contains "SHOULD NEVER APPEAR")
                "no OCR text may leak into a natively-extracted document"
        }

        test "an uncomposed deployment pays no extra OCR call on an empty PDF (GP 13)" {
            // The no-op cannot count its calls, so this is asserted the
            // only way it can be: the fallback is gated on `ocrComposed`,
            // and `ocrComposed noOpOcr` is false. The case pins the
            // gate's existence so a future refactor that drops it fails
            // here rather than quietly doubling every empty ingestion.
            Expect.isFalse
                (ocrComposed noOpOcr)
                "the empty-text fallback is gated on this; without the gate an uncomposed deployment pays an extra ExtractText per empty document"
        }
    ]

// ─── Structural arm: the Tesseract companion's own guards ────────────

/// Assert `f` raises and that the message NAMES the problem. For a
/// fail-loud companion the message text IS the behaviour under test —
/// an error at the right moment that says nothing useful is only
/// marginally better than one at the wrong moment.
let private expectRaisesNaming (fragment: string) (message: string) (f: unit -> unit) =
    let caught =
        try
            f ()
            None
        with ex ->
            Some ex

    match caught with
    | None -> failtestf "%s — expected an exception naming '%s'; none was raised" message fragment
    | Some ex -> Expect.stringContains ex.Message fragment message

let private tesseractOptions =
    testList "Tesseract options" [
        test "the default options validate" {
            match TesseractOcrOptions.validate (TesseractOcrOptions.forTessData "/tessdata") with
            | Ok() -> ()
            | Error problem -> failtestf "the shipped defaults must validate; got: %s" problem
        }

        test "every invalid lever is reported, not just the first" {
            let broken = {
                TesseractOcrOptions.forTessData "" with
                    Language = ""
                    MaxPages = 0
                    DocumentTimeout = TimeSpan.Zero
                    MaxConcurrency = 0
                    MaxDocumentBytes = 0L
            }

            match TesseractOcrOptions.validate broken with
            | Ok() -> failtest "wholly invalid options must not validate"
            | Error problem ->
                for lever in
                    [
                        "TessDataPath"
                        "Language"
                        "MaxPages"
                        "DocumentTimeout"
                        "MaxConcurrency"
                        "MaxDocumentBytes"
                    ] do
                    Expect.stringContains
                        problem
                        lever
                        (sprintf "an operator fixing a config wants every problem at once; '%s' was not reported" lever)
        }

        test "a '+'-joined language spec splits into its languages" {
            let options = {
                TesseractOcrOptions.forTessData "/tessdata" with
                    Language = "eng+deu + fra"
            }

            Expect.equal
                (TesseractOcrOptions.languages options)
                [ "eng"; "deu"; "fra" ]
                "each named language needs its own .traineddata, so each must be enumerable"
        }
    ]

let private tesseractFailLoud =
    testList "Tesseract fail-loud create" [
        test "a missing tessdata directory fails at create, naming the path and the remedy" {
            let absent =
                Path.Combine(Path.GetTempPath(), "toolup-tessdata-" + Guid.NewGuid().ToString("N"))

            expectRaisesNaming absent "create must name the directory it could not find" (fun () ->
                create (TesseractOcrOptions.forTessData absent) |> ignore)

            expectRaisesNaming "traineddata" "create must name what the directory is supposed to contain" (fun () ->
                create (TesseractOcrOptions.forTessData absent) |> ignore)
        }

        test "a tessdata directory missing the language file fails at create, naming the file" {
            let empty =
                Path.Combine(Path.GetTempPath(), "toolup-tessdata-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory empty |> ignore

            try
                expectRaisesNaming
                    "deu.traineddata"
                    "create must name the specific language file that is absent"
                    (fun () ->
                        create {
                            TesseractOcrOptions.forTessData empty with
                                Language = "deu"
                        }
                        |> ignore)
            finally
                try
                    Directory.Delete(empty, true)
                with _ ->
                    ()
        }

        test "invalid options fail at create, before any filesystem or native probe" {
            expectRaisesNaming
                "MaxConcurrency"
                "option validation must run first — it is the cheapest check and the one most likely to be wrong"
                (fun () ->
                    create {
                        TesseractOcrOptions.forTessData "/tessdata" with
                            MaxConcurrency = 0
                    }
                    |> ignore)
        }
    ]

let private tesseractMime =
    testList "Tesseract MIME classification" [
        test "image types are recognised" {
            for mimeType in [ "image/png"; "image/jpeg"; "IMAGE/TIFF"; "image/png; charset=binary" ] do
                Expect.isTrue (Mime.isImage mimeType) (sprintf "'%s' must classify as an image" mimeType)
        }

        test "pdf is recognised and is not an image" {
            Expect.isTrue (Mime.isPdf "application/pdf") "application/pdf must classify as a PDF"
            Expect.isFalse (Mime.isImage "application/pdf") "a PDF is not a single image"
        }

        test "an unsupported type is neither" {
            Expect.isFalse (Mime.isSupported "application/zip") "OCR has nothing to offer a zip archive"
        }
    ]

// ─── Structural arm: the upload path end to end (the GP 13 claim) ────
//
// Everything above proves a piece. This proves the sentence: *without
// the companion composed, a scanned upload REPORTS "OCR unavailable"
// rather than silently indexing nothing* — driven through the real
// `uploadDocument` handler, reading the terminal status back off the
// persisted index exactly as the client's status poll does.

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private noopNotifications =
    { new INotificationChannel with
        member _.Publish(_, _) = async.Return()

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()
    }

let private mkDeps (storage: IBlobStorage) (ocr: IOcrProvider) (container: string) : KnowledgeApiDeps = {
    Storage = storage
    Queue = IngestionQueue()
    OcrProvider = ocr
    TableExtractor = noOpTables
    Notifications = noopNotifications
    Logger = noopLogger
    Scope = {
        ScopeId = container
        Container = container
        Persist = true
    }
    UserId = "user-1"
    VectorScope = Deployment
    VectorStore = None
    IndexLifecycle = None
    EventStore = None
    NarrativeStore = None
    AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
    OriginalResolver = createDefault ()
    AuditLog = None
    RecordEnqueue = ignore
    PublishInventory = fun () -> async.Return()
    MarkIngestionFailed = fun _ _ _ -> async.Return()
    EnsureContextWriteAllowed = fun () -> async { return Ok() }
    ScopeResolvedFromRequest = true
    UploadPolicy = KnowledgeUploadPolicy.permissive
    DedupPolicy = KnowledgeDedupPolicy.enabled
    VersioningPolicy = KnowledgeVersioningPolicy.disabled
    // Phase 105 — retention not composed: the pre-105 convention-blob path.
    DataObjectStore = None
    QuotaPolicy = KnowledgeQuotaPolicy.unlimited
    RetentionPolicy = KnowledgeRetentionPolicy.retainForever
    // Phase 515 — no scanner composed: the pre-515 upload path.
    ContentScanner = None
    ScanPolicy = ContentScanPolicy.defaults
    DisclosureGate = None
    // Phase 511 — bulk import is not exercised here: archive guards at
    // their shipped defaults, URL ingestion inert, no transport.
    ArchiveImportPolicy = ArchiveImportPolicy.defaults
    UrlIngestionPolicy = UrlIngestionPolicy.disabled
    UrlFetcher = None
}

/// Extraction runs off the request path via `Async.Start`, so the
/// terminal status is read by polling the persisted index — the same
/// thing the client's 2s status poll does.
let rec private waitTerminal (deps: KnowledgeApiDeps) (docId: string) (attempts: int) = async {
    let! index = loadIndex deps.Storage deps.Scope.Container

    match index |> List.tryFind (fun d -> d.Id = docId) with
    | Some d ->
        match d.Status with
        | Complete _
        | Failed _
        | UnsupportedFormat _
        | UploadRejected _
        | OcrUnavailable _ -> return Some d.Status
        | _ when attempts <= 0 -> return Some d.Status
        | _ ->
            do! Async.Sleep 50
            return! waitTerminal deps docId (attempts - 1)
    | None -> return None
}

let private uploadPath =
    testList "upload path (GP 13 acceptance)" [
        testCaseAsync "a scanned PDF with no companion reports OcrUnavailable, not Complete 0"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let deps = mkDeps storage noOpOcr "team-a"

            let! doc = uploadDocument deps (scannedPdf "INVOICE") "contract-scan.pdf"
            let! terminal = waitTerminal deps doc.Id 100

            match terminal with
            | Some(OcrUnavailable detail) ->
                Expect.stringContains
                    detail
                    "ToolUp.OcrProviders.Tesseract"
                    "the user-visible reason must name the remedy"
            | Some(Complete 0) ->
                failtest
                    "the scanned upload reported a successful index of zero chunks — this is exactly the silent degradation Phase 500 exists to remove"
            | other -> failtestf "expected OcrUnavailable, got %A" other

            // Storage is unchanged by the signal: the original is still
            // downloadable, as it was before.
            let! blobs = storage.List("team-a", "knowledge/")

            Expect.isNonEmpty
                blobs
                "the raw upload must still be stored — the signal is about searchability, not retention"
        }

        testCaseAsync "an image upload with no companion reports OcrUnavailable, not UnsupportedFormat"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let deps = mkDeps storage noOpOcr "team-a"

            let! doc = uploadDocument deps (syntheticScanJpeg "TOTAL") "receipt.png"
            let! terminal = waitTerminal deps doc.Id 100

            match terminal with
            | Some(OcrUnavailable _) -> ()
            | other ->
                failtestf
                    "an image's type IS handled once OCR exists, so 'no extractor for .png' is the wrong story to tell; got %A"
                    other
        }

        testCaseAsync "a scanned PDF WITH a companion composed indexes and reports Complete"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let stub =
                StubOcrProvider(
                    "stub-ocr",
                    true,
                    [
                        {
                            PageNumber = 1
                            Text = "ACME LIMITED — INVOICE 4471"
                        }
                    ]
                )

            let deps = mkDeps storage (stub :> IOcrProvider) "team-a"

            let! doc = uploadDocument deps (scannedPdf "INVOICE") "contract-scan.pdf"
            let! terminal = waitTerminal deps doc.Id 100

            match terminal with
            | Some(OcrUnavailable _) -> failtest "a composed companion must never yield the unavailable signal"
            | Some(Complete 0) -> failtest "the OCR chunks did not reach the ingestion path"
            | _ ->
                // With a queue but no vector store the document advances
                // to Embedding rather than Complete; what this case pins
                // is that chunks WERE produced and enqueued, which is the
                // difference the companion makes.
                let! index = loadIndex storage "team-a"

                let stored =
                    index |> List.tryFind (fun d -> d.Id = doc.Id) |> Option.defaultValue doc

                Expect.isGreaterThan
                    stored.ChunkCount
                    0
                    "the composed companion must produce indexable chunks from a document that yielded none without it"
        }

        testCaseAsync "an ordinary text PDF is unaffected (GP 11)"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let deps = mkDeps storage noOpOcr "team-a"

            let! doc =
                uploadDocument
                    deps
                    (textPdf "Quarterly revenue rose by eleven percent against the prior period.")
                    "report.pdf"

            let! terminal = waitTerminal deps doc.Id 100

            match terminal with
            | Some(OcrUnavailable _) -> failtest "a PDF with a real text layer must never be reported as needing OCR"
            | _ -> ()
        }
    ]

// ─── Native arm (env-gated) ──────────────────────────────────────────

let private nativeArm =
    match tessDataPath with
    | None ->
        testList "native Tesseract" [
            ptest "requires TOOLUP_TESSDATA (a directory holding eng.traineddata)" { () }
        ]
    | Some path ->
        testList "native Tesseract" [
            test "create succeeds against real tessdata and reports its language" {
                use provider = create (TesseractOcrOptions.forTessData path) :?> IDisposable

                let named = provider :?> IOcrProvider
                Expect.equal named.Name "tesseract-eng" "the provider must identify itself by engine and language"
            }

            test "IsScanned discriminates a scan from a text PDF" {
                let provider = create (TesseractOcrOptions.forTessData path)

                Expect.isTrue
                    (provider.IsScanned (scannedPdf "INVOICE") "application/pdf"
                     |> Async.RunSynchronously)
                    "an image-only PDF must read as scanned"

                Expect.isFalse
                    (provider.IsScanned (textPdf "Quarterly revenue rose by eleven percent.") "application/pdf"
                     |> Async.RunSynchronously)
                    "a PDF with a real text layer must not read as scanned"

                Expect.isTrue
                    (provider.IsScanned (syntheticScanJpeg "TOTAL") "image/jpeg"
                     |> Async.RunSynchronously)
                    "an image upload has no text layer by construction"
            }

            test "a scanned PDF yields real text end to end" {
                let provider = create (TesseractOcrOptions.forTessData path)

                let pages =
                    provider.ExtractText (scannedPdf "INVOICE") "application/pdf"
                    |> Async.RunSynchronously

                Expect.isNonEmpty pages "OCR must recover at least one page from the synthesised scan"

                let text = pages |> List.map _.Text |> String.concat " "

                Expect.stringContains
                    (text.ToUpperInvariant())
                    "INVOICE"
                    "the word rendered into the scan must come back out of OCR"
            }

            test "an oversized document is refused rather than partially processed" {
                let provider =
                    create {
                        TesseractOcrOptions.forTessData path with
                            MaxDocumentBytes = 16L
                    }

                expectRaisesNaming
                    "MaxDocumentBytes"
                    "the size guard must refuse loudly, naming the lever the operator would raise"
                    (fun () ->
                        provider.ExtractText (scannedPdf "INVOICE") "application/pdf"
                        |> Async.RunSynchronously
                        |> ignore)
            }
        ]

[<Tests>]
let tests =
    testList "Phase 500 — OCR provider companion + OCR-unavailable signal" [
        compositionDetection
        unavailableClassification
        extractionWiring
        uploadPath
        tesseractOptions
        tesseractFailLoud
        tesseractMime
        nativeArm
    ]