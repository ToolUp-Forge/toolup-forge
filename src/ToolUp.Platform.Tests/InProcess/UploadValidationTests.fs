module ToolUp.Platform.Tests.InProcess.UploadValidationTests

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open Giraffe
open Expecto
open ToolUp.Platform
open ToolUp.AssetStore
open ToolUp.Platform.Tests.Contracts

// ─── Phase 186 — upload-validation seam ──────────────────────────
//
// Four things are pinned here, in the order the upload path meets
// them:
//
//   1. `AssetUploadHandler.readCapped` stops READING at the ceiling.
//      Measured with a counting stream rather than inferred from the
//      returned error — an implementation that buffered the whole
//      payload and compared afterwards returns the identical `Error`,
//      so the error alone proves nothing.
//   2. `MagicBytes.sniff` recognises what the bytes are, and
//      `containsMarkup` catches the polyglot a header check passes.
//   3. `UploadValidator.run` is FAIL-CLOSED: a validator that cannot
//      answer, and a validator that raises, both refuse. Neither
//      collapses into "clean".
//   4. The handler runs the seam AFTER `UploadRequest.create` and
//      BEFORE `IAssetStore.Upload`, so a refusal means nothing was
//      stored — asserted against a store that counts its calls.
//
// Every refusal case is paired with a control asserting a legitimate
// upload of the same shape still succeeds, and the default
// (`NoUploadValidator`) is pinned as admitting everything the
// pre-186 path admitted (GP 11).
//
// Corpora are `byte[]` literals built here rather than committed
// binary fixtures: a magic-byte test genuinely needs binary content,
// and building it in code keeps raw control bytes out of the tree.

// ── Byte corpora ────────────────────────────────────────────────

/// Pad a header out to a plausible payload length. The sniffer only
/// ever reads the prefix, so the filler's content is irrelevant —
/// it is deliberately non-zero so nothing accidentally depends on a
/// zero-filled tail.
let private withBody (header: byte[]) =
    Array.append header (Array.init 96 (fun i -> byte ((i * 7) % 251)))

let private pngBytes =
    withBody [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]

let private jpegBytes = withBody [| 0xFFuy; 0xD8uy; 0xFFuy; 0xE0uy |]

let private gifBytes = withBody (Text.Encoding.ASCII.GetBytes "GIF89a")

/// RIFF container: the "RIFF" tag, a 4-byte little-endian chunk
/// size the sniffer never reads, then the "WEBP" form type at
/// offset 8. The size bytes are written as escapes, never as raw
/// bytes in the literal — a raw NUL makes git classify the whole
/// file binary, which silently disables eol normalisation.
let private webpBytes =
    withBody [|
        yield! Text.Encoding.ASCII.GetBytes "RIFF"
        yield! [| 0x40uy; 0x00uy; 0x00uy; 0x00uy |]
        yield! Text.Encoding.ASCII.GetBytes "WEBP"
    |]

/// ISO-BMFF: a 4-byte box size, then the "ftyp" box tag at offset
/// 4 and the major brand at offset 8. Same escape rule as above.
let private avifBytes =
    withBody [|
        yield! [| 0x00uy; 0x00uy; 0x00uy; 0x20uy |]
        yield! Text.Encoding.ASCII.GetBytes "ftypavif"
    |]

let private zipBytes = withBody [| 0x50uy; 0x4Buy; 0x03uy; 0x04uy |]
let private elfBytes = withBody [| 0x7Fuy; 0x45uy; 0x4Cuy; 0x46uy |]
let private exeBytes = withBody [| 0x4Duy; 0x5Auy |]

/// The polyglot: a byte-for-byte valid GIF header followed by markup
/// a content-sniffing browser will happily execute if it is ever
/// served back from the deployment's own origin. Every magic-byte
/// check in the table passes this.
let private gifHtmlPolyglot =
    Array.append (Text.Encoding.ASCII.GetBytes "GIF89a") (Text.Encoding.ASCII.GetBytes "<script>alert(1)</script>")

/// Bytes matching nothing in the table.
let private unknownBytes = withBody [| 0x2Auy; 0x13uy; 0x77uy; 0x01uy |]

