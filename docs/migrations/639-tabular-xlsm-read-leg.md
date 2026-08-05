# Phase 639 — `ToolUp.Tabular` XLSM read leg (macros never executed)

Macro-enabled workbooks (`.xlsm`) now read through `ToolUp.Tabular` exactly as `.xlsx` does,
and `SniffingUploadValidator` can corroborate a declared spreadsheet-package type against the
container's own manifest instead of against its zip header.

**Macros are never executed, evaluated, or extracted.** The reader resolves four parts — the
workbook part, the shared-string table, the stylesheet, and the selected worksheet — and
`xl/vbaProject.bin` is not among them. See the "Macro-enabled workbooks" section of
[`src/ToolUp.Tabular/README.md`](../../src/ToolUp.Tabular/README.md) for the posture in full,
including what it does and does not entitle you to tell your own uploaders.

## What changes

| Surface | Change | Action needed |
|---|---|---|
| `TabularReader.readXlsx*` / `streamXlsx` / `Xlsx.readRows` | accept `.xlsm`; identical rows, values and errors | none — no API change, no flag |
| the fatal-read message | `"not a readable XLSX workbook"` → `"not a readable XLSX/XLSM workbook"` | only if you assert on the string |
| `MimeSniffOptions` | **new field** `RecogniseSpreadsheetPackages: bool` (default `false`) | see below |
| `MagicBytes` | new `openXmlPackage`, `spreadsheetPackage`, `macroEnabledSpreadsheetPackage` | none (additive) |

Reading `.xlsm` needs **no consumer change at all**. Only the upload-validation opt-in does.

## Consumer diff — the one breaking shape

`MimeSniffOptions` gained a field, so a consumer constructing it with full record syntax stops
compiling. The `with`-copy idiom is unaffected and is the recommended form:

```fsharp
// BEFORE — full record syntax no longer compiles
let options = {
    AllowUnrecognisedBytes = false
    RejectMarkupPolyglots = true
    MarkupScanBytes = 1024
}

// AFTER — copy from defaults (also correct before this phase)
let options = {
    MimeSniffOptions.defaults with
        MarkupScanBytes = 4096
}
```

## Opting in to spreadsheet uploads

An Office Open XML file *is* a zip, so `MagicBytes.sniff` reports `application/zip` for it and
the header cross-check refuses a perfectly honest `…spreadsheetml.sheet` declaration. Opt in and
the validator reads the container's `[Content_Types].xml` before refusing:

```fsharp
open ToolUp.AssetStore

let options =
    AssetStoreOptions.defaults
    |> AssetStoreOptions.withUploadValidator (
        SniffingUploadValidator MimeSniffOptions.withSpreadsheetPackages
    )
```

Three properties of the opt-in worth knowing:

- **It only ever widens.** The package check is reached only on the arm that was already going
  to refuse, so turning it on cannot newly reject anything (GP 11). Off is byte-for-byte the
  pre-639 behaviour.
- **The two flavours are not interchangeable.** A `.xlsm` declared as `.xlsx` is refused, and
  the rejection names what the container actually declares rather than `application/zip`.
- **It is not an accept-list.** You still add the MIME types to
  `AssetStoreOptions.AcceptedMimeTypes`; the validator corroborates a declaration, it does not
  authorise one.

The registered macro-enabled type is spelled `application/vnd.ms-excel.sheet.macroEnabled.12`
— mixed case, and the comparison is case-insensitive, so whichever casing your client sends is
accepted.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Tabular.Tests/ToolUp.Tabular.Tests.fsproj
dotnet run --project Build.fsproj -- VerifyAll
```

The `Xlsm` list in the Tabular pack asserts grid, binding, error-report and streaming parity
against the equivalent `.xlsx`, and separately asserts that the two fixtures genuinely differ as
packages — so the parity is a result, not two identical files agreeing.

## Rollback

Revert the phase commit. Nothing persists state and nothing is written, so there is no data
migration in either direction; a deployment that had set `RecogniseSpreadsheetPackages = true`
returns to refusing workbook uploads at the validator.
