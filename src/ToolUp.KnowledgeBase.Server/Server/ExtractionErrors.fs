module KnowledgeBase.ServerExtractionErrors

open System

// ─── Extraction error classifier ──────────────────────────────────
//
// `Extractors.extractChunks` and the narrative / notes ingestion paths
// can throw a wide variety of exceptions — corrupt PDFs from PdfPig,
// password-protected Office docs from `DocumentFormat.OpenXml`,
// generic IO / parsing errors, OCR fallbacks returning empty content.
// The user-facing `IngestionStatus.Failed reason` badge surfaces the
// `reason` string verbatim, so leaking raw stack-ish text gives the
// user nothing actionable.
//
// `classify` translates an exception into a user-facing message.
// Recognises common cases by type name (string-matching keeps it
// stable across minor PdfPig / OpenXml version bumps) and falls back
// to a generic "couldn't process" message that still names the file.

let private firstLine (msg: string) : string =
    if isNull msg then
        ""
    else
        msg.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)
        |> Array.tryHead
        |> Option.defaultValue msg

let private contains (needle: string) (haystack: string) =
    not (isNull haystack)
    && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)

/// Translate an extraction-time exception into a user-facing string for
/// `IngestionStatus.Failed`. Recognised cases get a fix-it hint; everything
/// else falls back to a generic message that names the file.
let classify (fileName: string) (ex: exn) : string =
    let typeName = ex.GetType().FullName
    let msg = if isNull ex.Message then "" else ex.Message

    let isPdf = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)

    if
        contains "PdfDocumentEncrypted" typeName
        || (isPdf && (contains "encrypted" msg || contains "password" msg))
    then
        "PDF is password-protected. Remove the password and re-upload."
    elif
        contains "PdfDocumentFormat" typeName
        || (isPdf && contains "malformed" msg)
        || (isPdf && contains "not a valid pdf" msg)
    then
        "PDF appears to be corrupt. Re-export from your source application and try again."
    elif
        contains "OpenXmlPackageException" typeName
        || contains "FileFormatException" typeName
    then
        "Office document is corrupt or password-protected. Re-export and try again."
    else
        sprintf "Couldn't process '%s'. (%s)" fileName (firstLine msg)