# ToolUp.OcrProviders.Tesseract

Tesseract-backed `IOcrProvider` for `ToolUp.KnowledgeBase` / `ToolUp.RAG` — the companion that makes **scanned PDFs and image uploads searchable**.

Without an OCR companion, a scanned PDF has no text layer to extract, so the knowledge base indexes nothing from it. That is not a failure the SDK can hide, and since Phase 500 it no longer tries to: such an upload lands with the `OcrUnavailable` ingestion status, whose reason text names this package. Composing it turns that status into a real index.

| Composition | Scanned PDF | Image upload (`.png` / `.jpg` / `.tiff` / …) |
|---|---|---|
| no OCR companion (default) | stored, `OcrUnavailable` | stored, `OcrUnavailable` |
| **this companion** | **indexed, one chunk per page, citable by page number** | **indexed, citable as page 1** |

Licensed under Apache-2.0. The Tesseract wrapper and the native libraries beneath it stay inside this package and never reach `ToolUp.Platform.*` (GP 1). A deployment that does not compose it loads no native library and pays nothing (GP 13).

## Prerequisites

Two things the package does **not** ship:

1. **Language data.** Download the `.traineddata` files for the languages you need — [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) (recommended: ~2 MB per language, LSTM-only, several times quicker) or [tessdata_best](https://github.com/tesseract-ocr/tessdata_best) (largest, most accurate). Put them in one directory; that directory is `TessDataPath`.
2. **The native engine on non-Windows.** `win-x64` and `win-x86` binaries are vendored by the upstream package and copied beside your assembly automatically. Elsewhere, install the platform's own:

   ```sh
   # Debian / Ubuntu
   apt-get install -y libtesseract5 libleptonica-dev
   # macOS
   brew install tesseract
   ```

Neither omission fails quietly: `create` probes for both and raises a `TesseractOcrException` naming the missing directory, the missing `.traineddata`, or the RID with no native library.

## Composition

The KnowledgeBase extractor resolves `IOcrProvider` from DI, so registering the provider **before** `composeWithRAG` is all that is required — the RAG composition sees a provider is already registered and skips its no-op default.

```fsharp
open ToolUp.RAG.OcrProviders.Tesseract

let ocr = TesseractOcrProvider.createForTessData "/var/lib/tessdata"

ServerConfig.defaults
|> ServerApp.withServices (fun services ->
    services
        .AddSingleton<ToolUp.Platform.IOcrProvider.IOcrProvider>(ocr)
        .AddSingleton<ToolUp.Platform.HealthChecks.IHealthCheck>(
            Health.create (TesseractOcrOptions.forTessData "/var/lib/tessdata")))
|> RAGServerApp.run
```

Tuned form:

```fsharp
let ocr =
    TesseractOcrProvider.create {
        TesseractOcrOptions.forTessData "/var/lib/tessdata" with
            Language = "eng+deu"
            MaxPages = 50
            MaxConcurrency = 2
            DocumentTimeout = TimeSpan.FromMinutes 2.0
    }
```

`TessDataPath` comes from the deployment's own configuration or `ISecretStore` — the companion never reads environment variables itself.

## Options

| Option | Default | Notes |
|---|---|---|
| `TessDataPath` | *(required)* | Directory holding `<language>.traineddata`. Probed at `create`. |
| `Language` | `eng` | A tessdata language code, or a `+`-joined set (`eng+deu`). Every named language needs its own file. |
| `MaxPages` | `200` | Pages OCR'd per document. **Truncates** — a partial index of a 900-page scan is still useful. |
| `DocumentTimeout` | 5 min | Wall-clock ceiling per document, checked *between* pages (see below). |
| `MaxConcurrency` | `3` | Concurrent OCR operations process-wide, and the engine-pool ceiling. This × the loaded model size is the memory bound. |
| `MaxDocumentBytes` | 128 MB | **Raises** above the cap rather than truncating — a half-OCR'd document reported as complete is worse than a visible refusal. |
| `ScannedTextThreshold` | `32` | A PDF whose whole text layer is shorter than this, and which carries images, reads as scanned. Absorbs a stray page-number stamp or scanner watermark. |

### Why the timeout is per document, not per page

`TesseractEngine.Process` is a synchronous native call with no cancellation token. A per-page "timeout" could therefore only *abandon* a thread that keeps running and keeps holding native memory — the work would not stop, it would merely stop being awaited, which is the worst of both. The deadline is checked between pages instead: bounded work, honestly reported, no orphaned threads.

### Coordinating with ingestion concurrency

`MaxConcurrency` and the RAG ingestion concurrency (`withIngestionConcurrency`) bound the same machine. OCR is by far the more expensive of the two, so size this one first and treat it as the real ceiling; raising ingestion concurrency above it simply queues work in front of the engine pool.

## What it does with each input

- **`application/pdf`** — `IsScanned` opens the document and asks two questions: is the extractable text layer shorter than `ScannedTextThreshold`, and does any page carry an embedded raster image? Both yes ⇒ scanned. `ExtractText` then pulls each page's embedded images (via PdfPig, re-encoded to PNG) and OCRs them, returning one `OcrPage` per page that yielded text. Pages whose image encoding cannot be re-encoded are skipped rather than failing the document.
- **`image/*`** — one page, OCR'd directly from memory by Leptonica.
- **anything else** — `IsScanned = false`, `ExtractText = []`. OCR has nothing to offer a spreadsheet.

The provider does **not** rasterise vector PDF content. A PDF whose pages are drawn rather than scanned, and which carries no text layer, has nothing for this companion to read; that is a genuinely different problem from OCR and would need a PDF renderer.

## Distributed readiness

**Production-ready, distributed-ready.** Stateless between calls in the `IOcrProvider` sense — the engine pool caches loaded, immutable model files, not per-call state — so replicas behave identically and a request may land on any of them. Each replica needs its own access to the tessdata directory (a read-only mount is ideal).

## Native dependency

| | |
|---|---|
| Upstream package | [`Tesseract` 5.2.0](https://www.nuget.org/packages/Tesseract/5.2.0) — Apache-2.0, © 2012-2020 Charles Weld |
| Vendored natives | `tesseract50` (Apache-2.0) + `leptonica-1.82.0` (BSD-2-Clause), `win-x64` and `win-x86` |
| Other RIDs | operator-installed system library, dynamically resolved |
| Linking | dynamic only, via P/Invoke |
| Integrity | the NuGet package hash + signature for the pinned version in `Directory.Packages.props` |
| Language data | **not redistributed** — operator-supplied, Apache-2.0 |

The binaries ship unmodified and stay user-replaceable. Credits are recorded in the repository's `NOTICE.md`.

This companion declares no `DllImport` of its own — the extern surface is the upstream wrapper's, so the review surface for a native-contract change is the pinned package version rather than a local `Native.fs`.

## Health

`Health.create options` registers an `IHealthCheck` (`ocr:tesseract`, `Readiness`) that watches the tessdata directory. It reports `Degraded`, never `Unhealthy`: a vanished tessdata mount degrades ingestion quality and must be visible to an operator, but taking replicas out of rotation over it would convert a document problem into an outage — and every replica reads the same mount, so the rotation would empty.

## Tests

`src/ToolUp.Platform.Tests/InProcess/OcrProviderTests.fs`. The structural arm runs everywhere with no native library and no language data. The native arm is env-gated on `TOOLUP_TESSDATA` (point it at a directory holding `eng.traineddata`) and reports `Pending` when unset, so a fresh checkout is green without provisioning OCR.

## See also

- `docs/companions/ocr-providers.md` — composing OCR, and what an uncomposed deployment sees.
- `docs/knowledge-base/extending.md` — the extraction pipeline this plugs into.