// ── Phase 639 — Office Open XML spreadsheet packages ─────────────
//
// Real OPC packages, built with the BCL zip writer rather than an
// OpenXml dependency: the validator reads exactly one part — the
// `[Content_Types].xml` manifest — so a fixture carrying that part
// (plus a stand-in workbook part, and for the macro flavour a
// `vbaProject.bin`) exercises the whole code path faithfully. The
// grid-level parity against genuine OpenXml-written workbooks is
// pinned in the ToolUp.Tabular pack, where the reader lives.

let private zipOf (entries: (string * byte[]) list) : byte[] =
    use buffer = new MemoryStream()

    // Nested so the archive is disposed — and its central directory
    // therefore flushed — before `ToArray` reads the buffer.
    let writeArchive () =
        use archive =
            new Compression.ZipArchive(buffer, Compression.ZipArchiveMode.Create, true)

        for name, content in entries do
            let entry = archive.CreateEntry name
            use entryStream = entry.Open()
            entryStream.Write(content, 0, content.Length)

    writeArchive ()
    buffer.ToArray()

let private opcPackage (workbookPartType: string) (extraParts: (string * byte[]) list) : byte[] =
    let manifest =
        sprintf
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="bin" ContentType="application/vnd.ms-office.vbaProject"/><Override PartName="/xl/workbook.xml" ContentType="%s"/></Types>"""
            workbookPartType

    zipOf [
        "[Content_Types].xml", Text.Encoding.UTF8.GetBytes manifest
        "xl/workbook.xml", Text.Encoding.UTF8.GetBytes "<workbook/>"
        yield! extraParts
    ]

/// A plain `.xlsx` package.
let private xlsxPackage =
    opcPackage "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" []

/// A macro-enabled `.xlsm` package — same manifest shape, macro
/// content type, plus the `vbaProject.bin` part itself.
let private xlsmPackage =
    opcPackage "application/vnd.ms-excel.sheet.macroEnabled.main+xml" [
        "xl/vbaProject.bin", [| 0xD0uy; 0xCFuy; 0x11uy; 0xE0uy; 0xA1uy; 0xB1uy; 0x1Auy; 0xE1uy |]
    ]

/// A zip that is not an OPC package at all.
let private plainZip = zipOf [ "notes.txt", Text.Encoding.UTF8.GetBytes "hello" ]

let private xlsxMime =
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"

let private xlsmMime = "application/vnd.ms-excel.sheet.macroEnabled.12"

let private packageAwareValidator () =
    SniffingUploadValidator MimeSniffOptions.withSpreadsheetPackages :> IUploadValidator

// ── Doubles ─────────────────────────────────────────────────────

/// A validator standing in for a scan backend that is configured but
/// unreachable — the case the whole fail-closed posture exists for.
type private UnavailableScanner() =
    interface IUploadValidator with
        member _.Name = "unavailable-scanner"

        member _.Validate(_, _) =
            async.Return(Error(ValidationUnavailable "scan daemon refused the connection"))

/// A scan backend that positively identifies the payload.
type private DetectingScanner() =
    interface IUploadValidator with
        member _.Name = "detecting-scanner"

        member _.Validate(_, _) =
            async.Return(Error(MalwareDetected "Eicar-Test-Signature"))

/// A validator that throws instead of returning a verdict — a
/// perfectly ordinary shape for a companion wrapping an HTTP client
/// whose socket has gone away.
type private ThrowingScanner() =
    interface IUploadValidator with
        member _.Name = "throwing-scanner"

        member _.Validate(_, _) =
            raise (InvalidOperationException "socket closed")

/// A validator that admits everything — the "legitimate upload"
/// control for handler-level assertions.
type private PermissiveScanner() =
    interface IUploadValidator with
        member _.Name = "permissive"
        member _.Validate(_, _) = async.Return(Ok())

/// Counts the bytes actually pulled through it. The size probe's
/// teeth: an implementation that read the whole payload and compared
/// afterwards would return the same `Error` and a wildly different
/// count.
type private CountingStream(inner: Stream) =
    inherit Stream()
    let mutable pulled = 0L

    member _.BytesRead = pulled

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = inner.Length

    override _.Position
        with get () = inner.Position
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()

    override _.Read(buffer, offset, count) =
        let read = inner.Read(buffer, offset, count)
        pulled <- pulled + int64 read
        read

    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

/// `IAssetStore` that records whether `Upload` was ever reached.
type private CountingAssetStore() =
    let mutable uploads = 0

    member _.Uploads = uploads

    interface IAssetStore with
        member _.Upload(_, request) = async {
            uploads <- uploads + 1

            return
                Ok {
                    Id = AssetId.create ()
                    ContentHash = String.replicate 64 "a"
                    OriginalFilename = request.OriginalFilename
                    MimeType = request.MimeType
                    SizeBytes = int64 request.Bytes.Length
                    AltText = request.AltText
                    Caption = request.Caption
                    UploadedBy = request.UploadedBy
                    UploadedAt = DateTimeOffset.UtcNow
                    Width = None
                    Height = None
                    DerivativeProfile = request.Profile
                }
        }

        member _.Get(_, _) = async.Return None

        member _.GetDerivative(_, _, _) =
            async.Return(Error AssetDerivativeError.AssetNotFound)

        member _.Delete(_, _) = async.Return(Ok())
        member _.List(_, _, _) = async.Return []

// ── Handler harness ─────────────────────────────────────────────

/// Drive `AssetUploadHandler.uploadHandler` against a synthetic
/// multipart request. Returns the status code, the response body and
/// the store's upload count.
let private postUpload
    (options: AssetStoreOptions)
    (store: CountingAssetStore)
    (bytes: byte[])
    (declaredMime: string)
    : Async<int * string> =
    async {
        let services = ServiceCollection()
        services.AddSingleton<IAssetStore>(store) |> ignore
        ComposeBootstrap.registerGiraffeDefaults services
        use provider = services.BuildServiceProvider()

        let ctx = DefaultHttpContext()
        ctx.RequestServices <- provider
        ctx.Request.Method <- "POST"
        ctx.Request.Path <- PathString "/api/assets/upload"
        ctx.Request.ContentType <- "multipart/form-data; boundary=----phase186"

        let file =
            FormFile(new MemoryStream(bytes), 0L, int64 bytes.Length, "file", "fixture.png")

        file.Headers <- HeaderDictionary()
        file.Headers["Content-Type"] <- StringValues declaredMime

        let files = FormFileCollection()
        files.Add file

        let fields = Dictionary<string, StringValues>()
        fields["altText"] <- StringValues "A test image"

        ctx.Request.Form <- FormCollection(fields, files)

        ctx.Items[box "ToolUp.StorageScope"] <-
            box {
                ScopeId = "user-test"
                Container = "user-test"
                Persist = true
            }

        ctx.Items[box "ToolUp.UserId"] <- box "tester"

        use body = new MemoryStream()
        ctx.Response.Body <- body

        let next: HttpFunc = fun c -> Task.FromResult(Some c)

        do!
            AssetUploadHandler.uploadHandler options next ctx
            |> Async.AwaitTask
            |> Async.Ignore

        return ctx.Response.StatusCode, Text.Encoding.UTF8.GetString(body.ToArray())
    }

// ── Contract binding ────────────────────────────────────────────

let private sniffingFixture () : IUploadValidatorContract.UploadValidatorFixture = {
    Validator = SniffingUploadValidator() :> IUploadValidator
    Png = pngBytes
    Jpeg = jpegBytes
}

let contractTests =
    IUploadValidatorContract.tests "SniffingUploadValidator" sniffingFixture

// ── Pack ────────────────────────────────────────────────────────

let tests =
    testList "Phase 186 — IAssetStore upload-validation seam" [

        testList "MagicBytes.sniff" [
            test "recognises each type on the SDK's default accept-list" {
                Expect.equal (MagicBytes.sniff pngBytes) (Some "image/png") "PNG signature"
                Expect.equal (MagicBytes.sniff jpegBytes) (Some "image/jpeg") "JPEG SOI"
                Expect.equal (MagicBytes.sniff gifBytes) (Some "image/gif") "GIF89a"
                Expect.equal (MagicBytes.sniff webpBytes) (Some "image/webp") "RIFF….WEBP"
                Expect.equal (MagicBytes.sniff avifBytes) (Some "image/avif") "ftyp avif brand"
            }

            test "recognises the shapes a spoofed image usually turns out to be" {
                Expect.equal (MagicBytes.sniff zipBytes) (Some "application/zip") "PK\\x03\\x04"
                Expect.equal (MagicBytes.sniff elfBytes) (Some "application/x-elf") "\\x7fELF"

                Expect.equal (MagicBytes.sniff exeBytes) (Some "application/vnd.microsoft.portable-executable") "MZ"
            }

            test "guesses nothing it cannot corroborate" {
                Expect.isNone (MagicBytes.sniff unknownBytes) "an unknown prefix is None, not a guess"
                Expect.isNone (MagicBytes.sniff [||]) "an empty payload sniffs to None"
                Expect.isNone (MagicBytes.sniff null) "a null payload does not raise"
            }

            test "reads only the prefix it needs — a truncated header is not a match" {
                // Two bytes of the eight-byte PNG signature. A matcher
                // that read past the end of the array would throw here.
                Expect.isNone (MagicBytes.sniff [| 0x89uy; 0x50uy |]) "a partial PNG signature is not a PNG"
            }
        ]

        testList "MagicBytes.containsMarkup (polyglot detection)" [
            test "finds markup hiding behind a valid image header" {
                Expect.isTrue (MagicBytes.containsMarkup 1024 gifHtmlPolyglot) "<script> after the GIF header is found"

                Expect.equal
                    (MagicBytes.sniff gifHtmlPolyglot)
                    (Some "image/gif")
                    "CONTROL — the header check alone passes the polyglot, which is why this check exists"
            }

            test "CONTROL — an ordinary image carries no markup" {
                Expect.isFalse (MagicBytes.containsMarkup 1024 pngBytes) "a real PNG is not flagged"
                Expect.isFalse (MagicBytes.containsMarkup 1024 gifBytes) "a real GIF is not flagged"
            }

            test "the scan window is bounded — markup past it is out of scope by construction" {
                let deep =
                    Array.append (Array.create 4096 0x41uy) (Text.Encoding.ASCII.GetBytes "<script>")

                Expect.isFalse (MagicBytes.containsMarkup 1024 deep) "the check is O(1) in payload size, by design"
                Expect.isTrue (MagicBytes.containsMarkup 8192 deep) "widening the window finds it"
            }

            test "degenerate inputs do not raise" {
                Expect.isFalse (MagicBytes.containsMarkup 1024 null) "null"
                Expect.isFalse (MagicBytes.containsMarkup 1024 [||]) "empty"
                Expect.isFalse (MagicBytes.containsMarkup 0 gifHtmlPolyglot) "zero window"
            }
        ]

        testList "SniffingUploadValidator" [
            testCaseAsync "refuses a polyglot even though its header is a genuine GIF"
            <| async {
                let validator = SniffingUploadValidator() :> IUploadValidator
                let! verdict = validator.Validate(gifHtmlPolyglot, "image/gif")

                match verdict with
                | Error(MimeMismatch("image/gif", sniffed)) ->
                    Expect.equal sniffed MagicBytes.markup "the rejection says the payload is markup"
                | other -> failtestf "expected a markup MimeMismatch, got %A" other
            }

            testCaseAsync "CONTROL — a genuine GIF declared image/gif is admitted"
            <| async {
                let validator = SniffingUploadValidator() :> IUploadValidator
                let! verdict = validator.Validate(gifBytes, "image/gif")
                Expect.equal verdict (Ok()) "the polyglot check does not refuse ordinary images"
            }

            testCaseAsync "the polyglot check is switchable without disabling the sniff"
            <| async {
                let validator =
                    SniffingUploadValidator {
                        MimeSniffOptions.defaults with
                            RejectMarkupPolyglots = false
                    }
                    :> IUploadValidator

                let! admitted = validator.Validate(gifHtmlPolyglot, "image/gif")
                Expect.equal admitted (Ok()) "polyglot admitted with the check off"

                let! stillRefused = validator.Validate(pngBytes, "image/gif")
                Expect.notEqual stillRefused (Ok()) "the declared-vs-sniffed check is untouched"
            }

            testCaseAsync "fails CLOSED on bytes it cannot recognise"
            <| async {
                let validator = SniffingUploadValidator() :> IUploadValidator
                let! verdict = validator.Validate(unknownBytes, "image/png")

                match verdict with
                | Error(MimeMismatch("image/png", sniffed)) ->
                    Expect.equal sniffed MagicBytes.unrecognised "cannot-corroborate is not corroboration"
                | other -> failtestf "expected an unrecognised-bytes refusal, got %A" other
            }

            testCaseAsync "a deployment whose accept-list outruns the table can open that door explicitly"
            <| async {
                let validator =
                    SniffingUploadValidator {
                        MimeSniffOptions.defaults with
                            AllowUnrecognisedBytes = true
                    }
                    :> IUploadValidator

                let! admitted = validator.Validate(unknownBytes, "image/svg+xml")
                Expect.equal admitted (Ok()) "unrecognised bytes admitted when asked for"

                let! stillRefused = validator.Validate(zipBytes, "image/png")

                match stillRefused with
                | Error(MimeMismatch("image/png", "application/zip")) -> ()
                | other -> failtestf "a RECOGNISED mismatch must still be refused, got %A" other
            }

            testCaseAsync "the declared type is normalised before comparison"
            <| async {
                let validator = SniffingUploadValidator() :> IUploadValidator
                let! verdict = validator.Validate(pngBytes, "  IMAGE/PNG  ")
                Expect.equal verdict (Ok()) "casing and whitespace are not a mismatch"
            }

            test "the default posture is fail-closed with polyglot detection on" {
                Expect.isFalse
                    MimeSniffOptions.defaults.AllowUnrecognisedBytes
                    "unrecognised bytes are refused unless a deployment says otherwise"

                Expect.isTrue MimeSniffOptions.defaults.RejectMarkupPolyglots "polyglot detection on by default"
            }
        ]

        // ── Phase 639 — spreadsheet packages (.xlsx / .xlsm) ─────────

        testList "MagicBytes.openXmlPackage" [
            test "reads the flavour from the container's own manifest" {
                Expect.equal (MagicBytes.openXmlPackage xlsxPackage) (Some MagicBytes.spreadsheetPackage) ".xlsx"

                Expect.equal
                    (MagicBytes.openXmlPackage xlsmPackage)
                    (Some MagicBytes.macroEnabledSpreadsheetPackage)
                    ".xlsm — the macro part does not change how the grid is typed"
            }

            test "a zip that is not an OPC package stays unrecognised" {
                Expect.isNone (MagicBytes.openXmlPackage plainZip) "no manifest part, no verdict"
                Expect.isNone (MagicBytes.openXmlPackage zipBytes) "a bare zip header is not a package"
            }

            test "non-zip and malformed payloads return None rather than raising" {
                Expect.isNone (MagicBytes.openXmlPackage pngBytes) "a PNG is not a package"
                Expect.isNone (MagicBytes.openXmlPackage null) "null is not a package"
                Expect.isNone (MagicBytes.openXmlPackage [||]) "empty is not a package"

                // A truncated archive: the header matches, the central
                // directory does not exist. The zip reader raises; the
                // seam must absorb it, because a crafted upload must not
                // become an exception unwinding through the upload path.
                let truncated = Array.sub xlsxPackage 0 (xlsxPackage.Length / 2)
                Expect.isNone (MagicBytes.openXmlPackage truncated) "a truncated archive is None, not a throw"
            }

            test "the sniff table still reports these as zip — the refinement is the validator's" {
                Expect.equal (MagicBytes.sniff xlsmPackage) (Some "application/zip") "a package IS a zip"
            }
        ]

        testList "SniffingUploadValidator — spreadsheet packages" [
            testCaseAsync "OFF by default: a workbook is refused exactly as before (GP 11)"
            <| async {
                Expect.isFalse
                    MimeSniffOptions.defaults.RecogniseSpreadsheetPackages
                    "the opt-in is off unless a deployment asks for it"

                let validator = SniffingUploadValidator() :> IUploadValidator
                let! verdict = validator.Validate(xlsmPackage, xlsmMime)

                match verdict with
                | Error(MimeMismatch(declared, "application/zip")) ->
                    // The echoed `declared` is the NORMALISED form (the
                    // validator lower-cases before comparing), which is
                    // why the constant is folded here too.
                    Expect.equal declared (xlsmMime.ToLowerInvariant()) "the pre-639 refusal is unchanged"
                | other -> failtestf "expected the unchanged zip mismatch, got %A" other
            }

            testCaseAsync "opted in, a macro-enabled workbook is admitted"
            <| async {
                let validator = packageAwareValidator ()
                let! verdict = validator.Validate(xlsmPackage, xlsmMime)
                Expect.equal verdict (Ok()) ".xlsm corroborated by the container's manifest"
            }

            testCaseAsync "the registered mixed-case spelling of the macro type is honoured"
            <| async {
                // `application/vnd.ms-excel.sheet.macroEnabled.12` is
                // the registered spelling, capital E and all, and it is
                // what a browser puts on the wire. The validator
                // lower-cases the declared type before comparing, so an
                // ordinal compare against the constant never matches —
                // the one type in the table where case matters.
                Expect.notEqual
                    MagicBytes.macroEnabledSpreadsheetPackage
                    (MagicBytes.macroEnabledSpreadsheetPackage.ToLowerInvariant())
                    "the registered spelling really is mixed-case (this test is not vacuous)"

                let validator = packageAwareValidator ()

                for spelling in
                    [
                        MagicBytes.macroEnabledSpreadsheetPackage
                        MagicBytes.macroEnabledSpreadsheetPackage.ToLowerInvariant()
                        MagicBytes.macroEnabledSpreadsheetPackage.ToUpperInvariant()
                        "  " + MagicBytes.macroEnabledSpreadsheetPackage + "  "
                    ] do
                    let! verdict = validator.Validate(xlsmPackage, spelling)
                    Expect.equal verdict (Ok()) (sprintf "declared as '%s'" spelling)
            }

            testCaseAsync "opted in, a plain workbook is admitted too"
            <| async {
                let validator = packageAwareValidator ()
                let! verdict = validator.Validate(xlsxPackage, xlsxMime)
                Expect.equal verdict (Ok()) ".xlsx corroborated by the container's manifest"
            }

            testCaseAsync "the two flavours are NOT interchangeable — each must declare itself"
            <| async {
                let validator = packageAwareValidator ()
                let! verdict = validator.Validate(xlsmPackage, xlsxMime)

                match verdict with
                | Error(MimeMismatch(declared, sniffed)) ->
                    Expect.equal declared xlsxMime "the declared type is echoed"

                    Expect.equal
                        sniffed
                        MagicBytes.macroEnabledSpreadsheetPackage
                        "the refusal names what the container actually declares, not 'application/zip'"
                | other -> failtestf "expected a package-level mismatch, got %A" other
            }

            testCaseAsync "the opt-in only widens — a spoof is still refused"
            <| async {
                let validator = packageAwareValidator ()

                let! zipSpoof = validator.Validate(plainZip, xlsxMime)
                Expect.notEqual zipSpoof (Ok()) "a zip that is not a workbook is not a workbook"

                let! imageSpoof = validator.Validate(pngBytes, xlsxMime)
                Expect.notEqual imageSpoof (Ok()) "a PNG declared as a workbook is still refused"

                let! executableSpoof = validator.Validate(exeBytes, xlsxMime)
                Expect.notEqual executableSpoof (Ok()) "an executable declared as a workbook is still refused"

                // The corroborated arm is untouched: a payload the
                // header check already agreed with is never re-judged.
                let! genuinePng = validator.Validate(pngBytes, "image/png")
                Expect.equal genuinePng (Ok()) "ordinary corroboration is unaffected"

                let! polyglot = validator.Validate(gifHtmlPolyglot, "image/gif")
                Expect.notEqual polyglot (Ok()) "the polyglot check is unaffected"
            }
        ]

        testList "UploadValidator.run — fail-closed" [
            testCaseAsync "a scanner that cannot answer refuses the upload"
            <| async {
                let! verdict = UploadValidator.run (EnabledUploadValidator(UnavailableScanner())) pngBytes "image/png"

                match verdict with
                | Error(ValidationUnavailable reason) ->
                    Expect.stringContains reason "refused the connection" "the backend's reason survives"
                | other -> failtestf "an unreachable scanner must NOT read as clean; got %A" other
            }

            testCaseAsync "a validator that RAISES refuses the upload, naming itself"
            <| async {
                // The shape that silently opens the door if the exception
                // is allowed to escape into some outer handler that logs
                // and continues.
                let! verdict = UploadValidator.run (EnabledUploadValidator(ThrowingScanner())) pngBytes "image/png"

                match verdict with
                | Error(ValidationUnavailable reason) ->
                    Expect.stringContains reason "throwing-scanner" "the reason names the validator"
                    Expect.stringContains reason "socket closed" "and carries the underlying failure"
                | other -> failtestf "a raising validator must refuse, got %A" other
            }

            testCaseAsync "a positive detection is surfaced as MalwareDetected, not flattened"
            <| async {
                let! verdict = UploadValidator.run (EnabledUploadValidator(DetectingScanner())) pngBytes "image/png"

                Expect.equal
                    verdict
                    (Error(MalwareDetected "Eicar-Test-Signature"))
                    "the backend's verdict string reaches the operator uninterpreted"
            }

            testCaseAsync "CONTROL — a validator that admits the payload does not block it"
            <| async {
                let! verdict = UploadValidator.run (EnabledUploadValidator(PermissiveScanner())) pngBytes "image/png"
                Expect.equal verdict (Ok()) "a clean verdict passes through"
            }

            testCaseAsync "NoUploadValidator admits everything the pre-186 path admitted (GP 11)"
            <| async {
                for payload in [ pngBytes; zipBytes; exeBytes; gifHtmlPolyglot; unknownBytes ] do
                    let! verdict = UploadValidator.run NoUploadValidator payload "image/png"
                    Expect.equal verdict (Ok()) "the default seam inspects nothing and refuses nothing"
            }

            test "AssetStoreOptions.defaults leaves the seam off" {
                Expect.equal
                    AssetStoreOptions.defaults.UploadValidation
                    NoUploadValidator
                    "an existing deployment that configures nothing is unchanged"
            }

            test "opting in is one field" {
                let opted =
                    AssetStoreOptions.defaults
                    |> AssetStoreOptions.withUploadValidator (SniffingUploadValidator())

                match opted.UploadValidation with
                | EnabledUploadValidator v ->
                    Expect.equal v.Name "sniffing" "the composed validator is the one asked for"
                | NoUploadValidator -> failtest "withUploadValidator did not opt in"

                Expect.equal opted.MaxBytes AssetStoreOptions.defaults.MaxBytes "no other lever moved"
                Expect.equal opted.AcceptedMimeTypes AssetStoreOptions.defaults.AcceptedMimeTypes "no other lever moved"
            }
        ]

        testList "AssetUploadHandler.readCapped — enforced DURING the read" [
            testCaseAsync "abandons an over-cap payload at the ceiling instead of buffering it"
            <| async {
                let cap = 1L * 1024L * 1024L
                let payload = Array.zeroCreate<byte> (10 * 1024 * 1024)
                use source = new MemoryStream(payload)
                use counting = new CountingStream(source)

                let! result = AssetUploadHandler.readCapped counting cap |> Async.AwaitTask

                match result with
                | Ok _ -> failtest "an over-cap payload was accepted"
                | Error pulled -> Expect.isGreaterThan pulled cap "the overflow is detected past the ceiling"

                // The measurement, not the verdict. An implementation
                // that read everything and compared afterwards returns
                // the identical Error above and pulls 10 MiB here.
                Expect.isLessThanOrEqual
                    counting.BytesRead
                    (cap + 16384L)
                    "the read stopped within one buffer of the ceiling"

                Expect.isGreaterThan counting.BytesRead cap "it did read up to the ceiling before stopping"
            }

            testCaseAsync "CONTROL — an under-cap payload is read whole and returned intact"
            <| async {
                let cap = 1L * 1024L * 1024L
                let payload = Array.init (512 * 1024) (fun i -> byte (i % 251))
                use source = new MemoryStream(payload)
                use counting = new CountingStream(source)

                let! result = AssetUploadHandler.readCapped counting cap |> Async.AwaitTask

                match result with
                | Error pulled -> failtestf "a legitimate payload was refused after %d bytes" pulled
                | Ok bytes -> Expect.sequenceEqual bytes payload "the bytes round-trip unchanged"

                Expect.equal counting.BytesRead (int64 payload.Length) "exactly the payload was read"
            }

            testCaseAsync "a payload sitting exactly on the cap is admitted"
            <| async {
                let cap = 64L * 1024L
                let payload = Array.zeroCreate<byte> (int cap)
                use source = new MemoryStream(payload)

                let! result = AssetUploadHandler.readCapped source cap |> Async.AwaitTask

                match result with
                | Ok bytes -> Expect.equal (int64 bytes.Length) cap "the cap is inclusive"
                | Error _ -> failtest "an exactly-at-cap payload must not be refused"
            }
        ]

        testList "AssetUploadHandler — seam ordering" [
            testCaseAsync "a spoofed type is refused BEFORE IAssetStore.Upload fires"
            <| async {
                let store = CountingAssetStore()

                let options =
                    AssetStoreOptions.defaults
                    |> AssetStoreOptions.withUploadValidator (SniffingUploadValidator())

                // Declared image/png (on the accept-list, so
                // UploadRequest.create passes it); the bytes are a ZIP.
                let! status, body = postUpload options store zipBytes "image/png"

                Expect.equal status 400 "the upload is refused"
                Expect.equal store.Uploads 0 "nothing reached storage"
                Expect.stringContains body "MimeMismatch" "the client is told which check refused it"
            }

            testCaseAsync "an unavailable scanner refuses rather than admitting unchecked"
            <| async {
                let store = CountingAssetStore()

                let options =
                    AssetStoreOptions.defaults
                    |> AssetStoreOptions.withUploadValidator (UnavailableScanner())

                let! status, body = postUpload options store pngBytes "image/png"

                Expect.equal status 400 "the upload is refused"
                Expect.equal store.Uploads 0 "an outage does not become an admission"
                Expect.stringContains body "ValidationUnavailable" "the refusal is distinguishable from a detection"
            }

            testCaseAsync "CONTROL — a legitimate upload still reaches Upload with a validator composed"
            <| async {
                let store = CountingAssetStore()

                let options =
                    AssetStoreOptions.defaults
                    |> AssetStoreOptions.withUploadValidator (SniffingUploadValidator())

                let! status, _ = postUpload options store pngBytes "image/png"

                Expect.equal status 201 "the upload succeeds"
                Expect.equal store.Uploads 1 "the store was reached exactly once"
            }

            testCaseAsync "CONTROL — the default composition is unchanged: no validator, upload proceeds"
            <| async {
                let store = CountingAssetStore()
                let! status, _ = postUpload AssetStoreOptions.defaults store pngBytes "image/png"

                Expect.equal status 201 "an existing deployment behaves as before"
                Expect.equal store.Uploads 1 "the seam short-circuited"
            }

            testCaseAsync "the declared-metadata checks still run first — and still refuse first"
            <| async {
                let store = CountingAssetStore()

                let options =
                    AssetStoreOptions.defaults
                    |> AssetStoreOptions.withUploadValidator (SniffingUploadValidator())

                // application/x-zip is not on the accept-list, so
                // UploadRequest.create refuses it before the seam is
                // consulted — the cheap check stays the cheap check.
                let! status, body = postUpload options store zipBytes "application/x-zip"

                Expect.equal status 400 "refused"
                Expect.equal store.Uploads 0 "nothing stored"
                Expect.stringContains body "UnsupportedMimeType" "the accept-list refused it, not the seam"
            }

            testCaseAsync "an over-cap upload is refused with FileTooLarge and never reaches the seam"
            <| async {
                let store = CountingAssetStore()

                let options = {
                    AssetStoreOptions.defaults with
                        MaxBytes = 64L
                        UploadValidation = EnabledUploadValidator(ThrowingScanner())
                }

                // The throwing scanner is the tell: whichever size
                // check refuses — the new pre-read one or
                // `UploadRequest.create`'s — it refuses BEFORE the seam
                // runs, or the rejection here would be
                // ValidationUnavailable. What this pins is the
                // ORDERING; that the read itself abandons early is
                // measured by the `readCapped` pack above, which is
                // where that guarantee can actually be observed.
                let! status, body = postUpload options store pngBytes "image/png"

                Expect.equal status 400 "refused"
                Expect.equal store.Uploads 0 "nothing stored"
                Expect.stringContains body "FileTooLarge" "size refused it before the expensive check ran"
            }
        ]
    ]